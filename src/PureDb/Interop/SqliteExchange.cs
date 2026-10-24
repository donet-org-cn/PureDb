using System.Globalization;
using System.Text;
namespace PureDb;

public sealed partial class SqlDatabase
{
    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static string SqliteExpression(Expr e) => e.Op switch
    {
        "column" => QuoteIdentifier(((string)e.Value!).Split('.')[^1]),
        "literal" when e.Value is string s => "'" + s.Replace("'", "''") + "'",
        "JSON_EXTRACT" => "json_extract(" + string.Join(",", e.Args.Select(SqliteExpression)) + ")",
        _ => throw new NotSupportedException("This index expression cannot be exported to SQLite.")
    };
    /// <summary>Export a consistent SQL snapshot as a new SQLite format 3 database, including indexes.
    /// PureDb JSONB is converted to JSON text. The destination must not exist.</summary>
    public void ExportSqlite(string destination, CancellationToken cancellationToken = default)
    {
        string path = Path.GetFullPath(destination); if (File.Exists(path)) throw new IOException("The export destination already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            _storage.Read(snapshot =>
            {
                WriteSqliteSnapshot(snapshot, Catalog(snapshot), temp, cancellationToken); return 0;
            });
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    internal static void WriteSqliteSnapshot(ISqlSnapshot snapshot, SqlCatalog catalog, string temp, CancellationToken cancellationToken = default, uint changeCounter = 1, uint schemaCookie = 1, uint userVersion = 0, uint applicationId = 0)
    {
        using var writer = new SqliteFileWriter(temp); var schema = new List<object?[]>();
        var names = catalog.Tables.Select(t => t.Name).Concat(catalog.Tables.SelectMany(t => t.Indexes.Select(i => i.Name))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var table in catalog.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = snapshot.Ids(table.Id).Select(id => SqlCodec.Decode(snapshot.Value(id), table.Columns.Count)).ToArray();
            // SQLite's JSONB byte encoding differs from PJB1. Export portable JSON text.
            foreach (var row in rows) for (int c = 0; c < row.Length; c++)
                if (table.Columns[c].Type == "JSONB" && row[c] is { } value) row[c] = SqlJsonFunctions.Canonical(value);
            string ddl = "CREATE TABLE " + QuoteIdentifier(table.Name) + "(" + string.Join(",", table.Columns.Select(c => QuoteIdentifier(c.Name) + " " + (c.Type is "JSON" or "JSONB" ? "JSON" : c.Type) + (c.Primary ? " PRIMARY KEY" : "") + (c.NotNull ? " NOT NULL" : ""))) + ")";
            uint root = writer.Table(rows.Select(row => { var copy = row.ToArray(); copy[table.Primary] = null; return ((long)row[table.Primary]!, copy); }));
            schema.Add(["table", table.Name, table.Name, (long)root, ddl]);
            var indexes = table.Indexes.ToList();
            for (int c = 0; c < table.Columns.Count; c++) if (table.Columns[c].Unique && !table.Columns[c].Primary)
            {
                string name = $"puredb_unique_{table.Id}_{c}"; while (!names.Add(name)) name += "_";
                indexes.Add(new(name, table.Columns[c].Name, true));
            }
            foreach (var index in indexes)
            {
                cancellationToken.ThrowIfCancellationRequested(); Expr expression = index.Expression ?? new Expr("column", index.Column);
                var binding = new SqlBinding(); binding.Add(table, table.Name); var get = binding.Bind(expression, EmptyParameters);
                var values = rows.Select(r => new object?[] { SqlJsonFunctions.Unwrap(get(r)), r[table.Primary] }).ToArray();
                Array.Sort(values, (a, b) => { int n = SQLiteCompare(a[0], b[0]); return n == 0 ? ((long)a[1]!).CompareTo((long)b[1]!) : n; });
                uint indexRoot = writer.Index(values);
                string sql = "CREATE " + (index.Unique ? "UNIQUE " : "") + "INDEX " + QuoteIdentifier(index.Name) + " ON " + QuoteIdentifier(table.Name) + "(" + SqliteExpression(expression) + ")";
                schema.Add(["index", index.Name, table.Name, (long)indexRoot, sql]);
            }
        }
        writer.Finish(schema, changeCounter, schemaCookie, userVersion, applicationId);
    }
    private static int SQLiteCompare(object? left, object? right)
    {
        if (left is string a && right is string b) return Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));
        return SqlValueComparer.Instance.Compare(left, right);
    }
    /// <summary>Import an offline SQLite file into a new PureDb database. Unsupported schema is rejected.
    /// The source must be closed and its WAL checkpointed. This does not update SQLite files in place.</summary>
    public static async Task ImportSqliteAsync(string source, string destination, SqlDatabaseOptions? options = null, CancellationToken cancellationToken = default)
    {
        string path = Path.GetFullPath(destination); if (File.Exists(path)) throw new IOException("The import destination already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); string temp = path + "." + Guid.NewGuid().ToString("N") + ".import";
        try
        {
            using var reader = new SqliteFileReader(Path.GetFullPath(source));
            var schema = reader.Table(1).Select(r => r.Values).ToArray();
            if (schema.Any(r => r.Length != 5 || r[0] is not string || r[1] is not string)) throw new InvalidDataException("Invalid sqlite_schema.");
            if (schema.Any(r => (string)r[0]! is not ("table" or "index"))) throw new NotSupportedException("Views and triggers cannot be imported without changing their semantics.");
            var tables = new List<(object?[] Schema, CreateTable Create)>(); var indexes = new List<string>();
            foreach (var item in schema)
            {
                cancellationToken.ThrowIfCancellationRequested(); string name = (string)item[1]!;
                if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
                { if ((string)item[0]! == "table") throw new NotSupportedException("SQLite internal tables (AUTOINCREMENT/statistics) require explicit migration."); continue; }
                if (item[4] is not string sql) throw new NotSupportedException("Missing SQLite schema SQL.");
                var parsed = new SqlParser(sql).Parse();
                if (parsed.Count != 1) throw new NotSupportedException("Unsupported SQLite schema.");
                if ((string)item[0]! == "table" && parsed[0] is CreateTable create) tables.Add((item, create));
                else if ((string)item[0]! == "index" && parsed[0] is CreateIndex) indexes.Add(sql);
                else throw new NotSupportedException("Unsupported SQLite schema object.");
            }
            await using (var db = new SqlDatabase(temp, options))
            {
                foreach (var table in tables)
                {
                    await db.ExecuteAsync((string)table.Schema[4]!, cancellationToken: cancellationToken);
                    int primary = table.Create.Columns.FindIndex(c => c.Primary); var batch = new List<SqlCommand>(); long bytes = 0;
                    string insert = "INSERT INTO " + QuoteIdentifier(table.Create.Name) + " VALUES(" + string.Join(",", Enumerable.Range(0, table.Create.Columns.Count).Select(i => "@p" + i)) + ")";
                    foreach (var record in reader.Table((long)table.Schema[3]!))
                    {
                        cancellationToken.ThrowIfCancellationRequested(); var row = record.Values;
                        if (row.Length != table.Create.Columns.Count) throw new NotSupportedException("Rows with omitted/default columns require explicit migration.");
                        row[primary] ??= record.Id;
                        var parameters = row.Select((v, i) => new KeyValuePair<string, object?>("p" + i, v)).ToDictionary();
                        long size = row.Sum(v => v is byte[] b ? b.LongLength : v is string s ? Encoding.UTF8.GetByteCount(s) : 8) + row.Length * 16 + 24;
                        long budget = (options?.Storage.MaxTransactionBytes ?? 64 * 1024 * 1024) / 2;
                        if (batch.Count > 0 && (batch.Count >= 256 || bytes + size > budget)) { await db.ExecuteTransactionAsync(batch, cancellationToken); batch.Clear(); bytes = 0; }
                        batch.Add(new(insert, parameters)); bytes += size;
                    }
                    if (batch.Count > 0) await db.ExecuteTransactionAsync(batch, cancellationToken);
                }
                foreach (string index in indexes) await db.ExecuteAsync(index, cancellationToken: cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested(); File.Move(temp, path);
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(temp)!, Path.GetFileName(temp) + "*")) File.Delete(file);
        }
    }
}
