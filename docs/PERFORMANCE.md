# PureDb 与 SQLite 最新全面对比

PureDb `1.0.0-preview.2` 原生格式，对照原版 SQLite 3.54.1。每平台 41 场景 × 2 引擎 × 3 轮 = 246 主样本，另有 18 个独立内存、72 个 ADO 和 6 个充分预热样本。只保留最新结果。

| 平台 | PureDb 耗时胜 | SQLite 耗时胜 | 接近 |
| --- | --- | --- | --- |
| Windows x64 | 34 | 6 | 1 |
| Linux ARM64 | 25 | 16 | 0 |

主套件串行查询预热至少 500 ms 且至少 64 次，测量至少 750 ms；三轮取中位数，低于 5% 标接近，不是统计等价证明。KV 与 SQL、Core 与 ADO、Full 与 Buffered 分开解释。数据集为 10 万行，缓存与 OS 文件缓存已预热。

PureDb 对原生 SQLite 的 .NET C API 封装，双方实际读取并物化结果；包含托管/原生边界开销。ADO 补充对 SQLite C API，不是对 Microsoft.Data.Sqlite 驱动。每次调用单位见表，批量按行，压缩按次，重开按 opens。

6 项使用 PureDb KV API 对 SQLite SQL：kv_get、kv_miss、kv_range_20、kv_range_1000、parallel_get_8、cache_limited_2MiB。聚合、JOIN 和 JSON 等重复查询可复用按表版本失效的缓存；初建和维护成本可能在计时之外。

Full 是各自强制刷盘路径；Buffered 与 SQLite WAL/NORMAL 的 checkpoint 和断电语义不同。写入计时不含关闭，PureDb 部分索引维护在查询或关闭时执行。重开在同一进程连续执行 5 次，允许有界目录缓存，不代表全新进程冷启动。

[资源占用直观表](RESOURCES.md) · [Windows 交互面板](PERFORMANCE.html) · [复现方法](BENCHMARKS.md)

## Windows x64

| 场景 | 单位 | PureDb µs | SQLite µs | 耗时胜方 |
| --- | --- | --- | --- | --- |
| 四进程读取 · shared_read | queries | 31.869 | 15.548 | 🟥 SQLite 2.05× |
| 四进程原子更新 · shared_update | updates | 206.377 | 108.771 | 🟥 SQLite 1.90× |
| KV 主键命中 · kv_get | queries | 0.303 | 5.560 | 🟩 PureDb 18.36× |
| KV 主键未命中 · kv_miss | queries | 0.051 | 4.666 | 🟩 PureDb 91.29× |
| KV 范围 20 行 · kv_range_20 | queries | 2.555 | 16.818 | 🟩 PureDb 6.58× |
| KV 范围 1000 行 · kv_range_1000 | queries | 107.185 | 516.391 | 🟩 PureDb 4.82× |
| SQL 主键完整行 · sql_point | queries | 0.399 | 5.227 | 🟩 PureDb 13.11× |
| SQL 主键覆盖投影 · sql_projection | queries | 0.307 | 5.004 | 🟩 PureDb 16.31× |
| SQL 范围 20 行 · sql_range20 | queries | 3.143 | 14.465 | 🟩 PureDb 4.60× |
| SQL 范围 1000 行 · sql_range1000 | queries | 126.425 | 498.480 | 🟩 PureDb 3.94× |
| COUNT(*) · sql_count | queries | 0.177 | 17.734 | 🟩 PureDb 100.10× |
| SUM 扫描 · sql_sum_scan | queries | 1,158.071 | 3,028.795 | 🟩 PureDb 2.62× |
| 分组计数 · sql_group | queries | 18.214 | 4,405.454 | 🟩 PureDb 241.88× |
| INNER JOIN · sql_join | queries | 1.246 | 10.198 | 🟩 PureDb 8.19× |
| LEFT JOIN · sql_left_join | queries | 1.058 | 7.085 | 🟩 PureDb 6.70× |
| JSON 表达式索引 · json_index | queries | 2.353 | 21.730 | 🟩 PureDb 9.24× |
| JSON 整数取余计数 · json_scan | queries | 4.335 | 509.308 | 🟩 PureDb 117.49× |
| JSONB 转换提取 · json_binary_conversion | queries | 1.001 | 5.760 | 🟩 PureDb 5.76× |
| 八线程 KV 读取 · parallel_get_8 | queries | 0.184 | 1.944 | 🟩 PureDb 10.55× |
| 重开并首次范围查询 · reopen_and_first_range | opens | 698.760 | 642.960 | 🟥 SQLite 1.09× |
| 2 MiB 页缓存读取 · cache_limited_2MiB | queries | 5.543 | 10.629 | 🟩 PureDb 1.92× |
| Full 逐条新增 · Full_single | rows | 18,341.519 | 15,602.437 | 🟥 SQLite 1.18× |
| Full 八线程新增 · Full_concurrent8 | rows | 4,555.714 | 24,558.607 | 🟩 PureDb 5.39× |
| Full 批量新增 · Full_batch1000 | rows | 23.231 | 30.431 | 🟩 PureDb 1.31× |
| Full 压缩 · Full_compact | compactions | 51,528.700 | 61,588.200 | 🟩 PureDb 1.20× |
| Full 唯一索引批量新增 · Full_batch_unique | rows | 41.909 | 17.582 | 🟥 SQLite 2.38× |
| Full 批量更新 · Full_batch_update | rows | 21.898 | 45.006 | 🟩 PureDb 2.06× |
| Full 批量删除 · Full_batch_delete | rows | 17.140 | 38.168 | 🟩 PureDb 2.23× |
| Full 逐条更新 · Full_update | rows | 19,950.198 | 21,077.293 | 🟩 PureDb 1.06× |
| Full 逐条删除 · Full_delete | rows | 16,723.946 | 15,977.901 | 🟨 接近 |
| Full 一次更新加五次读取 · Full_mixed | write+5reads | 16,892.167 | 17,827.075 | 🟩 PureDb 1.06× |
| Buffered 逐条新增 · Buffered_single | rows | 4.949 | 267.278 | 🟩 PureDb 54.01× |
| Buffered 八线程新增 · Buffered_concurrent8 | rows | 8.032 | 293.776 | 🟩 PureDb 36.58× |
| Buffered 批量新增 · Buffered_batch1000 | rows | 1.352 | 1.669 | 🟩 PureDb 1.23× |
| Buffered 压缩 · Buffered_compact | compactions | 64,954.100 | 20,108.700 | 🟥 SQLite 3.23× |
| Buffered 唯一索引批量新增 · Buffered_batch_unique | rows | 1.518 | 1.641 | 🟩 PureDb 1.08× |
| Buffered 批量更新 · Buffered_batch_update | rows | 1.814 | 17.560 | 🟩 PureDb 9.68× |
| Buffered 批量删除 · Buffered_batch_delete | rows | 1.999 | 16.245 | 🟩 PureDb 8.13× |
| Buffered 逐条更新 · Buffered_update | rows | 5.028 | 323.438 | 🟩 PureDb 64.32× |
| Buffered 逐条删除 · Buffered_delete | rows | 4.783 | 370.326 | 🟩 PureDb 77.42× |
| Buffered 一次更新加五次读取 · Buffered_mixed | write+5reads | 12.764 | 373.716 | 🟩 PureDb 29.28× |

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
| Full 八线程新增 | 0.031 | 1.202 |
| Full 批量新增 | 2.890 | 3.399 |
| Full 压缩 | 1.445 | 4.137 |
| Full 唯一索引批量新增 | 0.290 | 0.361 |
| Full 批量更新 | 5.780 | 7.021 |
| Full 批量删除 | 3.119 | 6.703 |
| Full 逐条更新 | 0.060 | 0.876 |
| Full 逐条删除 | 0.034 | 1.021 |
| Full 一次更新加五次读取 | 0.060 | 0.876 |
| Buffered 逐条新增 | 0.309 | 4.234 |
| Buffered 八线程新增 | 0.309 | 11.962 |
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
| point | µs/查询 | 0.720 | 1.477 | 5.564 | 🟩 PureDb 3.77× |
| range20 | µs/查询 | 4.240 | 4.929 | 14.216 | 🟩 PureDb 2.88× |
| group | µs/查询 | 28.510 | 45.553 | 4,494.023 | 🟩 PureDb 98.66× |
| join | µs/查询 | 2.049 | 3.052 | 10.760 | 🟩 PureDb 3.53× |
| json_scan | µs/查询 | 5.198 | 6.079 | 498.733 | 🟩 PureDb 82.04× |
| jsonb | µs/查询 | 1.411 | 1.957 | 5.655 | 🟩 PureDb 2.89× |
| insert1000 | µs/行 | 19.820 | 17.566 | 299.830 | 🟩 PureDb 17.07× |
| batch10000 | µs/千行事务 | 4,687.560 | 4,249.620 | 1,691.550 | 🟥 SQLite 2.51× |

### 磁盘观察：全机设备范围

| 引擎 | 全机设备 | 忙碌率 % | 读 MiB/s | 写 MiB/s |
| --- | --- | --- | --- | --- |
| Optimized | 0 d: | 0.09 | 0.07 | 0.01 |
| Optimized | 1 | 87.77 | 0.01 | 3.01 |
| Optimized | 2 c: | 1.40 | 0.01 | 0.17 |
| Optimized | _total | 29.16 | 0.08 | 3.19 |
| SQLite | 0 d: | 0.01 | 0.00 | 0.00 |
| SQLite | 1 | 90.18 | 0.00 | 5.65 |
| SQLite | 2 c: | 1.71 | 0.09 | 0.27 |
| SQLite | _total | 29.14 | 0.09 | 5.93 |

Windows 物理设备数据为每引擎一次独立写入套件补测，不是三轮主样本的 I/O，也不纳入主成绩。

设备计数包括系统服务与延迟回写，不等于引擎独占 I/O。Linux 为三轮套件期间 diskstats 增量；若内核未提供有效计数则不能排名。进程逻辑读写字节、次数、CPU 和关闭后文件列表保留在 raw JSON。

实测程序集 SHA256：`A240817D6F98A3264CB24AD474A870BC1AACA56A9466411CAB12387EB7EB04F5`；.NET 10.0.13；整机 12 核；[原始结果](../benchmarks/results/latest/windows)。

## Linux ARM64

| 场景 | 单位 | PureDb µs | SQLite µs | 耗时胜方 |
| --- | --- | --- | --- | --- |
| 四进程读取 · shared_read | queries | 59.753 | 33.189 | 🟥 SQLite 1.80× |
| 四进程原子更新 · shared_update | updates | 332.048 | 51.106 | 🟥 SQLite 6.50× |
| KV 主键命中 · kv_get | queries | 1.100 | 4.370 | 🟩 PureDb 3.97× |
| KV 主键未命中 · kv_miss | queries | 0.116 | 2.348 | 🟩 PureDb 20.19× |
| KV 范围 20 行 · kv_range_20 | queries | 15.040 | 28.812 | 🟩 PureDb 1.92× |
| KV 范围 1000 行 · kv_range_1000 | queries | 247.915 | 986.151 | 🟩 PureDb 3.98× |
| SQL 主键完整行 · sql_point | queries | 1.095 | 3.561 | 🟩 PureDb 3.25× |
| SQL 主键覆盖投影 · sql_projection | queries | 0.743 | 3.030 | 🟩 PureDb 4.08× |
| SQL 范围 20 行 · sql_range20 | queries | 6.608 | 19.494 | 🟩 PureDb 2.95× |
| SQL 范围 1000 行 · sql_range1000 | queries | 343.648 | 950.521 | 🟩 PureDb 2.77× |
| COUNT(*) · sql_count | queries | 0.456 | 19.935 | 🟩 PureDb 43.70× |
| SUM 扫描 · sql_sum_scan | queries | 2,710.722 | 5,069.989 | 🟩 PureDb 1.87× |
| 分组计数 · sql_group | queries | 57.109 | 7,139.051 | 🟩 PureDb 125.01× |
| INNER JOIN · sql_join | queries | 6.313 | 12.466 | 🟩 PureDb 1.97× |
| LEFT JOIN · sql_left_join | queries | 5.667 | 6.935 | 🟩 PureDb 1.22× |
| JSON 表达式索引 · json_index | queries | 5.840 | 30.493 | 🟩 PureDb 5.22× |
| JSON 整数取余计数 · json_scan | queries | 12.927 | 672.055 | 🟩 PureDb 51.99× |
| JSONB 转换提取 · json_binary_conversion | queries | 3.002 | 4.081 | 🟩 PureDb 1.36× |
| 八线程 KV 读取 · parallel_get_8 | queries | 0.631 | 1.439 | 🟩 PureDb 2.28× |
| 重开并首次范围查询 · reopen_and_first_range | opens | 655.900 | 422.380 | 🟥 SQLite 1.55× |
| 2 MiB 页缓存读取 · cache_limited_2MiB | queries | 4.067 | 6.948 | 🟩 PureDb 1.71× |
| Full 逐条新增 · Full_single | rows | 2,628.761 | 1,091.539 | 🟥 SQLite 2.41× |
| Full 八线程新增 · Full_concurrent8 | rows | 407.376 | 2,262.825 | 🟩 PureDb 5.55× |
| Full 批量新增 · Full_batch1000 | rows | 32.761 | 20.767 | 🟥 SQLite 1.58× |
| Full 压缩 · Full_compact | compactions | 77,412.300 | 32,217.900 | 🟥 SQLite 2.40× |
| Full 唯一索引批量新增 · Full_batch_unique | rows | 42.621 | 13.772 | 🟥 SQLite 3.09× |
| Full 批量更新 · Full_batch_update | rows | 37.544 | 11.724 | 🟥 SQLite 3.20× |
| Full 批量删除 · Full_batch_delete | rows | 18.984 | 10.698 | 🟥 SQLite 1.77× |
| Full 逐条更新 · Full_update | rows | 1,072.193 | 983.539 | 🟥 SQLite 1.09× |
| Full 逐条删除 · Full_delete | rows | 1,829.338 | 1,094.972 | 🟥 SQLite 1.67× |
| Full 一次更新加五次读取 · Full_mixed | write+5reads | 1,917.556 | 974.903 | 🟥 SQLite 1.97× |
| Buffered 逐条新增 · Buffered_single | rows | 60.266 | 74.201 | 🟩 PureDb 1.23× |
| Buffered 八线程新增 · Buffered_concurrent8 | rows | 73.339 | 127.674 | 🟩 PureDb 1.74× |
| Buffered 批量新增 · Buffered_batch1000 | rows | 14.027 | 7.498 | 🟥 SQLite 1.87× |
| Buffered 压缩 · Buffered_compact | compactions | 67,674.000 | 23,799.100 | 🟥 SQLite 2.84× |
| Buffered 唯一索引批量新增 · Buffered_batch_unique | rows | 10.796 | 7.718 | 🟥 SQLite 1.40× |
| Buffered 批量更新 · Buffered_batch_update | rows | 12.880 | 9.619 | 🟥 SQLite 1.34× |
| Buffered 批量删除 · Buffered_batch_delete | rows | 5.711 | 8.796 | 🟩 PureDb 1.54× |
| Buffered 逐条更新 · Buffered_update | rows | 38.093 | 104.427 | 🟩 PureDb 2.74× |
| Buffered 逐条删除 · Buffered_delete | rows | 15.938 | 123.488 | 🟩 PureDb 7.75× |
| Buffered 一次更新加五次读取 · Buffered_mixed | write+5reads | 53.577 | 123.767 | 🟩 PureDb 2.31× |

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
| Full 八线程新增 | 0.031 | 1.131 |
| Full 批量新增 | 2.890 | 3.399 |
| Full 压缩 | 1.445 | 4.137 |
| Full 唯一索引批量新增 | 0.290 | 0.361 |
| Full 批量更新 | 5.780 | 7.021 |
| Full 批量删除 | 3.119 | 6.703 |
| Full 逐条更新 | 0.060 | 0.876 |
| Full 逐条删除 | 0.034 | 1.021 |
| Full 一次更新加五次读取 | 0.060 | 0.876 |
| Buffered 逐条新增 | 0.309 | 4.234 |
| Buffered 八线程新增 | 0.309 | 9.401 |
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
| point | µs/查询 | 2.296 | 11.917 | 3.936 | 🟥 SQLite 3.03× |
| range20 | µs/查询 | 9.762 | 22.580 | 19.303 | 🟥 SQLite 1.17× |
| group | µs/查询 | 80.638 | 137.150 | 7,070.224 | 🟩 PureDb 51.55× |
| join | µs/查询 | 7.957 | 12.566 | 13.806 | 🟩 PureDb 1.10× |
| json_scan | µs/查询 | 16.698 | 20.919 | 669.935 | 🟩 PureDb 32.03× |
| jsonb | µs/查询 | 6.516 | 9.824 | 3.850 | 🟥 SQLite 2.55× |
| insert1000 | µs/行 | 32.540 | 32.779 | 78.373 | 🟩 PureDb 2.39× |
| batch10000 | µs/千行事务 | 10,292.700 | 12,995.650 | 7,802.050 | 🟥 SQLite 1.67× |

### 磁盘观察：全机设备范围

| 套件 | 引擎 | 设备 | 设备忙碌率 % | 设备读 MiB | 设备写 MiB |
| --- | --- | --- | --- | --- | --- |
| queries | Optimized | mmcblk0p6 | 0.63 | 0.07 | 8.09 |
| queries | SQLite | mmcblk0p6 | 0.37 | 0.06 | 1.46 |
| writes | Optimized | mmcblk0p6 | 25.65 | 0.02 | 40.01 |
| writes | SQLite | mmcblk0p6 | 52.65 | 0.03 | 139.93 |
| processes | Optimized | mmcblk0p6 | 3.87 | 0.00 | 1.10 |
| processes | SQLite | mmcblk0p6 | 7.53 | 0.00 | 16.64 |

设备计数包括系统服务与延迟回写，不等于引擎独占 I/O。Linux 为三轮套件期间 diskstats 增量；若内核未提供有效计数则不能排名。进程逻辑读写字节、次数、CPU 和关闭后文件列表保留在 raw JSON。

实测程序集 SHA256：`A240817D6F98A3264CB24AD474A870BC1AACA56A9466411CAB12387EB7EB04F5`；.NET 10.0.1；整机 8 核；[原始结果](../benchmarks/results/latest/linux)。

## 解释和限制

精确的 SQLite 计划保存在最新结果目录。CPU、内存、耗时没有全面领先；胜负计数不是业务加权总分。Full/Buffered、关闭维护、缓存初建、接口与 SQL 支持范围会影响结果。测量为冻结源码的本地发布构建；两平台使用同一份实测 DLL。GitHub 从提交重新构建时，路径和构建元数据可能改变 DLL 哈希。
