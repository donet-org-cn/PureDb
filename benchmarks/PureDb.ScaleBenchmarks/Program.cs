using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PureDb;
using PureDb.Benchmarks;

// 每个引擎、数据规模和轮次使用独立进程与新文件；准备成本与查询成本分别记录。
if (args.Length != 4) throw new ArgumentException("PureDb|SQLite rows output.json work-directory");
bool pure = args[0] == "PureDb";
int rows = int.Parse(args[1]);
string output = Path.GetFullPath(args[2]), work = Path.GetFullPath(args[3]);
Directory.CreateDirectory(work);
string path = Path.Combine(work, "scale-" + Guid.NewGuid().ToString("N") + (pure ? ".pdb" : ".sqlite"));
string library = Path.GetFullPath("artifacts/sqlite/3.54.1/" + (OperatingSystem.IsWindows() ? "sqlite3.dll" : "libsqlite3.so"));
if (Environment.GetEnvironmentVariable("SQLITE_LIBRARY") is null && File.Exists(library)) Environment.SetEnvironmentVariable("SQLITE_LIBRARY", library);
var samples = new List<object>();
SqlDatabase? db = null;
Sqlite? sqlite = null;
long Footprint() => Directory.EnumerateFiles(work, Path.GetFileName(path) + "*").Sum(f => new FileInfo(f).Length);
async Task Measure(string name, long operations, Func<Task> action)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    long allocated = GC.GetTotalAllocatedBytes(true);
    using var process = Process.GetCurrentProcess();
    double cpu = process.TotalProcessorTime.TotalMilliseconds;
    var io = ScaleIo.Read();
    var watch = Stopwatch.StartNew();
    await action();
    watch.Stop();
    double cpuMs = process.TotalProcessorTime.TotalMilliseconds - cpu;
    io = ScaleIo.Read() - io;
    process.Refresh();
    long allocation = GC.GetTotalAllocatedBytes(true) - allocated;
    samples.Add(new
    {
        Name = name,
        Operations = operations,
        Milliseconds = watch.Elapsed.TotalMilliseconds,
        CpuMilliseconds = cpuMs,
        AllocatedBytes = allocation,
        ManagedBytes = GC.GetTotalMemory(false),
        RssBytes = process.WorkingSet64,
        FileBytes = Footprint(),
        IO = io
    });
    Console.WriteLine($"{args[0]} {rows} {name}: {watch.Elapsed.TotalMilliseconds:F3} ms, {allocation:N0} B allocated");
}
void Open()
{
    if (pure)
        db = new(path, new()
        {
            Storage = new()
            {
                Durability = Durability.Buffered,
                CheckpointIntervalBytes = 0
            }
        });
    else
    {
        sqlite = new(path, Durability.Buffered);
        sqlite.Exec("PRAGMA cache_size=-65536");
    }
}
async Task Close()
{
    if (db is not null)
    {
        await db.DisposeAsync();
        db = null;
    }
    sqlite?.Dispose();
    sqlite = null;
}
void Check(bool value) { if (!value) throw new InvalidDataException("Scale benchmark result mismatch"); }
object?[][] Query(string sql, Dictionary<string, object?>? parameters = null)
{
    if (pure)
        return db!.Query(sql, parameters).Rows.ToArray();
    using var statement = sqlite!.Prepare(sql);
    if (parameters is not null)
        statement.Bind(1, (long)parameters["lo"]!);
    var result = new List<object?[]>();
    while (statement.Step())
        result.Add(statement.Values());
    return result.ToArray();
}
Open();
if (pure)
    await db!.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER,s TEXT)");
else
    sqlite!.Exec("CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER,s TEXT)");
const int batchSize = 1000;
const string text = "pure-managed-dotnet-database-中文";
await Measure("insert_batches_1000", rows, async () =>
{
    using var statement = sqlite?.Prepare("INSERT INTO t VALUES(?1,?2,?3)");
    for (int start = 0; start < rows; start += batchSize)
    {
        int count = Math.Min(batchSize, rows - start);
        if (pure)
        {
            var commands = new SqlCommand[count];
            for (int j = 0; j < count; j++)
                commands[j] = new("INSERT INTO t VALUES(@id,@n,@s)", new Dictionary<string, object?> { ["id"] = (long)start + j, ["n"] = (long)(start + j) % 100, ["s"] = text });
            await db!.ExecuteTransactionAsync(commands);
        }
        else
        {
            sqlite!.Exec("BEGIN");
            for (int j = 0; j < count; j++)
            {
                statement!.Bind(1, start + j);
                statement.Bind(2, (start + j) % 100);
                statement.Bind(3, text);
                statement.Step();
                statement.Reset();
            }
            sqlite.Exec("COMMIT");
        }
    }
});
const string rangeSql = "SELECT id,n FROM t WHERE id>=@lo ORDER BY id LIMIT 20";
async Task Ranges(string name)
{
    const int operations = 30;
    await Measure(name, operations, () =>
    {
        for (int i = 0; i < operations; i++)
        {
            long lo = (long)((i * 7919) % (rows - 20));
            var result = Query(rangeSql, new()
            {
                ["lo"] = lo
            });
            Check(result.Length == 20 && Equals(result[0][0], lo) && Equals(result[^1][0], lo + 19));
        }
        return Task.CompletedTask;
    });
}
await Ranges("range20_after_ingest");
await Measure("checkpoint", 1, async () =>
{
    if (pure)
        await db!.CheckpointAsync();
    else
        sqlite!.Exec("PRAGMA wal_checkpoint(TRUNCATE)");
});
await Ranges("range20_after_checkpoint");
using (var hotStatement = sqlite?.Prepare(rangeSql))
{
    void HotRange(int i)
    {
        long lo = (long)i * 7919 % (rows - 20);
        if (pure)
            Check(db!.Query(rangeSql, new Dictionary<string, object?> { ["lo"] = lo }).Rows.Count == 20);
        else
        {
            hotStatement!.Bind(1, lo);
            var materialized = new List<object?[]>(20);
            while (hotStatement.Step()) materialized.Add(hotStatement.Values());
            hotStatement.Reset();
            Check(materialized.Count == 20);
        }
    }
    var warmup = Stopwatch.StartNew();
    int warmOperations = 0;
    while (warmup.ElapsedMilliseconds < 500 || warmOperations < 64) HotRange(warmOperations++);
    await Measure("range20_hot", 10000, () =>
    {
        for (int i = 0; i < 10000; i++) HotRange(i);
        return Task.CompletedTask;
    });
}
await Measure("update_range_10000", 10000, async () =>
{
    if (pure)
        Check(await db!.ExecuteAsync("UPDATE t SET n=n+1 WHERE id>=1000 AND id<11000") == 10000);
    else
        sqlite!.Exec("UPDATE t SET n=n+1 WHERE id>=1000 AND id<11000");
});
await Measure("delete_range_10000", 10000, async () =>
{
    if (pure)
        Check(await db!.ExecuteAsync("DELETE FROM t WHERE id>=20000 AND id<30000") == 10000);
    else
        sqlite!.Exec("DELETE FROM t WHERE id>=20000 AND id<30000");
});
await Measure("compact", 1, async () =>
{
    if (pure)
        await db!.CompactAsync();
    else
        sqlite!.Exec("VACUUM; PRAGMA wal_checkpoint(TRUNCATE)");
});
Check(Equals(Query("SELECT COUNT(*) FROM t")[0][0], (long)rows - 10000));
await Measure("close", 1, Close);
await Measure("reopen_and_first_range", 1, () =>
{
    Open();
    Check(Query("SELECT id,n FROM t WHERE id>=1000 ORDER BY id LIMIT 20").Length == 20);
    return Task.CompletedTask;
});
await Measure("point_10000", 10000, () =>
{
    using var statement = sqlite?.Prepare("SELECT id,n FROM t WHERE id=?1");
    var parameters = new Dictionary<string, object?>();
    for (int i = 0; i < 10000; i++)
    {
        long id = i % 1000;
        if (pure)
        {
            parameters["id"] = id;
            var result = db!.Query("SELECT id,n FROM t WHERE id=@id", parameters).Rows;
            Check(result.Count == 1 && Equals(result[0][0], id));
        }
        else
        {
            statement!.Bind(1, id);
            Check(statement.Step() && Equals(statement.Values()[0], id));
            statement.Reset();
        }
    }
    return Task.CompletedTask;
});
await Close();
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(new { Engine = args[0], Rows = rows, OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Runtime = RuntimeInformation.FrameworkDescription, CpuCount = Environment.ProcessorCount, SQLite = Sqlite.Version, CoreVersion = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(SqlDatabase).Assembly)?.InformationalVersion, PageCacheBudgetBytes = 64L * 1024 * 1024, PureSqlCacheBudgetBytes = 64L * 1024 * 1024, AutomaticCheckpointBytes = 0, HostSHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Sqlite).Assembly.Location))), CoreSHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(SqlDatabase).Assembly.Location))), Simd = new { VectorBytes = System.Numerics.Vector<byte>.Count, Vector128 = System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated, Vector256 = System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated, Avx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported, ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32.IsSupported, AdvSimd = System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported }, MainFileBytes = new FileInfo(path).Length, TotalFileBytes = Footprint(), Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
// 只删除本次生成、精确以随机数据库文件名开头的文件，保留 JSON 原始结果。
foreach (string file in Directory.EnumerateFiles(work, Path.GetFileName(path) + "*")) File.Delete(file);
