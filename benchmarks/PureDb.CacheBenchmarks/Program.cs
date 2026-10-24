using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PureDb;
using PureDb.Benchmarks;

string engine=args[0],dataset=Path.GetFullPath(args[1]),output=args[2];
if(OperatingSystem.IsWindows()&&Environment.GetEnvironmentVariable("SQLITE_LIBRARY")is null) Environment.SetEnvironmentVariable("SQLITE_LIBRARY",Path.GetFullPath("artifacts/sqlite/3.54.1/sqlite3.dll"));
bool pure=engine!="SQLite";
using var db=pure?new SqlDatabase(Path.Combine(dataset,"sql.pdb")):null;
using var sqlite=pure?null:new Sqlite(Path.Combine(dataset,"reference.sqlite"),Durability.Buffered);
sqlite?.Exec("PRAGMA cache_size=-65536");
const string text="SELECT id,index_key,value FROM records WHERE id=@id";
using var stmt=sqlite?.Prepare(text);
var parameters=new Dictionary<string,object?>();
void Query(int i)
{
    long id=(long)((ulong)i*7919%100000);
    if(pure)
    {
        parameters["id"]=id;var rows=db!.Query(text,parameters).Rows;
        if(rows.Count!=1||(long)rows[0][0]! !=id||((byte[])rows[0][2]!).Length!=256)throw new Exception("Incorrect row");
    }
    else
    {
        stmt!.Bind(1,id);if(!stmt.Step())throw new Exception("Missing row");var values=stmt.Values();
        if((long)values[0]! !=id||((byte[])values[2]!).Length!=256)throw new Exception("Incorrect row");stmt.Reset();
    }
}
// Both engines visit every key twice in the same pseudo-random order before timing.
for(int pass=0;pass<2;pass++)for(int i=0;i<100000;i++)Query(i);
GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
using var process=Process.GetCurrentProcess();double cpu=process.TotalProcessorTime.TotalMilliseconds;long allocated=GC.GetTotalAllocatedBytes(true);
var watch=Stopwatch.StartNew();const int operations=500000;
for(int i=0;i<operations;i++)Query(i);
watch.Stop();cpu=process.TotalProcessorTime.TotalMilliseconds-cpu;allocated=GC.GetTotalAllocatedBytes(true)-allocated;
GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();process.Refresh();
var result=new{Engine=engine,Operations=operations,Milliseconds=watch.Elapsed.TotalMilliseconds,CpuMilliseconds=cpu,AllocatedBytes=allocated,
    WorkingSetBytes=process.WorkingSet64,PrivateBytes=process.PrivateMemorySize64,ManagedLiveBytes=GC.GetTotalMemory(true),
    CPUs=Environment.ProcessorCount,Runtime=Environment.Version.ToString(),SQLite=Sqlite.Version,
    LibrarySHA256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Database).Assembly.Location)))};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output,JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}),new System.Text.UTF8Encoding(true));
GC.KeepAlive(db);Console.WriteLine(engine+" steady SQL cache profile complete");
