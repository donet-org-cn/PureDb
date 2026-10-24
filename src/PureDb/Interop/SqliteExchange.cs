using System.Globalization;
using System.Text;

namespace PureDb;
/// <remarks>本部分提供受支持 SQLite 文件的离线导入、导出和模式转换。</remarks>
public sealed partial class SqlDatabase
{
    /// <summary>对 SQL 标识符进行双引号引用并转义内部引号。</summary>
    /// <param name = "value">需要转义的 SQL 标识符或待解析的 JSON 路径。</param>
    /// <returns>按当前方法语义生成、解析或规范化后的文本。</returns>
    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    /// <summary>将受支持的索引表达式转换为 SQLite 模式 SQL；未知形式明确拒绝。</summary>
    /// <param name = "e">待分析、绑定或求值的 SQL 表达式。</param>
    /// <returns>按当前方法语义生成、解析或规范化后的文本。</returns>
    private static string SqliteExpression(Expr e) => e.Op switch
    {
        "column" => QuoteIdentifier(((string)e.Value!).Split('.')[^1]),
        "literal" when e.Value is string s => "'" + s.Replace("'", "''") + "'",
        "JSON_EXTRACT" => "json_extract(" + string.Join(",", e.Args.Select(SqliteExpression)) + ")",
        _ => throw new NotSupportedException("This index expression cannot be exported to SQLite.")
    };
    /// <summary>将一致快照导出为新的 SQLite 格式 3 文件，包含受支持的索引；PureDb JSONB 转为 JSON 文本，目标不能已存在。</summary>
    /// <param name = "destination">必须尚不存在的新 SQLite 数据库文件路径。</param>
    /// <param name = "cancellationToken">取消请求；已准入写入不会因取消而返回不明确的提交结果。</param>
    /// <exception cref = "System.IO.IOException">目标文件已经存在或无法写入。</exception>
    /// <exception cref = "System.NotSupportedException">模式不能按当前 SQLite 导出范围保持语义。</exception>
    public void ExportSqlite(string destination, CancellationToken cancellationToken = default)
    {
        string path = Path.GetFullPath(destination);
        if (File.Exists(path))
            throw new IOException("The export destination already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            _storage.Read(snapshot =>
            {
                WriteSqliteSnapshot(snapshot, Catalog(snapshot), temp, cancellationToken);
                return 0;
            });
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>将一致 SQL 快照写成 SQLite 镜像，并保留受支持的表、索引及文件头版本信息。</summary>
    /// <param name = "snapshot">在当前存储保护期内有效的已提交快照。</param>
    /// <param name = "catalog">当前一致快照对应的 SQL 模式目录。</param>
    /// <param name = "temp">本次写入使用的临时目标文件路径。</param>
    /// <param name = "cancellationToken">取消请求；持久化开始后需等待明确结果。</param>
    /// <param name = "changeCounter">SQLite 文件头的数据变化计数器。</param>
    /// <param name = "schemaCookie">SQLite 文件头中的模式版本。</param>
    /// <param name = "userVersion">SQLite 文件头中由应用维护的版本号。</param>
    /// <param name = "applicationId">SQLite 文件头中的应用 ID。</param>
    internal static void WriteSqliteSnapshot(ISqlSnapshot snapshot, SqlCatalog catalog, string temp, CancellationToken cancellationToken = default, uint changeCounter = 1, uint schemaCookie = 1, uint userVersion = 0, uint applicationId = 0)
    {
        if (catalog.Views.Count > 0 || catalog.Triggers.Count > 0 || catalog.Tables.Any(t => t.Checks.Count > 0 || t.ForeignKeys.Count > 0 || t.Columns.Any(c => c.Default is not null) || t.Indexes.Any(i => i.Expression?.Op == "composite")))
            throw new NotSupportedException("SQLite file schema encoding for views/triggers/foreign keys/defaults/composite indexes is not implemented; use native PureDb format for these schema features");
        using var writer = new SqliteFileWriter(temp);
        var schema = new List<object?[]>();
        var names = catalog.Tables.Select(t => t.Name).Concat(catalog.Tables.SelectMany(t => t.Indexes.Select(i => i.Name))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var table in catalog.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = snapshot.Ids(table.Id).Select(id => SqlCodec.Decode(snapshot.Value(id), table.Columns.Count)).ToArray();
            // SQLite JSONB 与 PJB1 编码不同，导出时转换为可移植的 JSON 文本。
            foreach (var row in rows)
                for (int c = 0; c < row.Length; c++)
                    if (table.Columns[c].Type == "JSONB" && row[c] is { } value)
                        row[c] = SqlJsonFunctions.Canonical(value);
            string ddl = "CREATE TABLE " + QuoteIdentifier(table.Name) + "(" + string.Join(",", table.Columns.Select(c => QuoteIdentifier(c.Name) + " " + (c.Type is "JSON" or "JSONB" ? "JSON" : c.Type) + (c.Primary ? " PRIMARY KEY" : "") + (c.NotNull ? " NOT NULL" : ""))) + ")";
            uint root = writer.Table(rows.Select(row =>
            {
                var copy = row.ToArray();
                copy[table.Primary] = null;
                return ((long)row[table.Primary]!, copy);
            }));
            schema.Add(["table", table.Name, table.Name, (long)root, ddl]);
            var indexes = table.Indexes.ToList();
            for (int c = 0; c < table.Columns.Count; c++)
                if (table.Columns[c].Unique && !table.Columns[c].Primary)
                {
                    string name = $"puredb_unique_{table.Id}_{c}";
                    while (!names.Add(name))
                        name += "_";
                    indexes.Add(new(name, table.Columns[c].Name, true));
                }

            foreach (var index in indexes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Expr expression = index.Expression ?? new Expr("column", index.Column);
                var binding = new SqlBinding();
                binding.Add(table, table.Name);
                var get = binding.Bind(expression, EmptyParameters);
                var values = rows.Select(r => new object?[] { SqlJsonFunctions.Unwrap(get(r)), r[table.Primary] }).ToArray();
                Array.Sort(values, (a, b) =>
                {
                    int n = SQLiteCompare(a[0], b[0]);
                    return n == 0 ? ((long)a[1]!).CompareTo((long)b[1]!) : n;
                });
                uint indexRoot = writer.Index(values);
                string sql = "CREATE " + (index.Unique ? "UNIQUE " : "") + "INDEX " + QuoteIdentifier(index.Name) + " ON " + QuoteIdentifier(table.Name) + "(" + SqliteExpression(expression) + ")";
                schema.Add(["index", index.Name, table.Name, (long)indexRoot, sql]);
            }
        }

        writer.Finish(schema, changeCounter, schemaCookie, userVersion, applicationId);
    }

    /// <summary>按 SQLite 二进制排序比较索引值，文本使用 UTF-8 字节顺序。</summary>
    /// <param name = "left">二元操作的左侧 SQL 标量。</param>
    /// <param name = "right">二元操作的右侧 SQL 标量。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    private static int SQLiteCompare(object? left, object? right)
    {
        if (left is string a && right is string b)
            return Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));
        return SqlValueComparer.Instance.Compare(left, right);
    }

    /// <summary>将离线 SQLite 文件导入新的原生数据库；源文件必须关闭且 WAL 已检查点，无法保持语义的模式会被拒绝。</summary>
    /// <param name = "source">需要导入的离线 SQLite 源文件路径。</param>
    /// <param name = "destination">必须尚不存在的新 PureDb 原生数据库文件路径。</param>
    /// <param name = "options">可选配置；为 null 时使用默认值。</param>
    /// <param name = "cancellationToken">取消请求；已准入写入不会因取消而返回不明确的提交结果。</param>
    /// <returns>完成时表示该操作结束的任务。</returns>
    /// <exception cref = "System.NotSupportedException">源模式或格式超出当前导入范围。</exception>
    public static async Task ImportSqliteAsync(string source, string destination, SqlDatabaseOptions? options = null, CancellationToken cancellationToken = default)
    {
        string path = Path.GetFullPath(destination);
        if (File.Exists(path))
            throw new IOException("The import destination already exists.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".import";
        try
        {
            using var reader = new SqliteFileReader(Path.GetFullPath(source));
            var schema = reader.Table(1).Select(r => r.Values).ToArray();
            if (schema.Any(r => r.Length != 5 || r[0] is not string || r[1] is not string))
                throw new InvalidDataException("Invalid sqlite_schema.");
            if (schema.Any(r => (string)r[0]! is not ("table" or "index")))
                throw new NotSupportedException("Views and triggers cannot be imported without changing their semantics.");
            var tables = new List<(object?[] Schema, CreateTable Create)>();
            var indexes = new List<string>();
            foreach (var item in schema)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = (string)item[1]!;
                if (name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
                {
                    if ((string)item[0]! == "table")
                        throw new NotSupportedException("SQLite internal tables (AUTOINCREMENT/statistics) require explicit migration.");
                    continue;
                }

                if (item[4] is not string sql)
                    throw new NotSupportedException("Missing SQLite schema SQL.");
                var parsed = new SqlParser(sql).Parse();
                if (parsed.Count != 1)
                    throw new NotSupportedException("Unsupported SQLite schema.");
                if ((string)item[0]! == "table" && parsed[0] is CreateTable create)
                {
                    if (create.Checks.Count > 0 || create.ForeignKeys.Count > 0 || create.UniqueColumns.Count > 0 || create.Columns.Any(c => c.Default is not null))
                        throw new NotSupportedException("Import of foreign-key, table UNIQUE and DEFAULT schema requires explicit migration");
                    tables.Add((item, create));
                }
                else if ((string)item[0]! == "index" && parsed[0] is CreateIndex index)
                {
                    if (index.Expression.Op == "composite")
                        throw new NotSupportedException("Import of composite or descending indexes requires explicit migration");
                    indexes.Add(sql);
                }
                else
                    throw new NotSupportedException("Unsupported SQLite schema object.");
            }

            await using (var db = new SqlDatabase(temp, options))
            {
                foreach (var table in tables)
                {
                    await db.ExecuteAsync((string)table.Schema[4]!, cancellationToken: cancellationToken);
                    int primary = table.Create.Columns.FindIndex(c => c.Primary);
                    var batch = new List<SqlCommand>();
                    long bytes = 0;
                    string insert = "INSERT INTO " + QuoteIdentifier(table.Create.Name) + " VALUES(" + string.Join(",", Enumerable.Range(0, table.Create.Columns.Count).Select(i => "@p" + i)) + ")";
                    foreach (var record in reader.Table((long)table.Schema[3]!))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var row = record.Values;
                        if (row.Length != table.Create.Columns.Count)
                            throw new NotSupportedException("Rows with omitted/default columns require explicit migration.");
                        row[primary] ??= record.Id;
                        var parameters = row.Select((v, i) => new KeyValuePair<string, object?>("p" + i, v)).ToDictionary();
                        long size = row.Sum(v => v is byte[] b ? b.LongLength : v is string s ? Encoding.UTF8.GetByteCount(s) : 8) + row.Length * 16 + 24;
                        long budget = (options?.Storage.MaxTransactionBytes ?? 64 * 1024 * 1024) / 2;
                        if (batch.Count > 0 && (batch.Count >= 256 || bytes + size > budget))
                        {
                            await db.ExecuteTransactionAsync(batch, cancellationToken);
                            batch.Clear();
                            bytes = 0;
                        }

                        batch.Add(new(insert, parameters));
                        bytes += size;
                    }

                    if (batch.Count > 0)
                        await db.ExecuteTransactionAsync(batch, cancellationToken);
                }

                foreach (string index in indexes)
                    await db.ExecuteAsync(index, cancellationToken: cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path);
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(temp)!, Path.GetFileName(temp) + "*"))
                File.Delete(file);
        }
    }
}
