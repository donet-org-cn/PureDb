using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PureDb;
using PureDb.Benchmarks;

ThreadPool.SetMinThreads(32, 32);
string native = Path.GetFullPath("artifacts/sqlite/3.54.1/sqlite3.dll");
if (File.Exists(native)) Environment.SetEnvironmentVariable("SQLITE_LIBRARY", native);
if (args.Length < 4) throw new ArgumentException("queries|writes|processes engine dataset result.json [repeat]");
string mode=args[0], engine=args[1], dataset=Path.GetFullPath(args[2]), output=Path.GetFullPath(args[3]);
int repeat=args.Length>4?int.Parse(args[4]):1;
bool pure=engine!="SQLite";
var results=new List<Sample>();
string? activePath=null;
long Footprint()=>activePath is null?0:Directory.EnumerateFiles(Path.GetDirectoryName(activePath)!,Path.GetFileName(activePath)+"*").Sum(f=>new FileInfo(f).Length);
var payload=new byte[256]; new Random(42).NextBytes(payload);
string work=Path.Combine(mode=="worker"?Path.GetFullPath("artifacts/data"):Path.GetDirectoryName(output)!,"data",engine+"-"+mode+"-"+repeat+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
void Check(bool ok,string error) { if(!ok) throw new Exception(error); }
async Task Measure(string name,long operations,Func<Task> action,string unit="operations",List<double>? latencies=null)
{
    GC.Collect();GC.WaitForPendingFinalizers();long allocation=GC.GetTotalAllocatedBytes(true);
    using var cpu=Process.GetCurrentProcess();double cpuStart=cpu.TotalProcessorTime.TotalMilliseconds;var ioStart=Io.Read();long diskStart=Footprint();
    var sw=Stopwatch.StartNew();await action();sw.Stop();double cpuMs=cpu.TotalProcessorTime.TotalMilliseconds-cpuStart;var io=Io.Read()-ioStart;
    latencies?.Sort(); double Percent(double p)=>latencies is {Count:>0}?latencies[Math.Min(latencies.Count-1,(int)((latencies.Count-1)*p))]:0;
    using var process=Process.GetCurrentProcess();process.Refresh();
    results.Add(new(engine,mode,name,repeat,operations,unit,sw.Elapsed.TotalMilliseconds,operations/sw.Elapsed.TotalSeconds,Percent(.5),Percent(.95),Percent(.99),latencies?.Count??0,GC.GetTotalAllocatedBytes(true)-allocation,process.WorkingSet64,process.PrivateMemorySize64,process.PeakWorkingSet64,GC.GetTotalMemory(false),cpuMs,cpuMs/sw.Elapsed.TotalMilliseconds*100,cpuMs/sw.Elapsed.TotalMilliseconds*100/Environment.ProcessorCount,io.ReadBytes,io.WriteBytes,io.ReadOps,io.WriteOps,diskStart,Footprint()));
    Console.WriteLine($"{engine} {name}: {operations/sw.Elapsed.TotalSeconds:N0}/{unit}/s {sw.Elapsed.TotalMilliseconds:F1}ms");
}
void ReadBenchmark(string name,Action<int> action)
{
    // Let tiered compilation and repeated-query caches settle before measuring.
    // Every engine uses the same minimum time and operation count.
    var warmup=Stopwatch.StartNew();int warmOperations=0;
    do { action(warmOperations++); } while(warmOperations<64 || warmup.ElapsedMilliseconds<500);
    GC.Collect();GC.WaitForPendingFinalizers();long before=GC.GetTotalAllocatedBytes(true);var latency=new List<double>();
    using var cpu=Process.GetCurrentProcess();double cpuStart=cpu.TotalProcessorTime.TotalMilliseconds;var ioStart=Io.Read();long diskStart=Footprint();
    var sw=Stopwatch.StartNew();int ops=0;
    do { if((ops&63)==0) {long start=Stopwatch.GetTimestamp();action(ops);latency.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);}else action(ops);ops++; }while(sw.ElapsedMilliseconds<750);
    sw.Stop();double cpuMs=cpu.TotalProcessorTime.TotalMilliseconds-cpuStart;var io=Io.Read()-ioStart;latency.Sort();using var process=Process.GetCurrentProcess();process.Refresh();
    double P(double p)=>latency[Math.Min(latency.Count-1,(int)((latency.Count-1)*p))];
    results.Add(new(engine,mode,name,repeat,ops,"queries",sw.Elapsed.TotalMilliseconds,ops/sw.Elapsed.TotalSeconds,P(.5),P(.95),P(.99),latency.Count,GC.GetTotalAllocatedBytes(true)-before,process.WorkingSet64,process.PrivateMemorySize64,process.PeakWorkingSet64,GC.GetTotalMemory(false),cpuMs,cpuMs/sw.Elapsed.TotalMilliseconds*100,cpuMs/sw.Elapsed.TotalMilliseconds*100/Environment.ProcessorCount,io.ReadBytes,io.WriteBytes,io.ReadOps,io.WriteOps,diskStart,Footprint()));
    Console.WriteLine($"{engine} {name}: {ops/sw.Elapsed.TotalSeconds:N0} queries/s p95={P(.95):F2}us");
}
List<object?[]> Read(Sqlite db,string text)
{
    using var s=db.Prepare(text);var rows=new List<object?[]>();while(s.Step())rows.Add(s.Values());return rows;
}
long Scalar(Sqlite db,string text)=>(long)Read(db,text)[0][0]!;
if(mode=="queries")
{
    await using var sql=pure?new SqlDatabase(Path.Combine(dataset,"sql.pdb")):null;
    await using var kv=pure?new Database(Path.Combine(dataset,"core.pdb")):null;
    using var sqlite=pure?null:new Sqlite(Path.Combine(dataset,"reference.sqlite"),Durability.Buffered);
    if(sqlite is not null)sqlite.Exec("PRAGMA cache_size=-65536"); // same configured page-cache budget
    var p=new Dictionary<string,object?> { ["id"]=1L,["lo"]=10L,["hi"]=20L };
    activePath=Path.Combine(dataset,pure?"sql.pdb":"reference.sqlite");
    int count=100000;
    if(pure)for(int i=0;i<count;i++)kv!.Get(i);
    using(var point=sqlite?.Prepare("SELECT id,index_key,value FROM records WHERE id=?1"))
    {
        ReadBenchmark("kv_get",i=>{int id=(int)((uint)(i*7919)%count);if(pure)Check(kv!.Get(id)!.Value.Length==256,"kv");else{point!.Bind(1,id);Check(point.Step(),"sqlite point");Check(((byte[])point.Values()[2]!).Length==256,"value");point.Reset();}});
        ReadBenchmark("kv_miss",i=>{if(pure)Check(kv!.Get(count+i) is null,"miss");else{point!.Bind(1,count+i);Check(!point.Step(),"miss");point.Reset();}});
    }
    foreach(int limit in new[]{20,1000})
    {
        using var s=sqlite?.Prepare($"SELECT id,index_key,value FROM records WHERE index_key>=?1 AND index_key<=?2 ORDER BY index_key,id LIMIT {limit}");
        ReadBenchmark("kv_range_"+limit,i=>{if(pure)Check(kv!.Query(i%990,i%990+10,limit).Count==limit,"range");else{s!.Bind(1,i%990);s.Bind(2,i%990+10);int n=0;while(s.Step()){s.Values();n++;}s.Reset();Check(n==limit,"range");}});
    }
    var queries=new (string Name,string Sql,int Expected)[]{
        ("sql_point","SELECT id,index_key,value FROM records WHERE id=@id",1),
        ("sql_projection","SELECT id FROM records WHERE id=@id",1),
        ("sql_range20","SELECT id,index_key,value FROM records WHERE index_key>=@lo AND index_key<=@hi ORDER BY index_key,id LIMIT 20",20),
        ("sql_range1000","SELECT id,index_key,value FROM records WHERE index_key>=@lo AND index_key<=@hi ORDER BY index_key,id LIMIT 1000",1000),
        ("sql_count","SELECT COUNT(*) FROM records",1),
        ("sql_sum_scan","SELECT SUM(index_key) FROM records",1),
        ("sql_group","SELECT index_key,COUNT(*) FROM records GROUP BY index_key ORDER BY index_key",1000),
        ("sql_join","SELECT u.id,u.name,o.id,o.amount FROM users u JOIN orders o ON u.id=o.user_id WHERE u.id=@id ORDER BY o.id LIMIT 20",10),
        ("sql_left_join","SELECT u.id,o.id FROM users u LEFT JOIN orders o ON u.id=o.user_id WHERE u.id=@id ORDER BY o.id LIMIT 20",10),
        ("json_index","SELECT id,json_extract(body,'$.name') FROM docs WHERE json_extract(body,'$.age')=@id ORDER BY id LIMIT 20",20),
        ("json_scan","SELECT COUNT(*) FROM docs WHERE json_extract(body,'$.age')%2=0",1),
        ("json_binary_conversion","SELECT json_extract(jsonb(body),'$.age') FROM docs WHERE id=@id",1)
    };
    foreach(var q in queries)
    {
        using var s=sqlite?.Prepare(q.Sql);
        ReadBenchmark(q.Name,i=>
        {
            long id=q.Name=="json_index"?i%100:i%10000;p["id"]=id;
            if(pure)Check(sql!.Query(q.Sql,p).Rows.Count==q.Expected,q.Name);
            else
            {
                if(q.Sql.Contains("@id"))s!.Bind(1,id);
                if(q.Sql.Contains("@lo")){s!.Bind(1,10);s.Bind(2,20);}
                int n=0;while(s!.Step()){s.Values();n++;}s.Reset();Check(n==q.Expected,q.Name);
            }
        });
    }
    var readers=pure?[]:Enumerable.Range(0,8).Select(_=>new Sqlite(Path.Combine(dataset,"reference.sqlite"),Durability.Buffered)).ToArray();
    var statements=readers.Select(r=>r.Prepare("SELECT id,index_key,value FROM records WHERE id=?1")).ToArray();
    try
    {
        await Measure("parallel_get_8",400000,()=>Task.WhenAll(Enumerable.Range(0,8).Select(w=>Task.Run(()=>{for(int i=w;i<400000;i+=8){int id=i%count;if(pure)kv!.Get(id);else{var s=statements[w];s.Bind(1,id);Check(s.Step(),"parallel");s.Values();s.Reset();}}}))),"queries");
    }finally{foreach(var s in statements)s.Dispose();foreach(var r in readers)r.Dispose();}
    if(sql is not null) await sql.DisposeAsync();
    // Fresh managed/index cache; OS file cache is deliberately not claimed cold.
    await Measure("reopen_and_first_range",5,()=>{for(int i=0;i<5;i++){if(pure){using var db=new SqlDatabase(Path.Combine(dataset,"sql.pdb"));Check(db.Query(queries[2].Sql,p).Rows.Count==20,"reopen");}else{using var db=new Sqlite(Path.Combine(dataset,"reference.sqlite"),Durability.Buffered);Check(Read(db,"SELECT id,index_key,value FROM records WHERE index_key>=10 AND index_key<=20 ORDER BY index_key,id LIMIT 20").Count==20,"reopen");}}return Task.CompletedTask;},"opens");
    // Cache constrained below dataset size, OS cache remains warm.
    await using(var small=pure?new Database(Path.Combine(work,"cache-small.pdb"),new(){CacheBytes=2*1024*1024}):null)
    {
        // The existing core file is exclusively owned above; copy to an independent path first.
        if(pure)await small!.WriteAsync(Enumerable.Range(0,20000).Select(i=>Mutation.Put(i,i%1000,payload)).ToArray());
        if(sqlite is not null)sqlite.Exec("PRAGMA cache_size=-2048");
        using var get=sqlite?.Prepare("SELECT id,index_key,value FROM records WHERE id=?1");
        ReadBenchmark("cache_limited_2MiB",i=>{int id=(int)((uint)(i*7919)%20000);if(pure)small!.Get(id);else{get!.Bind(1,id);get.Step();get.Values();get.Reset();}});
    }
}
if(mode=="memory")
{
    string scenario=args[5]; string text=scenario switch
    {
        "point"=>"SELECT id,index_key,value FROM records WHERE id=@id",
        "range"=>"SELECT id,index_key,value FROM records WHERE index_key>=@id AND index_key<=@hi ORDER BY index_key,id LIMIT 20",
        _=>"SELECT id,json_extract(body,'$.name') FROM docs WHERE json_extract(body,'$.age')=@id ORDER BY id LIMIT 20"
    };
    await using var db=pure?new SqlDatabase(Path.Combine(dataset,"sql.pdb")):null;
    using var nativeDb=pure?null:new Sqlite(Path.Combine(dataset,"reference.sqlite"),Durability.Buffered);
    nativeDb?.Exec("PRAGMA cache_size=-65536");using var statement=nativeDb?.Prepare(text);
    var parameters=new Dictionary<string,object?>();int iterations=scenario=="point"?100000:1000;
    for(int i=0;i<iterations;i++)
    {
        long id=scenario=="point"?i:scenario=="range"?i%990:i%100;
        if(pure){parameters["id"]=id;parameters["hi"]=id+10;Check(db!.Query(text,parameters).Rows.Count>0,"memory query");}
        else{statement!.Bind(1,id);if(scenario=="range")statement.Bind(2,id+10);while(statement.Step())statement.Values();statement.Reset();}
    }
    GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long managed=GC.GetTotalMemory(true);
    using var process=Process.GetCurrentProcess();process.Refresh();
    var profile=new{Engine=engine,Scenario=scenario,Repeat=repeat,ManagedLiveBytes=managed,PrivateBytes=process.PrivateMemorySize64,WorkingSetBytes=process.WorkingSet64,PeakWorkingSetBytes=process.PeakWorkingSet64,LibrarySHA256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Database).Assembly.Location))),DatabaseInstances=1,PageCacheBytes=64*1024*1024,SqlCacheBudget=pure?64*1024*1024:0};
    File.WriteAllText(output,JsonSerializer.Serialize(profile,new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(true));return;
}
if(mode=="writes")
foreach(var durability in new[]{Durability.Full,Durability.Buffered})
{
    foreach(string scenario in new[]{"single","concurrent8","batch1000","batch_unique","batch_update","batch_delete","update","delete","mixed"})
    {
        string path=Path.Combine(work,durability+"-"+scenario+(pure?".pdb":".sqlite"));
        activePath=path;
        await using var sql=pure?new SqlDatabase(path,new(){Storage=new(){Durability=durability}}):null;
        using var sqlite=pure?null:new Sqlite(path,durability);
        string schema="CREATE TABLE t(id INTEGER PRIMARY KEY,k INTEGER NOT NULL,v BLOB NOT NULL); CREATE INDEX ix ON t(k);"+(scenario=="batch_unique"?"CREATE UNIQUE INDEX uq ON t(k)":"");
        if(pure)await sql!.ExecuteAsync(schema);else sqlite!.Exec(schema);
        using var insert=sqlite?.Prepare("INSERT INTO t VALUES(?1,?2,?3)");
        async Task Put(int id,SqlDatabase? d= null,Sqlite.Statement? statement=null)
        {
            if(pure)await (d??sql!).ExecuteAsync("INSERT INTO t VALUES(@id,@k,@v)",new Dictionary<string,object?>{["id"]=(long)id,["k"]=(long)id%1000,["v"]=payload});
            else {var s=statement??insert!;s.Bind(1,id);s.Bind(2,id%1000);s.Bind(3,payload);s.Step();s.Reset();}
        }
        async Task Batch(int start,int size)
        {
            if(pure)await sql!.ExecuteTransactionAsync(Enumerable.Range(start,size).Select(i=>new SqlCommand("INSERT INTO t VALUES(@id,@k,@v)",new Dictionary<string,object?>{["id"]=(long)i,["k"]=(long)i%1000,["v"]=payload})).ToArray());
            else {sqlite!.Exec("BEGIN IMMEDIATE");for(int i=start;i<start+size;i++)await Put(i);sqlite.Exec("COMMIT");}
        }
        int n=scenario is "batch1000" or "batch_unique" or "batch_update" or "batch_delete"?10000:durability==Durability.Full?100:1000;
        if(scenario=="batch_unique")n=1000; // demonstrates constraint complexity without an impractical quadratic baseline run
        if(scenario is "update" or "delete" or "mixed" or "batch_update" or "batch_delete")await Batch(0,n);
        var latency=new List<double>();
        if(scenario=="concurrent8")
        {
            var connections=pure?[]:Enumerable.Range(0,8).Select(_=>new Sqlite(path,durability)).ToArray();
            var puts=connections.Select(c=>c.Prepare("INSERT INTO t VALUES(?1,?2,?3)")).ToArray();
            try{await Measure(durability+"_"+scenario,n,()=>Task.WhenAll(Enumerable.Range(0,8).Select(w=>Task.Run(async()=>{for(int i=w;i<n;i+=8)await Put(i,null,pure?null:puts[w]);}))),"rows");}
            finally{foreach(var s in puts)s.Dispose();foreach(var c in connections)c.Dispose();}
        }
        else await Measure(durability+"_"+scenario,n,async()=>
        {
            if(scenario is "batch_update" or "batch_delete")
            {
                for(int i=0;i<n;i+=1000)
                {
                    string text=(scenario=="batch_delete"?"DELETE FROM t":"UPDATE t SET k=k+1")+$" WHERE id BETWEEN {i} AND {i+999}";
                    long t=Stopwatch.GetTimestamp();if(pure)await sql!.ExecuteAsync(text);else{sqlite!.Exec("BEGIN IMMEDIATE;"+text+";COMMIT;");}latency.Add(Stopwatch.GetElapsedTime(t).TotalMicroseconds);
                }
                return;
            }
            if(scenario is "batch1000" or "batch_unique") {for(int i=0;i<n;i+=1000){long t=Stopwatch.GetTimestamp();await Batch(i,Math.Min(1000,n-i));latency.Add(Stopwatch.GetElapsedTime(t).TotalMicroseconds);}return;}
            using var update=sqlite?.Prepare("UPDATE t SET k=k+1 WHERE id=?1");using var delete=sqlite?.Prepare("DELETE FROM t WHERE id=?1");using var get=sqlite?.Prepare("SELECT id,k,v FROM t WHERE id=?1");
            for(int i=0;i<n;i++)
            {
                long t=Stopwatch.GetTimestamp();
                if(scenario=="single")await Put(i);
                else if(pure) {await sql!.ExecuteAsync(scenario=="delete"?"DELETE FROM t WHERE id=@id":"UPDATE t SET k=k+1 WHERE id=@id",new Dictionary<string,object?>{["id"]=(long)i});if(scenario=="mixed")for(int j=0;j<5;j++)sql.Query("SELECT id,k,v FROM t WHERE id=@id",new Dictionary<string,object?>{["id"]=(long)((i+j)%n)});}
                else {var s=scenario=="delete"?delete!:update!;s.Bind(1,i);s.Step();s.Reset();if(scenario=="mixed")for(int j=0;j<5;j++){get!.Bind(1,(i+j)%n);get.Step();get.Values();get.Reset();}}
                latency.Add(Stopwatch.GetElapsedTime(t).TotalMicroseconds);
            }
        },scenario=="mixed"?"write+5reads":"rows",latency);
        long expected=scenario is "delete" or "batch_delete"?0:n;
        Check(pure?(long)sql!.Query("SELECT COUNT(*) FROM t").Rows[0][0]! ==expected:Scalar(sqlite!,"SELECT COUNT(*) FROM t")==expected,"write count");
        if(scenario is "update" or "mixed" or "batch_update")Check(pure?(long)sql!.Query("SELECT SUM(k) FROM t").Rows[0][0]! == Enumerable.Range(0,n).Sum(i=>(long)i%1000+1):Scalar(sqlite!,"SELECT SUM(k) FROM t")==Enumerable.Range(0,n).Sum(i=>(long)i%1000+1),"updated values");
        if(scenario=="batch1000")
        {
            if(pure)await sql!.ExecuteAsync("DELETE FROM t WHERE id<5000");else sqlite!.Exec("DELETE FROM t WHERE id<5000");
            await Measure(durability+"_compact",1,async()=>{if(pure)await sql!.CompactAsync();else sqlite!.Exec("PRAGMA wal_checkpoint(TRUNCATE); VACUUM;");},"compactions");
            Check(pure?(long)sql!.Query("SELECT COUNT(*) FROM t").Rows[0][0]! ==5000:Scalar(sqlite!,"SELECT COUNT(*) FROM t")==5000,"compact rows");
        }
    }
}
if(mode=="processes")
{
    string path=Path.Combine(work,pure?"shared.pdb":"shared.sqlite");
    await using(var d=pure?new SqlDatabase(path,new(){Storage=new(){Access=DatabaseAccess.Shared,Durability=Durability.Buffered}}):null)
    using(var s=pure?null:new Sqlite(path,Durability.Buffered))
    {string schema="CREATE TABLE t(id INTEGER PRIMARY KEY,k INTEGER); INSERT INTO t VALUES(1,0)";if(pure)await d!.ExecuteAsync(schema);else s!.Exec(schema);}
    activePath=path;
    foreach(var job in new[]{"shared_read","shared_update"})
    {
        var children=Enumerable.Range(0,4).Select(w=>
        {
            var si=new ProcessStartInfo("dotnet"){RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string a in new[]{Assembly.GetExecutingAssembly().Location,"worker",engine,path,job})si.ArgumentList.Add(a);
            return Process.Start(si)!;
        }).ToArray();
        foreach(var child in children)Check(await child.StandardOutput.ReadLineAsync()=="READY","worker failed to start: "+await(child.HasExited?child.StandardError.ReadToEndAsync():Task.FromResult("")));
        var childMetrics=new List<JsonElement>();
        await Measure(job,4000,async()=>
        {
            foreach(var child in children){await child.StandardInput.WriteLineAsync("GO");await child.StandardInput.FlushAsync();}
            foreach(var child in children){string response=await child.StandardOutput.ReadToEndAsync();await child.WaitForExitAsync();Check(child.ExitCode==0,"worker failed "+await child.StandardError.ReadToEndAsync());Check(response.Contains("DONE"),"worker incomplete");childMetrics.Add(JsonDocument.Parse(response.Trim()[5..]).RootElement.Clone());}
        },job=="shared_read"?"queries":"updates");
        results[^1]=results[^1] with { WorkerCpuMilliseconds=childMetrics.Sum(m=>m.GetProperty("CpuMs").GetDouble()),WorkerPeakBytesSum=childMetrics.Sum(m=>m.GetProperty("PeakBytes").GetInt64()),WorkerPrivateBytesSum=childMetrics.Sum(m=>m.GetProperty("PrivateBytes").GetInt64()),WorkerReadBytes=childMetrics.Sum(m=>m.GetProperty("IO").GetProperty("ReadBytes").GetInt64()),WorkerWriteBytes=childMetrics.Sum(m=>m.GetProperty("IO").GetProperty("WriteBytes").GetInt64()) };
        foreach(var child in children)child.Dispose();
    }
    if(pure){using var d=new SqlDatabase(path,new(){Storage=new(){Access=DatabaseAccess.Shared}});Check((long)d.Query("SELECT k FROM t WHERE id=1").Rows[0][0]! ==4000,"lost updates");}
    else {using var d=new Sqlite(path,Durability.Buffered);Check(Scalar(d,"SELECT k FROM t WHERE id=1")==4000,"lost updates");}
}
if(mode=="worker")
{
    string path=dataset,job=output;
    await using var d=pure?new SqlDatabase(path,new(){Storage=new(){Access=DatabaseAccess.Shared,Durability=Durability.Buffered}}):null;
    using var s=pure?null:new Sqlite(path,Durability.Buffered);
    using var get=s?.Prepare("SELECT k FROM t WHERE id=1");using var update=s?.Prepare("UPDATE t SET k=k+1 WHERE id=1");
    Console.WriteLine("READY");await Console.In.ReadLineAsync();using var cp=Process.GetCurrentProcess();double cpuBefore=cp.TotalProcessorTime.TotalMilliseconds;var ioBefore=Io.Read();
    for(int i=0;i<1000;i++)if(pure){if(job.EndsWith("read",StringComparison.Ordinal))Check((long)d!.Query("SELECT k FROM t WHERE id=1").Rows[0][0]!>=0,"read");else await d!.ExecuteAsync("UPDATE t SET k=k+1 WHERE id=1");}else{var stmt=job.EndsWith("read",StringComparison.Ordinal)?get!:update!;stmt.Step();if(job.EndsWith("read",StringComparison.Ordinal))stmt.Values();stmt.Reset();}
    cp.Refresh();Console.WriteLine("DONE "+JsonSerializer.Serialize(new{CpuMs=cp.TotalProcessorTime.TotalMilliseconds-cpuBefore,PeakBytes=cp.PeakWorkingSet64,PrivateBytes=cp.PrivateMemorySize64,IO=Io.Read()-ioBefore}));return;
}
var files=Directory.GetFiles(work,"*",SearchOption.AllDirectories).Select(f=>new{Path=Path.GetRelativePath(work,f),Bytes=new FileInfo(f).Length}).ToArray();
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output,JsonSerializer.Serialize(new{Engine=engine,Mode=mode,Runtime=Environment.Version.ToString(),SQLite=Sqlite.Version,CPUs=Environment.ProcessorCount,QueryWarmupMilliseconds=500,QueryMeasurementMilliseconds=750,LibrarySHA256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Database).Assembly.Location))),DateUtc=DateTime.UtcNow,Samples=results,Files=files},new JsonSerializerOptions{WriteIndented=true}),new UTF8Encoding(true));
internal sealed record Sample(string Engine,string Mode,string Scenario,int Repeat,long Operations,string Unit,double Milliseconds,double OpsPerSecond,double P50Microseconds,double P95Microseconds,double P99Microseconds,int LatencySamples,long AllocatedBytes,long WorkingSetBytes,long PrivateBytes,long PeakWorkingSetBytes,long ManagedLiveBytes,double CpuMilliseconds,double CpuOneCorePercent,double CpuMachinePercent,long ReadBytes,long WriteBytes,long ReadOperations,long WriteOperations,long StorageBeforeBytes,long StorageAfterBytes)
{
 public double WorkerCpuMilliseconds {get;init;}
 public long WorkerPeakBytesSum {get;init;}
 public long WorkerPrivateBytesSum {get;init;}
 public long WorkerReadBytes {get;init;}
 public long WorkerWriteBytes {get;init;}
}
internal readonly record struct Io(long ReadBytes,long WriteBytes,long ReadOps,long WriteOps)
{
 [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct Counters { public ulong Reads,Writes,Others,ReadBytes,WriteBytes,OtherBytes; }
 [System.Runtime.InteropServices.DllImport("kernel32.dll",SetLastError=true)] private static extern bool GetProcessIoCounters(nint process,out Counters counters);
 public static Io Read() { if(OperatingSystem.IsLinux() && File.Exists("/proc/self/io")){var c=File.ReadAllLines("/proc/self/io").Select(l=>l.Split(':')).ToDictionary(p=>p[0],p=>long.Parse(p[1].Trim()));return new(c["rchar"],c["wchar"],c["syscr"],c["syscw"]);} if(!OperatingSystem.IsWindows())return new(-1,-1,-1,-1); using var p=Process.GetCurrentProcess();if(!GetProcessIoCounters(p.Handle,out var counters))throw new System.ComponentModel.Win32Exception();return new((long)counters.ReadBytes,(long)counters.WriteBytes,(long)counters.Reads,(long)counters.Writes); }
 public static Io operator -(Io a,Io b)=>new(a.ReadBytes<0||b.ReadBytes<0?-1:a.ReadBytes-b.ReadBytes,a.WriteBytes<0||b.WriteBytes<0?-1:a.WriteBytes-b.WriteBytes,a.ReadOps<0||b.ReadOps<0?-1:a.ReadOps-b.ReadOps,a.WriteOps<0||b.WriteOps<0?-1:a.WriteOps-b.WriteOps);
}

