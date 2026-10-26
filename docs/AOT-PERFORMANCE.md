# Native AOT：文件大小、启动、CPU、内存和 SQLite 对比

版本：`1.0.0-preview.3`。Windows x64 与指定 Linux ARM64 主机，.NET SDK 10.0.402 / 运行时 10.0.13。AOT 和自包含 JIT 使用相同源码、相同数据、相同 SQL 和相同适配层；每台机器串行、三轮交替顺序，不能跨平台比较绝对耗时。

## 实际支持与验证

核心与可选 SQLite 兼容库启用 IsAotCompatible；内置 JSON 及兼容目录序列化改用源生成元数据，ADO.NET GetFieldType 保留所需的标量类型元数据。每个平台真正发布并运行 AOT，报告验证 DynamicCodeSupported=false；编译及裁剪警告为 0。额外运行 7 组 AOT 功能测试和 230 条 SQLite 差分查询，覆盖 JSON 修改、模式重开、CHECK/UPSERT、CTE/窗口/子查询、外键/触发器/保存点、ADO.NET 模式表和纯托管 SQLite 文件交换。

GetSchemaTable 有一处精确限定的 IL2111 抑制：DataColumn 保存 Type 对象，不会访问 Type.TypeInitializer；该路径通过实际 AOT 运行验证。没有关闭全局裁剪分析或全局屏蔽 AOT 警告。这里的零警告指本轮完整发布日志未产生 IL 警告，不表示应用可以任意使用反射。

## 对比口径

- 应用文件大小是本次 SQL 测量宿主的实际发布闭包，不是一个通用的“PureDb AOT 库大小”。AOT 只保留应用可达代码，引用更多 API、兼容库或业务代码会改变大小。调试符号和 XML 文档不计入运行文件，但清单另外保留全部文件字节。

- 自包含 JIT 包含 .NET 运行时；框架依赖 JIT 不包含共享运行时，只作体积参考。主性能比较使用自包含 JIT 与 AOT，版本均为 10.0.13。SQLite 宿主独立编译、不引用 PureDb，运行体积计入 SQLite 3.54.1 动态库；系统库不计入双方。

- 10 万行，整数主键、整数索引、未索引排序列、256 字节 BLOB 及 JSON，另有 JSON 表达式索引。查询预热 500 ms，计时至少 750 ms；两边均物化结果行，复用参数与 SQLite prepared statement。PureDb 的表和派生结构可按版本缓存，首次构建不在热查询计时内。

- Full 为各自强制刷盘路径；Buffered 与 SQLite WAL/NORMAL 的 checkpoint 和断电语义不同。逐条写入逐次等待，批量以 1000 行为一个事务；写入计时不含关闭及关闭维护。新增及删除各核对行数。

- CPU µs/单位比较相同工作量；满吞吐 CPU 占用可能相近而完成量不同。RSS 为独立进程、单个库、执行访问后 GC 的驻留量；不能用托管分配判断 SQLite 原生堆大小。短 CPU 区间不排名。

- 新进程启动每组合 21 次，OS 文件缓存已热。全进程耗时由父进程测量，包含进程创建、运行时、库加载、查询、JSON 报告输出及退出；内部打开/首次查询/关闭另列。不是断电后或清空 OS 缓存的冷盘启动。

- 编译使用 Release、IlcOptimizationPreference=Speed，保留正常全球化，未使用仅适用于当前 CPU 的 native 指令集选项。CI 只检查正确性，CI 机器不参与性能排名。

## 本轮实现优化

本轮主要优化 AST 身份计划缓存、小整数装箱、全列顺序投影、JSON 表达式规范化缓存和检查点归并。兼容库同步增加页大小、编码及持久模式；生产代码仍为纯托管。详细前后对照见 [优化报告](OPTIMIZATION.md)。本报告重新发布并实测 preview.3，不复用 preview.2 的 AOT 体积或耗时。

## Windows x64

### 实际发布文件体积

| 引擎 | 部署方式 | 主程序 MiB | 运行文件合计 MiB | 含符号及文档 MiB | 说明 |
| --- | --- | --- | --- | --- | --- |
| PureDb | framework | 0.155 | 0.719 | 1.306 | 不含共享 .NET 运行时 |
| PureDb | jit | 0.155 | 77.397 | 77.983 | 含 .NET 运行时 |
| PureDb | aot | 6.178 | 6.178 | 34.834 | 含可达运行时与引擎；SQLite 行还含原生库 |
| SQLite | framework | 0.155 | 3.559 | 3.589 | 不含共享 .NET 运行时 |
| SQLite | jit | 0.155 | 80.236 | 80.266 | 含 .NET 运行时 |
| SQLite | aot | 2.368 | 5.725 | 17.525 | 含可达运行时与引擎；SQLite 行还含原生库 |

### 查询与增删改：同引擎 JIT/AOT，以及双方 AOT

| 场景 | PureDb JIT µs | PureDb AOT µs | SQLite JIT µs | SQLite AOT µs | AOT 胜方 |
| --- | --- | --- | --- | --- | --- |
| point | 0.522 | 0.511 | 5.406 | 5.478 | 🟩 PureDb 10.72× |
| range20 | 3.044 | 4.596 | 15.680 | 17.191 | 🟩 PureDb 3.74× |
| json_index | 3.884 | 3.587 | 20.714 | 24.757 | 🟩 PureDb 6.90× |
| json_scan | 27.816 | 49.220 | 4,896.138 | 5,262.757 | 🟩 PureDb 106.92× |
| sort20 | 1,569.678 | 1,838.989 | 8,177.176 | 9,064.036 | 🟩 PureDb 4.93× |
| sort4096 | 6,985.326 | 7,192.115 | 17,647.835 | 24,617.523 | 🟩 PureDb 3.42× |
| sum_scan | 1,088.481 | 742.345 | 2,940.213 | 3,678.599 | 🟩 PureDb 4.96× |
| Buffered_single | 74.678 | 14.502 | 533.453 | 601.889 | 🟩 PureDb 41.50× |
| Buffered_batch_insert | 18.941 | 3.948 | 2.925 | 2.942 | 🟥 SQLite 1.34× |
| Buffered_batch_update | 18.445 | 3.838 | 3.041 | 3.116 | 🟥 SQLite 1.23× |
| Buffered_batch_delete | 3.546 | 1.515 | 3.671 | 3.702 | 🟩 PureDb 2.44× |
| Full_single | 27,765.258 | 25,804.860 | 25,158.056 | 28,991.024 | 🟩 PureDb 1.12× |
| Full_batch_insert | 41.222 | 34.951 | 37.953 | 43.435 | 🟩 PureDb 1.24× |
| Full_batch_update | 36.002 | 39.492 | 35.025 | 44.992 | 🟩 PureDb 1.14× |
| Full_batch_delete | 30.069 | 26.798 | 33.415 | 38.465 | 🟩 PureDb 1.44× |

普通查询单位为 µs/查询，增删改为 µs/行。AOT 与 JIT 的正反收益都保留，不把更快启动解读为所有热查询更快。

### AOT 的 CPU：每单位成本与满吞吐占用

| 场景 | PureDb CPU µs | SQLite CPU µs | CPU 成本胜方 | PureDb / SQLite CPU % |
| --- | --- | --- | --- | --- |
| point | 0.511 | 5.477 | 🟩 PureDb 10.72× | 8.333 / 8.333 |
| range20 | 4.500 | 17.190 | 🟩 PureDb 3.82× | 8.160 / 8.333 |
| json_index | 3.587 | 24.241 | 🟩 PureDb 6.76× | 8.333 / 8.160 |
| json_scan | 49.219 | 5,244.755 | 🟩 PureDb 106.56× | 8.333 / 8.305 |
| sort20 | 1,838.235 | 8,847.892 | 🟩 PureDb 4.81× | 8.325 / 8.135 |
| sort4096 | 7,142.857 | 24,193.548 | 🟩 PureDb 3.39× | 8.261 / 8.190 |
| sum_scan | 741.840 | 3,676.471 | 🟩 PureDb 4.96× | 8.328 / 8.312 |
| Buffered_single | 15.625 | 46.875 | 短区间，不排名 | 8.978 / 0.532 |
| Buffered_batch_insert | 3.125 | 3.125 | 短区间，不排名 | 6.597 / 8.837 |
| Buffered_batch_update | 3.125 | 3.125 | 短区间，不排名 | 7.123 / 8.357 |
| Buffered_batch_delete | 0.000 | 3.125 | 短区间，不排名 | 0.000 / 7.035 |
| Full_single | 0.000 | 312.500 | 短区间，不排名 | 0.000 / 0.090 |
| Full_batch_insert | 3.125 | 3.125 | 短区间，不排名 | 0.745 / 0.474 |
| Full_batch_update | 3.125 | 3.125 | 短区间，不排名 | 0.598 / 0.579 |
| Full_batch_delete | 0.000 | 3.125 | 短区间，不排名 | 0.000 / 0.655 |

### 独立进程 RSS：谁更高，高多少

| 场景 | PureDb JIT MiB | PureDb AOT MiB | SQLite JIT MiB | SQLite AOT MiB | AOT 当前谁更高 |
| --- | --- | --- | --- | --- | --- |
| point | 84.52 | 58.57 | 73.10 | 53.16 | **PureDb 更高**：多 5.42 MiB，高 10.2% |
| range20 | 42.75 | 22.80 | 34.29 | 19.68 | **PureDb 更高**：多 3.12 MiB，高 15.9% |
| json_index | 134.64 | 112.85 | 31.66 | 16.61 | **PureDb 更高**：多 96.24 MiB，高 579.4% |

### 每次新进程启动与首次范围查询

| 引擎 | 部署 | 全进程 ms | 内部打开/首查/关闭 ms |
| --- | --- | --- | --- |
| PureDb | jit | 225.752 | 128.842 |
| PureDb | aot | 34.646 | 4.095 |
| SQLite | jit | 124.400 | 5.404 |
| SQLite | aot | 34.277 | 2.289 |

### 同一 10 万行数据的关闭后数据库文件

| PureDb MiB（含侧文件） | SQLite MiB（含侧文件） | 谁更小 |
| --- | --- | --- |
| 38.866 | 32.273 | 🟥 SQLite 1.20× |

AOT 改变应用部署方式，数据文件格式相同。这一行只统计最初查询库及其侧文件，不累计各写入场景生成的临时数据库。文件占用不是物理写入流量。

### 本平台结论

PureDb AOT 的运行文件比自身自包含 JIT 减少 92.0%；全进程启动中位数由 225.75 ms 变为 34.65 ms。AOT 对比 SQLite：13 项耗时领先、2 项落后、0 项接近。体积、热查询、写入、CPU 与 RSS 仍需分别判断，不能宣称全面领先。

[原始清单、各轮样本及环境](../benchmarks/results/aot/latest/windows)。

## Linux ARM64

### 实际发布文件体积

| 引擎 | 部署方式 | 主程序 MiB | 运行文件合计 MiB | 含符号及文档 MiB | 说明 |
| --- | --- | --- | --- | --- | --- |
| PureDb | framework | 0.074 | 0.639 | 1.218 | 不含共享 .NET 运行时 |
| PureDb | jit | 0.074 | 85.178 | 85.758 | 含 .NET 运行时 |
| PureDb | aot | 6.115 | 6.115 | 26.263 | 含可达运行时与引擎；SQLite 行还含原生库 |
| SQLite | framework | 0.074 | 1.222 | 1.251 | 不含共享 .NET 运行时 |
| SQLite | jit | 0.074 | 85.761 | 85.791 | 含 .NET 运行时 |
| SQLite | aot | 2.691 | 3.792 | 12.542 | 含可达运行时与引擎；SQLite 行还含原生库 |

### 查询与增删改：同引擎 JIT/AOT，以及双方 AOT

| 场景 | PureDb JIT µs | PureDb AOT µs | SQLite JIT µs | SQLite AOT µs | AOT 胜方 |
| --- | --- | --- | --- | --- | --- |
| point | 2.633 | 1.300 | 3.805 | 3.797 | 🟩 PureDb 2.92× |
| range20 | 9.336 | 11.524 | 26.922 | 27.418 | 🟩 PureDb 2.38× |
| json_index | 16.288 | 14.498 | 34.601 | 35.713 | 🟩 PureDb 2.46× |
| json_scan | 103.117 | 98.933 | 7,134.180 | 7,065.012 | 🟩 PureDb 71.41× |
| sort20 | 3,461.230 | 3,938.555 | 17,376.136 | 17,504.623 | 🟩 PureDb 4.44× |
| sort4096 | 19,741.354 | 21,471.771 | 33,234.026 | 33,559.217 | 🟩 PureDb 1.56× |
| sum_scan | 2,751.334 | 1,756.071 | 5,286.392 | 5,312.090 | 🟩 PureDb 3.02× |
| Buffered_single | 103.463 | 24.194 | 98.733 | 128.098 | 🟩 PureDb 5.29× |
| Buffered_batch_insert | 28.092 | 10.350 | 9.241 | 9.202 | 🟥 SQLite 1.12× |
| Buffered_batch_update | 42.735 | 9.081 | 3.509 | 4.580 | 🟥 SQLite 1.98× |
| Buffered_batch_delete | 9.595 | 2.927 | 5.043 | 4.435 | 🟩 PureDb 1.52× |
| Full_single | 698.176 | 829.854 | 919.134 | 1,073.928 | 🟩 PureDb 1.29× |
| Full_batch_insert | 41.861 | 27.684 | 7.879 | 18.857 | 🟥 SQLite 1.47× |
| Full_batch_update | 38.591 | 13.588 | 6.098 | 7.618 | 🟥 SQLite 1.78× |
| Full_batch_delete | 11.265 | 3.723 | 7.287 | 7.477 | 🟩 PureDb 2.01× |

普通查询单位为 µs/查询，增删改为 µs/行。AOT 与 JIT 的正反收益都保留，不把更快启动解读为所有热查询更快。

### AOT 的 CPU：每单位成本与满吞吐占用

| 场景 | PureDb CPU µs | SQLite CPU µs | CPU 成本胜方 | PureDb / SQLite CPU % |
| --- | --- | --- | --- | --- |
| point | 1.300 | 3.797 | 🟩 PureDb 2.92× | 12.498 / 12.500 |
| range20 | 11.524 | 27.417 | 🟩 PureDb 2.38× | 12.500 / 12.500 |
| json_index | 14.498 | 35.712 | 🟩 PureDb 2.46× | 12.500 / 12.500 |
| json_scan | 98.930 | 6,996.514 | 🟩 PureDb 70.72× | 12.500 / 12.500 |
| sort20 | 3,938.508 | 17,504.279 | 🟩 PureDb 4.44× | 12.499 / 12.500 |
| sort4096 | 21,466.686 | 33,558.609 | 🟩 PureDb 1.56× | 12.499 / 12.500 |
| sum_scan | 1,755.883 | 5,311.993 | 🟩 PureDb 3.03× | 12.499 / 12.500 |
| Buffered_single | 24.179 | 41.271 | 🟩 PureDb 1.71× | 12.407 / 4.426 |
| Buffered_batch_insert | 10.347 | 9.120 | 🟥 SQLite 1.13× | 12.498 / 12.483 |
| Buffered_batch_update | 9.099 | 4.580 | 🟥 SQLite 1.99× | 12.522 / 12.506 |
| Buffered_batch_delete | 2.929 | 4.427 | 🟩 PureDb 1.51× | 12.511 / 12.500 |
| Full_single | 273.400 | 104.600 | 🟥 SQLite 2.61× | 4.118 / 1.217 |
| Full_batch_insert | 25.548 | 16.132 | 🟥 SQLite 1.58× | 11.536 / 10.694 |
| Full_batch_update | 11.182 | 5.293 | 🟥 SQLite 2.11× | 10.369 / 8.685 |
| Full_batch_delete | 2.941 | 4.762 | 🟩 PureDb 1.62× | 9.875 / 7.961 |

### 独立进程 RSS：谁更高，高多少

| 场景 | PureDb JIT MiB | PureDb AOT MiB | SQLite JIT MiB | SQLite AOT MiB | AOT 当前谁更高 |
| --- | --- | --- | --- | --- | --- |
| point | 98.78 | 60.67 | 71.56 | 42.55 | **PureDb 更高**：多 18.12 MiB，高 42.6% |
| range20 | 54.36 | 22.46 | 32.99 | 11.00 | **PureDb 更高**：多 11.46 MiB，高 104.2% |
| json_index | 151.92 | 113.94 | 33.05 | 11.27 | **PureDb 更高**：多 102.67 MiB，高 911.4% |

### 每次新进程启动与首次范围查询

| 引擎 | 部署 | 全进程 ms | 内部打开/首查/关闭 ms |
| --- | --- | --- | --- |
| PureDb | jit | 379.553 | 294.001 |
| PureDb | aot | 20.302 | 5.699 |
| SQLite | jit | 134.235 | 7.412 |
| SQLite | aot | 13.199 | 1.105 |

### 同一 10 万行数据的关闭后数据库文件

| PureDb MiB（含侧文件） | SQLite MiB（含侧文件） | 谁更小 |
| --- | --- | --- |
| 38.866 | 32.273 | 🟥 SQLite 1.20× |

AOT 改变应用部署方式，数据文件格式相同。这一行只统计最初查询库及其侧文件，不累计各写入场景生成的临时数据库。文件占用不是物理写入流量。

### 本平台结论

PureDb AOT 的运行文件比自身自包含 JIT 减少 92.8%；全进程启动中位数由 379.55 ms 变为 20.30 ms。AOT 对比 SQLite：11 项耗时领先、4 项落后、0 项接近。体积、热查询、写入、CPU 与 RSS 仍需分别判断，不能宣称全面领先。

[原始清单、各轮样本及环境](../benchmarks/results/aot/latest/linux)。

## 使用已发布包启用 AOT

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <PublishAot>true</PublishAot>
  <IlcOptimizationPreference>Speed</IlcOptimizationPreference>
</PropertyGroup>
```

```bash
dotnet add package PureDb --version 1.0.0-preview.3
dotnet publish -c Release -r win-x64
# Linux ARM64 应在对应 Linux 编译环境发布：
dotnet publish -c Release -r linux-arm64
```

Native AOT 是应用发布选项，NuGet 仍分发托管库，主库和可选兼容包均无原生 SQLite 依赖。应用自己的反射或 JSON 模型仍需满足 AOT 要求。官方说明见 [Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) 和 [JSON 源生成](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)。

复现：先运行 `scripts/publish-deployment.py` 发布两种引擎和三种部署方式，再用 `scripts/run-deployment.py` 串行测量；`scripts/make-aot-report.py` 从本轮公开原始结果生成本页。CI 及发布流水线在 Windows x64、Linux x64、Linux ARM64 真正编译和运行 AOT，发布前还以实际 NuGet 包运行 AOT 功能检查。
