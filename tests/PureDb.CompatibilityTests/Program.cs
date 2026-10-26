using System.Buffers.Binary;
using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using PureDb;
using PureDb.Benchmarks;
using PureDb.SqliteCompatibility;
using Compatibility = PureDb.SqliteCompatibility.SqliteCompatibility;

if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SQLITE_LIBRARY") is null) Environment.SetEnvironmentVariable("SQLITE_LIBRARY", Path.GetFullPath("artifacts/sqlite/3.54.1/sqlite3.dll"));
SqlDatabase Open(string path, SqliteCompatibilityOptions? options = null) => Compatibility.Open(path, new() { PageCacheBytes = options?.PageCacheBytes ?? 8*1024*1024, SqlCacheBytes = options?.SqlCacheBytes ?? 64*1024*1024, LockTimeout = options?.LockTimeout ?? TimeSpan.FromSeconds(5), LinuxExclusiveProcess = OperatingSystem.IsLinux(), EnableIncrementalUpdates = options?.EnableIncrementalUpdates ?? false });
Process Worker(params string[] values)
{
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add(typeof(Program).Assembly.Location); foreach (string value in values) start.ArgumentList.Add(value); return Process.Start(start)!;
}
if (args is ["--native-lock-probe", var lockPath])
{
    using var native = new Sqlite(lockPath, Durability.Full, configure: false); native.Exec("PRAGMA busy_timeout=0");
    try { native.Exec("SELECT * FROM t"); Console.WriteLine("UNLOCKED"); } catch (IOException) { Console.WriteLine("BUSY"); } return;
}
if (args is ["--hold-native-reader", var readerPath])
{
    using var native = new Sqlite(readerPath, Durability.Full, configure: false); native.Exec("BEGIN; SELECT * FROM t"); Console.WriteLine("READY"); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite); return;
}
if (args is ["--crash", var crashPath, var crashStage])
{
    using var database = Open(crashPath);
    SqliteStorage.CommitStage = stage => { if (stage == crashStage) { Console.WriteLine("READY"); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite); } };
    await database.ExecuteAsync("UPDATE t SET k=999 WHERE id=1"); return;
}
if (args is ["--crash-incremental", var incrementalPath, var incrementalStage, var incrementalKind])
{
    using var database = Open(incrementalPath, new() { EnableIncrementalUpdates = true });
    SqliteStorage.CommitStage = stage => { if (stage == incrementalStage) { Console.WriteLine("READY"); Console.Out.Flush(); Thread.Sleep(Timeout.Infinite); } };
    if(incrementalKind=="resize") await database.ExecuteAsync("UPDATE t SET payload=X'5A' WHERE id=2");
    else if(incrementalKind=="index") await database.ExecuteAsync("UPDATE t SET k=11 WHERE id=1");
    else await database.ExecuteAsync("UPDATE t SET payload=@blob WHERE id=1", new Dictionary<string,object?> { ["blob"]=Enumerable.Repeat((byte)0x5a,20000).ToArray() }); return;
}
var folder = Path.GetFullPath("artifacts/compatibility-tests/" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
void Check(bool value, string message) { if (!value) throw new Exception(message); }
async Task Reject<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
object? Scalar(Sqlite db, string query) { using var s = db.Prepare(query); Check(s.Step(), "native missing row"); return s.Values()[0]; }
string NewPath(string name) => Path.Combine(folder, name + ".sqlite");
void Seed(string path)
{
    using var db = new Sqlite(path, Durability.Full, configure: false);
    db.Exec("PRAGMA page_size=4096; PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; CREATE TABLE t(id INTEGER PRIMARY KEY,k INTEGER NOT NULL,name TEXT UNIQUE,body JSON,payload BLOB); CREATE INDEX ik ON t(k); INSERT INTO t VALUES(1,10,'中文😀','{\"x\":7}',zeroblob(20000)),(2,20,'two','{\"x\":8}',X'010203'); PRAGMA user_version=42; PRAGMA application_id=12345;");
}
int passed = 0;
async Task Test(string name, Func<Task> action) { if(args.Contains("--incremental-page-check")&&!name.StartsWith("incremental dirty",StringComparison.Ordinal))return; await action(); passed++; Console.WriteLine("PASS " + name); }
await Test("direct SQLite page reads, original-handle writes, native integrity and reopen", async () =>
{
    string path = NewPath("roundtrip"); Seed(path);
    using (var observer = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
    {
        await using var db = Open(path);
        Check((long)db.Query("SELECT k FROM t WHERE id=1").Rows[0][0]! == 10, "point");
        Check((string)db.Query("SELECT name FROM t WHERE id=1").Rows[0][0]! == "中文😀", "UTF8");
        Check(((byte[])db.Query("SELECT payload FROM t WHERE id=1").Rows[0][0]!).Length == 20000, "overflow chain");
        Check((long)db.Query("SELECT json_extract(body,'$.x') FROM t WHERE id=1").Rows[0][0]! == 7, "JSON");
        await db.ExecuteAsync("UPDATE t SET k=k+1 WHERE id=1; DELETE FROM t WHERE id=2; INSERT INTO t VALUES(3,30,'three','{\"x\":9}',X'FF')");
        Check(db.Query("SELECT id FROM t WHERE k BETWEEN 0 AND 50 ORDER BY k").Rows.Count == 2, "range after commit");
        await Reject<SqlException>(async () => await db.ExecuteAsync("INSERT INTO t VALUES(4,0,'three',NULL,NULL)"));
        await db.CheckpointAsync();
    }
    using (var native = new Sqlite(path, Durability.Full, configure: false))
    {
        Check(Equals(Scalar(native, "PRAGMA integrity_check"), "ok"), "native integrity");
        Check(Equals(Scalar(native, "SELECT k FROM t WHERE id=1"), 11L), "native value");
        Check(Equals(Scalar(native, "PRAGMA user_version"), 42L) && Equals(Scalar(native, "PRAGMA application_id"), 12345L), "header metadata preserved");
        native.Exec("INSERT INTO t VALUES(5,50,'native','{\"x\":10}',NULL)");
    }
    using var reopened = Open(path); Check(reopened.Query("SELECT * FROM t").Rows.Count == 3, "native to managed edits");
    Check(!File.Exists(path + ".pidx") && !Directory.EnumerateFiles(folder, "roundtrip.sqlite.sidx*").Any(), "no PureDb indexes for native files");
});
await Test("new SQLite file, batch DDL/DML, JOIN, expression indexes and schema evolution", async () =>
{
    string path = NewPath("new");
    await using (var db = Open(path))
    {
        await db.ExecuteAsync("CREATE TABLE a(id INTEGER PRIMARY KEY,k INTEGER,body JSON); CREATE TABLE b(id INTEGER PRIMARY KEY,aid INTEGER); INSERT INTO a VALUES(1,10,'{\"x\":3}'),(2,20,'{\"x\":4}'); INSERT INTO b VALUES(1,1),(2,2); CREATE INDEX jx ON a(json_extract(body,'$.x'))");
        Check(db.Query("SELECT a.id,b.id FROM a JOIN b ON a.id=b.aid ORDER BY a.id").Rows.Count == 2, "JOIN");
        await db.ExecuteTransactionAsync(Enumerable.Range(3,100).Select(i => new SqlCommand("INSERT INTO a VALUES(@id,@k,'{}')", new Dictionary<string,object?> { ["id"]=(long)i,["k"]=(long)i })).ToArray());
        await db.ExecuteAsync("DROP TABLE b; CREATE TABLE c(id INTEGER PRIMARY KEY,v TEXT); INSERT INTO c VALUES(1,'c')");
        Check(db.Query("SELECT * FROM c").Rows.Count == 1, "drop/create");
    }
    using var native = new Sqlite(path, Durability.Full, configure: false); Check(Equals(Scalar(native,"PRAGMA integrity_check"),"ok"),"native index integrity"); Check(Equals(Scalar(native,"SELECT COUNT(*) FROM a"),102L),"native batch count");
});
await Test("ADO.NET factory, parameters, Serializable commit, rollback and DataTable", async () =>
{
    string path = NewPath("ado"); using var c = new SqliteConnection("Data Source=" + path) { LinuxExclusiveProcess = OperatingSystem.IsLinux() }; c.Open();
    using var command = c.CreateCommand(); command.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY,k INTEGER)"; command.ExecuteNonQuery();
    using (var transaction = c.BeginTransaction()) { command.Transaction = transaction; command.CommandText="INSERT INTO t VALUES(@id,@k)"; command.Parameters.AddWithValue("id",1L); command.Parameters.AddWithValue("k",4L); command.ExecuteNonQuery(); transaction.Commit(); }
    command.Transaction = null; command.Parameters.Clear();
    using (var transaction = c.BeginTransaction()) { command.Transaction=transaction; command.CommandText="UPDATE t SET k=99";command.ExecuteNonQuery();transaction.Rollback(); }
    command.Transaction=null;command.CommandText="SELECT * FROM t";using var reader=command.ExecuteReader();var table=new DataTable();table.Load(reader);Check((long)table.Rows[0][1]==4,"ADO rollback");
    Check(SqliteProviderFactory.Instance.CreateConnection() is SqliteConnection,"provider factory"); await Task.CompletedTask;
});
await Test("exclusive byte-range locks conflict with native and managed connections", async () =>
{
    string path = NewPath("locks"); Seed(path); using var managed = Open(path);
    await Reject<IOException>(() => Task.Run(() => { using var second=Open(path,new(){LockTimeout=TimeSpan.Zero}); }));
    if (OperatingSystem.IsWindows()) { using var native = new Sqlite(path,Durability.Full,configure:false); native.Exec("PRAGMA busy_timeout=0"); await Reject<IOException>(()=>Task.Run(()=>native.Exec("SELECT * FROM t"))); }
    using (var probe=Worker("--native-lock-probe",path)) { Check(await probe.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20))=="BUSY","native process bypassed managed lock"); await probe.WaitForExitAsync(); Check(probe.ExitCode==0,"native lock worker"); }
    managed.Dispose();
    string reverse=NewPath("native-lock");Seed(reverse);
    using var holder=Worker("--hold-native-reader",reverse);
    try { Check(await holder.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20))=="READY","native reader worker"); await Reject<IOException>(()=>Task.Run(()=>{using var db=Open(reverse,new(){LockTimeout=TimeSpan.Zero});})); }
    finally { if(!holder.HasExited)holder.Kill(entireProcessTree:true);await holder.WaitForExitAsync(); }
});
await Test("unsupported WAL, aliases, native JSONB and dynamic values fail safely", async () =>
{
    foreach(var (name,sql) in new[]{("wal","PRAGMA journal_mode=WAL"),("alias","CREATE TABLE bad(id INT PRIMARY KEY)"),("jsonb","CREATE TABLE bad(id INTEGER PRIMARY KEY,b JSONB)")})
    {
        string path=NewPath(name);Seed(path);using(var native=new Sqlite(path,Durability.Full,configure:false))native.Exec(sql);
        byte[] hash=SHA256.HashData(File.ReadAllBytes(path));
        await Reject<NotSupportedException>(()=>Task.Run(()=>{using var db=Open(path);}));Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))),"unsupported open changed main");
    }
    string dynamicPath=NewPath("dynamic");Seed(dynamicPath);using(var native=new Sqlite(dynamicPath,Durability.Full,configure:false))native.Exec("UPDATE t SET k='text' WHERE id=2");
    byte[] before=SHA256.HashData(File.ReadAllBytes(dynamicPath));using(var db=Open(dynamicPath))await Reject<NotSupportedException>(async()=>await db.ExecuteAsync("UPDATE t SET k=12 WHERE id=1"));
    Check(before.SequenceEqual(SHA256.HashData(File.ReadAllBytes(dynamicPath))),"failed rewrite changed main");
});
await Test("process kill at five commit boundaries, managed and original SQLite recovery", async () =>
{
    foreach(string stage in new[]{"before-journal-header","after-journal-sync","after-first-page","after-database-sync","after-commit"})
    foreach(bool nativeRecovery in new[]{false,true})
    {
        string path=NewPath(stage+nativeRecovery);Seed(path);
        var start=new ProcessStartInfo("dotnet"){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true};
        foreach(string a in new[]{typeof(Program).Assembly.Location,"--crash",path,stage})start.ArgumentList.Add(a);
        using var process=Process.Start(start)!;string? line=await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Check(line=="READY","crash worker failed: "+line);process.Kill(entireProcessTree:true);await process.WaitForExitAsync();
        long expected=stage=="after-commit"?999:10;
        if(nativeRecovery){using var native=new Sqlite(path,Durability.Full,configure:false);Check(Equals(Scalar(native,"SELECT k FROM t WHERE id=1"),expected),"native journal rollback "+stage);Check(Equals(Scalar(native,"PRAGMA integrity_check"),"ok"),"native recovered integrity");}
        else{using var db=Open(path);Check((long)db.Query("SELECT k FROM t WHERE id=1").Rows[0][0]! == expected,"managed recovery "+stage);}
    }
    string failed=NewPath("exception-rollback");Seed(failed);
    await using var afterError=Open(failed);
    SqliteStorage.CommitStage=stage=>{if(stage=="after-first-page")throw new IOException("Injected storage error");};
    try{await Reject<IOException>(async()=>await afterError.ExecuteAsync("UPDATE t SET k=88 WHERE id=1"));}
    finally{SqliteStorage.CommitStage=null;}
    Check((long)afterError.Query("SELECT k FROM t WHERE id=1").Rows[0][0]! == 10,"exception did not roll back");
    await afterError.ExecuteAsync("UPDATE t SET k=21 WHERE id=1");Check((long)afterError.Query("SELECT k FROM t WHERE id=1").Rows[0][0]! == 21,"writer unusable after rollback");
});
await Test("malformed hot journal is rejected before any restoration", async () =>
{
    string path=NewPath("corrupt-journal");Seed(path);byte[] before=File.ReadAllBytes(path);var header=new byte[4096+4104];new byte[]{0xd9,0xd5,5,0xf9,0x20,0xa1,0x63,0xd7}.CopyTo(header,0);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8),1);BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16),2);BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20),4096);BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24),4096);BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4096),1);header[4096+4+3896]=1;File.WriteAllBytes(path+"-journal",header);
    await Reject<InvalidDataException>(()=>Task.Run(()=>{using var db=Open(path);}));Check(before.SequenceEqual(File.ReadAllBytes(path)),"corrupt rollback modified database");
});
await Test("record serial widths, UTF8, large blobs and repeated rewrites match native SQLite", async () =>
{
    string path = NewPath("record-boundaries");
    long[] integers = [long.MinValue, -140737488355329, -140737488355328, -2147483649, -8388609, -32769, -129, -128, -1, 0, 1, 127, 128, 32767, 32768, 8388607, 8388608, int.MaxValue, 2147483648, 140737488355327, 140737488355328, long.MaxValue];
    var blob = new byte[70000]; new Random(18).NextBytes(blob);
    await using (var db = Open(path))
    {
        await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,k INTEGER,r REAL,s TEXT,b BLOB);CREATE INDEX ik ON t(k)");
        await db.ExecuteTransactionAsync(integers.Select((n, i) => new SqlCommand("INSERT INTO t VALUES(@id,@k,@r,@s,@b)", new Dictionary<string, object?> { ["id"]=(long)i,["k"]=n,["r"]=i*0.125,["s"]=new string('\u4e2d',i*31)+"\U0001F600",["b"]=i%2==0?blob:Array.Empty<byte>() })).ToArray());
        for (int i=0;i<3;i++) await db.ExecuteAsync("UPDATE t SET r=r+1 WHERE id=0");
    }
    using var native = new Sqlite(path,Durability.Full,configure:false);
    Check(Equals(Scalar(native,"PRAGMA integrity_check"),"ok"),"boundary index integrity");
    using var query = native.Prepare("SELECT id,k,r,s,b FROM t ORDER BY id"); int row=0;
    while(query.Step()) { var v=query.Values(); Check(Equals(v[1],integers[row]),"serial width "+row); Check(Equals(v[2],row==0?3d:row*0.125),"real"); Check(Equals(v[3],new string('\u4e2d',row*31)+"\U0001F600"),"UTF8"); Check(((byte[])v[4]!).AsSpan().SequenceEqual(row%2==0?blob:Array.Empty<byte>()),"overflow blob");row++; }
    Check(row==integers.Length,"boundary rows");
});
await Test("incremental dirty pages, same-leaf batches, indexes, overflow and safe fallback", async () =>
{
    string path=NewPath("incremental");
    using(var native=new Sqlite(path,Durability.Full,configure:false))
    {
        native.Exec("CREATE TABLE t(id INTEGER PRIMARY KEY,k INTEGER,name TEXT UNIQUE,body JSON,payload BLOB);CREATE INDEX ik ON t(k);CREATE INDEX ij ON t(json_extract(body,'$.x'));BEGIN");
        using var insert=native.Prepare("INSERT INTO t VALUES(?1,?2,'n'||?1,'{\"x\":1}',?3)");
        for(int i=0;i<2000;i++){insert.Bind(1,i);insert.Bind(2,i);insert.Bind(3,new byte[i==0?20000:256]);insert.Step();insert.Reset();}native.Exec("COMMIT");
    }
    long pages=new FileInfo(path).Length/4096;int journalCount=0;bool sparse=false,rebuilt=false;
    SqliteStorage.CommitStage=stage=>{if(stage=="before-image-commit")rebuilt=true;if(stage=="before-incremental-commit")rebuilt=false;if(stage=="after-journal-sync"){using var journal=File.OpenRead(path+"-journal");Span<byte> header=stackalloc byte[28];journal.ReadExactly(header);journalCount=(int)BinaryPrimitives.ReadUInt32BigEndian(header[8..]);sparse=journalCount<pages;}};
    var expected=new Dictionary<int,byte[]>();var rng=new Random(884);
    try
    {
        await using var db=Open(path,new(){EnableIncrementalUpdates=true});
        for(int batch=0;batch<6;batch++)
        {
            var commands=new List<SqlCommand>();
            for(int n=0;n<20;n++){int id=batch==0?n:rng.Next(2000);var value=new byte[id==0?20000:256];rng.NextBytes(value);expected[id]=value;commands.Add(new("UPDATE t SET payload=@v WHERE id=@id",new Dictionary<string,object?>{["v"]=value,["id"]=(long)id}));}
            await db.ExecuteTransactionAsync(commands);Check(sparse,"same-size batch rebuilt entire image");
        }
        await db.ExecuteAsync("UPDATE t SET k=601 WHERE id=600");Check(sparse,"same-leaf integer index update rebuilt entire image");
        await db.ExecuteAsync("UPDATE t SET k=620 WHERE id=610");Check(sparse,"rightward index entry move rebuilt entire image");
        await db.ExecuteAsync("UPDATE t SET k=615 WHERE id=620");Check(sparse,"leftward index entry move rebuilt entire image");
        await db.ExecuteAsync("UPDATE t SET k=1700 WHERE id=500");Check(rebuilt,"cross-leaf index update must fall back");
        pages=new FileInfo(path).Length/4096;
        await db.ExecuteAsync("UPDATE t SET body='{\"x\":2}' WHERE id=1");Check(rebuilt,"changed expression key must fall back");
        pages=new FileInfo(path).Length/4096;
        foreach(int length in new[]{0,1,57,58,127,128,300})
        {
            var value=new byte[length];rng.NextBytes(value);
            await db.ExecuteAsync("UPDATE t SET payload=@v WHERE id=1",new Dictionary<string,object?>{["v"]=value});
            Check(sparse,"local resize rebuilt entire image");expected[1]=value;
        }
        var resized=new List<SqlCommand>();
        for(int id=2;id<20;id++){var value=new byte[rng.Next(200)];rng.NextBytes(value);expected[id]=value;resized.Add(new("UPDATE t SET payload=@v WHERE id=@id",new Dictionary<string,object?>{["v"]=value,["id"]=(long)id}));}
        await db.ExecuteTransactionAsync(resized);Check(sparse,"same-leaf resize batch rebuilt entire image");
        await db.ExecuteAsync("UPDATE t SET payload=@v WHERE id=1",new Dictionary<string,object?>{["v"]=new byte[4000]});Check(rebuilt,"leaf requiring split must fall back");expected[1]=new byte[4000];
        pages=new FileInfo(path).Length/4096;
        await db.ExecuteAsync("UPDATE t SET payload=@v WHERE id=1",new Dictionary<string,object?>{["v"]=new byte[5000]});Check(rebuilt,"overflow allocation must fall back");expected[1]=new byte[5000];
        pages=new FileInfo(path).Length/4096;
        await db.ExecuteAsync("INSERT INTO t VALUES(3000,3000,'new','{}',NULL);DELETE FROM t WHERE id=1999");Check(rebuilt,"insert/delete must fall back");expected.Remove(1999);
        using var canceled=new CancellationTokenSource();canceled.Cancel();await Reject<OperationCanceledException>(async()=>await db.ExecuteAsync("UPDATE t SET payload=X'00' WHERE id=1",cancellationToken:canceled.Token));
        SqliteStorage.CommitStage=stage=>{if(stage=="after-first-page")throw new IOException("injected incremental failure");};
        await Reject<IOException>(async()=>await db.ExecuteAsync("UPDATE t SET payload=@v WHERE id=0",new Dictionary<string,object?>{["v"]=new byte[20000]}));
        Check(((byte[])db.Query("SELECT payload FROM t WHERE id=0").Rows[0][0]!).AsSpan().SequenceEqual(expected[0]),"incremental rollback");
    }
    finally {SqliteStorage.CommitStage=null;}
    using var verify=new Sqlite(path,Durability.Full,configure:false);Check(Equals(Scalar(verify,"PRAGMA integrity_check"),"ok"),"incremental native integrity");
    using var q=verify.Prepare("SELECT payload FROM t WHERE id=?1");foreach(var (id,value) in expected){q.Bind(1,id);Check(q.Step()&&((byte[])q.Values()[0]!).AsSpan().SequenceEqual(value),"randomized payload mismatch");q.Reset();}
    Check(Equals(Scalar(verify,"SELECT COUNT(*) FROM t WHERE json_extract(body,'$.x')=2"),1L),"expression index fallback");
    Check(Equals(Scalar(verify,"SELECT COUNT(*) FROM t WHERE k=601"),2L),"same-leaf index order");
    Check(Equals(Scalar(verify,"SELECT COUNT(*) FROM t WHERE k=620"),1L)&&Equals(Scalar(verify,"SELECT COUNT(*) FROM t WHERE k=615"),2L),"bidirectional index moves");
    Check(Equals(Scalar(verify,"SELECT COUNT(*) FROM t WHERE k=1700"),2L),"cross-leaf index fallback");
});
await Test("incremental sparse journal: five process-kill stages and both recovery engines", async () =>
{
    foreach(string stage in new[]{"before-journal-header","after-journal-sync","after-first-page","after-database-sync","after-commit"})
    foreach(bool nativeRecovery in new[]{false,true})
    foreach(string kind in new[]{"payload","index","resize"})
    {
        string path=NewPath("incremental-"+stage+nativeRecovery+kind);Seed(path);
        using var worker=Worker("--crash-incremental",path,stage,kind);
        string? line=await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));Check(line=="READY","incremental worker: "+line);worker.Kill(entireProcessTree:true);await worker.WaitForExitAsync();
        if(stage is "after-journal-sync" or "after-first-page" or "after-database-sync")
        {
            byte[] header=File.ReadAllBytes(path+"-journal");Check(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8))<BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16)),"worker did not use sparse journal");
        }
        byte expected=stage=="after-commit"?(byte)0x5a:(byte)0;
        byte[] resizedExpected=stage=="after-commit"?[0x5a]:[1,2,3];
        if(nativeRecovery){using var native=new Sqlite(path,Durability.Full,configure:false);if(kind=="payload")Check(((byte[])Scalar(native,"SELECT payload FROM t WHERE id=1")!).All(b=>b==expected),"native sparse recovery");else if(kind=="resize")Check(((byte[])Scalar(native,"SELECT payload FROM t WHERE id=2")!).AsSpan().SequenceEqual(resizedExpected),"native resize recovery");else Check(Equals(Scalar(native,"SELECT k FROM t WHERE id=1"),stage=="after-commit"?11L:10L),"native index recovery");Check(Equals(Scalar(native,"PRAGMA integrity_check"),"ok"),"native sparse integrity");}
        else{using var db=Open(path,new(){EnableIncrementalUpdates=true});if(kind=="payload")Check(((byte[])db.Query("SELECT payload FROM t WHERE id=1").Rows[0][0]!).All(b=>b==expected),"managed sparse recovery");else if(kind=="resize")Check(((byte[])db.Query("SELECT payload FROM t WHERE id=2").Rows[0][0]!).AsSpan().SequenceEqual(resizedExpected),"managed resize recovery");else Check(Equals(db.Query("SELECT k FROM t WHERE id=1").Rows[0][0],stage=="after-commit"?11L:10L),"managed index recovery");}
        using var integrity=new Sqlite(path,Durability.Full,configure:false);Check(Equals(Scalar(integrity,"PRAGMA integrity_check"),"ok"),"sparse index/overflow integrity");
    }
});
Console.WriteLine($"{passed} managed SQLite compatibility test groups passed; SQLite {Sqlite.Version}. Artifacts: {folder}");
