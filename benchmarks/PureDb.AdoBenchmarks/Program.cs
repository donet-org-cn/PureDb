using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PureDb;
using PureDb.AdoNet;
using PureDb.Benchmarks;

string engine = args[0], dataset = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]); int repeat = int.Parse(args[3]);
if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SQLITE_LIBRARY") is null)
    Environment.SetEnvironmentVariable("SQLITE_LIBRARY", Path.GetFullPath("artifacts/sqlite/3.54.1/sqlite3.dll"));
bool ado = engine == "Ado", core = engine == "Core";
var samples = new List<object>();
void Measure(string scenario, Func<int, long> run, long fixedOperations = 0)
{
    if (fixedOperations == 0) for (int i = 0; i < 64; i++) run(i);
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    long allocation = GC.GetTotalAllocatedBytes(true); using var process = Process.GetCurrentProcess(); double start = process.TotalProcessorTime.TotalMilliseconds;
    var timer = Stopwatch.StartNew(); long operations = 0, checksum = 0;
    do { checksum += run((int)operations); operations++; } while (fixedOperations == 0 ? timer.ElapsedMilliseconds < 500 : operations < fixedOperations);
    timer.Stop(); double cpu = process.TotalProcessorTime.TotalMilliseconds - start; process.Refresh();
    samples.Add(new { Engine = engine, Scenario = scenario, Repeat = repeat, Operations = operations, Milliseconds = timer.Elapsed.TotalMilliseconds,
        CpuMilliseconds = cpu, CpuMachinePercent = cpu / timer.Elapsed.TotalMilliseconds * 100 / Environment.ProcessorCount,
        AllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocation, process.PrivateMemorySize64, process.WorkingSet64, Checksum = checksum });
    Console.WriteLine($"{engine} {scenario}: {operations / timer.Elapsed.TotalSeconds:F0}/s {timer.Elapsed.TotalMilliseconds:F3} ms");
}
string queryPath = Path.Combine(dataset, engine == "SQLiteNative" ? "reference.sqlite" : "sql.pdb");
using (var pure = core ? new SqlDatabase(queryPath) : null)
using (DbConnection? connection = ado ? new PureDbConnection(new DbConnectionStringBuilder { ["Data Source"] = queryPath, ["Access"] = "Exclusive", ["Durability"] = "Buffered" }.ConnectionString) : null)
using (var sqlite = engine == "SQLiteNative" ? new Sqlite(queryPath, Durability.Buffered) : null)
{
    connection?.Open(); sqlite?.Exec("PRAGMA cache_size=-65536");
    foreach (var q in new[] {
        (Name:"point", Sql:"SELECT id,index_key,value FROM records WHERE id=@id", Rows:1),
        (Name:"range20", Sql:"SELECT id,index_key,value FROM records WHERE index_key>=10 AND index_key<=20 ORDER BY index_key,id LIMIT 20", Rows:20),
        (Name:"group", Sql:"SELECT index_key,COUNT(*) FROM records GROUP BY index_key ORDER BY index_key", Rows:1000),
        (Name:"join", Sql:"SELECT u.id,u.name,o.id,o.amount FROM users u JOIN orders o ON u.id=o.user_id WHERE u.id=@id ORDER BY o.id LIMIT 20", Rows:10),
        (Name:"json_scan", Sql:"SELECT COUNT(*) FROM docs WHERE json_extract(body,'$.age')%2=0", Rows:1),
        (Name:"jsonb", Sql:"SELECT json_extract(jsonb(body),'$.age') FROM docs WHERE id=@id", Rows:1) })
    {
        using var statement = sqlite?.Prepare(q.Sql); using var command = connection?.CreateCommand();
        DbParameter? parameter = null; if (command is not null) { command.CommandText = q.Sql; if (q.Sql.Contains("@id")) { parameter = command.CreateParameter(); parameter.ParameterName = "id"; parameter.Value = 1L; command.Parameters.Add(parameter); } command.Prepare(); }
        var parameters = new Dictionary<string, object?>();
        Measure(q.Name, i =>
        {
            long id = i % 10000, checksum = 0; int count = 0;
            if (core) { parameters["id"] = id; var result = pure!.Query(q.Sql, parameters); foreach (var row in result.Rows) { count++; foreach (var value in row) checksum += value is long n ? n : value is byte[] b ? b.Length : value is string s ? s.Length : 0; } }
            else if (ado) { if (parameter is not null) parameter.Value = id; using var reader = command!.ExecuteReader(); while (reader.Read()) { count++; for (int c = 0; c < reader.FieldCount; c++) { var v = reader.GetValue(c); checksum += v is long n ? n : v is byte[] b ? b.Length : v is string s ? s.Length : 0; } } }
            else { if (q.Sql.Contains("@id")) statement!.Bind(1, id); while (statement!.Step()) { count++; foreach (var v in statement.Values()) checksum += v is long n ? n : v is byte[] b ? b.Length : v is string s ? s.Length : 0; } statement.Reset(); }
            if (count != q.Rows) throw new Exception("Unexpected row count"); return checksum;
        });
    }
}
string work = Path.Combine(Path.GetDirectoryName(output)!, "data", engine + "-" + repeat + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(work);
foreach (string scenario in new[] { "insert1000", "batch10000" })
{
    string path = Path.Combine(work, scenario + (engine == "SQLiteNative" ? ".sqlite" : ".pdb"));
    using var pure = core ? new SqlDatabase(path, new() { Storage = new() { Durability = Durability.Buffered } }) : null;
    using DbConnection? connection = ado ? new PureDbConnection(new DbConnectionStringBuilder { ["Data Source"] = path, ["Access"] = "Exclusive", ["Durability"] = "Buffered" }.ConnectionString) : null;
    using var sqlite = engine == "SQLiteNative" ? new Sqlite(path, Durability.Buffered) : null; connection?.Open();
    const string schema = "CREATE TABLE t(id INTEGER PRIMARY KEY,k INTEGER,v BLOB); CREATE INDEX ix ON t(k)";
    if (core) pure!.ExecuteAsync(schema).AsTask().GetAwaiter().GetResult(); else if (ado) { using var c = connection!.CreateCommand(); c.CommandText = schema; c.ExecuteNonQuery(); } else sqlite!.Exec(schema);
    byte[] blob = new byte[256]; new Random(42).NextBytes(blob); using var insert = sqlite?.Prepare("INSERT INTO t VALUES(?1,?2,?3)"); using var command = connection?.CreateCommand();
    DbParameter? idParameter = null, keyParameter = null;
    if (command is not null) { command.CommandText = "INSERT INTO t VALUES(@id,@k,@v)"; idParameter = command.CreateParameter(); idParameter.ParameterName = "id"; command.Parameters.Add(idParameter); keyParameter = command.CreateParameter(); keyParameter.ParameterName = "k"; command.Parameters.Add(keyParameter); var v = command.CreateParameter(); v.ParameterName = "v"; v.Value = blob; command.Parameters.Add(v); }
    int size = scenario == "insert1000" ? 1 : 1000, calls = scenario == "insert1000" ? 1000 : 10;
    Measure(scenario, call =>
    {
        if (core)
        {
            var commands = Enumerable.Range(call * size, size).Select(i => new SqlCommand("INSERT INTO t VALUES(@id,@k,@v)", new Dictionary<string, object?> { ["id"] = (long)i, ["k"] = (long)i % 1000, ["v"] = blob })).ToArray();
            pure!.ExecuteTransactionAsync(commands).AsTask().GetAwaiter().GetResult();
        }
        else if (ado)
        {
            using var tx = size > 1 ? connection!.BeginTransaction() : null; command!.Transaction = tx;
            for (int i = call * size; i < (call + 1) * size; i++) { idParameter!.Value = (long)i; keyParameter!.Value = (long)i % 1000; command.ExecuteNonQuery(); }
            tx?.Commit(); command.Transaction = null;
        }
        else
        {
            if (size > 1) sqlite!.Exec("BEGIN IMMEDIATE");
            for (int i = call * size; i < (call + 1) * size; i++) { insert!.Bind(1, i); insert.Bind(2, i % 1000); insert.Bind(3, blob); insert.Step(); insert.Reset(); }
            if (size > 1) sqlite!.Exec("COMMIT");
        }
        return size;
    }, calls);
    long count;
    if (core) count = (long)pure!.Query("SELECT COUNT(*) FROM t").Rows[0][0]!;
    else if (ado) { command!.CommandText = "SELECT COUNT(*) FROM t"; count = (long)command.ExecuteScalar()!; }
    else { using var s = sqlite!.Prepare("SELECT COUNT(*) FROM t"); s.Step(); count = (long)s.Values()[0]!; }
    if (count != size * calls) throw new Exception("Write verification failed");
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(new { Engine = engine, Repeat = repeat, SQLite = Sqlite.Version, Runtime = Environment.Version.ToString(), LogicalCPUs = Environment.ProcessorCount, LibrarySHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Database).Assembly.Location))), Samples = samples }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(true));
