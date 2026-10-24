# 开发分支验证记录

日期：2026-10-24。开发版本：`1.0.0-preview.2`，尚未发布。此次已完成中文源码文档和 C# 标量用户函数；其余 SQL 扩展见 [工作记录](FEATURE-WORK.md)，不能视为已经全部实现。

## 中文文档与格式

- 主库与兼容库的 746 个类型、方法、构造函数、属性、索引器及公开字段声明通过中文 XML 检查。
- 参数、类型参数和非 void 返回值逐项检查；位置记录的构造参数也覆盖。源码以严格 UTF-8 解码，检查 XML 格式和疑似替换乱码。
- 两个 NuGet 包均包含中文 XML 文档；打包验证检查实际 `.nupkg` 中的文件。
- 按 [代码规范](CODE-STYLE.md) 统一格式，CI 和预览发布流水线执行相同检查。
- 与 `02a69ff` 比较，原有 29 个源码文件的 C# token 完全相同，只改变注释和空白；7 个原有源码文件有用户函数或求值相关变更，另新增 2 个库源码文件。存储日志、检查点、SQLite 页和回滚日志实现没有功能变更。

## 功能与回归

| 验证项 | Windows x64 / .NET 10.0.13 | Linux ARM64 / .NET 10.0.1 |
| --- | --- | --- |
| 核心存储 | 11 组通过 | 11 组通过 |
| SQL / JOIN / Shared 多进程 | 16 组通过 | 16 组通过 |
| ADO.NET / 缓存 | 10 组通过 | 10 组通过 |
| 检查点 / 文件交换 | 11 组通过 | 11 组通过 |
| 纯托管 SQLite 文件兼容 | 10 组通过 | 10 组通过 |
| 新增用户函数 | 7 组通过 | 7 组通过 |
| 合计 | **65 组通过** | **65 组通过** |
| 对 SQLite 3.54.1 的 SQL / JSON / JOIN 差分查询 | **134 条通过** | **134 条通过** |

用户函数测试覆盖注册、重载、移除、替换、连接隔离、NULL、参数转换、返回值规范化、BLOB 所有权、异常回滚、并发查询、易变函数、排序别名、COALESCE 惰性分支、空结果元数据，以及兼容连接中的 SQL / ADO.NET 调用。它们不是完整 SQLite SQL 一致性的证明。

两个本地开发包通过实际包恢复、CRUD、JSON、用户函数、ADO.NET 事务和重新打开测试，并检查库内没有 P/Invoke 方法。原生 SQLite 只用于测试和对比。

## 用户函数专项：两种 PureDb 注册入口

比较强类型 `CreateFunction<string, bool>` 与通用参数数组 `CreateFunction(name, 1, SqlScalarFunction)`。**本节双方都是 PureDb，不是 SQLite 对比。** 两者在相同的 20,000 行缓存表上执行布尔筛选，业务委托恒返回 false，结果均为空。每种入口预热 500 ms、测量至少 750 ms，三轮交替顺序，取各归一化指标的中位数。

| 平台 | 注册入口 | 耗时 µs/次查询 | CPU µs/次查询 | 托管分配 B/次查询 | 谁更低 |
| --- | --- | ---: | ---: | ---: | --- |
| Windows x64 | 强类型 | **241.729** | **257.467** | **2,856** | **三项均更低** |
| Windows x64 | 通用数组 | 502.916 | 502.681 | 1,602,824 | 三项均更高 |
| Linux ARM64 | 强类型 | **633.846** | **637.755** | **2,856** | **三项均更低** |
| Linux ARM64 | 通用数组 | 1,683.863 | 1,697.913 | 1,602,824 | 三项均更高 |

此场景中，强类型入口在 Windows 耗时约为通用入口的 **1/2.08**，Linux 约为 **1/2.66**；累计托管分配减少约 **99.82%**。原因是直接委托调用、复用布尔结果的 SQL 0/1 对象，以及省去逐行参数数组。仍有每次查询的绑定和迭代器分配，不能称整个查询零分配。

CPU 列表示完成一次相同 SQL 查询消耗的进程 CPU 时间；分配列表示查询线程累计申请的托管字节，**不是 RSS、峰值内存或进程内存占用率**。Linux 使用同一组 4 个大核。空业务委托用于观察调用开销，复杂业务逻辑、返回大量行或复制 BLOB 时收益会不同。此实验不推导任何领先 SQLite 的结论，也不替代 [已发布预览版的全面对比](PERFORMANCE.md)。

原始数据：[Windows](../benchmarks/results/functions/latest/windows.json)、[Linux](../benchmarks/results/functions/latest/linux.json)。两平台使用相同托管 DLL，SHA256：`B1C917A7C5E8E1C8FFE1A65E0190723261FE1C58A117B94F566975A5005E064B`。CI 在其他路径重新构建时 DLL 校验值可能不同，这里的数值只标识本次实测构建。

复现功能和专项分配测量：

```bash
dotnet build PureDb.slnx -c Release
dotnet run --project tools/PureDb.SourceCheck -c Release --no-build
python scripts/prepare-sqlite.py
python scripts/test.py
dotnet run --project tests/PureDb.FeatureTests -c Release --no-build -- --measure-functions
```
