namespace PureDb;
/// <summary>SQL 事务控制节点，不直接生成数据记录。</summary>
/// <param name = "Action">BEGIN、COMMIT、ROLLBACK、SAVEPOINT、ROLLBACK TO 或 RELEASE。</param>
/// <param name = "Name">保存点名称。</param>
/// <param name = "Mode">BEGIN 模式。</param>
internal sealed record SqlTransactionControl(string Action, string? Name = null, string Mode = "DEFERRED") : SqlStatement;
/// <remarks>本部分提供跨调用 SQL 事务控制和命名保存点。</remarks>
public sealed partial class SqlDatabase
{
    private TransactionSession? _sqlTransaction;
    private SemaphoreSlim? _sqlTransactionGate;
    private bool _savepointTransaction;
    /// <summary>模式回滚后清空依赖表 ID 和列位置的计划，保留纯语法解析缓存。</summary>
    internal void InvalidateStructurePlans()
    {
        _primaryPlans.Clear();
        _scalarPlans.Clear();
        _rangePlans.Clear();
        _insertColumns.Clear();
        if (_joinPlans.IsValueCreated)
            _joinPlans.Value.Clear();
        if (_dmlPlans.IsValueCreated)
            _dmlPlans.Value.Clear();
        if (_expressionPlans.IsValueCreated)
            _expressionPlans.Value.Clear();
        lock (_cacheGate)
            _descriptions.Clear();
    }

    /// <summary>按 SQL 控制语句边界执行命令，普通调用继续使用原有原子批处理路径。</summary>
    /// <param name = "commands">已经解析并复制参数的命令。</param>
    /// <param name = "token">取消请求。</param>
    /// <returns>直接受影响的行数。</returns>
    private async ValueTask<int> ExecuteSqlControlAsync((List<SqlStatement> Statements, IReadOnlyDictionary<string, object?> Parameters)[] commands, CancellationToken token)
    {
        var gate = LazyInitializer.EnsureInitialized(ref _sqlTransactionGate, static () => new(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            int affected = 0;
            foreach (var command in commands)
                foreach (var statement in command.Statements)
                {
                    token.ThrowIfCancellationRequested();
                    if (statement is SqlTransactionControl control)
                    {
                        switch (control.Action)
                        {
                            case "BEGIN":
                                if (_sqlTransaction is not null)
                                    throw new SqlException("Cannot BEGIN within an active transaction");
                                if (control.Mode != "DEFERRED" && _storage is Database { Access: DatabaseAccess.Shared })
                                    throw new NotSupportedException("BEGIN IMMEDIATE/EXCLUSIVE requires Exclusive file access; Shared uses optimistic DEFERRED transactions");
                                _sqlTransaction = BeginSession();
                                _savepointTransaction = false;
                                break;
                            case "SAVEPOINT":
                                if (_sqlTransaction is null)
                                {
                                    _sqlTransaction = BeginSession();
                                    _savepointTransaction = true;
                                }

                                _sqlTransaction.Save(control.Name!);
                                break;
                            case "ROLLBACK TO":
                                if (_sqlTransaction is null)
                                    throw new SqlException("No active transaction");
                                _sqlTransaction.RollbackTo(control.Name!);
                                break;
                            case "RELEASE":
                                if (_sqlTransaction is null)
                                    throw new SqlException("No active transaction");
                                if (_savepointTransaction && _sqlTransaction.IsOutermostRelease(control.Name!))
                                {
                                    await _sqlTransaction.CommitAsync(token).ConfigureAwait(false);
                                    _sqlTransaction = null;
                                }
                                else
                                    _sqlTransaction.Release(control.Name!);
                                break;
                            case "COMMIT":
                                if (_sqlTransaction is null)
                                    throw new SqlException("No active transaction");
                                await _sqlTransaction.CommitAsync(token).ConfigureAwait(false);
                                _sqlTransaction = null;
                                break;
                            case "ROLLBACK":
                                if (_sqlTransaction is null)
                                    throw new SqlException("No active transaction");
                                _sqlTransaction.Rollback();
                                _sqlTransaction = null;
                                break;
                        }
                    }
                    else if (_sqlTransaction is not null)
                        affected += _sqlTransaction.ExecuteStatement(statement, command.Parameters);
                    else
                        affected += await _storage.TransformAsync(snapshot =>
                        {
                            var session = new SqlSession(this, snapshot);
                            int changed = session.Execute(statement, command.Parameters);
                            session.ValidateForeignKeys(true);
                            return (session.Mutations.Values.ToArray(), changed);
                        }, token).ConfigureAwait(false);
                }

            return affected;
        }
        finally
        {
            gate.Release();
        }
    }
}

/// <remarks>本部分解析 SQLite 风格事务控制关键字。</remarks>
internal sealed partial class SqlParser
{
    /// <summary>尝试解析当前事务控制语句。</summary>
    /// <returns>匹配时返回事务控制节点，否则返回 null。</returns>
    private SqlTransactionControl? ReadTransaction()
    {
        if (Eat("BEGIN"))
        {
            string mode = Is("DEFERRED") || Is("IMMEDIATE") || Is("EXCLUSIVE") ? Name().ToUpperInvariant() : "DEFERRED";
            Eat("TRANSACTION");
            return new("BEGIN", Mode: mode);
        }

        if (Eat("COMMIT") || Eat("END"))
        {
            Eat("TRANSACTION");
            return new("COMMIT");
        }

        if (Eat("SAVEPOINT"))
            return new("SAVEPOINT", Name());
        if (Eat("RELEASE"))
        {
            Eat("SAVEPOINT");
            return new("RELEASE", Name());
        }

        if (Eat("ROLLBACK"))
        {
            Eat("TRANSACTION");
            if (Eat("TO"))
            {
                Eat("SAVEPOINT");
                return new("ROLLBACK TO", Name());
            }

            return new("ROLLBACK");
        }

        return null;
    }
}
