using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace PureDb.AdoNet;

public class PureDbConnection : DbConnection
{
    private string _connectionString = "";
    private SqlDatabase? _database;
    internal PureDbTransaction? ActiveTransaction;
    internal SqlDatabase Engine => _database ?? throw new InvalidOperationException("Connection is closed");
    public PureDbConnection() { }
    public PureDbConnection(string connectionString) => ConnectionString = connectionString;
    [AllowNull] public override string ConnectionString
    {
        get => _connectionString;
        set { if (State != ConnectionState.Closed) throw new InvalidOperationException("Close before changing connection string"); _connectionString = value ?? ""; }
    }
    public override string Database => Path.GetFileNameWithoutExtension(DataSource);
    public override string DataSource
    {
        get { var b = new DbConnectionStringBuilder { ConnectionString = _connectionString }; return b.TryGetValue("Data Source", out var path) ? Convert.ToString(path, CultureInfo.InvariantCulture)! : ""; }
    }
    public override string ServerVersion => "PureDb/1 (.NET 10)";
    public override ConnectionState State => _database is null ? ConnectionState.Closed : ConnectionState.Open;
    protected override DbProviderFactory DbProviderFactory => PureDbProviderFactory.Instance;
    public override void Open()
    {
        if (_database is not null) throw new InvalidOperationException("Connection already open");
        var b = new DbConnectionStringBuilder { ConnectionString = _connectionString };
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Data Source", "Access", "Durability", "Cache Bytes", "SQL Cache Bytes", "Pooling" };
        foreach (string key in b.Keys) if (!allowed.Contains(key)) throw new ArgumentException("Unknown connection option: " + key);
        if (string.IsNullOrWhiteSpace(DataSource)) throw new ArgumentException("Data Source is required");
        string Text(string key, string fallback) => b.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture)! : fallback;
        long Size(string key) => long.Parse(Text(key, "67108864"), CultureInfo.InvariantCulture);
        if (bool.Parse(Text("Pooling", "false"))) throw new NotSupportedException("Connection pooling is not implemented");
        var options = new SqlDatabaseOptions { CacheBytes = Size("SQL Cache Bytes"), Storage = new()
        {
            Access = Enum.Parse<DatabaseAccess>(Text("Access", DefaultAccess.ToString()), true),
            Durability = Enum.Parse<Durability>(Text("Durability", "Full"), true), CacheBytes = Size("Cache Bytes")
        } };
        _database = OpenDatabase(DataSource, options); OnStateChange(new(ConnectionState.Closed, ConnectionState.Open));
    }
    protected virtual DatabaseAccess DefaultAccess => DatabaseAccess.Shared;
    protected virtual SqlDatabase OpenDatabase(string path, SqlDatabaseOptions options) => new(path, options);
    public override Task OpenAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); Open(); return Task.CompletedTask; }
    public override void Close()
    {
        if (_database is null) return;
        ActiveTransaction?.Rollback(); var database = _database; _database = null; database.Dispose();
        OnStateChange(new(ConnectionState.Open, ConnectionState.Closed));
    }
    protected override void Dispose(bool disposing) { if (disposing) Close(); base.Dispose(disposing); }
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException("Open another connection to change database");
    protected override DbCommand CreateDbCommand() => new PureDbCommand { Connection = this };
    public new PureDbCommand CreateCommand() => (PureDbCommand)base.CreateCommand();
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (ActiveTransaction is not null) throw new InvalidOperationException("Nested transactions are not supported");
        if (isolationLevel is not (IsolationLevel.Unspecified or IsolationLevel.Serializable)) throw new NotSupportedException("Only optimistic Serializable isolation is supported");
        return ActiveTransaction = new(this, Engine.BeginSession());
    }
    public override DataTable GetSchema() => GetSchema("MetaDataCollections");
    public override DataTable GetSchema(string name) => GetSchema(name, null);
    public override DataTable GetSchema(string name, string?[]? restrictions)
    {
        _ = Engine;
        if (name.Equals("MetaDataCollections", StringComparison.OrdinalIgnoreCase))
        {
            var t = new DataTable(name); t.Columns.Add("CollectionName", typeof(string)); t.Columns.Add("NumberOfRestrictions", typeof(int)); t.Columns.Add("NumberOfIdentifierParts", typeof(int));
            t.Rows.Add("Tables", 4, 1); t.Rows.Add("Columns", 4, 2); return t;
        }
        bool columns = name.Equals("Columns", StringComparison.OrdinalIgnoreCase);
        if (!columns && !name.Equals("Tables", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Schema collection: " + name);
        var table = new DataTable(name);
        foreach (string c in new[] { "TABLE_CATALOG", "TABLE_SCHEMA", "TABLE_NAME" }) table.Columns.Add(c, typeof(string));
        if (columns) { table.Columns.Add("COLUMN_NAME", typeof(string)); table.Columns.Add("ORDINAL_POSITION", typeof(int)); table.Columns.Add("DATA_TYPE", typeof(string)); table.Columns.Add("IS_NULLABLE", typeof(bool)); }
        else table.Columns.Add("TABLE_TYPE", typeof(string));
        bool Match(int i, string value) => restrictions is null || i >= restrictions.Length || restrictions[i] is null || restrictions[i]!.Equals(value, StringComparison.OrdinalIgnoreCase);
        foreach (var schema in Engine.Schema().Tables)
        {
            if (!Match(0, Database) || !Match(1, "") || !Match(2, schema.Name)) continue;
            if (!columns) { if (Match(3, "TABLE")) table.Rows.Add(Database, "", schema.Name, "TABLE"); }
            else for (int i = 0; i < schema.Columns.Count; i++) { var c = schema.Columns[i]; if (Match(3, c.Name)) table.Rows.Add(Database, "", schema.Name, c.Name, i + 1, c.Type, !c.NotNull && !c.Primary); }
        }
        return table;
    }
}

public sealed class PureDbTransaction : DbTransaction
{
    private PureDbConnection? _connection;
    internal SqlDatabase.TransactionSession Session { get; }
    internal PureDbTransaction(PureDbConnection connection, SqlDatabase.TransactionSession session) { _connection = connection; Session = session; }
    public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
    protected override DbConnection? DbConnection => _connection;
    public override void Commit() => CommitAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override async Task CommitAsync(CancellationToken token = default)
    {
        if (_connection is null) throw new InvalidOperationException("Transaction already complete");
        token.ThrowIfCancellationRequested();
        try { await Session.CommitAsync(token).ConfigureAwait(false); } finally { Session.Rollback(); End(); }
    }
    public override void Rollback() { if (_connection is null) return; Session.Rollback(); End(); }
    private void End() { if (_connection is { } c) { c.ActiveTransaction = null; _connection = null; } }
    protected override void Dispose(bool disposing) { if (disposing) Rollback(); base.Dispose(disposing); }
}

public sealed class PureDbProviderFactory : DbProviderFactory
{
    public static readonly PureDbProviderFactory Instance = new();
    private PureDbProviderFactory() { }
    public override DbConnection CreateConnection() => new PureDbConnection();
    public override DbCommand CreateCommand() => new PureDbCommand();
    public override DbParameter CreateParameter() => new PureDbParameter();
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new();
    public override DbDataAdapter CreateDataAdapter() => new PureDbDataAdapter();
    public override bool CanCreateDataAdapter => true;
}
public sealed class PureDbDataAdapter : DbDataAdapter
{
    public PureDbDataAdapter() { }
    public PureDbDataAdapter(PureDbCommand selectCommand) => SelectCommand = selectCommand;
}
