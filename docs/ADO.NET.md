# PureDb ADO.NET 提供程序

命名空间：`PureDb.AdoNet`。与数据库核心一同编译到 `PureDb.dll`，目标 .NET 10，仅使用标准库。

SQLite 原地兼容后端另见 [独立兼容库](SQLITE-COMPATIBILITY.md)：引用 `PureDb.SqliteCompatibility`，使用其 `SqliteConnection` / `SqliteProviderFactory`，命令、参数、读取器和事务复用本提供程序。兼容连接默认 `Access=Exclusive;Durability=Full`；其文件格式、锁和写入成本限制与默认 PureDb 内核不同。 可在 Open 前设置 `EnableIncrementalUpdates=true`，Linux 还须 `LinuxExclusiveProcess=true`；选项是连接属性，不能放入连接字符串。支持范围、回退情况与两平台性能见 [兼容支持范围](SQLITE-COMPATIBILITY.md)。

## 连接与参数

```csharp
using System.Data.Common;
using PureDb.AdoNet;

DbProviderFactories.RegisterFactory("PureDb", PureDbProviderFactory.Instance);
DbProviderFactory factory = DbProviderFactories.GetFactory("PureDb");
await using DbConnection connection = factory.CreateConnection()!;
connection.ConnectionString = new DbConnectionStringBuilder
{
    ["Data Source"] = "data/app.pdb",
    ["Access"] = "Shared",
    ["Durability"] = "Full"
}.ConnectionString;
await connection.OpenAsync();

using DbCommand command = connection.CreateCommand();
command.CommandText = "CREATE TABLE IF NOT EXISTS users(id INTEGER PRIMARY KEY,name TEXT NOT NULL)";
await command.ExecuteNonQueryAsync();
command.CommandText = "INSERT INTO users VALUES(@id,@name)";
DbParameter id = command.CreateParameter();
id.ParameterName = "id"; id.Value = 1L; command.Parameters.Add(id);
DbParameter name = command.CreateParameter();
name.ParameterName = "name"; name.Value = "中文名字"; command.Parameters.Add(name);
command.Prepare();
await command.ExecuteNonQueryAsync();
command.CommandText = "SELECT id,name FROM users WHERE id=@id";
await using DbDataReader reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
    Console.WriteLine($"{reader.GetInt64(0)}: {reader.GetString(1)}");
```

支持 `@name`、`:name`、`$name` 和 `?1` 参数。不带名称的参数依次绑定 `?1`、`?2`。传入 `DBNull.Value` 对应 SQL NULL；读取 NULL 返回 `DBNull.Value`，可用 `IsDBNull()` 判断。参数只支持 Input。文本值始终使用 UTF-8 存储。

| 连接选项 | 默认 | 说明 |
|---|---|---|
| Data Source | 必填 | 数据库文件路径；路径含分号时用 DbConnectionStringBuilder |
| Access | Shared | Shared 允许多个连接/进程；Exclusive 限单实例 |
| Durability | Full | Full 返回前刷盘；Buffered 可能在断电时丢失已确认写入 |
| Cache Bytes | 67108864 | 页缓存字节预算，粒度 4 KiB |
| SQL Cache Bytes | 67108864 | SQL 表缓存估算预算，包含索引/派生列等额外结构的估算而非进程内存硬上限 |
| Pooling | false | 仅支持 false；当前没有连接池 |

未知选项直接报错。核心 SQL 类型为 INTEGER、REAL、TEXT、BLOB、JSON、JSONB；INTEGER 映射 Int64，REAL 映射 Double。DateTime/DateTimeOffset 参数写为 ISO 8601 文本，Guid 写为文本；Decimal 依核心规则转换为 Double，不能用于要求精确十进制的金额。整数主键范围仍为 `[0, 2^40)`，不是完整 SQLite 类型系统兼容层。

## 事务与批量写入

```csharp
await using DbTransaction transaction = await connection.BeginTransactionAsync();
using DbCommand insert = connection.CreateCommand();
insert.Transaction = transaction;
insert.CommandText = "INSERT INTO users VALUES(@id,@name)";
DbParameter rowId = insert.CreateParameter();
rowId.ParameterName = "id"; insert.Parameters.Add(rowId);
DbParameter rowName = insert.CreateParameter();
rowName.ParameterName = "name"; insert.Parameters.Add(rowName);
for (int i = 100; i < 1100; i++)
{
    rowId.Value = (long)i;
    rowName.Value = "用户" + i;
    await insert.ExecuteNonQueryAsync();
}
await transaction.CommitAsync();
```

采用乐观 Serializable 隔离：变更暂存，不向其他连接暴露，事务内查询可以读到自己的修改。每条语句与最终提交都会验证全局提交版本；其他连接的提交或文件压缩可能使事务发生冲突，抛出 `DBConcurrencyException`。调用方必须重做整个事务；这是保守的冲突检测，没有逐行 MVCC。提交时所有变更合成一笔原子日志事务。

默认隔离级别和显式 Serializable 均可用；其他隔离级别与嵌套事务会报 NotSupportedException。显式事务中所有命令必须设置 `command.Transaction`。约束错误会使该事务失效，必须回滚；Dispose 未提交事务、关闭连接均回滚。提交失败不会部分提交，发生 I/O 故障时仍需按数据库故障语义重新打开并核查，不能盲目重试非幂等外部副作用。

批量写入应分成合理大小的事务（例如每笔 1000 行），复用命令和参数，尽量提供主键。原生 `SqlDatabase.ExecuteTransactionAsync` 仍可用于一次性提交命令集合。当前不提供 SqlBulkCopy 类；单笔事务受默认 64 MiB 日志帧限制。

## 读取、DataTable 和 DataAdapter

```csharp
using var select = new PureDbCommand("SELECT id,name FROM users ORDER BY id", (PureDbConnection)connection);
using var adapter = new PureDbDataAdapter(select);
var table = new System.Data.DataTable();
adapter.Fill(table);
```

支持同步/异步入口、ExecuteNonQuery、ExecuteScalar、ExecuteReader、多条 SELECT 的 NextResult、字段名称/类型、空结果基本字段类型、GetValues、GetBytes/GetChars 分段读取、DataTable.Load、DataAdapter.Fill。DataAdapter.Update 需要手工配置 InsertCommand/UpdateCommand/DeleteCommand 和参数 SourceColumn；没有自动生成 DML 的 CommandBuilder。

支持 `GetSchema("Tables")`、`GetSchema("Columns")` 及限制条件。读取器基于已物化的结果集，遍历时不持有数据库锁；这不是大型结果的流式游标。SequentialAccess 不会减少物化内存，SchemaOnly 目前也会先执行只读查询再抑制行输出。动态 JSON 提取和全 NULL 表达式的空结果字段类型可能为 Object。KeyInfo 元数据尚未实现，显式请求会报错。

## 并发、取消和兼容边界

连接、命令、事务对象不支持调用方并发执行；多线程使用各自连接并选择 Shared。Shared 保留文件句柄、通过协调戳读取已提交快照，日志追赶和提交仍通过命名 Mutex 协调。跨进程压缩、进程退出和提交可见性有专门测试；未宣称具备 SQLite WAL 的完整并发模型。

取消令牌支持调用开始前检查和写队列/临界区准入取消；写请求一旦准入，等待确定的提交结果，避免把成功提交误报为取消。同步查询/事务内求值不能在中途被强制打断；CommandTimeout/Cancel 对读取在执行前后检查，对写入适用准入规则。异步入口不表示扫描已改为异步 I/O。

未实现：存储过程、输出参数、连接池、分布式/环境事务、自动 CommandBuilder、EF Core 提供程序、完整 SQLite SQL/PRAGMA/类型兼容。Dapper 等依赖通用 ADO.NET 的库可以按接口接入，但本轮没有安装这些第三方库做集成认证。

验证命令：

```powershell
dotnet run -c Release --project tests/PureDb.AdoNetTests
```

接口约定参考 [.NET DbConnection](https://learn.microsoft.com/en-us/dotnet/api/system.data.common.dbconnection?view=net-10.0)。Full/Buffered 与 SQLite WAL 同步行为的对比参考 [SQLite WAL 文档](https://www.sqlite.org/wal.html)；两者的 Buffered 持久化过程不同，不能将吞吐倍率当成相同断电保证。

首个公开预览版：`1.0.0-preview.1`。最新接口开销见 [全面性能报告](PERFORMANCE.md)。
