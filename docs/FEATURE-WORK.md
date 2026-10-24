# SQL 兼容性扩展工作记录

本次目标：继续完善性能与 SQLite 语义兼容，补齐复合索引、外键、视图、触发器、CTE、窗口函数、子查询、UNION、JSON5、json_each/json_tree、SAVEPOINT 和事务语法，以及 C# 用户函数。新旧公开 API 需中文 XML 注释（类、属性、方法、参数、返回值、重要异常），并统一代码格式。

已完成的用户函数、查询优化及中文文档已由 `feature/sql-compatibility` 合并到 `main`，主分支源码版本为 `1.0.0-preview.2`。下列未完成项继续保留在扩展计划中。已发布版本 `v1.0.0-preview.1` 保留，不重写标签。

- [x] C# 标量函数：SqlDatabase / ADO.NET 注册，按名称和参数数量解析，NULL、类型、异常、缓存失效和并发验证。实现及边界见 [用户函数](USER-FUNCTIONS.md)。
- [x] 小分页排序优化：有界堆、纯列延迟投影、表达式候选缓冲区复用、整数比较快路径、稳定顺序、别名及易变函数语义保护；Windows / Linux 优化前后和 SQLite 专项见 [排序报告](SORT-PERFORMANCE.md)。分组、DISTINCT、大分页保留原路径。
- [ ] 查询组合：UNION / UNION ALL、非递归及受控递归 CTE、FROM/标量/IN/EXISTS 子查询，相关子查询。
- [ ] 窗口函数：排名、偏移与聚合窗口，正确的分区、排序、帧和 NULL 语义。
- [ ] 复合索引：多列排序、左前缀查找、唯一性、NULL、修改失效与持久化。
- [ ] 视图、触发器、外键：模式持久化、原子回滚、约束和级联语义，兼容后端的显式范围保护。
- [ ] 事务语法：BEGIN / COMMIT / END / ROLLBACK，嵌套 SAVEPOINT / ROLLBACK TO / RELEASE，ADO.NET 保存点；不能把乐观事务称为完全一致的 SQLite 锁模型。
- [ ] JSON5 解析、JSON 表值函数以及 SQLite 差分测试。
- [x] 中文 API 与实现注释、格式化、文档支持范围更新。主库与兼容库 758 个类型、成员声明通过中文摘要、参数及返回值检查；见 [代码规范](CODE-STYLE.md)。
- [x] Windows / Linux 回归、针对新增路径的性能与分配验证。两平台各 67 组回归及 294 条 SQLite 差分查询通过，新增函数专项结果见 [开发验证](DEVELOPMENT-VALIDATION.md)。历史预览版跑分不冒充新增功能构建成绩。

用户函数预期示例：

```csharp
db.CreateFunction<string, bool>("RELIGIBLE", user => IsEligible(user));
var result = db.Query("SELECT * FROM Awards WHERE RELIGIBLE(User)");
```

函数只存在于注册它的连接；SQL 参数是列中的标量值。复杂 C# 对象需由函数自行从 JSON 等存储值转换。未实现的协议、语法或优化形状必须显式报错，不静默忽略。
