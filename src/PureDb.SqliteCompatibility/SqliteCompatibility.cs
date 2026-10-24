using System.Data.Common;
using PureDb.AdoNet;

namespace PureDb.SqliteCompatibility;
/// <summary>SQLite 文件兼容后端的页缓存、SQL 缓存、锁等待及实验性写入选项。</summary>
public sealed class SqliteCompatibilityOptions
{
    /// <summary>兼容后端页缓存预算，单位为字节，默认 8 MiB。</summary>
    public long PageCacheBytes { get; init; } = 8 * 1024 * 1024;
    /// <summary>SQL 解码表缓存预算，单位为字节，默认 64 MiB。</summary>
    public long SqlCacheBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>取得 SQLite 字节范围锁的最长等待时间，默认 5 秒。</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>显式启用 Linux POSIX 锁；同一进程仅允许一个兼容连接，存活期间不得通过原生 SQLite 或其他句柄打开此文件。</summary>
    public bool LinuxExclusiveProcess
    {
        get; init;
    }
    /// <summary>启用实验性原地页更新：支持保持 B-tree 拓扑的部分记录和索引变更，其他写入回退到整库重建。</summary>
    public bool EnableIncrementalUpdates
    {
        get; init;
    }
}

/// <summary>可选的纯托管 SQLite 格式 3 入口；连接生命周期持有独占字节范围锁，使用回滚日志提交。</summary>
public static class SqliteCompatibility
{
    /// <summary>打开受支持的 SQLite 文件，取得独占锁并恢复热回滚日志；不支持的格式或模式显式报错。</summary>
    /// <param name="path">当前数据库或索引文件路径。</param>
    /// <param name="options">可选配置；为 null 时使用默认值。</param>
    /// <returns>持有 SQLite 兼容存储的 SQL 数据库；调用方负责释放。</returns>
    /// <exception cref = "System.NotSupportedException">文件格式或模式超出兼容范围。</exception>
    public static SqlDatabase Open(string path, SqliteCompatibilityOptions? options = null)
    {
        options ??= new();
        if (options.PageCacheBytes < 0 || options.SqlCacheBytes < 0 || options.LockTimeout < TimeSpan.Zero || options.LockTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(options));
        return new(new SqliteStorage(path, options), options.SqlCacheBytes, true);
    }
}

/// <summary>使用可选纯托管兼容后端原地访问 SQLite 文件的 ADO.NET 连接；兼容范围见项目文档。</summary>
public sealed class SqliteConnection : PureDbConnection
{
    /// <summary>是否启用 Linux POSIX 独占进程锁协议；必须在 Open 前设置，并遵守兼容选项的句柄限制。</summary>
    public bool LinuxExclusiveProcess
    {
        get; set;
    }
    /// <summary>是否启用实验性增量页写入；必须在 Open 前设置。</summary>
    public bool EnableIncrementalUpdates
    {
        get; set;
    }

    /// <summary>创建尚未打开的 SQLite 文件兼容连接。</summary>
    public SqliteConnection()
    {
    }

    /// <summary>创建尚未打开的 SQLite 文件兼容连接。</summary>
    /// <param name="connectionString">包含 Data Source 等受支持配置的连接字符串。</param>
    public SqliteConnection(string connectionString) : base(connectionString)
    {
    }

    /// <summary>当前 SQLite 文件兼容后端的阶段和运行时说明。</summary>
    public override string ServerVersion => "PureDb SQLite format compatibility/phase 1 (.NET 10, managed)";
    /// <summary>兼容连接要求的默认独占访问方式。</summary>
    protected override DatabaseAccess DefaultAccess => DatabaseAccess.Exclusive;
    /// <summary>创建 SQLite 兼容连接的提供程序工厂。</summary>
    protected override DbProviderFactory DbProviderFactory => SqliteProviderFactory.Instance;

    /// <summary>按兼容连接配置创建 SQL 引擎；拒绝不受支持的共享访问选项。</summary>
    /// <param name="path">当前数据库或索引文件路径。</param>
    /// <param name="options">可选配置；为 null 时使用默认值。</param>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    protected override SqlDatabase OpenDatabase(string path, SqlDatabaseOptions options)
    {
        if (options.Storage.Access != DatabaseAccess.Exclusive || options.Storage.Durability != Durability.Full)
            throw new NotSupportedException("SQLite compatibility phase 1 requires Access=Exclusive;Durability=Full.");
        return SqliteCompatibility.Open(path, new()
        {
            PageCacheBytes = options.Storage.CacheBytes,
            SqlCacheBytes = options.CacheBytes,
            LinuxExclusiveProcess = LinuxExclusiveProcess,
            EnableIncrementalUpdates = EnableIncrementalUpdates
        });
    }
}

/// <summary>SQLite 文件兼容的 ADO.NET 提供程序工厂。</summary>
public sealed class SqliteProviderFactory : DbProviderFactory
{
    /// <summary>提供程序工厂的共享单例。</summary>
    public static readonly SqliteProviderFactory Instance = new();
    /// <summary>限制兼容工厂构造，调用方使用共享 Instance。</summary>
    private SqliteProviderFactory()
    {
    }

    /// <summary>创建尚未打开的连接。</summary>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    public override DbConnection CreateConnection() => new SqliteConnection();
    /// <summary>创建未绑定连接的文本 SQL 命令。</summary>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    public override DbCommand CreateCommand() => new PureDbCommand();
    /// <summary>创建输入参数。</summary>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    public override DbParameter CreateParameter() => new PureDbParameter();
    /// <summary>创建数据适配器。</summary>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    public override DbDataAdapter CreateDataAdapter() => new PureDbDataAdapter();
    /// <summary>创建通用连接字符串构造器。</summary>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new();
    /// <summary>报告当前工厂支持创建数据适配器。</summary>
    public override bool CanCreateDataAdapter => true;
}
