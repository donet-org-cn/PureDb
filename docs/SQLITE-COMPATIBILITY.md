# 可选的纯 .NET SQLite 文件兼容库

安装 `PureDb.SqliteCompatibility` `1.0.0-preview.3`。它依赖同版本 PureDb，核心不反向依赖兼容库；两个包均无原生依赖。原生 SQLite 只参与测试与性能对照。

```csharp
using var db = PureDb.SqliteCompatibility.SqliteCompatibility.Open("existing.sqlite");
var rows = db.Query("SELECT id FROM users LIMIT 20");
```

Linux 使用独立进程，显式设置 `new SqliteCompatibilityOptions { LinuxExclusiveProcess = true }`。该选项要求同一进程没有其他库或原生句柄同时打开这个文件；POSIX 文件锁具有进程级语义。Windows 使用 SQLite 字节范围锁，连接存续期间独占文件。当前并不提供 SQLite 原生的并发读写体验。

| 能力 | preview.3 支持范围 | 限制 |
| --- | --- | --- |
| 文件页 | 512、1024、2048、4096、8192、16384、32768、65536 字节 | 保留原页大小；reserved bytes 必须为 0 |
| 文本编码 | UTF-8、UTF-16LE、UTF-16BE | 保留编码，BINARY 排序及文本/BLOB 转换使用该编码 |
| 表数据 | INTEGER PRIMARY KEY 普通 rowid 表、record、B-tree、overflow | 主键 `[0,2^40)`，列类型受核心严格类型限制 |
| 持久模式 | 单列/复合/混合方向索引、UNIQUE、外键、DEFAULT、CHECK、视图、行触发器 | 仅 SQL 清单中受支持的语法和表达式 |
| 默认写入 | 同目录生成完整镜像后原地更新主文件 | 镜像构建仍随库大小增长；不适合大库高频点写 |
| 实验增量写入 | `EnableIncrementalUpdates=true` | 4096 字节 UTF-8，受支持的等长行/单叶页及整数索引变更；其他情况安全回退 |
| 部分旧页日志 | 完整镜像和增量路径均仅备份实际要覆盖或截断的旧页 | 比较镜像仍需读取数据；不是所有写入已变成页级增量 |
| 回滚恢复 | 单日志头、已知扇区尺寸及有限记录数的回滚日志 | 完整边界、页号和校验检查后才写回；未知协议拒绝 |
| SQL / ADO.NET | 复用核心 SQL、用户函数和 Provider | API 为兼容包的 SqliteConnection / SqliteProviderFactory |
| 导入导出 | 上述持久模式、有效 Unicode、BLOB 默认值 | 导入先建表、装载行、建索引，最后创建视图和触发器 |

以下仍不支持：WAL、auto_vacuum/指针图、非零 reserved bytes、AUTOINCREMENT 和 SQLite 内部统计表、隐式 rowid 表、WITHOUT ROWID、复合主键、任意类型亲和性、COLLATE/部分索引、FTS/RTree/虚拟表、原生 SQLite JSONB 二进制、跨库事务和超级日志、多日志头热日志。普通 SQLite 文件可能使用这些能力，不能据此声称“任意 SQLite 文件可直接打开”。

导入需要外键依赖的父行先存在；循环依赖、子表先于父表的数据及 ALTER TABLE 产生的短物理行可能需要显式迁移。此类导入失败不发布目标文件。JSON 文本可交换；PureDb JSONB 使用自有 PJB1 编码。

恢复顺序：持独占锁，验证日志完整性，恢复旧页并恢复原始文件长度，再清理日志。提交顺序：刷旧页日志，覆盖主文件页，刷主文件，最后清除日志作为提交点。新增页不需要旧页副本，回滚时按原长度截断；缩小时被截断的旧尾页必须备份。测试在自动生成的数据库上强杀进程，并分别交给 SQLite 和托管实现恢复。它是进程故障注入，不等于真实断电或存储设备故障认证。

本轮分别报告 [175 条公开 SQL 用例与 64 组文件互操作测试](COMPATIBILITY-REPORT.md)，不是 SQLite 官方完整测试套件的通过率。SQLite 协议依据：[文件格式](https://www.sqlite.org/fileformat.html)、[原子提交](https://www.sqlite.org/atomiccommit.html)。

默认 `new SqlDatabase("app.pdb")` 始终使用 PureDb 原版格式。[全面性能表](PERFORMANCE.md) 的成绩属于原版格式，不能外推到本兼容后端。原版 Shared 多进程模式也不同于本兼容后端的独占文件模式。
