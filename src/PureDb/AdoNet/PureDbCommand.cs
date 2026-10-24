using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace PureDb.AdoNet;
/// <summary>执行参数化文本 SQL 的 ADO.NET 命令；同一命令实例不能并发执行。</summary>
public sealed class PureDbCommand : DbCommand
{
    private readonly PureDbParameterCollection _parameters = new();
    private PureDbConnection? _connection;
    private PureDbTransaction? _transaction;
    private CancellationTokenSource? _running;
    private readonly object _cancelGate = new();
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    /// <summary>创建文本 SQL 命令，可同时指定目标连接。</summary>
    public PureDbCommand()
    {
    }

    /// <summary>创建文本 SQL 命令，可同时指定目标连接。</summary>
    /// <param name="text">待执行的 SQL 文本。</param>
    /// <param name="connection">执行命令的连接。</param>
    public PureDbCommand(string text, PureDbConnection connection)
    {
        CommandText = text;
        Connection = connection;
    }

    /// <summary>要执行的 SQL 文本；读取器接受 SELECT，非查询接口接受 DDL/DML。</summary>
    [AllowNull]
    public override string CommandText { get; set; } = "";
    /// <summary>写入等待的超时秒数，0 表示不设置超时；当前同步查询不支持执行中抢占取消。</summary>
    public override int CommandTimeout { get; set; } = 30;
    /// <summary>命令类型；执行时仅接受 Text。</summary>
    public override CommandType CommandType { get; set; } = CommandType.Text;
    /// <summary>是否向设计时工具显示该命令。</summary>
    public override bool DesignTimeVisible
    {
        get; set;
    }
    /// <summary>数据适配器更新后的回填方式；默认为 None。</summary>
    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.None;
    /// <summary>执行命令的连接；只接受 PureDbConnection 及其派生类型。</summary>
    protected override DbConnection? DbConnection
    {
        get => _connection; set => _connection = value is null or PureDbConnection ? (PureDbConnection?)value : throw new ArgumentException("Expected PureDbConnection");
    }
    /// <summary>命令所属事务；必须与连接当前活动事务一致。</summary>
    protected override DbTransaction? DbTransaction
    {
        get => _transaction; set => _transaction = value is null or PureDbTransaction ? (PureDbTransaction?)value : throw new ArgumentException("Expected PureDbTransaction");
    }
    /// <summary>供 ADO.NET 基类访问的输入参数集合。</summary>
    protected override DbParameterCollection DbParameterCollection => _parameters;
    /// <summary>命令的强类型连接。</summary>
    public new PureDbConnection? Connection
    {
        get => _connection; set => _connection = value;
    }
    /// <summary>命令的强类型输入参数集合；重复参数名在执行时被拒绝。</summary>
    public new PureDbParameterCollection Parameters => _parameters;

    /// <summary>请求取消当前执行；已准入写入仍等待明确的提交结果，同步查询不能被抢占。</summary>
    public override void Cancel()
    {
        lock (_cancelGate)
            _running?.Cancel();
    }

    /// <summary>解析并缓存 SQL 语法；参数值及最终模式绑定在执行时检查。</summary>
    public override void Prepare()
    {
        _ = Validate();
        _connection!.Engine.Prepare(CommandText);
    }

    /// <summary>创建可添加到当前命令的输入参数。</summary>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    protected override DbParameter CreateDbParameter() => new PureDbParameter();
    /// <summary>检查连接、命令类型和活动事务归属，返回适用的显式事务会话。</summary>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private SqlDatabase.TransactionSession? Validate()
    {
        if (CommandType != CommandType.Text)
            throw new NotSupportedException("Only CommandType.Text is supported");
        if (CommandTimeout < 0)
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        if (string.IsNullOrWhiteSpace(CommandText))
            throw new InvalidOperationException("CommandText is empty");
        _ = _connection?.Engine ?? throw new InvalidOperationException("Command requires an open connection");
        if (!ReferenceEquals(_transaction, _connection.ActiveTransaction))
            throw new InvalidOperationException("Command must use the connection's active transaction");
        return _transaction?.Session;
    }

    /// <summary>规范化输入参数名称与常见 ADO.NET 标量，拒绝重复名称和非输入方向。</summary>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private IReadOnlyDictionary<string, object?> Values()
    {
        if (_parameters.Count == 0)
            return SqlDatabase.EmptyParameters;
        var values = _values;
        values.Clear();
        int position = 0;
        foreach (PureDbParameter p in _parameters)
        {
            if (p.Direction != ParameterDirection.Input)
                throw new NotSupportedException("Only input parameters are supported");
            position++;
            string name = PureDbParameterCollection.Name(p.ParameterName);
            if (name.Length == 0)
                name = position.ToString(CultureInfo.InvariantCulture);
            object? value = p.Value is DBNull ? null : p.Value;
            value = value switch
            {
                DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
                DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
                Guid g => g.ToString(),
                _ => value
            };
            if (!values.TryAdd(name, value))
                throw new ArgumentException("Duplicate parameter: " + name);
        }

        return values;
    }

    /// <summary>创建关联取消源并设置超时，拒绝同一命令实例的并发执行。</summary>
    /// <param name="token">取消请求；原子提交开始后需完成提交或回滚。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private CancellationTokenSource Begin(CancellationToken token)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (CommandTimeout > 0)
            source.CancelAfter(TimeSpan.FromSeconds(CommandTimeout));
        lock (_cancelGate)
        {
            if (_running is not null)
            {
                source.Dispose();
                throw new InvalidOperationException("Command already executing");
            }

            _running = source;
        }

        return source;
    }

    /// <summary>清除当前运行中的取消源引用，允许后续再次执行命令。</summary>
    /// <param name="source">本次执行关联的取消源。</param>
    private void End(CancellationTokenSource source)
    {
        lock (_cancelGate)
        {
            if (ReferenceEquals(_running, source))
                _running = null;
        }
    }

    /// <summary>同步执行原子 DDL/DML 命令并返回直接受影响的行数。</summary>
    /// <returns>直接受影响的行数。</returns>
    public override int ExecuteNonQuery() => ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();
    /// <summary>异步提交 DDL/DML 命令并返回受影响行数；显式事务中先暂存变更。</summary>
    /// <param name="token">取消请求；执行中的同步计算不能被抢占。</param>
    /// <returns>完成时提供直接受影响行数的任务。</returns>
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken token)
    {
        var session = Validate();
        using var source = Begin(token);
        try
        {
            source.Token.ThrowIfCancellationRequested();
            var values = Values();
            return session is null ? await _connection!.Engine.ExecuteAsync(CommandText, values, source.Token).ConfigureAwait(false) : session.Execute(CommandText, values);
        }
        finally
        {
            End(source);
        }
    }

    /// <summary>验证并执行查询，返回不持有存储锁的物化读取器。</summary>
    /// <param name="behavior">ADO.NET 读取器的结果、行数及连接关闭行为。</param>
    /// <param name="token">取消请求；原子提交开始后需完成提交或回滚。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private DbDataReader Reader(CommandBehavior behavior, CancellationToken token)
    {
        if ((behavior & CommandBehavior.KeyInfo) != 0)
            throw new NotSupportedException("KeyInfo metadata is not implemented");
        var session = Validate();
        using var source = Begin(token);
        try
        {
            source.Token.ThrowIfCancellationRequested();
            var values = Values();
            var result = session is null ? _connection!.Engine.QueryResults(CommandText, values) : session.Query(CommandText, values);
            source.Token.ThrowIfCancellationRequested();
            return new PureDbDataReader(result, behavior, _connection!);
        }
        finally
        {
            End(source);
        }
    }

    /// <summary>执行 SELECT 并返回物化结果的读取器；枚举行时不持有数据库锁。</summary>
    /// <param name="behavior">读取器行为，包括 SchemaOnly、SingleRow、SingleResult 或 CloseConnection。</param>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => Reader(behavior, CancellationToken.None);
    /// <summary>检查取消并执行物化查询；当前查询计算仍在调用线程执行。</summary>
    /// <param name="behavior">读取器行为，包括 SchemaOnly、SingleRow、SingleResult 或 CloseConnection。</param>
    /// <param name="token">取消请求；执行中的同步计算不能被抢占。</param>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken token) => Task.FromResult(Reader(behavior, token));
    /// <summary>返回第一结果集第一行第一列；没有行时为 null，SQL NULL 为 DBNull.Value。</summary>
    /// <returns>第一行第一列的值；无行时为 null，SQL NULL 为 DBNull.Value。</returns>
    public override object? ExecuteScalar()
    {
        using var r = ExecuteReader(CommandBehavior.SingleResult | CommandBehavior.SingleRow);
        return r.Read() && r.FieldCount > 0 ? r.GetValue(0) : null;
    }

    /// <summary>检查取消并返回第一行第一列；没有行时为 null，SQL NULL 为 DBNull.Value。</summary>
    /// <param name="token">取消请求；执行中的同步计算不能被抢占。</param>
    /// <returns>完成时提供首个标量结果的任务；无行时为 null，SQL NULL 为 DBNull.Value。</returns>
    public override async Task<object?> ExecuteScalarAsync(CancellationToken token)
    {
        await using var r = await ExecuteReaderAsync(CommandBehavior.SingleResult | CommandBehavior.SingleRow, token).ConfigureAwait(false);
        return await r.ReadAsync(token).ConfigureAwait(false) && r.FieldCount > 0 ? r.GetValue(0) : null;
    }
}
