namespace PureDb;

public sealed class SqlException(string message) : System.Data.Common.DbException(message);
public sealed record SqlCommand(string Text, IReadOnlyDictionary<string, object?>? Parameters = null);
public sealed record SqlResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, string Plan)
{
    public IReadOnlyList<Type> FieldTypes { get; internal set; } = [];
}
public sealed class SqlDatabaseOptions
{
    public long CacheBytes { get; init; } = 64 * 1024 * 1024;
    public DatabaseOptions Storage { get; init; } = new();
}
internal sealed record SqlColumn(string Name, string Type, bool Primary = false, bool NotNull = false, bool Unique = false);
internal sealed record SqlIndex(string Name, string Column, bool Unique, Expr? Expression = null);
internal sealed class SqlTable
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<SqlColumn> Columns { get; set; } = [];
    public List<SqlIndex> Indexes { get; set; } = [];
    public int Primary => Columns.FindIndex(c => c.Primary);
    internal bool HasUniqueConstraints => Columns.Any(c => c.Unique && !c.Primary) || Indexes.Any(i => i.Unique);
    public int Column(string name)
    {
        int i = Columns.FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? throw new SqlException($"Unknown column: {name}") : i;
    }
}
internal sealed class SqlCatalog
{
    public int NextId { get; set; } = 1;
    public List<SqlTable> Tables { get; set; } = [];
    private Dictionary<string, SqlTable>? _lookup;
    public SqlTable Table(string name)
    {
        var lookup = Volatile.Read(ref _lookup);
        if (lookup is null) { lookup = Tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase); lookup = Interlocked.CompareExchange(ref _lookup, lookup, null) ?? lookup; }
        return lookup.TryGetValue(name, out var table) ? table : throw new SqlException($"Unknown table: {name}");
    }
}
internal sealed record Expr(string Op, object? Value = null, Expr[]? Items = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public Expr[] Args => Items ?? [];
    public static Expr Literal(object? value) => new("literal", value);
}
internal abstract record SqlStatement;
internal sealed record CreateTable(string Name, List<SqlColumn> Columns, bool IfNotExists) : SqlStatement;
internal sealed record CreateIndex(string Name, string Table, Expr Expression, bool Unique, bool IfNotExists) : SqlStatement;
internal sealed record Drop(string Kind, string Name, bool IfExists) : SqlStatement;
internal sealed record Insert(string Table, string[]? Columns, List<Expr[]> Values) : SqlStatement;
internal sealed record Update(string Table, List<(string Column, Expr Value)> Assignments, Expr? Where) : SqlStatement;
internal sealed record Delete(string Table, Expr? Where) : SqlStatement;
internal sealed record Source(string Table, string Alias);
internal sealed record Join(string Kind, Source Source, Expr? On);
internal sealed record Projection(Expr Expr, string? Alias);
internal sealed record Ordering(Expr Expr, bool Descending);
internal sealed record Select(List<Projection> Columns, Source From, List<Join> Joins, Expr? Where,
    List<Expr> GroupBy, Expr? Having, List<Ordering> Order, Expr? Limit, Expr? Offset, bool Distinct) : SqlStatement;

internal static class SqlAst
{
    // Catalogs returned here are shared immutable schema objects. DDL always clones
    // through Load before changing a catalog; never put a mutable DDL copy in this cache.
    private static readonly object CatalogGate = new();
    private static readonly Dictionary<string, (byte[] Bytes, SqlCatalog Catalog)> CatalogCache = [];
    private static readonly Queue<string> CatalogOrder = new();
    private static int _catalogBytes;
    internal static SqlCatalog LoadCached(byte[] bytes)
    {
        if (bytes.Length > 65536) return Load(bytes);
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        lock (CatalogGate)
            if (CatalogCache.TryGetValue(key, out var hit) && bytes.AsSpan().SequenceEqual(hit.Bytes)) return hit.Catalog;
        var parsed = Load(bytes);
        lock (CatalogGate)
        {
            if (CatalogCache.TryGetValue(key, out var hit)) return bytes.AsSpan().SequenceEqual(hit.Bytes) ? hit.Catalog : parsed;
            while (CatalogCache.Count >= 16 || _catalogBytes + bytes.Length > 256 * 1024)
            {
                string old = CatalogOrder.Dequeue(); _catalogBytes -= CatalogCache[old].Bytes.Length; CatalogCache.Remove(old);
            }
            CatalogCache.Add(key, (bytes.ToArray(), parsed)); CatalogOrder.Enqueue(key); _catalogBytes += bytes.Length;
        }
        return parsed;
    }
    public static Expr Normalize(Expr e) => e with
    {
        Value = e.Value is System.Text.Json.JsonElement j ? j.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => j.GetString(),
            System.Text.Json.JsonValueKind.Number => j.TryGetInt64(out long n) ? n : (object)j.GetDouble(),
            System.Text.Json.JsonValueKind.Null => null,
            _ => throw new SqlException("Invalid persisted index expression")
        } : e.Value,
        Items = e.Args.Select(Normalize).ToArray()
    };
    public static bool Equal(Expr a, Expr b) => a.Op == b.Op && (a.Op == "column" ?
        ((string)a.Value!).Split('.')[^1].Equals(((string)b.Value!).Split('.')[^1], StringComparison.OrdinalIgnoreCase) : Equals(a.Value, b.Value)) &&
        a.Args.Length == b.Args.Length && a.Args.Zip(b.Args).All(p => Equal(p.First, p.Second));
    public static SqlCatalog Load(byte[] bytes)
    {
        var catalog = System.Text.Json.JsonSerializer.Deserialize(bytes, SqlCatalogJsonContext.Default.SqlCatalog) ?? throw new InvalidDataException("Invalid catalog");
        foreach (var table in catalog.Tables)
            for (int i = 0; i < table.Indexes.Count; i++)
                if (table.Indexes[i].Expression is { } e) table.Indexes[i] = table.Indexes[i] with { Expression = Normalize(e) };
        return catalog;
    }
}
