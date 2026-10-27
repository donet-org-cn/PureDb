using PureDb;
using PureDb.Benchmarks;
using PureDb.SqliteCompatibility;
using System.Diagnostics;

if (args.Length is 3 or 4 && args[0] == "--crash-file")
{
    string crashPath = args[1], crashStage = args[2];
    using var crashDb = SqliteCompatibility.Open(crashPath, new() { LinuxExclusiveProcess = OperatingSystem.IsLinux() });
    SqliteStorage.CommitStage = stage => { if (stage == crashStage) { Console.WriteLine("READY"); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite); } };
    await crashDb.ExecuteAsync(args.Length == 3 ? "UPDATE t SET n=777 WHERE id=1" : args[3] == "grow" ? "INSERT INTO t VALUES(1000,777,zeroblob(90000))" : "DELETE FROM t WHERE id>1");
    return;
}

// 测试文件由原生 SQLite 创建，写回后再次由 SQLite 校验结构与持久化模式。
string root = Path.GetFullPath(Path.Combine("artifacts", "sqlite-parity", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SQLITE_LIBRARY") is null)
    Environment.SetEnvironmentVariable("SQLITE_LIBRARY", Path.GetFullPath("artifacts/sqlite/3.54.1/sqlite3.dll"));
int passed = 0;
var fileCases = new List<object>();
if (!args.Contains("--files-only"))
    await SqlParity.Run(root, args.Contains("--baseline"));
if (args.Contains("--sql-only")) return;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
object? Scalar(Sqlite db, string sql) { using var s = db.Prepare(sql); Check(s.Step(), sql); return s.Values()[0]; }
foreach (var (name, schema, query, update) in new[]
{
    ("defaults", "CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER DEFAULT 7,s TEXT DEFAULT '中文',CHECK(typeof(1000000000000000.0)='real'));INSERT INTO t(id) VALUES(1)", "SELECT n FROM t", "INSERT INTO t(id) VALUES(2)"),
    ("blob-default", "CREATE TABLE t(id INTEGER PRIMARY KEY,b BLOB DEFAULT X'0102');INSERT INTO t(id) VALUES(1);CREATE VIEW v AS SELECT id,X'FF' AS marker FROM t", "SELECT hex(b) FROM t", "INSERT INTO t(id) VALUES(2)"),
    ("check", "CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER CHECK(n>=0));INSERT INTO t VALUES(1,5)", "SELECT n FROM t", "UPDATE t SET n=6 WHERE id=1"),
    ("is-and-bits", "CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER CHECK(n IS NOT NULL AND (n&1)=1));INSERT INTO t VALUES(1,5)", "SELECT n IS DISTINCT FROM 0 FROM t", "UPDATE t SET n=n|2 WHERE id=1"),
    ("boolean-check", "CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER CHECK(n IS TRUE));INSERT INTO t VALUES(1,2)", "SELECT n IS TRUE FROM t", "UPDATE t SET n=3.0 WHERE id=1"),
    ("unique", "CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER,s TEXT,UNIQUE(n,s));INSERT INTO t VALUES(1,5,'a')", "SELECT n FROM t", "INSERT INTO t VALUES(2,6,'b')"),
    ("composite", "CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER,s TEXT);CREATE INDEX ix ON t(n DESC,s);INSERT INTO t VALUES(1,5,'a')", "SELECT n FROM t ORDER BY n DESC,s", "INSERT INTO t VALUES(2,6,'b')"),
    ("foreign", "CREATE TABLE p(id INTEGER PRIMARY KEY);CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER REFERENCES p(id) ON DELETE CASCADE);INSERT INTO p VALUES(5);INSERT INTO t VALUES(1,5)", "SELECT n FROM t", "DELETE FROM p WHERE id=5"),
    ("view", "CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER);INSERT INTO t VALUES(1,5);CREATE VIEW v AS SELECT id,n+1 AS n FROM t", "SELECT n FROM v", "UPDATE t SET n=6 WHERE id=1"),
    ("trigger", "CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER);CREATE TABLE audit(id INTEGER PRIMARY KEY,n INTEGER);CREATE TRIGGER tr AFTER INSERT ON t BEGIN INSERT INTO audit VALUES(NEW.id,NEW.n);END;INSERT INTO t VALUES(1,5)", "SELECT n FROM audit", "INSERT INTO t VALUES(2,6)")
})
{
    string path = Path.Combine(root, name + ".sqlite"), imported = path + ".pdb", exported = path + ".exported.sqlite";
    using (var native = new Sqlite(path, Durability.Full, configure: false))
        native.Exec("PRAGMA journal_mode=DELETE;PRAGMA foreign_keys=ON;" + schema);
    object? before;
    using (var native = new Sqlite(path, Durability.Full, configure: false)) before = Scalar(native, query);
    using (var managed = SqliteCompatibility.Open(path, new() { LinuxExclusiveProcess = OperatingSystem.IsLinux() }))
    {
        Check(Equals(managed.Query(query).Rows[0][0], before), name + " initial query");
        await managed.ExecuteAsync(update);
    }
    using (var native = new Sqlite(path, Durability.Full, configure: false))
        Check(Equals(Scalar(native, "PRAGMA integrity_check"), "ok"), name + " native integrity");
    using (var managed = SqliteCompatibility.Open(path, new() { LinuxExclusiveProcess = OperatingSystem.IsLinux() }))
        managed.ExportSqlite(exported);
    using (var native = new Sqlite(exported, Durability.Full, configure: false))
        Check(Equals(Scalar(native, "PRAGMA integrity_check"), "ok"), name + " exported integrity");
    await SqlDatabase.ImportSqliteAsync(exported, imported);
    using (var managed = new SqlDatabase(imported))
        Check(managed.Query("SELECT COUNT(*) FROM t").Rows.Count == 1, name + " import");
    fileCases.Add(new { Family = "schema", Name = name, Passed = true });
    passed++; Console.WriteLine("PASS native/managed/schema/import/export: " + name);
}
foreach (int pageSize in new[] { 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 })
foreach (string encoding in new[] { "UTF-8", "UTF-16le", "UTF-16be" })
{
    string path = Path.Combine(root, $"format-{pageSize}-{encoding}.sqlite");
    using (var native = new Sqlite(path, Durability.Full, configure: false))
        native.Exec($"PRAGMA page_size={pageSize};PRAGMA encoding='{encoding}';PRAGMA journal_mode=DELETE;CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER,s TEXT,b BLOB);WITH RECURSIVE seq(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM seq WHERE x<120) INSERT INTO t SELECT x,x%13,'名'||x||'😀',zeroblob(1024+(x%7)*33) FROM seq;INSERT INTO t VALUES(201,0,char(97),X''),(202,0,char(20013),X''),(203,0,char(57344),X''),(204,0,char(65536),X''),(205,0,char(1114111),X'');CREATE INDEX ix ON t(n,s DESC)");
    object?[][] expectedOrder;
    object?[] expectedEncoding;
    using (var native = new Sqlite(path, Durability.Full, configure: false))
    {
        using var order = native.Prepare("SELECT id FROM t ORDER BY n,s DESC,id");
        var rows = new List<object?[]>(); while (order.Step()) rows.Add(order.Values()); expectedOrder = rows.ToArray();
        using var scalars = native.Prepare("SELECT hex(char(20013)),octet_length(char(20013)),hex(CAST(char(65536) AS BLOB))");
        Check(scalars.Step(), "encoding scalar result"); expectedEncoding = scalars.Values();
    }
    using (var managed = SqliteCompatibility.Open(path, new() { LinuxExclusiveProcess = OperatingSystem.IsLinux(), EnableIncrementalUpdates = true }))
    {
        Check(managed.Query("SELECT s FROM t WHERE id=1").Rows[0][0] is "名1😀", "format Unicode");
        Check(managed.Query("SELECT id FROM t ORDER BY n,s DESC,id").Rows.Select(r => r[0]).SequenceEqual(expectedOrder.Select(r => r[0])), "encoding BINARY order " + encoding);
        Check(managed.Query("SELECT hex(char(20013)),octet_length(char(20013)),hex(CAST(char(65536) AS BLOB))").Rows[0].SequenceEqual(expectedEncoding), "encoding scalar bytes " + encoding);
        using (var core = new SqlDatabase(path + ".nested.pdb"))
        {
            managed.CreateFunction<long>("nested", () =>
            {
                Check(core.Query("SELECT hex(char(20013))").Rows[0][0] is "E4B8AD", "nested core UTF-8 scope");
                return 1;
            });
            Check(managed.Query("SELECT nested(),hex(char(20013))").Rows[0][1]!.Equals(expectedEncoding[0]), "restored file encoding scope");
        }
        await managed.ExecuteAsync("UPDATE t SET n=777,s='更新😀' WHERE id=1;DELETE FROM t WHERE id=2;INSERT INTO t VALUES(500,13,'最后',X'010203')");
    }
    using (var native = new Sqlite(path, Durability.Full, configure: false))
    {
        Check(Equals(Scalar(native, "PRAGMA integrity_check"), "ok"), $"format integrity {pageSize} {encoding}");
        Check(Equals(Scalar(native, "PRAGMA page_size"), (long)pageSize), "preserved page size");
        Check(string.Equals((string)Scalar(native, "PRAGMA encoding")!, encoding, StringComparison.OrdinalIgnoreCase), "preserved encoding");
        Check(Equals(Scalar(native, "SELECT s FROM t WHERE id=1"), "更新😀"), "native Unicode update");
    }
    fileCases.Add(new { Family = "format", Name = pageSize + "/" + encoding, Passed = true });
    passed++; Console.WriteLine($"PASS native/managed/page-size/encoding: {pageSize} {encoding}");
}
foreach (int pageSize in new[] { 512, 65536 })
foreach (string stage in new[] { "after-journal-sync", "after-first-page", "after-database-sync", "after-commit" })
foreach (bool nativeRecovery in new[] { false, true })
{
    string path = Path.Combine(root, $"crash-{pageSize}-{stage}-{nativeRecovery}.sqlite");
    using (var native = new Sqlite(path, Durability.Full, configure: false))
        native.Exec($"PRAGMA page_size={pageSize};PRAGMA encoding='UTF-16le';CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER);INSERT INTO t VALUES(1,5)");
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (string arg in new[] { typeof(Program).Assembly.Location, "--crash-file", path, stage }) start.ArgumentList.Add(arg);
    using var child = Process.Start(start)!;
    string? signal = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
    Check(signal == "READY", "fault worker failed: " + signal + " " + (child.HasExited ? await child.StandardError.ReadToEndAsync() : "running"));
    child.Kill(entireProcessTree: true); await child.WaitForExitAsync();
    long expected = stage == "after-commit" ? 777L : 5L;
    if (nativeRecovery)
    {
        using var native = new Sqlite(path, Durability.Full, configure: false);
        Check(Equals(Scalar(native, "SELECT n FROM t"), expected), "native journal recovery");
        Check(Equals(Scalar(native, "PRAGMA integrity_check"), "ok"), "native recovered integrity");
    }
    else
    {
        using var managed = SqliteCompatibility.Open(path, new() { LinuxExclusiveProcess = OperatingSystem.IsLinux() });
        Check(Equals(managed.Query("SELECT n FROM t").Rows[0][0], expected), "managed journal recovery");
    }
    fileCases.Add(new { Family = "recovery", Name = pageSize + "/" + stage + "/native=" + nativeRecovery, Passed = true });
    passed++; Console.WriteLine($"PASS cross-engine crash recovery: {pageSize} {stage} native={nativeRecovery}");
}
foreach (string workload in new[] { "grow", "shrink" })
foreach (string stage in new[] { "after-journal-sync", "after-first-page", "after-database-sync", "after-commit" })
foreach (bool nativeRecovery in new[] { false, true })
{
    string path = Path.Combine(root, $"resize-{workload}-{stage}-{nativeRecovery}.sqlite");
    using (var native = new Sqlite(path, Durability.Full, configure: false))
        native.Exec("CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER,b BLOB);WITH RECURSIVE s(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM s WHERE x<31) INSERT INTO t SELECT x,5,zeroblob(1024) FROM s");
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (string arg in new[] { typeof(Program).Assembly.Location, "--crash-file", path, stage, workload }) start.ArgumentList.Add(arg);
    using var child = Process.Start(start)!;
    Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) == "READY", "resize worker did not reach " + stage);
    child.Kill(entireProcessTree: true); await child.WaitForExitAsync();
    long expected = stage != "after-commit" ? 31L : workload == "grow" ? 32L : 1L;
    if (nativeRecovery)
    {
        using var native = new Sqlite(path, Durability.Full, configure: false);
        Check(Equals(Scalar(native, "SELECT COUNT(*) FROM t"), expected), "native resized journal recovery");
        Check(Equals(Scalar(native, "PRAGMA integrity_check"), "ok"), "native resized integrity");
    }
    else
    {
        using var managed = SqliteCompatibility.Open(path, new() { LinuxExclusiveProcess = OperatingSystem.IsLinux() });
        Check(Equals(managed.Query("SELECT COUNT(*) FROM t").Rows[0][0], expected), "managed resized journal recovery");
    }
    fileCases.Add(new { Family = "resize-recovery", Name = workload + "/" + stage + "/native=" + nativeRecovery, Passed = true });
    passed++; Console.WriteLine($"PASS sparse journal growth/truncation recovery: {workload} {stage} native={nativeRecovery}");
}
File.WriteAllText(Path.Combine(root, "file-parity.json"), System.Text.Json.JsonSerializer.Serialize(new { Scope = "INTEGER PRIMARY KEY typed tables; rollback journal mode; no auto_vacuum/reserved bytes; tested schema and file profiles, not all SQLite features", Total = fileCases.Count, Passed = passed, Rate = (double)passed / fileCases.Count, Cases = fileCases }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS {passed} SQLite interoperability cases: {root}");
