# SQL 兼容性扩展工作记录

本次目标：继续完善性能与 SQLite 语义兼容，补齐复合索引、外键、视图、触发器、CTE、窗口函数、子查询、UNION、JSON5、json_each/json_tree、SAVEPOINT 和事务语法，以及 C# 用户函数。新旧公开 API 需中文 XML 注释（类、属性、方法、参数、返回值、重要异常），并统一代码格式。

源码版本为 `1.0.0-preview.2`，NuGet 仍为 preview.1。本轮已实现下列扩展，详细语法、示例、原理及限制集中在 [SQL 支持清单](SQL-SUPPORT.md)。这些勾选表示所列范围完成，不表示完整 SQLite SQL 或文件协议兼容。已发布版本 `v1.0.0-preview.1` 保留，不重写标签。

- [x] C# 标量函数：SqlDatabase / ADO.NET 注册，按名称和参数数量解析，NULL、类型、异常、缓存失效和并发验证。实现及边界见 [用户函数](USER-FUNCTIONS.md)。
- [x] 小分页排序优化：有界堆、纯列延迟投影、表达式候选缓冲区复用、整数比较快路径、稳定顺序、别名及易变函数语义保护；Windows / Linux 优化前后和 SQLite 专项见 [排序报告](SORT-PERFORMANCE.md)。分组、DISTINCT、大分页保留原路径。
- [x] 查询组合：UNION / UNION ALL / INTERSECT / EXCEPT，普通及受控递归 CTE，WITH 增删改，FROM / 标量 / IN / EXISTS 及相关子查询。
- [x] 窗口函数：排名、偏移、取值及聚合窗口，分区、命名窗口、分组后窗口、ROWS / RANGE / GROUPS、EXCLUDE 和 NULL 语义；前缀统计与区间树控制帧计算成本。
- [x] 复合索引：多列升降序、左前缀及范围查找、唯一性、NULL、修改失效与定义持久化；数据按版本惰性内存构建。
- [x] 视图、触发器、外键：模式持久化、语句原子撤销、外键五种动作及延迟检查、行触发器和 INSTEAD OF；SQLite 文件后端显式拒绝尚未接入的模式。
- [x] 事务语法：BEGIN / COMMIT / END / ROLLBACK，嵌套 SAVEPOINT / ROLLBACK TO / RELEASE，ADO.NET 保存点、DDL 回滚；Shared 使用乐观 DEFERRED，不能称为 SQLite 的完整锁模型。
- [x] JSON5 输入及 json_valid 标志 2，json_each / json_tree，可见列、隐藏列、路径及左侧相关 JSON 连接。
- [x] 中文 API 与实现注释、格式化、独立 SQL 支持文档及 README 矩阵更新。源码检查覆盖 922 个类型和成员声明；见 [代码规范](CODE-STYLE.md)。
- [x] Windows / Linux 回归与 SQLite 差分。各平台原有 67 组、294 条差分及新增扩展查询验证，具体数量见 [开发验证](DEVELOPMENT-VALIDATION.md)。历史性能报告保留构建标识，不冒充本轮扩展成绩。

后续仍需实现或完善：

- [ ] 分页持久化、增量维护的复合索引，降低首次构建和写后重建成本。
- [ ] 通用子查询去相关、CTE / 视图展开及谓词下推、同窗排序共享、临时结果磁盘溢出。
- [ ] 将新增模式安全接入 SQLite 文件页读写、导入导出及故障恢复；完整 WAL 和锁协议仍未实现。
- [ ] ALTER TABLE、CHECK、UPSERT / RETURNING、COLLATE、聚合 FILTER、完整内置函数等清单中的缺项。
- [ ] 本轮扩展构建的全面性能、CPU、RSS 和落盘对比重测；不能沿用旧构建成绩宣称全面领先。

用户函数预期示例：

```csharp
db.CreateFunction<string, bool>("RELIGIBLE", user => IsEligible(user));
var result = db.Query("SELECT * FROM Awards WHERE RELIGIBLE(User)");
```

函数只存在于注册它的连接；SQL 参数是列中的标量值。复杂 C# 对象需由函数自行从 JSON 等存储值转换。未实现的协议、语法或优化形状必须显式报错，不静默忽略。
