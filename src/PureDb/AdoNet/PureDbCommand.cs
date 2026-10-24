using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace PureDb.AdoNet;

public sealed class PureDbCommand : DbCommand
{
    private readonly PureDbParameterCollection _parameters = new();
    private PureDbConnection? _connection;
    private PureDbTransaction? _transaction;
    private CancellationTokenSource? _running;
    private readonly object _cancelGate = new();
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);
    public PureDbCommand() { }
    public PureDbCommand(string text, PureDbConnection connection) { CommandText = text; Connection = connection; }
    [AllowNull] public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; } = 30;
    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.None;
    protected override DbConnection? DbConnection { get => _connection; set => _connection = value is null or PureDbConnection ? (PureDbConnection?)value : throw new ArgumentException("Expected PureDbConnection"); }
    protected override DbTransaction? DbTransaction { get => _transaction; set => _transaction = value is null or PureDbTransaction ? (PureDbTransaction?)value : throw new ArgumentException("Expected PureDbTransaction"); }
    protected override DbParameterCollection DbParameterCollection => _parameters;
    public new PureDbConnection? Connection { get => _connection; set => _connection = value; }
    public new PureDbParameterCollection Parameters => _parameters;
    public override void Cancel() { lock (_cancelGate) _running?.Cancel(); }
    public override void Prepare() { _ = Validate(); _connection!.Engine.Prepare(CommandText); }
    protected override DbParameter CreateDbParameter() => new PureDbParameter();
    private SqlDatabase.TransactionSession? Validate()
    {
        if (CommandType != CommandType.Text) throw new NotSupportedException("Only CommandType.Text is supported");
        if (CommandTimeout < 0) throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        if (string.IsNullOrWhiteSpace(CommandText)) throw new InvalidOperationException("CommandText is empty");
        _ = _connection?.Engine ?? throw new InvalidOperationException("Command requires an open connection");
        if (!ReferenceEquals(_transaction, _connection.ActiveTransaction)) throw new InvalidOperationException("Command must use the connection's active transaction");
        return _transaction?.Session;
    }
    private IReadOnlyDictionary<string, object?> Values()
    {
        if (_parameters.Count == 0) return SqlDatabase.EmptyParameters;
        var values = _values; values.Clear(); int position = 0;
        foreach (PureDbParameter p in _parameters)
        {
            if (p.Direction != ParameterDirection.Input) throw new NotSupportedException("Only input parameters are supported");
            position++; string name = PureDbParameterCollection.Name(p.ParameterName);
            if (name.Length == 0) name = position.ToString(CultureInfo.InvariantCulture);
            object? value = p.Value is DBNull ? null : p.Value;
            value = value switch { DateTime d => d.ToString("O", CultureInfo.InvariantCulture), DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture), Guid g => g.ToString(), _ => value };
            if (!values.TryAdd(name, value)) throw new ArgumentException("Duplicate parameter: " + name);
        }
        return values;
    }
    private CancellationTokenSource Begin(CancellationToken token)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (CommandTimeout > 0) source.CancelAfter(TimeSpan.FromSeconds(CommandTimeout));
        lock (_cancelGate) { if (_running is not null) { source.Dispose(); throw new InvalidOperationException("Command already executing"); } _running = source; } return source;
    }
    private void End(CancellationTokenSource source) { lock (_cancelGate) { if (ReferenceEquals(_running, source)) _running = null; } }
    public override int ExecuteNonQuery() => ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override async Task<int> ExecuteNonQueryAsync(CancellationToken token)
    {
        var session = Validate(); using var source = Begin(token);
        try { source.Token.ThrowIfCancellationRequested(); var values = Values(); return session is null ? await _connection!.Engine.ExecuteAsync(CommandText, values, source.Token).ConfigureAwait(false) : session.Execute(CommandText, values); }
        finally { End(source); }
    }
    private DbDataReader Reader(CommandBehavior behavior, CancellationToken token)
    {
        if ((behavior & CommandBehavior.KeyInfo) != 0) throw new NotSupportedException("KeyInfo metadata is not implemented");
        var session = Validate(); using var source = Begin(token);
        try
        {
            source.Token.ThrowIfCancellationRequested(); var values = Values();
            var result = session is null ? _connection!.Engine.QueryResults(CommandText, values) : session.Query(CommandText, values);
            source.Token.ThrowIfCancellationRequested(); return new PureDbDataReader(result, behavior, _connection!);
        }
        finally { End(source); }
    }
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => Reader(behavior, CancellationToken.None);
    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken token) => Task.FromResult(Reader(behavior, token));
    public override object? ExecuteScalar() { using var r = ExecuteReader(CommandBehavior.SingleResult | CommandBehavior.SingleRow); return r.Read() && r.FieldCount > 0 ? r.GetValue(0) : null; }
    public override async Task<object?> ExecuteScalarAsync(CancellationToken token) { await using var r = await ExecuteReaderAsync(CommandBehavior.SingleResult | CommandBehavior.SingleRow, token).ConfigureAwait(false); return await r.ReadAsync(token).ConfigureAwait(false) && r.FieldCount > 0 ? r.GetValue(0) : null; }
}
