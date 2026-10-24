# 本轮优化前后及 SQLite 对照

2026-10-24，`1.0.0-preview.2` 本轮发布构建。优化前基于 `21571bc`，已含 CHECK、UPSERT 和上一轮优化；本轮修复缓存计量并增加 Native AOT 支持及分页物化优化。两平台使用同一组前后 DLL，各场景三轮中位数；同一台机器串行测量，轮换引擎次序。保留本轮一组前后证据，旧全面对比已替换。

[完整 41 场景与 ADO.NET](PERFORMANCE.md) · [CPU / RSS 谁更高](RESOURCES.md) · [排序专项](SORT-PERFORMANCE.md) · [SQL 支持清单](SQL-SUPPORT.md)

## 改了什么，为什么可能更快

1. 表缓存计量：原先统一按每列 80 字节预留，含 BLOB/JSON 的可容纳表会被误判超预算，每次查询都解码整表。现在按数组、装箱数值、UTF-16 文本、BLOB 视图与源编码估算；默认预算未扩大。新增测试覆盖复用、写后失效与零缓存。

2. 分页物化：将最终结果复制移出查询绑定作用域，避免捕获投影列的闭包；已知候选数组直接按分页一次分配结果。BLOB 仍复制，表达式仍保持逐行求值。

3. Native AOT：JSON 与可选兼容库目录序列化统一使用源生成元数据；ADO.NET 保留 SQL 标量的必要元数据。真正编译运行 AOT、7 组功能及 230 条差分；AOT、JIT、SQLite 的体积、启动、CPU、RSS 和写入见 [独立报告](AOT-PERFORMANCE.md)。

4. 缓存阈值专项使用同一个自包含 JIT 宿主和运行时，只替换前后 PureDb.dll；三轮交替测量，专门验证重解码瓶颈，见 [缓存专项](CACHE-BUDGET-PERFORMANCE.md)。本页的旧 41 场景也已完整重测，收益与退化均列出。

本轮没有新增 SIMD 指令路径。现有 CRC 使用 ARM CRC32 硬件指令，指定整数 JSON 计数路径使用可用的向量指令；这些已存在于基线，不能冒充本轮 SIMD 收益。排序的比较、分支和共享锁成本不适合直接用 SIMD 替代。

## 测量边界

源码在冻结测量前完成核心格式检查。两平台使用同一份实测 DLL；GitHub 从提交重新构建包时，路径及构建元数据可能改变哈希。AOT/JIT 发布对比使用同一运行时的独立宿主，详见 [AOT 报告](AOT-PERFORMANCE.md)。

同机前后差值包含调度、JIT、GC、页缓存、温度和存储状态变化，三轮中位数不是因果或统计显著性证明；小于 5% 只标为接近。下列 CPU 是完成同样单位工作消耗的时间，RSS 是独立进程单库 GC 后驻留量，累计托管分配不是 RSS。SQLite 的原生堆不计入托管分配。Full / Buffered、关闭维护、缓存初建和接口差异详见全面报告。

## Windows x64

### 全部主场景：前后耗时及当前 SQLite

| 场景 | 优化前 PureDb µs | 优化后 PureDb µs | 前后倍率 | SQLite µs | 当前胜方 |
| --- | --- | --- | --- | --- | --- |
| shared_read | 32.935 | 31.869 | 1.03× | 15.548 | 🟥 SQLite 2.05× |
| shared_update | 205.403 | 206.377 | 1.00× | 108.771 | 🟥 SQLite 1.90× |
| kv_get | 0.290 | 0.303 | 0.96× | 5.560 | 🟩 PureDb 18.36× |
| kv_miss | 0.050 | 0.051 | 0.97× | 4.666 | 🟩 PureDb 91.29× |
| kv_range_20 | 2.541 | 2.555 | 0.99× | 16.818 | 🟩 PureDb 6.58× |
| kv_range_1000 | 103.825 | 107.185 | 0.97× | 516.391 | 🟩 PureDb 4.82× |
| sql_point | 0.387 | 0.399 | 0.97× | 5.227 | 🟩 PureDb 13.11× |
| sql_projection | 0.307 | 0.307 | 1.00× | 5.004 | 🟩 PureDb 16.31× |
| sql_range20 | 2.903 | 3.143 | 0.92× | 14.465 | 🟩 PureDb 4.60× |
| sql_range1000 | 115.182 | 126.425 | 0.91× | 498.480 | 🟩 PureDb 3.94× |
| sql_count | 0.173 | 0.177 | 0.97× | 17.734 | 🟩 PureDb 100.10× |
| sql_sum_scan | 1,165.713 | 1,158.071 | 1.01× | 3,028.795 | 🟩 PureDb 2.62× |
| sql_group | 18.351 | 18.214 | 1.01× | 4,405.454 | 🟩 PureDb 241.88× |
| sql_join | 1.241 | 1.246 | 1.00× | 10.198 | 🟩 PureDb 8.19× |
| sql_left_join | 1.040 | 1.058 | 0.98× | 7.085 | 🟩 PureDb 6.70× |
| json_index | 2.553 | 2.353 | 1.09× | 21.730 | 🟩 PureDb 9.24× |
| json_scan | 3.887 | 4.335 | 0.90× | 509.308 | 🟩 PureDb 117.49× |
| json_binary_conversion | 0.958 | 1.001 | 0.96× | 5.760 | 🟩 PureDb 5.76× |
| parallel_get_8 | 0.197 | 0.184 | 1.07× | 1.944 | 🟩 PureDb 10.55× |
| reopen_and_first_range | 677.960 | 698.760 | 0.97× | 642.960 | 🟥 SQLite 1.09× |
| cache_limited_2MiB | 5.387 | 5.543 | 0.97× | 10.629 | 🟩 PureDb 1.92× |
| Full_single | 24,428.963 | 18,341.519 | 1.33× | 15,602.437 | 🟥 SQLite 1.18× |
| Full_concurrent8 | 5,968.335 | 4,555.714 | 1.31× | 24,558.607 | 🟩 PureDb 5.39× |
| Full_batch1000 | 30.731 | 23.231 | 1.32× | 30.431 | 🟩 PureDb 1.31× |
| Full_compact | 66,412.900 | 51,528.700 | 1.29× | 61,588.200 | 🟩 PureDb 1.20× |
| Full_batch_unique | 44.497 | 41.909 | 1.06× | 17.582 | 🟥 SQLite 2.38× |
| Full_batch_update | 28.249 | 21.898 | 1.29× | 45.006 | 🟩 PureDb 2.06× |
| Full_batch_delete | 25.974 | 17.140 | 1.52× | 38.168 | 🟩 PureDb 2.23× |
| Full_update | 25,795.701 | 19,950.198 | 1.29× | 21,077.293 | 🟩 PureDb 1.06× |
| Full_delete | 26,060.507 | 16,723.946 | 1.56× | 15,977.901 | 🟨 接近 |
| Full_mixed | 20,037.999 | 16,892.167 | 1.19× | 17,827.075 | 🟩 PureDb 1.06× |
| Buffered_single | 5.037 | 4.949 | 1.02× | 267.278 | 🟩 PureDb 54.01× |
| Buffered_concurrent8 | 7.428 | 8.032 | 0.92× | 293.776 | 🟩 PureDb 36.58× |
| Buffered_batch1000 | 2.209 | 1.352 | 1.63× | 1.669 | 🟩 PureDb 1.23× |
| Buffered_compact | 67,888.800 | 64,954.100 | 1.05× | 20,108.700 | 🟥 SQLite 3.23× |
| Buffered_batch_unique | 1.516 | 1.518 | 1.00× | 1.641 | 🟩 PureDb 1.08× |
| Buffered_batch_update | 1.663 | 1.814 | 0.92× | 17.560 | 🟩 PureDb 9.68× |
| Buffered_batch_delete | 1.464 | 1.999 | 0.73× | 16.245 | 🟩 PureDb 8.13× |
| Buffered_update | 5.118 | 5.028 | 1.02× | 323.438 | 🟩 PureDb 64.32× |
| Buffered_delete | 5.259 | 4.783 | 1.10× | 370.326 | 🟩 PureDb 77.42× |
| Buffered_mixed | 11.952 | 12.764 | 0.94× | 373.716 | 🟩 PureDb 29.28× |

单位沿用全面报告：普通查询为 µs/查询，批量为 µs/行，压缩为 µs/次，重开为 µs/open。前后倍率大于 1 表示变快，小于 1 表示退化。

### 全部主场景：每单位 CPU 成本

| 场景 | 优化前 CPU µs | 优化后 CPU µs | SQLite CPU µs | 当前 CPU 胜方 |
| --- | --- | --- | --- | --- |
| shared_read | 82.031 | 78.125 | 11.719 | 短区间，不排名 |
| shared_update | 148.438 | 148.438 | 27.344 | 🟥 SQLite 5.43× |
| kv_get | 0.296 | 0.309 | 5.560 | 🟩 PureDb 17.99× |
| kv_miss | 0.050 | 0.051 | 4.666 | 🟩 PureDb 91.29× |
| kv_range_20 | 2.541 | 2.502 | 16.818 | 🟩 PureDb 6.72× |
| kv_range_1000 | 105.753 | 108.696 | 516.173 | 🟩 PureDb 4.75× |
| sql_point | 0.387 | 0.399 | 5.227 | 🟩 PureDb 13.11× |
| sql_projection | 0.307 | 0.305 | 5.108 | 🟩 PureDb 16.76× |
| sql_range20 | 2.963 | 3.143 | 14.465 | 🟩 PureDb 4.60× |
| sql_range1000 | 115.172 | 126.412 | 502.049 | 🟩 PureDb 3.97× |
| sql_count | 0.173 | 0.177 | 17.382 | 🟩 PureDb 98.21× |
| sql_sum_scan | 1,164.596 | 1,157.407 | 3,012.048 | 🟩 PureDb 2.60× |
| sql_group | 18.454 | 18.214 | 4,503.676 | 🟩 PureDb 247.27× |
| sql_join | 1.241 | 1.246 | 10.096 | 🟩 PureDb 8.10× |
| sql_left_join | 1.040 | 1.058 | 7.085 | 🟩 PureDb 6.70× |
| json_index | 2.553 | 2.353 | 21.730 | 🟩 PureDb 9.24× |
| json_scan | 3.867 | 4.335 | 509.165 | 🟩 PureDb 117.46× |
| json_binary_conversion | 0.978 | 1.008 | 5.880 | 🟩 PureDb 5.83× |
| parallel_get_8 | 1.406 | 1.367 | 13.828 | 🟩 PureDb 10.11× |
| reopen_and_first_range | 0.000 | 0.000 | 0.000 | 短区间，不排名 |
| cache_limited_2MiB | 5.387 | 5.543 | 10.629 | 🟩 PureDb 1.92× |
| Full_single | 3,281.250 | 3,437.500 | 468.750 | 短区间，不排名 |
| Full_concurrent8 | 468.750 | 468.750 | 312.500 | 短区间，不排名 |
| Full_batch1000 | 9.375 | 6.250 | 4.688 | 短区间，不排名 |
| Full_compact | 0.000 | 15,625.000 | 0.000 | 短区间，不排名 |
| Full_batch_unique | 0.000 | 0.000 | 31.250 | 短区间，不排名 |
| Full_batch_update | 10.938 | 10.938 | 3.125 | 短区间，不排名 |
| Full_batch_delete | 7.812 | 4.688 | 1.562 | 短区间，不排名 |
| Full_update | 781.250 | 2,187.500 | 156.250 | 短区间，不排名 |
| Full_delete | 625.000 | 937.500 | 312.500 | 短区间，不排名 |
| Full_mixed | 937.500 | 937.500 | 156.250 | 短区间，不排名 |
| Buffered_single | 0.000 | 0.000 | 62.500 | 短区间，不排名 |
| Buffered_concurrent8 | 0.000 | 15.625 | 62.500 | 短区间，不排名 |
| Buffered_batch1000 | 3.125 | 1.562 | 1.562 | 短区间，不排名 |
| Buffered_compact | 78,125.000 | 46,875.000 | 0.000 | 短区间，不排名 |
| Buffered_batch_unique | 0.000 | 0.000 | 0.000 | 短区间，不排名 |
| Buffered_batch_update | 1.562 | 1.562 | 7.812 | 短区间，不排名 |
| Buffered_batch_delete | 1.562 | 1.562 | 0.000 | 短区间，不排名 |
| Buffered_update | 0.000 | 0.000 | 15.625 | 短区间，不排名 |
| Buffered_delete | 15.625 | 0.000 | 46.875 | 短区间，不排名 |
| Buffered_mixed | 15.625 | 15.625 | 46.875 | 短区间，不排名 |

### 内存：RSS 与托管存活量

| 场景 | 优化前 RSS MiB | 优化后 RSS MiB | SQLite RSS MiB | 当前谁更高 | PureDb 托管存活 MiB |
| --- | --- | --- | --- | --- | --- |
| point | 86.36 | 86.47 | 78.86 | **PureDb 更高**：多 7.62 MiB，高 9.7% | 34.14 → 34.15 |
| range | 56.68 | 56.53 | 56.52 | **PureDb 更高**：多 0.02 MiB，高 0.0% | 7.19 → 7.19 |
| json | 61.38 | 61.30 | 45.36 | **PureDb 更高**：多 15.95 MiB，高 35.2% | 5.99 → 5.99 |

沿用上一轮 Shared 协议，本轮工作进程逻辑读取字节三轮中位数：9,000,876 → 9,000,876，减少 0.0%。这是操作系统进程逻辑 I/O，不是物理盘读取，也不等于相同比例的耗时降低。

### 当前仍落后的项目（按差距排列）

| 场景 | PureDb µs | SQLite µs | SQLite 倍率 |
| --- | --- | --- | --- |
| Buffered_compact | 64,954.100 | 20,108.700 | 3.23× |
| Full_batch_unique | 41.909 | 17.582 | 2.38× |
| shared_read | 31.869 | 15.548 | 2.05× |
| shared_update | 206.377 | 108.771 | 1.90× |
| Full_single | 18,341.519 | 15,602.437 | 1.18× |
| reopen_and_first_range | 698.760 | 642.960 | 1.09× |

优化前 DLL：`F57A192E23B4467BAEAF12D40B4A5849C3E09E80A8D408120B55E8F517DCC41B`；优化后 DLL：`A240817D6F98A3264CB24AD474A870BC1AACA56A9466411CAB12387EB7EB04F5`。本轮 [前置样本](../benchmarks/results/optimization/latest/windows/before) 与 [最终样本](../benchmarks/results/latest/windows) 均保留实际构建哈希。

## Linux ARM64

### 全部主场景：前后耗时及当前 SQLite

| 场景 | 优化前 PureDb µs | 优化后 PureDb µs | 前后倍率 | SQLite µs | 当前胜方 |
| --- | --- | --- | --- | --- | --- |
| shared_read | 68.317 | 59.753 | 1.14× | 33.189 | 🟥 SQLite 1.80× |
| shared_update | 297.658 | 332.048 | 0.90× | 51.106 | 🟥 SQLite 6.50× |
| kv_get | 1.085 | 1.100 | 0.99× | 4.370 | 🟩 PureDb 3.97× |
| kv_miss | 0.122 | 0.116 | 1.05× | 2.348 | 🟩 PureDb 20.19× |
| kv_range_20 | 14.800 | 15.040 | 0.98× | 28.812 | 🟩 PureDb 1.92× |
| kv_range_1000 | 287.536 | 247.915 | 1.16× | 986.151 | 🟩 PureDb 3.98× |
| sql_point | 1.127 | 1.095 | 1.03× | 3.561 | 🟩 PureDb 3.25× |
| sql_projection | 0.759 | 0.743 | 1.02× | 3.030 | 🟩 PureDb 4.08× |
| sql_range20 | 6.564 | 6.608 | 0.99× | 19.494 | 🟩 PureDb 2.95× |
| sql_range1000 | 342.081 | 343.648 | 1.00× | 950.521 | 🟩 PureDb 2.77× |
| sql_count | 0.459 | 0.456 | 1.01× | 19.935 | 🟩 PureDb 43.70× |
| sql_sum_scan | 2,739.897 | 2,710.722 | 1.01× | 5,069.989 | 🟩 PureDb 1.87× |
| sql_group | 69.710 | 57.109 | 1.22× | 7,139.051 | 🟩 PureDb 125.01× |
| sql_join | 6.064 | 6.313 | 0.96× | 12.466 | 🟩 PureDb 1.97× |
| sql_left_join | 5.299 | 5.667 | 0.94× | 6.935 | 🟩 PureDb 1.22× |
| json_index | 5.711 | 5.840 | 0.98× | 30.493 | 🟩 PureDb 5.22× |
| json_scan | 12.967 | 12.927 | 1.00× | 672.055 | 🟩 PureDb 51.99× |
| json_binary_conversion | 2.961 | 3.002 | 0.99× | 4.081 | 🟩 PureDb 1.36× |
| parallel_get_8 | 0.846 | 0.631 | 1.34× | 1.439 | 🟩 PureDb 2.28× |
| reopen_and_first_range | 1,244.560 | 655.900 | 1.90× | 422.380 | 🟥 SQLite 1.55× |
| cache_limited_2MiB | 4.124 | 4.067 | 1.01× | 6.948 | 🟩 PureDb 1.71× |
| Full_single | 2,113.026 | 2,628.761 | 0.80× | 1,091.539 | 🟥 SQLite 2.41× |
| Full_concurrent8 | 322.613 | 407.376 | 0.79× | 2,262.825 | 🟩 PureDb 5.55× |
| Full_batch1000 | 41.431 | 32.761 | 1.26× | 20.767 | 🟥 SQLite 1.58× |
| Full_compact | 63,191.400 | 77,412.300 | 0.82× | 32,217.900 | 🟥 SQLite 2.40× |
| Full_batch_unique | 39.961 | 42.621 | 0.94× | 13.772 | 🟥 SQLite 3.09× |
| Full_batch_update | 32.696 | 37.544 | 0.87× | 11.724 | 🟥 SQLite 3.20× |
| Full_batch_delete | 11.885 | 18.984 | 0.63× | 10.698 | 🟥 SQLite 1.77× |
| Full_update | 1,113.355 | 1,072.193 | 1.04× | 983.539 | 🟥 SQLite 1.09× |
| Full_delete | 1,368.571 | 1,829.338 | 0.75× | 1,094.972 | 🟥 SQLite 1.67× |
| Full_mixed | 2,055.724 | 1,917.556 | 1.07× | 974.903 | 🟥 SQLite 1.97× |
| Buffered_single | 49.573 | 60.266 | 0.82× | 74.201 | 🟩 PureDb 1.23× |
| Buffered_concurrent8 | 68.539 | 73.339 | 0.93× | 127.674 | 🟩 PureDb 1.74× |
| Buffered_batch1000 | 9.179 | 14.027 | 0.65× | 7.498 | 🟥 SQLite 1.87× |
| Buffered_compact | 48,037.800 | 67,674.000 | 0.71× | 23,799.100 | 🟥 SQLite 2.84× |
| Buffered_batch_unique | 12.058 | 10.796 | 1.12× | 7.718 | 🟥 SQLite 1.40× |
| Buffered_batch_update | 9.507 | 12.880 | 0.74× | 9.619 | 🟥 SQLite 1.34× |
| Buffered_batch_delete | 10.673 | 5.711 | 1.87× | 8.796 | 🟩 PureDb 1.54× |
| Buffered_update | 26.038 | 38.093 | 0.68× | 104.427 | 🟩 PureDb 2.74× |
| Buffered_delete | 20.384 | 15.938 | 1.28× | 123.488 | 🟩 PureDb 7.75× |
| Buffered_mixed | 74.677 | 53.577 | 1.39× | 123.767 | 🟩 PureDb 2.31× |

单位沿用全面报告：普通查询为 µs/查询，批量为 µs/行，压缩为 µs/次，重开为 µs/open。前后倍率大于 1 表示变快，小于 1 表示退化。

### 全部主场景：每单位 CPU 成本

| 场景 | 优化前 CPU µs | 优化后 CPU µs | SQLite CPU µs | 当前 CPU 胜方 |
| --- | --- | --- | --- | --- |
| shared_read | 168.823 | 147.686 | 17.831 | 🟥 SQLite 8.28× |
| shared_update | 366.557 | 404.043 | 20.146 | 🟥 SQLite 20.06× |
| kv_get | 1.151 | 1.152 | 4.561 | 🟩 PureDb 3.96× |
| kv_miss | 0.124 | 0.119 | 2.380 | 🟩 PureDb 20.06× |
| kv_range_20 | 18.203 | 18.325 | 29.205 | 🟩 PureDb 1.59× |
| kv_range_1000 | 310.568 | 260.814 | 990.145 | 🟩 PureDb 3.80× |
| sql_point | 1.413 | 1.370 | 3.568 | 🟩 PureDb 2.61× |
| sql_projection | 0.761 | 0.745 | 3.096 | 🟩 PureDb 4.15× |
| sql_range20 | 7.076 | 7.171 | 19.550 | 🟩 PureDb 2.73× |
| sql_range1000 | 344.465 | 346.003 | 976.991 | 🟩 PureDb 2.82× |
| sql_count | 0.459 | 0.456 | 20.028 | 🟩 PureDb 43.89× |
| sql_sum_scan | 3,525.963 | 3,376.621 | 5,070.750 | 🟩 PureDb 1.50× |
| sql_group | 71.327 | 61.465 | 7,398.509 | 🟩 PureDb 120.37× |
| sql_join | 7.167 | 7.172 | 12.468 | 🟩 PureDb 1.74× |
| sql_left_join | 5.300 | 5.786 | 6.937 | 🟩 PureDb 1.20× |
| json_index | 6.017 | 6.076 | 30.513 | 🟩 PureDb 5.02× |
| json_scan | 12.969 | 12.927 | 680.635 | 🟩 PureDb 52.65× |
| json_binary_conversion | 3.295 | 3.316 | 4.274 | 🟩 PureDb 1.29× |
| parallel_get_8 | 2.338 | 1.486 | 4.804 | 🟩 PureDb 3.23× |
| reopen_and_first_range | 1,291.400 | 675.600 | 438.800 | 短区间，不排名 |
| cache_limited_2MiB | 4.125 | 4.071 | 6.949 | 🟩 PureDb 1.71× |
| Full_single | 1,848.090 | 2,536.420 | 362.770 | 🟥 SQLite 6.99× |
| Full_concurrent8 | 373.330 | 444.370 | 661.240 | 🟩 PureDb 1.49× |
| Full_batch1000 | 49.984 | 37.667 | 18.438 | 🟥 SQLite 2.04× |
| Full_compact | 57,975.000 | 69,532.000 | 22,690.000 | 🟥 SQLite 3.06× |
| Full_batch_unique | 37.624 | 37.717 | 11.330 | 🟥 SQLite 3.33× |
| Full_batch_update | 34.630 | 43.230 | 9.648 | 🟥 SQLite 4.48× |
| Full_batch_delete | 20.933 | 24.313 | 6.445 | 🟥 SQLite 3.77× |
| Full_update | 579.660 | 649.730 | 1,097.970 | 🟩 PureDb 1.69× |
| Full_delete | 775.730 | 828.820 | 284.520 | 🟥 SQLite 2.91× |
| Full_mixed | 1,777.680 | 1,422.230 | 183.620 | 🟥 SQLite 7.75× |
| Buffered_single | 50.029 | 60.634 | 49.831 | 🟥 SQLite 1.22× |
| Buffered_concurrent8 | 161.212 | 139.381 | 82.135 | 🟥 SQLite 1.70× |
| Buffered_batch1000 | 10.417 | 27.322 | 7.547 | 🟥 SQLite 3.62× |
| Buffered_compact | 86,021.000 | 57,033.000 | 22,359.000 | 🟥 SQLite 2.55× |
| Buffered_batch_unique | 13.634 | 11.098 | 11.512 | 🟨 接近 |
| Buffered_batch_update | 12.781 | 21.457 | 16.404 | 🟥 SQLite 1.31× |
| Buffered_batch_delete | 20.345 | 8.549 | 11.069 | 🟩 PureDb 1.29× |
| Buffered_update | 26.421 | 59.676 | 134.048 | 🟩 PureDb 2.25× |
| Buffered_delete | 38.773 | 30.502 | 132.946 | 🟩 PureDb 4.36× |
| Buffered_mixed | 146.788 | 105.105 | 132.076 | 🟩 PureDb 1.26× |

### 内存：RSS 与托管存活量

| 场景 | 优化前 RSS MiB | 优化后 RSS MiB | SQLite RSS MiB | 当前谁更高 | PureDb 托管存活 MiB |
| --- | --- | --- | --- | --- | --- |
| point | 100.54 | 101.19 | 77.94 | **PureDb 更高**：多 23.25 MiB，高 29.8% | 33.87 → 33.87 |
| range | 69.75 | 70.64 | 58.20 | **PureDb 更高**：多 12.44 MiB，高 21.4% | 6.93 → 6.93 |
| json | 74.88 | 75.63 | 50.05 | **PureDb 更高**：多 25.59 MiB，高 51.1% | 5.75 → 5.75 |

该 Linux 内核不提供有效的进程逻辑 I/O 字节，不能从 -1 推断为零；不发布该指标的前后降低比例。

### 当前仍落后的项目（按差距排列）

| 场景 | PureDb µs | SQLite µs | SQLite 倍率 |
| --- | --- | --- | --- |
| shared_update | 332.048 | 51.106 | 6.50× |
| Full_batch_update | 37.544 | 11.724 | 3.20× |
| Full_batch_unique | 42.621 | 13.772 | 3.09× |
| Buffered_compact | 67,674.000 | 23,799.100 | 2.84× |
| Full_single | 2,628.761 | 1,091.539 | 2.41× |
| Full_compact | 77,412.300 | 32,217.900 | 2.40× |
| Full_mixed | 1,917.556 | 974.903 | 1.97× |
| Buffered_batch1000 | 14.027 | 7.498 | 1.87× |
| shared_read | 59.753 | 33.189 | 1.80× |
| Full_batch_delete | 18.984 | 10.698 | 1.77× |
| Full_delete | 1,829.338 | 1,094.972 | 1.67× |
| Full_batch1000 | 32.761 | 20.767 | 1.58× |
| reopen_and_first_range | 655.900 | 422.380 | 1.55× |
| Buffered_batch_unique | 10.796 | 7.718 | 1.40× |
| Buffered_batch_update | 12.880 | 9.619 | 1.34× |
| Full_update | 1,072.193 | 983.539 | 1.09× |

优化前 DLL：`F57A192E23B4467BAEAF12D40B4A5849C3E09E80A8D408120B55E8F517DCC41B`；优化后 DLL：`A240817D6F98A3264CB24AD474A870BC1AACA56A9466411CAB12387EB7EB04F5`。本轮 [前置样本](../benchmarks/results/optimization/latest/linux/before) 与 [最终样本](../benchmarks/results/latest/linux) 均保留实际构建哈希。

## 结论与后续重点

性能、CPU、RSS 没有全面超过 SQLite。应分别看排序分配、Shared 逻辑读取减少及实际耗时；没有收益和出现退化的场景也完整保留。托管运行时、行对象缓存及辅助索引占用仍是 RSS 成本，减少 JSON 树存活不能保证进程驻留量按相同幅度下降。

仍需处理 Shared 锁竞争与 SQL 状态刷新、批量唯一键和完整持久化路径、压缩复制成本、重开索引目录成本及行缓存对象开销。复合索引仍是按版本惰性内存构建，尚未实现分页增量维护；相关子查询、视图、CTE 与窗口仍有文档列明的物化和优化边界。

恢复验证包括每个字节位置截断、完整坏帧拒绝、大帧和缓冲区边界、已提交前缀缩短、四进程原子更新、压缩代次变化及强杀；Windows 和 ARM64 执行相同 DLL，Linux x64 由 CI 验证。详见 [批准的 Shared 方案](SHARED-REPLAY-PLAN.md) 及 [开发验证](DEVELOPMENT-VALIDATION.md)。
