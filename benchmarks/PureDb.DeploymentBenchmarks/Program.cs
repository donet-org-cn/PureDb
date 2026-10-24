using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

// 同一源码分别引用 PureDb 和 SQLite，分别发布为 JIT 与 AOT；计时输出使用源生成元数据。
if (args.Length < 2) throw new ArgumentException("create|queries|writes|startup|memory dataset [output round scenario]");
string mode = args[0], dataset = Path.GetFullPath(args[1]);
Directory.CreateDirectory(dataset);
string path = Path.Combine(dataset, Engine.FileName);
byte[] payload = new byte[256];
new Random(42).NextBytes(payload);
if (mode == "create")
{
    if (File.Exists(path))
        throw new IOException("Use a fresh fixture");
    using var db = new Engine(path);
    db.Execute(Workload.Schema);
    for (int i = 0; i < 100000; i += 1000)
        db.Batch(i, 1000, payload);
    db.PrepareIndexes();
    if (db.Count != 100000)
        throw new Exception("Fixture count");
    Console.WriteLine("CREATED " + path);
    return;
}
string output = args[2];
int round = args.Length > 3 ? int.Parse(args[3]) : 0;
var report = new DeploymentReport { Engine = Engine.Name, Runtime = RuntimeInformation.FrameworkDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), DynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported, Mode = mode, Round = round, SQLite = Engine.SQLiteVersion };
long Storage(string file) => Directory.EnumerateFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file) + "*").Sum(f => new FileInfo(f).Length);
DeploymentSample Measure(string scenario, long operations, Action action, string file)
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    using var process = Process.GetCurrentProcess();
    double cpu = process.TotalProcessorTime.TotalMilliseconds;
    long allocation = GC.GetTotalAllocatedBytes(true);
    var timer = Stopwatch.StartNew();
    action();
    timer.Stop();
    double usedCpu = process.TotalProcessorTime.TotalMilliseconds - cpu;
    process.Refresh();
    return new(scenario, operations, timer.Elapsed.TotalMilliseconds, usedCpu, GC.GetTotalAllocatedBytes(true) - allocation,
        process.WorkingSet64, process.PrivateMemorySize64, process.PeakWorkingSet64, GC.GetTotalMemory(false), Storage(file));
}
if (mode == "startup")
{
    report.Samples.Add(Measure("open_first_range_close", 1, () => { using var db = new Engine(path); if (db.Query(1, 10).Length != 20) throw new Exception("Startup rows"); }, path));
}
else if (mode == "queries")
{
    using var db = new Engine(path);
    for (int kind = 0; kind < Workload.Queries.Length; kind++)
    {
        int id = 0;
        var warm = Stopwatch.StartNew();
        void Query()
        {
            int count = db.Query(kind, kind == 0 ? id++ % 100000 : id++ % 100).Length;
            if (count == 0)
                throw new Exception("Empty query");
        }
        do
        {
            Query();
        } while (warm.ElapsedMilliseconds < 500);
        long operations = 0;
        var sample = Measure(Workload.Names[kind], 0, () => { var timer = Stopwatch.StartNew(); do { Query(); operations++; } while (timer.ElapsedMilliseconds < 750); }, path);
        report.Samples.Add(sample with
        {
            Operations = operations
        });
    }
}
else if (mode == "memory")
{
    using var db = new Engine(path);
    int kind = int.Parse(args[4]);
    int count = kind == 0 ? 100000 : 1000;
    for (int i = 0; i < count; i++)
        db.Query(kind, kind == 0 ? i : i % 100);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    using var p = Process.GetCurrentProcess();
    p.Refresh();
    report.Samples.Add(new(Workload.Names[kind], count, 0, 0, 0, p.WorkingSet64, p.PrivateMemorySize64, p.PeakWorkingSet64, GC.GetTotalMemory(true), Storage(path)));
}
else if (mode == "writes")
{
    foreach (bool full in new[] { false, true })
        foreach (string scenario in new[] { "single", "batch_insert", "batch_update", "batch_delete" })
        {
            string file = Path.Combine(dataset, $"{Engine.Name}-{full}-{scenario}-{round}-{Guid.NewGuid():N}.db");
            using var db = new Engine(file, full);
            db.Execute(Workload.Schema);
            int count = scenario == "single" ? full ? 50 : 1000 : 5000;
            if (scenario is "batch_update" or "batch_delete")
                db.Batch(0, count, payload);
            var sample = Measure((full ? "Full_" : "Buffered_") + scenario, count, () =>
            {
                if (scenario == "single")
                {
                    for (int i = 0; i < count; i++)
                        db.Insert(i, payload);
                }
                else if (scenario == "batch_insert")
                {
                    for (int i = 0; i < count; i += 1000)
                        db.Batch(i, 1000, payload);
                }
                else
                    for (int i = 0; i < count; i += 1000)
                    {
                        string statement = (scenario == "batch_update" ? "UPDATE t SET k=k+1" : "DELETE FROM t") + $" WHERE id BETWEEN {i} AND {i + 999}";
                        // 两引擎均使用一个明确的千行事务；PureDb 单语句 Execute 本身即为原子事务。
                        if (Engine.Name == "SQLite")
                            statement = "BEGIN IMMEDIATE;" + statement + ";COMMIT";
                        db.Execute(statement);
                    }
            }, file);
            if (db.Count != (scenario == "batch_delete" ? 0 : count))
                throw new Exception("Write count");
            report.Samples.Add(sample);
        }
}
else
    throw new ArgumentException("Unknown mode");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
File.WriteAllText(output, JsonSerializer.Serialize(report, ReportContext.Default.DeploymentReport));
Console.WriteLine($"PASS {Engine.Name} {mode}; dynamic code={report.DynamicCodeSupported}; {output}");
