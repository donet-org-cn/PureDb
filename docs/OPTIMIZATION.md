# 本轮优化前后及 SQLite 对照

2026-10-27，`1.0.0-preview.3` 本轮发布构建。优化前为已发布 preview.2（`09cdfff`）。同一平台使用完全相同的测试宿主，仅替换 PureDb.dll，三轮串行轮换旧版、新版和 SQLite，采用各场景中位数；两平台使用相同的一组内核 DLL。

[完整 41 场景与 ADO.NET](PERFORMANCE.md) · [CPU / RSS 谁更高](RESOURCES.md) · [兼容性范围](COMPATIBILITY-REPORT.md) · [博客园文章与性能图](CNBLOGS-preview.3.md)

## 改了什么，为什么可能更快

1. 计划键：对已缓存 AST 使用对象身份及表/模式上下文，避免每次命中缓存时递归计算整棵不可变语法树哈希。仍按模式和函数版本失效；没有缓存业务查询最终结果。

2. 解码：复用 0–1023 的不可变 long 装箱；全列顺序投影直接顺序解码，避免逐字段查找和回填。修改、重复列、重排列和 BLOB 复制仍走正确分支。

3. JSON 计数：弱引用缓存不可变的规范化表达式，避免反复重建表达式树；派生列按数据版本失效，参数值和最终计数没有被该弱缓存固化。原有 SIMD 逐次计算继续执行。

4. 检查点：复用已排序的基础索引，将增量排序后归并，避免重新排序全部主键。表计数更新采用单次字典定位；日志 CRC、边界与持久化顺序不变。

密集 KV 主键的热点页增加有界数组目录，减少并发字典散列；按已有记录数和预算限制容量，稀疏高位键保留字典路径。编码作用域只在编码实际变化时写线程状态；跨编码嵌套调用仍恢复原值。并发初始化、边界键、写后失效和压缩重开均验证。

5. SQLite 文件模式：新增持久模式、页尺寸及编码；完整镜像回退也只备份实际改变/截断的旧页，减少日志写放大。镜像构建仍随数据库规模增长，单独测试，不能算作原版内核的性能。

本轮没有新增 SIMD 指令路径；已有 ARM CRC32 和 JSON 向量计数已存在于基线。文本排序、CAST 和 BLOB 默认值的改动用于语义兼容，不能全部归因于性能收益。

## 测量边界

固定 19 项读取/重开场景的吞吐倍率用每项前后中位数比值的几何平均；耗时下降为 1−1/倍率。同时列出全部 41 场景、CPU、RSS 及退化项，不把 19 项收益声称为所有增删改都改善。

同机差值包含调度、JIT、GC、页缓存、温度和存储变化；三轮不是统计显著性证明。小于 5% 标为接近。首次缓存构建、关闭维护、Full/Buffered 语义与接口成本见全面报告。

每轮只执行旧版、新版和 SQLite 三种引擎；优化前后报告共用当轮 SQLite 对照，复制其 JSON 便于分别汇总。两平台合计 738 次主场景执行；984 是含重复 SQLite 对照的证据记录数，不是独立执行次数。

## Windows x64

### 全部主场景：前后耗时及当前 SQLite

| 场景 | 优化前 PureDb µs | 优化后 PureDb µs | 前后倍率 | SQLite µs | 当前胜方 |
| --- | --- | --- | --- | --- | --- |
| shared_read | 30.535 | 36.189 | 0.84× | 15.671 | 🟥 SQLite 2.31× |
| shared_update | 225.812 | 228.206 | 0.99× | 179.760 | 🟥 SQLite 1.27× |
| kv_get | 0.356 | 0.326 | 1.09× | 5.662 | 🟩 PureDb 17.36× |
| kv_miss | 0.052 | 0.053 | 0.98× | 4.788 | 🟩 PureDb 90.53× |
| kv_range_20 | 3.200 | 2.706 | 1.18× | 16.659 | 🟩 PureDb 6.16× |
| kv_range_1000 | 115.605 | 95.322 | 1.21× | 525.776 | 🟩 PureDb 5.52× |
| sql_point | 0.431 | 0.323 | 1.33× | 5.304 | 🟩 PureDb 16.44× |
| sql_projection | 0.320 | 0.256 | 1.25× | 5.078 | 🟩 PureDb 19.86× |
| sql_range20 | 3.185 | 2.305 | 1.38× | 14.827 | 🟩 PureDb 6.43× |
| sql_range1000 | 131.668 | 106.697 | 1.23× | 511.499 | 🟩 PureDb 4.79× |
| sql_count | 0.181 | 0.195 | 0.93× | 18.204 | 🟩 PureDb 93.59× |
| sql_sum_scan | 1,378.391 | 1,254.796 | 1.10× | 3,222.164 | 🟩 PureDb 2.57× |
| sql_group | 20.118 | 19.665 | 1.02× | 4,673.675 | 🟩 PureDb 237.66× |
| sql_join | 1.259 | 0.951 | 1.32× | 10.562 | 🟩 PureDb 11.11× |
| sql_left_join | 1.060 | 0.778 | 1.36× | 7.474 | 🟩 PureDb 9.60× |
| json_index | 2.583 | 2.069 | 1.25× | 21.854 | 🟩 PureDb 10.56× |
| json_scan | 4.045 | 4.784 | 0.85× | 515.405 | 🟩 PureDb 107.74× |
| json_binary_conversion | 1.028 | 0.846 | 1.22× | 6.165 | 🟩 PureDb 7.29× |
| parallel_get_8 | 0.202 | 0.189 | 1.07× | 2.061 | 🟩 PureDb 10.90× |
| reopen_and_first_range | 626.540 | 721.320 | 0.87× | 637.020 | 🟥 SQLite 1.13× |
| cache_limited_2MiB | 5.708 | 5.840 | 0.98× | 11.041 | 🟩 PureDb 1.89× |
| Full_single | 28,343.376 | 26,346.146 | 1.08× | 31,760.512 | 🟩 PureDb 1.21× |
| Full_concurrent8 | 7,796.116 | 7,644.061 | 1.02× | 36,475.911 | 🟩 PureDb 4.77× |
| Full_batch1000 | 35.729 | 42.384 | 0.84× | 57.303 | 🟩 PureDb 1.35× |
| Full_compact | 65,607.200 | 91,024.800 | 0.72× | 81,425.700 | 🟥 SQLite 1.12× |
| Full_batch_unique | 48.944 | 56.382 | 0.87× | 91.377 | 🟩 PureDb 1.62× |
| Full_batch_update | 31.939 | 38.616 | 0.83× | 54.922 | 🟩 PureDb 1.42× |
| Full_batch_delete | 24.943 | 36.581 | 0.68× | 59.050 | 🟩 PureDb 1.61× |
| Full_update | 25,180.728 | 24,950.604 | 1.01× | 30,655.058 | 🟩 PureDb 1.23× |
| Full_delete | 29,270.291 | 25,818.259 | 1.13× | 24,983.782 | 🟨 接近 |
| Full_mixed | 29,408.250 | 26,908.626 | 1.09× | 24,654.002 | 🟥 SQLite 1.09× |
| Buffered_single | 5.033 | 4.964 | 1.01× | 459.113 | 🟩 PureDb 92.49× |
| Buffered_concurrent8 | 7.482 | 8.060 | 0.93× | 491.507 | 🟩 PureDb 60.98× |
| Buffered_batch1000 | 1.420 | 2.146 | 0.66× | 1.761 | 🟥 SQLite 1.22× |
| Buffered_compact | 75,067.200 | 91,242.500 | 0.82× | 31,659.200 | 🟥 SQLite 2.88× |
| Buffered_batch_unique | 1.494 | 1.930 | 0.77× | 1.829 | 🟥 SQLite 1.05× |
| Buffered_batch_update | 1.786 | 2.442 | 0.73× | 25.321 | 🟩 PureDb 10.37× |
| Buffered_batch_delete | 1.620 | 1.387 | 1.17× | 23.193 | 🟩 PureDb 16.72× |
| Buffered_update | 5.238 | 5.934 | 0.88× | 413.941 | 🟩 PureDb 69.75× |
| Buffered_delete | 13.639 | 4.846 | 2.81× | 478.587 | 🟩 PureDb 98.75× |
| Buffered_mixed | 11.737 | 11.379 | 1.03× | 448.906 | 🟩 PureDb 39.45× |

单位沿用全面报告：普通查询为 µs/查询，批量为 µs/行，压缩为 µs/次，重开为 µs/open。前后倍率大于 1 表示变快，小于 1 表示退化。

### 全部主场景：每单位 CPU 成本

| 场景 | 优化前 CPU µs | 优化后 CPU µs | SQLite CPU µs | 当前 CPU 胜方 |
| --- | --- | --- | --- | --- |
| shared_read | 82.031 | 89.844 | 7.812 | 短区间，不排名 |
| shared_update | 152.344 | 152.344 | 27.344 | 🟥 SQLite 5.57× |
| kv_get | 0.349 | 0.333 | 5.780 | 🟩 PureDb 17.36× |
| kv_miss | 0.051 | 0.053 | 4.788 | 🟩 PureDb 90.53× |
| kv_range_20 | 3.267 | 2.706 | 16.659 | 🟩 PureDb 6.16× |
| kv_range_1000 | 117.481 | 97.296 | 525.578 | 🟩 PureDb 5.40× |
| sql_point | 0.422 | 0.323 | 5.304 | 🟩 PureDb 16.44× |
| sql_projection | 0.320 | 0.256 | 5.090 | 🟩 PureDb 19.90× |
| sql_range20 | 3.251 | 2.348 | 14.827 | 🟩 PureDb 6.31× |
| sql_range1000 | 131.625 | 106.686 | 511.247 | 🟩 PureDb 4.79× |
| sql_count | 0.181 | 0.195 | 18.204 | 🟩 PureDb 93.59× |
| sql_sum_scan | 1,376.147 | 1,280.309 | 3,218.884 | 🟩 PureDb 2.51× |
| sql_group | 20.118 | 20.075 | 4,658.385 | 🟩 PureDb 232.05× |
| sql_join | 1.241 | 0.951 | 10.562 | 🟩 PureDb 11.11× |
| sql_left_join | 1.042 | 0.778 | 7.474 | 🟩 PureDb 9.60× |
| json_index | 2.530 | 2.069 | 21.854 | 🟩 PureDb 10.56× |
| json_scan | 4.045 | 4.684 | 515.110 | 🟩 PureDb 109.97× |
| json_binary_conversion | 1.050 | 0.864 | 6.195 | 🟩 PureDb 7.17× |
| parallel_get_8 | 1.289 | 1.328 | 14.492 | 🟩 PureDb 10.91× |
| reopen_and_first_range | 0.000 | 0.000 | 0.000 | 短区间，不排名 |
| cache_limited_2MiB | 5.708 | 5.840 | 11.041 | 🟩 PureDb 1.89× |
| Full_single | 3,593.750 | 3,437.500 | 625.000 | 短区间，不排名 |
| Full_concurrent8 | 468.750 | 625.000 | 468.750 | 短区间，不排名 |
| Full_batch1000 | 14.062 | 7.812 | 3.125 | 短区间，不排名 |
| Full_compact | 15,625.000 | 15,625.000 | 0.000 | 短区间，不排名 |
| Full_batch_unique | 0.000 | 0.000 | 0.000 | 短区间，不排名 |
| Full_batch_update | 15.625 | 14.062 | 6.250 | 短区间，不排名 |
| Full_batch_delete | 6.250 | 6.250 | 0.000 | 短区间，不排名 |
| Full_update | 781.250 | 625.000 | 156.250 | 短区间，不排名 |
| Full_delete | 781.250 | 625.000 | 312.500 | 短区间，不排名 |
| Full_mixed | 937.500 | 781.250 | 625.000 | 短区间，不排名 |
| Buffered_single | 0.000 | 0.000 | 78.125 | 短区间，不排名 |
| Buffered_concurrent8 | 15.625 | 0.000 | 46.875 | 短区间，不排名 |
| Buffered_batch1000 | 1.562 | 1.562 | 1.562 | 短区间，不排名 |
| Buffered_compact | 31,250.000 | 31,250.000 | 0.000 | 短区间，不排名 |
| Buffered_batch_unique | 0.000 | 0.000 | 0.000 | 短区间，不排名 |
| Buffered_batch_update | 1.562 | 1.562 | 6.250 | 短区间，不排名 |
| Buffered_batch_delete | 3.125 | 1.562 | 1.562 | 短区间，不排名 |
| Buffered_update | 0.000 | 0.000 | 46.875 | 短区间，不排名 |
| Buffered_delete | 15.625 | 0.000 | 46.875 | 短区间，不排名 |
| Buffered_mixed | 15.625 | 0.000 | 46.875 | 短区间，不排名 |

### 内存：RSS 与托管存活量

| 场景 | 优化前 RSS MiB | 优化后 RSS MiB | SQLite RSS MiB | 当前谁更高 | PureDb 托管存活 MiB |
| --- | --- | --- | --- | --- | --- |
| point | 86.64 | 87.00 | 78.85 | **PureDb 更高**：多 8.15 MiB，高 10.3% | 34.14 → 34.17 |
| range | 56.64 | 56.28 | 56.56 | **SQLite 更高**：多 0.28 MiB，高 0.5% | 7.20 → 7.22 |
| json | 61.38 | 61.60 | 45.41 | **PureDb 更高**：多 16.19 MiB，高 35.6% | 6.00 → 6.01 |

沿用上一轮 Shared 协议，本轮工作进程逻辑读取字节三轮中位数：9,000,876 → 9,000,876，减少 0.0%。这是操作系统进程逻辑 I/O，不是物理盘读取，也不等于相同比例的耗时降低。

### 当前仍落后的项目（按差距排列）

| 场景 | PureDb µs | SQLite µs | SQLite 倍率 |
| --- | --- | --- | --- |
| Buffered_compact | 91,242.500 | 31,659.200 | 2.88× |
| shared_read | 36.189 | 15.671 | 2.31× |
| shared_update | 228.206 | 179.760 | 1.27× |
| Buffered_batch1000 | 2.146 | 1.761 | 1.22× |
| reopen_and_first_range | 721.320 | 637.020 | 1.13× |
| Full_compact | 91,024.800 | 81,425.700 | 1.12× |
| Full_mixed | 26,908.626 | 24,654.002 | 1.09× |
| Buffered_batch_unique | 1.930 | 1.829 | 1.05× |

优化前 DLL：`A240817D6F98A3264CB24AD474A870BC1AACA56A9466411CAB12387EB7EB04F5`；优化后 DLL：`30C7DB3221A39E08DF491CB88D85EB04E6ABD226990A2C89C7E6A916E62CC510`。本轮 [前置样本](../benchmarks/results/optimization/latest/windows/before) 与 [最终样本](../benchmarks/results/latest/windows) 均保留实际构建哈希。

## Linux ARM64

### 全部主场景：前后耗时及当前 SQLite

| 场景 | 优化前 PureDb µs | 优化后 PureDb µs | 前后倍率 | SQLite µs | 当前胜方 |
| --- | --- | --- | --- | --- | --- |
| shared_read | 84.332 | 92.305 | 0.91× | 36.853 | 🟥 SQLite 2.50× |
| shared_update | 241.928 | 265.254 | 0.91× | 59.532 | 🟥 SQLite 4.46× |
| kv_get | 1.222 | 1.246 | 0.98× | 4.543 | 🟩 PureDb 3.65× |
| kv_miss | 0.120 | 0.119 | 1.01× | 2.375 | 🟩 PureDb 19.91× |
| kv_range_20 | 13.781 | 13.825 | 1.00× | 30.046 | 🟩 PureDb 2.17× |
| kv_range_1000 | 245.994 | 229.862 | 1.07× | 1,046.166 | 🟩 PureDb 4.55× |
| sql_point | 1.152 | 0.887 | 1.30× | 3.537 | 🟩 PureDb 3.99× |
| sql_projection | 0.816 | 0.648 | 1.26× | 3.030 | 🟩 PureDb 4.68× |
| sql_range20 | 7.276 | 5.186 | 1.40× | 20.521 | 🟩 PureDb 3.96× |
| sql_range1000 | 342.807 | 258.556 | 1.33× | 1,000.178 | 🟩 PureDb 3.87× |
| sql_count | 0.496 | 0.500 | 0.99× | 20.628 | 🟩 PureDb 41.23× |
| sql_sum_scan | 2,875.146 | 2,465.234 | 1.17× | 5,357.262 | 🟩 PureDb 2.17× |
| sql_group | 59.936 | 59.683 | 1.00× | 7,400.288 | 🟩 PureDb 123.99× |
| sql_join | 6.162 | 5.431 | 1.13× | 12.535 | 🟩 PureDb 2.31× |
| sql_left_join | 5.855 | 4.889 | 1.20× | 7.018 | 🟩 PureDb 1.44× |
| json_index | 6.291 | 5.156 | 1.22× | 31.085 | 🟩 PureDb 6.03× |
| json_scan | 13.637 | 13.442 | 1.01× | 684.088 | 🟩 PureDb 50.89× |
| json_binary_conversion | 3.029 | 2.599 | 1.17× | 4.072 | 🟩 PureDb 1.57× |
| parallel_get_8 | 0.597 | 0.651 | 0.92× | 1.779 | 🟩 PureDb 2.73× |
| reopen_and_first_range | 773.840 | 647.320 | 1.20× | 506.560 | 🟥 SQLite 1.28× |
| cache_limited_2MiB | 4.369 | 4.391 | 1.00× | 6.093 | 🟩 PureDb 1.39× |
| Full_single | 1,221.091 | 1,011.368 | 1.21× | 871.288 | 🟥 SQLite 1.16× |
| Full_concurrent8 | 280.408 | 254.038 | 1.10× | 2,102.165 | 🟩 PureDb 8.28× |
| Full_batch1000 | 20.777 | 17.696 | 1.17× | 9.516 | 🟥 SQLite 1.86× |
| Full_compact | 32,642.900 | 30,531.400 | 1.07× | 26,478.200 | 🟥 SQLite 1.15× |
| Full_batch_unique | 22.490 | 21.649 | 1.04× | 8.437 | 🟥 SQLite 2.57× |
| Full_batch_update | 19.809 | 19.960 | 0.99× | 16.874 | 🟥 SQLite 1.18× |
| Full_batch_delete | 11.254 | 10.210 | 1.10× | 7.404 | 🟥 SQLite 1.38× |
| Full_update | 906.487 | 1,070.350 | 0.85× | 1,165.722 | 🟩 PureDb 1.09× |
| Full_delete | 959.350 | 912.815 | 1.05× | 1,084.716 | 🟩 PureDb 1.19× |
| Full_mixed | 1,428.283 | 1,356.961 | 1.05× | 1,175.545 | 🟥 SQLite 1.15× |
| Buffered_single | 70.309 | 41.018 | 1.71× | 100.856 | 🟩 PureDb 2.46× |
| Buffered_concurrent8 | 41.036 | 36.868 | 1.11× | 107.203 | 🟩 PureDb 2.91× |
| Buffered_batch1000 | 11.415 | 12.141 | 0.94× | 3.331 | 🟥 SQLite 3.64× |
| Buffered_compact | 51,321.600 | 57,012.700 | 0.90× | 11,694.700 | 🟥 SQLite 4.88× |
| Buffered_batch_unique | 8.089 | 11.689 | 0.69× | 2.493 | 🟥 SQLite 4.69× |
| Buffered_batch_update | 8.822 | 11.529 | 0.77× | 8.298 | 🟥 SQLite 1.39× |
| Buffered_batch_delete | 5.169 | 5.156 | 1.00× | 5.146 | 🟨 接近 |
| Buffered_update | 18.888 | 16.655 | 1.13× | 83.793 | 🟩 PureDb 5.03× |
| Buffered_delete | 16.028 | 15.357 | 1.04× | 70.438 | 🟩 PureDb 4.59× |
| Buffered_mixed | 48.577 | 37.091 | 1.31× | 135.212 | 🟩 PureDb 3.65× |

单位沿用全面报告：普通查询为 µs/查询，批量为 µs/行，压缩为 µs/次，重开为 µs/open。前后倍率大于 1 表示变快，小于 1 表示退化。

### 全部主场景：每单位 CPU 成本

| 场景 | 优化前 CPU µs | 优化后 CPU µs | SQLite CPU µs | 当前 CPU 胜方 |
| --- | --- | --- | --- | --- |
| shared_read | 148.703 | 157.401 | 18.457 | 🟥 SQLite 8.53× |
| shared_update | 238.383 | 265.145 | 21.704 | 🟥 SQLite 12.22× |
| kv_get | 1.251 | 1.301 | 4.614 | 🟩 PureDb 3.55× |
| kv_miss | 0.123 | 0.122 | 2.417 | 🟩 PureDb 19.81× |
| kv_range_20 | 14.791 | 15.077 | 30.722 | 🟩 PureDb 2.04× |
| kv_range_1000 | 251.788 | 229.636 | 1,055.734 | 🟩 PureDb 4.60× |
| sql_point | 1.335 | 0.989 | 3.546 | 🟩 PureDb 3.58× |
| sql_projection | 0.817 | 0.652 | 3.060 | 🟩 PureDb 4.69× |
| sql_range20 | 7.485 | 5.348 | 20.580 | 🟩 PureDb 3.85× |
| sql_range1000 | 343.924 | 258.597 | 1,009.809 | 🟩 PureDb 3.90× |
| sql_count | 0.496 | 0.500 | 20.737 | 🟩 PureDb 41.45× |
| sql_sum_scan | 3,172.762 | 2,697.285 | 5,356.636 | 🟩 PureDb 1.99× |
| sql_group | 61.673 | 61.548 | 7,705.167 | 🟩 PureDb 125.19× |
| sql_join | 6.174 | 5.432 | 12.537 | 🟩 PureDb 2.31× |
| sql_left_join | 5.852 | 4.893 | 7.006 | 🟩 PureDb 1.43× |
| json_index | 6.323 | 5.155 | 31.080 | 🟩 PureDb 6.03× |
| json_scan | 13.650 | 13.450 | 686.560 | 🟩 PureDb 51.05× |
| json_binary_conversion | 3.076 | 2.776 | 4.151 | 🟩 PureDb 1.50× |
| parallel_get_8 | 1.218 | 1.225 | 4.485 | 🟩 PureDb 3.66× |
| reopen_and_first_range | 794.800 | 670.000 | 525.800 | 短区间，不排名 |
| cache_limited_2MiB | 4.373 | 4.392 | 6.094 | 🟩 PureDb 1.39× |
| Full_single | 794.950 | 626.130 | 218.990 | 🟥 SQLite 2.86× |
| Full_concurrent8 | 253.510 | 222.470 | 525.210 | 🟩 PureDb 2.36× |
| Full_batch1000 | 19.331 | 15.850 | 7.142 | 🟥 SQLite 2.22× |
| Full_compact | 27,928.000 | 24,470.000 | 20,576.000 | 🟥 SQLite 1.19× |
| Full_batch_unique | 20.761 | 19.919 | 6.584 | 🟥 SQLite 3.03× |
| Full_batch_update | 17.990 | 18.211 | 11.608 | 🟥 SQLite 1.57× |
| Full_batch_delete | 13.236 | 18.746 | 4.206 | 🟥 SQLite 4.46× |
| Full_update | 431.790 | 318.260 | 1,314.870 | 🟩 PureDb 4.13× |
| Full_delete | 514.140 | 597.060 | 1,056.740 | 🟩 PureDb 1.77× |
| Full_mixed | 1,356.770 | 1,197.360 | 283.820 | 🟥 SQLite 4.22× |
| Buffered_single | 70.687 | 41.244 | 85.605 | 🟩 PureDb 2.08× |
| Buffered_concurrent8 | 77.089 | 81.002 | 74.213 | 🟥 SQLite 1.09× |
| Buffered_batch1000 | 17.937 | 14.382 | 3.312 | 🟥 SQLite 4.34× |
| Buffered_compact | 94,895.000 | 94,543.000 | 16,164.000 | 🟥 SQLite 5.85× |
| Buffered_batch_unique | 10.383 | 11.895 | 3.880 | 短区间，不排名 |
| Buffered_batch_update | 11.487 | 22.099 | 10.384 | 🟥 SQLite 2.13× |
| Buffered_batch_delete | 5.306 | 5.271 | 2.455 | 🟥 SQLite 2.15× |
| Buffered_update | 19.228 | 16.917 | 88.013 | 🟩 PureDb 5.20× |
| Buffered_delete | 28.683 | 15.618 | 65.884 | 🟩 PureDb 4.22× |
| Buffered_mixed | 49.622 | 38.050 | 107.881 | 🟩 PureDb 2.84× |

### 内存：RSS 与托管存活量

| 场景 | 优化前 RSS MiB | 优化后 RSS MiB | SQLite RSS MiB | 当前谁更高 | PureDb 托管存活 MiB |
| --- | --- | --- | --- | --- | --- |
| point | 100.93 | 101.80 | 77.64 | **PureDb 更高**：多 24.15 MiB，高 31.1% | 33.88 → 33.91 |
| range | 70.63 | 74.25 | 56.73 | **PureDb 更高**：多 17.52 MiB，高 30.9% | 6.95 → 6.98 |
| json | 75.20 | 75.75 | 49.68 | **PureDb 更高**：多 26.08 MiB，高 52.5% | 5.76 → 5.77 |

该 Linux 内核不提供有效的进程逻辑 I/O 字节，不能从 -1 推断为零；不发布该指标的前后降低比例。

### 当前仍落后的项目（按差距排列）

| 场景 | PureDb µs | SQLite µs | SQLite 倍率 |
| --- | --- | --- | --- |
| Buffered_compact | 57,012.700 | 11,694.700 | 4.88× |
| Buffered_batch_unique | 11.689 | 2.493 | 4.69× |
| shared_update | 265.254 | 59.532 | 4.46× |
| Buffered_batch1000 | 12.141 | 3.331 | 3.64× |
| Full_batch_unique | 21.649 | 8.437 | 2.57× |
| shared_read | 92.305 | 36.853 | 2.50× |
| Full_batch1000 | 17.696 | 9.516 | 1.86× |
| Buffered_batch_update | 11.529 | 8.298 | 1.39× |
| Full_batch_delete | 10.210 | 7.404 | 1.38× |
| reopen_and_first_range | 647.320 | 506.560 | 1.28× |
| Full_batch_update | 19.960 | 16.874 | 1.18× |
| Full_single | 1,011.368 | 871.288 | 1.16× |
| Full_mixed | 1,356.961 | 1,175.545 | 1.15× |
| Full_compact | 30,531.400 | 26,478.200 | 1.15× |

优化前 DLL：`A240817D6F98A3264CB24AD474A870BC1AACA56A9466411CAB12387EB7EB04F5`；优化后 DLL：`30C7DB3221A39E08DF491CB88D85EB04E6ABD226990A2C89C7E6A916E62CC510`。本轮 [前置样本](../benchmarks/results/optimization/latest/linux/before) 与 [最终样本](../benchmarks/results/latest/linux) 均保留实际构建哈希。

## 结论与后续重点

性能、CPU、RSS 没有全面超过 SQLite。应分别看排序分配、Shared 逻辑读取减少及实际耗时；没有收益和出现退化的场景也完整保留。托管运行时、行对象缓存及辅助索引占用仍是 RSS 成本，减少 JSON 树存活不能保证进程驻留量按相同幅度下降。

仍需处理 Shared 锁竞争与 SQL 状态刷新、批量唯一键和完整持久化路径、压缩复制成本、重开索引目录成本及行缓存对象开销。复合索引仍是按版本惰性内存构建，尚未实现分页增量维护；相关子查询、视图、CTE 与窗口仍有文档列明的物化和优化边界。

恢复验证包括每个字节位置截断、完整坏帧拒绝、大帧和缓冲区边界、已提交前缀缩短、四进程原子更新、压缩代次变化及强杀；Windows 和 ARM64 执行相同 DLL，Linux x64 由 CI 验证。详见 [批准的 Shared 方案](SHARED-REPLAY-PLAN.md) 及 [开发验证](DEVELOPMENT-VALIDATION.md)。


## 固定 19 场景验收

| 平台 | 吞吐几何平均倍率 | 吞吐提升 | 归一化耗时下降 | 耗时下降至少 10% |
| --- | --- | --- | --- | --- |
| Windows x64 | 1.127× | 12.66% | 11.24% | ✅ |
| Linux ARM64 | 1.115× | 11.55% | 10.35% | ✅ |

包含全部固定 19 项读取/重开用例；没有挑掉退化项。阈值不表示每个场景改善 10%，更不代表全部 41 项增删改性能提升 10%。
