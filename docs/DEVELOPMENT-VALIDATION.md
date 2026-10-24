# preview.2 发布验证记录

日期：2026-10-24。版本：`1.0.0-preview.2`。已补入复合索引、外键、视图、行触发器、CTE、窗口、子查询、集合运算、JSON5 / JSON 表函数及 SQL 事务和保存点。逐项范围、实现原理和剩余缺项见 [SQL 支持清单](SQL-SUPPORT.md) 与 [工作记录](FEATURE-WORK.md)。本轮同时补充 CHECK、UPSERT、Shared 尾部追赶、排序与 JSON 缓存优化；[全面对比](PERFORMANCE.md)、[前后对照](OPTIMIZATION.md) 和 [排序专项](SORT-PERFORMANCE.md) 已用当前冻结构建重新测量。本文下方用户函数专项保留早期构建标识，仅比较两种函数注册入口。

本轮冻结核心 SHA256：`A240817D6F98A3264CB24AD474A870BC1AACA56A9466411CAB12387EB7EB04F5`，两平台完整测试和全面性能表使用同一托管构建。额外在 Windows x64 / Linux ARM64 的 .NET SDK 10.0.402、运行时 10.0.13 下真正编译并运行 Native AOT，每平台 7 组功能及 230 条扩展差分，DynamicCodeSupported=false；这些查询是上表 524 条中的重复执行，不重复累计。实际 NuGet 包另外通过 AOT 消费测试。详见 [AOT 报告](AOT-PERFORMANCE.md) 和 [缓存配对验证](CACHE-BUDGET-PERFORMANCE.md)。

## 中文文档与格式

- 主库与兼容库的 948 个类型、方法、构造函数、属性、索引器及公开字段声明通过中文 XML 检查。
- 参数、类型参数和非 void 返回值逐项检查；位置记录的构造参数也覆盖。源码以严格 UTF-8 解码，检查 XML 格式和疑似替换乱码。
- 两个 NuGet 包均包含中文 XML 文档；打包验证检查实际 `.nupkg` 中的文件。
- 按 [代码规范](CODE-STYLE.md) 统一格式，CI 和预览发布流水线执行相同检查。
- 用户函数及排序阶段的验证保留其原始构建标识。本轮新增解析、执行、模式目录、撤销和事务状态，原版记录日志及检查点的编码未改变；SQLite 导出增加模式拒绝保护，未增加新模式的 SQLite 页编码。

## 功能与回归

| 验证项 | Windows x64 / .NET 10.0.13 | Linux ARM64 / .NET 10.0.1 |
| --- | --- | --- |
| 核心存储 | 11 组通过 | 11 组通过 |
| SQL / JOIN / Shared 多进程 | 17 组通过 | 17 组通过 |
| ADO.NET / 缓存 | 11 组通过 | 11 组通过 |
| 检查点 / 文件交换与 Shared 恢复边界 | 12 组通过 | 12 组通过 |
| 纯托管 SQLite 文件兼容 | 10 组通过 | 10 组通过 |
| 新增用户函数 | 7 组通过 | 7 组通过 |
| 新增排序语义与路径 | 2 组通过 | 2 组通过 |
| AOT 功能路径（此表按 JIT 运行） | 7 组通过 | 7 组通过 |
| 合计 | **77 组通过** | **77 组通过** |
| 对 SQLite 3.54.1 的 SQL / JSON / JOIN 差分查询 | **134 条通过** | **134 条通过** |
| 新增排序差分查询 | **160 条通过** | **160 条通过** |
| 新增 SQL 扩展差分查询 | **230 条通过** | **230 条通过** |
| 差分合计 | **524 条通过** | **524 条通过** |

用户函数测试覆盖注册、重载、移除、替换、连接隔离、NULL、参数转换、返回值规范化、BLOB 所有权、异常回滚、并发查询、易变函数、排序别名、COALESCE 惰性分支、空结果元数据，以及兼容连接中的 SQL / ADO.NET 调用。它们不是完整 SQLite SQL 一致性的证明。

排序测试覆盖 NULL、文本、BLOB、混合升降序、表达式、多列排序、投影别名遮蔽、序号、JOIN、空输入、LIMIT 为零、分页边界和回退路径，并检查易变函数、异常、并发缓冲区、稳定顺序和 BLOB 所有权。两平台分别测量同一优化前后构建；公开原始样本和报告会检查中位数计算。

两个预览包通过实际包恢复、CRUD、JSON、用户函数、ADO.NET 事务和重新打开测试，并检查库内没有 P/Invoke 方法。原生 SQLite 只用于测试和对比。

新增 SQL 扩展测试另外覆盖模式重开恢复、空派生表及窗口类型元数据、普通语句错误后的事务可用性、同名与 DDL 保存点回滚、延迟外键提交失败后修正、复合外键级联、Shared 冲突及不可见的暂存写入。兼容后端复用扩展查询；尚未支持的新增模式写入被拒绝，验证原 SQLite 文件字节不变、原生 integrity_check 为 ok。随机窗口查询为 ROWS 指定唯一排序键，避免将并列行的未定义次序误作语义差异。

扩展路径的性能措施包括按需分配会话状态、保留普通查询计划、增量撤销、缓存外键父键和索引委托、主键外键直接检查索引、只校验相关变更行、标量 / EXISTS 的安全早停，以及窗口前缀统计和区间树。此处描述实现，不据此推算领先 SQLite 的倍数；新增特性仍有物化及首次索引构建成本。

本轮额外验证 CHECK 的多行失败回滚、重开持久化及 C# 函数替换，UPSERT 的主键 / 复合唯一目标、excluded、WHERE、DO NOTHING、触发器、自动主键及错误绑定。JSON 缓存验证强制 GC 后重解析、三个路径交替并发访问及二进制返回值所有权。Shared 日志覆盖大小帧与缓冲区边界、每字节截断、CRC 和操作边界损坏及前缀缩短。

当前两平台实测 DLL SHA256：`F57A192E23B4467BAEAF12D40B4A5849C3E09E80A8D408120B55E8F517DCC41B`。

## 用户函数专项：两种 PureDb 注册入口

比较强类型 `CreateFunction<string, bool>` 与通用参数数组 `CreateFunction(name, 1, SqlScalarFunction)`。**本节双方都是 PureDb，不是 SQLite 对比。** 两者在相同的 20,000 行缓存表上执行布尔筛选，业务委托恒返回 false，结果均为空。每种入口预热 500 ms、测量至少 750 ms，三轮交替顺序，取各归一化指标的中位数。

| 平台 | 注册入口 | 耗时 µs/次查询 | CPU µs/次查询 | 托管分配 B/次查询 | 谁更低 |
| --- | --- | ---: | ---: | ---: | --- |
| Windows x64 | 强类型 | **241.729** | **257.467** | **2,856** | **三项均更低** |
| Windows x64 | 通用数组 | 502.916 | 502.681 | 1,602,824 | 三项均更高 |
| Linux ARM64 | 强类型 | **633.846** | **637.755** | **2,856** | **三项均更低** |
| Linux ARM64 | 通用数组 | 1,683.863 | 1,697.913 | 1,602,824 | 三项均更高 |

此场景中，强类型入口在 Windows 耗时约为通用入口的 **1/2.08**，Linux 约为 **1/2.66**；累计托管分配减少约 **99.82%**。原因是直接委托调用、复用布尔结果的 SQL 0/1 对象，以及省去逐行参数数组。仍有每次查询的绑定和迭代器分配，不能称整个查询零分配。

CPU 列表示完成一次相同 SQL 查询消耗的进程 CPU 时间；分配列表示查询线程累计申请的托管字节，**不是 RSS、峰值内存或进程内存占用率**。Linux 使用同一组 4 个大核。空业务委托用于观察调用开销，复杂业务逻辑、返回大量行或复制 BLOB 时收益会不同。此实验不推导任何领先 SQLite 的结论，也不替代 [本轮构建的全面对比](PERFORMANCE.md)。

原始数据：[Windows](../benchmarks/results/functions/latest/windows.json)、[Linux](../benchmarks/results/functions/latest/linux.json)。两平台使用相同托管 DLL，SHA256：`B1C917A7C5E8E1C8FFE1A65E0190723261FE1C58A117B94F566975A5005E064B`。CI 在其他路径重新构建时 DLL 校验值可能不同，这里的数值只标识本次实测构建。

复现功能和专项分配测量：

```bash
dotnet build PureDb.slnx -c Release
dotnet run --project tools/PureDb.SourceCheck -c Release --no-build
python scripts/prepare-sqlite.py
python scripts/test.py
dotnet run --project tests/PureDb.FeatureTests -c Release --no-build -- --measure-functions
```
