using PureDb;
using PureDb.AdoNet;
using PureDb.SqliteCompatibility;

string root = Path.GetFullPath(Path.Combine("artifacts", "feature-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
int passed = 0;

void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

async Task Reject(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (SqlException) { return; }
    throw new Exception("预期 SQL 执行失败。");
}

async Task Test(string name, Func<Task> action)
{
    await action();
    Console.WriteLine("PASS " + name);
    passed++;
}

SqlDatabase Open(string name) => new(Path.Combine(root, name + ".puredb"), new()
{
    Storage = new()
    {
        Durability = Durability.Buffered
    }
});

await Test("C# 用户函数：筛选、投影、聚合、重载、替换和连接隔离", async () =>
{
    using var db = Open("functions");
    await db.ExecuteAsync("CREATE TABLE Awards(id INTEGER PRIMARY KEY,User TEXT,n INTEGER,b BLOB);" +
        "INSERT INTO Awards VALUES(1,'Alice',10,X'01'),(2,'Bob',20,X'02'),(3,NULL,NULL,NULL)");
    db.CreateFunction<string?, bool>("RELIGIBLE", user => user == "Alice");
    Check(db.Query("SELECT * FROM Awards WHERE RELIGIBLE(User)").Rows.Count == 1, "函数筛选");
    db.CreateFunction<long?, long?>("twice", n => n * 2);
    Check(Equals(db.Query("SELECT twice(n) FROM Awards WHERE id=1").Rows[0][0], 20L), "主键标量函数");
    Check(db.Query("SELECT twice(n) FROM Awards WHERE id=3").Rows[0][0] is null, "NULL 参数");
    Check(Equals(db.Query("SELECT twice(SUM(n)),SUM(twice(n)) FROM Awards").Rows[0][0], 60L), "聚合外函数");
    Check(Equals(db.Query("SELECT SUM(twice(n)) FROM Awards").Rows[0][0], 60L), "聚合内函数");
    db.CreateFunction<long, long, long>("twice", (a, b) => a + b);
    Check(Equals(db.Query("SELECT twice(3,4)").Rows[0][0], 7L), "按参数数量重载");
    db.CreateFunction<long?, long?>("TWICE", n => n * 3);
    Check(Equals(db.Query("SELECT twice(n) FROM Awards WHERE id=1").Rows[0][0], 30L), "替换缓存函数");
    Check(db.RemoveFunction("twice", 2), "移除重载");
    await Reject(() => Task.FromResult(db.Query("SELECT twice(3,4)")));
    db.CreateFunction("argcount", -1, args => args.Length);
    Check(Equals(db.Query("SELECT argcount(),argcount(1,NULL,3)").Rows[0][1], 3L), "可变参数");
    using var other = Open("isolated");
    await Reject(() => Task.FromResult(other.Query("SELECT RELIGIBLE('Alice')")));
});

await Test("C# 用户函数：易变函数不能被索引提前计算，异常不能留下部分写入", async () =>
{
    using var db = Open("function-safety");
    await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER); INSERT INTO t VALUES(1,1),(2,2),(3,3)");
    int calls = 0;
    db.CreateFunction("sequence", () => (long)++calls);
    Check(db.Query("SELECT id FROM t WHERE id=sequence()").Rows.Count == 3 && calls == 3, "逐行执行易变函数");
    db.CreateFunction<long, long>("explode", n => n == 2 ? throw new InvalidOperationException("测试异常") : n + 10);
    await Reject(async () => await db.ExecuteAsync("UPDATE t SET n=explode(n)"));
    Check(Equals(db.Query("SELECT SUM(n) FROM t").Rows[0][0], 6L), "原子回滚");
    db.CreateFunction<long, long>("inc", n => n + 1);
    Check(await db.ExecuteAsync("UPDATE t SET n=inc(n) WHERE id=1") == 1, "主键更新函数");
    Check(Equals(db.Query("SELECT n FROM t WHERE id=1").Rows[0][0], 2L), "更新值");
    Check(await db.ExecuteAsync("DELETE FROM t WHERE inc(n)>3") == 1, "删除谓词函数");
    await db.ExecuteAsync("INSERT INTO t VALUES(4,inc(4))");
    Check(Equals(db.Query("SELECT n FROM t WHERE id=4").Rows[0][0], 5L), "插入表达式函数");
    await db.ExecuteAsync("UPDATE t SET n=COALESCE(n,json_extract('invalid','$')) WHERE id=4");
    Check(Equals(db.Query("SELECT n FROM t WHERE id=4").Rows[0][0], 5L), "主键 DML 的 COALESCE 无用分支");
});

await Test("C# 用户函数：BLOB 所有权和可空参数转换", async () =>
{
    using var db = Open("function-blobs");
    await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,b BLOB); INSERT INTO t VALUES(1,X'0102')");
    db.CreateFunction<byte[], byte[]>("mutate", bytes => { bytes[0] = 99; return bytes; });
    Check(((byte[])db.Query("SELECT mutate(b) FROM t").Rows[0][0]!)[0] == 99, "函数输出");
    Check(((byte[])db.Query("SELECT b FROM t WHERE id=1").Rows[0][0]!)[0] == 1, "缓存未被修改");
    db.CreateFunction("array_mutate", 1, values => { ((byte[])values[0]!)[0] = 88; return values[0]; });
    Check(((byte[])db.Query("SELECT array_mutate(b) FROM t").Rows[0][0]!)[0] == 88, "通用函数输出");
    Check(((byte[])db.Query("SELECT b FROM t WHERE id=1").Rows[0][0]!)[0] == 1, "通用函数也不得修改缓存");
    byte[] output = [3];
    db.CreateFunction("owned", () => output);
    ((byte[])db.Query("SELECT owned()").Rows[0][0]!)[0] = 4;
    Check(output[0] == 3, "返回数组独立");
    db.CreateFunction<int?, int?>("nullable", n => n + 1);
    var values = db.Query("SELECT nullable(1),nullable(NULL)").Rows[0];
    Check(Equals(values[0], 2L) && values[1] is null, "可空整数转换");
    db.CreateFunction<int, int>("required", n => n);
    await Reject(() => Task.FromResult(db.Query("SELECT required(NULL)")));
    db.CreateFunction<string?, bool?>("nullable_bool", text => text is null ? null : text == "yes");
    var booleans = db.Query("SELECT nullable_bool(NULL),nullable_bool('yes'),nullable_bool('no')").Rows[0];
    Check(booleans[0] is null && Equals(booleans[1], 1L) && Equals(booleans[2], 0L), "可空布尔返回值");
});

await Test("ADO.NET 用户函数：打开前注册、类型元数据、替换和重新打开", () =>
{
    using var connection = new PureDbConnection("Data Source=" + Path.Combine(root, "ado-functions.puredb") + ";Access=Exclusive;Durability=Buffered");
    connection.CreateFunction<string?, bool>("RELIGIBLE", user => user == "Alice");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT RELIGIBLE('Alice') AS eligible";
    using (var reader = command.ExecuteReader())
    {
        Check(reader.GetFieldType(0) == typeof(long), "bool 元数据");
        Check(reader.Read() && reader.GetInt64(0) == 1, "ADO 函数执行");
    }
    connection.CreateFunction<string?, bool>("RELIGIBLE", _ => false);
    Check(Equals(command.ExecuteScalar(), 0L), "ADO 替换");
    connection.Close();
    connection.Open();
    Check(Equals(command.ExecuteScalar(), 0L), "重新打开保留注册");
    Check(connection.RemoveFunction("RELIGIBLE", 1), "ADO 移除");
    try
    {
        command.ExecuteScalar();
        throw new Exception("应拒绝未注册函数");
    }
    catch (SqlException) { }
    return Task.CompletedTask;
});

await Test("用户函数的惰性求值、排序别名和并发查询", async () =>
{
    using var db = Open("function-evaluation");
    await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER); INSERT INTO t VALUES(1,1),(2,2),(3,3)");
    int calls = 0;
    db.CreateFunction("sequence", () => (long)Interlocked.Increment(ref calls));
    var sorted = db.Query("SELECT sequence() AS s FROM t ORDER BY s DESC");
    Check(calls == 3 && sorted.Rows.Select(r => (long)r[0]!).SequenceEqual(new long[] { 3, 2, 1 }), "排序别名只能计算一次");
    db.CreateFunction<long>("fail", () => throw new Exception("不得执行无用分支"));
    Check(Equals(db.Query("SELECT COALESCE(1,fail())").Rows[0][0], 1L), "COALESCE 惰性分支");
    Check(Equals(db.Query("SELECT COALESCE(SUM(n),fail()) FROM t").Rows[0][0], 6L), "聚合后惰性分支");
    db.CreateFunction<long, long>("twice", n => n * 2, isDeterministic: true);
    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
    {
        for (int i = 0; i < 100; i++)
            Check(Equals(db.Query("SELECT SUM(twice(n)) FROM t").Rows[0][0], 12L), "并发函数绑定");
    })));
});

await Test("ADO.NET 空结果函数元数据与替换失效", () =>
{
    using var connection = new PureDbConnection("Data Source=" + Path.Combine(root, "function-metadata.puredb") + ";Access=Exclusive;Durability=Buffered");
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY)";
    command.ExecuteNonQuery();
    int calls = 0;
    connection.CreateFunction<int?>("meta", () => { calls++; return 1; });
    command.CommandText = "SELECT meta() FROM t";
    using (var reader = command.ExecuteReader())
        Check(reader.GetFieldType(0) == typeof(long) && !reader.Read(), "空结果整数类型");
    connection.CreateFunction<decimal?>("meta", () => { calls++; return 1.5m; });
    using (var reader = command.ExecuteReader())
        Check(reader.GetFieldType(0) == typeof(double) && !reader.Read(), "替换后的空结果实数类型");
    Check(calls == 0, "推断类型不能调用用户函数");
    return Task.CompletedTask;
});

await Test("纯托管 SQLite 文件兼容后端复用用户函数注册和 ADO.NET", async () =>
{
    string path = Path.Combine(root, "compatible-functions.sqlite");
    using (var db = SqliteCompatibility.Open(path, new()
    {
        LinuxExclusiveProcess = OperatingSystem.IsLinux()
    }))
    {
        db.CreateFunction<long, long>("inc", n => n + 1);
        await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER); INSERT INTO t VALUES(1,inc(1))");
        Check(Equals(db.Query("SELECT inc(n) FROM t WHERE id=1").Rows[0][0], 3L), "兼容引擎函数");
        await db.ExecuteAsync("UPDATE t SET n=inc(n) WHERE id=1");
    }
    using var connection = new SqliteConnection("Data Source=" + path) { LinuxExclusiveProcess = OperatingSystem.IsLinux() };
    connection.CreateFunction<long, long>("twice", n => n * 2);
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "SELECT twice(n) FROM t WHERE id=1";
    Check(Equals(command.ExecuteScalar(), 6L), "兼容 ADO 函数和重开持久化");
});

Console.WriteLine($"{passed} feature test groups passed: {root}");

// 可选的分配实测不作为 CI 计时阈值；相同数据、相同全量筛选比较两种注册入口。
if (args.Contains("--measure-functions"))
{
    const int rowCount = 20_000;
    const int warmupMilliseconds = 500;
    const int measurementMilliseconds = 750;
    using var db = Open("function-allocation");
    await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,User TEXT)");
    await db.ExecuteTransactionAsync(Enumerable.Range(0, rowCount)
        .Select(id => new SqlCommand("INSERT INTO t VALUES(@id,@user)", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["user"] = "user" + id
        })).ToArray());
    db.CreateFunction<string, bool>("typed_reject", _ => false);
    db.CreateFunction("array_reject", 1, _ => false);
    var samples = new List<object>();
    for (int round = 0; round < 3; round++)
    {
        string[] functions = round % 2 == 0 ? ["typed_reject", "array_reject"] : ["array_reject", "typed_reject"];
        foreach (string function in functions)
        {
            string sql = "SELECT id FROM t WHERE " + function + "(User)";
            var warmup = System.Diagnostics.Stopwatch.StartNew();
            while (warmup.ElapsedMilliseconds < warmupMilliseconds)
                Check(db.Query(sql).Rows.Count == 0, "测量预热结果");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var startCpu = process.TotalProcessorTime;
            long startAllocation = GC.GetAllocatedBytesForCurrentThread();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int repetitions = 0;
            do
            {
                Check(db.Query(sql).Rows.Count == 0, "测量查询结果");
                repetitions++;
            } while (watch.ElapsedMilliseconds < measurementMilliseconds);
            watch.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - startAllocation;
            double cpuMilliseconds = (process.TotalProcessorTime - startCpu).TotalMilliseconds;
            samples.Add(new
            {
                Function = function,
                Round = round + 1,
                RowsPerQuery = rowCount,
                Repetitions = repetitions,
                WarmupMilliseconds = warmupMilliseconds,
                MeasurementMilliseconds = measurementMilliseconds,
                ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                CpuMilliseconds = cpuMilliseconds,
                AllocatedBytes = allocated
            });
        }
    }
    string report = Path.Combine(root, "function-allocation.json");
    File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(new
    {
        Runtime = Environment.Version.ToString(),
        OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        CoreSHA256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(SqlDatabase).Assembly.Location))),
        Samples = samples
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("Function allocation measurements: " + report);
}
