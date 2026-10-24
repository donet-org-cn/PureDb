using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using PureDb;
using PureDb.Benchmarks;

if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SQLITE_LIBRARY") is null) Environment.SetEnvironmentVariable("SQLITE_LIBRARY", Path.GetFullPath("artifacts/sqlite/3.54.1/sqlite3.dll"));
var folder = Path.GetFullPath("artifacts/storage-tests/" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
void Check(bool value, string message) { if (!value) throw new Exception(message); }
async Task Reject<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
int passed = 0;
async Task Test(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + name); }
if (args.Length > 0 && args[0] == "--tail-worker")
{
    using var db = new Database(args[1], new() { Access = DatabaseAccess.Shared });
    await db.PutAsync(10001, 9, new byte[] { 77 }); Console.WriteLine("READY"); await Task.Delay(Timeout.Infinite); return;
}
await Test("paged primary index, boundary keys, writes, deletes and compaction", async () =>
{
    string path = Path.Combine(folder, "primary.pdb"); var oracle = new Dictionary<long, byte[]>();
    await using (var db = new Database(path, new() { Durability = Durability.Buffered }))
    {
        var batch = new List<Mutation>();
        for (int i = 0; i < 9000; i++) { long id = i == 0 ? long.MinValue : i == 8999 ? long.MaxValue : i; byte[] v = BitConverter.GetBytes(id); oracle[id] = v; batch.Add(Mutation.Put(id, i % 11, v)); }
        await db.WriteAsync(batch);
    }
    for (int pass = 0; pass < 3; pass++)
    {
        await using var db = new Database(path, new() { Durability = Durability.Buffered }); Check(db.Count == oracle.Count, "count");
        foreach (var pair in oracle) Check(db.Get(pair.Key)!.Value.SequenceEqual(pair.Value), "primary data");
        await db.PutAsync(10000 + pass, 7, new byte[] { (byte)pass }); oracle[10000 + pass] = [(byte)pass];
        await db.DeleteAsync(pass == 0 ? long.MinValue : pass); oracle.Remove(pass == 0 ? long.MinValue : pass);
        if (pass == 1) await db.CompactAsync(); db.VerifyIntegrity();
    }
});
await Test("checkpoint crash tail is recovered exactly once", async () =>
{
    string path = Path.Combine(folder, "tail.pdb");
    using (var db = new Database(path, new() { Access = DatabaseAccess.Shared })) await db.PutAsync(1, 1, new byte[] { 5 });
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add("--tail-worker"); start.ArgumentList.Add(path);
    using (var child = Process.Start(start)!)
    {
        Check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)) == "READY", "child admission"); child.Kill(true); await child.WaitForExitAsync();
    }
    using var read = new Database(path, new() { Access = DatabaseAccess.Shared }); Check(read.Count == 2 && read.Get(10001)!.Value[0] == 77, "tail lost"); read.VerifyIntegrity();
});
await Test("periodic checkpoint exists before close and SQL close prepares fresh indexes", async () =>
{
    string path = Path.Combine(folder, "periodic.pdb");
    using (var db = new Database(path, new() { Durability = Durability.Buffered, CheckpointIntervalBytes = 100 }))
    { await db.PutAsync(1, 7, new byte[200]); Check(File.Exists(path + ".pidx") && db.CheckpointError is null, "periodic checkpoint"); }
    string sql = Path.Combine(folder, "automatic-sql.pdb");
    using (var db = new SqlDatabase(sql, new() { Storage = new() { Durability = Durability.Buffered } }))
        await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,k INTEGER); CREATE INDEX ix ON t(k); INSERT INTO t VALUES(1,10),(2,20)");
    using var read = new SqlDatabase(sql); Check(read.Query("SELECT id FROM t WHERE k>=10 ORDER BY k").Plan.StartsWith("PersistentIndex"), "SQL close did not prepare index");
});
await Test("bad checkpoint header rebuild, lazy page CRC and strict data integrity", async () =>
{
    string path = Path.Combine(folder, "corruption.pdb"); using (var db = new Database(path)) await db.PutAsync(1, 1, new byte[100]);
    byte[] index = File.ReadAllBytes(path + ".pidx"); index[0] ^= 1; File.WriteAllBytes(path + ".pidx", index);
    using (var db = new Database(path)) Check(db.Get(1)!.Value.Length == 100, "header rebuild");
    byte[] valid = File.ReadAllBytes(path + ".pidx"); var bad = valid.ToArray(); bad[^30] ^= 1; File.WriteAllBytes(path + ".pidx", bad);
    await Reject<InvalidDataException>(() => { using var db = new Database(path); db.Get(1); return Task.CompletedTask; });
    File.WriteAllBytes(path + ".pidx", valid); DateTime time = File.GetLastWriteTimeUtc(path); byte[] data = File.ReadAllBytes(path); data[^1] ^= 1; File.WriteAllBytes(path, data); File.SetLastWriteTimeUtc(path, time);
    await Reject<InvalidDataException>(() => { using var db = new Database(path); db.Get(1); return Task.CompletedTask; });
    await Reject<InvalidDataException>(() => { using var db = new Database(path, new() { VerifyOnOpen = true }); return Task.CompletedTask; });
});
await Test("persistent SQL ranges, limits, exact numeric bounds, invalidation and shared writers", async () =>
{
    string path = Path.Combine(folder, "ranges.pdb");
    using (var db = new SqlDatabase(path, new() { Storage = new() { Durability = Durability.Buffered } }))
    {
        await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,k INTEGER,b BLOB); CREATE INDEX ix ON t(k)");
        await db.ExecuteTransactionAsync(Enumerable.Range(0, 4000).Select(i => new SqlCommand("INSERT INTO t VALUES(@id,@k,@b)", new Dictionary<string, object?> { ["id"] = (long)i, ["k"] = (long)(i % 50), ["b"] = new byte[] { (byte)i } })).ToArray());
        Check(db.Query("SELECT id,k FROM t WHERE k BETWEEN 10 AND 20 ORDER BY k,id LIMIT 20").Rows.Count == 20, "prime");
    }
    using (var db = new SqlDatabase(path))
    {
        var range = db.Query("SELECT id,k FROM t WHERE k BETWEEN 10 AND 20 ORDER BY k,id LIMIT 20"); Check(range.Plan.StartsWith("PersistentIndex"), "not persistent path");
        Check((long)range.Rows[0][0]! == 10 && (long)range.Rows[19][0]! == 960, "ties");
        var desc = db.Query("SELECT id FROM t WHERE k=10 ORDER BY id DESC LIMIT 5 OFFSET 2"); Check((long)desc.Rows[0][0]! == 3860, "descending offset");
        Check(db.Query("SELECT id FROM t WHERE k>49.5 ORDER BY k").Rows.Count == 0, "fractional");
        await db.ExecuteAsync("UPDATE t SET k=60 WHERE id=10; DELETE FROM t WHERE id=60");
        Check(db.Query("SELECT id FROM t WHERE k=10 ORDER BY id").Rows.Count == 78, "invalidation");
        await db.CompactAsync(); Check(db.Query("SELECT id FROM t WHERE k=60").Rows.Count == 1, "compaction");
    }
    using var a = new SqlDatabase(path, new() { Storage = new() { Access = DatabaseAccess.Shared, Durability = Durability.Buffered } });
    using var b = new SqlDatabase(path, new() { Storage = new() { Access = DatabaseAccess.Shared, Durability = Durability.Buffered } });
    a.Query("SELECT id FROM t WHERE k=60"); await b.ExecuteAsync("UPDATE t SET k=61 WHERE id=10");
    Check(a.Query("SELECT id FROM t WHERE k=60").Rows.Count == 0 && a.Query("SELECT id FROM t WHERE k=61").Rows.Count == 1, "shared invalidation");
});
await Test("managed SQLite export: official integrity_check, indexes, overflow, JSON and subsequent writes", async () =>
{
    string path = Path.Combine(folder, "exchange.pdb"), sqlite = Path.Combine(folder, "export.sqlite");
    using (var db = new SqlDatabase(path, new() { Storage = new() { Durability = Durability.Buffered } }))
    {
        await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,k INTEGER,name TEXT UNIQUE,b BLOB,j JSONB); CREATE INDEX ix ON t(k); CREATE INDEX jx ON t(json_extract(j,'$.n'))");
        var commands = Enumerable.Range(0, 9000).Select(i => new SqlCommand("INSERT INTO t VALUES(@id,@k,@name,@b,jsonb(@j))", new Dictionary<string, object?> { ["id"] = (long)i, ["k"] = (long)(i % 101), ["name"] = "中文😀'" + i, ["b"] = Enumerable.Repeat((byte)i, i % 100 == 0 ? 20000 : 12).ToArray(), ["j"] = "{\"n\":" + i + "}" })).ToArray();
        foreach (var batch in commands.Chunk(300)) await db.ExecuteTransactionAsync(batch);
        db.ExportSqlite(sqlite);
    }
    using (var db = new Sqlite(sqlite, Durability.Buffered))
    {
        using var integrity = db.Prepare("PRAGMA integrity_check"); Check(integrity.Step() && (string)integrity.Values()[0]! == "ok", "SQLite rejected managed file");
        using var count = db.Prepare("SELECT COUNT(*),SUM(k),SUM(length(b)) FROM t"); Check(count.Step() && count.Int64(0) == 9000, "export row count");
        using var row = db.Prepare("SELECT name,length(b),json_extract(j,'$.n') FROM t WHERE id=100"); Check(row.Step() && (string)row.Values()[0]! == "中文😀'100" && row.Int64(1) == 20000 && row.Int64(2) == 100, "typed export");
        db.Exec("UPDATE t SET k=1000 WHERE id=100; INSERT INTO t VALUES(9000,1,'new',X'00','{\"n\":9000}')");
        bool uniqueRejected = false; try { db.Exec("INSERT INTO t VALUES(9001,1,'new',NULL,NULL)"); } catch (IOException) { uniqueRejected = true; } Check(uniqueRejected, "unique index lost");
    }
    await ImportAndCheck(sqlite, Path.Combine(folder, "roundtrip.pdb"), 9001);
});
async Task ImportAndCheck(string sqlite, string pure, long expected)
{
    await SqlDatabase.ImportSqliteAsync(sqlite, pure, new() { Storage = new() { Durability = Durability.Buffered } });
    using var read = new SqlDatabase(pure); Check((long)read.Query("SELECT COUNT(*) FROM t").Rows[0][0]! == expected, "import count");
    Check((long)read.Query("SELECT k FROM t WHERE id=100").Rows[0][0]! == 1000, "SQLite update lost");
}
await Test("native SQLite UTF16, integer serial types and blobs import; unsupported schema is atomic", async () =>
{
    string sqlite = Path.Combine(folder, "native.sqlite"), pure = Path.Combine(folder, "native.pdb");
    using (var db = new Sqlite(sqlite, Durability.Buffered))
    {
        db.Exec("PRAGMA journal_mode=DELETE; PRAGMA encoding='UTF-16le'; CREATE TABLE t(id INTEGER PRIMARY KEY,k INTEGER,name TEXT,b BLOB); INSERT INTO t VALUES(100,1000,'中文😀',X'0102'),(0,-9223372036854775808,'',X''),(1,140737488355327,NULL,NULL),(2,-8388609,'x',NULL)");
    }
    await ImportAndCheck(sqlite, pure, 4);
    using (var read = new SqlDatabase(pure)) Check((long)read.Query("SELECT k FROM t WHERE id=0").Rows[0][0]! == long.MinValue, "signed serial");
    using (var db = new Sqlite(sqlite, Durability.Buffered)) db.Exec("CREATE VIEW v AS SELECT * FROM t");
    string refused = Path.Combine(folder, "refused.pdb"); await Reject<NotSupportedException>(() => SqlDatabase.ImportSqliteAsync(sqlite, refused)); Check(!File.Exists(refused), "partial import published");
    // Windows sharing flags detect an open native handle. POSIX flock and native
    // SQLite fcntl locks do not interoperate; offline import requires a closed source.
    // Linux cross-process live locking is tested by PureDb.CompatibilityTests.
    if (OperatingSystem.IsWindows())
        using (var source = new Sqlite(sqlite, Durability.Buffered))
            await Reject<IOException>(() => SqlDatabase.ImportSqliteAsync(sqlite, refused));
});
await Test("paged primary hot cache publishes immutable entries across concurrent readers",async()=>
{
    string path=Path.Combine(folder,"hot-pages.pdb");
    long[] ids=Enumerable.Range(0,4096).Select(i=>(long)i).Concat(new[]{long.MinValue,-1L,1L<<50,long.MaxValue}).ToArray();
    using(var db=new Database(path,new(){Durability=Durability.Buffered}))
        await db.WriteAsync(ids.Select(id=>Mutation.Put(id,1,BitConverter.GetBytes(id))).ToArray());
    using(var db=new Database(path,new(){CacheBytes=2*1024*1024,Durability=Durability.Buffered}))
    {
        await Task.WhenAll(Enumerable.Range(0,8).Select(worker=>Task.Run(()=>
        {
            for(int i=0;i<ids.Length;i++){long id=ids[(i*17+worker)%ids.Length];Check(BitConverter.ToInt64(db.Get(id)!.Value)==id,"torn cached entry");}
        })));
        await db.PutAsync(10,2,BitConverter.GetBytes(999L));await db.DeleteAsync(20);
        Check(BitConverter.ToInt64(db.Get(10)!.Value)==999&&db.Get(20) is null,"hot cache hid mutations");
        await db.CompactAsync();Check(BitConverter.ToInt64(db.Get(10)!.Value)==999&&db.Get(20) is null,"hot cache survived generation change");
    }
    using(var db=new Database(path)){Check(db.Count==ids.Length-1&&db.Get(20) is null,"hot cache reopen");db.VerifyIntegrity();}
});
await Test("primary covering projection validates index while payload reads retain CRC checks",async()=>
{
    string path=Path.Combine(folder,"covering.pdb");
    using(var db=new SqlDatabase(path,new(){Storage=new(){Durability=Durability.Buffered}}))
        await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,b BLOB);INSERT INTO t VALUES(1,X'010203')");
    DateTime stamp=File.GetLastWriteTimeUtc(path);var bytes=File.ReadAllBytes(path);bytes[^1]^=1;
    File.WriteAllBytes(path,bytes);File.SetLastWriteTimeUtc(path,stamp);
    using(var db=new SqlDatabase(path))
    {
        var result=db.Query("SELECT id,id AS again FROM t WHERE id=1");
        Check(result.Plan.Contains("covering")&&(long)result.Rows[0][0]! ==1&&(long)result.Rows[0][1]! ==1,"covering rowid");
        Check(db.Query("SELECT id FROM t WHERE id=99").Rows.Count==0,"covering absent row");
        await Reject<InvalidDataException>(()=>{db.Query("SELECT b FROM t WHERE id=1");return Task.CompletedTask;});
    }
    await Reject<InvalidDataException>(()=>{using var db=new SqlDatabase(path,new(){Storage=new(){VerifyOnOpen=true}});return Task.CompletedTask;});
});
await Test("limited persistent ranges retain exact bounds across duplicates, skew and offsets", async () =>
{
    string path = Path.Combine(folder, "limited-ranges.pdb");
    long[] keys = Enumerable.Range(0, 4096).Select(i => i switch { 0 => long.MinValue, 4095 => long.MaxValue, _ => i < 3000 ? 7L : i % 99 - 50L }).ToArray();
    using (var db = new SqlDatabase(path, new() { Storage = new() { Durability = Durability.Buffered } }))
    {
        await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,k INTEGER NOT NULL); CREATE INDEX ix ON t(k)");
        await db.ExecuteTransactionAsync(keys.Select((key, id) => new SqlCommand("INSERT INTO t VALUES(@id,@k)", new Dictionary<string, object?> { ["id"] = (long)id, ["k"] = key })).ToArray());
    }
    var filters = new (string Sql, long Value, Func<long, bool> Test)[]
    {
        ("k=@v",7,k=>k==7), ("k>@v",7,k=>k>7), ("k>=@v",7,k=>k>=7), ("k<@v",7,k=>k<7), ("k<=@v",7,k=>k<=7),
        ("k BETWEEN @v AND 20",-10,k=>k>=-10&&k<=20), ("k BETWEEN @v AND @negative",20,_=>false),
        ("k>=@v",long.MinValue,_=>true), ("k>@v",long.MaxValue,_=>false), ("k<=@v",long.MinValue,k=>k==long.MinValue),
        ("k<0.5",0,k=>k<1), ("k>=0.5",0,k=>k>=1)
    };
    foreach (var filter in filters)
    foreach (bool desc in new[] { false, true })
    foreach (var page in new[] { (Skip: 0, Take: 20), (Skip: 9, Take: 1), (Skip: 4094, Take: 20), (Skip: int.MaxValue, Take: 20) })
    {
        using var db = new SqlDatabase(path);
        var expected = keys.Select((key, id) => (key, id)).Where(r => filter.Test(r.key));
        expected = desc ? expected.OrderByDescending(r => r.key).ThenByDescending(r => r.id) : expected.OrderBy(r => r.key).ThenBy(r => r.id);
        string order = desc ? "DESC" : "ASC";
        var actual = db.Query($"SELECT id,k FROM t WHERE {filter.Sql} ORDER BY k {order},id {order} LIMIT {page.Take} OFFSET {page.Skip}", new Dictionary<string, object?> { ["v"] = filter.Value, ["negative"] = -10L });
        Check(actual.Plan.StartsWith("PersistentIndex"), "persistent range path not exercised: " + filter.Sql);
        Check(actual.Rows.Select(r => ((long)r[0]!, (long)r[1]!)).SequenceEqual(expected.Skip(page.Skip).Take(page.Take).Select(r => ((long)r.id, r.key))), "limited boundary mismatch: " + filter.Sql);
    }
});
await Test("small budget page eviction preserves concurrent values, tail growth and compaction", async () =>
{
    string path = Path.Combine(folder, "cache-eviction.pdb");
    byte[] Payload(int id) => Enumerable.Repeat((byte)(id % 251), 5000 + id % 31).ToArray();
    using (var db = new Database(path, new() { Durability = Durability.Buffered }))
        await db.WriteAsync(Enumerable.Range(0, 1024).Select(i => Mutation.Put(i, 1, Payload(i))).ToArray());
    using (var db = new Database(path, new() { Durability = Durability.Buffered, CacheBytes = 64 * 1024 }))
    {
        void Read(int id) => Check(db.Get(id)!.Value.SequenceEqual(Payload(id)), "evicted page changed value");
        for (int i = 0; i < 1024; i++) Read(i);
        var readers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            var random = new Random(800 + worker);
            for (int i = 0; i < 2500; i++) Read(random.Next(i % 5 == 0 ? 1024 : 16));
        })).ToArray();
        for (int i = 1024; i < 1088; i++) { await db.PutAsync(i, 1, Payload(i)); Read(i); }
        await Task.WhenAll(readers);
        await db.CompactAsync(); for (int i = 0; i < 1088; i++) Read(i); db.VerifyIntegrity();
    }
    using var reopened = new Database(path, new() { CacheBytes = 4096 });
    for (int i = 1087; i >= 0; i--) Check(reopened.Get(i)!.Value.SequenceEqual(Payload(i)), "single-page budget reopen");
});
Console.WriteLine($"{passed} storage/exchange groups passed: {folder}");
