using System.Data;

namespace PureDb;
/// <remarks>本部分提供显式事务、全局版本冲突检查和 ADO.NET 结果类型描述缓存。</remarks>
public sealed partial class SqlDatabase
{
    private readonly Dictionary<Select, IReadOnlyList<Type>> _descriptions = [];
    private long _descriptionVersion = -1;
    /// <summary>在当前快照上创建带全局版本检查的乐观事务会话。</summary>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    internal TransactionSession BeginSession() => _storage.Read(s => new TransactionSession(this, s));
    /// <summary>执行全部 SELECT 并补充 ADO.NET 类型元数据；按模式版本复用描述缓存。</summary>
    /// <param name="text">待匹配的词法文本或 JSON 文本。</param>
    /// <param name="parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    internal IReadOnlyList<SqlResult> QueryResults(string text, IReadOnlyDictionary<string, object?> parameters)
    {
        if (_storage is Database database)
            return database.Read(static (snapshot, state) => state.Owner.QueryResults(new SqlSession(state.Owner, snapshot), state.Text, state.Parameters), (Owner: this, Text: text, Parameters: parameters));
        return QueryResultsStorage(text, parameters);
    }

    /// <summary>通过可替换存储后端执行多结果集读取。</summary>
    /// <param name="text">待匹配的词法文本或 JSON 文本。</param>
    /// <param name="parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    private IReadOnlyList<SqlResult> QueryResultsStorage(string text, IReadOnlyDictionary<string, object?> parameters) => _storage.Read(s => QueryResults(new SqlSession(this, s), text, parameters));
    /// <summary>执行全部 SELECT 并补充 ADO.NET 类型元数据；按模式版本复用描述缓存。</summary>
    /// <param name="text">待匹配的词法文本或 JSON 文本。</param>
    /// <param name="parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <param name="session">持有当前 SQL 私有变更的会话。</param>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    private IReadOnlyList<SqlResult> QueryResults(SqlSession session, string text, IReadOnlyDictionary<string, object?> parameters)
    {
        var statements = Parse(text);
        if (statements.Count == 0 || statements.Any(s => s is not Select))
            throw new SqlException("Reader requires SELECT statements");
        var results = new SqlResult[statements.Count];
        for (int i = 0; i < statements.Count; i++)
        {
            var select = (Select)statements[i];
            var result = session.Query(select, parameters);
            static bool Parameter(Expr e) => e.Op == "parameter" || e.Args.Any(Parameter);
            if (session.SchemaChanged || select.Columns.Any(p => Parameter(p.Expr)))
                result.FieldTypes = session.Describe(select, parameters);
            else
                lock (_cacheGate)
                {
                    if (_descriptionVersion != session.SchemaVersion || _descriptions.Count > 1024)
                    {
                        _descriptions.Clear();
                        _descriptionVersion = session.SchemaVersion;
                    }

                    if (!_descriptions.TryGetValue(select, out var types))
                        _descriptions[select] = types = session.Describe(select, parameters);
                    result.FieldTypes = types;
                }

            results[i] = result;
        }

        return results;
    }

    /// <summary>解析 SQL 并缓存语法树，不捕获参数值或查询结果。</summary>
    /// <param name="text">待匹配的词法文本或 JSON 文本。</param>
    internal void Prepare(string text) => Parse(text);
    /// <summary>在读取保护期间取得当前不可变模式目录。</summary>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    internal SqlCatalog Schema() => _storage.Read(Catalog);
    // 乐观可串行化事务：每次读取、执行语句和提交都校验同一个全局已提交版本，
    // 未提交变更只保存在当前事务中。
    /// <summary>乐观显式事务，暂存变更并在每次访问和提交时检查全局版本。</summary>
    /// <param name="owner">拥有函数注册和计划缓存的 SQL 数据库。</param>
    /// <param name="snapshot">在当前读取保护期内有效的存储快照。</param>
    internal sealed class TransactionSession(SqlDatabase owner, ISqlSnapshot snapshot)
    {
        private readonly long _version = snapshot.Version;
        private readonly SqlSession _session = new(owner, snapshot);
        private bool _ended, _failed;
        private readonly object _sync = new();
        /// <summary>验证事务尚未结束或失败，并确保已提交全局版本没有变化。</summary>
        /// <param name="current">用于验证事务版本的当前已提交快照。</param>
        private void Check(ISqlSnapshot current)
        {
            if (_ended || _failed)
                throw new InvalidOperationException("Transaction is complete or failed; rollback required");
            if (current.Version != _version)
            {
                _failed = true;
                throw new DBConcurrencyException("Committed data changed; rollback and retry the transaction");
            }
        }

        /// <summary>在事务同步保护中执行语句并暂存变更，执行失败后要求回滚。</summary>
        /// <param name="text">待匹配的词法文本或 JSON 文本。</param>
        /// <param name="parameters">本次调用的参数值或编译期间收集的参数名称。</param>
        /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
        public int Execute(string text, IReadOnlyDictionary<string, object?> parameters)
        {
            lock (_sync)
                return owner._storage.Read(s =>
                {
                    Check(s);
                    try
                    {
                        int count = 0;
                        foreach (var statement in owner.Parse(text))
                            count += _session.Execute(statement, parameters);
                        return count;
                    }
                    catch
                    {
                        _failed = true;
                        throw;
                    }
                });
        }

        /// <summary>在同一事务可见状态中查询，校验全局版本并保留私有暂存变更。</summary>
        /// <param name="text">待匹配的词法文本或 JSON 文本。</param>
        /// <param name="parameters">本次调用的参数值或编译期间收集的参数名称。</param>
        /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
        public IReadOnlyList<SqlResult> Query(string text, IReadOnlyDictionary<string, object?> parameters)
        {
            lock (_sync)
                return owner._storage.Read(s =>
                {
                    Check(s);
                    try
                    {
                        return owner.QueryResults(_session, text, parameters);
                    }
                    catch
                    {
                        _failed = true;
                        throw;
                    }
                });
        }

        /// <summary>冻结暂存变更，在存储写入保护期间再次验证版本，然后原子提交。</summary>
        /// <param name="token">取消请求；原子提交开始后需完成提交或回滚。</param>
        /// <returns>表示操作最终完成的任务。</returns>
        public async ValueTask CommitAsync(CancellationToken token)
        {
            Mutation[] changes;
            lock (_sync)
            {
                if (_ended || _failed)
                    throw new InvalidOperationException("Transaction cannot commit");
                changes = _session.Mutations.Values.ToArray();
                _ended = true;
            }

            await owner._storage.TransformAsync(s =>
            {
                if (s.Version != _version)
                    throw new DBConcurrencyException("Committed data changed; transaction was not committed");
                return (changes, 0);
            }, token).ConfigureAwait(false);
        }

        /// <summary>标记事务结束并丢弃全部私有暂存变更。</summary>
        public void Rollback()
        {
            lock (_sync)
            {
                _ended = true;
                _session.Mutations.Clear();
            }
        }
    }
}
