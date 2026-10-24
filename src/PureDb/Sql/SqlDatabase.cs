using System.Collections.Concurrent;
using System.Text.Json;
namespace PureDb;

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
    public SqlDatabase(string path, SqlDatabaseOptions? options = null)
    {
        options ??= new(); if (options.CacheBytes < 0) throw new ArgumentOutOfRangeException(nameof(options));
        _storage = new Database(path, options.Storage); _owns = true; _cacheBudget = options.CacheBytes;
        (_initialLength, _initialGeneration) = _storage.Read(s => (s.Length, s.Generation));
    }
    /// <summary>The supplied storage must be dedicated to SQL; its record IDs are reserved by the catalog.</summary>
    public SqlDatabase(Database storage, long cacheBytes = 64 * 1024 * 1024)
    { if (cacheBytes < 0) throw new ArgumentOutOfRangeException(nameof(cacheBytes)); _storage = storage; _cacheBudget = cacheBytes; (_initialLength, _initialGeneration) = _storage.Read(s => (s.Length, s.Generation)); }
    internal SqlDatabase(ISqlStorage storage, long cacheBytes, bool owns)
    { _storage = storage; _cacheBudget = cacheBytes; _owns = owns; (_initialLength, _initialGeneration) = _storage.Read(s => (s.Length, s.Generation)); }
    private List<SqlStatement> Parse(string sql)
    {
        // Count takes every ConcurrentDictionary lock. Cache hits must avoid it.
        if (_plans.TryGetValue(sql, out var cached)) return cached;
        if (_plans.Count > 1024)
        {
            _plans.Clear(); _primaryPlans.Clear(); _rangePlans.Clear(); _scalarPlans.Clear(); _insertColumns.Clear();
            if (_joinPlans.IsValueCreated) _joinPlans.Value.Clear();
            if (_dmlPlans.IsValueCreated) _dmlPlans.Value.Clear();
            if (_expressionPlans.IsValueCreated) _expressionPlans.Value.Clear();
        }
        return _plans.GetOrAdd(sql, s => new SqlParser(s).Parse());
    }
    public SqlResult Query(string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        var statements = Parse(sql);
        if (statements.Count != 1 || statements[0] is not Select select) throw new SqlException("Query requires one SELECT");
        parameters ??= EmptyParameters;
        if (_storage is Database database)
            return database.Read(static (snapshot, state) => new SqlSession(state.Owner, snapshot).Query(state.Select, state.Parameters),
                (Owner: this, Select: select, Parameters: parameters));
        return QueryStorage(select, parameters);
    }
    private SqlResult QueryStorage(Select select, IReadOnlyDictionary<string, object?> parameters) =>
        _storage.Read(snapshot => new SqlSession(this, snapshot).Query(select, parameters));
    /// <summary>All statements in the call commit atomically; constraints are checked under the writer lock.</summary>
    public ValueTask<int> ExecuteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken cancellationToken = default) =>
        ExecuteTransactionAsync([new(sql, parameters)], cancellationToken);
    public ValueTask<int> ExecuteTransactionAsync(IReadOnlyList<SqlCommand> commands, CancellationToken cancellationToken = default)
    {
        Dictionary<byte[], byte[]>? blobs = null;
        object? CopyParameter(object? value)
        {
            if (value is not byte[] bytes) return value;
            if (commands.Count == 1) return bytes.ToArray();
            blobs ??= new(ReferenceEqualityComparer.Instance);
            if (!blobs.TryGetValue(bytes, out var copy)) blobs.Add(bytes, copy = bytes.ToArray()); return copy;
        }
        var parsed = commands.Select(c => (Statements: Parse(c.Text), Parameters: c.Parameters is null ? EmptyParameters :
            (IReadOnlyDictionary<string, object?>)c.Parameters.ToDictionary(p => p.Key, p => CopyParameter(p.Value), StringComparer.Ordinal))).ToArray();
        return _storage.TransformAsync(snapshot =>
        {
            var session = new SqlSession(this, snapshot); int affected = 0;
            foreach (var command in parsed) foreach (var statement in command.Statements) affected += session.Execute(statement, command.Parameters);
            return (session.Mutations.Values.ToArray(), affected);
        }, cancellationToken);
    }
    public ValueTask CompactAsync(CancellationToken cancellationToken = default) => _storage.CompactAsync(cancellationToken);
    internal SqlCatalog Catalog(ISqlSnapshot snapshot)
    {
        lock (_cacheGate)
        {
            if (_catalogVersion != snapshot.KeyVersion(-1) || _catalog is null)
            {
                var record = snapshot.Get(-1);
                if (record is null && snapshot.Count != 0) throw new SqlException("This is a key/value database, not a SQL database; use a dedicated SQL file");
                _catalog = record is null ? new() : snapshot.HasPersistentIndexes ? SqlAst.LoadCached(record.Value) : SqlAst.Load(record.Value);
                _catalogVersion = snapshot.KeyVersion(-1);
            }
            return _catalog;
        }
    }
    internal SqlTableData Table(ISqlSnapshot snapshot, SqlTable table)
    {
        lock (_cacheGate)
        {
            if (_tables.TryGetValue(table.Id, out var cached) && cached.Version == snapshot.KeyVersion(table.Id) && cached.SchemaVersion == snapshot.KeyVersion(-1)) return cached.Data;
            var rows = new object?[snapshot.CountKey(table.Id)][]; int at = 0; long bytes = 0;
            if (snapshot.HasPersistentIndexes) foreach (var entry in snapshot.TableEntries(table.Id))
            {
                var value = snapshot.IndexMemory(entry);
                rows[at++] = SqlCodec.DecodeCached(value, table.Columns.Count);
                bytes += value.Length + table.Columns.Count * 80L + 96;
            }
            else foreach (long id in snapshot.Ids(table.Id))
            {
                var value = snapshot.ValueMemory(id); rows[at++] = SqlCodec.DecodeCached(value, table.Columns.Count);
                bytes += value.Length + table.Columns.Count * 80L + 96;
            }
            var data = new SqlTableData(table, rows, bytes);
            if (_tables.Remove(table.Id, out var old)) _cachedBytes -= old.Data.Bytes;
            if (data.Bytes <= _cacheBudget)
            {
                if (_cachedBytes + data.Bytes > _cacheBudget) { _tables.Clear(); _cachedBytes = 0; }
                _tables[table.Id] = (snapshot.KeyVersion(table.Id), snapshot.KeyVersion(-1), data); _cachedBytes += data.Bytes;
            }
            return data;
        }
    }
    internal SqlRangePlan RangePlan(Select select, SqlTable table) => _rangePlans.GetOrAdd((select, table.Id, table.Indexes.Count), static (key, schema) => SqlRangePlan.Build(key.Query, schema), table);
    internal SqlPrimaryPlan PrimaryPlan(Select select, SqlTable table) => _primaryPlans.GetOrAdd((select, table.Id), static (key, schema) => SqlPrimaryPlan.Build(key.Query, schema), table);
    internal int[] InsertColumns(Insert insert, SqlTable table) => _insertColumns.GetOrAdd((insert,table.Id), _ =>
    {
        int[] columns = insert.Columns?.Select(table.Column).ToArray() ?? Enumerable.Range(0,table.Columns.Count).ToArray();
        if (columns.Distinct().Count() != columns.Length) throw new SqlException("Duplicate insert column"); return columns;
    });
    internal SqlScalarPlan ScalarPlan(Select select, SqlTable table) => _scalarPlans.GetOrAdd((select, table.Id), static (key, schema) => SqlScalarPlan.Build(key.Query, schema), table);
    internal SqlJoinPlan JoinPlan(Select select, SqlTable left, SqlTable right) => _joinPlans.Value.GetOrAdd((select, left.Id, right.Id), static (key, schemas) => SqlJoinPlan.Build(key.Query, schemas.Left, schemas.Right), (Left: left, Right: right));
    internal SqlDmlPlan DmlPlan(SqlStatement statement, SqlTable table) => _dmlPlans.Value.GetOrAdd((statement, table.Id), static (key, schema) => SqlDmlPlan.Build(key.Statement, schema), table);
    internal SqlExpressionRangePlan ExpressionPlan(Select select, SqlTable table) => _expressionPlans.Value.GetOrAdd((select, table), static key => SqlExpressionRangePlan.Build(key.Query, key.Table));
    internal object?[]? CachedRow(ISqlSnapshot snapshot, SqlTable table, long id)
    {
        lock (_cacheGate)
        {
            if (!_tables.TryGetValue(table.Id, out var cached) || cached.Version != snapshot.KeyVersion(table.Id) || cached.SchemaVersion != snapshot.KeyVersion(-1)) return null;
            var rows = cached.Data.Rows; int lo = 0, hi = rows.Length - 1, primary = table.Primary;
            while (lo <= hi) { int mid = lo + (hi-lo)/2; long key = (long)rows[mid][primary]!; if (key == id) return rows[mid]; if (key < id) lo = mid + 1; else hi = mid - 1; }
            return null;
        }
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() { lock (_closeGate) return new(_closeTask ??= CloseSqlAsync()); }
    private async Task CloseSqlAsync()
    {
        try { if (_owns) await _storage.DrainForCloseAsync().ConfigureAwait(false); PrepareDiskIndexes(changedOnly: true); }
        finally { CloseDiskRanges(); if (_owns) await _storage.DisposeAsync().ConfigureAwait(false); }
    }
}
internal sealed class SqlTableData(SqlTable schema, object?[][] rows, long bytes)
{
    public SqlTable Schema => schema;
    public object?[][] Rows => rows;
    public long Bytes => bytes;
    private readonly ConcurrentDictionary<string, (object?[][] Rows, object?[] Keys)> _expressions = new();
    private readonly ConcurrentDictionary<int, object?[][]> _ordered = new();
    private readonly ConcurrentDictionary<int, Dictionary<object, List<object?[]>>> _hash = new();
    private readonly ConcurrentDictionary<int, (object? Key, long Count)[]> _groups = new();
    internal sealed class DerivedColumn(object?[] values)
    {
        internal readonly object?[] Values = values;
        internal readonly bool Integers = values.All(v => v is null or long);
        internal sealed record IntegerColumn(long[] Values, int NullCount);
        private IntegerColumn? _integerColumn;
        internal IntegerColumn Numbers()
        {
            if (Volatile.Read(ref _integerColumn) is { } existing) return existing;
            var numbers = new long[Values.Length]; int nulls = 0;
            for (int i = 0; i < numbers.Length; i++) { if (Values[i] is long n) numbers[i] = n; else nulls++; }
            var result = new IntegerColumn(numbers, nulls);
            return Interlocked.CompareExchange(ref _integerColumn, result, null) ?? result;
        }
    }
    private readonly ConcurrentDictionary<(int Column, string Path), DerivedColumn> _derived = new();
    public DerivedColumn Derived(Expr expression)
    {
        var key = (schema.Column((string)expression.Args[0].Value!), (string)expression.Args[1].Value!);
        if (_derived.TryGetValue(key, out var cached)) return cached;
        if (_derived.Count >= 4) _derived.Clear();
        return _derived.GetOrAdd(key, _ =>
        {
            var bind = new SqlBinding(); bind.Add(schema, schema.Name); var get = bind.Bind(expression, SqlDatabase.EmptyParameters);
            return new DerivedColumn(rows.Select(get).ToArray());
        });
    }
    public (object? Key, long Count)[] Groups(int column)
    {
        if (_groups.TryGetValue(column, out var cached)) return cached;
        return _groups.GetOrAdd(column, BuildGroups);
    }
    private (object? Key, long Count)[] BuildGroups(int c)
    {
        var counts = new Dictionary<object, long>(SqlValueComparer.Instance); long nulls = 0;
        foreach (var row in rows) { if (row[c] is { } key) counts[key] = counts.GetValueOrDefault(key) + 1; else nulls++; }
        var result = counts.Select(p => ((object?)p.Key, p.Value)).ToList();
        if (nulls > 0) result.Add((null, nulls));
        var sorted = result.ToArray(); Array.Sort(sorted, static (a,b) => SqlValueComparer.Instance.Compare(a.Item1,b.Item1)); return sorted;
    }
    public object?[][] Ordered(int column) => _ordered.GetOrAdd(column, c =>
    {
        var sorted = rows.ToArray();
        Array.Sort(sorted, (a, b) => { int n = SqlValueComparer.Instance.Compare(a[c], b[c]); return n == 0 ? ((long)a[schema.Primary]!).CompareTo((long)b[schema.Primary]!) : n; }); return sorted;
    });
    public Dictionary<object, List<object?[]>> Hash(int column) => _hash.GetOrAdd(column, c =>
    {
        var hash = new Dictionary<object, List<object?[]>>(SqlValueComparer.Instance);
        foreach (var row in rows)
        {
            if (row[c] is not { } key) continue;
            if (!hash.TryGetValue(key, out var list)) hash[key] = list = [];
            list.Add(row);
        }
        return hash;
    });
    public IEnumerable<object?[]> SeekExpression(SqlIndex index, string op, object? value, object? high, bool descending)
    {
        var range = ExpressionRange(index, op, value, high);
        if (descending) for (int i = range.End - 1; i >= range.Begin; i--) yield return range.Rows[i];
        else for (int i = range.Begin; i < range.End; i++) yield return range.Rows[i];
    }
    private (object?[][] Rows, object?[] Keys) Expression(SqlIndex index)
    {
        if (_expressions.TryGetValue(index.Name, out var cached)) return cached;
        return _expressions.GetOrAdd(index.Name, _ =>
        {
            var binding = new SqlBinding(); binding.Add(schema, schema.Name);
            var get = binding.Bind(index.Expression!, SqlDatabase.EmptyParameters);
            var pairs = rows.Select(row => (Row: row, Key: get(row))).ToArray();
            Array.Sort(pairs, (a, b) => { int n = SqlValueComparer.Instance.Compare(a.Key, b.Key); return n == 0 ? ((long)a.Row[schema.Primary]!).CompareTo((long)b.Row[schema.Primary]!) : n; });
            return (pairs.Select(p => p.Row).ToArray(), pairs.Select(p => p.Key).ToArray());
        });
    }
    internal (object?[][] Rows, int Begin, int End) ExpressionRange(SqlIndex index, string op, object? value, object? high)
    {
        var data = Expression(index);
        if (value is null || op == "BETWEEN" && high is null) return (data.Rows, 0, 0);
        int Bound(object? key, bool after)
        {
            int lo = 0, hi = data.Keys.Length;
            while (lo < hi) { int mid = lo + (hi - lo) / 2; int n = SqlValueComparer.Instance.Compare(data.Keys[mid], key); if (n < 0 || after && n == 0) lo = mid + 1; else hi = mid; } return lo;
        }
        int begin = op switch { "=" or ">=" or "BETWEEN" => Bound(value, false), ">" => Bound(value, true), _ => Bound(null, true) };
        int end = op switch { "=" or "<=" => Bound(value, true), "<" => Bound(value, false), "BETWEEN" => Bound(high, true), _ => data.Rows.Length };
        return (data.Rows, begin, end);
    }
    public IEnumerable<object?[]> Seek(int column, string op, object? value, object? high = null, bool descending = false)
    {
        if (value is null || op == "BETWEEN" && high is null) yield break;
        var sorted = Ordered(column); var cmp = SqlValueComparer.Instance;
        int Bound(object? key, bool after)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi) { int mid = lo + (hi - lo) / 2; int n = cmp.Compare(sorted[mid][column], key); if (n < 0 || after && n == 0) lo = mid + 1; else hi = mid; }
            return lo;
        }
        int begin = op switch { "=" or ">=" or "BETWEEN" => Bound(value, false), ">" => Bound(value, true), _ => Bound(null, true) };
        int end = op switch { "=" or "<=" => Bound(value, true), "<" => Bound(value, false), "BETWEEN" => Bound(high, true), _ => sorted.Length };
        if (descending) for (int i = end - 1; i >= begin; i--) yield return sorted[i];
        else for (int i = begin; i < end; i++) yield return sorted[i];
    }
}
internal sealed partial class SqlSession(SqlDatabase owner, ISqlSnapshot snapshot)
{
    private Dictionary<long, Mutation>? _mutations;
    public Dictionary<long, Mutation> Mutations => _mutations ??= [];
    private bool HasMutations => _mutations is { Count: > 0 };
    private SqlCatalog? _catalog;
    private SqlCatalog Catalog => _catalog ??= owner.Catalog(snapshot);
    private static long StorageId(SqlTable table, long id)
    {
        if (id < 0 || id >= 1L << 40) throw new SqlException("INTEGER PRIMARY KEY must be in [0, 2^40)");
        return ((long)table.Id << 40) | id;
    }
    private DbRecord? Get(long id)
    {
        if (_mutations is not null && _mutations.TryGetValue(id, out var m)) return m.IsDelete ? null : new(m.Id, m.IndexKey, m.Value.ToArray());
        return snapshot.Get(id);
    }
    private object?[]? Row(SqlTable table, long id)
    {
        long key = StorageId(table, id);
        if (_mutations is not null && _mutations.TryGetValue(key, out var mutation)) return mutation.IsDelete ? null : SqlCodec.Decode(mutation.Value.Span, table.Columns.Count);
        if (owner.CachedRow(snapshot, table, id) is { } cached) return cached;
        var value = snapshot.Value(key); return value.IsEmpty ? null : SqlCodec.Decode(value, table.Columns.Count);
    }
    private object?[]? ProjectRow(SqlTable table, long id, int[] columns, bool indexOnly)
    {
        long key = StorageId(table, id);
        if (_mutations is not null && _mutations.TryGetValue(key, out var mutation)) return mutation.IsDelete ? null : SqlCodec.DecodeProjection(mutation.Value.Span, table.Columns.Count, columns);
        if (indexOnly && snapshot.HasPersistentIndexes)
        {
            if (!snapshot.Contains(key)) return null;
            var row = new object?[columns.Length]; object boxed = id;
            Array.Fill(row, boxed); return row;
        }
        var value = snapshot.Value(key); return value.IsEmpty ? null : SqlCodec.DecodeProjection(value, table.Columns.Count, columns);
    }
    private SqlTableData Data(SqlTable table)
    {
        if (!HasMutations) return owner.Table(snapshot, table);
        var changes = Mutations.Values.Where(m => m.Id >= 0 && (m.Id >> 40) == table.Id).ToArray();
        if (changes.Length == 0) return owner.Table(snapshot, table);
        var map = snapshot.Range(table.Id, table.Id).ToDictionary(r => r.Id, r => SqlCodec.Decode(r.Value, table.Columns.Count));
        foreach (var m in changes) { if (m.IsDelete) map.Remove(m.Id); else map[m.Id] = SqlCodec.Decode(m.Value.ToArray(), table.Columns.Count); }
        return new(table, map.OrderBy(p => p.Key).Select(p => p.Value).ToArray(), 0);
    }
    private void ChangeCatalog(Action<SqlCatalog> change)
    {
        var copy = SqlAst.Load(JsonSerializer.SerializeToUtf8Bytes(Catalog, SqlCatalogJsonContext.Default.SqlCatalog));
        change(copy); _catalog = copy; _uniqueMaps?.Clear();
        Mutations[-1] = Mutation.Put(-1, -1, JsonSerializer.SerializeToUtf8Bytes(copy, SqlCatalogJsonContext.Default.SqlCatalog));
    }
    private static object? Coerce(SqlColumn column, object? value)
    {
        value = SqlJsonFunctions.Unwrap(SqlValueComparer.Normalize(value));
        if (value is null) { if (column.NotNull || column.Primary) throw new SqlException($"{column.Name} cannot be NULL"); return null; }
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
    private sealed record UniqueMap(Func<object?[], object?> Get, Dictionary<object,long> Values);
    private Dictionary<int, List<UniqueMap>>? _uniqueMaps;
    private List<UniqueMap> UniqueConstraints(SqlTable table)
    {
        _uniqueMaps ??= [];
        if (_uniqueMaps.TryGetValue(table.Id, out var maps)) return maps;
        maps = [];
        var cols = table.Columns.Select((c,i)=>(c,i)).Where(p=>p.c.Unique && !p.c.Primary).Select(p=>p.i)
            .Concat(table.Indexes.Where(i=>i.Unique && i.Expression is null && table.Column(i.Column)!=table.Primary).Select(i=>table.Column(i.Column))).Distinct();
        foreach (int column in cols) maps.Add(new(r=>r[column],new(SqlValueComparer.Instance)));
        foreach (var index in table.Indexes.Where(i=>i.Unique && i.Expression is not null))
        { var binding = new SqlBinding(); binding.Add(table,table.Name); maps.Add(new(binding.Bind(index.Expression!,SqlDatabase.EmptyParameters),new(SqlValueComparer.Instance))); }
        if (maps.Count>0) foreach (var other in Data(table).Rows)
            foreach (var map in maps) if (map.Get(other) is { } value) map.Values.Add(value,(long)other[table.Primary]!);
        _uniqueMaps[table.Id]=maps; return maps;
    }
    private void Store(SqlTable table, object?[] row, long? replacing = null)
    {
        for (int i = 0; i < row.Length; i++) row[i] = Coerce(table.Columns[i], row[i]);
        long id = (long)row[table.Primary]!; long key = StorageId(table, id);
        if (replacing != id && Get(key) is not null) throw new SqlException("PRIMARY KEY constraint failed");
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
            if (index.Unique || index.Expression is null) continue;
            var bind = new SqlBinding(); bind.Add(table, table.Name); bind.Bind(index.Expression!, SqlDatabase.EmptyParameters)(row);
        }
        object?[]? previous = constraints.Count > 0 && replacing is { } previousId ? Row(table, previousId) : null;
        for (int i = 0; i < constraints.Count; i++)
        {
            var state = constraints[i]; var value = pending[i];
            if (previous is not null && state.Get(previous) is { } oldValue) state.Values.Remove(oldValue);
            if (value is not null) state.Values[value] = id;
        }
        if (replacing is { } old && old != id) Mutations[StorageId(table, old)] = Mutation.Delete(StorageId(table, old));
        Mutations[key] = Mutation.Put(key, table.Id, SqlCodec.Encode(row));
    }
    internal int Execute(SqlStatement statement, IReadOnlyDictionary<string, object?> parameters)
    {
        if (statement is Update update)
        {
            var table = Catalog.Table(update.Table);
            if (owner.DmlPlan(update, table) is { Supported: true } plan) return ExecutePrimaryDml(plan, table, parameters);
        }
        else if (statement is Delete delete)
        {
            var table = Catalog.Table(delete.Table);
            if (owner.DmlPlan(delete, table) is { Supported: true } plan) return ExecutePrimaryDml(plan, table, parameters);
        }
        return ExecuteGeneral(statement, parameters);
    }
    private int ExecuteGeneral(SqlStatement statement, IReadOnlyDictionary<string, object?> parameters)
    {
        switch (statement)
        {
            case CreateTable c:
                if (Catalog.Tables.Any(t => t.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))) { if (c.IfNotExists) return 0; throw new SqlException("Table already exists"); }
                ChangeCatalog(catalog =>
                {
                    if (catalog.NextId >= 1 << 23) throw new SqlException("Table identifier space exhausted");
                    catalog.Tables.Add(new() { Id = catalog.NextId++, Name = c.Name, Columns = c.Columns });
                }); return 0;
            case CreateIndex c:
                if (Catalog.Tables.Any(t => t.Indexes.Any(i => i.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase)))) { if (c.IfNotExists) return 0; throw new SqlException("Index already exists"); }
                var indexed = Catalog.Table(c.Table);
                var indexBinding = new SqlBinding(); indexBinding.Add(indexed, indexed.Name);
                if (c.Expression.Op != "column" && !(c.Expression.Op == "JSON_EXTRACT" && c.Expression.Args.Length == 2 && c.Expression.Args[0].Op == "column" && c.Expression.Args[1].Op == "literal" && c.Expression.Args[1].Value is string))
                    throw new SqlException("Indexes support a column or JSON_EXTRACT(column, literal_path)");
                var indexGet = indexBinding.Bind(c.Expression, SqlDatabase.EmptyParameters);
                string indexColumn = c.Expression.Op == "column" ? ((string)c.Expression.Value!).Split('.')[^1] : "";
                var seenIndexValues = c.Unique ? new HashSet<object?>(SqlValueComparer.Instance) : null;
                foreach (var row in Data(indexed).Rows)
                {
                    object? value = indexGet(row);
                    if (c.Unique && value is not null && !seenIndexValues!.Add(value)) throw new SqlException("UNIQUE constraint failed");
                }
                ChangeCatalog(catalog => catalog.Table(c.Table).Indexes.Add(new(c.Name, indexColumn, c.Unique, c.Expression.Op == "column" ? null : c.Expression))); return 0;
            case Drop d:
                if (d.Kind == "TABLE")
                {
                    var table = Catalog.Tables.FirstOrDefault(t => t.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase));
                    if (table is null) { if (d.IfExists) return 0; throw new SqlException("Unknown table"); }
                    foreach (var row in Data(table).Rows) { long key = StorageId(table, (long)row[table.Primary]!); Mutations[key] = Mutation.Delete(key); }
                    ChangeCatalog(c => c.Tables.RemoveAll(t => t.Id == table.Id));
                }
                else
                {
                    if (!Catalog.Tables.Any(t => t.Indexes.Any(i => i.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase))) && !d.IfExists) throw new SqlException("Unknown index");
                    ChangeCatalog(c => { foreach (var table in c.Tables) table.Indexes.RemoveAll(i => i.Name.Equals(d.Name, StringComparison.OrdinalIgnoreCase)); });
                }
                return 0;
            case Insert insert:
                var target = Catalog.Table(insert.Table); int[] columns = owner.InsertColumns(insert, target);
                foreach (var values in insert.Values)
                {
                    if (values.Length != columns.Length) throw new SqlException("INSERT column/value count mismatch");
                    var row = new object?[target.Columns.Count];
                    for (int i = 0; i < columns.Length; i++) row[columns[i]] = values[i].Op switch
                    {
                        "literal" => values[i].Value, "parameter" => SqlPrimaryPlan.Parameter(values[i], parameters),
                        _ => new SqlBinding().Bind(values[i], parameters)([])
                    };
                    if (row[target.Primary] is null) row[target.Primary] = Data(target).Rows.Select(r => (long)r[target.Primary]!).DefaultIfEmpty(-1).Max() + 1;
                    Store(target, row);
                }
                return insert.Values.Count;
            case Update update:
                var updated = Catalog.Table(update.Table);
                var binding = new SqlBinding(); binding.Add(updated, updated.Name);
                var assignments = update.Assignments.Select(a => (Column: updated.Column(a.Column), Value: binding.Bind(a.Value, parameters))).ToArray();
                if (assignments.Select(a => a.Column).Distinct().Count() != assignments.Length) throw new SqlException("Duplicate assignment");
                var predicate = update.Where is null ? null : binding.Bind(update.Where, parameters); int affected = 0;
                foreach (var original in Candidates(updated, updated.Name, update.Where, [], parameters, out _, out _).ToArray())
                {
                    if (predicate is not null && SqlValueComparer.Truth(predicate(original)) != true) continue;
                    var row = original.ToArray(); foreach (var a in assignments) row[a.Column] = a.Value(original);
                    Store(updated, row, (long)original[updated.Primary]!); affected++;
                }
                return affected;
            case Delete delete:
                var deleted = Catalog.Table(delete.Table);
                var dbind = new SqlBinding(); dbind.Add(deleted, deleted.Name);
                var filter = delete.Where is null ? null : dbind.Bind(delete.Where, parameters); int removed = 0;
                foreach (var row in Candidates(deleted, deleted.Name, delete.Where, [], parameters, out _, out _).ToArray())
                {
                    if (filter is not null && SqlValueComparer.Truth(filter(row)) != true) continue;
                    if (deleted.HasUniqueConstraints)
                        foreach(var state in UniqueConstraints(deleted)) if(state.Get(row) is {} value) state.Values.Remove(value);
                    long key = StorageId(deleted, (long)row[deleted.Primary]!); Mutations[key] = Mutation.Delete(key); removed++;
                }
                return removed;
            default: throw new SqlException("ExecuteAsync accepts DDL/DML only. Use Query for SELECT; BEGIN/COMMIT are replaced by ExecuteTransactionAsync.");
        }
    }
}
