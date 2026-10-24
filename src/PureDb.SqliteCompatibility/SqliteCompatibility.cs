using System.Data.Common;
using PureDb.AdoNet;

namespace PureDb.SqliteCompatibility;

public sealed class SqliteCompatibilityOptions
{
    public long PageCacheBytes { get; init; } = 8 * 1024 * 1024;
    public long SqlCacheBytes { get; init; } = 64 * 1024 * 1024;
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Opt into Linux POSIX locks. The hosting process must not open this database
    /// through native SQLite or any other file handle while the connection is alive.
    /// Native SQLite in other processes is supported. One compatibility connection per process.
    /// Process-crash recovery is tested; power-loss durability is not certified.</summary>
    public bool LinuxExclusiveProcess { get; init; }
    /// <summary>Experimental updates preserving B-tree topology. Same-size records
    /// (including overflow), local records fitting their existing leaf, and same-leaf
    /// non-unique integer index changes are supported. Other writes rebuild the image.</summary>
    public bool EnableIncrementalUpdates { get; init; }
}

/// <summary>Pure managed, optional SQLite format 3 access. Phase 1 holds an exclusive SQLite
/// byte-range lock for the connection lifetime and commits with a durable rollback journal.
/// Writes use a full-image fallback unless the experimental incremental subset applies.</summary>
public static class SqliteCompatibility
{
    public static SqlDatabase Open(string path, SqliteCompatibilityOptions? options = null)
    {
        options ??= new();
        if (options.PageCacheBytes < 0 || options.SqlCacheBytes < 0 || options.LockTimeout < TimeSpan.Zero || options.LockTimeout > TimeSpan.FromMinutes(10)) throw new ArgumentOutOfRangeException(nameof(options));
        return new(new SqliteStorage(path, options), options.SqlCacheBytes, true);
    }
}

public sealed class SqliteConnection : PureDbConnection
{
    /// <summary>See SqliteCompatibilityOptions.LinuxExclusiveProcess. Set before Open.</summary>
    public bool LinuxExclusiveProcess { get; set; }
    /// <summary>See SqliteCompatibilityOptions.EnableIncrementalUpdates. Set before Open.</summary>
    public bool EnableIncrementalUpdates { get; set; }
    public SqliteConnection() { }
    public SqliteConnection(string connectionString) : base(connectionString) { }
    public override string ServerVersion => "PureDb SQLite format compatibility/phase 1 (.NET 10, managed)";
    protected override DatabaseAccess DefaultAccess => DatabaseAccess.Exclusive;
    protected override DbProviderFactory DbProviderFactory => SqliteProviderFactory.Instance;
    protected override SqlDatabase OpenDatabase(string path, SqlDatabaseOptions options)
    {
        if (options.Storage.Access != DatabaseAccess.Exclusive || options.Storage.Durability != Durability.Full)
            throw new NotSupportedException("SQLite compatibility phase 1 requires Access=Exclusive;Durability=Full.");
        return SqliteCompatibility.Open(path, new() { PageCacheBytes = options.Storage.CacheBytes, SqlCacheBytes = options.CacheBytes, LinuxExclusiveProcess = LinuxExclusiveProcess, EnableIncrementalUpdates = EnableIncrementalUpdates });
    }
}

public sealed class SqliteProviderFactory : DbProviderFactory
{
    public static readonly SqliteProviderFactory Instance = new();
    private SqliteProviderFactory() { }
    public override DbConnection CreateConnection() => new SqliteConnection();
    public override DbCommand CreateCommand() => new PureDbCommand();
    public override DbParameter CreateParameter() => new PureDbParameter();
    public override DbDataAdapter CreateDataAdapter() => new PureDbDataAdapter();
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new();
    public override bool CanCreateDataAdapter => true;
}
