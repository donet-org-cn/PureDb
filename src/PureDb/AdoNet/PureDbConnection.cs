using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace PureDb.AdoNet;
/// <summary>PureDb 的 ADO.NET 连接；支持参数化命令、乐观 Serializable 事务和连接级用户函数。</summary>
public partial class PureDbConnection : DbConnection
{
    private string _connectionString = "";
    private SqlDatabase? _database;
    internal PureDbTransaction? ActiveTransaction;
    /// <summary>已打开连接持有的 SQL 引擎；关闭状态访问会抛出异常。</summary>
    internal SqlDatabase Engine => _database ?? throw new InvalidOperationException("Connection is closed");

    /// <summary>创建尚未打开的连接；可以在 Open 之前配置连接字符串和用户函数。</summary>
    public PureDbConnection()
    {
    }

    /// <summary>创建尚未打开的连接；可以在 Open 之前配置连接字符串和用户函数。</summary>
    /// <param name="connectionString">包含 Data Source 等受支持配置的连接字符串。</param>
    public PureDbConnection(string connectionString) => ConnectionString = connectionString;
    /// <summary>连接配置；必须关闭连接后才能修改，不支持连接池。</summary>
    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (State != ConnectionState.Closed)
                throw new InvalidOperationException("Close before changing connection string");
            _connectionString = value ?? "";
        }
    }

    /// <summary>从数据源路径推导的数据库名称，不包含文件扩展名。</summary>
    public override string Database => Path.GetFileNameWithoutExtension(DataSource);

    /// <summary>连接字符串中的 Data Source 文件路径。</summary>
    public override string DataSource
    {
        get
        {
            var b = new DbConnectionStringBuilder
            {
                ConnectionString = _connectionString
            };
            return b.TryGetValue("Data Source", out var path) ? Convert.ToString(path, CultureInfo.InvariantCulture)! : "";
        }
    }

    /// <summary>当前托管数据库提供程序的版本说明。</summary>
    public override string ServerVersion => "PureDb/1 (.NET 10)";
    /// <summary>连接当前为 Open 或 Closed。</summary>
    public override ConnectionState State => _database is null ? ConnectionState.Closed : ConnectionState.Open;
    /// <summary>与本连接对应的 ADO.NET 提供程序工厂。</summary>
    protected override DbProviderFactory DbProviderFactory => PureDbProviderFactory.Instance;

    /// <summary>校验连接选项并打开文件，然后应用当前连接注册的 C# 函数。</summary>
    public override void Open()
    {
        if (_database is not null)
            throw new InvalidOperationException("Connection already open");
        var b = new DbConnectionStringBuilder
        {
            ConnectionString = _connectionString
        };
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Data Source",
            "Access",
            "Durability",
            "Cache Bytes",
            "SQL Cache Bytes",
            "Pooling"
        };
        foreach (string key in b.Keys)
            if (!allowed.Contains(key))
                throw new ArgumentException("Unknown connection option: " + key);
        if (string.IsNullOrWhiteSpace(DataSource))
            throw new ArgumentException("Data Source is required");
        string Text(string key, string fallback) => b.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture)! : fallback;
        long Size(string key) => long.Parse(Text(key, "67108864"), CultureInfo.InvariantCulture);
        if (bool.Parse(Text("Pooling", "false")))
            throw new NotSupportedException("Connection pooling is not implemented");
        var options = new SqlDatabaseOptions
        {
            CacheBytes = Size("SQL Cache Bytes"),
            Storage = new()
            {
                Access = Enum.Parse<DatabaseAccess>(Text("Access", DefaultAccess.ToString()), true),
                Durability = Enum.Parse<Durability>(Text("Durability", "Full"), true),
                CacheBytes = Size("Cache Bytes")
            }
        };
        var database = OpenDatabase(DataSource, options);
        try
        {
            foreach (var register in _functionRegistrations.Values)
                register(database);
        }
        catch
        {
            database.Dispose();
            throw;
        }

        _database = database;
        OnStateChange(new(ConnectionState.Closed, ConnectionState.Open));
    }

    /// <summary>未配置 Access 时使用的访问方式；原生连接默认为 Shared。</summary>
    protected virtual DatabaseAccess DefaultAccess => DatabaseAccess.Shared;

    /// <summary>创建实际使用的 SQL 引擎；派生连接可替换存储后端。</summary>
    /// <param name="path">当前数据库或索引文件路径。</param>
    /// <param name="options">可选配置；为 null 时使用默认值。</param>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    protected virtual SqlDatabase OpenDatabase(string path, SqlDatabaseOptions options) => new(path, options);
    /// <summary>检查取消后打开连接；文件打开和恢复工作在当前线程执行。</summary>
    /// <param name="token">取消请求；执行中的同步计算不能被抢占。</param>
    /// <returns>完成时表示该操作结束的任务。</returns>
    public override Task OpenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Open();
        return Task.CompletedTask;
    }

    /// <summary>回滚尚未提交的事务并关闭引擎；函数注册保留到下次打开。</summary>
    public override void Close()
    {
        if (_database is null)
            return;
        ActiveTransaction?.Rollback();
        var database = _database;
        _database = null;
        database.Dispose();
        OnStateChange(new(ConnectionState.Open, ConnectionState.Closed));
    }

    /// <summary>释放连接；显式释放时关闭数据库并回滚未提交事务。</summary>
    /// <param name="disposing">是否由显式资源释放触发；为 true 时释放托管连接及事务资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Close();
        base.Dispose(disposing);
    }

    /// <summary>不支持切换已打开连接的数据文件；应创建另一个连接。</summary>
    /// <param name="databaseName">请求切换到的数据库名称；该操作不受支持。</param>
    /// <exception cref = "System.NotSupportedException">当前提供程序不支持切换数据库。</exception>
    public override void ChangeDatabase(string databaseName) => throw new NotSupportedException("Open another connection to change database");
    /// <summary>创建绑定到当前连接的 ADO.NET 命令。</summary>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    protected override DbCommand CreateDbCommand() => new PureDbCommand
    {
        Connection = this
    };
    /// <summary>创建绑定到当前连接的强类型 SQL 命令。</summary>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    public new PureDbCommand CreateCommand() => (PureDbCommand)base.CreateCommand();
    /// <summary>创建乐观 Serializable 事务；每次访问及提交验证版本，冲突时需要回滚并重试整个事务。</summary>
    /// <param name="isolationLevel">请求的隔离级别；只接受 Unspecified 或 Serializable。</param>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    /// <exception cref = "System.NotSupportedException">请求的隔离级别不受支持。</exception>
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (ActiveTransaction is not null)
            throw new InvalidOperationException("Nested transactions are not supported");
        if (isolationLevel is not (IsolationLevel.Unspecified or IsolationLevel.Serializable))
            throw new NotSupportedException("Only optimistic Serializable isolation is supported");
        return ActiveTransaction = new(this, Engine.BeginSession());
    }

    /// <summary>获取模式集合目录、表或列信息；支持按目录、模式、表名和列名筛选。</summary>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    public override DataTable GetSchema() => GetSchema("MetaDataCollections");
    /// <summary>获取模式集合目录、表或列信息；支持按目录、模式、表名和列名筛选。</summary>
    /// <param name="name">模式集合名称：MetaDataCollections、Tables 或 Columns。</param>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    public override DataTable GetSchema(string name) => GetSchema(name, null);
    /// <summary>获取模式集合目录、表或列信息；支持按目录、模式、表名和列名筛选。</summary>
    /// <param name="name">模式集合名称：MetaDataCollections、Tables 或 Columns。</param>
    /// <param name="restrictions">按模式集合约定排列的筛选值，null 项表示不限制。</param>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    public override DataTable GetSchema(string name, string?[]? restrictions)
    {
        _ = Engine;
        if (name.Equals("MetaDataCollections", StringComparison.OrdinalIgnoreCase))
        {
            var t = new DataTable(name);
            t.Columns.Add("CollectionName", typeof(string));
            t.Columns.Add("NumberOfRestrictions", typeof(int));
            t.Columns.Add("NumberOfIdentifierParts", typeof(int));
            t.Rows.Add("Tables", 4, 1);
            t.Rows.Add("Columns", 4, 2);
            return t;
        }

        bool columns = name.Equals("Columns", StringComparison.OrdinalIgnoreCase);
        if (!columns && !name.Equals("Tables", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Schema collection: " + name);
        var table = new DataTable(name);
        foreach (string c in new[]
        {
            "TABLE_CATALOG",
            "TABLE_SCHEMA",
            "TABLE_NAME"
        }

        )
            table.Columns.Add(c, typeof(string));
        if (columns)
        {
            table.Columns.Add("COLUMN_NAME", typeof(string));
            table.Columns.Add("ORDINAL_POSITION", typeof(int));
            table.Columns.Add("DATA_TYPE", typeof(string));
            table.Columns.Add("IS_NULLABLE", typeof(bool));
        }
        else
            table.Columns.Add("TABLE_TYPE", typeof(string));
        bool Match(int i, string value) => restrictions is null || i >= restrictions.Length || restrictions[i] is null || restrictions[i]!.Equals(value, StringComparison.OrdinalIgnoreCase);
        foreach (var schema in Engine.Schema().Tables)
        {
            if (!Match(0, Database) || !Match(1, "") || !Match(2, schema.Name))
                continue;
            if (!columns)
            {
                if (Match(3, "TABLE"))
                    table.Rows.Add(Database, "", schema.Name, "TABLE");
            }
            else
                for (int i = 0; i < schema.Columns.Count; i++)
                {
                    var c = schema.Columns[i];
                    if (Match(3, c.Name))
                        table.Rows.Add(Database, "", schema.Name, c.Name, i + 1, c.Type, !c.NotNull && !c.Primary);
                }
        }

        return table;
    }
}

/// <summary>保存私有暂存变更的乐观 Serializable 事务；提交前验证全局已提交版本。</summary>
public sealed class PureDbTransaction : DbTransaction
{
    private PureDbConnection? _connection;
    /// <summary>当前事务的私有暂存会话。</summary>
    internal SqlDatabase.TransactionSession Session
    {
        get;
    }

    /// <summary>将已创建的事务会话绑定到连接。</summary>
    /// <param name="connection">拥有当前对象的 ADO.NET 连接。</param>
    /// <param name="session">持有当前 SQL 私有变更的会话。</param>
    internal PureDbTransaction(PureDbConnection connection, SqlDatabase.TransactionSession session)
    {
        _connection = connection;
        Session = session;
    }

    /// <summary>本提供程序实现的 Serializable 隔离级别；使用乐观冲突检查。</summary>
    public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
    /// <summary>拥有当前事务的连接；事务结束后为 null。</summary>
    protected override DbConnection? DbConnection => _connection;

    /// <summary>提交全部暂存变更；版本冲突时不写入任何事务变更。</summary>
    /// <exception cref = "System.Data.DBConcurrencyException">事务期间全局已提交版本发生变化，必须重试整个事务。</exception>
    public override void Commit() => CommitAsync(CancellationToken.None).GetAwaiter().GetResult();
    /// <summary>异步提交暂存变更；无论提交成功或失败，当前事务对象都会结束。</summary>
    /// <param name="token">取消请求；执行中的同步计算不能被抢占。</param>
    /// <returns>完成时表示该操作结束的任务。</returns>
    /// <exception cref = "System.Data.DBConcurrencyException">提交时检测到已提交版本变化，没有提交本事务变更。</exception>
    public override async Task CommitAsync(CancellationToken token = default)
    {
        if (_connection is null)
            throw new InvalidOperationException("Transaction already complete");
        token.ThrowIfCancellationRequested();
        try
        {
            await Session.CommitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            Session.Rollback();
            End();
        }
    }

    /// <summary>丢弃全部暂存变更并结束事务；已结束事务重复调用无操作。</summary>
    public override void Rollback()
    {
        if (_connection is null)
            return;
        Session.Rollback();
        End();
    }

    /// <summary>解除连接的活动事务关联，防止已结束事务再次提交。</summary>
    private void End()
    {
        if (_connection is { } c)
        {
            c.ActiveTransaction = null;
            _connection = null;
        }
    }

    /// <summary>释放事务；尚未提交时自动回滚。</summary>
    /// <param name="disposing">是否由显式资源释放触发；为 true 时释放托管连接及事务资源。</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Rollback();
        base.Dispose(disposing);
    }
}

/// <summary>原生数据库的 ADO.NET 提供程序工厂。</summary>
public sealed class PureDbProviderFactory : DbProviderFactory
{
    /// <summary>提供程序工厂的共享单例。</summary>
    public static readonly PureDbProviderFactory Instance = new();
    /// <summary>限制工厂构造，调用方使用共享 Instance。</summary>
    private PureDbProviderFactory()
    {
    }

    /// <summary>创建尚未打开的连接。</summary>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    public override DbConnection CreateConnection() => new PureDbConnection();
    /// <summary>创建未绑定连接的文本 SQL 命令。</summary>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    public override DbCommand CreateCommand() => new PureDbCommand();
    /// <summary>创建输入参数。</summary>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    public override DbParameter CreateParameter() => new PureDbParameter();
    /// <summary>创建通用连接字符串构造器。</summary>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new();
    /// <summary>创建数据适配器。</summary>
    /// <returns>当前配置对应的对象；由调用方按其生命周期约定使用和释放。</returns>
    public override DbDataAdapter CreateDataAdapter() => new PureDbDataAdapter();
    /// <summary>报告当前工厂支持创建数据适配器。</summary>
    public override bool CanCreateDataAdapter => true;
}

/// <summary>用于 DataTable 填充和命令驱动更新的 ADO.NET 数据适配器。</summary>
public sealed class PureDbDataAdapter : DbDataAdapter
{
    /// <summary>创建数据适配器，可指定用于填充数据的查询命令。</summary>
    public PureDbDataAdapter()
    {
    }

    /// <summary>创建数据适配器，可指定用于填充数据的查询命令。</summary>
    /// <param name="selectCommand">用于数据填充的查询命令。</param>
    public PureDbDataAdapter(PureDbCommand selectCommand) => SelectCommand = selectCommand;
}
