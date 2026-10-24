# 注入 C# 用户函数

本页对应 `1.0.0-preview.2` NuGet 包和同版本源码。目前实现连接级标量函数，聚合函数和窗口函数的自定义注册接口尚未实现。

## SQL API

```csharp
using PureDb;

await using var db = new SqlDatabase("awards.puredb");
await db.ExecuteAsync("""
    CREATE TABLE IF NOT EXISTS Awards(id INTEGER PRIMARY KEY, User TEXT);
    INSERT INTO Awards VALUES(1,'Alice'),(2,'Bob'),(3,NULL);
    """);

// SQL 把 User 列的标量值传给 C#；bool 结果映射为 SQL 的 0 或 1。
db.CreateFunction<string?, bool>("RELIGIBLE", user => user is "Alice");
var result = db.Query("SELECT * FROM Awards WHERE RELIGIBLE(User)");
```

`User` 是列值。若要接收复杂 C# 对象，可以将列声明为 JSON，并在函数内显式反序列化为业务对象。

支持 0、1、2 参数的强类型重载，以及接收 `object?[]` 的通用重载。函数名不区分大小写，相同名称按参数数量区分重载；同名、同数量再次注册会替换旧函数。

```csharp
db.CreateFunction<long?, long?>("twice", value => value * 2, isDeterministic: true);
db.CreateFunction<long, long, long>("add_values", (a, b) => a + b);
db.CreateFunction("argument_count", -1, values => values.Length);

// 在投影、WHERE、JOIN ON、排序、分组表达式和 DML 中使用。
var total = db.Query("SELECT twice(SUM(id)) FROM Awards");
await db.ExecuteAsync("UPDATE Awards SET User=UPPER(User) WHERE RELIGIBLE(User)");
db.RemoveFunction("twice", 1);
```

固定参数数量范围为 0～127；`-1` 表示可变参数重载。固定数量的匹配优先于可变参数匹配。当前不允许覆盖内置函数；JSON 前缀和 AND、OR、NOT、IN、BETWEEN、LIKE 等运算符名称保留，避免改变既有语句的含义。用户函数不能出现在持久化索引定义中。

## ADO.NET

```csharp
using PureDb.AdoNet;

using var connection = new PureDbConnection("Data Source=awards.puredb");
connection.CreateFunction<string?, bool>("RELIGIBLE", user => user == "Alice");
connection.Open();

using var command = connection.CreateCommand();
command.CommandText = "SELECT * FROM Awards WHERE RELIGIBLE(User)";
using var reader = command.ExecuteReader();
while (reader.Read())
    Console.WriteLine(reader.GetString(1));
```

可以在打开连接前后注册。关闭后重新打开同一个连接对象，注册仍然保留。其他连接不会自动获得这些函数，函数委托也不会写入数据库文件。可选的 `SqliteConnection` 继承同一注册接口，文件读写仍受兼容包的现有限制约束。

## 类型、异常与并发

- SQL NULL 映射到 `null`；用 `long?`、`string?` 等可空参数接收。传给非可空值类型时明确失败。
- 输入及返回值限于支持的 SQL 标量：整数、有限实数、文本、BLOB、NULL。`bool` 转为整数 0/1，常见整数类型转为 `long`，`float` 和 `decimal` 转为 `double`。
- 传给用户的 BLOB 是独立副本；函数返回的数组也复制，防止修改缓存页或外部数组。
- 用户异常包装为保留 `InnerException` 的 `SqlException`；当前原子执行调用不提交部分变更。ADO.NET 显式事务中的语句失败后需要回滚整个事务。
- 引擎可从多个查询线程调用委托；捕获的业务状态由注册方负责同步。ADO.NET 的连接、命令对象不提供同一实例的并发执行保证。
- `isDeterministic: true` 表示注册方保证相同参数总是返回相同结果；引擎据此允许安全的索引边界预计算。默认保留逐行调用语义。不要在委托内重入同一数据库执行写操作。

## 执行与性能

强类型单、双参数入口通过绑定后的委托直接调用 C#，不使用反射调用或逐行参数数组。SQL 值装箱、结果物化及 BLOB 所有权复制仍可能分配；通用可变参数入口会分配参数数组。

未注册函数的连接不创建函数字典，普通查询只执行空引用检查。注册后仅引用用户函数的查询绕过不支持这类表达式的旧专用计划，通用执行器仍可利用安全的主键或索引候选。注册与替换会清理可能捕获旧委托或旧返回类型的缓存。

`ORDER BY` 使用投影别名时复用已计算的值，避免易变函数执行两次。`COALESCE` 执行时只计算到第一个非 NULL 参数；绑定时仍检查所有参数是否合法。

强类型与通用数组注册入口的 Windows / Linux 专项实测见 [开发验证](DEVELOPMENT-VALIDATION.md)。上述实现和专项结果不能推导为比 SQLite 用户函数更快；现有 [性能报告](PERFORMANCE.md) 对应已发布预览版的历史实测，不包含这些新增函数场景。
