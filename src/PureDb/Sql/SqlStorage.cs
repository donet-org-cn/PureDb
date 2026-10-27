using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("PureDb.SqliteCompatibility")]
[assembly: InternalsVisibleTo("PureDb.CompatibilityTests")]
[assembly: InternalsVisibleTo("PureDb.StorageTests")]
namespace PureDb;

/// <summary>SQL 层与可替换存储后端之间的内部协议；变换必须在受保护快照上原子提交。</summary>
internal interface ISqlStorage : IDisposable, IAsyncDisposable
{
    /// <summary>在存储保护期内执行快照回调，不允许回调持有无保护的可变存储引用。</summary>
    /// <param name="action">只在读取保护期内执行的快照回调。</param>
    /// <typeparam name="T">回调返回值或请求转换的结果类型。</typeparam>
    /// <returns>回调或类型转换得到的结果。</returns>
    T Read<T>(Func<ISqlSnapshot, T> action);
    /// <summary>在写入保护期间基于一致快照生成并原子提交变更。</summary>
    /// <param name="transform">在存储保护期内从一致快照生成原子变更的回调。</param>
    /// <param name="token">取消请求；原子提交开始后需完成提交或回滚。</param>
    /// <returns>完成时提供直接受影响行数的任务。</returns>
    ValueTask<int> TransformAsync(Func<ISqlSnapshot, (Mutation[] Mutations, int Affected)> transform, CancellationToken token);
    /// <summary>压缩可回收的历史存储；后端不支持时必须明确报错。</summary>
    /// <param name="token">取消请求；原子提交开始后需完成提交或回滚。</param>
    /// <returns>表示操作最终完成的任务。</returns>
    ValueTask CompactAsync(CancellationToken token = default);
    /// <summary>保存可重建索引检查点；后端不支持时必须明确报错。</summary>
    /// <param name="token">取消请求；原子提交开始后需完成提交或回滚。</param>
    /// <returns>表示操作最终完成的任务。</returns>
    ValueTask CheckpointAsync(CancellationToken token = default);
    /// <summary>关闭准入并等待已接受的写入结束，确保上层安全释放缓存及文件。</summary>
    /// <returns>表示操作最终完成的任务。</returns>
    ValueTask DrainForCloseAsync();
}

/// <summary>仅在存储读取保护期内有效的已提交视图；内存片段不可被调用方修改或越期持有。</summary>
internal interface ISqlSnapshot
{
    /// <summary>是否支持原生持久化索引；兼容或叠加快照可关闭专用路径。</summary>
    bool HasPersistentIndexes
    {
        get;
    }

    /// <summary>当前数据文件的规范路径。</summary>
    string Path
    {
        get;
    }

    /// <summary>用于识别文件替换的存储代次。</summary>
    int Generation
    {
        get;
    }

    /// <summary>当前快照可见的数据文件提交长度。</summary>
    long Length
    {
        get;
    }

    /// <summary>当前数据文件的修改时间戳。</summary>
    long SourceTime
    {
        get;
    }

    /// <summary>当前视图对应的已提交版本。</summary>
    long Version
    {
        get;
    }

    /// <summary>获取指定表或键空间的变化版本，用于缓存失效。</summary>
    /// <param name="key">用于查询或统计的内部表键或索引键。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    long KeyVersion(long key);
    /// <summary>统计指定表或键空间中的有效记录数。</summary>
    /// <param name="key">用于查询或统计的内部表键或索引键。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    int CountKey(long key);
    /// <summary>枚举指定表或键空间内的有效记录 ID。</summary>
    /// <param name="key">用于查询或统计的内部表键或索引键。</param>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    IEnumerable<long> Ids(long key);
    /// <summary>当前快照中的有效记录总数。</summary>
    long Count
    {
        get;
    }

    /// <summary>按记录 ID 读取独立记录；不存在时返回 null。</summary>
    /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    DbRecord? Get(long id);
    /// <summary>判断当前视图是否存在指定记录 ID。</summary>
    /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    bool Contains(long id) => Get(id) is not null;
    /// <summary>取得记录的只读字节片段；仅在读取保护期间有效，不得修改。</summary>
    /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
    /// <returns>受当前读取保护期约束的只读字节片段。</returns>
    ReadOnlySpan<byte> Value(long id);
    /// <summary>取得记录的只读内存；不得修改或越过所属读取保护期持有。</summary>
    /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
    /// <returns>只读字节内存；不得修改，且必须遵守所属存储的生命周期。</returns>
    ReadOnlyMemory<byte> ValueMemory(long id);
    /// <summary>按索引键闭区间枚举当前视图中的有效记录。</summary>
    /// <param name="min">查询闭区间的下界，包含该值。</param>
    /// <param name="max">查询闭区间的上界，包含该值。</param>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    IEnumerable<DbRecord> Range(long min, long max);
    /// <summary>取得指定记录对应的日志位置及校验信息。</summary>
    /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
    /// <param name="key">用于查询或统计的内部表键或索引键。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    DiskIndex.Item IndexItem(long id, long key);
    /// <summary>读取一个索引项对应的只读记录字节。</summary>
    /// <param name="entry">已定位的日志索引项，包含偏移、长度及校验和。</param>
    /// <returns>受当前读取保护期约束的只读字节片段。</returns>
    ReadOnlySpan<byte> IndexValue(DiskIndex.Item entry);
    /// <summary>读取索引项对应的只读记录内存，供 SQL 缓存在版本约束下使用。</summary>
    /// <param name="entry">已定位的日志索引项，包含偏移、长度及校验和。</param>
    /// <returns>只读字节内存；不得修改，且必须遵守所属存储的生命周期。</returns>
    ReadOnlyMemory<byte> IndexMemory(DiskIndex.Item entry);
    /// <summary>按原生记录 ID 枚举指定表的索引项。</summary>
    /// <param name="tableId">表在模式目录中的内部 ID。</param>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    IEnumerable<DiskIndex.Item> TableEntries(int tableId);
    /// <summary>按原生记录 ID 的闭区间枚举指定表的索引项。</summary>
    /// <param name="tableId">表在模式目录中的内部 ID。</param>
    /// <param name="min">查询闭区间的下界，包含该值。</param>
    /// <param name="max">查询闭区间的上界，包含该值。</param>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    IEnumerable<DiskIndex.Item> TableEntriesRange(int tableId, long min, long max) => TableEntries(tableId).Where(e => e.Id >= min && e.Id <= max);

    /// <summary>按指定方向惰性枚举主键闭区间；普通后端可使用默认反向实现。</summary>
    /// <param name="tableId">表在模式目录中的内部 ID。</param>
    /// <param name="min">包含的记录 ID 下界。</param>
    /// <param name="max">包含的记录 ID 上界。</param>
    /// <param name="descending">是否按记录 ID 降序读取。</param>
    /// <returns>读取保护期内有效的有序索引项。</returns>
    IEnumerable<DiskIndex.Item> TableEntriesRange(int tableId, long min, long max, bool descending)
        => descending ? TableEntriesRange(tableId, min, max).Reverse() : TableEntriesRange(tableId, min, max);
}
