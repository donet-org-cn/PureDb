# 本轮优化前后及 SQLite 对照

2026-10-24，主分支 `1.0.0-preview.2` 开发构建，尚未发布至 NuGet。优化前基于 `804f6ef`，已含上一轮 SQL 扩展；优化后增加 CHECK、单分支 UPSERT，以及本页所述性能优化。两平台使用同一组前后 DLL，各场景三轮中位数；同一台机器串行测量，轮换引擎次序。保留本轮一组前后证据，旧全面对比已替换。

[完整 41 场景与 ADO.NET](PERFORMANCE.md) · [CPU / RSS 谁更高](RESOURCES.md) · [排序专项](SORT-PERFORMANCE.md) · [SQL 支持清单](SQL-SUPPORT.md)

## 改了什么，为什么可能更快

1. Shared 日志追赶：已确认同一代文件并持有进程锁时，从本地提交偏移读取真实新增尾部。使用独立随机读句柄及池化缓冲区，避免为几十字节更新重新预读 64 KiB 文件头。CRC、帧及操作边界逐项验证；异常协调戳、文件换代或锁遗弃仍走完整恢复。没有减少刷盘或合并原本独立的事务。

2. 非索引小分页排序：基线已有有界堆，本轮进一步以紧凑二叉堆取代纯列 PriorityQueue，候选只存一份行引用和序号。首批自底向上建堆，整数 / NULL 内联比较；仅对经过 OFFSET / LIMIT 的最终结果复制投影与 BLOB。计算表达式继续逐行求值，保留业务函数和异常语义。

3. JSON 内存：完整解析树使用弱引用，源文本仍缓存时也可被 GC 回收；保留两个常用标量路径，交替查字段时避免重复解析。再次遍历且树已回收时会重新解析，因此是保留空间与重解析成本的取舍。JSONB 返回值仍复制，防止修改结果污染缓存。

4. 新增 CHECK 和 UPSERT：CHECK 在统一 Store 入口检查，事务内复用已绑定委托，函数注册变化后按语句失效。UPSERT 复用唯一键映射，冲突后进入标准 UPDATE 流程，避免逐行整表扫描并保留外键、触发器和原子撤销。

本轮没有新增 SIMD 指令路径。现有 CRC 使用 ARM CRC32 硬件指令，指定整数 JSON 计数路径使用可用的向量指令；这些已存在于基线，不能冒充本轮 SIMD 收益。排序的比较、分支和共享锁成本不适合直接用 SIMD 替代。

## 测量边界

计时完成后仅执行代码空白格式化。格式化构建与实测构建的方法 IL、签名和类型属性指纹一致；DLL 本身因调试及构建元数据而不同，具体 SHA256 和校验结果见 [构建一致性记录](../benchmarks/results/optimization/latest/build-equivalence.json)。报告保留实际计时 DLL 的哈希，不将格式化构建伪装成重新测量。

同机前后差值包含调度、JIT、GC、页缓存、温度和存储状态变化，三轮中位数不是因果或统计显著性证明；小于 5% 只标为接近。下列 CPU 是完成同样单位工作消耗的时间，RSS 是独立进程单库 GC 后驻留量，累计托管分配不是 RSS。SQLite 的原生堆不计入托管分配。Full / Buffered、关闭维护、缓存初建和接口差异详见全面报告。

## Windows x64

### 全部主场景：前后耗时及当前 SQLite

| 场景 | 优化前 PureDb µs | 优化后 PureDb µs | 前后倍率 | SQLite µs | 当前胜方 |
| --- | --- | --- | --- | --- | --- |
| shared_read | 29.392 | 32.935 | 0.89× | 15.908 | 🟥 SQLite 2.07× |
| shared_update | 214.091 | 205.403 | 1.04× | 110.762 | 🟥 SQLite 1.85× |
| kv_get | 0.288 | 0.290 | 0.99× | 5.519 | 🟩 PureDb 19.00× |
| kv_miss | 0.051 | 0.050 | 1.02× | 4.710 | 🟩 PureDb 94.61× |
| kv_range_20 | 2.601 | 2.541 | 1.02× | 16.230 | 🟩 PureDb 6.39× |
| kv_range_1000 | 102.484 | 103.825 | 0.99× | 517.571 | 🟩 PureDb 4.99× |
| sql_point | 0.383 | 0.387 | 0.99× | 5.205 | 🟩 PureDb 13.44× |
| sql_projection | 0.301 | 0.307 | 0.98× | 4.920 | 🟩 PureDb 16.03× |
| sql_range20 | 2.831 | 2.903 | 0.98× | 14.665 | 🟩 PureDb 5.05× |
| sql_range1000 | 117.360 | 115.182 | 1.02× | 504.554 | 🟩 PureDb 4.38× |
| sql_count | 0.171 | 0.173 | 0.99× | 17.547 | 🟩 PureDb 101.71× |
| sql_sum_scan | 1,096.418 | 1,165.713 | 0.94× | 2,986.428 | 🟩 PureDb 2.56× |
| sql_group | 17.730 | 18.351 | 0.97× | 4,289.983 | 🟩 PureDb 233.77× |
| sql_join | 1.215 | 1.241 | 0.98× | 10.242 | 🟩 PureDb 8.25× |
| sql_left_join | 1.066 | 1.040 | 1.02× | 7.007 | 🟩 PureDb 6.73× |
| json_index | 2.373 | 2.553 | 0.93× | 21.555 | 🟩 PureDb 8.44× |
| json_scan | 3.727 | 3.887 | 0.96× | 492.965 | 🟩 PureDb 126.81× |
| json_binary_conversion | 0.986 | 0.958 | 1.03× | 5.795 | 🟩 PureDb 6.05× |
| parallel_get_8 | 0.187 | 0.197 | 0.95× | 1.976 | 🟩 PureDb 10.02× |
| reopen_and_first_range | 678.800 | 677.960 | 1.00× | 610.100 | 🟥 SQLite 1.11× |
| cache_limited_2MiB | 5.427 | 5.387 | 1.01× | 10.306 | 🟩 PureDb 1.91× |
| Full_single | 24,259.748 | 24,428.963 | 0.99× | 24,174.499 | 🟨 接近 |
| Full_concurrent8 | 5,397.850 | 5,968.335 | 0.90× | 31,752.354 | 🟩 PureDb 5.32× |
| Full_batch1000 | 29.045 | 30.731 | 0.95× | 36.436 | 🟩 PureDb 1.19× |
| Full_compact | 52,496.100 | 66,412.900 | 0.79× | 70,444.100 | 🟩 PureDb 1.06× |
| Full_batch_unique | 40.913 | 44.497 | 0.92× | 82.275 | 🟩 PureDb 1.85× |
| Full_batch_update | 28.522 | 28.249 | 1.01× | 53.217 | 🟩 PureDb 1.88× |
| Full_batch_delete | 25.160 | 25.974 | 0.97× | 52.419 | 🟩 PureDb 2.02× |
| Full_update | 19,689.625 | 25,795.701 | 0.76× | 28,820.095 | 🟩 PureDb 1.12× |
| Full_delete | 16,960.080 | 26,060.507 | 0.65× | 23,710.411 | 🟥 SQLite 1.10× |
| Full_mixed | 22,521.532 | 20,037.999 | 1.12× | 23,238.207 | 🟩 PureDb 1.16× |
| Buffered_single | 5.090 | 5.037 | 1.01× | 400.421 | 🟩 PureDb 79.50× |
| Buffered_concurrent8 | 8.637 | 7.428 | 1.16× | 383.994 | 🟩 PureDb 51.70× |
| Buffered_batch1000 | 1.256 | 2.209 | 0.57× | 1.744 | 🟥 SQLite 1.27× |
| Buffered_compact | 82,686.500 | 67,888.800 | 1.22× | 27,230.000 | 🟥 SQLite 2.49× |
| Buffered_batch_unique | 1.458 | 1.516 | 0.96× | 1.567 | 🟨 接近 |
| Buffered_batch_update | 2.052 | 1.663 | 1.23× | 18.352 | 🟩 PureDb 11.04× |
| Buffered_batch_delete | 1.403 | 1.464 | 0.96× | 15.869 | 🟩 PureDb 10.84× |
| Buffered_update | 5.902 | 5.118 | 1.15× | 289.888 | 🟩 PureDb 56.64× |
| Buffered_delete | 4.630 | 5.259 | 0.88× | 341.580 | 🟩 PureDb 64.95× |
| Buffered_mixed | 11.484 | 11.952 | 0.96× | 390.546 | 🟩 PureDb 32.68× |

单位沿用全面报告：普通查询为 µs/查询，批量为 µs/行，压缩为 µs/次，重开为 µs/open。前后倍率大于 1 表示变快，小于 1 表示退化。

### 全部主场景：每单位 CPU 成本

| 场景 | 优化前 CPU µs | 优化后 CPU µs | SQLite CPU µs | 当前 CPU 胜方 |
| --- | --- | --- | --- | --- |
| shared_read | 70.312 | 82.031 | 19.531 | 短区间，不排名 |
| shared_update | 156.250 | 148.438 | 23.438 | 短区间，不排名 |
| kv_get | 0.294 | 0.296 | 5.738 | 🟩 PureDb 19.35× |
| kv_miss | 0.050 | 0.050 | 4.710 | 🟩 PureDb 94.61× |
| kv_range_20 | 2.601 | 2.541 | 15.969 | 🟩 PureDb 6.28× |
| kv_range_1000 | 104.608 | 105.753 | 517.241 | 🟩 PureDb 4.89× |
| sql_point | 0.383 | 0.387 | 5.313 | 🟩 PureDb 13.72× |
| sql_projection | 0.301 | 0.307 | 4.989 | 🟩 PureDb 16.26× |
| sql_range20 | 2.876 | 2.963 | 14.665 | 🟩 PureDb 4.95× |
| sql_range1000 | 114.908 | 115.172 | 513.842 | 🟩 PureDb 4.46× |
| sql_count | 0.171 | 0.173 | 17.547 | 🟩 PureDb 101.71× |
| sql_sum_scan | 1,094.891 | 1,164.596 | 2,918.288 | 🟩 PureDb 2.51× |
| sql_group | 17.730 | 18.454 | 4,375.000 | 🟩 PureDb 237.07× |
| sql_join | 1.215 | 1.241 | 10.082 | 🟩 PureDb 8.12× |
| sql_left_join | 1.044 | 1.040 | 6.943 | 🟩 PureDb 6.67× |
| json_index | 2.373 | 2.553 | 21.555 | 🟩 PureDb 8.44× |
| json_scan | 3.727 | 3.867 | 492.773 | 🟩 PureDb 127.42× |
| json_binary_conversion | 0.993 | 0.978 | 5.915 | 🟩 PureDb 6.05× |
| parallel_get_8 | 1.289 | 1.406 | 13.867 | 🟩 PureDb 9.86× |
| reopen_and_first_range | 0.000 | 0.000 | 0.000 | 短区间，不排名 |
| cache_limited_2MiB | 5.427 | 5.387 | 10.306 | 🟩 PureDb 1.91× |
| Full_single | 3,593.750 | 3,281.250 | 156.250 | 短区间，不排名 |
| Full_concurrent8 | 468.750 | 468.750 | 468.750 | 短区间，不排名 |
| Full_batch1000 | 7.812 | 9.375 | 1.562 | 短区间，不排名 |
| Full_compact | 0.000 | 0.000 | 15,625.000 | 短区间，不排名 |
| Full_batch_unique | 0.000 | 0.000 | 0.000 | 短区间，不排名 |
| Full_batch_update | 7.812 | 10.938 | 4.688 | 短区间，不排名 |
| Full_batch_delete | 9.375 | 7.812 | 3.125 | 短区间，不排名 |
| Full_update | 937.500 | 781.250 | 0.000 | 短区间，不排名 |
| Full_delete | 781.250 | 625.000 | 156.250 | 短区间，不排名 |
| Full_mixed | 625.000 | 937.500 | 156.250 | 短区间，不排名 |
| Buffered_single | 0.000 | 0.000 | 31.250 | 短区间，不排名 |
| Buffered_concurrent8 | 31.250 | 0.000 | 31.250 | 短区间，不排名 |
| Buffered_batch1000 | 1.562 | 3.125 | 1.562 | 短区间，不排名 |
| Buffered_compact | 78,125.000 | 78,125.000 | 0.000 | 短区间，不排名 |
| Buffered_batch_unique | 0.000 | 0.000 | 0.000 | 短区间，不排名 |
| Buffered_batch_update | 1.562 | 1.562 | 4.688 | 短区间，不排名 |
| Buffered_batch_delete | 1.562 | 1.562 | 1.562 | 短区间，不排名 |
| Buffered_update | 0.000 | 0.000 | 31.250 | 短区间，不排名 |
| Buffered_delete | 0.000 | 15.625 | 31.250 | 短区间，不排名 |
| Buffered_mixed | 15.625 | 15.625 | 62.500 | 短区间，不排名 |

### 内存：RSS 与托管存活量

| 场景 | 优化前 RSS MiB | 优化后 RSS MiB | SQLite RSS MiB | 当前谁更高 | PureDb 托管存活 MiB |
| --- | --- | --- | --- | --- | --- |
| point | 86.54 | 86.36 | 78.93 | **PureDb 更高**：多 7.43 MiB，高 9.4% | 34.14 → 34.14 |
| range | 56.61 | 56.68 | 56.50 | **PureDb 更高**：多 0.18 MiB，高 0.3% | 7.19 → 7.19 |
| json | 58.32 | 61.38 | 45.45 | **PureDb 更高**：多 15.94 MiB，高 35.1% | 8.67 → 5.99 |

Shared 更新工作进程逻辑读取字节三轮中位数：236,738,250 → 9,000,876，减少 96.2%。这是操作系统进程逻辑 I/O，不是物理盘读取，也不等于相同比例的耗时降低。

### 当前仍落后的项目（按差距排列）

| 场景 | PureDb µs | SQLite µs | SQLite 倍率 |
| --- | --- | --- | --- |
| Buffered_compact | 67,888.800 | 27,230.000 | 2.49× |
| shared_read | 32.935 | 15.908 | 2.07× |
| shared_update | 205.403 | 110.762 | 1.85× |
| Buffered_batch1000 | 2.209 | 1.744 | 1.27× |
| reopen_and_first_range | 677.960 | 610.100 | 1.11× |
| Full_delete | 26,060.507 | 23,710.411 | 1.10× |

优化前 DLL：`57F0A827BC683100C486E5508E416F22A28C6C6B0BFD68103ED78896E17F7069`；优化后 DLL：`F57A192E23B4467BAEAF12D40B4A5849C3E09E80A8D408120B55E8F517DCC41B`。本轮 [前置样本](../benchmarks/results/optimization/latest/windows/before) 与 [最终样本](../benchmarks/results/latest/windows) 均保留实际构建哈希。

## Linux ARM64

### 全部主场景：前后耗时及当前 SQLite

| 场景 | 优化前 PureDb µs | 优化后 PureDb µs | 前后倍率 | SQLite µs | 当前胜方 |
| --- | --- | --- | --- | --- | --- |
| shared_read | 66.469 | 68.317 | 0.97× | 31.979 | 🟥 SQLite 2.14× |
| shared_update | 298.582 | 297.658 | 1.00× | 60.261 | 🟥 SQLite 4.94× |
| kv_get | 1.132 | 1.085 | 1.04× | 4.455 | 🟩 PureDb 4.11× |
| kv_miss | 0.116 | 0.122 | 0.95× | 2.335 | 🟩 PureDb 19.13× |
| kv_range_20 | 15.132 | 14.800 | 1.02× | 29.081 | 🟩 PureDb 1.96× |
| kv_range_1000 | 274.565 | 287.536 | 0.95× | 983.177 | 🟩 PureDb 3.42× |
| sql_point | 1.104 | 1.127 | 0.98× | 3.579 | 🟩 PureDb 3.18× |
| sql_projection | 0.721 | 0.759 | 0.95× | 2.983 | 🟩 PureDb 3.93× |
| sql_range20 | 6.800 | 6.564 | 1.04× | 19.315 | 🟩 PureDb 2.94× |
| sql_range1000 | 339.957 | 342.081 | 0.99× | 939.422 | 🟩 PureDb 2.75× |
| sql_count | 0.455 | 0.459 | 0.99× | 20.249 | 🟩 PureDb 44.11× |
| sql_sum_scan | 2,750.156 | 2,739.897 | 1.00× | 5,110.227 | 🟩 PureDb 1.87× |
| sql_group | 56.389 | 69.710 | 0.81× | 7,097.957 | 🟩 PureDb 101.82× |
| sql_join | 6.006 | 6.064 | 0.99× | 12.247 | 🟩 PureDb 2.02× |
| sql_left_join | 5.654 | 5.299 | 1.07× | 6.807 | 🟩 PureDb 1.28× |
| json_index | 5.610 | 5.711 | 0.98× | 30.097 | 🟩 PureDb 5.27× |
| json_scan | 12.821 | 12.967 | 0.99× | 668.052 | 🟩 PureDb 51.52× |
| json_binary_conversion | 2.954 | 2.961 | 1.00× | 3.927 | 🟩 PureDb 1.33× |
| parallel_get_8 | 0.848 | 0.846 | 1.00× | 1.526 | 🟩 PureDb 1.80× |
| reopen_and_first_range | 1,042.880 | 1,244.560 | 0.84× | 407.920 | 🟥 SQLite 3.05× |
| cache_limited_2MiB | 4.134 | 4.124 | 1.00× | 7.159 | 🟩 PureDb 1.74× |
| Full_single | 1,586.453 | 2,113.026 | 0.75× | 1,035.954 | 🟥 SQLite 2.04× |
| Full_concurrent8 | 288.955 | 322.613 | 0.90× | 2,325.447 | 🟩 PureDb 7.21× |
| Full_batch1000 | 35.011 | 41.431 | 0.85× | 17.647 | 🟥 SQLite 2.35× |
| Full_compact | 30,280.400 | 63,191.400 | 0.48× | 34,414.100 | 🟥 SQLite 1.84× |
| Full_batch_unique | 48.333 | 39.961 | 1.21× | 8.085 | 🟥 SQLite 4.94× |
| Full_batch_update | 35.971 | 32.696 | 1.10× | 13.798 | 🟥 SQLite 2.37× |
| Full_batch_delete | 15.376 | 11.885 | 1.29× | 9.678 | 🟥 SQLite 1.23× |
| Full_update | 835.838 | 1,113.355 | 0.75× | 1,362.941 | 🟩 PureDb 1.22× |
| Full_delete | 1,148.098 | 1,368.571 | 0.84× | 1,484.841 | 🟩 PureDb 1.08× |
| Full_mixed | 1,770.330 | 2,055.724 | 0.86× | 2,034.480 | 🟨 接近 |
| Buffered_single | 45.134 | 49.573 | 0.91× | 97.285 | 🟩 PureDb 1.96× |
| Buffered_concurrent8 | 53.122 | 68.539 | 0.78× | 194.853 | 🟩 PureDb 2.84× |
| Buffered_batch1000 | 11.636 | 9.179 | 1.27× | 7.372 | 🟥 SQLite 1.25× |
| Buffered_compact | 51,540.200 | 48,037.800 | 1.07× | 19,336.100 | 🟥 SQLite 2.48× |
| Buffered_batch_unique | 8.675 | 12.058 | 0.72× | 5.819 | 🟥 SQLite 2.07× |
| Buffered_batch_update | 9.502 | 9.507 | 1.00× | 14.225 | 🟩 PureDb 1.50× |
| Buffered_batch_delete | 5.445 | 10.673 | 0.51× | 6.917 | 🟥 SQLite 1.54× |
| Buffered_update | 20.710 | 26.038 | 0.80× | 115.282 | 🟩 PureDb 4.43× |
| Buffered_delete | 16.292 | 20.384 | 0.80× | 105.960 | 🟩 PureDb 5.20× |
| Buffered_mixed | 43.166 | 74.677 | 0.58× | 131.205 | 🟩 PureDb 1.76× |

单位沿用全面报告：普通查询为 µs/查询，批量为 µs/行，压缩为 µs/次，重开为 µs/open。前后倍率大于 1 表示变快，小于 1 表示退化。

### 全部主场景：每单位 CPU 成本

| 场景 | 优化前 CPU µs | 优化后 CPU µs | SQLite CPU µs | 当前 CPU 胜方 |
| --- | --- | --- | --- | --- |
| shared_read | 147.403 | 168.823 | 17.677 | 🟥 SQLite 9.55× |
| shared_update | 373.562 | 366.557 | 18.334 | 🟥 SQLite 19.99× |
| kv_get | 1.233 | 1.151 | 4.650 | 🟩 PureDb 4.04× |
| kv_miss | 0.123 | 0.124 | 2.433 | 🟩 PureDb 19.58× |
| kv_range_20 | 17.751 | 18.203 | 30.259 | 🟩 PureDb 1.66× |
| kv_range_1000 | 299.899 | 310.568 | 987.279 | 🟩 PureDb 3.18× |
| sql_point | 1.337 | 1.413 | 3.585 | 🟩 PureDb 2.54× |
| sql_projection | 0.723 | 0.761 | 3.155 | 🟩 PureDb 4.15× |
| sql_range20 | 7.062 | 7.076 | 19.480 | 🟩 PureDb 2.75× |
| sql_range1000 | 340.907 | 344.465 | 965.768 | 🟩 PureDb 2.80× |
| sql_count | 0.455 | 0.459 | 20.626 | 🟩 PureDb 44.92× |
| sql_sum_scan | 3,061.527 | 3,525.963 | 5,110.680 | 🟩 PureDb 1.45× |
| sql_group | 57.667 | 71.327 | 7,715.236 | 🟩 PureDb 108.17× |
| sql_join | 6.464 | 7.167 | 12.249 | 🟩 PureDb 1.71× |
| sql_left_join | 5.753 | 5.300 | 6.807 | 🟩 PureDb 1.28× |
| json_index | 5.611 | 6.017 | 30.170 | 🟩 PureDb 5.01× |
| json_scan | 12.823 | 12.969 | 676.386 | 🟩 PureDb 52.15× |
| json_binary_conversion | 3.112 | 3.295 | 3.994 | 🟩 PureDb 1.21× |
| parallel_get_8 | 2.442 | 2.338 | 4.902 | 🟩 PureDb 2.10× |
| reopen_and_first_range | 1,079.800 | 1,291.400 | 423.800 | 短区间，不排名 |
| cache_limited_2MiB | 4.136 | 4.125 | 7.189 | 🟩 PureDb 1.74× |
| Full_single | 1,354.220 | 1,848.090 | 314.310 | 🟥 SQLite 5.88× |
| Full_concurrent8 | 350.780 | 373.330 | 750.400 | 🟩 PureDb 2.01× |
| Full_batch1000 | 33.568 | 49.984 | 13.594 | 🟥 SQLite 3.68× |
| Full_compact | 24,653.000 | 57,975.000 | 26,169.000 | 🟥 SQLite 2.22× |
| Full_batch_unique | 44.203 | 37.624 | 5.708 | 🟥 SQLite 6.59× |
| Full_batch_update | 47.561 | 34.630 | 11.088 | 🟥 SQLite 3.12× |
| Full_batch_delete | 23.870 | 20.933 | 8.817 | 🟥 SQLite 2.37× |
| Full_update | 426.770 | 579.660 | 1,346.670 | 🟩 PureDb 2.32× |
| Full_delete | 733.660 | 775.730 | 297.440 | 🟥 SQLite 2.61× |
| Full_mixed | 1,699.870 | 1,777.680 | 576.560 | 🟥 SQLite 3.08× |
| Buffered_single | 49.083 | 50.029 | 158.595 | 🟩 PureDb 3.17× |
| Buffered_concurrent8 | 117.790 | 161.212 | 136.931 | 🟥 SQLite 1.18× |
| Buffered_batch1000 | 13.851 | 10.417 | 9.068 | 🟥 SQLite 1.15× |
| Buffered_compact | 42,303.000 | 86,021.000 | 36,421.000 | 🟥 SQLite 2.36× |
| Buffered_batch_unique | 8.985 | 13.634 | 6.146 | 🟥 SQLite 2.22× |
| Buffered_batch_update | 18.354 | 12.781 | 21.318 | 🟩 PureDb 1.67× |
| Buffered_batch_delete | 7.887 | 20.345 | 9.432 | 🟥 SQLite 2.16× |
| Buffered_update | 40.267 | 26.421 | 130.692 | 🟩 PureDb 4.95× |
| Buffered_delete | 32.327 | 38.773 | 88.698 | 🟩 PureDb 2.29× |
| Buffered_mixed | 83.891 | 146.788 | 151.038 | 🟨 接近 |

### 内存：RSS 与托管存活量

| 场景 | 优化前 RSS MiB | 优化后 RSS MiB | SQLite RSS MiB | 当前谁更高 | PureDb 托管存活 MiB |
| --- | --- | --- | --- | --- | --- |
| point | 99.91 | 100.54 | 77.62 | **PureDb 更高**：多 22.91 MiB，高 29.5% | 33.86 → 33.87 |
| range | 69.01 | 69.75 | 56.95 | **PureDb 更高**：多 12.79 MiB，高 22.5% | 6.93 → 6.93 |
| json | 74.75 | 74.88 | 51.09 | **PureDb 更高**：多 23.79 MiB，高 46.6% | 8.43 → 5.75 |

该 Linux 内核不提供有效的进程逻辑 I/O 字节，不能从 -1 推断为零；不发布该指标的前后降低比例。

### 当前仍落后的项目（按差距排列）

| 场景 | PureDb µs | SQLite µs | SQLite 倍率 |
| --- | --- | --- | --- |
| Full_batch_unique | 39.961 | 8.085 | 4.94× |
| shared_update | 297.658 | 60.261 | 4.94× |
| reopen_and_first_range | 1,244.560 | 407.920 | 3.05× |
| Buffered_compact | 48,037.800 | 19,336.100 | 2.48× |
| Full_batch_update | 32.696 | 13.798 | 2.37× |
| Full_batch1000 | 41.431 | 17.647 | 2.35× |
| shared_read | 68.317 | 31.979 | 2.14× |
| Buffered_batch_unique | 12.058 | 5.819 | 2.07× |
| Full_single | 2,113.026 | 1,035.954 | 2.04× |
| Full_compact | 63,191.400 | 34,414.100 | 1.84× |
| Buffered_batch_delete | 10.673 | 6.917 | 1.54× |
| Buffered_batch1000 | 9.179 | 7.372 | 1.25× |
| Full_batch_delete | 11.885 | 9.678 | 1.23× |

优化前 DLL：`57F0A827BC683100C486E5508E416F22A28C6C6B0BFD68103ED78896E17F7069`；优化后 DLL：`F57A192E23B4467BAEAF12D40B4A5849C3E09E80A8D408120B55E8F517DCC41B`。本轮 [前置样本](../benchmarks/results/optimization/latest/linux/before) 与 [最终样本](../benchmarks/results/latest/linux) 均保留实际构建哈希。

## 结论与后续重点

性能、CPU、RSS 没有全面超过 SQLite。应分别看排序分配、Shared 逻辑读取减少及实际耗时；没有收益和出现退化的场景也完整保留。托管运行时、行对象缓存及辅助索引占用仍是 RSS 成本，减少 JSON 树存活不能保证进程驻留量按相同幅度下降。

仍需处理 Shared 锁竞争与 SQL 状态刷新、批量唯一键和完整持久化路径、压缩复制成本、重开索引目录成本及行缓存对象开销。复合索引仍是按版本惰性内存构建，尚未实现分页增量维护；相关子查询、视图、CTE 与窗口仍有文档列明的物化和优化边界。

恢复验证包括每个字节位置截断、完整坏帧拒绝、大帧和缓冲区边界、已提交前缀缩短、四进程原子更新、压缩代次变化及强杀；Windows 和 ARM64 执行相同 DLL，Linux x64 由 CI 验证。详见 [批准的 Shared 方案](SHARED-REPLAY-PLAN.md) 及 [开发验证](DEVELOPMENT-VALIDATION.md)。
