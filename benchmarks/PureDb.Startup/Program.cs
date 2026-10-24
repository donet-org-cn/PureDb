using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using PureDb;
using PureDb.Benchmarks;

Environment.SetEnvironmentVariable("SQLITE_LIBRARY", Path.GetFullPath("artifacts/sqlite/3.54.1/sqlite3.dll"));
const string query = "SELECT id,index_key,value FROM records WHERE index_key>=10 AND index_key<=20 ORDER BY index_key,id LIMIT 20";
if (args[0] == "create")
{
    string root = Path.GetFullPath(args[1]); int count = int.Parse(args[2]); Directory.CreateDirectory(root);
    string pure = Path.Combine(root, "sql.pdb"), sqlite = Path.Combine(root, "reference.sqlite");
    if (File.Exists(pure) || File.Exists(sqlite)) throw new IOException("Use a fresh dataset directory.");
    var value = new byte[256]; new Random(42).NextBytes(value);
    var timer = Stopwatch.StartNew();
    using (var db = new SqlDatabase(pure, new() { Storage = new() { Durability = Durability.Buffered } }))
    {
        await db.ExecuteAsync("CREATE TABLE records(id INTEGER PRIMARY KEY,index_key INTEGER NOT NULL,value BLOB NOT NULL); CREATE INDEX ix ON records(index_key)");
        for (int start = 0; start < count; start += 5000)
        {
            await db.ExecuteTransactionAsync(Enumerable.Range(start, Math.Min(5000, count - start)).Select(i => new SqlCommand("INSERT INTO records VALUES(@id,@k,@v)", new Dictionary<string, object?> { ["id"] = (long)i, ["k"] = (long)(i % 1000), ["v"] = value })).ToArray());
        }
        db.Query(query); // Explicit one-time SQL index creation; excluded from steady reopen timings.
    }
    Console.WriteLine($"PureDb created {count}: {timer.Elapsed.TotalSeconds:F2}s"); timer.Restart();
    using (var db = new Sqlite(sqlite, Durability.Buffered, true))
    {
        db.Exec("BEGIN"); using var insert = db.Prepare("INSERT INTO records VALUES(?1,?2,?3)");
        for (int i = 0; i < count; i++) { insert.Bind(1, i); insert.Bind(2, i % 1000); insert.Bind(3, value); insert.Step(); insert.Reset(); }
        db.Exec("COMMIT; PRAGMA wal_checkpoint(TRUNCATE)");
    }
    Console.WriteLine($"SQLite created {count}: {timer.Elapsed.TotalSeconds:F2}s"); return;
}
string engine = args[0], dataset = Path.GetFullPath(args[1]), output = args[2]; int rounds = args.Length > 3 ? int.Parse(args[3]) : 31;
var samples = new List<object>();
object? warmup = null;
for (int i = -1; i < rounds; i++)
{
    GC.Collect(); GC.WaitForPendingFinalizers(); long before = GC.GetTotalAllocatedBytes(true); using var process = Process.GetCurrentProcess(); double cpu = process.TotalProcessorTime.TotalMilliseconds;
    long begin = Stopwatch.GetTimestamp(), opened, queried, closed;
    if (engine == "SQLite")
    {
        var db = new Sqlite(Path.Combine(dataset, "reference.sqlite"), Durability.Buffered); opened = Stopwatch.GetTimestamp();
        using (var q = db.Prepare(query)) { int count = 0; while (q.Step()) { q.Values(); count++; } if (count != 20) throw new Exception("row count"); }
        queried = Stopwatch.GetTimestamp(); db.Dispose(); closed = Stopwatch.GetTimestamp();
    }
    else
    {
        var db = new SqlDatabase(Path.Combine(dataset, "sql.pdb")); opened = Stopwatch.GetTimestamp();
        if (db.Query(query).Rows.Count != 20) throw new Exception("row count"); queried = Stopwatch.GetTimestamp(); db.Dispose(); closed = Stopwatch.GetTimestamp();
    }
    process.Refresh(); double Us(long start, long end) => (end - start) * 1000000d / Stopwatch.Frequency;
    var sample = new { Round = i, OpenUs = Us(begin, opened), FirstQueryUs = Us(opened, queried), CloseUs = Us(queried, closed), TotalUs = Us(begin, closed), CpuMs = process.TotalProcessorTime.TotalMilliseconds - cpu, AllocationBytes = GC.GetTotalAllocatedBytes(true) - before, PrivateBytes = process.PrivateMemorySize64, WorkingSetBytes = process.WorkingSet64 };
    if (i >= 0) samples.Add(sample); else warmup = sample;
}
File.WriteAllText(output, JsonSerializer.Serialize(new { Engine = engine, Date = DateTimeOffset.Now, Dataset = dataset, Rounds = rounds, WarmupOpens = 1, ExcludedWarmup = warmup, Runtime = Environment.Version.ToString(), SQLite = Sqlite.Version, LibrarySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Database).Assembly.Location))), Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{engine}: {rounds} measured opens -> {output}");
