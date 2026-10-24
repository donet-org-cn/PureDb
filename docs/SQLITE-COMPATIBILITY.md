# 可选的纯 .NET SQLite 文件兼容库

安装 `PureDb.SqliteCompatibility` `1.0.0-preview.2`；它依赖同版本 PureDb，核心不反向依赖兼容库。两个包均无原生依赖。

```csharp
using var db = PureDb.SqliteCompatibility.SqliteCompatibility.Open("existing.sqlite");
var rows = db.Query("SELECT id FROM users LIMIT 20");
```

当前实现 UTF-8、4096 字节页、受支持的普通整数主键表、B-tree、record、overflow 及独占回滚日志子集。SQL、JSON 和 JOIN 由主库执行器处理。兼容 ADO.NET 入口为 `PureDb.SqliteCompatibility.SqliteConnection` / `SqliteProviderFactory`。

| 能力 | 当前支持范围 |
| --- | --- |
| 读写已有 SQLite 文件 | 受支持的模式和编码 |
| 默认提交 | 可能重建整库，写入成本与库大小有关 |
| 实验增量写入 | `EnableIncrementalUpdates=true`；特定等长、同叶页和整数索引变更，其他情况回退 |
| 日志恢复 | 实现的回滚日志协议；未识别或异常日志拒绝 |
| 多进程同时访问 | 本预览版兼容后端要求独占使用；Linux 需显式设置 `LinuxExclusiveProcess=true` |
| WAL / UTF-16 / 任意页尺寸 / 任意 SQLite schema | 不支持，明确拒绝 |
| JSONB 二进制互通 | 不支持；使用 JSON 文本交换 |
| preview.2 扩展查询和写入 | 原有受支持表可执行 CTE、集合、子查询、窗口、JSON5、JSON 表函数及单分支 UPSERT |
| preview.2 新增持久化模式 | 复合索引、外键、默认值、CHECK、视图和触发器尚未接入页写入；明确拒绝，原版格式支持 |

`LinuxExclusiveProcess` 与 `EnableIncrementalUpdates` 也可通过兼容 ADO.NET 连接属性在 Open 前设置，不属于连接字符串参数。原生格式 Shared 模式与这里的 SQLite 兼容独占模式是两个不同后端。

完整 SQL 分类见 [SQL 支持清单](SQL-SUPPORT.md)。主库新增执行器能力不代表 SQLite 页格式已支持相应持久化模式。导入导出也拒绝尚未接入的新模式；测试检查失败时原文件字节不变、不发布残缺文件，并通过原生 integrity_check。

默认 `new SqlDatabase("app.pdb")` 始终使用 PureDb 文件格式。最新性能表只评价原生格式，不将其成绩用于声称兼容后端也有相同速度。现有回归包含 SQLite integrity_check、跨引擎读取/写入、overflow、索引和进程强杀恢复验证。
