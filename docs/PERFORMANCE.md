# PureDb 与 SQLite 最新全面对比

PureDb `1.0.0-preview.2` 原生格式，对照原版 SQLite 3.54.1。每平台 41 场景 × 2 引擎 × 3 轮 = 246 主样本，另有 18 个独立内存、72 个 ADO 和 6 个充分预热样本。只保留最新结果。

| 平台 | PureDb 耗时胜 | SQLite 耗时胜 | 接近 |
| --- | --- | --- | --- |
| Windows x64 | 33 | 6 | 2 |
| Linux ARM64 | 27 | 13 | 1 |

主套件串行查询预热至少 500 ms 且至少 64 次，测量至少 750 ms；三轮取中位数，低于 5% 标接近，不是统计等价证明。KV 与 SQL、Core 与 ADO、Full 与 Buffered 分开解释。数据集为 10 万行，缓存与 OS 文件缓存已预热。

PureDb 对原生 SQLite 的 .NET C API 封装，双方实际读取并物化结果；包含托管/原生边界开销。ADO 补充对 SQLite C API，不是对 Microsoft.Data.Sqlite 驱动。每次调用单位见表，批量按行，压缩按次，重开按 opens。

6 项使用 PureDb KV API 对 SQLite SQL：kv_get、kv_miss、kv_range_20、kv_range_1000、parallel_get_8、cache_limited_2MiB。聚合、JOIN 和 JSON 等重复查询可复用按表版本失效的缓存；初建和维护成本可能在计时之外。

Full 是各自强制刷盘路径；Buffered 与 SQLite WAL/NORMAL 的 checkpoint 和断电语义不同。写入计时不含关闭，PureDb 部分索引维护在查询或关闭时执行。重开在同一进程连续执行 5 次，允许有界目录缓存，不代表全新进程冷启动。

[资源占用直观表](RESOURCES.md) · [Windows 交互面板](PERFORMANCE.html) · [复现方法](BENCHMARKS.md)

## Windows x64

| 场景 | 单位 | PureDb µs | SQLite µs | 耗时胜方 |
| --- | --- | --- | --- | --- |
| 四进程读取 · shared_read | queries | 32.935 | 15.908 | 🟥 SQLite 2.07× |
| 四进程原子更新 · shared_update | updates | 205.403 | 110.762 | 🟥 SQLite 1.85× |
| KV 主键命中 · kv_get | queries | 0.290 | 5.519 | 🟩 PureDb 19.00× |
| KV 主键未命中 · kv_miss | queries | 0.050 | 4.710 | 🟩 PureDb 94.61× |
| KV 范围 20 行 · kv_range_20 | queries | 2.541 | 16.230 | 🟩 PureDb 6.39× |
| KV 范围 1000 行 · kv_range_1000 | queries | 103.825 | 517.571 | 🟩 PureDb 4.99× |
| SQL 主键完整行 · sql_point | queries | 0.387 | 5.205 | 🟩 PureDb 13.44× |
| SQL 主键覆盖投影 · sql_projection | queries | 0.307 | 4.920 | 🟩 PureDb 16.03× |
| SQL 范围 20 行 · sql_range20 | queries | 2.903 | 14.665 | 🟩 PureDb 5.05× |
| SQL 范围 1000 行 · sql_range1000 | queries | 115.182 | 504.554 | 🟩 PureDb 4.38× |
| COUNT(*) · sql_count | queries | 0.173 | 17.547 | 🟩 PureDb 101.71× |
| SUM 扫描 · sql_sum_scan | queries | 1,165.713 | 2,986.428 | 🟩 PureDb 2.56× |
| 分组计数 · sql_group | queries | 18.351 | 4,289.983 | 🟩 PureDb 233.77× |
| INNER JOIN · sql_join | queries | 1.241 | 10.242 | 🟩 PureDb 8.25× |
| LEFT JOIN · sql_left_join | queries | 1.040 | 7.007 | 🟩 PureDb 6.73× |
| JSON 表达式索引 · json_index | queries | 2.553 | 21.555 | 🟩 PureDb 8.44× |
| JSON 整数取余计数 · json_scan | queries | 3.887 | 492.965 | 🟩 PureDb 126.81× |
| JSONB 转换提取 · json_binary_conversion | queries | 0.958 | 5.795 | 🟩 PureDb 6.05× |
| 八线程 KV 读取 · parallel_get_8 | queries | 0.197 | 1.976 | 🟩 PureDb 10.02× |
| 重开并首次范围查询 · reopen_and_first_range | opens | 677.960 | 610.100 | 🟥 SQLite 1.11× |
| 2 MiB 页缓存读取 · cache_limited_2MiB | queries | 5.387 | 10.306 | 🟩 PureDb 1.91× |
| Full 逐条新增 · Full_single | rows | 24,428.963 | 24,174.499 | 🟨 接近 |
| Full 八线程新增 · Full_concurrent8 | rows | 5,968.335 | 31,752.354 | 🟩 PureDb 5.32× |
| Full 批量新增 · Full_batch1000 | rows | 30.731 | 36.436 | 🟩 PureDb 1.19× |
| Full 压缩 · Full_compact | compactions | 66,412.900 | 70,444.100 | 🟩 PureDb 1.06× |
| Full 唯一索引批量新增 · Full_batch_unique | rows | 44.497 | 82.275 | 🟩 PureDb 1.85× |
| Full 批量更新 · Full_batch_update | rows | 28.249 | 53.217 | 🟩 PureDb 1.88× |
| Full 批量删除 · Full_batch_delete | rows | 25.974 | 52.419 | 🟩 PureDb 2.02× |
| Full 逐条更新 · Full_update | rows | 25,795.701 | 28,820.095 | 🟩 PureDb 1.12× |
| Full 逐条删除 · Full_delete | rows | 26,060.507 | 23,710.411 | 🟥 SQLite 1.10× |
| Full 一次更新加五次读取 · Full_mixed | write+5reads | 20,037.999 | 23,238.207 | 🟩 PureDb 1.16× |
| Buffered 逐条新增 · Buffered_single | rows | 5.037 | 400.421 | 🟩 PureDb 79.50× |
| Buffered 八线程新增 · Buffered_concurrent8 | rows | 7.428 | 383.994 | 🟩 PureDb 51.70× |
| Buffered 批量新增 · Buffered_batch1000 | rows | 2.209 | 1.744 | 🟥 SQLite 1.27× |
| Buffered 压缩 · Buffered_compact | compactions | 67,888.800 | 27,230.000 | 🟥 SQLite 2.49× |
| Buffered 唯一索引批量新增 · Buffered_batch_unique | rows | 1.516 | 1.567 | 🟨 接近 |
| Buffered 批量更新 · Buffered_batch_update | rows | 1.663 | 18.352 | 🟩 PureDb 11.04× |
| Buffered 批量删除 · Buffered_batch_delete | rows | 1.464 | 15.869 | 🟩 PureDb 10.84× |
| Buffered 逐条更新 · Buffered_update | rows | 5.118 | 289.888 | 🟩 PureDb 56.64× |
| Buffered 逐条删除 · Buffered_delete | rows | 5.259 | 341.580 | 🟩 PureDb 64.95× |
| Buffered 一次更新加五次读取 · Buffered_mixed | write+5reads | 11.952 | 390.546 | 🟩 PureDb 32.68× |

| 场景 | PureDb 文件 MiB | SQLite 文件 MiB |
| --- | --- | --- |
| 四进程读取 | 0.012 | 0.008 |
| 四进程原子更新 | 0.249 | 0.008 |
| KV 主键命中 | 47.895 | 30.863 |
| KV 主键未命中 | 47.895 | 30.863 |
| KV 范围 20 行 | 47.895 | 30.863 |
| KV 范围 1000 行 | 47.895 | 30.863 |
| SQL 主键完整行 | 47.895 | 30.863 |
| SQL 主键覆盖投影 | 47.895 | 30.863 |
| SQL 范围 20 行 | 47.895 | 30.863 |
| SQL 范围 1000 行 | 47.895 | 30.863 |
| COUNT(*) | 47.895 | 30.863 |
| SUM 扫描 | 47.895 | 30.863 |
| 分组计数 | 47.895 | 30.863 |
| INNER JOIN | 47.895 | 30.863 |
| LEFT JOIN | 47.895 | 30.863 |
| JSON 表达式索引 | 47.895 | 30.863 |
| JSON 整数取余计数 | 47.895 | 30.863 |
| JSONB 转换提取 | 47.895 | 30.863 |
| 八线程 KV 读取 | 47.895 | 30.863 |
| 重开并首次范围查询 | 47.895 | 30.863 |
| 2 MiB 页缓存读取 | 47.895 | 30.863 |
| Full 逐条新增 | 0.031 | 0.888 |
| Full 八线程新增 | 0.031 | 1.190 |
| Full 批量新增 | 2.890 | 3.399 |
| Full 压缩 | 1.445 | 4.137 |
| Full 唯一索引批量新增 | 0.290 | 0.361 |
| Full 批量更新 | 5.780 | 7.021 |
| Full 批量删除 | 3.119 | 6.703 |
| Full 逐条更新 | 0.060 | 0.876 |
| Full 逐条删除 | 0.034 | 1.021 |
| Full 一次更新加五次读取 | 0.060 | 0.876 |
| Buffered 逐条新增 | 0.309 | 4.234 |
| Buffered 八线程新增 | 0.309 | 11.907 |
| Buffered 批量新增 | 2.890 | 3.399 |
| Buffered 压缩 | 1.445 | 4.137 |
| Buffered 唯一索引批量新增 | 0.290 | 0.361 |
| Buffered 批量更新 | 5.780 | 7.021 |
| Buffered 批量删除 | 3.119 | 6.703 |
| Buffered 逐条更新 | 0.598 | 4.250 |
| Buffered 逐条删除 | 0.332 | 4.253 |
| Buffered 一次更新加五次读取 | 0.598 | 4.250 |

文件含侧文件，是场景结束时占用而非物理写入字节。查询套件均记录 SQL 文件，KV 行不代表 KV 文件本身体积。进程逻辑 I/O 见原始样本；Linux 定制内核没有 /proc/self/io 时记录 -1，不当成 0。设备计数含全机其他活动，不能归因于数据库进程。

### ADO.NET 补充

| 场景 | 单位 | Core | ADO.NET | SQLite C API | ADO 胜方 |
| --- | --- | --- | --- | --- | --- |
| point | µs/查询 | 0.703 | 1.461 | 5.452 | 🟩 PureDb 3.73× |
| range20 | µs/查询 | 4.228 | 5.057 | 14.685 | 🟩 PureDb 2.90× |
| group | µs/查询 | 27.087 | 45.193 | 4,358.574 | 🟩 PureDb 96.44× |
| join | µs/查询 | 2.115 | 2.907 | 10.813 | 🟩 PureDb 3.72× |
| json_scan | µs/查询 | 6.134 | 5.981 | 505.463 | 🟩 PureDb 84.51× |
| jsonb | µs/查询 | 1.445 | 2.028 | 5.771 | 🟩 PureDb 2.85× |
| insert1000 | µs/行 | 20.043 | 18.579 | 341.868 | 🟩 PureDb 18.40× |
| batch10000 | µs/千行事务 | 4,779.530 | 4,420.930 | 1,667.380 | 🟥 SQLite 2.65× |

### 磁盘观察：全机设备范围

| 引擎 | 全机设备 | 忙碌率 % | 读 MiB/s | 写 MiB/s |
| --- | --- | --- | --- | --- |
| Optimized | 0 d: | 0.00 | 0.00 | 0.00 |
| Optimized | 1 | 86.51 | 0.21 | 2.30 |
| Optimized | 2 c: | 1.63 | 0.00 | 0.13 |
| Optimized | _total | 29.38 | 0.22 | 2.43 |
| SQLite | 0 d: | 0.03 | 0.00 | 0.01 |
| SQLite | 1 | 93.82 | 0.00 | 5.53 |
| SQLite | 2 c: | 1.51 | 0.02 | 0.16 |
| SQLite | _total | 31.79 | 0.02 | 5.69 |

Windows 物理设备数据为每引擎一次独立写入套件补测，不是三轮主样本的 I/O，也不纳入主成绩。

设备计数包括系统服务与延迟回写，不等于引擎独占 I/O。Linux 为三轮套件期间 diskstats 增量；若内核未提供有效计数则不能排名。进程逻辑读写字节、次数、CPU 和关闭后文件列表保留在 raw JSON。

实测程序集 SHA256：`F57A192E23B4467BAEAF12D40B4A5849C3E09E80A8D408120B55E8F517DCC41B`；.NET 10.0.13；整机 12 核；[原始结果](../benchmarks/results/latest/windows)。

## Linux ARM64

| 场景 | 单位 | PureDb µs | SQLite µs | 耗时胜方 |
| --- | --- | --- | --- | --- |
| 四进程读取 · shared_read | queries | 68.317 | 31.979 | 🟥 SQLite 2.14× |
| 四进程原子更新 · shared_update | updates | 297.658 | 60.261 | 🟥 SQLite 4.94× |
| KV 主键命中 · kv_get | queries | 1.085 | 4.455 | 🟩 PureDb 4.11× |
| KV 主键未命中 · kv_miss | queries | 0.122 | 2.335 | 🟩 PureDb 19.13× |
| KV 范围 20 行 · kv_range_20 | queries | 14.800 | 29.081 | 🟩 PureDb 1.96× |
| KV 范围 1000 行 · kv_range_1000 | queries | 287.536 | 983.177 | 🟩 PureDb 3.42× |
| SQL 主键完整行 · sql_point | queries | 1.127 | 3.579 | 🟩 PureDb 3.18× |
| SQL 主键覆盖投影 · sql_projection | queries | 0.759 | 2.983 | 🟩 PureDb 3.93× |
| SQL 范围 20 行 · sql_range20 | queries | 6.564 | 19.315 | 🟩 PureDb 2.94× |
| SQL 范围 1000 行 · sql_range1000 | queries | 342.081 | 939.422 | 🟩 PureDb 2.75× |
| COUNT(*) · sql_count | queries | 0.459 | 20.249 | 🟩 PureDb 44.11× |
| SUM 扫描 · sql_sum_scan | queries | 2,739.897 | 5,110.227 | 🟩 PureDb 1.87× |
| 分组计数 · sql_group | queries | 69.710 | 7,097.957 | 🟩 PureDb 101.82× |
| INNER JOIN · sql_join | queries | 6.064 | 12.247 | 🟩 PureDb 2.02× |
| LEFT JOIN · sql_left_join | queries | 5.299 | 6.807 | 🟩 PureDb 1.28× |
| JSON 表达式索引 · json_index | queries | 5.711 | 30.097 | 🟩 PureDb 5.27× |
| JSON 整数取余计数 · json_scan | queries | 12.967 | 668.052 | 🟩 PureDb 51.52× |
| JSONB 转换提取 · json_binary_conversion | queries | 2.961 | 3.927 | 🟩 PureDb 1.33× |
| 八线程 KV 读取 · parallel_get_8 | queries | 0.846 | 1.526 | 🟩 PureDb 1.80× |
| 重开并首次范围查询 · reopen_and_first_range | opens | 1,244.560 | 407.920 | 🟥 SQLite 3.05× |
| 2 MiB 页缓存读取 · cache_limited_2MiB | queries | 4.124 | 7.159 | 🟩 PureDb 1.74× |
| Full 逐条新增 · Full_single | rows | 2,113.026 | 1,035.954 | 🟥 SQLite 2.04× |
| Full 八线程新增 · Full_concurrent8 | rows | 322.613 | 2,325.447 | 🟩 PureDb 7.21× |
| Full 批量新增 · Full_batch1000 | rows | 41.431 | 17.647 | 🟥 SQLite 2.35× |
| Full 压缩 · Full_compact | compactions | 63,191.400 | 34,414.100 | 🟥 SQLite 1.84× |
| Full 唯一索引批量新增 · Full_batch_unique | rows | 39.961 | 8.085 | 🟥 SQLite 4.94× |
| Full 批量更新 · Full_batch_update | rows | 32.696 | 13.798 | 🟥 SQLite 2.37× |
| Full 批量删除 · Full_batch_delete | rows | 11.885 | 9.678 | 🟥 SQLite 1.23× |
| Full 逐条更新 · Full_update | rows | 1,113.355 | 1,362.941 | 🟩 PureDb 1.22× |
| Full 逐条删除 · Full_delete | rows | 1,368.571 | 1,484.841 | 🟩 PureDb 1.08× |
| Full 一次更新加五次读取 · Full_mixed | write+5reads | 2,055.724 | 2,034.480 | 🟨 接近 |
| Buffered 逐条新增 · Buffered_single | rows | 49.573 | 97.285 | 🟩 PureDb 1.96× |
| Buffered 八线程新增 · Buffered_concurrent8 | rows | 68.539 | 194.853 | 🟩 PureDb 2.84× |
| Buffered 批量新增 · Buffered_batch1000 | rows | 9.179 | 7.372 | 🟥 SQLite 1.25× |
| Buffered 压缩 · Buffered_compact | compactions | 48,037.800 | 19,336.100 | 🟥 SQLite 2.48× |
| Buffered 唯一索引批量新增 · Buffered_batch_unique | rows | 12.058 | 5.819 | 🟥 SQLite 2.07× |
| Buffered 批量更新 · Buffered_batch_update | rows | 9.507 | 14.225 | 🟩 PureDb 1.50× |
| Buffered 批量删除 · Buffered_batch_delete | rows | 10.673 | 6.917 | 🟥 SQLite 1.54× |
| Buffered 逐条更新 · Buffered_update | rows | 26.038 | 115.282 | 🟩 PureDb 4.43× |
| Buffered 逐条删除 · Buffered_delete | rows | 20.384 | 105.960 | 🟩 PureDb 5.20× |
| Buffered 一次更新加五次读取 · Buffered_mixed | write+5reads | 74.677 | 131.205 | 🟩 PureDb 1.76× |

| 场景 | PureDb 文件 MiB | SQLite 文件 MiB |
| --- | --- | --- |
| 四进程读取 | 0.012 | 0.008 |
| 四进程原子更新 | 0.249 | 0.008 |
| KV 主键命中 | 47.895 | 30.863 |
| KV 主键未命中 | 47.895 | 30.863 |
| KV 范围 20 行 | 47.895 | 30.863 |
| KV 范围 1000 行 | 47.895 | 30.863 |
| SQL 主键完整行 | 47.895 | 30.863 |
| SQL 主键覆盖投影 | 47.895 | 30.863 |
| SQL 范围 20 行 | 47.895 | 30.863 |
| SQL 范围 1000 行 | 47.895 | 30.863 |
| COUNT(*) | 47.895 | 30.863 |
| SUM 扫描 | 47.895 | 30.863 |
| 分组计数 | 47.895 | 30.863 |
| INNER JOIN | 47.895 | 30.863 |
| LEFT JOIN | 47.895 | 30.863 |
| JSON 表达式索引 | 47.895 | 30.863 |
| JSON 整数取余计数 | 47.895 | 30.863 |
| JSONB 转换提取 | 47.895 | 30.863 |
| 八线程 KV 读取 | 47.895 | 30.863 |
| 重开并首次范围查询 | 47.895 | 30.863 |
| 2 MiB 页缓存读取 | 47.895 | 30.863 |
| Full 逐条新增 | 0.031 | 0.888 |
| Full 八线程新增 | 0.031 | 1.096 |
| Full 批量新增 | 2.890 | 3.399 |
| Full 压缩 | 1.445 | 4.137 |
| Full 唯一索引批量新增 | 0.290 | 0.361 |
| Full 批量更新 | 5.780 | 7.021 |
| Full 批量删除 | 3.119 | 6.703 |
| Full 逐条更新 | 0.060 | 0.876 |
| Full 逐条删除 | 0.034 | 1.021 |
| Full 一次更新加五次读取 | 0.060 | 0.876 |
| Buffered 逐条新增 | 0.309 | 4.234 |
| Buffered 八线程新增 | 0.309 | 8.108 |
| Buffered 批量新增 | 2.890 | 3.399 |
| Buffered 压缩 | 1.445 | 4.137 |
| Buffered 唯一索引批量新增 | 0.290 | 0.361 |
| Buffered 批量更新 | 5.780 | 7.021 |
| Buffered 批量删除 | 3.119 | 6.703 |
| Buffered 逐条更新 | 0.598 | 4.250 |
| Buffered 逐条删除 | 0.332 | 4.253 |
| Buffered 一次更新加五次读取 | 0.598 | 4.250 |

文件含侧文件，是场景结束时占用而非物理写入字节。查询套件均记录 SQL 文件，KV 行不代表 KV 文件本身体积。进程逻辑 I/O 见原始样本；Linux 定制内核没有 /proc/self/io 时记录 -1，不当成 0。设备计数含全机其他活动，不能归因于数据库进程。

### ADO.NET 补充

| 场景 | 单位 | Core | ADO.NET | SQLite C API | ADO 胜方 |
| --- | --- | --- | --- | --- | --- |
| point | µs/查询 | 3.700 | 13.995 | 3.992 | 🟥 SQLite 3.51× |
| range20 | µs/查询 | 10.774 | 23.896 | 19.157 | 🟥 SQLite 1.25× |
| group | µs/查询 | 89.103 | 139.242 | 7,075.432 | 🟩 PureDb 50.81× |
| join | µs/查询 | 7.520 | 10.668 | 14.417 | 🟩 PureDb 1.35× |
| json_scan | µs/查询 | 19.819 | 26.643 | 669.930 | 🟩 PureDb 25.14× |
| jsonb | µs/查询 | 7.793 | 10.800 | 3.923 | 🟥 SQLite 2.75× |
| insert1000 | µs/行 | 38.570 | 35.661 | 66.829 | 🟩 PureDb 1.87× |
| batch10000 | µs/千行事务 | 12,820.960 | 17,894.010 | 7,879.150 | 🟥 SQLite 2.27× |

### 磁盘观察：全机设备范围

| 套件 | 引擎 | 设备 | 设备忙碌率 % | 设备读 MiB | 设备写 MiB |
| --- | --- | --- | --- | --- | --- |
| queries | Optimized | mmcblk0p6 | 2.95 | 12.30 | 7.71 |
| queries | SQLite | mmcblk0p6 | 3.00 | 11.87 | 1.85 |
| writes | Optimized | mmcblk0p6 | 27.90 | 3.00 | 39.94 |
| writes | SQLite | mmcblk0p6 | 54.04 | 0.62 | 140.07 |
| processes | Optimized | mmcblk0p6 | 6.66 | 2.25 | 1.08 |
| processes | SQLite | mmcblk0p6 | 10.41 | 0.05 | 16.63 |

设备计数包括系统服务与延迟回写，不等于引擎独占 I/O。Linux 为三轮套件期间 diskstats 增量；若内核未提供有效计数则不能排名。进程逻辑读写字节、次数、CPU 和关闭后文件列表保留在 raw JSON。

实测程序集 SHA256：`F57A192E23B4467BAEAF12D40B4A5849C3E09E80A8D408120B55E8F517DCC41B`；.NET 10.0.1；整机 8 核；[原始结果](../benchmarks/results/latest/linux)。

## 解释和限制

精确的 SQLite 计划保存在最新结果目录。CPU、内存、耗时没有全面领先；胜负计数不是业务加权总分。Full/Buffered、关闭维护、缓存初建、接口与 SQL 支持范围会影响结果。测量为同版本源码的本地预览版构建；测后空白格式化的方法 IL、签名及类型属性指纹一致，DLL 哈希变化见 [构建一致性记录](../benchmarks/results/optimization/latest/build-equivalence.json)。GitHub 从提交重新构建时，路径和构建元数据也可能改变 DLL 哈希。
