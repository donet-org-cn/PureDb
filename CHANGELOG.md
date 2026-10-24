# 1.0.0-preview.1

首个公开预览版。

- 纯 .NET 10 原生文件数据库，KV 与 SQL API。
- SQL 子集、JOIN、JSON / JSONB、参数化查询、批量操作及版本化查询缓存。
- 分页检查点、持久整数索引、CRC32、事务日志恢复、分组刷盘与 Shared 多进程访问。
- 内置 ADO.NET 提供程序和乐观 Serializable 事务。
- 独立、可选的纯 .NET SQLite 文件兼容包，支持范围见兼容文档。
- Windows x64 / Linux ARM64 测试、最新 SQLite 对比证据、GitHub Actions 自动预览发布。

当前性能、CPU、内存未全面超过 SQLite；完整 SQL、WAL 兼容及兼容包的通用增量写入尚未实现。
