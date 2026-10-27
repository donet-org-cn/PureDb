using PureDb;
using PureDb.AdoNet;
using PureDb.Benchmarks;

string root = Path.GetFullPath(Path.Combine("artifacts", "sql-compatibility-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
string library = Path.GetFullPath("artifacts/sqlite/3.54.1/" + (OperatingSystem.IsWindows() ? "sqlite3.dll" : "libsqlite3.so"));
if (Environment.GetEnvironmentVariable("SQLITE_LIBRARY")is null && File.Exists(library))
    Environment.SetEnvironmentVariable("SQLITE_LIBRARY", library);
using var db = new SqlDatabase(Path.Combine(root, "test.puredb"), new() { Storage = new() { Durability = Durability.Buffered } });
using var sqlite = new Sqlite(Path.Combine(root, "reference.sqlite"), Durability.Buffered);
int comparisons = 0;
void Check(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

async Task Both(string sql)
{
    await db.ExecuteAsync(sql);
    sqlite.Exec(sql);
}

void Compare(string sql, SqlDatabase? database = null)
{
    var actual = (database ?? db).Query(sql);
    using var statement = sqlite.Prepare(sql);
    var expected = new List<object? []>();
    while (statement.Step())
        expected.Add(statement.Values());
    Check(actual.Rows.Count == expected.Count, "行数不一致: " + sql);
    for (int i = 0; i < expected.Count; i++)
        Check(actual.Rows[i].Length == expected[i].Length && actual.Rows[i].Zip(expected[i]).All(p => p.First is byte[] a && p.Second is byte[] b ? a.SequenceEqual(b) : p.First is double x && p.Second is double y ? x.Equals(y) || Math.Abs(x - y) < 1e-10 : Equals(p.First, p.Second)), "结果不一致: " + sql + " at " + i + " actual=" + FormatRow(actual.Rows[i]) + " expected=" + FormatRow(expected[i]));
    comparisons++;
}

// 差分失败诊断也必须能在 AOT 中运行，不在消息格式化时请求反射 JSON 元数据。
string FormatRow(object?[] row) => string.Join(";", row.Select(v => v is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)));

await Both("CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER,s TEXT); INSERT INTO t VALUES(1,10,'a'),(2,10,'b'),(3,NULL,'c'),(4,30,'d')");
foreach (string sql in new[]
{
    "SELECT 1 AS n UNION SELECT 1 UNION SELECT NULL ORDER BY n",
    "SELECT 1 AS n UNION ALL SELECT 1 UNION ALL SELECT 2 ORDER BY n DESC LIMIT 2",
    "SELECT n FROM t UNION SELECT id FROM t ORDER BY n",
    "SELECT n FROM t INTERSECT SELECT 10 UNION SELECT 99 ORDER BY n",
    "SELECT id FROM t EXCEPT SELECT 2 EXCEPT SELECT 4 ORDER BY id",
    "SELECT X'0102' AS b UNION SELECT X'0102' ORDER BY b",
    "WITH a(x) AS (SELECT 1 UNION ALL SELECT 2) SELECT x FROM a ORDER BY x DESC",
    "WITH a AS (SELECT id,n FROM t WHERE id<4), b(x) AS (SELECT n FROM a) SELECT x FROM b ORDER BY x",
    "WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM seq WHERE x<8) SELECT x FROM seq ORDER BY x",
    "WITH RECURSIVE seq(x) AS (SELECT 1 UNION SELECT x FROM seq) SELECT x FROM seq",
    "SELECT q.id,q.n FROM (SELECT id,n FROM t WHERE id<3) AS q ORDER BY q.id DESC",
    "SELECT (SELECT n FROM t WHERE id=1), (SELECT n FROM t WHERE id=100)",
    "SELECT a.id,(SELECT b.s FROM t b WHERE b.id=a.id) AS label FROM t a ORDER BY a.id",
    "SELECT id FROM t WHERE id IN (SELECT id FROM t WHERE n=10) ORDER BY id",
    "SELECT id FROM t a WHERE EXISTS(SELECT 1 FROM t b WHERE b.id=a.id AND b.n=10) ORDER BY id",
    "SELECT id FROM t a WHERE NOT EXISTS(SELECT 1 FROM t b WHERE b.id=a.id AND b.n=10) ORDER BY id",
    "SELECT NULL IN (SELECT id FROM t WHERE id<0),2 IN (SELECT n FROM t),2 NOT IN (SELECT n FROM t)",
    "SELECT a.id,(SELECT b.n FROM t b WHERE b.id=a.id) FROM t a WHERE a.id=3",
    "WITH a AS (SELECT id FROM t) SELECT id FROM a UNION ALL SELECT 9 ORDER BY id",
    "SELECT id FROM t WHERE id IN (WITH x AS (SELECT 2 AS n) SELECT n FROM x) ORDER BY id"
}

)
    Compare(sql);
foreach (string sql in new[]
{
    "SELECT json_extract('{a:0x10,b:.5,c:+2,}', '$.a'),json_extract('{a:0x10,b:.5,c:+2,}', '$.b')",
    "SELECT json_valid('{a:1}'),json_valid('{a:1}',2),json_valid('{\"a\":1}',1),json_valid('{a:1}',3)",
    "SELECT json_extract('{a:/* comment */[1,2,],}', '$.a[1]')",
    "SELECT json_extract('{a:''hello'',b:''line\\\ncontinued''}', '$.a')",
    "SELECT json_extract('{a:NaN,b:Infinity}', '$.a'),json_type('{a:NaN}', '$.a')",
    "SELECT key,value,type,atom,fullkey,path FROM json_each('[1,null,true,{\"x\":2}]') ORDER BY key",
    "SELECT key,value,type,atom,fullkey,path FROM json_tree('{\"a\":[1,null],\"b\":true}') ORDER BY fullkey",
    "SELECT key,value,type,fullkey,path FROM json_each('{a:[4,5]}','$.a') ORDER BY key",
    "SELECT COUNT(*) FROM json_each(NULL)",
    "SELECT key,value,type,fullkey,path FROM json_each('7')",
    "SELECT key,value,type,fullkey,path FROM json_tree('{a:7}','$.a')",
    "SELECT COUNT(*) FROM json_tree('{a:1}','$.missing')"
}

)
    Compare(sql);
await Both("CREATE TABLE documents(id INTEGER PRIMARY KEY,body TEXT); INSERT INTO documents VALUES(1,'[1,2]'),(2,'[]'),(3,NULL),(4,'[3]')");
Compare("SELECT d.id,j.key,j.value FROM documents d,json_each(d.body) AS j ORDER BY d.id,j.key");
Compare("SELECT d.id,j.value FROM documents d LEFT JOIN json_each(d.body) AS j ON j.value>1 ORDER BY d.id,j.key");
Check(db.Query("SELECT * FROM json_each('[1]')").Columns.Count == 8, "JSON 表值函数隐藏列不参与星号投影");
foreach (string sql in new[]
{
    "SELECT id,ROW_NUMBER() OVER (ORDER BY n,id),RANK() OVER (ORDER BY n),DENSE_RANK() OVER (ORDER BY n) FROM t ORDER BY id",
    "SELECT id,ROW_NUMBER() OVER (PARTITION BY n ORDER BY id DESC) FROM t ORDER BY id",
    "SELECT id,SUM(n) OVER (ORDER BY id),COUNT(*) OVER () FROM t ORDER BY id",
    "SELECT id,SUM(n) OVER (ORDER BY n) FROM t ORDER BY id",
    "SELECT id,SUM(n) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND 1 FOLLOWING) FROM t ORDER BY id",
    "SELECT id,AVG(n) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM t ORDER BY id",
    "SELECT id,MIN(n) OVER (ORDER BY id ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING),MAX(n) OVER () FROM t ORDER BY id",
    "SELECT id,LAG(s,1,'missing') OVER (ORDER BY id),LEAD(s) OVER (ORDER BY id) FROM t ORDER BY id",
    "SELECT id,FIRST_VALUE(s) OVER (ORDER BY n,id),LAST_VALUE(s) OVER (ORDER BY id),NTH_VALUE(s,2) OVER (ORDER BY id) FROM t ORDER BY id",
    "SELECT id,NTILE(3) OVER (ORDER BY id),PERCENT_RANK() OVER (ORDER BY n),CUME_DIST() OVER (ORDER BY n) FROM t ORDER BY id",
    "SELECT id,NTILE(10) OVER (ORDER BY id) FROM t ORDER BY id",
    "SELECT id,SUM(n) OVER (ORDER BY n GROUPS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM t ORDER BY id",
    "SELECT id,SUM(n) OVER (ORDER BY n ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING EXCLUDE GROUP) FROM t ORDER BY id",
    "SELECT id,COUNT(*) OVER (ORDER BY n ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW EXCLUDE TIES) FROM t ORDER BY id",
    "SELECT id,SUM(n) OVER w,ROW_NUMBER() OVER w FROM t WINDOW w AS (ORDER BY id) ORDER BY id",
    "SELECT id,ROW_NUMBER() OVER (ORDER BY id)+10 AS position FROM t ORDER BY position DESC LIMIT 2",
    "SELECT id FROM t ORDER BY ROW_NUMBER() OVER (ORDER BY id DESC) LIMIT 2",
    "SELECT id,SUM(n) OVER (ORDER BY id ROWS BETWEEN 2 PRECEDING AND 1 PRECEDING) FROM t ORDER BY id",
    "SELECT id,ROW_NUMBER() OVER (ORDER BY id) FROM t WHERE id<0"
}

)
    Compare(sql);
await Both("CREATE INDEX ix_t_n_id ON t(n,id DESC)");
foreach (string sql in new[]
{
    "SELECT id,n FROM t WHERE n=10 ORDER BY id DESC",
    "SELECT id,n FROM t WHERE n>=10 AND n<=30 ORDER BY n,id DESC",
    "SELECT id,n FROM t WHERE n=10 AND id>1 ORDER BY id DESC",
    "SELECT id,n FROM t ORDER BY n,id DESC LIMIT 3",
    "SELECT id,n FROM t ORDER BY n DESC,id ASC LIMIT 3",
    "SELECT id,n FROM t WHERE n BETWEEN 10 AND 20 ORDER BY n,id DESC",
    "SELECT id,n FROM t WHERE n=NULL ORDER BY n,id DESC"
}

)
    Compare(sql);
Check(db.Query("SELECT id FROM t WHERE n=10 ORDER BY id DESC").Plan.Contains("CompositeIndexSeek"), "使用复合索引左前缀");
await Both("CREATE TABLE unique_rows(id INTEGER PRIMARY KEY,a INTEGER,b TEXT); CREATE UNIQUE INDEX ux_ab ON unique_rows(a,b); INSERT INTO unique_rows VALUES(1,1,'x'),(2,1,'y'),(3,NULL,'x'),(4,NULL,'x')");
try
{
    await db.ExecuteAsync("INSERT INTO unique_rows VALUES(5,1,'x')");
    throw new Exception("复合唯一约束未检查");
}
catch (SqlException)
{
}

await Both("UPDATE unique_rows SET b='z' WHERE id=1; INSERT INTO unique_rows VALUES(5,1,'x')");
Compare("SELECT id,a,b FROM unique_rows ORDER BY id");
await Both("CREATE VIEW big_rows(key,amount) AS SELECT id,n FROM t WHERE n>=10");
Compare("SELECT key,amount FROM big_rows ORDER BY key");
await Both("CREATE TABLE audit(id INTEGER PRIMARY KEY,row_id INTEGER,old_n INTEGER,new_n INTEGER); CREATE TRIGGER record_insert AFTER INSERT ON t BEGIN INSERT INTO audit(row_id,new_n) VALUES(NEW.id,NEW.n); END");
await Both("INSERT INTO t VALUES(10,50,'new')");
Compare("SELECT id,row_id,old_n,new_n FROM audit ORDER BY id");
await Both("CREATE TRIGGER record_update AFTER UPDATE OF n ON t WHEN NEW.n!=OLD.n BEGIN INSERT INTO audit(row_id,old_n,new_n) VALUES(NEW.id,OLD.n,NEW.n); END; UPDATE t SET n=60 WHERE id=10");
Compare("SELECT id,row_id,old_n,new_n FROM audit ORDER BY id");
await Both("CREATE TRIGGER reject_negative BEFORE INSERT ON t WHEN NEW.n<0 BEGIN SELECT RAISE(ABORT,'negative n'); END");
try
{
    await db.ExecuteAsync("INSERT INTO t VALUES(11,70,'ok'),(12,-1,'bad')");
    throw new Exception("触发器未中止");
}
catch (SqlException)
{
}

Check(db.Query("SELECT id FROM t WHERE id=11").Rows.Count == 0, "触发器失败回滚整条语句");
await Both("CREATE TRIGGER write_big INSTEAD OF INSERT ON big_rows BEGIN INSERT INTO t(id,n,s) VALUES(NEW.key,NEW.amount,'view'); END; INSERT INTO big_rows VALUES(20,80)");
Compare("SELECT key,amount FROM big_rows ORDER BY key");
await Both("INSERT INTO t(id,n,s) SELECT id+100,n,s FROM t WHERE id<3");
Compare("SELECT id,n FROM t WHERE id>=100 ORDER BY id");
sqlite.Exec("PRAGMA foreign_keys=ON");
await Both("CREATE TABLE parent(id INTEGER PRIMARY KEY,code TEXT UNIQUE); CREATE TABLE child(id INTEGER PRIMARY KEY,parent_id INTEGER REFERENCES parent(id) ON UPDATE CASCADE ON DELETE CASCADE); INSERT INTO parent VALUES(1,'p1'); INSERT INTO child VALUES(1,1)");
try
{
    await db.ExecuteAsync("INSERT INTO child VALUES(2,999)");
    throw new Exception("外键未检查");
}
catch (SqlException)
{
}

await Both("UPDATE parent SET id=2 WHERE id=1");
Compare("SELECT id,parent_id FROM child ORDER BY id");
await Both("DELETE FROM parent WHERE id=2");
Compare("SELECT id,parent_id FROM child ORDER BY id");
await Both("CREATE TABLE default_child(id INTEGER PRIMARY KEY,parent_id INTEGER DEFAULT 1 REFERENCES parent(id) ON DELETE SET DEFAULT); INSERT INTO parent VALUES(1,'fallback'),(2,'remove'); INSERT INTO default_child VALUES(1,2); DELETE FROM parent WHERE id=2");
Compare("SELECT id,parent_id FROM default_child ORDER BY id");
await Both("BEGIN TRANSACTION; INSERT INTO t VALUES(200,200,'tx'); SAVEPOINT a; UPDATE t SET n=201 WHERE id=200; SAVEPOINT b; INSERT INTO t VALUES(201,201,'nested')");
Compare("SELECT id,n FROM t WHERE id>=200 ORDER BY id");
await Both("ROLLBACK TRANSACTION TO SAVEPOINT a; RELEASE SAVEPOINT a; END TRANSACTION");
Compare("SELECT id,n FROM t WHERE id>=200 ORDER BY id");
await Both("SAVEPOINT outer_point; INSERT INTO t VALUES(210,210,'rollback'); SAVEPOINT same; UPDATE t SET n=211 WHERE id=210; SAVEPOINT same; UPDATE t SET n=212 WHERE id=210; ROLLBACK TO same; RELEASE same; ROLLBACK TO same; RELEASE same; RELEASE outer_point");
Compare("SELECT id,n FROM t WHERE id=210");
await Both("BEGIN IMMEDIATE; SAVEPOINT schema_point; CREATE TABLE rolled_back(id INTEGER PRIMARY KEY,n TEXT); INSERT INTO rolled_back VALUES(1,'temporary'); ROLLBACK TO schema_point; CREATE TABLE rolled_back(id INTEGER PRIMARY KEY,n INTEGER); INSERT INTO rolled_back VALUES(1,7); COMMIT");
Compare("SELECT id,n FROM rolled_back");
await Both("BEGIN EXCLUSIVE; UPDATE t SET n=999 WHERE id=1; ROLLBACK TRANSACTION");
Compare("SELECT id,n FROM t WHERE id=1");
await Both("CREATE TABLE deferred_child(id INTEGER PRIMARY KEY,parent_id INTEGER REFERENCES parent(id) DEFERRABLE INITIALLY DEFERRED); BEGIN; INSERT INTO deferred_child VALUES(1,999)");
try
{
    await db.ExecuteAsync("COMMIT");
    throw new Exception("延迟外键未检查");
}
catch (SqlException)
{
}

try
{
    sqlite.Exec("COMMIT");
    throw new Exception("SQLite 延迟外键未检查");
}
catch (IOException)
{
}

await Both("INSERT INTO parent VALUES(999,'deferred'); COMMIT");
Compare("SELECT id,parent_id FROM deferred_child");
using (var connection = new PureDbConnection("Data Source=" + Path.Combine(root, "ado-savepoints.puredb") + ";Access=Exclusive;Durability=Buffered"))
{
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "CREATE TABLE values_table(id INTEGER PRIMARY KEY,n INTEGER)";
    command.ExecuteNonQuery();
    using var transaction = connection.BeginTransaction();
    command.Transaction = transaction;
    command.CommandText = "INSERT INTO values_table VALUES(1,10)";
    command.ExecuteNonQuery();
    Check(transaction.SupportsSavepoints, "ADO.NET 保存点声明");
    await transaction.SaveAsync("中文 ' 保存点");
    command.CommandText = "UPDATE values_table SET n=20 WHERE id=1";
    command.ExecuteNonQuery();
    await transaction.RollbackAsync("中文 ' 保存点");
    await transaction.ReleaseAsync("中文 ' 保存点");
    command.CommandText = "SELECT n FROM values_table WHERE id=1";
    Check(Equals(command.ExecuteScalar(), 10L), "ADO.NET 保存点回滚");
    transaction.Commit();
}

foreach (string sql in new[]
{
    "SELECT id FROM t ORDER BY id LIMIT -1 OFFSET -3",
    "SELECT id FROM t WHERE n=10 ORDER BY id LIMIT -1",
    "SELECT COUNT(*) FROM t LIMIT -1",
    "SELECT id,CASE WHEN n IS NULL THEN 'empty' WHEN n<20 THEN 'small' ELSE 'large' END AS size FROM t ORDER BY id",
    "SELECT id,CASE n WHEN NULL THEN 'wrong' WHEN 10 THEN 'ten' ELSE 'other' END FROM t ORDER BY id",
    "SELECT CASE WHEN 1 THEN 7 ELSE json_extract('invalid','$') END",
    "SELECT CASE WHEN COUNT(*)>0 THEN SUM(n) ELSE 0 END FROM t"
}

)
    Compare(sql);
await Both("WITH input(id,n,s) AS (SELECT 300,30,'cte') INSERT INTO t SELECT id,n,s FROM input");
await Both("WITH input AS (SELECT 300 AS id) UPDATE t SET n=n+1 WHERE id IN (SELECT id FROM input)");
Compare("SELECT id,n FROM t WHERE id=300");
await Both("WITH input AS (SELECT 300 AS id) DELETE FROM t WHERE id IN (SELECT id FROM input)");
Compare("SELECT id,n FROM t WHERE id=300");
foreach (string sql in new[]
{
    "SELECT id,SUM(id) OVER (ORDER BY n RANGE BETWEEN 10 PRECEDING AND CURRENT ROW) FROM t ORDER BY id",
    "SELECT id,SUM(id) OVER (ORDER BY n DESC RANGE BETWEEN 10 PRECEDING AND 3 FOLLOWING) FROM t ORDER BY id",
    "SELECT id,COUNT(*) OVER (ORDER BY n RANGE BETWEEN 0.5 PRECEDING AND 1.5 FOLLOWING) FROM t ORDER BY id",
    "SELECT id,COUNT(*) OVER (ORDER BY s RANGE BETWEEN 1 PRECEDING AND 1 FOLLOWING) FROM t ORDER BY id",
    "SELECT n,COUNT(*) AS total,ROW_NUMBER() OVER (ORDER BY n) AS rank FROM t GROUP BY n ORDER BY n",
    "SELECT n,SUM(id),SUM(SUM(id)) OVER (ORDER BY n) FROM t GROUP BY n HAVING COUNT(*)>0 ORDER BY n",
    "SELECT COUNT(*),SUM(COUNT(*)) OVER () FROM t GROUP BY n ORDER BY COUNT(*),SUM(id)",
    "WITH groups AS (SELECT n,COUNT(*) AS total FROM t GROUP BY n) SELECT n,total,RANK() OVER (ORDER BY total DESC) FROM groups ORDER BY n"
}

)
    Compare(sql);
foreach (string sql in new[]
{
    "WITH seq(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM seq WHERE x<5) SELECT x FROM seq ORDER BY x",
    "WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT 10 UNION ALL SELECT x+1 FROM seq WHERE x<3) SELECT x FROM seq ORDER BY x",
    "WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM seq LIMIT 5) SELECT x FROM seq",
    "WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM seq LIMIT 5 OFFSET 2) SELECT x FROM seq",
    "WITH RECURSIVE seq(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM seq) SELECT x FROM seq LIMIT 8 OFFSET 2",
    "WITH RECURSIVE tree(x) AS (SELECT 1 UNION ALL SELECT x*2 FROM tree WHERE x<5 UNION ALL SELECT x*2+1 FROM tree WHERE x<5 ORDER BY 1 DESC LIMIT 6) SELECT x FROM tree"
}

)
    Compare(sql);
await Both("CREATE TABLE automatic(id INTEGER PRIMARY KEY,n INTEGER); CREATE TABLE automatic_audit(id INTEGER PRIMARY KEY,allocated INTEGER); CREATE TRIGGER automatic_delete AFTER INSERT ON automatic BEGIN INSERT INTO automatic_audit(allocated) VALUES(NEW.id); DELETE FROM automatic WHERE id=NEW.id; END; INSERT INTO automatic VALUES(NULL,1),(NULL,2)");
Compare("SELECT id,allocated FROM automatic_audit ORDER BY id");
foreach (string sql in new[]
{
    "SELECT key,value,type,fullkey,path FROM json_tree('[1,2,3]','$[#-1]')",
    "SELECT key,value,type,fullkey,path FROM json_tree('{\"a.b[\":{\"x\":7}}','$.\"a.b[\"') ORDER BY fullkey",
    "SELECT key,value,type,fullkey,path FROM json_each('{\"a.b[\":[1,2]}','$.\"a.b[\"') ORDER BY key"
}

)
    Compare(sql);
await Both("CREATE TABLE random_rows(id INTEGER PRIMARY KEY,a INTEGER,b INTEGER,c TEXT); CREATE INDEX ix_random ON random_rows(a DESC,b ASC,c DESC)");
var random = new Random(20261024);
var tuples = Enumerable.Range(1, 240).Select(i => $"({i},{(i % 11 == 0 ? "NULL" : random.Next(0, 9).ToString())},{random.Next(-6, 8)},'v{random.Next(0, 5)}')");
await Both("INSERT INTO random_rows VALUES" + string.Join(',', tuples));
for (int i = 0; i < 80; i++)
{
    int a = random.Next(0, 9), low = random.Next(-8, 5), high = random.Next(5, 10);
    Compare($"SELECT id,a,b,c FROM random_rows WHERE a={a} AND b>={low} AND b<{high} ORDER BY b,c DESC,id LIMIT {random.Next(1, 30)} OFFSET {random.Next(0, 4)}");
}

foreach (string frame in new[]
{
    "ROWS BETWEEN 3 PRECEDING AND 2 FOLLOWING",
    "ROWS BETWEEN 4 FOLLOWING AND 8 FOLLOWING",
    "ROWS BETWEEN 9 PRECEDING AND 3 PRECEDING",
    "GROUPS BETWEEN 1 PRECEDING AND 2 FOLLOWING",
    "RANGE BETWEEN 2 PRECEDING AND 1 FOLLOWING"
}

)
    foreach (string exclude in new[]
    {
        "NO OTHERS",
        "CURRENT ROW",
        "GROUP",
        "TIES"
    }

    )
    {
        bool uniqueOrder = frame.StartsWith("ROWS");
        string values = uniqueOrder ? ",FIRST_VALUE(c) OVER w,LAST_VALUE(c) OVER w,NTH_VALUE(c,2) OVER w" : "";
        Compare($"SELECT id,SUM(b) OVER w,AVG(b) OVER w,MIN(b) OVER w,MAX(b) OVER w,COUNT(b) OVER w{values} FROM random_rows WINDOW w AS (PARTITION BY a ORDER BY b{(uniqueOrder ? ",id" : "")} {frame} EXCLUDE {exclude}) ORDER BY id");
    }

await Both("UPDATE random_rows SET b=b+1 WHERE a=3; DELETE FROM random_rows WHERE a=4; INSERT INTO random_rows VALUES(500,4,2,'new')");
Compare("SELECT id,a,b,c FROM random_rows WHERE a>=3 AND a<6 ORDER BY a DESC,b,c DESC,id");
await Both("CREATE TABLE compound_parent(id INTEGER PRIMARY KEY,a INTEGER,b TEXT,UNIQUE(a,b)); CREATE TABLE compound_child(id INTEGER PRIMARY KEY,a INTEGER,b TEXT,FOREIGN KEY(a,b) REFERENCES compound_parent(a,b) ON DELETE SET NULL ON UPDATE CASCADE); INSERT INTO compound_parent VALUES(1,10,'a'); INSERT INTO compound_child VALUES(1,10,'a'),(2,NULL,'other'); UPDATE compound_parent SET a=11 WHERE id=1");
Compare("SELECT id,a,b FROM compound_child ORDER BY id");
await Both("DELETE FROM compound_parent WHERE id=1");
Compare("SELECT id,a,b FROM compound_child ORDER BY id");
await Both("SAVEPOINT release_failure; INSERT INTO deferred_child VALUES(2,888)");
try
{
    await db.ExecuteAsync("RELEASE release_failure");
    throw new Exception("外层保存点未验证外键");
}
catch (SqlException)
{
}

try
{
    sqlite.Exec("RELEASE release_failure");
    throw new Exception("SQLite 外层保存点未验证外键");
}
catch (IOException)
{
}

await Both("ROLLBACK TO release_failure; RELEASE release_failure");
Compare("SELECT id,parent_id FROM deferred_child ORDER BY id");
foreach (string sql in new[]
{
    "SELECT (SELECT missing FROM t) FROM t WHERE id<0",
    "SELECT (SELECT id,n FROM t) FROM t WHERE id<0",
    "SELECT ROW_NUMBER() OVER (ORDER BY id ROWS BETWEEN -1 PRECEDING AND CURRENT ROW) FROM t",
    "SELECT * FROM unknown_table",
    "SELECT * FROM json_each('[1]',42)",
    "WITH RECURSIVE x(n) AS (SELECT 1 UNION ALL SELECT COUNT(*) FROM x) SELECT n FROM x"
}

)
    try
    {
        db.Query(sql);
        throw new Exception("无效查询被接受: " + sql);
    }
    catch (SqlException)
    {
    }

Compare("SELECT id,ROW_NUMBER() OVER (ORDER BY id),ROW_NUMBER() OVER (ORDER BY id DESC) FROM t ORDER BY ROW_NUMBER() OVER (ORDER BY id DESC)");
Compare("SELECT (SELECT 1) AS a,(SELECT 2) AS b FROM t ORDER BY (SELECT 2) LIMIT 2");
Compare("SELECT ALL id FROM t a WHERE a.n=(SELECT a.n) ORDER BY id");
Compare("SELECT a.id,b.id FROM t a JOIN t b ON a.id=b.id WHERE b.n=10 ORDER BY a.id");
Compare("SELECT EXISTS(SELECT DISTINCT n FROM t LIMIT 1 OFFSET 1)");
Compare("SELECT EXISTS(SELECT DISTINCT 1 FROM t LIMIT 1 OFFSET 1)");
Compare("SELECT SUM(COUNT(*)) OVER () FROM t");
Compare("SELECT LAG(SUM(n),0) OVER () FROM t");
var metadata = db.Query("SELECT q.id,ROW_NUMBER() OVER () FROM (SELECT id FROM t WHERE id<0) q");
Check(metadata.FieldTypes.SequenceEqual(new[] { typeof(long), typeof(long) }), "空派生表和窗口元数据");
Check(db.Query("SELECT 1 AS n UNION ALL SELECT 'x'").FieldTypes.Single() == typeof(object), "集合混合类型不错误声明为整数");
string untouched = Path.Combine(root, "must-not-overwrite.sqlite");
db.ExportSqlite(untouched);
using (var nativeExport = new Sqlite(untouched, Durability.Full, configure: false))
{
    using var integrity = nativeExport.Prepare("PRAGMA integrity_check");
    Check(integrity.Step() && Equals(integrity.Values()[0], "ok"), "扩展模式导出后的原生完整性");
}
File.Delete(untouched);
File.WriteAllText(untouched, "existing destination");
try
{
    db.ExportSqlite(untouched);
    throw new Exception("不应覆盖既有文件");
}
catch (IOException)
{
}

Check(File.ReadAllText(untouched) == "existing destination", "拒绝导出不得破坏既有目标");
string compatible = Path.Combine(root, "extended-queries.sqlite");
using (var managed = PureDb.SqliteCompatibility.SqliteCompatibility.Open(compatible, new() { LinuxExclusiveProcess = OperatingSystem.IsLinux() }))
{
    await managed.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER); INSERT INTO t VALUES(1,10),(2,20)");
    Check(Equals(managed.Query("WITH x AS (SELECT id,n FROM t) SELECT SUM(n) OVER () FROM x ORDER BY id LIMIT 1").Rows[0][0], 30L), "SQLite 文件复用扩展查询执行器");
    await managed.ExecuteAsync("INSERT INTO t VALUES(1,5) ON CONFLICT(id) DO UPDATE SET n=n+excluded.n");
    Check(Equals(managed.Query("SELECT n FROM t WHERE id=1").Rows[0][0], 15L), "SQLite 文件后端 UPSERT");
}

foreach (string sql in new[]
{
    "CREATE VIEW v AS SELECT id FROM t",
    "CREATE INDEX compound ON t(n,id)",
    "CREATE TABLE child(id INTEGER PRIMARY KEY,p INTEGER REFERENCES t(id))",
    "CREATE TRIGGER tr AFTER INSERT ON t BEGIN DELETE FROM t WHERE id=NEW.id; END",
    "CREATE TABLE defaults(id INTEGER PRIMARY KEY,n INTEGER DEFAULT 1)"
}

)
{
    using (var managed = PureDb.SqliteCompatibility.SqliteCompatibility.Open(compatible, new() { LinuxExclusiveProcess = OperatingSystem.IsLinux() }))
    {
        await managed.ExecuteAsync(sql);
        Check(managed.Query("SELECT COUNT(*) FROM t").Rows.Count == 1, "扩展模式创建后连接继续可用");
    }


}

using (var original = new Sqlite(compatible, Durability.Buffered))
{
    using var check = original.Prepare("PRAGMA integrity_check");
    Check(check.Step() && Equals(check.Values()[0], "ok"), "兼容扩展模式后的原生完整性");
}

foreach (string ddl in new[]
{
    "CREATE TABLE t(id INTEGER PRIMARY KEY,a INTEGER CHECK(a>0))",
    "CREATE TABLE t(id INTEGER PRIMARY KEY,a INTEGER DEFAULT 1)",
    "CREATE TABLE t(id INTEGER PRIMARY KEY,a INTEGER,b INTEGER,UNIQUE(a,b))",
    "CREATE TABLE t(id INTEGER PRIMARY KEY,a INTEGER REFERENCES t(id))",
    "CREATE TABLE t(id INTEGER PRIMARY KEY,a INTEGER);CREATE INDEX c ON t(a,id)"
}

)
{
    string source = Path.Combine(root, Guid.NewGuid().ToString("N") + ".sqlite"), target = source + ".pdb";
    using (var native = new Sqlite(source, Durability.Full, configure: false))
        native.Exec(ddl);
    byte[] bytes = File.ReadAllBytes(source);
    using (var accepted = PureDb.SqliteCompatibility.SqliteCompatibility.Open(source, new() { LinuxExclusiveProcess = OperatingSystem.IsLinux() }))
        Check(accepted.Query("SELECT COUNT(*) FROM t").Rows.Count == 1, "读取原生扩展模式");
    await SqlDatabase.ImportSqliteAsync(source, target);
    Check(File.Exists(target) && bytes.SequenceEqual(File.ReadAllBytes(source)), "导入扩展模式应成功且源文件保持不变");
}

using (var first = new SqlDatabase(Path.Combine(root, "shared.puredb"), new() { Storage = new() { Access = DatabaseAccess.Shared, Durability = Durability.Buffered } }))
using (var second = new SqlDatabase(Path.Combine(root, "shared.puredb"), new() { Storage = new() { Access = DatabaseAccess.Shared, Durability = Durability.Buffered } }))
{
    await first.ExecuteAsync("CREATE TABLE shared_rows(id INTEGER PRIMARY KEY,n INTEGER); INSERT INTO shared_rows VALUES(1,1)");
    await first.ExecuteAsync("BEGIN; SAVEPOINT p; UPDATE shared_rows SET n=2 WHERE id=1");
    Check(Equals(second.Query("SELECT n FROM shared_rows").Rows[0][0], 1L), "事务暂存对其他连接不可见");
    await second.ExecuteAsync("UPDATE shared_rows SET n=3 WHERE id=1");
    try
    {
        await first.ExecuteAsync("COMMIT");
        throw new Exception("乐观事务冲突未检测");
    }
    catch (System.Data.DBConcurrencyException)
    {
    }

    await first.ExecuteAsync("ROLLBACK");
    Check(Equals(first.Query("SELECT n FROM shared_rows").Rows[0][0], 3L), "冲突未覆盖已提交内容");
    try
    {
        await first.ExecuteAsync("BEGIN IMMEDIATE");
        throw new Exception("Shared 模式不应冒充 SQLite 预留锁");
    }
    catch (NotSupportedException)
    {
    }
}

await Both("CREATE TABLE drop_parent(id INTEGER PRIMARY KEY);CREATE TABLE drop_child(id INTEGER PRIMARY KEY,p INTEGER REFERENCES drop_parent(id));INSERT INTO drop_parent VALUES(1);INSERT INTO drop_child VALUES(1,1)");
try
{
    await db.ExecuteAsync("DROP TABLE drop_parent");
    throw new Exception("DROP TABLE 不得留下非空悬挂外键");
}
catch (SqlException)
{
}

try
{
    sqlite.Exec("DROP TABLE drop_parent");
    throw new Exception("SQLite DROP TABLE 未检查外键");
}
catch (IOException)
{
}

Check(db.Query("SELECT id FROM drop_parent").Rows.Count == 1, "DROP 外键失败须恢复父表模式和数据");
await Both("DELETE FROM drop_child;DROP TABLE drop_parent;DROP TABLE drop_child");
await Both("CREATE TABLE nullable_parent(id INTEGER PRIMARY KEY);CREATE TABLE nullable_child(id INTEGER PRIMARY KEY,p INTEGER REFERENCES nullable_parent(id) ON DELETE SET NULL);INSERT INTO nullable_parent VALUES(1);INSERT INTO nullable_child VALUES(1,1);DROP TABLE nullable_parent");
Check(db.Query("SELECT p FROM nullable_child").Rows[0][0] is null, "DROP TABLE 的 SET NULL 动作应允许保留空引用");
await Both("DROP TABLE nullable_child");
await Both("CREATE TABLE checked_rows(id INTEGER PRIMARY KEY,n INTEGER CHECK(n>=0),max_n INTEGER,CONSTRAINT max_check CHECK(max_n IS NULL OR n<=max_n));INSERT INTO checked_rows VALUES(1,5,10),(2,NULL,10)");
foreach (string sql in new[]
{
    "INSERT INTO checked_rows VALUES(3,-1,10)",
    "UPDATE checked_rows SET n=11 WHERE id=1",
    "INSERT INTO checked_rows VALUES(4,1,10),(5,12,10)"
}

)
{
    try
    {
        await db.ExecuteAsync(sql);
        throw new Exception("CHECK 未拒绝不合法行");
    }
    catch (SqlException)
    {
    }

    try
    {
        sqlite.Exec(sql);
        throw new Exception("SQLite CHECK 未拒绝不合法行");
    }
    catch (IOException)
    {
    }
}

Compare("SELECT id,n,max_n FROM checked_rows ORDER BY id");
await Both("CREATE TABLE upserts(id INTEGER PRIMARY KEY,code TEXT UNIQUE,a INTEGER,b TEXT,n INTEGER CHECK(n>=0),UNIQUE(a,b));INSERT INTO upserts VALUES(1,'one',10,'a',1),(2,'two',20,'b',2)");
foreach (string sql in new[]
{
    "INSERT INTO upserts VALUES(1,'ignored',11,'x',3) ON CONFLICT(id) DO UPDATE SET n=n+excluded.n",
    "INSERT INTO upserts VALUES(3,'two',30,'c',8) ON CONFLICT(code) DO UPDATE SET n=excluded.n WHERE excluded.n>n",
    "INSERT INTO upserts VALUES(4,'four',10,'a',9) ON CONFLICT(b,a) DO UPDATE SET n=upserts.n+1",
    "INSERT INTO upserts VALUES(1,'ignored',1,'i',1),(5,'five',50,'e',5) ON CONFLICT DO NOTHING",
    "INSERT INTO upserts VALUES(6,'six',NULL,'e',6),(7,'seven',NULL,'e',7) ON CONFLICT(a,b) DO UPDATE SET n=excluded.n",
    "INSERT INTO upserts(id,code,a,b,n) SELECT 1,'new',1,'q',3 WHERE 1 ON CONFLICT(id) DO UPDATE SET n=excluded.n",
    "WITH input AS (SELECT 1 AS id,2 AS n) INSERT INTO upserts(id,n) SELECT id,n FROM input WHERE 1 ON CONFLICT(id) DO UPDATE SET n=n+excluded.n"
}

)
{
    await Both(sql);
    Compare("SELECT id,code,a,b,n FROM upserts ORDER BY id");
}

foreach (string sql in new[]
{
    "INSERT INTO upserts VALUES(9,'one',9,'i',1) ON CONFLICT(id) DO NOTHING",
    "INSERT INTO upserts VALUES(1,'one',10,'a',-1) ON CONFLICT DO NOTHING",
    "INSERT INTO upserts(id,n) VALUES(10,1),(1,2) ON CONFLICT(id) DO UPDATE SET n=-1",
    "INSERT INTO upserts(id,n) VALUES(10,1) ON CONFLICT(id) DO UPDATE SET missing=1",
    "INSERT INTO upserts(id,n) VALUES(10,1) ON CONFLICT(n) DO NOTHING"
}

)
{
    try
    {
        await db.ExecuteAsync(sql);
        throw new Exception("无效 UPSERT 被接受: " + sql);
    }
    catch (SqlException)
    {
    }

    try
    {
        sqlite.Exec(sql);
        throw new Exception("SQLite 无效 UPSERT 被接受: " + sql);
    }
    catch (IOException)
    {
    }
}

Compare("SELECT id,code,a,b,n FROM upserts ORDER BY id");
await Both("CREATE TABLE upsert_audit(id INTEGER PRIMARY KEY,event TEXT,row_id INTEGER);CREATE TRIGGER upsert_before BEFORE INSERT ON upserts BEGIN INSERT INTO upsert_audit(event,row_id) VALUES('before',NEW.id);END;CREATE TRIGGER upsert_update AFTER UPDATE ON upserts BEGIN INSERT INTO upsert_audit(event,row_id) VALUES('update',NEW.id);END;INSERT INTO upserts(id,n) VALUES(1,20) ON CONFLICT(id) DO UPDATE SET n=excluded.n");
Compare("SELECT id,event,row_id FROM upsert_audit ORDER BY id");
await Both("INSERT INTO upserts(code,n) VALUES('one',1),('one',1),('auto-new',1) ON CONFLICT(code) DO UPDATE SET n=n+1");
Compare("SELECT id,code,n FROM upserts ORDER BY id");
db.CreateFunction<long, bool>("valid_n", n => n > 0);
await db.ExecuteAsync("CREATE TABLE function_check(id INTEGER PRIMARY KEY,n INTEGER CHECK(valid_n(n)));BEGIN;INSERT INTO function_check VALUES(1,1)");
db.CreateFunction<long, bool>("valid_n", n => n > 10);
try
{
    await db.ExecuteAsync("INSERT INTO function_check VALUES(2,2)");
    throw new Exception("CHECK 继续使用已替换的函数");
}
catch (SqlException)
{
}

await db.ExecuteAsync("ROLLBACK;DROP TABLE function_check");
// 大整数边界同时验证数值及 typeof，覆盖常量、行投影与主键 DML 三条路径。
foreach (string expression in new[]
{
    "-9223372036854775808", "9223372036854775807+1", "-9223372036854775808-1",
    "3037000500*3037000500", "-(-9223372036854775808)", "-9223372036854775808/-1",
    "-9223372036854775808%-1", "5.9%2.1", "-5.9%2", "9007199254740993%2.0",
    "5%0.5", "9223372036854775807>9223372036854775806.0", "9007199254740993=9007199254740992.0",
    "1e308*1e308", "-1e308*1e308", "(1e308*1e308)-(1e308*1e308)", "(1e308*1e308)/(1e308*1e308)"
}) Compare($"SELECT {expression},typeof({expression})");
foreach (string expression in new[]
{
    "NULL IS NULL", "NULL IS 0", "1 IS 1.0", "1 IS NOT '1'", "NULL IS NOT NULL",
    "NULL IS DISTINCT FROM 1", "1 IS NOT DISTINCT FROM 1.0", "NULL IS NOT DISTINCT FROM NULL",
    "2 IS TRUE", "2 IS NOT TRUE", "0 IS FALSE", "NULL IS FALSE", "NULL IS NOT FALSE",
    "'1tail' IS TRUE", "'bad' IS FALSE", "2 IS (TRUE)", "2 IS +TRUE", "2 IS TRUE+1",
    "'3.0'+1", "'3'+1", "'bad'+1", "X'3132'+1", "'1e2'*2", "NOT 'bad'", "NOT '1tail'",
    "1||2+3", "1+2||3", "2*3||4", "~1", "~NULL", "~'3.5'", "3&5", "3|4",
    "1<<63", "1<<64", "-1>>64", "8<<-1", "8>>-1", "1<<-9223372036854775808",
    "1|2&4", "1+2<<3", "1==1.0", "2=1<1", "NULL&3", "2 IS NULL+1",
    "2 IS DISTINCT FROM TRUE", "2 IS NOT DISTINCT FROM TRUE"
}) Compare($"SELECT {expression},typeof({expression})");
await Both("CREATE TABLE integer_edges(id INTEGER PRIMARY KEY,n INTEGER);INSERT INTO integer_edges VALUES(1,9223372036854775807),(2,-9223372036854775808),(3,9007199254740993)");
Compare("SELECT id,n+1,typeof(n+1),n/-1,n%-1,-n,n%2.0 FROM integer_edges ORDER BY id");
await Both("UPDATE integer_edges SET n=n%2.0 WHERE id=3");
Compare("SELECT id,n,typeof(n) FROM integer_edges ORDER BY id");
foreach (string expression in new[] { new string('(', 500) + "1" + new string(')', 500), string.Join('+', Enumerable.Repeat("1", 5000)) })
{
    try { db.Query("SELECT " + expression); throw new Exception("Deep SQL was accepted"); }
    catch (SqlException) { }
    Compare("SELECT 1+2");
}
db.Dispose();
using (var reopened = new SqlDatabase(Path.Combine(root, "test.puredb"), new() { Storage = new() { Durability = Durability.Buffered } }))
{
    Compare("SELECT id,n,max_n FROM checked_rows ORDER BY id", reopened);
    try
    {
        await reopened.ExecuteAsync("INSERT INTO checked_rows VALUES(999,-1,1)");
        throw new Exception("重开后 CHECK 丢失");
    }
    catch (SqlException)
    {
    }

    Compare("SELECT key,amount FROM big_rows ORDER BY key", reopened);
    Compare("SELECT id,n FROM t WHERE n=10 ORDER BY id DESC", reopened);
    await reopened.ExecuteAsync("INSERT INTO t VALUES(900,90,'reopened')");
    sqlite.Exec("INSERT INTO t VALUES(900,90,'reopened')");
    Compare("SELECT id,row_id,new_n FROM audit ORDER BY id", reopened);
    try
    {
        await reopened.ExecuteAsync("INSERT INTO child VALUES(999,123456)");
        throw new Exception("重开后的外键丢失");
    }
    catch (SqlException)
    {
    }

    await reopened.ExecuteAsync("INSERT INTO default_child(id) VALUES(2)");
    sqlite.Exec("INSERT INTO default_child(id) VALUES(2)");
    Compare("SELECT id,parent_id FROM default_child ORDER BY id", reopened);
}

Console.WriteLine($"PASS {comparisons} extended SQL differential queries; SQLite {Sqlite.Version}: {root}");
