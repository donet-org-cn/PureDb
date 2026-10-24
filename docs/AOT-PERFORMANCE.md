# Native AOT：文件大小、启动、CPU、内存和 SQLite 对比

版本：`1.0.0-preview.2`。Windows x64 与指定 Linux ARM64 主机，.NET SDK 10.0.402 / 运行时 10.0.13。AOT 和自包含 JIT 使用相同源码、相同数据、相同 SQL 和相同适配层；每台机器串行、三轮交替顺序，不能跨平台比较绝对耗时。

## 实际支持与验证

核心与可选 SQLite 兼容库启用 IsAotCompatible；内置 JSON 及兼容目录序列化改用源生成元数据，ADO.NET GetFieldType 保留所需的标量类型元数据。每个平台真正发布并运行 AOT，报告验证 DynamicCodeSupported=false；编译及裁剪警告为 0。额外运行 7 组 AOT 功能测试和 230 条 SQLite 差分查询，覆盖 JSON 修改、模式重开、CHECK/UPSERT、CTE/窗口/子查询、外键/触发器/保存点、ADO.NET 模式表和纯托管 SQLite 文件交换。

GetSchemaTable 有一处精确限定的 IL2111 抑制：DataColumn 保存 Type 对象，不会访问 Type.TypeInitializer；该路径通过实际 AOT 运行验证。没有关闭全局裁剪分析或全局屏蔽 AOT 警告。这里的零警告指本轮完整发布日志未产生 IL 警告，不表示应用可以任意使用反射。

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

表缓存此前按“源编码 + 每列 80 字节 + 固定开销”估算；BLOB/JSON 组合表会被过度计费并反复整表解码。本轮按行数组、数值装箱、UTF-16 文本、BLOB 视图和源后备数据计费，没有扩大默认预算。新增测试验证可容纳表复用缓存、写后失效及预算为 0 时不保留缓存。预算仍是行缓存估算，不含全部辅助索引，也不是进程 RSS 硬上限。

分页结果物化独立为小方法，避免查询绑定作用域捕获投影列；已知候选数组直接按最终分页一次分配结果。所有 BLOB 输出继续复制，计算表达式的逐行求值语义保持。

## Windows x64

### 实际发布文件体积

| 引擎 | 部署方式 | 主程序 MiB | 运行文件合计 MiB | 含符号及文档 MiB | 说明 |
| --- | --- | --- | --- | --- | --- |
| PureDb | framework | 0.155 | 0.702 | 1.272 | 不含共享 .NET 运行时 |
| PureDb | jit | 0.155 | 77.380 | 77.949 | 含 .NET 运行时 |
| PureDb | aot | 6.128 | 6.128 | 34.462 | 含可达运行时与引擎；SQLite 行还含原生库 |
| SQLite | framework | 0.155 | 3.559 | 3.589 | 不含共享 .NET 运行时 |
| SQLite | jit | 0.155 | 80.236 | 80.266 | 含 .NET 运行时 |
| SQLite | aot | 2.368 | 5.725 | 17.525 | 含可达运行时与引擎；SQLite 行还含原生库 |

### 查询与增删改：同引擎 JIT/AOT，以及双方 AOT

| 场景 | PureDb JIT µs | PureDb AOT µs | SQLite JIT µs | SQLite AOT µs | AOT 胜方 |
| --- | --- | --- | --- | --- | --- |
| point | 0.534 | 0.636 | 5.315 | 5.566 | 🟩 PureDb 8.75× |
| range20 | 3.321 | 5.047 | 16.251 | 16.876 | 🟩 PureDb 3.34× |
| json_index | 4.535 | 4.086 | 21.087 | 22.640 | 🟩 PureDb 5.54× |
| json_scan | 32.622 | 53.713 | 5,001.599 | 5,012.385 | 🟩 PureDb 93.32× |
| sort20 | 1,727.060 | 2,046.116 | 8,479.376 | 8,641.147 | 🟩 PureDb 4.22× |
| sort4096 | 7,534.022 | 7,433.869 | 17,991.586 | 17,860.349 | 🟩 PureDb 2.40× |
| sum_scan | 1,384.076 | 1,033.664 | 2,960.517 | 2,963.018 | 🟩 PureDb 2.87× |
| Buffered_single | 57.062 | 14.809 | 383.336 | 393.068 | 🟩 PureDb 26.54× |
| Buffered_batch_insert | 11.931 | 3.865 | 2.861 | 2.804 | 🟥 SQLite 1.38× |
| Buffered_batch_update | 15.247 | 3.758 | 2.656 | 2.864 | 🟥 SQLite 1.31× |
| Buffered_batch_delete | 3.279 | 1.288 | 3.565 | 3.684 | 🟩 PureDb 2.86× |
| Full_single | 24,931.326 | 24,303.328 | 23,159.306 | 22,775.766 | 🟥 SQLite 1.07× |
| Full_batch_insert | 31.248 | 31.422 | 28.333 | 30.096 | 🟨 接近 |
| Full_batch_update | 28.809 | 30.745 | 28.227 | 29.809 | 🟨 接近 |
| Full_batch_delete | 24.521 | 20.366 | 28.815 | 38.246 | 🟩 PureDb 1.88× |

普通查询单位为 µs/查询，增删改为 µs/行。AOT 与 JIT 的正反收益都保留，不把更快启动解读为所有热查询更快。

### AOT 的 CPU：每单位成本与满吞吐占用

| 场景 | PureDb CPU µs | SQLite CPU µs | CPU 成本胜方 | PureDb / SQLite CPU % |
| --- | --- | --- | --- | --- |
| point | 0.636 | 5.566 | 🟩 PureDb 8.75× | 8.333 / 8.333 |
| range20 | 5.047 | 16.876 | 🟩 PureDb 3.34× | 8.333 / 8.333 |
| json_index | 4.086 | 22.639 | 🟩 PureDb 5.54× | 8.333 / 8.333 |
| json_scan | 53.710 | 4,895.833 | 🟩 PureDb 91.15× | 8.333 / 8.140 |
| sort20 | 2,043.597 | 8,700.284 | 🟩 PureDb 4.26× | 8.323 / 8.320 |
| sort4096 | 7,425.743 | 17,805.233 | 🟩 PureDb 2.40× | 8.326 / 8.249 |
| sum_scan | 1,033.058 | 2,952.756 | 🟩 PureDb 2.86× | 8.332 / 8.304 |
| Buffered_single | 15.625 | 31.250 | 短区间，不排名 | 8.137 / 0.663 |
| Buffered_batch_insert | 3.125 | 3.125 | 短区间，不排名 | 6.948 / 9.286 |
| Buffered_batch_update | 3.125 | 3.125 | 短区间，不排名 | 6.929 / 9.091 |
| Buffered_batch_delete | 0.000 | 3.125 | 短区间，不排名 | 0.000 / 7.068 |
| Full_single | 312.500 | 312.500 | 短区间，不排名 | 0.133 / 0.114 |
| Full_batch_insert | 6.250 | 0.000 | 短区间，不排名 | 1.646 / 0.000 |
| Full_batch_update | 3.125 | 3.125 | 短区间，不排名 | 0.904 / 0.944 |
| Full_batch_delete | 0.000 | 3.125 | 短区间，不排名 | 0.000 / 0.681 |

### 独立进程 RSS：谁更高，高多少

| 场景 | PureDb JIT MiB | PureDb AOT MiB | SQLite JIT MiB | SQLite AOT MiB | AOT 当前谁更高 |
| --- | --- | --- | --- | --- | --- |
| point | 81.54 | 55.99 | 72.98 | 53.18 | **PureDb 更高**：多 2.82 MiB，高 5.3% |
| range20 | 42.44 | 22.58 | 34.25 | 19.57 | **PureDb 更高**：多 3.02 MiB，高 15.4% |
| json_index | 130.80 | 116.48 | 31.66 | 16.61 | **PureDb 更高**：多 99.86 MiB，高 601.1% |

### 每次新进程启动与首次范围查询

| 引擎 | 部署 | 全进程 ms | 内部打开/首查/关闭 ms |
| --- | --- | --- | --- |
| PureDb | jit | 216.722 | 119.754 |
| PureDb | aot | 33.744 | 3.998 |
| SQLite | jit | 119.450 | 5.153 |
| SQLite | aot | 32.631 | 2.033 |

### 同一 10 万行数据的关闭后数据库文件

| PureDb MiB（含侧文件） | SQLite MiB（含侧文件） | 谁更小 |
| --- | --- | --- |
| 38.866 | 32.273 | 🟥 SQLite 1.20× |

AOT 改变应用部署方式，数据文件格式相同。这一行只统计最初查询库及其侧文件，不累计各写入场景生成的临时数据库。文件占用不是物理写入流量。

### 本平台结论

PureDb AOT 的运行文件比自身自包含 JIT 减少 92.1%；全进程启动中位数由 216.72 ms 变为 33.74 ms。AOT 对比 SQLite：10 项耗时领先、3 项落后、2 项接近。体积、热查询、写入、CPU 与 RSS 仍需分别判断，不能宣称全面领先。

[原始清单、各轮样本及环境](../benchmarks/results/aot/latest/windows)。

## Linux ARM64

### 实际发布文件体积

| 引擎 | 部署方式 | 主程序 MiB | 运行文件合计 MiB | 含符号及文档 MiB | 说明 |
| --- | --- | --- | --- | --- | --- |
| PureDb | framework | 0.074 | 0.622 | 1.185 | 不含共享 .NET 运行时 |
| PureDb | jit | 0.074 | 85.161 | 85.724 | 含 .NET 运行时 |
| PureDb | aot | 6.060 | 6.060 | 26.095 | 含可达运行时与引擎；SQLite 行还含原生库 |
| SQLite | framework | 0.074 | 1.222 | 1.252 | 不含共享 .NET 运行时 |
| SQLite | jit | 0.074 | 85.761 | 85.791 | 含 .NET 运行时 |
| SQLite | aot | 2.691 | 3.792 | 12.542 | 含可达运行时与引擎；SQLite 行还含原生库 |

### 查询与增删改：同引擎 JIT/AOT，以及双方 AOT

| 场景 | PureDb JIT µs | PureDb AOT µs | SQLite JIT µs | SQLite AOT µs | AOT 胜方 |
| --- | --- | --- | --- | --- | --- |
| point | 8.563 | 1.365 | 3.599 | 3.548 | 🟩 PureDb 2.60× |
| range20 | 9.001 | 10.532 | 22.268 | 23.364 | 🟩 PureDb 2.22× |
| json_index | 17.375 | 13.524 | 30.841 | 31.717 | 🟩 PureDb 2.35× |
| json_scan | 103.259 | 97.424 | 6,769.817 | 6,733.502 | 🟩 PureDb 69.12× |
| sort20 | 3,880.068 | 9,171.413 | 16,302.336 | 16,433.739 | 🟩 PureDb 1.79× |
| sort4096 | 20,706.641 | 22,820.594 | 31,754.558 | 32,407.558 | 🟩 PureDb 1.42× |
| sum_scan | 2,921.625 | 1,956.807 | 5,048.772 | 5,023.961 | 🟩 PureDb 2.57× |
| Buffered_single | 93.232 | 53.736 | 157.776 | 128.023 | 🟩 PureDb 2.38× |
| Buffered_batch_insert | 28.811 | 15.526 | 9.075 | 12.995 | 🟥 SQLite 1.19× |
| Buffered_batch_update | 41.855 | 10.804 | 4.584 | 4.779 | 🟥 SQLite 2.26× |
| Buffered_batch_delete | 9.020 | 6.441 | 3.992 | 5.485 | 🟥 SQLite 1.17× |
| Full_single | 1,471.522 | 1,152.684 | 1,097.448 | 1,295.912 | 🟩 PureDb 1.12× |
| Full_batch_insert | 87.665 | 34.621 | 15.985 | 19.552 | 🟥 SQLite 1.77× |
| Full_batch_update | 41.223 | 18.803 | 7.008 | 9.597 | 🟥 SQLite 1.96× |
| Full_batch_delete | 20.292 | 4.730 | 9.540 | 11.423 | 🟩 PureDb 2.42× |

普通查询单位为 µs/查询，增删改为 µs/行。AOT 与 JIT 的正反收益都保留，不把更快启动解读为所有热查询更快。

### AOT 的 CPU：每单位成本与满吞吐占用

| 场景 | PureDb CPU µs | SQLite CPU µs | CPU 成本胜方 | PureDb / SQLite CPU % |
| --- | --- | --- | --- | --- |
| point | 1.365 | 3.547 | 🟩 PureDb 2.60× | 12.498 / 12.499 |
| range20 | 10.532 | 23.362 | 🟩 PureDb 2.22× | 12.500 / 12.499 |
| json_index | 13.524 | 31.717 | 🟩 PureDb 2.35× | 12.500 / 12.499 |
| json_scan | 97.423 | 6,733.384 | 🟩 PureDb 69.12× | 12.500 / 12.500 |
| sort20 | 9,169.890 | 16,432.565 | 🟩 PureDb 1.79× | 12.500 / 12.499 |
| sort4096 | 22,820.333 | 32,403.042 | 🟩 PureDb 1.42× | 12.500 / 12.499 |
| sum_scan | 1,954.456 | 5,023.640 | 🟩 PureDb 2.57× | 12.500 / 12.500 |
| Buffered_single | 54.211 | 56.813 | 🟨 接近 | 12.555 / 6.194 |
| Buffered_batch_insert | 15.549 | 12.998 | 🟥 SQLite 1.20× | 12.519 / 12.503 |
| Buffered_batch_update | 10.835 | 4.776 | 🟥 SQLite 2.27× | 12.536 / 12.508 |
| Buffered_batch_delete | 6.446 | 5.487 | 🟥 SQLite 1.17× | 12.509 / 12.503 |
| Full_single | 721.120 | 309.120 | 🟥 SQLite 2.33× | 6.500 / 2.791 |
| Full_batch_insert | 30.051 | 16.685 | 🟥 SQLite 1.80× | 11.062 / 10.667 |
| Full_batch_update | 16.541 | 6.533 | 🟥 SQLite 2.53× | 10.996 / 8.509 |
| Full_batch_delete | 3.552 | 8.057 | 🟩 PureDb 2.27× | 9.405 / 8.838 |

### 独立进程 RSS：谁更高，高多少

| 场景 | PureDb JIT MiB | PureDb AOT MiB | SQLite JIT MiB | SQLite AOT MiB | AOT 当前谁更高 |
| --- | --- | --- | --- | --- | --- |
| point | 99.74 | 59.18 | 71.23 | 42.25 | **PureDb 更高**：多 16.93 MiB，高 40.1% |
| range20 | 53.82 | 22.26 | 32.16 | 10.84 | **PureDb 更高**：多 11.43 MiB，高 105.4% |
| json_index | 151.77 | 118.53 | 32.54 | 11.12 | **PureDb 更高**：多 107.41 MiB，高 965.8% |

### 每次新进程启动与首次范围查询

| 引擎 | 部署 | 全进程 ms | 内部打开/首查/关闭 ms |
| --- | --- | --- | --- |
| PureDb | jit | 429.114 | 267.655 |
| PureDb | aot | 43.429 | 9.942 |
| SQLite | jit | 211.646 | 9.558 |
| SQLite | aot | 32.843 | 2.064 |

### 同一 10 万行数据的关闭后数据库文件

| PureDb MiB（含侧文件） | SQLite MiB（含侧文件） | 谁更小 |
| --- | --- | --- |
| 38.866 | 32.273 | 🟥 SQLite 1.20× |

AOT 改变应用部署方式，数据文件格式相同。这一行只统计最初查询库及其侧文件，不累计各写入场景生成的临时数据库。文件占用不是物理写入流量。

### 本平台结论

PureDb AOT 的运行文件比自身自包含 JIT 减少 92.9%；全进程启动中位数由 429.11 ms 变为 43.43 ms。AOT 对比 SQLite：10 项耗时领先、5 项落后、0 项接近。体积、热查询、写入、CPU 与 RSS 仍需分别判断，不能宣称全面领先。

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
dotnet add package PureDb --version 1.0.0-preview.2
dotnet publish -c Release -r win-x64
# Linux ARM64 应在对应 Linux 编译环境发布：
dotnet publish -c Release -r linux-arm64
```

Native AOT 是应用发布选项，NuGet 仍分发托管库，主库和可选兼容包均无原生 SQLite 依赖。应用自己的反射或 JSON 模型仍需满足 AOT 要求。官方说明见 [Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) 和 [JSON 源生成](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)。

复现：先运行 `scripts/publish-deployment.py` 发布两种引擎和三种部署方式，再用 `scripts/run-deployment.py` 串行测量；`scripts/make-aot-report.py` 从本轮公开原始结果生成本页。CI 及发布流水线在 Windows x64、Linux x64、Linux ARM64 真正编译和运行 AOT，发布前还以实际 NuGet 包运行 AOT 功能检查。
