# PureDb 使用与支持范围

当前包与主分支版本：`1.0.0-preview.2`。用户函数等 API 见 [README](../README.md) 和 [用户函数](USER-FUNCTIONS.md)。

## SQL、JSON 和 JOIN 示例

```csharp
using PureDb;

await using var db = new SqlDatabase("app.pdb", new SqlDatabaseOptions
{
    Storage = new DatabaseOptions
    {
        Access = DatabaseAccess.Shared,
        Durability = Durability.Full
    }
});
await db.ExecuteAsync("""
    CREATE TABLE IF NOT EXISTS users(
        id INTEGER PRIMARY KEY, name TEXT NOT NULL, profile JSON
    );
    CREATE TABLE IF NOT EXISTS orders(
        id INTEGER PRIMARY KEY, user_id INTEGER, amount REAL
    );
    CREATE INDEX IF NOT EXISTS orders_user ON orders(user_id);
    CREATE INDEX IF NOT EXISTS users_age ON users(json_extract(profile,'$.age'));
    """);
await db.ExecuteTransactionAsync([
    new SqlCommand("INSERT INTO users VALUES(@id,@name,@profile)",
        new Dictionary<string, object?>
        {
            ["id"] = 1L, ["name"] = "Alice", ["profile"] = "{\"age\":25}"
        }),
    new SqlCommand("INSERT INTO orders VALUES(10,1,99.5)")
]);
var result = db.Query("""
    SELECT u.name, json_extract(u.profile,'$.age') AS age, o.amount
    FROM users u LEFT JOIN orders o ON u.id=o.user_id
    WHERE json_extract(u.profile,'$.age') >= @age
    ORDER BY u.id,o.id LIMIT 20
    """, new Dictionary<string, object?> { ["age"] = 18L });
Console.WriteLine(result.Plan);
await db.ExecuteAsync("""
    UPDATE users SET profile=json_set(profile,'$.age',26) WHERE id=1
    """);
```

`Query` 返回列名、已物化的行和执行计划。BLOB 为独立副本，文本为不可变字符串。参数支持具名参数及 `?1` 等值参数，标识符不能通过参数替换。

不含显式事务控制时，每次 `ExecuteAsync` 中的多条 DDL/DML 原子提交。`ExecuteTransactionAsync` 可给每条 SQL 独立参数，支持事务内前面语句的改动。约束检查在写锁/进程锁下完成，错误使整批回滚，写入器继续可用；多进程 `UPDATE ... SET value=value+1` 不会通过客户端读改写丢失更新。preview.2 支持 SQL BEGIN / COMMIT / ROLLBACK 和嵌套保存点，跨调用采用乐观 Serializable。显式事务中的普通语句失败只撤销该语句；含事务控制语句的批处理按显式边界执行，详见 [SQL 支持清单](SQL-SUPPORT.md) 和 [ADO.NET](ADO.NET.md)。

## SQL 支持范围

- DDL：表、单列及复合索引、唯一与 JSON_EXTRACT 路径索引、外键、视图、行触发器；支持对应的删除及存在性子句。
- 类型：INTEGER、REAL、TEXT、BLOB、JSON、JSONB，以及 NOT NULL、单列/复合 UNIQUE、常量 DEFAULT 和 CHECK。每张表需一个 INTEGER PRIMARY KEY，SQL 主键范围 `[0,2^40)`，表标识存于内部键高位。
- DML：多行 INSERT VALUES、INSERT SELECT、DEFAULT VALUES、UPDATE SET、DELETE WHERE，支持 WITH 前缀及单分支 ON CONFLICT DO NOTHING / DO UPDATE。详细冲突目标与限制见 [SQL 支持清单](SQL-SUPPORT.md)。
- 查询：列/表达式、别名、星号、参数、WHERE、DISTINCT、GROUP BY、HAVING、ORDER BY、LIMIT/OFFSET、投影别名及排序序号、无 FROM 的常量查询。
- 连接：INNER、LEFT、RIGHT、FULL OUTER、CROSS，多表与自连接。可提取等值条件时使用哈希/缓存索引查找，否则使用嵌套循环；NULL 键不互相匹配。
- 表达式：算术、比较、AND/OR/NOT、BETWEEN、IN、IS NULL、LIKE、字符串拼接和 NULL 三值逻辑。
- 函数：COUNT、SUM、AVG、MIN、MAX、LENGTH、LOWER、UPPER、COALESCE 与 JSON 函数。

preview.2 还支持普通及受控递归 CTE、相关标量 / IN / EXISTS 子查询、FROM 派生表、集合运算、窗口函数、CASE、SQL 事务和保存点。逐条语法、例子、实现方式及明确未支持项集中在独立的 [SQL 支持与不支持清单](SQL-SUPPORT.md)，包括原版文件和 SQLite 文件后端的差异。ALTER TABLE、RETURNING、COLLATE 等仍未实现；ON CONFLICT 支持已列出的单分支 UPSERT 子集。采用严格类型，未实现 SQLite 的全部类型亲和性；排序使用 .NET ordinal，LIKE 和大小写使用 invariant Unicode，与 SQLite 部分规则不同。REAL 为有限 double，整数算术溢出报错。

## JSON / JSONB

JSON 列验证并保存紧凑的标准 JSON 文本；JSONB 列保存 PureDb 自有 PJB1 二进制语法树，支持从文本转换。JSONB **不与 SQLite 的二进制格式兼容**，迁移需使用 JSON 文本中转。

支持 json/jsonb、json_valid、json_extract/jsonb_extract、json_type、json_array_length、json_set/jsonb_set、json_insert、json_replace、json_remove、json_object、json_array、json_quote、json_patch、json_array_insert，以及对应已实现操作的 jsonb 变体。支持 `->` 与 `->>`。

路径支持字段、带双引号的字段、数组下标、倒数下标，编辑支持末尾追加。JSON 函数产生的 JSON 子类型在表达式内部保留，嵌套构造不会被错误地引用成普通字符串。

`json_valid(NULL)` 返回 SQL NULL；单参数只验证标准 JSON 文本；PureDb JSONB 可用标志 8 严格验证、标志 4 检查格式头。标志 2 接受 JSON5。JSON 最大深度 128；preview.2 已支持 JSON5 输入、json_each / json_tree 和左侧相关 JSON 连接。不支持通配 JSONPath、SQLite JSONB 互通及所有 SQLite 宽松输入规则，详见 [SQL 清单](SQL-SUPPORT.md)。

路径读取采用弱引用解析缓存，文档字符串释放后允许回收解析树。路径索引更新/删除后按表版本失效，多进程追赶或压缩更换 generation 后重新构建。参考：[SQLite JSON 文档](https://www.sqlite.org/json1.html)。

## 存储与本轮优化

数据以顺序事务日志保存，包含长度、反码、CRC32 和操作列表，值保存在磁盘。主键使用分页检查点、内存增量和有界热点缓存；检查点有效时，启动只加载元数据并恢复检查点后的日志尾部，无需扫描全部历史。检查点丢失或失效时仍需扫描重建。支持的整数 SQL 范围索引按需读取持久化页面，其他索引可能需要在内存构建。

本轮优化包括：

- ADO.NET 提供程序与乐观 Serializable 事务，预编译表达式投影及版本化字段类型元数据。
- Buffered SQL 在调用线程提交，减少队列往返；共享协调戳内存映射，日志追赶合并预读。
- 内部不可变页面视图缓存 BLOB，避免缓存整表时复制整列，公开结果仍是独立副本。
- 分组计数元数据、JSON 派生列与转换弱引用缓存；数值扫描归约，缓存更新按数据版本失效。
- 缓存独立计数替代字典全局 Count，避免淘汰时锁争用；同步随机读取采用同步句柄。

- CRC32 在支持 PCLMULQDQ 的处理器上使用 SIMD 多项式折叠，支持 ARM CRC 指令时使用硬件校验，其余情况使用 slicing-by-8 路径；保持既有 IEEE CRC 文件校验格式兼容，独立逐位校验测试通过。适配的 .NET MIT 源码声明见 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。
- Span 行解码、按投影跳过无关字段，直接读取只读页中的 SQL 行，减少复制和流对象分配。
- 元数据 COUNT、流式聚合和分组计数，减少全表物化及中间数组。
- 按表数据版本和表结构版本失效缓存，减少无关表写入触发的重建。
- 事务内唯一索引哈希校验，替代批量操作时反复扫描。
- SQL 合并刷盘：各请求仍保留独立事务；持锁验证并顺序暂存，最终刷盘成功后才对读者开放并确认。
- 默认 64 MiB、页大小 4 KiB 的数据页缓存。缓存命中和读取慢路径分离；页缓存上限不等于进程总内存。可缓存已提交尾页前缀，新记录发布后按需扩展；不缓存提交边界之外的数据。
- 默认 64 MiB 的 SQL 行缓存估算预算；主键/索引元数据、临时结果、解析计划及弱引用 JSON 树仍会额外占用内存，这不是进程总内存硬上限。

解析计划缓存最多 1024 个 SQL 文本。缓存之外仍需读取磁盘；主索引和非空整数 SQL 范围索引现在有分页持久化文件，按需读页；它们是检查点快照，不是原地增量更新的 B+Tree。非整数/表达式索引和 KV 首次范围仍可能构建内存索引，不能把热缓存吞吐外推至超内存场景。

## 多进程和持久性

默认 `Exclusive` 限制为一个实例，可在该实例内并行读取。多个进程必须全部选择 `Shared`，混用会被拒绝。

Shared 使用 .NET 命名 Mutex 协调写入和日志追赶；稳定的内存映射协调戳允许读取本地已提交快照，不争用跨进程写锁。写入在锁内检查真实日志长度，需要时验证并追赶尾部，旧预读缓冲先失效。构造实例从有效分页索引检查点开始恢复追加尾部；协调戳 CRC/代际无效、代际变化或 mutex 被遗弃时走恢复路径。保留支持删除共享的文件句柄，压缩原子替换后旧快照仍可读。协调戳只是缓存提示，不是恢复真值。

**Shared 写入和日志追赶仍串行化，并非 SQLite WAL 的完整 MVCC。** 已在 Windows 和指定 Linux ARM64 服务器测试四进程更新、查询、压缩、强杀与持锁中断恢复；其他系统尚未实测；网络文件系统、不同会话及路径别名/硬链接不受支持。升级时应先关闭全部旧版实例。

Full 在确认前调用 Flush(true)，设备需正确实现刷盘语义。Buffered 普通提交只刷操作系统缓存，断电可能丢失已确认写入；主索引检查点和关闭会先持久化其引用的数据前缀。SQLite NORMAL 在 checkpoint 时仍可能同步，因此两者缓冲模式不是完全相同的行为。I/O 错误后提交可能不确定，需关闭重开并由业务幂等键处理。

有界队列限制等待请求数量，不限制调用方所有已分配缓冲区；应用仍需限制并发和事务大小。Full/核心键值写入的取消适用于队列准入；Buffered SQL 在调用线程持锁执行，取消适用于进入临界区前。接受写入后等待明确结果。关闭会排空已接受写入。

压缩将活跃记录写入同目录临时文件并刷盘后替换，需要额外空间并暂时阻塞操作。恢复部分的 CRC/结构损坏拒绝打开，不静默丢弃后续数据；有效检查点之前的索引页和记录在读取时校验，可设置 VerifyOnOpen=true 或调用 VerifyIntegrity() 做完整日志验证。不足完整事务的尾部会截去。进程恢复已测，完整断电/设备故障矩阵未测，不能代替备份。

## 键值 API

```csharp
await using var kv = new Database("cache.pdb");
await kv.PutAsync(1, 100, new byte[] { 1, 2, 3 });
var row = kv.Get(1);
var range = kv.Query(50, 150, limit: 20);
await kv.WriteAsync([Mutation.Put(2,100,new byte[] { 4 }), Mutation.Delete(1)]);
await kv.CompactAsync();
```

键值主键仍支持完整有符号 long 范围，范围结果按 `(IndexKey,Id)` 排序。输入在入队前复制，调用方不得在复制期间并发修改输入集合；返回值为副本。

## 验证范围

回归覆盖随机独立模型、增删改查、并发原子性、逐字节截断、独立位运算 CRC 校验、大值、事务约束、Unicode 行解码、缓存失效、JOIN/NULL/聚合/JSON、四进程更新和持锁退出恢复。另逐行逐值核对 134 条固定及随机 SQL/JSON/JOIN，与原版 SQLite 3.54.1 对照。

SQLite DLL 位于 `artifacts/sqlite/3.54.1/sqlite3.dll`，可通过 `SQLITE_LIBRARY` 指定；实际版本以程序输出为准。下载包已核对官方 SHA3-256：`c2d09c8babb9399277055c057c97a53f1ffd7c5f11d0c1f2118ffdfa39f2daa4`。

本项目仍需长期故障压力测试、超内存调优及更完整的 SQL 验证，适合先进行评估与受控使用。全面报告会同时列出优势、退步和限制。

自动脚本运行 77 组回归、134 条 SQL/JSON/JOIN、160 条排序及 230 条 SQL 扩展差分（共 524 条），并检查约束、保存点、重开及文件保护。平台验证见 [开发记录](DEVELOPMENT-VALIDATION.md)。全面基准、排序专项及优化前后报告已使用本轮相同 DLL 在 Windows 与 Linux ARM64 重测。

## ADO.NET 接入

```csharp
using PureDb.AdoNet;
await using var connection = new PureDbConnection("Data Source=app.pdb;Access=Shared;Durability=Full");
await connection.OpenAsync();
using var command = connection.CreateCommand();
command.CommandText = "SELECT COUNT(*) FROM users";
Console.WriteLine(await command.ExecuteScalarAsync());
```

支持 DbProviderFactory、参数、读取器、多结果集、DataTable/DataAdapter 和事务。事务冲突必须整体重试；未实现连接池、存储过程、KeyInfo、自动 CommandBuilder 或 EF Core。读取器先物化结果，查询取消不打断计算。完整支持边界见 [ADO.NET.md](ADO.NET.md)。

