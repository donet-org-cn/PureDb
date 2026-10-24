using System.Collections.Concurrent;
using System.Text.Json;

namespace PureDb;
/// <summary>建立在文件存储上的 SQL 数据库，提供查询、原子 DDL/DML 批处理及连接级 C# 函数。</summary>
public sealed partial class SqlDatabase : IDisposable, IAsyncDisposable
{
    private readonly ISqlStorage _storage;
    private readonly bool _owns;
    private readonly long _cacheBudget;
    private readonly long _initialLength;
    private readonly int _initialGeneration;
    private readonly object _closeGate = new();
    private Task? _closeTask;
    private readonly ConcurrentDictionary<(Select Query, int TableId, int Indexes), SqlRangePlan> _rangePlans = new();
    private readonly ConcurrentDictionary<(Select Query, int TableId), SqlPrimaryPlan> _primaryPlans = new();
    private readonly ConcurrentDictionary<(Select Query, int TableId), SqlScalarPlan> _scalarPlans = new();
    private readonly Lazy<ConcurrentDictionary<(Select Query, int Left, int Right), SqlJoinPlan>> _joinPlans = new(static () => new());
    private readonly Lazy<ConcurrentDictionary<(SqlStatement Statement, int Table), SqlDmlPlan>> _dmlPlans = new(static () => new());
    private readonly Lazy<ConcurrentDictionary<(Select Query, SqlTable Table), SqlExpressionRangePlan>> _expressionPlans = new(static () => new());
    private readonly ConcurrentDictionary<string, List<SqlStatement>> _plans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(Insert Statement, int Table), int[]> _insertColumns = new();
    private readonly object _cacheGate = new();
    private readonly Dictionary<int, (long Version, long SchemaVersion, SqlTableData Data)> _tables = [];
    private long _cachedBytes, _catalogVersion = -1;
    private SqlCatalog? _catalog;
    internal static readonly IReadOnlyDictionary<string, object?> EmptyParameters = new Dictionary<string, object?>();
    /// <summary>创建 SQL 数据库实例；外部传入的存储必须专用于 SQL，释放本实例不会释放该外部存储。</summary>
    /// <param name = "path">当前数据库或索引文件路径。</param>
    /// <param name = "options">可选配置；为 null 时使用默认值。</param>
    public SqlDatabase(string path, SqlDatabaseOptions? options = null)
    {
        options ??= new();
        if (options.CacheBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        _storage = new Database(path, options.Storage);
        _owns = true;
        _cacheBudget = options.CacheBytes;
        (_initialLength, _initialGeneration) = _storage.Read(s => (s.Length, s.Generation));
    }

    /// <summary>创建 SQL 数据库实例；外部传入的存储必须专用于 SQL，释放本实例不会释放该外部存储。</summary>
    /// <param name = "storage">专用于 SQL 的底层存储；本实例不取得该外部存储的所有权。</param>
    /// <param name = "cacheBytes">SQL 已解码表数据的缓存预算，单位为字节。</param>
    public SqlDatabase(Database storage, long cacheBytes = 64 * 1024 * 1024)
    {
        if (cacheBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(cacheBytes));
        _storage = storage;
        _cacheBudget = cacheBytes;
        (_initialLength, _initialGeneration) = _storage.Read(s => (s.Length, s.Generation));
    }

    /// <summary>接管可替换存储后端，并按配置决定缓存预算和底层存储的释放所有权。</summary>
    /// <param name = "storage">底层原生或兼容存储后端。</param>
    /// <param name = "cacheBytes">缓存预算，单位为字节。</param>
    /// <param name = "owns">SQL 实例是否负责关闭底层存储。</param>
    internal SqlDatabase(ISqlStorage storage, long cacheBytes, bool owns)
    {
        _storage = storage;
        _cacheBudget = cacheBytes;
        _owns = owns;
        (_initialLength, _initialGeneration) = _storage.Read(s => (s.Length, s.Generation));
    }

    /// <summary>复用 SQL 语法树缓存；超出容量时清空相关结构计划，命中路径避免全局计数锁。</summary>
    /// <param name = "sql">需要词法和语法分析的 SQL 文本。</param>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    private List<SqlStatement> Parse(string sql)
    {
        // Count 会获取 ConcurrentDictionary 的所有分区锁，缓存命中路径必须避开它。
        if (_plans.TryGetValue(sql, out var cached))
            return cached;
        if (_plans.Count > 1024)
        {
            _plans.Clear();
            _primaryPlans.Clear();
            _rangePlans.Clear();
            _scalarPlans.Clear();
            _insertColumns.Clear();
            if (_joinPlans.IsValueCreated)
                _joinPlans.Value.Clear();
            if (_dmlPlans.IsValueCreated)
                _dmlPlans.Value.Clear();
            if (_expressionPlans.IsValueCreated)
                _expressionPlans.Value.Clear();
        }

        return _plans.GetOrAdd(sql, s => new SqlParser(s).Parse());
    }

    /// <summary>向执行器提供模式定义的同一解析缓存，不暴露可变缓存到公开 API。</summary>
    /// <param name = "text">视图或触发器的持久化 SQL。</param>
    /// <returns>已解析的语句列表，调用方不得修改。</returns>
    internal List<SqlStatement> ParseSchema(string text) => Parse(text);
    /// <summary>在一致的读取快照中执行一条 SELECT，返回物化行和实际使用的执行计划。</summary>
    /// <param name = "sql">待解析及执行的 SQL 文本。</param>
    /// <param name = "parameters">命名或位置参数；可使用带前缀名称或移除首个前缀后的名称。</param>
    /// <returns>包含物化行、列名和执行计划的结果。</returns>
    /// <exception cref = "SqlException">SQL 不是一条 SELECT，或解析、绑定、计算失败。</exception>
    public SqlResult Query(string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var statements = Parse(sql);
        if (statements.Count != 1 || statements[0] is not Select select)
            throw new SqlException("Query requires one SELECT");
        parameters ??= EmptyParameters;
        if (Volatile.Read(ref _sqlTransaction) is { } transaction)
            return transaction.Query(sql, parameters)[0];
        if (_storage is Database database)
            return database.Read(static (snapshot, state) => new SqlSession(state.Owner, snapshot).Query(state.Select, state.Parameters), (Owner: this, Select: select, Parameters: parameters));
        return QueryStorage(select, parameters);
    }

    /// <summary>通过可替换存储后端在一致快照内执行查询。</summary>
    /// <param name = "select">当前已解析的 SELECT 查询结构。</param>
    /// <param name = "parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private SqlResult QueryStorage(Select select, IReadOnlyDictionary<string, object?> parameters) => _storage.Read(snapshot => new SqlSession(this, snapshot).Query(select, parameters));
    /// <summary>将本次调用中的全部 DDL/DML 语句作为一个事务提交；语法或约束失败时不提交部分结果。</summary>
    /// <param name = "sql">待解析及执行的 SQL 文本。</param>
    /// <param name = "parameters">命名或位置参数；可使用带前缀名称或移除首个前缀后的名称。</param>
    /// <param name = "cancellationToken">取消请求；已准入写入不会因取消而返回不明确的提交结果。</param>
    /// <returns>事务成功时返回直接受影响的行数。</returns>
    /// <exception cref = "SqlException">SQL 语法、类型或约束校验失败，整次调用不提交。</exception>
    public ValueTask<int> ExecuteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default) => ExecuteTransactionAsync([new(sql, parameters)], cancellationToken);
    /// <summary>按顺序执行一组带参数的命令，并原子提交全部变更；约束在写入保护期间检查。</summary>
    /// <param name = "commands">需要按顺序原子执行的 SQL 命令集合。</param>
    /// <param name = "cancellationToken">取消请求；已准入写入不会因取消而返回不明确的提交结果。</param>
    /// <returns>整批成功时返回各命令直接受影响行数之和。</returns>
    /// <exception cref = "SqlException">任一命令执行失败，整批变更不提交。</exception>
    public ValueTask<int> ExecuteTransactionAsync(IReadOnlyList<SqlCommand> commands, CancellationToken cancellationToken = default)
    {
        Dictionary<byte[], byte[]>? blobs = null;
        object? CopyParameter(object? value)
        {
            if (value is not byte[] bytes)
                return value;
            if (commands.Count == 1)
                return bytes.ToArray();
            blobs ??= new(ReferenceEqualityComparer.Instance);
            if (!blobs.TryGetValue(bytes, out var copy))
                blobs.Add(bytes, copy = bytes.ToArray());
            return copy;
        }

        var parsed = commands.Select(c => (Statements: Parse(c.Text), Parameters: c.Parameters is null ? EmptyParameters : (IReadOnlyDictionary<string, object?>)c.Parameters.ToDictionary(p => p.Key, p => CopyParameter(p.Value), StringComparer.Ordinal))).ToArray();
        if (Volatile.Read(ref _sqlTransaction) is not null || parsed.Any(c => c.Statements.Any(s => s is SqlTransactionControl)))
            return ExecuteSqlControlAsync(parsed, cancellationToken);
        return _storage.TransformAsync(snapshot =>
        {
            var session = new SqlSession(this, snapshot);
            int affected = 0;
            foreach (var command in parsed)
                foreach (var statement in command.Statements)
                    affected += session.Execute(statement, command.Parameters);
            session.ValidateForeignKeys(true);
            return (session.Mutations.Values.ToArray(), affected);
        }, cancellationToken);
    }

    /// <summary>压缩底层文件以回收历史记录占用的空间。</summary>
    /// <param name = "cancellationToken">取消请求；已准入写入不会因取消而返回不明确的提交结果。</param>
    /// <returns>完成时表示该操作结束的任务。</returns>
    public ValueTask CompactAsync(CancellationToken cancellationToken = default) => _storage.CompactAsync(cancellationToken);
    /// <summary>按模式记录版本读取或复用不可变 SQL 目录。</summary>
    /// <param name = "snapshot">在当前存储保护期内有效的已提交快照。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    internal SqlCatalog Catalog(ISqlSnapshot snapshot)
    {
        lock (_cacheGate)
        {
            if (_catalogVersion != snapshot.KeyVersion(-1) || _catalog is null)
            {
                var record = snapshot.Get(-1);
                if (record is null && snapshot.Count != 0)
                    throw new SqlException("This is a key/value database, not a SQL database; use a dedicated SQL file");
                _catalog = record is null ? new() : snapshot.HasPersistentIndexes ? SqlAst.LoadCached(record.Value) : SqlAst.Load(record.Value);
                _catalogVersion = snapshot.KeyVersion(-1);
            }

            return _catalog;
        }
    }

    /// <summary>按表和模式版本取得解码行缓存；超出预算时淘汰旧表缓存。</summary>
    /// <param name = "snapshot">在当前存储保护期内有效的已提交快照。</param>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    internal SqlTableData Table(ISqlSnapshot snapshot, SqlTable table)
    {
        lock (_cacheGate)
        {
            if (_tables.TryGetValue(table.Id, out var cached) && cached.Version == snapshot.KeyVersion(table.Id) && cached.SchemaVersion == snapshot.KeyVersion(-1))
                return cached.Data;
            var rows = new object?[snapshot.CountKey(table.Id)][];
            int at = 0;
            long bytes = 0;
            if (snapshot.HasPersistentIndexes)
                foreach (var entry in snapshot.TableEntries(table.Id))
                {
                    var value = snapshot.IndexMemory(entry);
                    rows[at++] = SqlCodec.DecodeCached(value, table.Columns.Count);
                    bytes += value.Length + table.Columns.Count * 80L + 96;
                }
            else
                foreach (long id in snapshot.Ids(table.Id))
                {
                    var value = snapshot.ValueMemory(id);
                    rows[at++] = SqlCodec.DecodeCached(value, table.Columns.Count);
                    bytes += value.Length + table.Columns.Count * 80L + 96;
                }

            var data = new SqlTableData(table, rows, bytes);
            if (_tables.Remove(table.Id, out var old))
                _cachedBytes -= old.Data.Bytes;
            if (data.Bytes <= _cacheBudget)
            {
                if (_cachedBytes + data.Bytes > _cacheBudget)
                {
                    _tables.Clear();
                    _cachedBytes = 0;
                }

                _tables[table.Id] = (snapshot.KeyVersion(table.Id), snapshot.KeyVersion(-1), data);
                _cachedBytes += data.Bytes;
            }

            return data;
        }
    }

    /// <summary>取得普通列索引范围查询的结构计划。</summary>
    /// <param name = "select">当前已解析的 SELECT 查询结构。</param>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    internal SqlRangePlan RangePlan(Select select, SqlTable table) => _rangePlans.GetOrAdd((select, table.Id, table.Indexes.Count), static (key, schema) => SqlRangePlan.Build(key.Query, schema), table);
    /// <summary>取得主键投影查询的结构计划。</summary>
    /// <param name = "select">当前已解析的 SELECT 查询结构。</param>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    internal SqlPrimaryPlan PrimaryPlan(Select select, SqlTable table) => _primaryPlans.GetOrAdd((select, table.Id), static (key, schema) => SqlPrimaryPlan.Build(key.Query, schema), table);
    /// <summary>解析并缓存插入列序号，拒绝重复列。</summary>
    /// <param name = "insert">已解析的 VALUES 插入语句。</param>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    internal int[] InsertColumns(Insert insert, SqlTable table) => _insertColumns.GetOrAdd((insert, table.Id), _ =>
    {
        int[] columns = insert.Columns?.Select(table.Column).ToArray() ?? Enumerable.Range(0, table.Columns.Count).ToArray();
        if (columns.Distinct().Count() != columns.Length)
            throw new SqlException("Duplicate insert column");
        return columns;
    });
    /// <summary>取得主键定位后的标量投影计划。</summary>
    /// <param name = "select">当前已解析的 SELECT 查询结构。</param>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    internal SqlScalarPlan ScalarPlan(Select select, SqlTable table) => _scalarPlans.GetOrAdd((select, table.Id), static (key, schema) => SqlScalarPlan.Build(key.Query, schema), table);
    /// <summary>取得主键驱动两表连接的结构计划。</summary>
    /// <param name = "select">当前已解析的 SELECT 查询结构。</param>
    /// <param name = "left">当前操作的不可变表模式。</param>
    /// <param name = "right">当前操作的不可变表模式。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    internal SqlJoinPlan JoinPlan(Select select, SqlTable left, SqlTable right) => _joinPlans.Value.GetOrAdd((select, left.Id, right.Id), static (key, schemas) => SqlJoinPlan.Build(key.Query, schemas.Left, schemas.Right), (Left: left, Right: right));
    /// <summary>取得主键更新或删除的结构计划。</summary>
    /// <param name = "statement">本次待编译或执行的已解析语句。</param>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    internal SqlDmlPlan DmlPlan(SqlStatement statement, SqlTable table) => _dmlPlans.Value.GetOrAdd((statement, table.Id), static (key, schema) => SqlDmlPlan.Build(key.Statement, schema), table);
    /// <summary>取得 JSON 表达式索引范围查询计划。</summary>
    /// <param name = "select">当前已解析的 SELECT 查询结构。</param>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    internal SqlExpressionRangePlan ExpressionPlan(Select select, SqlTable table) => _expressionPlans.Value.GetOrAdd((select, table), static key => SqlExpressionRangePlan.Build(key.Query, key.Table));
    /// <summary>仅在表版本和模式版本仍匹配时，从已解码缓存按主键查找记录。</summary>
    /// <param name = "snapshot">在当前存储保护期内有效的已提交快照。</param>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <param name = "id">原生记录 ID 或受支持的整数主键。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    internal object?[]? CachedRow(ISqlSnapshot snapshot, SqlTable table, long id)
    {
        lock (_cacheGate)
        {
            if (!_tables.TryGetValue(table.Id, out var cached) || cached.Version != snapshot.KeyVersion(table.Id) || cached.SchemaVersion != snapshot.KeyVersion(-1))
                return null;
            var rows = cached.Data.Rows;
            int lo = 0, hi = rows.Length - 1, primary = table.Primary;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                long key = (long)rows[mid][primary]!;
                if (key == id)
                    return rows[mid];
                if (key < id)
                    lo = mid + 1;
                else
                    hi = mid - 1;
            }

            return null;
        }
    }

    /// <summary>等待已准入写入并释放 SQL 缓存和索引；仅在拥有底层存储时关闭该存储。</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    /// <summary>异步等待写入完成并关闭持有的存储、SQL 缓存和索引资源。</summary>
    /// <returns>完成时表示该操作结束的任务。</returns>
    public ValueTask DisposeAsync()
    {
        lock (_closeGate)
            return new(_closeTask ??= CloseSqlAsync());
    }

    /// <summary>等待底层已准入写入结束，保存所需索引并释放本实例拥有的存储。</summary>
    /// <returns>表示操作最终完成的任务。</returns>
    private async Task CloseSqlAsync()
    {
        if (_sqlTransactionGate is { } gate)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _sqlTransaction?.Rollback();
                _sqlTransaction = null;
            }
            finally
            {
                gate.Release();
            }
        }

        try
        {
            if (_owns)
                await _storage.DrainForCloseAsync().ConfigureAwait(false);
            PrepareDiskIndexes(changedOnly: true);
        }
        finally
        {
            CloseDiskRanges();
            if (_owns)
                await _storage.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>按表版本缓存的已解码行，以及按需建立的排序、哈希和派生列索引。</summary>
/// <param name = "schema">当前操作的不可变表模式。</param>
/// <param name = "rows">已解码、按主键顺序排列的行。</param>
/// <param name = "bytes">解码行缓存的内存占用估算，单位为字节。</param>
internal sealed partial class SqlTableData(SqlTable schema, object?[][] rows, long bytes)
{
    /// <summary>本行缓存对应的不可变表模式。</summary>
    public SqlTable Schema => schema;
    /// <summary>当前表版本按主键顺序排列的解码行；不得通过公开接口暴露内部可变值。</summary>
    public object?[][] Rows => rows;
    /// <summary>解码行缓存的内存占用估算，供表缓存预算控制。</summary>
    public long Bytes => bytes;

    private readonly ConcurrentDictionary<string, (object?[][] Rows, object?[] Keys)> _expressions = new();
    private readonly ConcurrentDictionary<int, object?[][]> _ordered = new();
    private readonly ConcurrentDictionary<int, Dictionary<object, List<object?[]>>> _hash = new();
    private readonly ConcurrentDictionary<int, (object? Key, long Count)[]> _groups = new();
    /// <summary>确定性表达式计算的列缓存，随所属表版本失效；不缓存最终谓词结果。</summary>
    /// <param name = "values">按对应模式列序或函数参数顺序排列的 SQL 标量值。</param>
    internal sealed class DerivedColumn(object?[] values)
    {
        internal readonly object?[] Values = values;
        internal readonly bool Integers = values.All(v => v is null or long);
        /// <summary>适合 SIMD 整数谓词的连续数值列，另存 NULL 数量以修正统计结果。</summary>
        /// <param name = "Values">列值、行列表或唯一键映射；具体形态由当前记录的字段类型决定。</param>
        /// <param name = "NullCount">列中 SQL NULL 的数量。</param>
        internal sealed record IntegerColumn(long[] Values, int NullCount);
        private IntegerColumn? _integerColumn;
        /// <summary>按需将整数派生列转换为连续 long 数组，并单独记录 NULL 数量。</summary>
        /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
        internal IntegerColumn Numbers()
        {
            if (Volatile.Read(ref _integerColumn) is { } existing)
                return existing;
            var numbers = new long[Values.Length];
            int nulls = 0;
            for (int i = 0; i < numbers.Length; i++)
            {
                if (Values[i] is long n)
                    numbers[i] = n;
                else
                    nulls++;
            }

            var result = new IntegerColumn(numbers, nulls);
            return Interlocked.CompareExchange(ref _integerColumn, result, null) ?? result;
        }
    }

    private readonly ConcurrentDictionary<(int Column, string Path), DerivedColumn> _derived = new();
    /// <summary>取得受表版本约束的确定性 JSON 派生列缓存。</summary>
    /// <param name = "expression">待分析、绑定或求值的 SQL 表达式。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    public DerivedColumn Derived(Expr expression)
    {
        var key = (schema.Column((string)expression.Args[0].Value!), (string)expression.Args[1].Value!);
        if (_derived.TryGetValue(key, out var cached))
            return cached;
        if (_derived.Count >= 4)
            _derived.Clear();
        return _derived.GetOrAdd(key, _ =>
        {
            var bind = new SqlBinding();
            bind.Add(schema, schema.Name);
            var get = bind.Bind(expression, SqlDatabase.EmptyParameters);
            return new DerivedColumn(rows.Select(get).ToArray());
        });
    }

    /// <summary>按列取得包含 NULL 分组的有序分组计数缓存。</summary>
    /// <param name = "column">目标列在行数组中的零基序号。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public (object? Key, long Count)[] Groups(int column)
    {
        if (_groups.TryGetValue(column, out var cached))
            return cached;
        return _groups.GetOrAdd(column, BuildGroups);
    }

    /// <summary>使用 SQL 比较器统计列值出现次数，并单独保留 NULL 分组。</summary>
    /// <param name = "c">列在行数组中的零基序号。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private (object? Key, long Count)[] BuildGroups(int c)
    {
        var counts = new Dictionary<object, long>(SqlValueComparer.Instance);
        long nulls = 0;
        foreach (var row in rows)
        {
            if (row[c] is { } key)
                counts[key] = counts.GetValueOrDefault(key) + 1;
            else
                nulls++;
        }

        var result = counts.Select(p => ((object?)p.Key, p.Value)).ToList();
        if (nulls > 0)
            result.Add((null, nulls));
        var sorted = result.ToArray();
        Array.Sort(sorted, static (a, b) => SqlValueComparer.Instance.Compare(a.Item1, b.Item1));
        return sorted;
    }

    /// <summary>按列值及主键排序并缓存行引用，不复制完整行内容。</summary>
    /// <param name = "column">目标列在行数组中的零基序号。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public object?[][] Ordered(int column) => _ordered.GetOrAdd(column, c =>
    {
        var sorted = rows.ToArray();
        Array.Sort(sorted, (a, b) =>
        {
            int n = SqlValueComparer.Instance.Compare(a[c], b[c]);
            return n == 0 ? ((long)a[schema.Primary]!).CompareTo((long)b[schema.Primary]!) : n;
        });
        return sorted;
    });
    /// <summary>按非 NULL 列值创建或复用连接哈希桶。</summary>
    /// <param name = "column">目标列在行数组中的零基序号。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public Dictionary<object, List<object?[]>> Hash(int column) => _hash.GetOrAdd(column, c =>
    {
        var hash = new Dictionary<object, List<object?[]>>(SqlValueComparer.Instance);
        foreach (var row in rows)
        {
            if (row[c] is not { } key)
                continue;
            if (!hash.TryGetValue(key, out var list))
                hash[key] = list = [];
            list.Add(row);
        }

        return hash;
    });
    /// <summary>在表达式排序索引的匹配区间内按指定方向枚举行。</summary>
    /// <param name = "index">目标索引的名称、唯一性及键表达式定义。</param>
    /// <param name = "op">当前 SQL 操作符或函数名。</param>
    /// <param name = "value">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <param name = "high">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <param name = "descending">是否按降序枚举结果。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public IEnumerable<object?[]> SeekExpression(SqlIndex index, string op, object? value, object? high, bool descending)
    {
        var range = ExpressionRange(index, op, value, high);
        if (descending)
            for (int i = range.End - 1; i >= range.Begin; i--)
                yield return range.Rows[i];
        else
            for (int i = range.Begin; i < range.End; i++)
                yield return range.Rows[i];
    }

    /// <summary>按需计算并排序受支持索引表达式的值，缓存键与行的对应关系。</summary>
    /// <param name = "index">目标索引的名称、唯一性及键表达式定义。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private (object?[][] Rows, object?[] Keys) Expression(SqlIndex index)
    {
        if (_expressions.TryGetValue(index.Name, out var cached))
            return cached;
        return _expressions.GetOrAdd(index.Name, _ =>
        {
            var binding = new SqlBinding();
            binding.Add(schema, schema.Name);
            var get = binding.Bind(index.Expression!, SqlDatabase.EmptyParameters);
            var pairs = rows.Select(row => (Row: row, Key: get(row))).ToArray();
            Array.Sort(pairs, (a, b) =>
            {
                int n = SqlValueComparer.Instance.Compare(a.Key, b.Key);
                return n == 0 ? ((long)a.Row[schema.Primary]!).CompareTo((long)b.Row[schema.Primary]!) : n;
            });
            return (pairs.Select(p => p.Row).ToArray(), pairs.Select(p => p.Key).ToArray());
        });
    }

    /// <summary>对表达式键执行二分查找，计算符合 SQL NULL 语义的半开行区间。</summary>
    /// <param name = "index">目标索引的名称、唯一性及键表达式定义。</param>
    /// <param name = "op">当前 SQL 操作符或函数名。</param>
    /// <param name = "value">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <param name = "high">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    internal (object?[][] Rows, int Begin, int End) ExpressionRange(SqlIndex index, string op, object? value, object? high)
    {
        var data = Expression(index);
        if (value is null || op == "BETWEEN" && high is null)
            return (data.Rows, 0, 0);
        int Bound(object? key, bool after)
        {
            int lo = 0, hi = data.Keys.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                int n = SqlValueComparer.Instance.Compare(data.Keys[mid], key);
                if (n < 0 || after && n == 0)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo;
        }

        int begin = op switch
        {
            "=" or ">=" or "BETWEEN" => Bound(value, false),
            ">" => Bound(value, true),
            _ => Bound(null, true)
        };
        int end = op switch
        {
            "=" or "<=" => Bound(value, true),
            "<" => Bound(value, false),
            "BETWEEN" => Bound(high, true),
            _ => data.Rows.Length
        };
        return (data.Rows, begin, end);
    }

    /// <summary>在普通列排序索引中二分定位范围，并按指定方向枚举匹配行。</summary>
    /// <param name = "column">目标列在行数组中的零基序号。</param>
    /// <param name = "op">当前 SQL 操作符或函数名。</param>
    /// <param name = "value">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <param name = "high">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <param name = "descending">是否按降序枚举结果。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public IEnumerable<object?[]> Seek(int column, string op, object? value, object? high = null, bool descending = false)
    {
        if (value is null || op == "BETWEEN" && high is null)
            yield break;
        var sorted = Ordered(column);
        var cmp = SqlValueComparer.Instance;
        int Bound(object? key, bool after)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2;
                int n = cmp.Compare(sorted[mid][column], key);
                if (n < 0 || after && n == 0)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            return lo;
        }

        int begin = op switch
        {
            "=" or ">=" or "BETWEEN" => Bound(value, false),
            ">" => Bound(value, true),
            _ => Bound(null, true)
        };
        int end = op switch
        {
            "=" or "<=" => Bound(value, true),
            "<" => Bound(value, false),
            "BETWEEN" => Bound(high, true),
            _ => sorted.Length
        };
        if (descending)
            for (int i = end - 1; i >= begin; i--)
                yield return sorted[i];
        else
            for (int i = begin; i < end; i++)
                yield return sorted[i];
    }
}

/// <summary>一次快照或显式事务中的 SQL 执行状态；维护私有变更、模式副本和约束映射。</summary>
/// <param name = "owner">拥有函数注册和计划缓存的 SQL 数据库。</param>
/// <param name = "snapshot">在当前读取保护期内有效的存储快照。</param>
internal sealed partial class SqlSession(SqlDatabase owner, ISqlSnapshot snapshot)
{
    private Dictionary<long, Mutation>? _mutations;
    /// <summary>当前会话私有的最终变更映射，按需分配并在提交时整体编码。</summary>
    public Dictionary<long, Mutation> Mutations => _mutations ??= [];
    /// <summary>当前会话是否存在尚未提交的记录变更。</summary>
    private bool HasMutations => _mutations is { Count: > 0 };

    private SqlCatalog? _catalog;
    /// <summary>当前会话可见的模式目录；DDL 修改前保持共享目录不可变。</summary>
    private SqlCatalog Catalog => _catalog ??= owner.Catalog(snapshot);

    /// <summary>将表 ID 与主键组合为底层记录 ID，校验整数主键范围。</summary>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <param name = "id">原生记录 ID 或受支持的整数主键。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    private static long StorageId(SqlTable table, long id)
    {
        if (id < 0 || id >= 1L << 40)
            throw new SqlException("INTEGER PRIMARY KEY must be in [0, 2^40)");
        return ((long)table.Id << 40) | id;
    }

    /// <summary>优先从会话暂存变更读取记录，再读取已提交快照。</summary>
    /// <param name = "id">原生记录 ID 或受支持的整数主键。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private DbRecord? Get(long id)
    {
        if (_mutations is not null && _mutations.TryGetValue(id, out var m))
            return m.IsDelete ? null : new(m.Id, m.IndexKey, m.Value.ToArray());
        return snapshot.Get(id);
    }

    /// <summary>按表和主键读取当前会话可见行，优先处理未提交更新及删除。</summary>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <param name = "id">原生记录 ID 或受支持的整数主键。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private object?[]? Row(SqlTable table, long id)
    {
        long key = StorageId(table, id);
        if (_mutations is not null && _mutations.TryGetValue(key, out var mutation))
            return mutation.IsDelete ? null : SqlCodec.Decode(mutation.Value.Span, table.Columns.Count);
        if (owner.CachedRow(snapshot, table, id) is { } cached)
            return cached;
        var value = snapshot.Value(key);
        return value.IsEmpty ? null : SqlCodec.Decode(value, table.Columns.Count);
    }

    /// <summary>按主键只解码请求的投影列，覆盖索引查询可直接返回主键。</summary>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <param name = "id">原生记录 ID 或受支持的整数主键。</param>
    /// <param name = "columns">需要读取并输出的零基列序号，允许重复。</param>
    /// <param name = "indexOnly">是否可以仅从主键索引得到结果，无需读取行载荷。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private object?[]? ProjectRow(SqlTable table, long id, int[] columns, bool indexOnly)
    {
        long key = StorageId(table, id);
        if (_mutations is not null && _mutations.TryGetValue(key, out var mutation))
            return mutation.IsDelete ? null : SqlCodec.DecodeProjection(mutation.Value.Span, table.Columns.Count, columns);
        if (indexOnly && snapshot.HasPersistentIndexes)
        {
            if (!snapshot.Contains(key))
                return null;
            var row = new object?[columns.Length];
            object boxed = id;
            Array.Fill(row, boxed);
            return row;
        }

        var value = snapshot.Value(key);
        return value.IsEmpty ? null : SqlCodec.DecodeProjection(value, table.Columns.Count, columns);
    }

    /// <summary>将已提交行与当前事务变更合并；没有变更时复用表版本缓存。</summary>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private SqlTableData Data(SqlTable table)
    {
        if (table.Id < 0)
            return _virtualData![table.Id];
        if (_extendedState?.MutatedTables is { } changedTables && !changedTables.Contains(table.Id))
            return owner.Table(snapshot, table);
        if (!HasMutations)
            return owner.Table(snapshot, table);
        var changes = Mutations.Values.Where(m => m.Id >= 0 && (m.Id >> 40) == table.Id).ToArray();
        if (changes.Length == 0)
            return owner.Table(snapshot, table);
        var map = snapshot.Range(table.Id, table.Id).ToDictionary(r => r.Id, r => SqlCodec.Decode(r.Value, table.Columns.Count));
        foreach (var m in changes)
        {
            if (m.IsDelete)
                map.Remove(m.Id);
            else
                map[m.Id] = SqlCodec.Decode(m.Value.ToArray(), table.Columns.Count);
        }

        return new(table, map.OrderBy(p => p.Key).Select(p => p.Value).ToArray(), 0);
    }

    /// <summary>克隆当前模式目录，应用 DDL 并将新目录作为同一事务的变更暂存。</summary>
    /// <param name = "change">对私有模式副本执行的变更。</param>
    private void ChangeCatalog(Action<SqlCatalog> change)
    {
        var copy = SqlAst.Load(JsonSerializer.SerializeToUtf8Bytes(Catalog, SqlCatalogJsonContext.Default.SqlCatalog));
        change(copy);
        _catalog = copy;
        _uniqueMaps?.Clear();
        Stage(Mutation.Put(-1, -1, JsonSerializer.SerializeToUtf8Bytes(copy, SqlCatalogJsonContext.Default.SqlCatalog)));
    }

    /// <summary>按列声明规范化输入值，并执行类型及 NOT NULL 校验。</summary>
    /// <param name = "column">目标列的类型及约束定义。</param>
    /// <param name = "value">写入目标列之前需要规范化并检查约束的 SQL 值。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private static object? Coerce(SqlColumn column, object? value)
    {
        value = SqlJsonFunctions.Unwrap(SqlValueComparer.Normalize(value));
        if (value is null)
        {
            if (column.NotNull || column.Primary)
                throw new SqlException($"{column.Name} cannot be NULL");
            return null;
        }

        return column.Type switch
        {
            "JSON" => SqlJsonFunctions.Canonical(value),
            "JSONB" => SqlJsonFunctions.Binary(value),
            "INTEGER" when value is long => value,
            "REAL" when value is long or double => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
            "TEXT" when value is string => value,
            "BLOB" when value is byte[] => value,
            _ => throw new SqlException($"Type mismatch for {column.Name}: expected {column.Type}")
        };
    }

    /// <summary>一个唯一约束的取值函数和当前事务可见的值到主键映射。</summary>
    /// <param name = "Get">从一行计算唯一约束键的委托。</param>
    /// <param name = "Values">列值、行列表或唯一键映射；具体形态由当前记录的字段类型决定。</param>
    private sealed record UniqueMap(Func<object?[], object?> Get, Dictionary<object, long> Values);
    private Dictionary<int, List<UniqueMap>>? _uniqueMaps;
    /// <summary>构建或复用当前事务可见的唯一键到主键映射，SQL NULL 不参与冲突。</summary>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    private List<UniqueMap> UniqueConstraints(SqlTable table)
    {
        _uniqueMaps ??= [];
        if (_uniqueMaps.TryGetValue(table.Id, out var maps))
            return maps;
        maps = [];
        var cols = table.Columns.Select((c, i) => (c, i)).Where(p => p.c.Unique && !p.c.Primary).Select(p => p.i).Concat(table.Indexes.Where(i => i.Unique && i.Expression is null && table.Column(i.Column) != table.Primary).Select(i => table.Column(i.Column))).Distinct();
        foreach (int column in cols)
            maps.Add(new(r => r[column], new(SqlValueComparer.Instance)));
        foreach (var index in table.Indexes.Where(i => i.Unique && i.Expression is not null))
        {
            var binding = new SqlBinding(owner, this);
            binding.Add(table, table.Name);
            maps.Add(new(binding.Bind(index.Expression!, SqlDatabase.EmptyParameters), new(SqlValueComparer.Instance)));
        }

        if (maps.Count > 0)
            foreach (var other in Data(table).Rows)
                foreach (var map in maps)
                    if (map.Get(other) is { } value)
                        map.Values.Add(value, (long)other[table.Primary]!);
        _uniqueMaps[table.Id] = maps;
        return maps;
    }

    /// <summary>在暂存行之前检查类型、主键和唯一约束，并一致地更新约束映射。</summary>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <param name = "row">待插入或更新的完整行；存储前校验列类型和约束。</param>
    /// <param name = "replacing">更新前的主键；null 表示插入新记录。</param>
    private void Store(SqlTable table, object?[] row, long? replacing = null)
    {
        for (int i = 0; i < row.Length; i++)
            row[i] = Coerce(table.Columns[i], row[i]);
        long id = (long)row[table.Primary]!;
        long key = StorageId(table, id);
        if (replacing != id && Get(key) is not null)
            throw new SqlException("PRIMARY KEY constraint failed");
        IReadOnlyList<UniqueMap> constraints = table.HasUniqueConstraints ? UniqueConstraints(table) : Array.Empty<UniqueMap>();
        object?[] pending = constraints.Count == 0 ? [] : new object?[constraints.Count];
        for (int i = 0; i < constraints.Count; i++)
        {
            var state = constraints[i];
            object? value = state.Get(row);
            if (value is not null && state.Values.TryGetValue(value, out long ownerId) && ownerId != replacing)
                throw new SqlException("UNIQUE constraint failed");
            pending[i] = value;
        }

        foreach (var index in table.Indexes)
        {
            if (index.Unique || index.Expression is null)
                continue;
            var getters = (_extendedState ??= new()).IndexGetters ??= new(StringComparer.OrdinalIgnoreCase);
            if (!getters.TryGetValue(index.Name, out var get))
            {
                var bind = new SqlBinding(owner, this);
                bind.Add(table, table.Name);
                getters[index.Name] = get = bind.Bind(index.Expression!, SqlDatabase.EmptyParameters);
            }

            get(row);
        }

        object?[]? previous = constraints.Count > 0 && replacing is { } previousId ? Row(table, previousId) : null;
        for (int i = 0; i < constraints.Count; i++)
        {
            var state = constraints[i];
            var value = pending[i];
            if (previous is not null && state.Get(previous) is { } oldValue)
                state.Values.Remove(oldValue);
            if (value is not null)
                state.Values[value] = id;
        }

        if (replacing is { } old && old != id)
            Stage(Mutation.Delete(StorageId(table, old)));
        Stage(Mutation.Put(key, table.Id, SqlCodec.Encode(row)));
    }

    /// <summary>选择安全的主键 DML 专用路径，其他结构回退到通用执行器。</summary>
    /// <param name = "statement">本次待编译或执行的已解析语句。</param>
    /// <param name = "parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    private int ExecuteCore(SqlStatement statement, IReadOnlyDictionary<string, object?> parameters)
    {
        if (!Catalog.ManagedWrites && statement is Update update && !ExtendedExpression(update.Where) && !update.Assignments.Any(a => ExtendedExpression(a.Value)) && !owner.UsesFunction(update.Where) && !update.Assignments.Any(a => owner.UsesFunction(a.Value)))
        {
            var table = Catalog.Table(update.Table);
            if (owner.DmlPlan(update, table) is { Supported: true } plan)
                return ExecutePrimaryDml(plan, table, parameters);
        }
        else if (!Catalog.ManagedWrites && statement is Delete delete && !ExtendedExpression(delete.Where) && !owner.UsesFunction(delete.Where))
        {
            var table = Catalog.Table(delete.Table);
            if (owner.DmlPlan(delete, table) is { Supported: true } plan)
                return ExecutePrimaryDml(plan, table, parameters);
        }

        return ExecuteGeneral(statement, parameters);
    }

    /// <summary>执行 DDL/DML 并仅暂存变更；整个调用成功后由存储层原子提交。</summary>
    /// <param name = "statement">本次待编译或执行的已解析语句。</param>
    /// <param name = "parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    private int ExecuteGeneral(SqlStatement statement, IReadOnlyDictionary<string, object?> parameters)
    {
        switch (statement)
        {
            case CreateTable c:
                if (Catalog.Views.Any(v => v.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new SqlException("Schema object already exists");
                if (Catalog.Tables.Any(t => t.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    if (c.IfNotExists)
                        return 0;
                    throw new SqlException("Table already exists");
                }

                foreach (var column in c.Columns)
                    if (column.Default is not null)
                    {
                        static bool ConstantDefault(Expr e) => e.Op != "parameter" && e.Op != "column" && e.Subquery is null && e.Window is null && e.Args.All(ConstantDefault);
                        if (!ConstantDefault(column.Default) || column.Default.Value is byte[])
                            throw new SqlException("DEFAULT requires a numeric/text/NULL constant expression");
                        if (new SqlBinding().Bind(column.Default, SqlDatabase.EmptyParameters)([]) is byte[])
                            throw new SqlException("BLOB DEFAULT expressions are not supported");
                    }

                foreach (var foreign in c.ForeignKeys)
                    foreach (string column in foreign.Columns)
                        if (!c.Columns.Any(col => col.Name.Equals(column, StringComparison.OrdinalIgnoreCase)))
                            throw new SqlException("Unknown foreign-key column");
                ChangeCatalog(catalog =>
                {
                    if (catalog.NextId >= 1 << 23)
                        throw new SqlException("Table identifier space exhausted");
                    catalog.Tables.Add(new()
                    {
                        Id = catalog.NextId++,
                        Name = c.Name,
                        Columns = c.Columns,
                        ForeignKeys = c.ForeignKeys
                    });
                    var table = catalog.Table(c.Name);
                    for (int i = 0; i < c.UniqueColumns.Count; i++)
                    {
                        foreach (string column in c.UniqueColumns[i])
                            table.Column(column);
                        table.Indexes.Add(new("puredb_unique_" + table.Id + "_" + i, "", true, new("composite", Items: c.UniqueColumns[i].Select(n => new Expr("column", n)).ToArray()))
                        {
                            Descending = new bool[c.UniqueColumns[i].Length]
                        });
                    }
                });
                return 0;
            case CreateIndex c:
                if (Catalog.Tables.Any(t => t.Indexes.Any(i => i.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))))
                {
                    if (c.IfNotExists)
                        return 0;
                    throw new SqlException("Index already exists");
                }

                var indexed = Catalog.Table(c.Table);
                var indexBinding = new SqlBinding(owner, this);
                indexBinding.Add(indexed, indexed.Name);
                static bool Allowed(Expr e) => e.Op == "column" || e.Op == "JSON_EXTRACT" && e.Args.Length == 2 && e.Args[0].Op == "column" && e.Args[1].Op == "literal" && e.Args[1].Value is string;
                if (!(c.Expression.Op == "composite" ? c.Expression.Args.All(Allowed) : Allowed(c.Expression)))
                    throw new SqlException("Indexes support a column or JSON_EXTRACT(column, literal_path)");
                var indexGet = indexBinding.Bind(c.Expression, SqlDatabase.EmptyParameters);
                string indexColumn = c.Expression.Op == "column" ? ((string)c.Expression.Value!).Split('.')[^1] : "";
                var seenIndexValues = c.Unique ? new HashSet<object?>(SqlValueComparer.Instance) : null;
                foreach (var row in Data(indexed).Rows)
                {
                    object? value = indexGet(row);
                    if (c.Unique && value is not null && !seenIndexValues!.Add(value))
                        throw new SqlException("UNIQUE constraint failed");
                }

                ChangeCatalog(catalog => catalog.Table(c.Table).Indexes.Add(new(c.Name, indexColumn, c.Unique, c.Expression.Op == "column" ? null : c.Expression) { Descending = c.Descending }));
                return 0;
            case Drop d:
                if (d.Kind is "VIEW" or "TRIGGER")
                    return DropRelation(d);
                if (d.Kind == "TABLE")
                {
                    var table = Catalog.Tables.FirstOrDefault(t => t.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
                    if (table is null)
                    {
                        if (d.IfExists)
                            return 0;
                        throw new SqlException("Unknown table");
                    }

                    foreach (var row in Data(table).Rows)
                    {
                        long key = StorageId(table, (long)row[table.Primary]!);
                        if (Catalog.ManagedWrites)
                            DeleteRow(table, row, false);
                        else
                            Stage(Mutation.Delete(key));
                    }

                    ChangeCatalog(c =>
                    {
                        c.Tables.RemoveAll(t => t.Id == table.Id);
                        c.Triggers.RemoveAll(t => t.Table.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
                    });
                }
                else
                {
                    if (!Catalog.Tables.Any(t => t.Indexes.Any(i => i.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase))) && !d.IfExists)
                        throw new SqlException("Unknown index");
                    ChangeCatalog(c =>
                    {
                        foreach (var table in c.Tables)
                            table.Indexes.RemoveAll(i => i.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
                    });
                }

                return 0;
            case Insert insert:
                if (Catalog.Views.Any(v => v.Name.Equals(insert.Table, StringComparison.OrdinalIgnoreCase)))
                    return WriteView(insert, parameters);
                var target = Catalog.Table(insert.Table);
                int[] columns = owner.InsertColumns(insert, target);
                int inserted = 0;
                var queryRows = insert.Query is null ? null : Query(insert.Query, parameters).Rows;
                string[] changedColumns = Catalog.ManagedWrites ? columns.Select(i => target.Columns[i].Name).ToArray() : [];
                bool defaults = target.Columns.Any(c => c.Default is not null);
                for (int rowIndex = 0; rowIndex < (queryRows?.Count ?? insert.Values.Count); rowIndex++)
                {
                    var queryRow = queryRows?[rowIndex];
                    var expressions = queryRows is null ? insert.Values[rowIndex] : null;
                    if ((queryRow?.Length ?? expressions!.Length) != columns.Length)
                        throw new SqlException("INSERT column/value count mismatch");
                    var row = new object?[target.Columns.Count];
                    if (defaults)
                        for (int i = 0; i < row.Length; i++)
                            if (target.Columns[i].Default is { } defaultValue)
                                row[i] = defaultValue.Op == "literal" ? defaultValue.Value : new SqlBinding(owner, this).Bind(defaultValue, SqlDatabase.EmptyParameters)([]);
                    for (int i = 0; i < columns.Length; i++)
                        row[columns[i]] = queryRow is not null ? queryRow[i] : expressions![i].Op switch
                        {
                            "literal" => expressions[i].Value,
                            "parameter" => SqlPrimaryPlan.Parameter(expressions[i], parameters),
                            _ => new SqlBinding(owner, this).Bind(expressions[i], parameters)([])
                        };
                    if (row[target.Primary] is null)
                    {
                        _automaticIds ??= [];
                        if (!_automaticIds.TryGetValue(target.Id, out long cachedMaximum))
                            cachedMaximum = Data(target).Rows.Select(r => (long)r[target.Primary]!).DefaultIfEmpty(0).Max();
                        row[target.Primary] = cachedMaximum + 1;
                        _automaticIds[target.Id] = cachedMaximum + 1;
                    }

                    WriteRow(target, row, null, changedColumns);
                    inserted++;
                }

                return inserted;
            case Update update:
                if (Catalog.Views.Any(v => v.Name.Equals(update.Table, StringComparison.OrdinalIgnoreCase)))
                    return WriteView(update, parameters);
                var updated = Catalog.Table(update.Table);
                var binding = new SqlBinding(owner, this);
                binding.Add(updated, updated.Name);
                var assignments = update.Assignments.Select(a => (Column: updated.Column(a.Column), Value: binding.Bind(a.Value, parameters))).ToArray();
                if (assignments.Select(a => a.Column).Distinct().Count() != assignments.Length)
                    throw new SqlException("Duplicate assignment");
                var predicate = update.Where is null ? null : binding.Bind(update.Where, parameters);
                string[] updatedColumns = Catalog.ManagedWrites ? update.Assignments.Select(a => a.Column).ToArray() : [];
                int affected = 0;
                foreach (var original in Candidates(updated, updated.Name, update.Where, [], parameters, out _, out _).ToArray())
                {
                    if (predicate is not null && SqlValueComparer.Truth(predicate(original)) != true)
                        continue;
                    var row = original.ToArray();
                    foreach (var a in assignments)
                        row[a.Column] = a.Value(original);
                    WriteRow(updated, row, original, updatedColumns);
                    affected++;
                }

                return affected;
            case Delete delete:
                if (Catalog.Views.Any(v => v.Name.Equals(delete.Table, StringComparison.OrdinalIgnoreCase)))
                    return WriteView(delete, parameters);
                var deleted = Catalog.Table(delete.Table);
                var dbind = new SqlBinding(owner, this);
                dbind.Add(deleted, deleted.Name);
                var filter = delete.Where is null ? null : dbind.Bind(delete.Where, parameters);
                int removed = 0;
                foreach (var row in Candidates(deleted, deleted.Name, delete.Where, [], parameters, out _, out _).ToArray())
                {
                    if (filter is not null && SqlValueComparer.Truth(filter(row)) != true)
                        continue;
                    DeleteRow(deleted, row);
                    removed++;
                }

                return removed;
            default:
                if (statement is CreateView or CreateTrigger)
                    return CreateRelation(statement);
                throw new SqlException("ExecuteAsync accepts DDL/DML only. Use Query for SELECT; BEGIN/COMMIT are replaced by ExecuteTransactionAsync.");
        }
    }
}
