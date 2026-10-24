# 1.0.0-preview.2（开发中，未发布）

- SQL / ADO.NET 连接级 C# 标量函数，支持固定数量重载、可变参数、NULL 和 BLOB 所有权隔离。
- 未注册函数时延迟分配注册表；强类型调用避免反射及逐行参数数组。
- 修复 ORDER BY 投影别名重复求值与 COALESCE 无用分支求值。
- 主库及兼容库中文 XML 文档、参数与返回值说明、统一代码格式。
- CI 增加源码文档检查、格式检查及 NuGet 中文 XML 文件校验。

复合索引、外键、视图、触发器、CTE、窗口函数、子查询、UNION、JSON5/表值 JSON 函数及保存点仍在扩展计划中。

# 1.0.0-preview.1

首个公开预览版。

- 纯 .NET 10 原生文件数据库，KV 与 SQL API。
- SQL 子集、JOIN、JSON / JSONB、参数化查询、批量操作及版本化查询缓存。
- 分页检查点、持久整数索引、CRC32、事务日志恢复、分组刷盘与 Shared 多进程访问。
- 内置 ADO.NET 提供程序和乐观 Serializable 事务。
- 独立、可选的纯 .NET SQLite 文件兼容包，支持范围见兼容文档。
- Windows x64 / Linux ARM64 测试、最新 SQLite 对比证据、GitHub Actions 自动预览发布。

当前性能、CPU、内存未全面超过 SQLite；完整 SQL、WAL 兼容及兼容包的通用增量写入尚未实现。
