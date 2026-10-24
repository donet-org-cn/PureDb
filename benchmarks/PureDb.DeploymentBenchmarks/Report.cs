using System.Text.Json.Serialization;

/// <summary>同一发布应用的一次隔离测量，使用编译期 JSON 元数据输出。</summary>
internal sealed class DeploymentReport
{
    /// <summary>当前独立宿主使用的数据库引擎。</summary>
    public string Engine { get; set; } = "";
    /// <summary>实际运行时的描述及补丁版本。</summary>
    public string Runtime { get; set; } = "";
    /// <summary>当前进程的指令集架构。</summary>
    public string Architecture { get; set; } = "";
    /// <summary>运行时是否支持动态代码；Native AOT 测量必须为 false。</summary>
    public bool DynamicCodeSupported
    {
        get; set;
    }
    /// <summary>查询、写入、启动或隔离内存场景。</summary>
    public string Mode { get; set; } = "";
    /// <summary>当前交替测量的轮次。</summary>
    public int Round
    {
        get; set;
    }
    /// <summary>SQLite 宿主实际加载的版本；PureDb 宿主为 null。</summary>
    public string? SQLite
    {
        get; set;
    }
    /// <summary>本进程采集的各项原始测量。</summary>
    public List<DeploymentSample> Samples { get; set; } = [];
}

/// <summary>单个场景完成相同工作量的时间、CPU、托管分配和进程内存。</summary>
/// <param name="Scenario">可复现的场景名。</param>
/// <param name="Operations">完成的查询数或写入行数。</param>
/// <param name="Milliseconds">工作区间的实际毫秒数。</param>
/// <param name="CpuMilliseconds">工作区间内全部线程的进程 CPU 毫秒数。</param>
/// <param name="AllocatedBytes">区间累计托管分配，不能当作总内存。</param>
/// <param name="WorkingSetBytes">采样时的进程 RSS 或工作集字节数。</param>
/// <param name="PrivateBytes">采样时的进程私有字节数。</param>
/// <param name="PeakWorkingSetBytes">进程截至采样时的工作集峰值。</param>
/// <param name="ManagedLiveBytes">采样时托管存活字节估算。</param>
/// <param name="StorageBytes">当前数据库及其侧文件总字节数。</param>
internal sealed record DeploymentSample(string Scenario, long Operations, double Milliseconds, double CpuMilliseconds,
    long AllocatedBytes, long WorkingSetBytes, long PrivateBytes, long PeakWorkingSetBytes, long ManagedLiveBytes, long StorageBytes);

/// <summary>发布基准自身的 JSON 源生成上下文，AOT 时不依赖运行时反射。</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DeploymentReport))]
internal partial class ReportContext : JsonSerializerContext;

/// <summary>两引擎共同执行的查询和确定性数据定义。</summary>
internal static class Workload
{
    /// <summary>双方使用的相同表、普通索引和 JSON 表达式索引定义。</summary>
    public const string Schema = "CREATE TABLE t(id INTEGER PRIMARY KEY,k INTEGER NOT NULL,n INTEGER,v BLOB NOT NULL,j JSON);CREATE INDEX ix ON t(k);CREATE INDEX jx ON t(json_extract(j,'$.age'))";
    /// <summary>按固定序号执行的七种相同 SQL 查询。</summary>
    public static readonly string[] Queries = [
        "SELECT id,k,v FROM t WHERE id=@id",
        "SELECT id,k,v FROM t WHERE k>=@id AND k<=@hi ORDER BY k,id LIMIT 20",
        "SELECT id,json_extract(j,'$.name') FROM t WHERE json_extract(j,'$.age')=@id ORDER BY id LIMIT 20",
        "SELECT COUNT(*) FROM t WHERE json_extract(j,'$.age')%2=0",
        "SELECT id,n FROM t ORDER BY n,id LIMIT 20",
        "SELECT id,n FROM t ORDER BY n,id LIMIT 4096",
        "SELECT SUM(k) FROM t"
    ];
    /// <summary>与查询序号一一对应的结果场景名。</summary>
    public static readonly string[] Names = ["point", "range20", "json_index", "json_scan", "sort20", "sort4096", "sum_scan"];
    /// <summary>生成两引擎相同且可重复的 JSON 文本。</summary>
    /// <param name="id">当前整数主键。</param>
    /// <returns>含年龄与用户名称的 JSON 文本。</returns>
    public static string Json(int id) => "{\"age\":" + id % 100 + ",\"name\":\"user" + id + "\"}";
}
