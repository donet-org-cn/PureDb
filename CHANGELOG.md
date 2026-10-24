# 1.0.0-preview.2

2026-10-24：核心包和纯托管兼容包同步发布 `1.0.0-preview.2`。

- 修复 BLOB / JSON 表缓存容量过度估算导致的重复整表解码；不扩大默认预算，增加缓存复用、写后失效和关闭缓存回归。
- 核心与兼容库声明 IsAotCompatible；JSON 与目录使用源生成元数据，ADO.NET 类型与模式表通过实际 AOT 验证。
- 查询最终物化独立为小方法，已知结果长度时一次分配；保留函数求值及 BLOB 所有权语义。
- 新增实际 Native AOT 的 7 组功能及 230 条差分测试，Windows x64 / Linux ARM64 同时测量三轮查询、写入、CPU、RSS、部署体积及 21 次新进程启动。
- CI / 发布增加 Windows x64、Linux x64、Linux ARM64 AOT 门禁；发布前用实际 NuGet 包编译并运行 AOT。
- 更新 [AOT 对比](docs/AOT-PERFORMANCE.md)、[缓存配对报告](docs/CACHE-BUDGET-PERFORMANCE.md) 和全部最新完整对照，保留 SQLite 更好的项目。

- SQL / ADO.NET 连接级 C# 标量函数，支持固定数量重载、可变参数、NULL 和 BLOB 所有权隔离。
- 未注册函数时延迟分配注册表；强类型调用避免反射及逐行参数数组。
- 修复 ORDER BY 投影别名重复求值与 COALESCE 无用分支求值。
- 主库及兼容库中文 XML 文档、参数与返回值说明、统一代码格式。
- CI 增加源码文档检查、格式检查及 NuGet 中文 XML 文件校验。
- 小分页非索引排序使用有界堆，纯列查询延迟投影，表达式查询复用被淘汰候选的投影与排序键数组；保留稳定顺序和用户函数求值语义。
- 整数对整数比较直接处理，跳过不必要的 JSON 解包和 SQL 存储类别判断，混合数值比较保留原语义。
- 增加 160 条 SQLite 排序差分查询、并发与语义验证，以及 Windows / Linux 优化前后、CPU 时间和托管分配专项报告。

- 补齐普通及受控递归 CTE、WITH 增删改、派生表、标量 / IN / EXISTS 及相关子查询、UNION / UNION ALL / INTERSECT / EXCEPT。
- 增加排名、偏移、取值及聚合窗口，ROWS / RANGE / GROUPS、EXCLUDE、命名窗口及分组后窗口；聚合帧使用前缀统计及区间树。
- 增加复合索引定义、唯一性、左前缀和范围二分定位、多列升降序及反向扫描；索引数据按版本惰性构建，尚非分页增量复合 B-tree。
- 增加持久化外键、五种引用动作及延迟检查、视图、BEFORE / AFTER / INSTEAD OF 行触发器和原子撤销。
- 增加 SQL BEGIN / COMMIT / END / ROLLBACK 和嵌套 SAVEPOINT / ROLLBACK TO / RELEASE，ADO.NET 保存点、DDL 回滚；普通语句失败保留事务以便修正。
- 增加 JSON5 输入、json_valid 标志 2、json_each / json_tree 及左侧相关 JSON 连接。
- 新增 [SQL 支持与不支持清单](docs/SQL-SUPPORT.md)；新增模式尚未接入 SQLite 文件格式，兼容后端及导出显式拒绝，验证拒绝后原文件不变。
- 扩展状态按需分配，保留普通查询快路径；保存点记录增量撤销，外键主键检查直接查索引，事务内缓存父键及索引绑定。

- CHECK 列级及表级约束，支持重开恢复、SQL / ADO.NET 原子事务及函数替换后的重新绑定；兼容 SQLite 模式显式拒绝 CHECK 持久化。
- UPSERT 单分支 DO NOTHING / DO UPDATE、excluded 引用及过滤条件，复用事务唯一映射，保留触发器、外键与约束失败回滚。
- Shared 同代次日志只追赶新增尾部，池化缓冲区及独立随机读句柄避免文件头预读；保留完整 CRC、边界检查及故障回退，按用户批准方案验证。
- 排序改用紧凑二叉堆、内联整数比较，只为最终返回行投影与复制 BLOB；JSON 完整树改为弱引用，保留两个常用标量路径。
- Windows x64 / Linux ARM64 使用相同构建重测完整增删改查、CPU、RSS、文件及 ADO.NET；只保留最新对照。

完整 SQLite SQL、类型亲和性、锁协议、通用去相关优化和分页增量复合索引仍未实现；CPU、内存及性能未全面领先。

# 1.0.0-preview.1

首个公开预览版。

- 纯 .NET 10 原生文件数据库，KV 与 SQL API。
- SQL 子集、JOIN、JSON / JSONB、参数化查询、批量操作及版本化查询缓存。
- 分页检查点、持久整数索引、CRC32、事务日志恢复、分组刷盘与 Shared 多进程访问。
- 内置 ADO.NET 提供程序和乐观 Serializable 事务。
- 独立、可选的纯 .NET SQLite 文件兼容包，支持范围见兼容文档。
- Windows x64 / Linux ARM64 测试、最新 SQLite 对比证据、GitHub Actions 自动预览发布。

当前性能、CPU、内存未全面超过 SQLite；完整 SQL、WAL 兼容及兼容包的通用增量写入尚未实现。
