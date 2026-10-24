using PureDb;

/// <summary>发布测量中的纯托管 SQL 适配层，参数字典在连接内复用。</summary>
internal sealed class Engine : IDisposable
{
    /// <summary>报告中的固定引擎名。</summary>
    public static string Name => "PureDb";
    /// <summary>该宿主不加载原生 SQLite，因此没有 SQLite 版本。</summary>
    public static string? SQLiteVersion => null;
    /// <summary>查询数据集文件名。</summary>
    public static string FileName => "pure.pdb";
    private readonly SqlDatabase _db;
    private readonly Dictionary<string, object?> _parameters = new() { ["id"] = 0L, ["hi"] = 10L };
    /// <summary>以明确持久性和默认缓存预算打开 SQL 库。</summary>
    /// <param name="path">隔离测量数据库路径。</param>
    /// <param name="full">是否使用每次提交强制刷盘。</param>
    public Engine(string path, bool full = false) => _db = new(path, new() { Storage = new() { Durability = full ? Durability.Full : Durability.Buffered } });
    /// <summary>重用参数字典执行固定查询并返回物化结果。</summary>
    /// <param name="kind">Workload 中的查询序号。</param>
    /// <param name="id">当前主键、范围下界或 JSON 年龄。</param>
    /// <returns>已经物化且由调用方持有的结果行。</returns>
    public object?[][] Query(int kind, int id)
    {
        _parameters["id"] = (long)id;
        _parameters["hi"] = (long)id + 10;
        var rows = _db.Query(Workload.Queries[kind], _parameters).Rows;
        return rows as object?[][] ?? rows.ToArray();
    }
    /// <summary>同步等待一个原子 SQL 命令完成。</summary>
    /// <param name="sql">受支持的建表或写入 SQL。</param>
    public void Execute(string sql) => _db.ExecuteAsync(sql).AsTask().GetAwaiter().GetResult();
    /// <summary>插入一行并等待该独立提交完成。</summary>
    /// <param name="id">目标整数主键。</param>
    /// <param name="payload">双方一致的 BLOB 内容。</param>
    public void Insert(int id, byte[] payload) => _db.ExecuteAsync("INSERT INTO t VALUES(@id,@k,@n,@v,@j)", Parameters(id, payload)).AsTask().GetAwaiter().GetResult();
    /// <summary>构造当前行的确定性插入参数。</summary>
    /// <param name="id">当前行整数主键。</param>
    /// <param name="payload">当前行 BLOB。</param>
    /// <returns>包含五个受支持标量的命名参数。</returns>
    private static Dictionary<string, object?> Parameters(int id, byte[] payload) => new() { ["id"] = (long)id, ["k"] = (long)(id % 1000), ["n"] = (long)(id * 7919 % 100000), ["v"] = payload, ["j"] = Workload.Json(id) };
    /// <summary>将连续主键的整批插入作为一个事务提交。</summary>
    /// <param name="start">批次起始主键。</param>
    /// <param name="count">批次行数。</param>
    /// <param name="payload">所有行使用的 BLOB 内容。</param>
    public void Batch(int start, int count, byte[] payload)
        => _db.ExecuteTransactionAsync(Enumerable.Range(start, count).Select(i => new SqlCommand("INSERT INTO t VALUES(@id,@k,@n,@v,@j)", Parameters(i, payload))).ToArray()).AsTask().GetAwaiter().GetResult();
    /// <summary>用于计时后核对写入结果的实际行数。</summary>
    public long Count => (long)_db.Query("SELECT COUNT(*) FROM t").Rows[0][0]!;
    /// <summary>在创建数据集时提前构建查询索引，不计入热查询区间。</summary>
    public void PrepareIndexes()
    {
        _db.Query(Workload.Queries[1], _parameters);
        _db.Query(Workload.Queries[2], _parameters);
    }
    /// <summary>执行关闭维护并释放数据库。</summary>
    public void Dispose() => _db.Dispose();
}
