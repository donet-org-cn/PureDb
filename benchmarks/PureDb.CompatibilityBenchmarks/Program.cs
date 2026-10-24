using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PureDb;
using PureDb.Benchmarks;
using Compatibility = PureDb.SqliteCompatibility.SqliteCompatibility;

if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SQLITE_LIBRARY") is null) Environment.SetEnvironmentVariable("SQLITE_LIBRARY",Path.GetFullPath("artifacts/sqlite/3.54.1/sqlite3.dll"));
const int Rows=2000;
if(args is ["create",var data])
{
    Directory.CreateDirectory(data);var payload=new byte[256];new Random(42).NextBytes(payload);
    using(var db=new Sqlite(Path.Combine(data,"source.sqlite"),Durability.Full,configure:false))
    {
        db.Exec("PRAGMA page_size=4096;PRAGMA journal_mode=DELETE;PRAGMA synchronous=FULL;CREATE TABLE t(id INTEGER PRIMARY KEY,k INTEGER,v BLOB);CREATE INDEX ik ON t(k);BEGIN");
        using var insert=db.Prepare("INSERT INTO t VALUES(?1,?2,?3)");for(int i=0;i<Rows;i++){insert.Bind(1,i);insert.Bind(2,i%100);insert.Bind(3,payload);insert.Step();insert.Reset();}db.Exec("COMMIT");
    }
    await using(var db=new SqlDatabase(Path.Combine(data,"source.pdb"),new(){Storage=new(){Durability=Durability.Buffered}}))
    {
        await db.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,k INTEGER,v BLOB);CREATE INDEX ik ON t(k)");
        await db.ExecuteTransactionAsync(Enumerable.Range(0,Rows).Select(i=>new SqlCommand("INSERT INTO t VALUES(@id,@k,@v)",new Dictionary<string,object?>{["id"]=(long)i,["k"]=(long)i%100,["v"]=payload})).ToArray());
    }
    Console.WriteLine("Created equal 2000-row, 256-byte BLOB fixtures.");return;
}
string engine=args[0],scenario=args[1],dataset=Path.GetFullPath(args[2]),output=Path.GetFullPath(args[3]);int repeat=int.Parse(args[4]);bool native=engine=="SQLite",compat=engine is "Compatibility" or "Incremental";
string work=Path.Combine(Path.GetDirectoryName(output)!,"data",engine+"-"+scenario+"-"+repeat+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
string source=Path.Combine(dataset,native||compat?"source.sqlite":"source.pdb"),path=Path.Combine(work,Path.GetFileName(source));
foreach(string file in Directory.EnumerateFiles(dataset,Path.GetFileName(source)+"*"))File.Copy(file,Path.Combine(work,Path.GetFileName(file)));
SqlDatabase? managed=null;Sqlite? sqlite=null;
void Open(){if(native){sqlite=new(path,Durability.Full,configure:false);sqlite.Exec("PRAGMA journal_mode=DELETE;PRAGMA synchronous=FULL;PRAGMA cache_size=-8192;PRAGMA locking_mode=EXCLUSIVE");}else managed=compat?Compatibility.Open(path,new(){LinuxExclusiveProcess=OperatingSystem.IsLinux(),EnableIncrementalUpdates=engine=="Incremental"}):new(path,new(){Storage=new(){CacheBytes=8*1024*1024}});}
void Close(){sqlite?.Dispose();managed?.Dispose();sqlite=null;managed=null;}
long Footprint()=>Directory.EnumerateFiles(work).Sum(f=>new FileInfo(f).Length);
var parameters=new Dictionary<string,object?>{["id"]=123L};
string sql=scenario switch{"point"=>"SELECT id,k,v FROM t WHERE id=@id","range20"=>"SELECT id,k,v FROM t WHERE k BETWEEN 10 AND 20 ORDER BY k,id LIMIT 20","count"=>"SELECT COUNT(*) FROM t",_=>"SELECT id,k,v FROM t WHERE id=@id"};
Sqlite.Statement? statement=null;
void Query(int i)
{
    if(native){if(scenario=="point")statement!.Bind(1,i%Rows);while(statement!.Step())statement.Values();statement.Reset();}
    else{if(scenario=="point")parameters["id"]=(long)(i%Rows);var result=managed!.Query(sql,parameters);if(result.Rows.Count!=(scenario=="range20"?20:1))throw new Exception("Row count");}
}
bool query=scenario is "point" or "range20" or "count";
if(scenario!="reopen"){Open();if(query){statement=sqlite?.Prepare(sql.Replace("@id","?1"));for(int i=0;i<64;i++)Query(i);}}
GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();using var process=Process.GetCurrentProcess();long allocation=GC.GetTotalAllocatedBytes(true);double cpu=process.TotalProcessorTime.TotalMilliseconds;var io=Io.Read();long footprint=Footprint();int operations=0;var timer=Stopwatch.StartNew();
if(query){do{Query(operations++);}while(timer.ElapsedMilliseconds<700);}
else if(scenario=="reopen"){for(int i=0;i<31;i++){Open();if(native){using var q=sqlite!.Prepare("SELECT id,k,v FROM t WHERE id=123");if(!q.Step())throw new Exception("missing");q.Values();}else if(managed!.Query("SELECT id,k,v FROM t WHERE id=123").Rows.Count!=1)throw new Exception("missing");Close();operations++;}}
else if(scenario=="single_update")
{
    using var update=sqlite?.Prepare("UPDATE t SET k=k+1 WHERE id=?1");
    for(int i=0;i<10;i++){if(native){update!.Bind(1,i);update.Step();update.Reset();}else await managed!.ExecuteAsync("UPDATE t SET k=k+1 WHERE id=@id",new Dictionary<string,object?>{["id"]=(long)i});operations++;}
}
else if(scenario=="batch1000")
{
    var value=new byte[256];
    if(native){sqlite!.Exec("BEGIN IMMEDIATE");using var insert=sqlite.Prepare("INSERT INTO t VALUES(?1,?2,?3)");for(int i=Rows;i<Rows+1000;i++){insert.Bind(1,i);insert.Bind(2,i%100);insert.Bind(3,value);insert.Step();insert.Reset();}sqlite.Exec("COMMIT");}
    else await managed!.ExecuteTransactionAsync(Enumerable.Range(Rows,1000).Select(i=>new SqlCommand("INSERT INTO t VALUES(@id,@k,@v)",new Dictionary<string,object?>{["id"]=(long)i,["k"]=(long)i%100,["v"]=value})).ToArray());operations=1000;
}
else if(scenario is "fixed_update" or "batch_fixed_update")
{
    var value=Enumerable.Repeat((byte)0x5a,256).ToArray();int count=scenario=="fixed_update"?10:1000;
    if(native)
    {
        using var update=sqlite!.Prepare("UPDATE t SET v=?1 WHERE id=?2");
        if(count==1000)sqlite.Exec("BEGIN IMMEDIATE");
        for(int i=0;i<count;i++){update.Bind(1,value);update.Bind(2,i);update.Step();update.Reset();}
        if(count==1000)sqlite.Exec("COMMIT");
    }
    else if(count==1000)await managed!.ExecuteTransactionAsync(Enumerable.Range(0,count).Select(i=>new SqlCommand("UPDATE t SET v=@v WHERE id=@id",new Dictionary<string,object?>{["v"]=value,["id"]=(long)i})).ToArray());
    else for(int i=0;i<count;i++)await managed!.ExecuteAsync("UPDATE t SET v=@v WHERE id=@id",new Dictionary<string,object?>{["v"]=value,["id"]=(long)i});
    operations=count;
}
else throw new ArgumentException(scenario);
timer.Stop();double cpuMs=process.TotalProcessorTime.TotalMilliseconds-cpu;var delta=Io.Read()-io;process.Refresh();
var resultRecord=new{Engine=engine,Scenario=scenario,Repeat=repeat,Rows,Operations=operations,Milliseconds=timer.Elapsed.TotalMilliseconds,MicrosecondsPerOperation=timer.Elapsed.TotalMicroseconds/operations,CpuMilliseconds=cpuMs,AllocatedBytes=GC.GetTotalAllocatedBytes(true)-allocation,process.PrivateMemorySize64,process.WorkingSet64,process.PeakWorkingSet64,IO=delta,StorageBefore=footprint,StorageAfter=Footprint(),SQLite=Sqlite.Version,Runtime=Environment.Version.ToString(),CoreSHA256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Database).Assembly.Location))),CompatibilitySHA256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Compatibility).Assembly.Location))),Date=DateTimeOffset.Now};
statement?.Dispose();Close();
File.WriteAllText(output,JsonSerializer.Serialize(resultRecord,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(true));Console.WriteLine($"{engine}/{scenario}: {resultRecord.MicrosecondsPerOperation:F3} us/op, {resultRecord.PrivateMemorySize64/1048576d:F1} MiB");
internal readonly record struct Io(long ReadBytes,long WriteBytes)
{
    [StructLayout(LayoutKind.Sequential)] private struct Counters{public ulong Reads,Writes,Others,ReadBytes,WriteBytes,OtherBytes;}
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessIoCounters(nint process,out Counters counters);
    public static Io Read(){if(OperatingSystem.IsLinux() && File.Exists("/proc/self/io")){var c=File.ReadAllLines("/proc/self/io").Select(l=>l.Split(':')).ToDictionary(p=>p[0],p=>long.Parse(p[1].Trim()));return new(c["rchar"],c["wchar"]);}if(!OperatingSystem.IsWindows())return new(-1,-1);using var p=Process.GetCurrentProcess();if(!GetProcessIoCounters(p.Handle,out var counters))throw new System.ComponentModel.Win32Exception();return new((long)counters.ReadBytes,(long)counters.WriteBytes);}
    public static Io operator-(Io a,Io b)=>new(a.ReadBytes<0||b.ReadBytes<0?-1:a.ReadBytes-b.ReadBytes,a.WriteBytes<0||b.WriteBytes<0?-1:a.WriteBytes-b.WriteBytes);
}
