using System.Data;

namespace PureDb;

public sealed partial class SqlDatabase
{
    private readonly Dictionary<Select, IReadOnlyList<Type>> _descriptions = [];
    private long _descriptionVersion = -1;
    internal TransactionSession BeginSession() => _storage.Read(s => new TransactionSession(this, s));
    internal IReadOnlyList<SqlResult> QueryResults(string text, IReadOnlyDictionary<string, object?> parameters)
    {
        if (_storage is Database database)
            return database.Read(static (snapshot, state) => state.Owner.QueryResults(new SqlSession(state.Owner, snapshot), state.Text, state.Parameters),
                (Owner: this, Text: text, Parameters: parameters));
        return QueryResultsStorage(text, parameters);
    }
    private IReadOnlyList<SqlResult> QueryResultsStorage(string text, IReadOnlyDictionary<string, object?> parameters) =>
        _storage.Read(s => QueryResults(new SqlSession(this, s), text, parameters));
    private IReadOnlyList<SqlResult> QueryResults(SqlSession session, string text, IReadOnlyDictionary<string, object?> parameters)
    {
        var statements = Parse(text);
        if (statements.Count == 0 || statements.Any(s => s is not Select)) throw new SqlException("Reader requires SELECT statements");
        var results = new SqlResult[statements.Count];
        for (int i = 0; i < statements.Count; i++)
        {
            var select = (Select)statements[i]; var result = session.Query(select, parameters);
            static bool Parameter(Expr e) => e.Op == "parameter" || e.Args.Any(Parameter);
            if (session.SchemaChanged || select.Columns.Any(p => Parameter(p.Expr))) result.FieldTypes = session.Describe(select, parameters);
            else lock (_cacheGate)
            {
                if (_descriptionVersion != session.SchemaVersion || _descriptions.Count > 1024) { _descriptions.Clear(); _descriptionVersion = session.SchemaVersion; }
                if (!_descriptions.TryGetValue(select, out var types)) _descriptions[select] = types = session.Describe(select, parameters);
                result.FieldTypes = types;
            }
            results[i] = result;
        }
        return results;
    }
    internal void Prepare(string text) => Parse(text);
    internal SqlCatalog Schema() => _storage.Read(Catalog);

    // Optimistic serializable transactions. Validate the same global committed
    // version on every read/statement and commit; staged writes remain private.
    internal sealed class TransactionSession(SqlDatabase owner, ISqlSnapshot snapshot)
    {
        private readonly long _version = snapshot.Version;
        private readonly SqlSession _session = new(owner, snapshot);
        private bool _ended, _failed;
        private readonly object _sync = new();
        private void Check(ISqlSnapshot current)
        {
            if (_ended || _failed) throw new InvalidOperationException("Transaction is complete or failed; rollback required");
            if (current.Version != _version) { _failed = true; throw new DBConcurrencyException("Committed data changed; rollback and retry the transaction"); }
        }
        public int Execute(string text, IReadOnlyDictionary<string, object?> parameters)
        {
            lock (_sync) return owner._storage.Read(s =>
            {
                Check(s);
                try { int count = 0; foreach (var statement in owner.Parse(text)) count += _session.Execute(statement, parameters); return count; }
                catch { _failed = true; throw; }
            });
        }
        public IReadOnlyList<SqlResult> Query(string text, IReadOnlyDictionary<string, object?> parameters)
        {
            lock (_sync) return owner._storage.Read(s => { Check(s); try { return owner.QueryResults(_session, text, parameters); } catch { _failed = true; throw; } });
        }
        public async ValueTask CommitAsync(CancellationToken token)
        {
            Mutation[] changes;
            lock (_sync) { if (_ended || _failed) throw new InvalidOperationException("Transaction cannot commit"); changes = _session.Mutations.Values.ToArray(); _ended = true; }
            await owner._storage.TransformAsync(s =>
            {
                if (s.Version != _version) throw new DBConcurrencyException("Committed data changed; transaction was not committed");
                return (changes, 0);
            }, token).ConfigureAwait(false);
        }
        public void Rollback() { lock (_sync) { _ended = true; _session.Mutations.Clear(); } }
    }
}
