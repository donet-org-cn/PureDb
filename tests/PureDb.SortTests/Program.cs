using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using PureDb;
using PureDb.Benchmarks;

// 排序专项同时运行正确性差分和可选测量；原生 SQLite 仅存在于测试进程。
string root = Path.GetFullPath(Path.Combine("artifacts", "sort-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
string official = Path.GetFullPath("artifacts/sqlite/3.54.1/" + (OperatingSystem.IsWindows() ? "sqlite3.dll" : "libsqlite3.so"));
if (Environment.GetEnvironmentVariable("SQLITE_LIBRARY") is null && File.Exists(official))
    Environment.SetEnvironmentVariable("SQLITE_LIBRARY", official);
bool measure = args.Contains("--measure");
int count = measure ? 100_000 : 5_000;
using var db = new SqlDatabase(Path.Combine(root, "sort.puredb"), new() { Storage = new() { Durability = Durability.Buffered } });
using var sqlite = new Sqlite(Path.Combine(root, "reference.sqlite"), Durability.Buffered);
const string create = "CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER,s TEXT,b BLOB)";
await db.ExecuteAsync(create);
sqlite.Exec(create);
for (int start = 0; start < count; start += 1000)
{
    string sql = "INSERT INTO t VALUES" + string.Join(',', Enumerable.Range(start, Math.Min(1000, count - start)).Select(i =>
        $"({i},{(i % 13 == 0 ? "NULL" : ((long)i * 7919 % count).ToString(CultureInfo.InvariantCulture))}," +
        $"{(i % 17 == 0 ? "NULL" : "'name" + i % 31 + "'")},X'{i % 256:X2}{i % 7:X2}')"));
    await db.ExecuteAsync(sql);
    sqlite.Exec(sql);
}

void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

List<object?[]> Reference(string sql)
{
    using var stmt = sqlite.Prepare(sql);
    var result = new List<object?[]>();
    while (stmt.Step()) result.Add(stmt.Values());
    return result;
}

void Compare(string sql)
{
    var actual = db.Query(sql).Rows;
    var expected = Reference(sql);
    Check(actual.Count == expected.Count, "行数不一致: " + sql);
    for (int i = 0; i < actual.Count; i++)
        Check(actual[i].Length == expected[i].Length && actual[i].Zip(expected[i]).All(p =>
            p.First is byte[] a && p.Second is byte[] b ? a.SequenceEqual(b) : Equals(p.First, p.Second)), "排序结果不一致: " + sql);
}

var random = new Random(51924);
for (int i = 0; i < 150; i++)
{
    string key = new[] { "n", "s", "b", "n%7", "id%3", "n+1" }[i % 6];
    string direction = i % 2 == 0 ? "ASC" : "DESC";
    Compare($"SELECT id,n,s,b FROM t WHERE id%5!=2 ORDER BY {key} {direction},id {(i % 3 == 0 ? "DESC" : "ASC")} LIMIT {random.Next(1, 40)} OFFSET {random.Next(70)}");
}
foreach (string sql in new[]
{
    "SELECT n AS id,t.id AS original FROM t ORDER BY id,original LIMIT 20",
    "SELECT id,n FROM t ORDER BY 2 DESC,1 LIMIT 30 OFFSET 10",
    "SELECT id,n FROM t ORDER BY n,id LIMIT 0",
    "SELECT id,n FROM t ORDER BY n,id LIMIT 4096",
    "SELECT id,n FROM t ORDER BY n,id LIMIT 4097",
    "SELECT id FROM t ORDER BY n,id LIMIT 2147483647 OFFSET 2147483647",
    "SELECT DISTINCT s FROM t ORDER BY s DESC LIMIT 5 OFFSET 2",
    "SELECT s,COUNT(*) AS total FROM t GROUP BY s ORDER BY total DESC,s LIMIT 10",
    "SELECT a.id,b.id FROM t a JOIN t b ON a.id=b.id WHERE a.id<70 ORDER BY b.n DESC,a.id LIMIT 12 OFFSET 3",
    "SELECT id,b FROM t WHERE id<0 ORDER BY b LIMIT 3"
}) Compare(sql);
Console.WriteLine($"PASS 160 SQLite {Sqlite.Version} differential sort queries");

int calls = 0;
db.CreateFunction("sequence", () => (long)++calls);
var volatileResult = db.Query("SELECT sequence() AS value FROM t ORDER BY value DESC LIMIT 5");
Check(calls == count && Equals(volatileResult.Rows[0][0], (long)count), "排序别名必须复用每行的一次易变函数求值");
db.CreateFunction<long, long>("fail_last", n => n == count - 1 ? throw new InvalidOperationException("最后一行") : n);
try
{
    db.Query("SELECT fail_last(id) FROM t ORDER BY n,id LIMIT 1");
    throw new Exception("未入选行的投影异常不能被吞掉");
}
catch (SqlException e) when (e.InnerException is InvalidOperationException) { }
var parameters = new Dictionary<string, object?> { ["take"] = 10L, ["skip"] = 7L };
Check(db.Query("SELECT id FROM t ORDER BY n,id LIMIT @take OFFSET @skip", parameters).Rows.Count == 10, "分页参数");
parameters["take"] = 0L;
Check(db.Query("SELECT id FROM t ORDER BY n,id LIMIT @take OFFSET @skip", parameters).Rows.Count == 0, "复用 SQL 后参数变动");
var tied = db.Query("SELECT id FROM t ORDER BY id%3 LIMIT 20").Rows;
Check(tied.Select(r => (long)r[0]!).SequenceEqual(Enumerable.Range(0, 20).Select(i => (long)i * 3)), "相等排序键保留输入顺序");
await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
{
    for (int i = 0; i < 20; i++)
        Check(db.Query("SELECT id FROM t ORDER BY n,id LIMIT 20").Rows.Count == 20, "并发排序独立缓冲区");
})));
Console.WriteLine("PASS sort semantics: aliases, volatile functions, exceptions, parameters, stable ties, concurrency");
if (!measure)
{
    Check(db.Query("SELECT id FROM t ORDER BY n,id LIMIT 20").Plan.Contains("TopSortColumns(20)"), "纯列小分页延迟投影");
    Check(db.Query("SELECT id FROM t ORDER BY n%7,id LIMIT 20").Plan.Contains("TopSort(20)"), "表达式小分页使用独立缓冲区");
    Check(db.Query("SELECT id FROM t ORDER BY n,id LIMIT 4096").Plan.Contains("TopSortColumns(4096)"), "上限分页命中有界排序");
    Check(!db.Query("SELECT id FROM t ORDER BY n,id LIMIT 4097").Plan.Contains("TopSort"), "大分页回退");
    Check(!db.Query("SELECT DISTINCT n FROM t ORDER BY n LIMIT 20").Plan.Contains("TopSort"), "去重语义保留");
    Check(!db.Query("SELECT n,COUNT(*) FROM t GROUP BY n ORDER BY n LIMIT 20").Plan.Contains("TopSort"), "分组语义保留");
    var blob = (byte[])db.Query("SELECT b FROM t ORDER BY n,id LIMIT 1").Rows[0][0]!;
    byte original = blob[0];
    blob[0] ^= 255;
    Check(((byte[])db.Query("SELECT b FROM t ORDER BY n,id LIMIT 1").Rows[0][0]!)[0] == original, "有界排序结果不暴露缓存 BLOB");
    Console.WriteLine("PASS sort plans: bounded selection, fallback boundaries, BLOB ownership");
}
if (!measure) return;

var scenarios = new Dictionary<string, string>
{
    ["sort_limit_20"] = "SELECT id,n FROM t ORDER BY n,id LIMIT 20",
    ["sort_offset_100"] = "SELECT id,n FROM t ORDER BY n DESC,id LIMIT 20 OFFSET 100",
    ["sort_limit_4096"] = "SELECT id,n FROM t ORDER BY n,id LIMIT 4096",
    ["indexed_range_control"] = "SELECT id,n FROM t WHERE id>=100 AND id<=200 ORDER BY id LIMIT 20"
};
var samples = new List<object>();
foreach (var (scenario, sql) in scenarios)
{
    Compare(sql);
    using var statement = sqlite.Prepare(sql);
    foreach (int round in Enumerable.Range(1, 3))
    {
        foreach (string engine in round % 2 == 1 ? new[] { "PureDb", "SQLite" } : new[] { "SQLite", "PureDb" })
        {
            int Execute()
            {
                if (engine == "PureDb") return db.Query(sql).Rows.Count;
                var rows = new List<object?[]>();
                while (statement.Step()) rows.Add(statement.Values());
                statement.Reset();
                return rows.Count;
            }
            int expectedCount = Execute();
            var warmup = Stopwatch.StartNew();
            while (warmup.ElapsedMilliseconds < 500) Check(Execute() == expectedCount, "预热结果");
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using var process = Process.GetCurrentProcess();
            var startCpu = process.TotalProcessorTime;
            long startAllocation = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            int repetitions = 0;
            do
            {
                Check(Execute() == expectedCount, "测量结果");
                repetitions++;
            } while (watch.ElapsedMilliseconds < 750);
            watch.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - startAllocation;
            double cpuMilliseconds = (process.TotalProcessorTime - startCpu).TotalMilliseconds;
            samples.Add(new { Engine = engine, Scenario = scenario, Round = round, Repetitions = repetitions,
                RowsPerQuery = count, ResultRows = expectedCount, WarmupMilliseconds = 500, MeasurementMilliseconds = 750,
                ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds, CpuMilliseconds = cpuMilliseconds, AllocatedBytes = allocated });
            Console.WriteLine($"{engine} {scenario} #{round}: {watch.Elapsed.TotalMilliseconds * 1000 / repetitions:F3} us/query, {allocated / repetitions} allocated B/query");
        }
    }
}
string report = Path.Combine(root, "sort-measurement.json");
File.WriteAllText(report, JsonSerializer.Serialize(new
{
    Runtime = Environment.Version.ToString(), OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    SQLiteVersion = Sqlite.Version,
    CoreSHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(SqlDatabase).Assembly.Location))), Samples = samples
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Sort measurements: " + report);
