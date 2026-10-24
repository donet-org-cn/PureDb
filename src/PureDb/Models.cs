namespace PureDb;
/// <summary>写入确认的持久性级别。</summary>
public enum Durability
{
    /// <summary>事务刷入磁盘后才确认成功；依赖操作系统和存储设备兑现刷盘请求。</summary>
    Full,
    /// <summary>事务交给操作系统缓存后确认成功；断电可能丢失已确认的写入。</summary>
    Buffered
}

/// <summary>数据库文件的访问和进程间协调方式。</summary>
public enum DatabaseAccess
{
    /// <summary>当前实例独占文件，避免每次操作的跨进程协调开销。</summary>
    Exclusive,
    /// <summary>通过进程间锁协调多个实例，读取前刷新已提交的文件变化。</summary>
    Shared
}

/// <summary>原生文件存储的访问方式、缓存、写入队列和恢复选项。</summary>
public sealed class DatabaseOptions
{
    /// <summary>文件访问方式；默认独占，多个进程访问同一文件时使用 Shared。</summary>
    public DatabaseAccess Access { get; init; } = DatabaseAccess.Exclusive;
    /// <summary>数据页缓存预算，单位为字节；默认 64 MiB，0 表示不缓存数据页。</summary>
    public long CacheBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>写入确认的持久性级别，默认为 Full。</summary>
    public Durability Durability { get; init; } = Durability.Full;
    /// <summary>待处理写入请求的有界队列容量；队列已满时等待准入。</summary>
    public int QueueCapacity { get; init; } = 4096;
    /// <summary>一次分组提交最多合并的请求数；合并刷盘不改变每个请求的事务边界。</summary>
    public int MaxGroupRequests { get; init; } = 128;
    /// <summary>单个编码事务的最大字节数；超限写入在落盘前失败。</summary>
    public int MaxTransactionBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>打开时是否重放并校验完整日志；默认使用持久化索引和按需校验以缩短启动时间。</summary>
    public bool VerifyOnOpen
    {
        get; init;
    }
    /// <summary>追加达到指定字节数后自动生成索引检查点；0 表示关闭定期检查点。</summary>
    public long CheckpointIntervalBytes { get; init; } = 64 * 1024 * 1024;
}

/// <summary>读取出的键值记录；Value 数组由调用方持有，可以独立修改。</summary>
/// <param name="Id">记录的唯一 ID。</param>
/// <param name="IndexKey">用于范围查询的索引键。</param>
/// <param name="Value">记录的数据内容。</param>
public sealed record DbRecord(long Id, long IndexKey, byte[] Value);
/// <summary>一个待提交的记录写入或删除操作；多个操作可作为一个原子事务提交。</summary>
/// <param name="Id">记录的唯一 ID。</param>
/// <param name="IndexKey">用于范围查询的索引键。</param>
/// <param name="Value">记录的数据内容。</param>
/// <param name="IsDelete">是否表示删除；为 true 时忽略值和索引键。</param>
public readonly record struct Mutation(long Id, long IndexKey, ReadOnlyMemory<byte> Value, bool IsDelete)
{
    /// <summary>构造记录写入操作；提交前应保持输入内存不变。</summary>
    /// <param name="id">记录的唯一 ID。</param>
    /// <param name="indexKey">用于范围查询的索引键。</param>
    /// <param name="value">要写入的数据；写入接口在准入前复制，构造 Mutation 本身不复制。</param>
    /// <returns>待提交的写入操作。</returns>
    public static Mutation Put(long id, long indexKey, ReadOnlyMemory<byte> value) => new(id, indexKey, value, false);
    /// <summary>构造按记录 ID 删除的操作；记录不存在时不产生记录。</summary>
    /// <param name="id">记录的唯一 ID。</param>
    /// <returns>待提交的删除操作。</returns>
    public static Mutation Delete(long id) => new(id, 0, ReadOnlyMemory<byte>.Empty, true);
}
