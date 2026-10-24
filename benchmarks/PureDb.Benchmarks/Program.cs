using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using PureDb;
using PureDb.Benchmarks;

int rows = args.Length > 0 ? int.Parse(args[0]) : 100_000;
int repeats = args.Length > 1 ? int.Parse(args[1]) : 3;
int workers = args.Length > 2 ? int.Parse(args[2]) : 8;
if (rows < 1000 || repeats < 1 || workers < 1) throw new ArgumentException("rows >=1000, repeats >=1, workers >=1");
ThreadPool.SetMinThreads(Math.Max(32, workers * 2), 32);
string root = Path.GetFullPath(Path.Combine("artifacts", "data", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
Directory.CreateDirectory(root);
var samples = new List<Sample>();
byte[] payload = new byte[256]; new Random(42).NextBytes(payload);
const string Insert = "INSERT INTO records(id,index_key,value) VALUES(?1,?2,?3)";
long Index(long id) => id % 1000;
void Verify(bool ok) { if (!ok) throw new Exception("Benchmark correctness validation failed"); }
async Task Measure(string engine, Durability durability, string name, int repeat, long count, Func<Task> action)
{
    GC.Collect(); GC.WaitForPendingFinalizers();
    long allocated = GC.GetTotalAllocatedBytes(true); var sw = Stopwatch.StartNew();
    await action(); sw.Stop(); allocated = GC.GetTotalAllocatedBytes(true) - allocated;
    var sample = new Sample(engine, durability.ToString(), name, repeat, count, sw.Elapsed.TotalMilliseconds, count / sw.Elapsed.TotalSeconds, allocated);
    samples.Add(sample);
    Console.WriteLine($"{durability,-8} {engine,-7} {name,-22} #{repeat}: {sample.OpsPerSecond,12:N0} ops/s {sample.Milliseconds,10:F2} ms");
}
void InsertOne(Sqlite.Statement stmt, long id)
{
    stmt.Bind(1, id); stmt.Bind(2, Index(id)); stmt.Bind(3, payload); Verify(!stmt.Step()); stmt.Reset();
}
void CheckSqlCount(Sqlite sql, int expected)
{
    using var stmt = sql.Prepare("SELECT count(*) FROM records"); Verify(stmt.Step() && stmt.Int64(0) == expected);
}
Console.WriteLine($".NET {Environment.Version}; SQLite {Sqlite.Version}; {RuntimeInformation.OSDescription}; CPUs {Environment.ProcessorCount}");
Console.WriteLine($"Rows={rows}; payload=256; workers={workers}; repeats={repeats}; bulk=1000; single=300; concurrent=1000; path={root}");
// Untimed warmup for both engines, including range and primary-key reads.
await using (var warm = new Database(Path.Combine(root, "warm.pdb"), new() { Durability = Durability.Buffered }))
{
    await warm.WriteAsync(Enumerable.Range(0, 2000).Select(i => Mutation.Put(i, Index(i), payload)).ToArray());
    for (int i = 0; i < 2000; i++) { warm.Get(i); warm.Query(i % 1000, i % 1000, 20); }
}
using (var warm = new Sqlite(Path.Combine(root, "warm.sqlite"), Durability.Buffered, true))
{
    using var put = warm.Prepare(Insert); warm.Exec("BEGIN IMMEDIATE"); for (int i = 0; i < 2000; i++) InsertOne(put, i); warm.Exec("COMMIT");
    using var get = warm.Prepare("SELECT id,index_key,value FROM records WHERE id=?1");
    using var range = warm.Prepare("SELECT id,index_key,value FROM records WHERE index_key>=?1 AND index_key<=?2 ORDER BY index_key,id LIMIT 20");
    for (int i = 0; i < 2000; i++) { get.Bind(1, i); while (get.Step()) get.Record(); get.Reset(); range.Bind(1, i % 1000); range.Bind(2, i % 1000); while (range.Step()) range.Record(); range.Reset(); }
}
foreach (var durability in new[] { Durability.Full, Durability.Buffered })
for (int repeat = 1; repeat <= repeats; repeat++)
{
    // Alternate engine order between repetitions to reduce ordering bias.
    foreach (bool pure in repeat % 2 == 1 ? new[] { true, false } : new[] { false, true })
    {
        string engine = pure ? "PureDb" : "SQLite";
        string prefix = Path.Combine(root, $"{durability}-{repeat}-{engine}");
        int singles = 300, concurrent = 1000;
        if (pure)
        {
            await using (var db = new Database(prefix + "-single.pdb", new() { Durability = durability }))
            {
                await Measure(engine, durability, "single_insert", repeat, singles, async () => { for (int i = 0; i < singles; i++) await db.PutAsync(i, Index(i), payload); });
                Verify(db.Count == singles);
            }
            await using (var db = new Database(prefix + "-concurrent.pdb", new() { Durability = durability }))
            {
                await Measure(engine, durability, "concurrent_insert", repeat, concurrent, () => Task.WhenAll(Enumerable.Range(0, workers).Select(w => Task.Run(async () => { for (int i = w; i < concurrent; i += workers) await db.PutAsync(i, Index(i), payload); }))));
                Verify(db.Count == concurrent);
            }
            await using (var db = new Database(prefix + "-parallel-batch.pdb", new() { Durability = durability }))
            {
                await Measure(engine, durability, "concurrent_batch_1000", repeat, rows, () => Task.WhenAll(Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
                {
                    for (int start = w * 1000; start < rows; start += workers * 1000)
                        await db.WriteAsync(Enumerable.Range(start, Math.Min(1000, rows - start)).Select(i => Mutation.Put(i, Index(i), payload)).ToArray());
                })))); Verify(db.Count == rows);
            }
            string path = prefix + "-bulk.pdb";
            await using (var db = new Database(path, new() { Durability = durability }))
            {
                await Measure(engine, durability, "batch_insert_1000", repeat, rows, async () =>
                {
                    for (int start = 0; start < rows; start += 1000)
                        await db.WriteAsync(Enumerable.Range(start, Math.Min(1000, rows - start)).Select(i => Mutation.Put(i, Index(i), payload)).ToArray());
                });
                Verify(db.Count == rows);
                await Measure(engine, durability, "random_get", repeat, 50000, () =>
                {
                    var rng = new Random(7); long sum = 0;
                    for (int i = 0; i < 50000; i++) { var r = db.Get(rng.Next(rows))!; sum += r.Value.Length; }
                    Verify(sum == 50000L * payload.Length); return Task.CompletedTask;
                });
                await Measure(engine, durability, "parallel_random_get", repeat, 50000, () => Task.WhenAll(Enumerable.Range(0, workers).Select(w => Task.Run(() =>
                {
                    var rng = new Random(7 + w);
                    for (int i = w; i < 50000; i += workers) Verify(db.Get(rng.Next(rows))!.Value.Length == payload.Length);
                }))));
                await Measure(engine, durability, "range_20_rows", repeat, 2000, () =>
                {
                    for (int i = 0; i < 2000; i++) { var result = db.Query(i % 990, i % 990 + 10, 20); Verify(result.Count > 0); }
                    return Task.CompletedTask;
                });
                for (int i = 0; i < rows; i++) { var r = db.Get(i)!; Verify(r.IndexKey == Index(i) && r.Value.SequenceEqual(payload)); }
            }
            await Measure(engine, durability, "reopen", repeat, 1, () => { using var db = new Database(path); Verify(db.Count == rows); return Task.CompletedTask; });
        }
        else
        {
            using (var db = new Sqlite(prefix + "-single.sqlite", durability, true))
            {
                using var stmt = db.Prepare(Insert);
                await Measure(engine, durability, "single_insert", repeat, singles, () => { for (int i = 0; i < singles; i++) InsertOne(stmt, i); return Task.CompletedTask; }); CheckSqlCount(db, singles);
            }
            async Task ConcurrentSql(string suffix, bool batch)
            {
                string path = prefix + suffix;
                using var anchor = new Sqlite(path, durability, true);
                var connections = Enumerable.Range(0, workers).Select(_ => new Sqlite(path, durability)).ToArray();
                var statements = connections.Select(c => c.Prepare(Insert)).ToArray();
                try
                {
                    int total = batch ? rows : concurrent;
                    await Measure(engine, durability, batch ? "concurrent_batch_1000" : "concurrent_insert", repeat, total, () => Task.WhenAll(Enumerable.Range(0, workers).Select(w => Task.Run(() =>
                    {
                        if (batch)
                        {
                            for (int start = w * 1000; start < rows; start += workers * 1000)
                            {
                                connections[w].Exec("BEGIN IMMEDIATE");
                                for (int i = start; i < Math.Min(start + 1000, rows); i++) InsertOne(statements[w], i);
                                connections[w].Exec("COMMIT");
                            }
                        }
                        else for (int i = w; i < concurrent; i += workers) InsertOne(statements[w], i);
                    })))); CheckSqlCount(anchor, total);
                }
                finally { foreach (var s in statements) s.Dispose(); foreach (var c in connections) c.Dispose(); }
            }
            await ConcurrentSql("-concurrent.sqlite", false); await ConcurrentSql("-parallel-batch.sqlite", true);
            string path = prefix + "-bulk.sqlite";
            using (var db = new Sqlite(path, durability, true))
            {
                using var stmt = db.Prepare(Insert);
                await Measure(engine, durability, "batch_insert_1000", repeat, rows, () =>
                {
                    for (int start = 0; start < rows; start += 1000)
                    {
                        db.Exec("BEGIN IMMEDIATE"); for (int i = start; i < Math.Min(start + 1000, rows); i++) InsertOne(stmt, i); db.Exec("COMMIT");
                    }
                    return Task.CompletedTask;
                }); CheckSqlCount(db, rows);
                using var get = db.Prepare("SELECT id,index_key,value FROM records WHERE id=?1");
                await Measure(engine, durability, "random_get", repeat, 50000, () =>
                {
                    var rng = new Random(7); long sum = 0;
                    for (int i = 0; i < 50000; i++) { get.Bind(1, rng.Next(rows)); Verify(get.Step()); sum += get.Record().Value.Length; get.Reset(); }
                    Verify(sum == 50000L * payload.Length); return Task.CompletedTask;
                });
                var readers = Enumerable.Range(0, workers).Select(_ => new Sqlite(path, durability)).ToArray();
                var readStatements = readers.Select(r => r.Prepare("SELECT id,index_key,value FROM records WHERE id=?1")).ToArray();
                try
                {
                    await Measure(engine, durability, "parallel_random_get", repeat, 50000, () => Task.WhenAll(Enumerable.Range(0, workers).Select(w => Task.Run(() =>
                    {
                        var rng = new Random(7 + w); var s = readStatements[w];
                        for (int i = w; i < 50000; i += workers) { s.Bind(1, rng.Next(rows)); Verify(s.Step() && s.Record().Value.Length == payload.Length); s.Reset(); }
                    }))));
                }
                finally { foreach (var s in readStatements) s.Dispose(); foreach (var r in readers) r.Dispose(); }
                using var range = db.Prepare("SELECT id,index_key,value FROM records WHERE index_key>=?1 AND index_key<=?2 ORDER BY index_key,id LIMIT 20");
                await Measure(engine, durability, "range_20_rows", repeat, 2000, () =>
                {
                    for (int i = 0; i < 2000; i++) { range.Bind(1, i % 990); range.Bind(2, i % 990 + 10); int count = 0; while (range.Step()) { range.Record(); count++; } Verify(count > 0); range.Reset(); }
                    return Task.CompletedTask;
                });
                using var all = db.Prepare("SELECT id,index_key,value FROM records ORDER BY id"); int n = 0;
                while (all.Step()) { var r = all.Record(); Verify(r.Id == n++ && r.IndexKey == Index(r.Id) && r.Value.SequenceEqual(payload)); } Verify(n == rows);
            }
            await Measure(engine, durability, "reopen", repeat, 1, () => { using var db = new Sqlite(path, durability); CheckSqlCount(db, rows); return Task.CompletedTask; });
        }
    }
}
Directory.CreateDirectory("artifacts");
var report = new { DateUtc = DateTime.UtcNow, Dotnet = Environment.Version.ToString(), OS = RuntimeInformation.OSDescription, CPUs = Environment.ProcessorCount, SQLite = Sqlite.Version, Rows = rows, PayloadBytes = 256, Workers = workers, Repeats = repeats, Singles = 300, ConcurrentInserts = 1000, DataPath = root, Samples = samples };
File.WriteAllText("artifacts/benchmark.json", JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllLines("artifacts/benchmark.csv", new[] { "Engine,Durability,Scenario,Repeat,Operations,Milliseconds,OpsPerSecond,AllocatedBytes" }.Concat(samples.Select(s => FormattableString.Invariant($"{s.Engine},{s.Durability},{s.Scenario},{s.Repeat},{s.Operations},{s.Milliseconds:F4},{s.OpsPerSecond:F2},{s.AllocatedBytes}"))));
Console.WriteLine("MEDIANS (ops/s; range counts queries, reopen counts opens)");
foreach (var group in samples.GroupBy(s => (s.Durability, s.Scenario)))
{
    double Median(string engine) { var a = group.Where(s => s.Engine == engine).Select(s => s.OpsPerSecond).Order().ToArray(); return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2; }
    double p = Median("PureDb"), s = Median("SQLite");
    Console.WriteLine($"{group.Key.Durability,-8} {group.Key.Scenario,-22} PureDb {p,12:N0} SQLite {s,12:N0} ratio {p / s:F2}x");
}
Console.WriteLine("Reports: artifacts/benchmark.json and artifacts/benchmark.csv");
internal sealed record Sample(string Engine, string Durability, string Scenario, int Repeat, long Operations, double Milliseconds, double OpsPerSecond, long AllocatedBytes);
