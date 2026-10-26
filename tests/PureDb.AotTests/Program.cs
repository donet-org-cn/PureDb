using System.Data;
using PureDb;
using PureDb.AdoNet;
using PureDb.SqliteCompatibility;

// 同一程序以 JIT 和 Native AOT 运行，覆盖核心及可选兼容库，不加载原生 SQLite。
string root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("artifacts", "aot-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
string FileFor(string name) => Path.Combine(root, name);
int groups = 0;
void Pass(string name) { groups++; Console.WriteLine("PASS " + name); }
string sqlPath = FileFor("main.pdb");
using (var db = new SqlDatabase(sqlPath, new() { Storage = new() { Durability = Durability.Buffered } }))
{
    await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,k INTEGER CHECK(k>=0),s TEXT UNIQUE,j JSON,b BLOB);CREATE INDEX ix ON t(k);INSERT INTO t VALUES(1,2,'one','{\"x\":2}',X'0102'),(2,3,'two','{\"x\":3}',NULL)");
    db.CreateFunction<long, bool>("eligible", n => n >= 2);
    Check(db.Query("SELECT id FROM t WHERE eligible(k) ORDER BY id").Rows.Count == 2, "C# function");
    await db.ExecuteAsync("INSERT INTO t VALUES(1,4,'unused','{}',NULL) ON CONFLICT(id) DO UPDATE SET k=k+excluded.k");
    Check(Equals(db.Query("SELECT k FROM t WHERE id=1").Rows[0][0], 6L), "UPSERT");
    await db.ExecuteAsync("CREATE TABLE binary_defaults(id INTEGER PRIMARY KEY,b BLOB DEFAULT X'0102',CHECK(typeof(1000000000000000.0)='real'));INSERT INTO binary_defaults(id) VALUES(1)");
    Check(db.Query("SELECT hex(b),CAST('12.5' AS REAL),iif(1,abs(-3),0) FROM binary_defaults").Rows[0][0] is "0102", "AOT scalar functions and BLOB default");
    Pass("CRUD, CHECK, UPSERT, indexes and C# function");
    string[] expressions = [
        "json('{unquoted:1,}')", "json_quote('quoted')", "json_quote(2)", "json_quote(NULL)",
        "json_object('a',1,'b',json_array(2,'x',NULL))", "json_set('{\"a\":1}','$.b',2)",
        "json_insert('{}','$.a',3)", "json_replace('{\"a\":1}','$.a',2)",
        "json_remove('{\"a\":1}','$.a')", "json_patch('{\"a\":1}','{\"b\":2}')",
        "json_array_insert('[1,2]','$[1]',9)", "json_type('{\"a\":true}','$.a')",
        "json_extract(jsonb('{\"a\":[true,2]}'),'$.a[1]')", "jsonb_set(jsonb('{}'),'$.n',4)",
        "json_valid('{a:1}',2)", "json_valid(2)", "json(2)", "json(2.5)",
        "'{\"a\":[1,2]}'->'$.a'", "json_extract('{\"a.b\":3}','$.'||'\"a.b\"')"
    ];
    foreach (string expression in expressions) Check(db.Query("SELECT " + expression).Rows.Count == 1, expression);
    Check(db.Query("SELECT key,value FROM json_tree('{\"a.b\":[1,2]}') ORDER BY id").Rows.Count == 4, "json_tree");
    Check(db.Query("SELECT key,value FROM json_each('[1,null,3]')").Rows.Count == 3, "json_each");
    Check(Equals(db.Query("SELECT json_extract(jsonb('{\"a\":true}'),'$.a')").Rows[0][0], 1L), "JSONB boolean");
    Pass("JSON/JSONB/JSON5 constructors, edits, paths and table functions");
    await db.ExecuteAsync("CREATE TABLE parent(id INTEGER PRIMARY KEY);CREATE TABLE child(id INTEGER PRIMARY KEY,p INTEGER REFERENCES parent(id) ON DELETE CASCADE);INSERT INTO parent VALUES(1);INSERT INTO child VALUES(1,1);CREATE VIEW v AS SELECT id,k FROM t;CREATE TABLE audit(id INTEGER PRIMARY KEY,n INTEGER);CREATE TRIGGER changed AFTER UPDATE ON t BEGIN INSERT INTO audit(n) VALUES(NEW.k);END");
    Check(db.Query("WITH c AS(SELECT id,k FROM v) SELECT id,SUM(k) OVER(ORDER BY id ROWS UNBOUNDED PRECEDING) FROM c ORDER BY id").Rows.Count == 2, "CTE/window/view");
    Check(db.Query("SELECT id FROM t WHERE EXISTS(SELECT id FROM parent WHERE parent.id=t.id) UNION SELECT 9 ORDER BY id").Rows.Count == 2, "subquery/UNION");
    await db.ExecuteAsync("BEGIN;SAVEPOINT step;UPDATE t SET k=9 WHERE id=1;ROLLBACK TO step;RELEASE step;DELETE FROM parent WHERE id=1;COMMIT");
    Check(db.Query("SELECT id FROM child").Rows.Count == 0 && db.Query("SELECT id FROM audit").Rows.Count == 0, "FK/savepoint/trigger rollback");
    Pass("CTE, windows, subqueries, UNION, views, triggers, foreign keys and savepoints");
}
using (var db = new SqlDatabase(sqlPath))
{
    Check(Equals(db.Query("SELECT k FROM t WHERE id=1").Rows[0][0], 6L), "reopen");
    await db.ExecuteAsync("INSERT INTO binary_defaults(id) VALUES(2)");
    Check(db.Query("SELECT hex(b) FROM binary_defaults WHERE id=2").Rows[0][0] is "0102", "reopened REAL expression and BLOB default");
    bool rejected = false;
    try { await db.ExecuteAsync("INSERT INTO t(id,k,s) VALUES(3,-1,'three')"); } catch (SqlException) { rejected = true; }
    Check(rejected, "persisted CHECK");
    Pass("source-generated catalog reopen and persisted constraints");
}
using (var connection = new PureDbConnection("Data Source=" + sqlPath))
{
    connection.Open(); using var command = connection.CreateCommand();
    command.CommandText = "SELECT id,k,s,b FROM t ORDER BY id";
    using var reader = command.ExecuteReader(); using var schema = reader.GetSchemaTable()!;
    Check(reader.GetFieldType(0) == typeof(long) && reader.GetFieldType(2) == typeof(string), "ADO field types");
    Check(schema.Columns["DataType"]!.DataType == typeof(Type) && (Type)schema.Rows[0]["DataType"] == typeof(long), "ADO schema Type column");
    Check(reader.Read() && reader.GetInt64(0) == 1 && reader.GetString(2) == "one", "ADO read");
    Pass("ADO.NET typed reader and GetSchemaTable");
}
using (var a = new Database(FileFor("shared.pdb"), new() { Access = DatabaseAccess.Shared, Durability = Durability.Buffered }))
using (var b = new Database(FileFor("shared.pdb"), new() { Access = DatabaseAccess.Shared, Durability = Durability.Buffered }))
{
    await a.PutAsync(1, 3, new byte[90000]); Check(b.Get(1)!.Value.Length == 90000, "Shared tail");
    await b.CompactAsync(); await a.PutAsync(2, 4, new byte[] { 7 });
    Check(b.Get(2)!.Value[0] == 7, "Shared generation");
    Pass("Shared large tail and compaction generation");
}
string sqlitePath = FileFor("managed.sqlite");
using (var db = new SqlDatabase(FileFor("exchange.pdb")))
{
    await db.ExecuteAsync("CREATE TABLE plain(id INTEGER PRIMARY KEY,n INTEGER,j JSON);INSERT INTO plain VALUES(1,2,'{\"a\":1}')"); db.ExportSqlite(sqlitePath);
}
using (var db = SqliteCompatibility.Open(sqlitePath, new() { LinuxExclusiveProcess = true }))
{
    Check(Equals(db.Query("SELECT n FROM plain WHERE id=1").Rows[0][0], 2L), "SQLite read");
    await db.ExecuteAsync("INSERT INTO plain VALUES(1,4,'{}') ON CONFLICT(id) DO UPDATE SET n=excluded.n");
}
await SqlDatabase.ImportSqliteAsync(sqlitePath, FileFor("imported.pdb"));
using (var db = new SqlDatabase(FileFor("imported.pdb"))) Check(Equals(db.Query("SELECT n FROM plain WHERE id=1").Rows[0][0], 4L), "SQLite roundtrip");
Pass("managed SQLite file update and import/export");
Console.WriteLine($"PASS {groups} AOT feature groups; dynamic code={System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported}; {root}");
