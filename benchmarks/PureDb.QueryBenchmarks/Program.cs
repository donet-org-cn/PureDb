using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using PureDb;
using PureDb.Benchmarks;

int count = args.Length > 0 ? int.Parse(args[0]) : 100000;
int repeats = args.Length > 1 ? int.Parse(args[1]) : 3;
if (count < 1000 || repeats < 1) throw new ArgumentException("rows>=1000, repeats>=1");
string official = Path.GetFullPath("artifacts/sqlite/3.54.1/sqlite3.dll");
if (Environment.GetEnvironmentVariable("SQLITE_LIBRARY") is null && File.Exists(official)) Environment.SetEnvironmentVariable("SQLITE_LIBRARY", official);
Console.WriteLine($".NET {Environment.Version}; SQLite {Sqlite.Version}; CPUs={Environment.ProcessorCount}; {RuntimeInformation.OSDescription}");
ThreadPool.SetMinThreads(32, 32);
bool reuse = args.Length > 2 && !args.Contains("--create");
string root = args.Length > 2 ? Path.GetFullPath(args[2]) : Path.GetFullPath(Path.Combine("artifacts", "data", "query-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"))); Directory.CreateDirectory(root);
byte[] value = new byte[256]; new Random(42).NextBytes(value);
var samples = new List<Measurement>();
void Check(bool c, string why) { if (!c) throw new Exception(why); }
async Task Measure(string engine, string scenario, int repeat, int operations, Func<Task> action)
{
    GC.Collect(); GC.WaitForPendingFinalizers(); long bytes = GC.GetTotalAllocatedBytes(true);
    var sw = Stopwatch.StartNew(); await action(); sw.Stop();
    samples.Add(new(engine, scenario, repeat, operations, sw.Elapsed.TotalMilliseconds, operations / sw.Elapsed.TotalSeconds, GC.GetTotalAllocatedBytes(true) - bytes));
    Console.WriteLine($"{engine,-14} {scenario,-25} #{repeat}: {operations / sw.Elapsed.TotalSeconds,12:N0} /s ({sw.Elapsed.TotalMilliseconds:F2}ms)");
}
string corePath = Path.Combine(root, "core.pdb"), sqlitePath = Path.Combine(root, "reference.sqlite"), sqlPath = Path.Combine(root, "sql.pdb");
if (!reuse)
await using (var db = new Database(corePath))
{
    for (int start = 0; start < count; start += 1000) await db.WriteAsync(Enumerable.Range(start, Math.Min(1000, count - start)).Select(i => Mutation.Put(i, i % 1000, value)).ToArray());
}
Console.WriteLine("Core dataset committed.");
await using var sql = new SqlDatabase(sqlPath);
using var reference = new Sqlite(sqlitePath, Durability.Buffered, !reuse);
int users = Math.Max(100, count / 10), docs = Math.Min(10000, count);
if (!reuse)
{
await sql.ExecuteAsync("CREATE TABLE records(id INTEGER PRIMARY KEY,index_key INTEGER NOT NULL,value BLOB NOT NULL); CREATE INDEX ix_records ON records(index_key); CREATE TABLE users(id INTEGER PRIMARY KEY,name TEXT NOT NULL); CREATE TABLE orders(id INTEGER PRIMARY KEY,user_id INTEGER,amount INTEGER); CREATE INDEX ix_orders_user ON orders(user_id); CREATE TABLE docs(id INTEGER PRIMARY KEY,body JSON); CREATE INDEX ix_json_age ON docs(json_extract(body,'$.age'))");
reference.Exec("CREATE TABLE users(id INTEGER PRIMARY KEY,name TEXT NOT NULL); CREATE TABLE orders(id INTEGER PRIMARY KEY,user_id INTEGER,amount INTEGER); CREATE INDEX ix_orders_user ON orders(user_id); CREATE TABLE docs(id INTEGER PRIMARY KEY,body TEXT); CREATE INDEX ix_json_age ON docs(json_extract(body,'$.age'))");
const string Insert = "INSERT INTO records(id,index_key,value) VALUES(@id,@key,@value)";
using (var stmt = reference.Prepare("INSERT INTO records VALUES(?1,?2,?3)"))
{
    for (int start = 0; start < count; start += 1000)
    {
        var commands = Enumerable.Range(start, Math.Min(1000, count - start)).Select(i => new SqlCommand(Insert, new Dictionary<string, object?> { ["id"] = (long)i, ["key"] = (long)i % 1000, ["value"] = value })).ToArray();
        await sql.ExecuteTransactionAsync(commands);
        reference.Exec("BEGIN IMMEDIATE");
        for (int i = start; i < Math.Min(start + 1000, count); i++) { stmt.Bind(1, i); stmt.Bind(2, i % 1000); stmt.Bind(3, value); stmt.Step(); stmt.Reset(); }
        reference.Exec("COMMIT");
    }
}
for (int start = 0; start < users; start += 1000)
{
    string text = "INSERT INTO users VALUES" + string.Join(',', Enumerable.Range(start, Math.Min(1000, users - start)).Select(i => $"({i},'user{i}')")); await sql.ExecuteAsync(text); reference.Exec(text);
}
for (int start = 0; start < count; start += 1000)
{
    string text = "INSERT INTO orders VALUES" + string.Join(',', Enumerable.Range(start, Math.Min(1000, count - start)).Select(i => $"({i},{i % users},{i % 1000})")); await sql.ExecuteAsync(text); reference.Exec("BEGIN IMMEDIATE;" + text + ";COMMIT;");
}
for (int start = 0; start < docs; start += 1000)
{
    string text = "INSERT INTO docs VALUES" + string.Join(',', Enumerable.Range(start, Math.Min(1000, docs - start)).Select(i => $"({i},'{{\"age\":{i % 100},\"name\":\"user{i}\",\"tags\":[1,2,null]}}')")); await sql.ExecuteAsync(text); reference.Exec(text);
}
}
Console.WriteLine("SQL, JOIN and JSON datasets committed. Checking results against SQLite...");
List<object?[]> Reference(string query, IReadOnlyDictionary<string, object?>? parameters = null)
{
    using var stmt = reference.Prepare(query);
    if (parameters is not null) foreach (var parameter in parameters)
    {
        if (parameter.Key == "id") stmt.Bind(1, Convert.ToInt64(parameter.Value, CultureInfo.InvariantCulture));
    }
    var result = new List<object?[]>(); while (stmt.Step()) result.Add(stmt.Values()); return result;
}
bool Equal(object? a, object? b) => a is byte[] x && b is byte[] y ? x.SequenceEqual(y) : a is double d && b is double e ? Math.Abs(d - e) < 1e-9 : Equals(a, b);
void Compare(string query)
{
    var expected = Reference(query); var actual = sql.Query(query).Rows;
    Check(actual.Count == expected.Count, "Row count mismatch: " + query);
    for (int i = 0; i < actual.Count; i++) Check(actual[i].Length == expected[i].Length && actual[i].Zip(expected[i]).All(p => Equal(p.First, p.Second)), "Value mismatch: " + query + " at " + i + " actual=" + JsonSerializer.Serialize(actual[i]) + " expected=" + JsonSerializer.Serialize(expected[i]));
}
var checks = new List<string>
{
    "SELECT id,index_key,value FROM records WHERE id=42",
    "SELECT id,index_key FROM records WHERE index_key BETWEEN 3 AND 15 ORDER BY index_key,id LIMIT 20 OFFSET 3",
    "SELECT id,index_key FROM records WHERE index_key>=990 ORDER BY index_key DESC,id DESC LIMIT 20",
    "SELECT index_key AS k,COUNT(*) AS n,SUM(id) FROM records WHERE id<100 GROUP BY index_key HAVING COUNT(*)>=1 ORDER BY n DESC,k ASC",
    "SELECT u.id,u.name,o.id,o.amount FROM users u JOIN orders o ON u.id=o.user_id WHERE u.id<3 ORDER BY u.id,o.id",
    "SELECT u.id,o.id FROM users u LEFT JOIN orders o ON u.id=o.user_id AND o.amount>999 WHERE u.id<3 ORDER BY u.id,o.id",
    "SELECT u.id,o.id FROM users u FULL OUTER JOIN orders o ON u.id=o.user_id WHERE o.id<3 ORDER BY o.id",
    "SELECT COUNT(*),SUM(amount),AVG(amount),MIN(amount),MAX(amount) FROM orders WHERE id<100",
    "SELECT id,json_extract(body,'$.name'),body->>'$.age',json_type(body,'$.tags[2]') FROM docs WHERE id<10 ORDER BY id",
    "SELECT id FROM docs WHERE json_extract(body,'$.age') BETWEEN 20 AND 22 ORDER BY id LIMIT 20",
    "SELECT json(json_set(body,'$.age',99,'$.tags[#]',3)),json(json_remove(body,'$.name')),json_array_length(body,'$.tags') FROM docs WHERE id=1",
    "SELECT json_valid(NULL),json_valid('bad'),json_extract(body,'$.missing'),json_type(body,'$.missing') FROM docs WHERE id=1",
    "SELECT json_valid(NULL),json_valid(jsonb('{\"x\":1}')),json_valid(jsonb('{\"x\":1}'),8)",
    "SELECT json_extract(json_object('nested',json_array(1,'text',NULL)),'$.nested[1]') FROM docs WHERE id=1"
};
var rng = new Random(914);
for (int i = 0; i < 120; i++)
{
    int a = rng.Next(990), b = a + rng.Next(10);
    checks.Add($"SELECT id,index_key FROM records WHERE index_key BETWEEN {a} AND {b} AND id%3!=0 ORDER BY index_key{(i % 2 == 0 ? " DESC" : "")},id LIMIT {rng.Next(1, 30)} OFFSET {rng.Next(10)}");
}
foreach (var query in checks) Compare(query);
Console.WriteLine($"PASS {checks.Count} differential SQL/JSON/JOIN queries against SQLite {Sqlite.Version}");
if(args.Contains("--verify-only")) return;
const string Point = "SELECT id,index_key,value FROM records WHERE id=@id";
const string Range = "SELECT id,index_key,value FROM records WHERE index_key>=@lo AND index_key<=@hi ORDER BY index_key,id LIMIT 20";
const string Joined = "SELECT u.id,u.name,o.id,o.amount FROM users u JOIN orders o ON u.id=o.user_id WHERE u.id=@id ORDER BY o.id LIMIT 20";
const string JsonQuery = "SELECT id,json_extract(body,'$.name') FROM docs WHERE json_extract(body,'$.age')=@id ORDER BY id LIMIT 20";
// Warm both engines and materialize the same values. Full scans/index builds are outside hot-cache timing.
await using var core = new Database(corePath);
var parameters = new Dictionary<string, object?> { ["id"] = 0L, ["lo"] = 0L, ["hi"] = 10L };
using var sqlitePoint = reference.Prepare("SELECT id,index_key,value FROM records WHERE id=?1");
using var sqliteRange = reference.Prepare("SELECT id,index_key,value FROM records WHERE index_key>=?1 AND index_key<=?2 ORDER BY index_key,id LIMIT 20");
using var sqliteJoin = reference.Prepare("SELECT u.id,u.name,o.id,o.amount FROM users u JOIN orders o ON u.id=o.user_id WHERE u.id=?1 ORDER BY o.id LIMIT 20");
using var sqliteJson = reference.Prepare("SELECT id,json_extract(body,'$.name') FROM docs WHERE json_extract(body,'$.age')=?1 ORDER BY id LIMIT 20");
void Consume(Sqlite.Statement stmt) { while (stmt.Step()) stmt.Values(); stmt.Reset(); }
for (int i = 0; i < count; i++) core.Get(i);
for (int i = 0; i < 2000; i++)
{
    parameters["id"] = (long)i % count; sql.Query(Point, parameters); sqlitePoint.Bind(1, i % count); Consume(sqlitePoint);
    parameters["lo"] = (long)i % 990; parameters["hi"] = (long)i % 990 + 10; sql.Query(Range, parameters); sqliteRange.Bind(1, i % 990); sqliteRange.Bind(2, i % 990 + 10); Consume(sqliteRange);
    if (i < 100) { parameters["id"] = (long)i; sql.Query(Joined, parameters); sql.Query(JsonQuery, parameters); sqliteJoin.Bind(1, i); Consume(sqliteJoin); sqliteJson.Bind(1, i); Consume(sqliteJson); }
}
for (int repeat = 1; repeat <= repeats; repeat++)
{
    foreach (bool managed in repeat % 2 == 1 ? new[] { true, false } : new[] { false, true })
    {
        string engine = managed ? "PureDb" : "SQLite";
        await Measure(engine, "core_random_get", repeat, 100000, () =>
        {
            var random = new Random(7);
            for (int i = 0; i < 100000; i++)
                if (managed) Check(core.Get(random.Next(count))!.Value.Length == value.Length, "core value");
                else { sqlitePoint.Bind(1, random.Next(count)); Consume(sqlitePoint); }
            return Task.CompletedTask;
        });
        var readers = managed ? [] : Enumerable.Range(0, 8).Select(_ => new Sqlite(sqlitePath, Durability.Buffered)).ToArray();
        var statements = readers.Select(r => r.Prepare("SELECT id,index_key,value FROM records WHERE id=?1")).ToArray();
        try
        {
            await Measure(engine, "core_parallel_get_8", repeat, 100000, () => Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(() =>
            {
                var random = new Random(7 + w);
                for (int i = w; i < 100000; i += 8) if (managed) Check(core.Get(random.Next(count))!.Value.Length == value.Length, "parallel value"); else { statements[w].Bind(1, random.Next(count)); Consume(statements[w]); }
            }))));
        }
        finally { foreach (var s in statements) s.Dispose(); foreach (var r in readers) r.Dispose(); }
        await Measure(engine, "core_range_20", repeat, 10000, () =>
        {
            for (int i = 0; i < 10000; i++) if (managed) Check(core.Query(i % 990, i % 990 + 10, 20).Count == 20, "range count"); else { sqliteRange.Bind(1, i % 990); sqliteRange.Bind(2, i % 990 + 10); Consume(sqliteRange); }
            return Task.CompletedTask;
        });
        await Measure(engine, "sql_primary_get", repeat, 100000, () =>
        {
            var random = new Random(7);
            for (int i = 0; i < 100000; i++) { int id = random.Next(count); if (managed) { parameters["id"] = (long)id; Check(sql.Query(Point, parameters).Rows.Count == 1, "SQL get"); } else { sqlitePoint.Bind(1, id); Consume(sqlitePoint); } }
            return Task.CompletedTask;
        });
        if (managed) sql.Query(Range, parameters);
        await Measure(engine, "sql_range_20", repeat, 10000, () =>
        {
            for (int i = 0; i < 10000; i++) if (managed) { parameters["lo"] = (long)i % 990; parameters["hi"] = (long)i % 990 + 10; Check(sql.Query(Range, parameters).Rows.Count == 20, "SQL range"); } else { sqliteRange.Bind(1, i % 990); sqliteRange.Bind(2, i % 990 + 10); Consume(sqliteRange); }
            return Task.CompletedTask;
        });
        if (managed) sql.Query(Joined, parameters);
        await Measure(engine, "sql_index_join", repeat, 10000, () =>
        {
            for (int i = 0; i < 10000; i++) if (managed) { parameters["id"] = (long)i % users; Check(sql.Query(Joined, parameters).Rows.Count > 0, "join"); } else { sqliteJoin.Bind(1, i % users); Consume(sqliteJoin); }
            return Task.CompletedTask;
        });
        if (managed) sql.Query(JsonQuery, parameters);
        await Measure(engine, "sql_json_index", repeat, 10000, () =>
        {
            for (int i = 0; i < 10000; i++) if (managed) { parameters["id"] = (long)i % 100; Check(sql.Query(JsonQuery, parameters).Rows.Count > 0, "json"); } else { sqliteJson.Bind(1, i % 100); Consume(sqliteJson); }
            return Task.CompletedTask;
        });
    }
}
// Report cold object-cache behavior independently; OS caches remain warm.
await using (var noCache = new Database(corePath + ".uncached", new() { CacheBytes = 0 }))
{
    await noCache.WriteAsync(Enumerable.Range(0, 1000).Select(i => Mutation.Put(i, i, value)).ToArray());
    await Measure("PureDb-noCache", "get_cache_disabled", 1, 10000, () => { for (int i = 0; i < 10000; i++) noCache.Get(i % 1000); return Task.CompletedTask; });
}
File.WriteAllText("artifacts/query-benchmark.json", JsonSerializer.Serialize(new { DateUtc = DateTime.UtcNow, SQLite = Sqlite.Version, Runtime = Environment.Version.ToString(), Rows = count, Repeats = repeats, Workers = 8, PageCacheBytes = 64 * 1024 * 1024, SqlCacheBytes = 64 * 1024 * 1024, DifferentialQueries = checks.Count, DataPath = root, Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllLines("artifacts/query-benchmark.csv", new[] { "Engine,Scenario,Repeat,Operations,Milliseconds,OpsPerSecond,AllocatedBytes" }.Concat(samples.Select(s => FormattableString.Invariant($"{s.Engine},{s.Scenario},{s.Repeat},{s.Operations},{s.Milliseconds:F3},{s.OpsPerSecond:F2},{s.AllocatedBytes}"))));
Console.WriteLine("MEDIANS /s");
foreach (var group in samples.GroupBy(s => (s.Engine, s.Scenario))) { var a = group.Select(s => s.OpsPerSecond).Order().ToArray(); Console.WriteLine($"{group.Key.Engine,-14} {group.Key.Scenario,-25} {a[a.Length / 2],12:N0}"); }
Console.WriteLine("Plans: " + sql.Query(Joined, parameters).Plan + "; " + sql.Query(JsonQuery, parameters).Plan);
internal sealed record Measurement(string Engine, string Scenario, int Repeat, int Operations, double Milliseconds, double OpsPerSecond, long AllocatedBytes);
