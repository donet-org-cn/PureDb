using System.Diagnostics;
using PureDb;

if (args.Length > 0)
{
    string mode = args[0], path = args[1];
    if (mode == "sql-worker")
    {
        int worker = int.Parse(args[2]);
        await using var db = new SqlDatabase(path, new() { Storage = new() { Access = DatabaseAccess.Shared, Durability = Durability.Buffered } });
        for (int i = 0; i < 40; i++)
        {
            await db.ExecuteAsync("UPDATE counters SET value=value+1 WHERE id=1; INSERT INTO events(id,worker) VALUES(@id,@worker)", new Dictionary<string, object?> { ["id"] = worker * 1000 + i, ["worker"] = worker });
            if (worker == 0 && i % 10 == 0) await db.CompactAsync();
            var result = db.Query("SELECT value FROM counters WHERE id=1");
            if ((long)result.Rows[0][0]! < i + 1) throw new Exception("Stale shared read");
        }
        return;
    }
    if (mode == "shared-abandon")
    {
        var db = new Database(path, new() { Access = DatabaseAccess.Shared });
        await db.PutAsync(55, 55, new byte[] { 55 });
        string canonical = OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path);
        var mutex = new Mutex(false, "PureDb-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))));
        mutex.WaitOne();
        using var tail = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        tail.Write(new byte[] { 1, 2, 3, 4, 5 }); tail.Flush(true);
        Console.WriteLine("ABANDONED"); Console.Out.Flush(); Process.GetCurrentProcess().Kill(); return;
    }
    if (mode == "shared-kill")
    {
        var db = new Database(path, new() { Access = DatabaseAccess.Shared });
        await db.PutAsync(99999, 99, new byte[] { 99 }); Console.WriteLine("COMMITTED"); Console.Out.Flush(); Process.GetCurrentProcess().Kill(); return;
    }
}
string root = Path.GetFullPath(Path.Combine("artifacts", "sql-tests", Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root);
int passed = 0;
void Assert(bool c, string message) { if (!c) throw new Exception(message); }
async Task Test(string name, Func<Task> body) { await body(); passed++; Console.WriteLine("PASS " + name); }
async Task Reject(Func<Task> body) { try { await body(); } catch (SqlException) { return; } throw new Exception("Expected SQL failure"); }
string FileFor(string n) => Path.Combine(root, n + ".pdb");
await Test("SQL DDL, typed parameters, DML, aggregates, persistence", async () =>
{
    string path = FileFor("sql");
    await using (var db = new SqlDatabase(path))
    {
        await db.ExecuteAsync("CREATE TABLE users(id INTEGER PRIMARY KEY, name TEXT NOT NULL, age INTEGER, score REAL, data BLOB); CREATE INDEX age_idx ON users(age)");
        await db.ExecuteAsync("INSERT INTO users VALUES(1,'Alice',20,1.5,X'0102'),(2,'O''Brien',30,2,NULL),(3,'Carol',NULL,NULL,NULL)");
        await db.ExecuteAsync("INSERT INTO users(id,name,age) VALUES(@id,@name,@age)", new Dictionary<string, object?> { ["id"] = 4, ["name"] = "Delta", ["age"] = 30 });
        var row = db.Query("SELECT name,age,data FROM users WHERE id=?1", new Dictionary<string, object?> { ["1"] = 1 });
        Assert((string)row.Rows[0][0]! == "Alice" && ((byte[])row.Rows[0][2]!).SequenceEqual(new byte[] { 1, 2 }) && row.Plan.Contains("PrimaryKeySeek"), "parameter seek");
        var range = db.Query("SELECT id FROM users WHERE age BETWEEN 20 AND 30 ORDER BY age DESC LIMIT 2 OFFSET 1");
        Assert(range.Rows.Select(r => (long)r[0]!).SequenceEqual(new long[] { 2, 1 }) && range.Plan.Contains("IndexRangeSeek"), "index range");
        Assert(await db.ExecuteAsync("UPDATE users SET age=age+1, name=UPPER(name) WHERE id=1") == 1, "update");
        Assert((long)db.Query("SELECT age FROM users WHERE id=1").Rows[0][0]! == 21, "updated row");
        var aggregates = db.Query("SELECT COUNT(*), COUNT(age), SUM(age), AVG(age), MIN(name), MAX(age) FROM users").Rows[0];
        Assert((long)aggregates[0]! == 4 && (long)aggregates[1]! == 3 && (long)aggregates[2]! == 81 && (double)aggregates[3]! == 27, "aggregates");
        var grouped = db.Query("SELECT age, COUNT(*) AS n FROM users GROUP BY age HAVING COUNT(*)>=1 ORDER BY n DESC, age ASC");
        Assert(grouped.Rows.LengthCompat() == 3 && (long)grouped.Rows[0][1]! == 2, "group by");
        Assert(await db.ExecuteAsync("DELETE FROM users WHERE age IS NULL") == 1, "delete");
        await db.CompactAsync();
    }
    await using var reopened = new SqlDatabase(path);
    Assert((long)reopened.Query("SELECT COUNT(*) FROM users").Rows[0][0]! == 3, "SQL reopen");
});
await Test("INNER/LEFT/RIGHT/FULL/CROSS joins, duplicate and NULL keys", async () =>
{
    await using var db = new SqlDatabase(FileFor("join"), new() { Storage = new() { Durability = Durability.Buffered } });
    await db.ExecuteAsync("CREATE TABLE a(id INTEGER PRIMARY KEY,k INTEGER); CREATE TABLE b(id INTEGER PRIMARY KEY,k INTEGER); CREATE INDEX bk ON b(k); INSERT INTO a VALUES(1,7),(2,7),(3,NULL),(4,9); INSERT INTO b VALUES(10,7),(11,7),(12,NULL),(13,10)");
    Assert(db.Query("SELECT a.id,b.id FROM a INNER JOIN b ON a.k=b.k").Rows.Count == 4, "inner duplicates");
    var left = db.Query("SELECT a.id,b.id FROM a LEFT JOIN b ON a.k=b.k ORDER BY a.id,b.id"); Assert(left.Rows.Count == 6 && left.Plan.Contains("IndexJoin"), "left");
    Assert(db.Query("SELECT a.id,b.id FROM a RIGHT JOIN b ON a.k=b.k").Rows.Count == 6, "right");
    Assert(db.Query("SELECT a.id,b.id FROM a FULL OUTER JOIN b ON a.k=b.k").Rows.Count == 8, "full");
    Assert(db.Query("SELECT * FROM a CROSS JOIN b").Rows.Count == 16, "cross");
    Assert(db.Query("SELECT a.id FROM a LEFT JOIN b ON a.k=b.k WHERE b.id IS NULL ORDER BY a.id").Rows.Select(r => (long)r[0]!).SequenceEqual(new long[] { 3, 4 }), "outer WHERE");
    Assert(db.Query("SELECT a.id,b.id FROM a LEFT JOIN b ON a.k=b.k AND b.id>10 WHERE a.id=1").Rows.Count == 1, "residual ON");
    Assert(db.Query("SELECT a.id,b.id FROM a JOIN b ON a.id<b.id").Rows.Count == 16, "non equijoin");
    Assert(db.Query("SELECT x.id,y.id,z.id FROM a x JOIN a y ON x.id=y.id JOIN a z ON y.id=z.id").Rows.Count == 4, "multiway self join");
    await Reject(() => Task.Run(() => db.Query("SELECT id FROM a JOIN b ON a.k=b.k")));
});
await Test("NULL three-valued logic, LIKE, IN, DISTINCT, aliases, validation", async () =>
{
    await using var db = new SqlDatabase(FileFor("expressions"), new() { Storage = new() { Durability = Durability.Buffered } });
    await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,a INTEGER,b TEXT); INSERT INTO t VALUES(1,NULL,'abc'),(2,2,'Abd'),(3,2,'def')");
    Assert(db.Query("SELECT id FROM t WHERE NOT(a=2)").Rows.Count == 0, "NOT null");
    Assert(db.Query("SELECT id FROM t WHERE a NOT IN(2,NULL)").Rows.Count == 0, "NOT IN null");
    Assert(db.Query("SELECT id FROM t WHERE a IS NULL OR a=2 AND b LIKE 'ab%'").Rows.Count == 2, "precedence LIKE");
    Assert(db.Query("SELECT DISTINCT a FROM t ORDER BY a").Rows.Count == 2, "distinct");
    Assert(db.Query("SELECT b AS name FROM t ORDER BY name DESC LIMIT 1").Rows[0][0]!.Equals("def"), "order alias");
    Assert((long)db.Query("SELECT COUNT(*), SUM(a) FROM t WHERE id=100").Rows[0][0]! == 0, "empty aggregates");
    foreach (string sql in new[] { "SELECT missing FROM t", "SELECT a FROM t WHERE unknown=1", "SELECT id FROM t LIMIT -1", "SELECT id FROM t ORDER BY 9", "SELECT id FROM t UNION SELECT id FROM t", "SELECT COUNT(a,b) FROM t" }) await Reject(() => Task.Run(() => db.Query(sql)));
});
await Test("transaction rollback, constraints and writer remains usable", async () =>
{
    await using var db = new SqlDatabase(FileFor("atomic"));
    await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,name TEXT UNIQUE NOT NULL); INSERT INTO t VALUES(1,'one')");
    await Reject(async () => await db.ExecuteAsync("INSERT INTO t VALUES(2,'two'); INSERT INTO t VALUES(1,'duplicate')"));
    Assert((long)db.Query("SELECT COUNT(*) FROM t").Rows[0][0]! == 1, "rollback");
    await Reject(async () => await db.ExecuteAsync("CREATE TABLE rolled(id INTEGER PRIMARY KEY); INSERT INTO t VALUES(2,'one')"));
    await Reject(() => Task.Run(() => db.Query("SELECT * FROM rolled")));
    await Reject(async () => await db.ExecuteAsync("INSERT INTO t VALUES(2,NULL)"));
    await Reject(async () => await db.ExecuteAsync("INSERT INTO t VALUES(2,99)"));
    await db.ExecuteTransactionAsync([new("INSERT INTO t VALUES(@id,@name)", new Dictionary<string, object?> { ["id"] = 2, ["name"] = "two" }), new("UPDATE t SET name='THREE' WHERE id=2")]);
    Assert(db.Query("SELECT name FROM t WHERE id=2").Rows[0][0]!.Equals("THREE"), "transaction read your writes");
});
await Test("shared instances tail new commits and follow compaction generations", async () =>
{
    var path = FileFor("shared"); var options = new DatabaseOptions { Access = DatabaseAccess.Shared };
    await using var a = new Database(path, options); await using var b = new Database(path, options);
    await a.PutAsync(1, 1, new byte[] { 1 }); Assert(b.Get(1)!.Value[0] == 1, "follow commit");
    await b.PutAsync(1, 2, new byte[] { 2 }); Assert(a.Get(1)!.IndexKey == 2, "follow update");
    await a.CompactAsync(); Assert(b.Get(1)!.Value[0] == 2, "follow compact");
    await b.PutAsync(2, 2, new byte[200000]); Assert(a.Count == 2, "append after compact");
    await a.DeleteAsync(1); await a.CompactAsync(); Assert(b.Get(1) is null && b.Count == 1, "follow second compact");
});
await Test("four OS processes atomic SQL updates, concurrent compaction and visibility", async () =>
{
    string path = FileFor("processes");
    await using var parent = new SqlDatabase(path, new() { Storage = new() { Access = DatabaseAccess.Shared } });
    await parent.ExecuteAsync("CREATE TABLE counters(id INTEGER PRIMARY KEY,value INTEGER); INSERT INTO counters VALUES(1,0); CREATE TABLE events(id INTEGER PRIMARY KEY,worker INTEGER)");
    var children = Enumerable.Range(0, 4).Select(w => Start("sql-worker", path, w.ToString())).ToArray();
    foreach (var p in children)
    {
        string error = await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(); Assert(p.ExitCode == 0, "child failure: " + error); p.Dispose();
    }
    Assert((long)parent.Query("SELECT value FROM counters WHERE id=1").Rows[0][0]! == 160, "lost concurrent update");
    Assert((long)parent.Query("SELECT COUNT(*) FROM events").Rows[0][0]! == 160, "lost child inserts");
});
await Test("shared mode process kill recovery and incompatible access rejection", async () =>
{
    string path = FileFor("kill");
    await using var db = new Database(path, new() { Access = DatabaseAccess.Shared });
    using var p = Start("shared-kill", path);
    string output = await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync(); Assert(output.Contains("COMMITTED"), "child commit");
    Assert(db.Get(99999)!.Value[0] == 99, "read after kill");
    bool rejected = false; try { using var exclusive = new Database(path); } catch (IOException) { rejected = true; } Assert(rejected, "mixed access must reject");
    await db.PutAsync(4, 4, new byte[1]);
});
await Test("JSON / JSONB columns, paths, arrows, edits, validation and persistence", async () =>
{
    string path = FileFor("json");
    await using (var db = new SqlDatabase(path))
    {
        await db.ExecuteAsync("CREATE TABLE docs(id INTEGER PRIMARY KEY,body JSON,bin JSONB); INSERT INTO docs VALUES(1,'{\"name\":\"Alice\",\"age\":20,\"tags\":[\"a\",\"b\"],\"nested\":{\"ok\":true},\"nil\":null}',JSONB('{\"x\":1}'))");
        var values = db.Query("SELECT json_extract(body,'$.name'), body->>'$.age', body->'$.tags', json_type(body,'$.nil'), json_type(body,'$.missing'), json_extract(bin,'$.x') FROM docs WHERE id=1").Rows[0];
        Assert(values[0]!.Equals("Alice") && values[1]!.Equals(20L) && values[2]!.Equals("[\"a\",\"b\"]") && values[3]!.Equals("null") && values[4] is null && values[5]!.Equals(1L), "JSON scalar/type/path");
        await db.ExecuteAsync("UPDATE docs SET body=json_set(body,'$.age',21,'$.tags[#]','c'),bin=jsonb_set(bin,'$.x',2) WHERE id=1");
        Assert(db.Query("SELECT body->>'$.age',body->>'$.tags[#-1]',bin->>'$.x' FROM docs").Rows[0].SequenceEqual(new object?[] { 21L, "c", 2L }), "JSON update");
        Assert(db.Query("SELECT json_extract(json_object('a',json_array(1,'x',NULL)), '$.a[1]') FROM docs").Rows[0][0]!.Equals("x"), "JSON constructors preserve subtype");
        Assert(db.Query("SELECT json_extract(json_remove(body,'$.name'),'$.name') FROM docs").Rows[0][0] is null, "remove");
        Assert(db.Query("SELECT json_extract(json_patch(body,'{\"age\":99}'),'$.age') FROM docs").Rows[0][0]!.Equals(99L), "patch");
        Assert(db.Query("SELECT json_array_length(json_array_insert('[1,2]','$[1]',9)) FROM docs").Rows[0][0]!.Equals(3L), "array insert");
        Assert(db.Query("SELECT json_valid('bad'),json_valid(body),json_type(body,'$.nested.ok') FROM docs").Rows[0].SequenceEqual(new object?[] { 0L, 1L, "true" }), "valid");
        Assert(db.Query("SELECT json_valid(bin),json_valid(bin,8) FROM docs").Rows[0].SequenceEqual(new object?[] { 0L, 1L }), "JSONB validity flags");
        await Reject(async () => await db.ExecuteAsync("INSERT INTO docs VALUES(2,'bad',NULL)"));
        await Reject(() => Task.Run(() => db.Query("SELECT json_extract(body,'not a path') FROM docs")));
        await db.ExecuteAsync("CREATE INDEX json_age ON docs(json_extract(body,'$.age'))");
        Assert(db.Query("SELECT id FROM docs WHERE json_extract(body,'$.age')=21").Plan.Contains("ExpressionIndexSeek"), "JSON index plan");
        await db.ExecuteAsync("UPDATE docs SET body=json_set(body,'$.age',22) WHERE id=1");
        Assert(db.Query("SELECT id FROM docs WHERE json_extract(body,'$.age')=21").Rows.Count == 0, "JSON index invalidation");
        await db.ExecuteAsync("UPDATE docs SET body=json_set(body,'$.age',21) WHERE id=1");
        await db.CompactAsync();
    }
    await using var reopened = new SqlDatabase(path);
    Assert(reopened.Query("SELECT id FROM docs WHERE json_extract(body,'$.age')=21").Plan.Contains("ExpressionIndexSeek"), "persisted JSON index");
    Assert(reopened.Query("SELECT json_extract(body,'$.age'),json_extract(bin,'$.x') FROM docs").Rows[0].SequenceEqual(new object?[] { 21L, 2L }), "JSON persistence");
});
await Test("abandoned process mutex repairs an interrupted append", async () =>
{
    string path = FileFor("abandon");
    await using var db = new Database(path, new() { Access = DatabaseAccess.Shared });
    using var p = Start("shared-abandon", path); string output = await p.StandardOutput.ReadToEndAsync(); await p.WaitForExitAsync();
    Assert(output.Contains("ABANDONED"), "did not abandon mutex");
    Assert(db.Get(55)!.Value[0] == 55, "abandoned recovery lost commit");
    await db.PutAsync(56, 56, new byte[] { 56 }); Assert(db.Count == 2, "write after abandoned recovery");
});
await Test("constant SELECT, exact numeric comparisons, cached plans and index constraints", async () =>
{
    await using var db = new SqlDatabase(FileFor("edge"), new() { Storage = new() { Durability = Durability.Buffered } });
    Assert(db.Query("SELECT json_extract('{\"n\":1}','$.n'),1+2").Rows[0].SequenceEqual(new object?[] { 1L, 3L }), "SELECT without FROM");
    Assert(db.Query("SELECT 9007199254740993=9007199254740992.0,1e100>9223372036854775807").Rows[0].SequenceEqual(new object?[] { 0L, 1L }), "numeric precision");
    await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,a INTEGER,b INTEGER,body TEXT); CREATE INDEX ia ON t(a); INSERT INTO t VALUES(1,3,9,'{\"x\":1}'),(2,3,8,'{\"x\":2}'),(3,4,7,'bad')");
    Assert(db.Query("SELECT b AS a FROM t WHERE a=3 ORDER BY a").Rows.Select(r => (long)r[0]!).SequenceEqual(new long[] { 8, 9 }), "ORDER alias shadows source");
    Assert(db.Query("SELECT id FROM t WHERE a=3 ORDER BY 1 DESC LIMIT 1").Rows[0][0]!.Equals(2L), "ordinal and cached range fallback");
    await Reject(async () => await db.ExecuteAsync("CREATE INDEX badjson ON t(json_extract(body,'$.x'))"));
    await db.ExecuteAsync("DELETE FROM t WHERE id=3; CREATE UNIQUE INDEX ux ON t(json_extract(body,'$.x'))");
    await Reject(async () => await db.ExecuteAsync("INSERT INTO t VALUES(3,5,6,'{\"x\":1}')"));
    await Reject(async () => await db.ExecuteAsync("INSERT INTO t VALUES(3,5,6,'bad')"));
    await db.ExecuteAsync("DROP INDEX ia; CREATE INDEX ib ON t(b)");
    Assert(db.Query("SELECT id FROM t WHERE a=3 ORDER BY a,id LIMIT 10").Rows.Count == 2, "cached plan after index change");
});
await Test("batched UNIQUE indexes, reuse after delete, projected UTF8 rows and count metadata", async () =>
{
    await using var db = new SqlDatabase(FileFor("optimizations"), new() { Storage = new() { Durability = Durability.Buffered } });
    await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,name TEXT UNIQUE,v BLOB); CREATE TABLE other(id INTEGER PRIMARY KEY,k INTEGER); CREATE INDEX other_k ON other(k)");
    string unicode = string.Concat(Enumerable.Repeat("\u4e2d\u6587\U0001F600", 3000));
    await db.ExecuteAsync("INSERT INTO t VALUES(@id,@text,@blob)",new Dictionary<string,object?> { ["id"]=1L,["text"]=unicode,["blob"]=new byte[257] });
    Assert(db.Query("SELECT name,id FROM t WHERE id=1").Rows[0].SequenceEqual(new object?[]{unicode,1L}),"span UTF8 projection");
    Assert((long)db.Query("SELECT COUNT(*) FROM t").Rows[0][0]! == 1,"count metadata");
    await db.ExecuteAsync("INSERT INTO t VALUES(2,'reuse',NULL); DELETE FROM t WHERE id=2; INSERT INTO t VALUES(3,'reuse',NULL)");
    await Reject(async()=>await db.ExecuteAsync("INSERT INTO t VALUES(4,'new',NULL),(5,'new',NULL)"));
    Assert((long)db.Query("SELECT COUNT(*) FROM t").Rows[0][0]! == 2,"duplicate transaction rollback");
    await db.ExecuteAsync("INSERT INTO other VALUES(1,3),(2,4)"); db.Query("SELECT * FROM other WHERE k BETWEEN 0 AND 10");
    await db.ExecuteAsync("DELETE FROM t WHERE id=3");
    Assert(db.Query("SELECT * FROM other WHERE k BETWEEN 0 AND 10").Rows.Count == 2,"unrelated table cache");
    await db.ExecuteAsync("CREATE UNIQUE INDEX u2 ON other(k); UPDATE other SET k=k+10 WHERE id=1; INSERT INTO other VALUES(3,3)");
    Assert(db.Query("SELECT * FROM other WHERE k=3").Rows.Count == 1,"unique update frees old key");
    var requests=Enumerable.Range(10,80).Select(i=>db.ExecuteAsync("INSERT INTO t VALUES(@id,@name,NULL)",new Dictionary<string,object?>{["id"]=(long)i,["name"]="n"+i}).AsTask()).ToArray();
    await Task.WhenAll(requests); Assert((long)db.Query("SELECT COUNT(*) FROM t").Rows[0][0]! == 81,"SQL group commit");
    await db.CompactAsync(); Assert((long)db.Query("SELECT COUNT(*) FROM t").Rows[0][0]! == 81,"count after compact");
    Assert((long)db.Query("SELECT SUM(k) FROM other").Rows[0][0]! == 20,"stream sum");
    Assert((double)db.Query("SELECT AVG(k) FROM other WHERE k>3").Rows[0][0]! == 8.5,"stream filtered average");
    var grouped=db.Query("SELECT k,COUNT(*) FROM other GROUP BY k ORDER BY k");Assert(grouped.Rows.Count==3 && grouped.Rows[0][0]!.Equals(3L),"hash group count");
    await Reject(()=>Task.Run(()=>db.Query("SELECT nope.k,COUNT(*) FROM other GROUP BY other.k")));
    await Reject(()=>Task.Run(()=>db.Query("SELECT k,COUNT(*) FROM other GROUP BY k ORDER BY nope.k")));
});
await Test("cached primary DML rebinding and JOIN projections match general execution", async () =>
{
    await using var fast = new SqlDatabase(FileFor("cached-fast"), new(){Storage=new(){Durability=Durability.Buffered}});
    await using var slow = new SqlDatabase(FileFor("cached-general"), new(){Storage=new(){Durability=Durability.Buffered}});
    const string seed = "CREATE TABLE a(id INTEGER PRIMARY KEY,k INTEGER,n INTEGER,text TEXT,body JSON,v BLOB); CREATE TABLE b(id INTEGER PRIMARY KEY,k INTEGER,v BLOB);CREATE INDEX bk ON b(k);INSERT INTO a VALUES(1,7,4,'one','{\"x\":1}',X'0102'),(2,NULL,5,'two','{\"x\":2}',NULL),(3,9,6,'three','{\"x\":3}',X'03');INSERT INTO b VALUES(20,7,X'20'),(10,7,X'10'),(30,9,NULL),(40,NULL,X'40')";
    await fast.ExecuteAsync(seed); await slow.ExecuteAsync(seed);
    string Shape(SqlResult result) => System.Text.Json.JsonSerializer.Serialize(new { result.Columns, result.Rows });
    var random = new Random(3814);
    var p = new Dictionary<string,object?> { ["id"]=1L, ["n"]=1L, ["text"]="x", ["limit"]=2L, ["offset"]=0L };
    string[] updates=["SET k=k+@n,n=n-@n,text=UPPER(@text)","SET k=n,n=k,text=COALESCE(@text,'empty')","SET n=LENGTH(@text)+json_extract(body,'$.x'),text=@text","SET n=(n*@n)%17,text=LOWER(@text)"];
    for(int iteration=0;iteration<120;iteration++)
    {
        p["id"]=(long)random.Next(1,5);p["n"]=(long)random.Next(-3,4);p["text"]=iteration%7==0?null:"Utf8-中文-"+iteration;
        string update="UPDATE a "+updates[iteration%updates.Length]+" WHERE id=@id";
        Assert(await fast.ExecuteAsync(update,p)==await slow.ExecuteAsync(update+" AND 1=1",p),"DML affected");
        Assert(Shape(fast.Query("SELECT * FROM a ORDER BY id"))==Shape(slow.Query("SELECT * FROM a ORDER BY id")),"DML parameter/assignment mismatch");
    }
    await fast.ExecuteAsync("UPDATE a SET k=7 WHERE id=1;UPDATE a SET k=NULL WHERE id=2;UPDATE a SET k=9 WHERE id=3");
    await slow.ExecuteAsync("UPDATE a SET k=7 WHERE id=1;UPDATE a SET k=NULL WHERE id=2;UPDATE a SET k=9 WHERE id=3");
    foreach(string kind in new[]{"JOIN","LEFT JOIN"})
    foreach(string order in new[]{"b.id","b.id DESC","rid DESC","2 DESC"})
    foreach(object? id in new object?[]{1L,2L,3L,4L,null,1.0,1.5,-1L,1L<<40})
    foreach(long offset in new[]{0L,1L,5L})
    {
        p["id"]=id;p["offset"]=offset;
        string prefix=$"SELECT a.id,b.id AS rid,a.v,b.v FROM a {kind} b ON b.k=a.k WHERE a.id=@id";
        string suffix=$" ORDER BY {order} LIMIT @limit OFFSET @offset";
        var actual=fast.Query(prefix+suffix,p);var expected=slow.Query(prefix+" AND 1=1"+suffix,p);
        Assert(Shape(actual)==Shape(expected),"cached JOIN null/order/offset mismatch");
        foreach(var row in actual.Rows)foreach(var value in row)if(value is byte[] blob)Array.Fill(blob,(byte)0xff);
        Assert(Shape(fast.Query(prefix+suffix,p))==Shape(expected),"JOIN leaked cached BLOB ownership");
    }
    p["id"]=999L;
    await Reject(async()=>await fast.ExecuteAsync("UPDATE a SET text=@missing WHERE id=@id",p));
    await Reject(async()=>await fast.ExecuteAsync("UPDATE a SET n=1,n=2 WHERE id=@id",p));
    await Reject(async()=>await fast.ExecuteAsync("UPDATE a SET n=@n WHERE id=@id",new Dictionary<string,object?>{["id"]=1L,["n"]=double.NaN}));
    Assert(await fast.ExecuteAsync("DELETE FROM a WHERE id=@id",p)==0,"delete miss");
    p["id"]=2L;Assert(await fast.ExecuteAsync("DELETE FROM a WHERE id=@id",p)==1,"delete cached parameter");
    p["id"]=3L;Assert(await fast.ExecuteAsync("DELETE FROM a WHERE @id=id",p)==1,"delete reverse predicate");
    Assert((long)fast.Query("SELECT COUNT(*) FROM a").Rows[0][0]! == 1L,"metadata after cached deletes");
});
await Test("cached JSON expression index projections match general execution and invalidate", async () =>
{
    await using var db = new SqlDatabase(FileFor("expression-plan"),new(){Storage=new(){Durability=Durability.Buffered}});
    await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,body JSON,v BLOB);CREATE INDEX j ON t(json_extract(body,'$.x'))");
    await db.ExecuteTransactionAsync(Enumerable.Range(0,60).Select(id=>new SqlCommand("INSERT INTO t VALUES(@id,@body,X'0102')",new Dictionary<string,object?>{["id"]=(long)id,["body"]=System.Text.Json.JsonSerializer.Serialize(new{x=id%7,name="n"+id})})).ToArray());
    string Shape(SqlResult result)=>System.Text.Json.JsonSerializer.Serialize(new{result.Columns,result.Rows});
    var parameters=new Dictionary<string,object?>{["lo"]=2L,["hi"]=4L,["take"]=9L,["skip"]=2L};
    foreach(string operation in new[]{"=","<","<=",">",">=","BETWEEN"})
    foreach(bool descending in new[]{false,true})
    foreach(object? low in new object?[]{null,-1L,2L,2.5,6L,100L})
    {
        parameters["lo"]=low;
        string expr="json_extract(body,'$.x')";
        string condition=expr+" "+operation+" @lo"+(operation=="BETWEEN"?" AND @hi":"");
        string select="SELECT id,json_extract(body,'$.name') AS name,v FROM t WHERE ";
        string order=" ORDER BY "+(operation=="="?"id":expr)+(descending?" DESC":"")+" LIMIT @take OFFSET @skip";
        var actual=db.Query(select+condition+order,parameters);
        var general=db.Query(select+"("+condition+") AND 1=1"+order,parameters);
        Assert(Shape(actual)==Shape(general),"JSON index prepared range/projection");
        foreach(var row in actual.Rows)Array.Fill((byte[])row[2]!, (byte)0xff);
        Assert(Shape(db.Query(select+condition+order,parameters))==Shape(general),"JSON index BLOB ownership");
    }
    const string lookup="SELECT id,json_extract(body,'$.name') FROM t WHERE json_extract(body,'$.x')=@lo ORDER BY id";
    parameters["lo"]=99L;Assert(db.Query(lookup,parameters).Rows.Count==0,"expression miss");
    await db.ExecuteAsync("UPDATE t SET body='{\"x\":99,\"name\":\"changed\"}' WHERE id=1");
    Assert(db.Query(lookup,parameters).Rows.Count==1,"expression cache invalidation");
    await db.ExecuteAsync("DROP INDEX j;CREATE INDEX other ON t(json_extract(body,'$.name'))");
    Assert(db.Query(lookup,parameters).Rows.Count==1,"expression plan schema invalidation");
    await Reject(()=>Task.Run(()=>db.Query("SELECT id FROM t WHERE json_extract(wrong.body,'$.name')='changed'")));
    await using var first = new SqlDatabase(FileFor("schema-a"),new(){Storage=new(){Durability=Durability.Buffered}});
    await using var second = new SqlDatabase(FileFor("schema-b"),new(){Storage=new(){Durability=Durability.Buffered}});
    foreach(var connection in new[]{first,second})
    {
        await connection.ExecuteAsync("CREATE TABLE shared_schema(id INTEGER PRIMARY KEY,v INTEGER);INSERT INTO shared_schema VALUES(1,7)");
        Assert((long)connection.Query("SELECT COUNT(*) FROM shared_schema").Rows[0][0]! == 1L,"shared catalog count");
    }
    await first.ExecuteAsync("CREATE UNIQUE INDEX uv ON shared_schema(v)");
    await Reject(async()=>await first.ExecuteAsync("INSERT INTO shared_schema VALUES(2,7)"));
    Assert(await second.ExecuteAsync("INSERT INTO shared_schema VALUES(2,7)")==1,"DDL mutated another database's cached schema");
    Assert((long)first.Query("SELECT COUNT(*) FROM shared_schema").Rows[0][0]! == 1L,"cached schema leaked rows");
});
await Test("primary interval DML preserves sparse bounds, atomicity and transaction overlays", async () =>
{
    string a=FileFor("interval-fast"),b=FileFor("interval-oracle");
    foreach(string path in new[]{a,b})
        using(var db=new SqlDatabase(path,new(){Storage=new(){Durability=Durability.Buffered}}))
        {
            await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER,v BLOB)");
            await db.ExecuteTransactionAsync(Enumerable.Range(0,200).Select(i=>new SqlCommand("INSERT INTO t VALUES(@id,@n,X'010203')",new Dictionary<string,object?>{["id"]=(long)i*2,["n"]=(long)i})).ToArray());
        }
    using var fast=new SqlDatabase(a,new(){Storage=new(){Durability=Durability.Buffered}});
    using var oracle=new SqlDatabase(b,new(){Storage=new(){Durability=Durability.Buffered}});
    string Shape(SqlDatabase db)=>System.Text.Json.JsonSerializer.Serialize(db.Query("SELECT * FROM t ORDER BY id").Rows);
    var p=new Dictionary<string,object?>{["lo"]=20L,["hi"]=81L};
    foreach(string op in new[]{">=",">","<=","<","BETWEEN"})
    foreach(object? bound in new object?[]{null,-100L,0L,20.5,80L,10000L,long.MaxValue,long.MinValue})
    {
        p["lo"]=bound;string suffix=" "+op+" @lo"+(op=="BETWEEN"?" AND @hi":"");
        Assert(await fast.ExecuteAsync("UPDATE t SET n=n+3 WHERE id"+suffix,p)==await oracle.ExecuteAsync("UPDATE t SET n=n+3 WHERE id+0"+suffix,p),"interval count");
        Assert(Shape(fast)==Shape(oracle),"interval update mismatch");
    }
    foreach(var db in new[]{fast,oracle})
        await db.ExecuteAsync("INSERT INTO t VALUES(401,1,X'FF');UPDATE t SET n=n+1 WHERE id BETWEEN 390 AND 410;DELETE FROM t WHERE id BETWEEN 395 AND 400");
    Assert(Shape(fast)==Shape(oracle),"transaction overlay");
    string before=Shape(fast);
    await Reject(async()=>await fast.ExecuteAsync("UPDATE t SET id=0 WHERE id BETWEEN 2 AND 10"));
    Assert(Shape(fast)==before,"interval constraint failure committed partially");
    foreach(string op in new[]{">",">=","BETWEEN","<","<="})
    {
        p["lo"]=80.5;string suffix=" "+op+" @lo"+(op=="BETWEEN"?" AND @hi":"");
        Assert(await fast.ExecuteAsync("DELETE FROM t WHERE id"+suffix,p)==await oracle.ExecuteAsync("DELETE FROM t WHERE id+0"+suffix,p),"interval delete count");
        Assert(Shape(fast)==Shape(oracle),"interval delete mismatch");
    }
});
await Test("vector JSON remainder count matches general SQL including NULL and signed extremes", async () =>
{
    using var db=new SqlDatabase(FileFor("vector-count"),new(){Storage=new(){Durability=Durability.Buffered}});
    await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,j JSON)");
    long?[] numbers=Enumerable.Range(-69,140).Select(i=>(long?)i).Concat(new long?[]{null,long.MinValue,long.MaxValue}).ToArray();
    await db.ExecuteTransactionAsync(numbers.Select((n,i)=>new SqlCommand("INSERT INTO t VALUES(@id,@j)",new Dictionary<string,object?>{["id"]=(long)i,["j"]=System.Text.Json.JsonSerializer.Serialize(new{x=n})})).ToArray());
    foreach(long divisor in new[]{0L,1L,2L,3L,4L,8L,64L,1L<<32,1L<<62,-2L})
    foreach(bool reverse in new[]{false,true})
    {
        var p=new Dictionary<string,object?>{["d"]=divisor};string expr="json_extract(j,'$.x')%@d";string condition=reverse?"0="+expr:expr+"=0";
        var fast=db.Query("SELECT COUNT(*) FROM t WHERE "+condition,p);
        var slow=db.Query("SELECT COUNT(*) FROM t WHERE ("+condition+") AND id>=0",p);
        Assert(Equals(fast.Rows[0][0],slow.Rows[0][0]),"SIMD remainder semantics");
    }
    await db.ExecuteAsync("UPDATE t SET j='{\"x\":2}' WHERE id=0;DELETE FROM t WHERE id=1");
    Assert(Equals(db.Query("SELECT COUNT(*) FROM t WHERE json_extract(j,'$.x')%2=0").Rows[0][0],db.Query("SELECT COUNT(*) FROM t WHERE json_extract(j,'$.x')%2=0 AND id>=0").Rows[0][0]),"SIMD cache invalidation");
});
await Test("cached sorted group counts preserve NULL, BLOB ownership, ordering and pagination",async()=>
{
    using var db=new SqlDatabase(FileFor("group-page"),new(){Storage=new(){Durability=Durability.Buffered}});
    await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,b BLOB);INSERT INTO t VALUES(1,NULL),(2,X'01'),(3,X'02'),(4,X'01'),(5,X'')");
    string Shape(SqlResult r)=>System.Text.Json.JsonSerializer.Serialize(new{r.Columns,r.Rows});
    foreach(string order in new[]{"b","b DESC","g DESC","1 DESC"})
    foreach(int offset in new[]{0,1,4,100})
    {
        string suffix=$" GROUP BY b ORDER BY {order} LIMIT 3 OFFSET {offset}";
        string query="SELECT b AS g,COUNT(*) AS n FROM t";
        var expected=db.Query(query+" WHERE 1=1"+suffix);var actual=db.Query(query+suffix);
        Assert(Shape(actual)==Shape(expected),"group sorting/limit mismatch");
        foreach(var row in actual.Rows)if(row[0] is byte[] bytes)Array.Fill(bytes,(byte)0xff);
        Assert(Shape(db.Query(query+suffix))==Shape(expected),"group BLOB alias");
    }
    await db.ExecuteAsync("UPDATE t SET b=X'02' WHERE id=2;DELETE FROM t WHERE id=1");
    Assert(Shape(db.Query("SELECT b,COUNT(*) FROM t GROUP BY b ORDER BY b"))==Shape(db.Query("SELECT b,COUNT(*) FROM t WHERE 1=1 GROUP BY b ORDER BY b")),"sorted group invalidation");
});
Console.WriteLine($"{passed} SQL/shared test groups passed: {root}");
Process Start(params string[] values)
{
    var si = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
    si.ArgumentList.Add(typeof(Program).Assembly.Location); foreach (var v in values) si.ArgumentList.Add(v); return Process.Start(si)!;
}
static class Helpers { public static int LengthCompat<T>(this IReadOnlyList<T> list) => list.Count; }
