using PureDb.Benchmarks;

/// <summary>发布测量中的 SQLite C API 适配层，与托管引擎一样物化行和值。</summary>
internal sealed class Engine : IDisposable
{
    /// <summary>获取测量报告中的引擎名称。</summary>
    public static string Name => "SQLite";
    /// <summary>获取实际加载的原生库版本。</summary>
    public static string? SQLiteVersion => Sqlite.Version;
    /// <summary>获取隔离数据库文件名。</summary>
    public static string FileName => "sqlite.sqlite";
    private readonly Sqlite _db;
    private readonly Sqlite.Statement?[] _queries = new Sqlite.Statement?[Workload.Queries.Length];
    private Sqlite.Statement? _insert;
    /// <summary>打开连接并设置相同的页面缓存预算。</summary>
    /// <param name="path">测试数据库路径。</param>
    /// <param name="full">是否每次提交强制刷盘。</param>
    public Engine(string path, bool full = false)
    {
        _db = new(path, full ? SqliteDurability.Full : SqliteDurability.Buffered);
        _db.Exec("PRAGMA cache_size=-65536");
    }
    /// <summary>复用预编译语句并物化查询结果。</summary>
    /// <param name="kind">工作负载编号。</param>
    /// <param name="id">查询参数。</param>
    /// <returns>完成所有权复制的结果行。</returns>
    public object?[][] Query(int kind, int id)
    {
        var statement = _queries[kind] ??= _db.Prepare(Workload.Queries[kind]);
        if (kind <= 2)
        {
            statement.Bind(1, id);
            if (kind == 1)
                statement.Bind(2, id + 10);
        }
        var rows = new List<object?[]>();
        while (statement.Step())
            rows.Add(statement.Values());
        statement.Reset();
        return rows.ToArray();
    }
    /// <summary>执行没有返回行的语句。</summary>
    /// <param name="sql">待执行语句。</param>
    public void Execute(string sql) => _db.Exec(sql);
    /// <summary>复用语句写入一行相同负载的数据。</summary>
    /// <param name="id">主键及数据生成种子。</param>
    /// <param name="payload">二进制负载。</param>
    public void Insert(int id, byte[] payload)
    {
        var statement = _insert ??= _db.Prepare("INSERT INTO t VALUES(?1,?2,?3,?4,?5)");
        statement.Bind(1, id);
        statement.Bind(2, id % 1000);
        statement.Bind(3, id * 7919 % 100000);
        statement.Bind(4, payload);
        statement.Bind(5, Workload.Json(id));
        statement.Step();
        statement.Reset();
    }
    /// <summary>在单一事务内批量插入。</summary>
    /// <param name="start">起始主键。</param>
    /// <param name="count">写入行数。</param>
    /// <param name="payload">二进制负载。</param>
    public void Batch(int start, int count, byte[] payload)
    {
        _db.Exec("BEGIN IMMEDIATE");
        for (int i = start; i < start + count; i++)
            Insert(i, payload);
        _db.Exec("COMMIT");
    }
    /// <summary>获取已提交行数，验证批量结果。</summary>
    public long Count
    {
        get
        {
            using var q = _db.Prepare("SELECT COUNT(*) FROM t");
            if (!q.Step())
                throw new Exception("COUNT");
            return q.Int64(0);
        }
    }
    /// <summary>将测试数据检查点完成后用于后续只读测量。</summary>
    public void PrepareIndexes() => _db.Exec("PRAGMA wal_checkpoint(TRUNCATE)");
    /// <summary>释放预编译语句和数据库连接。</summary>
    public void Dispose()
    {
        _insert?.Dispose();
        foreach (var q in _queries)
            q?.Dispose();
        _db.Dispose();
    }
}
