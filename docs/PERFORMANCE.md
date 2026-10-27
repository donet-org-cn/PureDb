# PureDb 与 SQLite 最新全面对比

当前 rc.1 请查看 [完整性能及 CPU / RSS](RC-PERFORMANCE.md)、[十万 / 百万行及文件大小](SCALE-PERFORMANCE.md)。以下数据冻结于 preview.3，用作历史对照。

PureDb `1.0.0-preview.3` 原生格式，对照原版 SQLite 3.54.1。每平台 41 场景 × 2 引擎 × 3 轮 = 246 主样本，另有 18 个独立内存、72 个 ADO 和 6 个充分预热样本。

| 平台 | PureDb 耗时胜 | SQLite 耗时胜 | 接近 |
| --- | --- | --- | --- |
| Windows x64 | 32 | 8 | 1 |
| Linux ARM64 | 26 | 14 | 1 |

主套件串行查询预热至少 500 ms 且至少 64 次，测量至少 750 ms；三轮取中位数，低于 5% 标接近，不是统计等价证明。KV 与 SQL、Core 与 ADO、Full 与 Buffered 分开解释。数据集为 10 万行，缓存与 OS 文件缓存已预热。

PureDb 对原生 SQLite 的 .NET C API 封装，双方实际读取并物化结果；包含托管/原生边界开销。ADO 补充对 SQLite C API，不是对 Microsoft.Data.Sqlite 驱动。每次调用单位见表，批量按行，压缩按次，重开按 opens。

6 项使用 PureDb KV API 对 SQLite SQL：kv_get、kv_miss、kv_range_20、kv_range_1000、parallel_get_8、cache_limited_2MiB。聚合、JOIN 和 JSON 等重复查询可复用按表版本失效的缓存；初建和维护成本可能在计时之外。

Full 是各自强制刷盘路径；Buffered 与 SQLite WAL/NORMAL 的 checkpoint 和断电语义不同。写入计时不含关闭，PureDb 部分索引维护在查询或关闭时执行。重开在同一进程连续执行 5 次，允许有界目录缓存，不代表全新进程冷启动。

[资源占用直观表](RESOURCES.md) · [Windows 交互面板](PERFORMANCE.html) · [复现方法](BENCHMARKS.md)

## Windows x64

| 场景 | 单位 | PureDb µs | SQLite µs | 耗时胜方 |
| --- | --- | --- | --- | --- |
| 四进程读取 · shared_read | queries | 36.189 | 15.671 | 🟥 SQLite 2.31× |
| 四进程原子更新 · shared_update | updates | 228.206 | 179.760 | 🟥 SQLite 1.27× |
| KV 主键命中 · kv_get | queries | 0.326 | 5.662 | 🟩 PureDb 17.36× |
| KV 主键未命中 · kv_miss | queries | 0.053 | 4.788 | 🟩 PureDb 90.53× |
| KV 范围 20 行 · kv_range_20 | queries | 2.706 | 16.659 | 🟩 PureDb 6.16× |
| KV 范围 1000 行 · kv_range_1000 | queries | 95.322 | 525.776 | 🟩 PureDb 5.52× |
| SQL 主键完整行 · sql_point | queries | 0.323 | 5.304 | 🟩 PureDb 16.44× |
| SQL 主键覆盖投影 · sql_projection | queries | 0.256 | 5.078 | 🟩 PureDb 19.86× |
| SQL 范围 20 行 · sql_range20 | queries | 2.305 | 14.827 | 🟩 PureDb 6.43× |
| SQL 范围 1000 行 · sql_range1000 | queries | 106.697 | 511.499 | 🟩 PureDb 4.79× |
| COUNT(*) · sql_count | queries | 0.195 | 18.204 | 🟩 PureDb 93.59× |
| SUM 扫描 · sql_sum_scan | queries | 1,254.796 | 3,222.164 | 🟩 PureDb 2.57× |
| 分组计数 · sql_group | queries | 19.665 | 4,673.675 | 🟩 PureDb 237.66× |
| INNER JOIN · sql_join | queries | 0.951 | 10.562 | 🟩 PureDb 11.11× |
| LEFT JOIN · sql_left_join | queries | 0.778 | 7.474 | 🟩 PureDb 9.60× |
| JSON 表达式索引 · json_index | queries | 2.069 | 21.854 | 🟩 PureDb 10.56× |
| JSON 整数取余计数 · json_scan | queries | 4.784 | 515.405 | 🟩 PureDb 107.74× |
| JSONB 转换提取 · json_binary_conversion | queries | 0.846 | 6.165 | 🟩 PureDb 7.29× |
| 八线程 KV 读取 · parallel_get_8 | queries | 0.189 | 2.061 | 🟩 PureDb 10.90× |
| 重开并首次范围查询 · reopen_and_first_range | opens | 721.320 | 637.020 | 🟥 SQLite 1.13× |
| 2 MiB 页缓存读取 · cache_limited_2MiB | queries | 5.840 | 11.041 | 🟩 PureDb 1.89× |
| Full 逐条新增 · Full_single | rows | 26,346.146 | 31,760.512 | 🟩 PureDb 1.21× |
| Full 八线程新增 · Full_concurrent8 | rows | 7,644.061 | 36,475.911 | 🟩 PureDb 4.77× |
| Full 批量新增 · Full_batch1000 | rows | 42.384 | 57.303 | 🟩 PureDb 1.35× |
| Full 压缩 · Full_compact | compactions | 91,024.800 | 81,425.700 | 🟥 SQLite 1.12× |
| Full 唯一索引批量新增 · Full_batch_unique | rows | 56.382 | 91.377 | 🟩 PureDb 1.62× |
| Full 批量更新 · Full_batch_update | rows | 38.616 | 54.922 | 🟩 PureDb 1.42× |
| Full 批量删除 · Full_batch_delete | rows | 36.581 | 59.050 | 🟩 PureDb 1.61× |
| Full 逐条更新 · Full_update | rows | 24,950.604 | 30,655.058 | 🟩 PureDb 1.23× |
| Full 逐条删除 · Full_delete | rows | 25,818.259 | 24,983.782 | 🟨 接近 |
| Full 一次更新加五次读取 · Full_mixed | write+5reads | 26,908.626 | 24,654.002 | 🟥 SQLite 1.09× |
| Buffered 逐条新增 · Buffered_single | rows | 4.964 | 459.113 | 🟩 PureDb 92.49× |
| Buffered 八线程新增 · Buffered_concurrent8 | rows | 8.060 | 491.507 | 🟩 PureDb 60.98× |
| Buffered 批量新增 · Buffered_batch1000 | rows | 2.146 | 1.761 | 🟥 SQLite 1.22× |
| Buffered 压缩 · Buffered_compact | compactions | 91,242.500 | 31,659.200 | 🟥 SQLite 2.88× |
| Buffered 唯一索引批量新增 · Buffered_batch_unique | rows | 1.930 | 1.829 | 🟥 SQLite 1.05× |
| Buffered 批量更新 · Buffered_batch_update | rows | 2.442 | 25.321 | 🟩 PureDb 10.37× |
| Buffered 批量删除 · Buffered_batch_delete | rows | 1.387 | 23.193 | 🟩 PureDb 16.72× |
| Buffered 逐条更新 · Buffered_update | rows | 5.934 | 413.941 | 🟩 PureDb 69.75× |
| Buffered 逐条删除 · Buffered_delete | rows | 4.846 | 478.587 | 🟩 PureDb 98.75× |
| Buffered 一次更新加五次读取 · Buffered_mixed | write+5reads | 11.379 | 448.906 | 🟩 PureDb 39.45× |

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
| Full 八线程新增 | 0.031 | 1.179 |
| Full 批量新增 | 2.890 | 3.399 |
| Full 压缩 | 1.445 | 4.137 |
| Full 唯一索引批量新增 | 0.290 | 0.361 |
| Full 批量更新 | 5.780 | 7.021 |
| Full 批量删除 | 3.119 | 6.703 |
| Full 逐条更新 | 0.060 | 0.876 |
| Full 逐条删除 | 0.034 | 1.021 |
| Full 一次更新加五次读取 | 0.060 | 0.876 |
| Buffered 逐条新增 | 0.309 | 4.234 |
| Buffered 八线程新增 | 0.309 | 11.943 |
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
| point | µs/查询 | 0.514 | 1.287 | 5.520 | 🟩 PureDb 4.29× |
| range20 | µs/查询 | 3.305 | 3.938 | 14.307 | 🟩 PureDb 3.63× |
| group | µs/查询 | 27.754 | 44.639 | 4,355.551 | 🟩 PureDb 97.57× |
| join | µs/查询 | 1.595 | 2.698 | 10.814 | 🟩 PureDb 4.01× |
| json_scan | µs/查询 | 4.909 | 6.208 | 499.320 | 🟩 PureDb 80.43× |
| jsonb | µs/查询 | 1.256 | 1.729 | 5.796 | 🟩 PureDb 3.35× |
| insert1000 | µs/行 | 19.058 | 17.401 | 499.423 | 🟩 PureDb 28.70× |
| batch10000 | µs/千行事务 | 4,685.680 | 4,379.690 | 1,686.490 | 🟥 SQLite 2.60× |

### 磁盘观察：全机设备范围

| 引擎 | 全机设备 | 忙碌率 % | 读 MiB/s | 写 MiB/s |
| --- | --- | --- | --- | --- |
| Optimized | 0 d: | 0.00 | 0.00 | 0.00 |
| Optimized | 1 | 91.20 | 0.00 | 2.18 |
| Optimized | 2 c: | 1.35 | 0.01 | 0.15 |
| Optimized | _total | 30.85 | 0.01 | 2.33 |
| SQLite | 0 d: | 0.00 | 0.00 | 0.00 |
| SQLite | 1 | 90.74 | 0.00 | 4.98 |
| SQLite | 2 c: | 1.44 | 0.01 | 0.19 |
| SQLite | _total | 30.73 | 0.02 | 5.17 |

Windows 物理设备数据为每引擎一次独立写入套件补测，不是三轮主样本的 I/O，也不纳入主成绩。

设备计数包括系统服务与延迟回写，不等于引擎独占 I/O。Linux 为三轮套件期间 diskstats 增量；若内核未提供有效计数则不能排名。进程逻辑读写字节、次数、CPU 和关闭后文件列表保留在 raw JSON。

实测程序集 SHA256：`30C7DB3221A39E08DF491CB88D85EB04E6ABD226990A2C89C7E6A916E62CC510`；.NET 10.0.13；整机 12 核；[原始结果](../benchmarks/results/latest/windows)。

## Linux ARM64

| 场景 | 单位 | PureDb µs | SQLite µs | 耗时胜方 |
| --- | --- | --- | --- | --- |
| 四进程读取 · shared_read | queries | 92.305 | 36.853 | 🟥 SQLite 2.50× |
| 四进程原子更新 · shared_update | updates | 265.254 | 59.532 | 🟥 SQLite 4.46× |
| KV 主键命中 · kv_get | queries | 1.246 | 4.543 | 🟩 PureDb 3.65× |
| KV 主键未命中 · kv_miss | queries | 0.119 | 2.375 | 🟩 PureDb 19.91× |
| KV 范围 20 行 · kv_range_20 | queries | 13.825 | 30.046 | 🟩 PureDb 2.17× |
| KV 范围 1000 行 · kv_range_1000 | queries | 229.862 | 1,046.166 | 🟩 PureDb 4.55× |
| SQL 主键完整行 · sql_point | queries | 0.887 | 3.537 | 🟩 PureDb 3.99× |
| SQL 主键覆盖投影 · sql_projection | queries | 0.648 | 3.030 | 🟩 PureDb 4.68× |
| SQL 范围 20 行 · sql_range20 | queries | 5.186 | 20.521 | 🟩 PureDb 3.96× |
| SQL 范围 1000 行 · sql_range1000 | queries | 258.556 | 1,000.178 | 🟩 PureDb 3.87× |
| COUNT(*) · sql_count | queries | 0.500 | 20.628 | 🟩 PureDb 41.23× |
| SUM 扫描 · sql_sum_scan | queries | 2,465.234 | 5,357.262 | 🟩 PureDb 2.17× |
| 分组计数 · sql_group | queries | 59.683 | 7,400.288 | 🟩 PureDb 123.99× |
| INNER JOIN · sql_join | queries | 5.431 | 12.535 | 🟩 PureDb 2.31× |
| LEFT JOIN · sql_left_join | queries | 4.889 | 7.018 | 🟩 PureDb 1.44× |
| JSON 表达式索引 · json_index | queries | 5.156 | 31.085 | 🟩 PureDb 6.03× |
| JSON 整数取余计数 · json_scan | queries | 13.442 | 684.088 | 🟩 PureDb 50.89× |
| JSONB 转换提取 · json_binary_conversion | queries | 2.599 | 4.072 | 🟩 PureDb 1.57× |
| 八线程 KV 读取 · parallel_get_8 | queries | 0.651 | 1.779 | 🟩 PureDb 2.73× |
| 重开并首次范围查询 · reopen_and_first_range | opens | 647.320 | 506.560 | 🟥 SQLite 1.28× |
| 2 MiB 页缓存读取 · cache_limited_2MiB | queries | 4.391 | 6.093 | 🟩 PureDb 1.39× |
| Full 逐条新增 · Full_single | rows | 1,011.368 | 871.288 | 🟥 SQLite 1.16× |
| Full 八线程新增 · Full_concurrent8 | rows | 254.038 | 2,102.165 | 🟩 PureDb 8.28× |
| Full 批量新增 · Full_batch1000 | rows | 17.696 | 9.516 | 🟥 SQLite 1.86× |
| Full 压缩 · Full_compact | compactions | 30,531.400 | 26,478.200 | 🟥 SQLite 1.15× |
| Full 唯一索引批量新增 · Full_batch_unique | rows | 21.649 | 8.437 | 🟥 SQLite 2.57× |
| Full 批量更新 · Full_batch_update | rows | 19.960 | 16.874 | 🟥 SQLite 1.18× |
| Full 批量删除 · Full_batch_delete | rows | 10.210 | 7.404 | 🟥 SQLite 1.38× |
| Full 逐条更新 · Full_update | rows | 1,070.350 | 1,165.722 | 🟩 PureDb 1.09× |
| Full 逐条删除 · Full_delete | rows | 912.815 | 1,084.716 | 🟩 PureDb 1.19× |
| Full 一次更新加五次读取 · Full_mixed | write+5reads | 1,356.961 | 1,175.545 | 🟥 SQLite 1.15× |
| Buffered 逐条新增 · Buffered_single | rows | 41.018 | 100.856 | 🟩 PureDb 2.46× |
| Buffered 八线程新增 · Buffered_concurrent8 | rows | 36.868 | 107.203 | 🟩 PureDb 2.91× |
| Buffered 批量新增 · Buffered_batch1000 | rows | 12.141 | 3.331 | 🟥 SQLite 3.64× |
| Buffered 压缩 · Buffered_compact | compactions | 57,012.700 | 11,694.700 | 🟥 SQLite 4.88× |
| Buffered 唯一索引批量新增 · Buffered_batch_unique | rows | 11.689 | 2.493 | 🟥 SQLite 4.69× |
| Buffered 批量更新 · Buffered_batch_update | rows | 11.529 | 8.298 | 🟥 SQLite 1.39× |
| Buffered 批量删除 · Buffered_batch_delete | rows | 5.156 | 5.146 | 🟨 接近 |
| Buffered 逐条更新 · Buffered_update | rows | 16.655 | 83.793 | 🟩 PureDb 5.03× |
| Buffered 逐条删除 · Buffered_delete | rows | 15.357 | 70.438 | 🟩 PureDb 4.59× |
| Buffered 一次更新加五次读取 · Buffered_mixed | write+5reads | 37.091 | 135.212 | 🟩 PureDb 3.65× |

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
| Buffered 八线程新增 | 0.309 | 11.432 |
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
| point | µs/查询 | 2.485 | 9.153 | 3.861 | 🟥 SQLite 2.37× |
| range20 | µs/查询 | 8.321 | 13.302 | 20.378 | 🟩 PureDb 1.53× |
| group | µs/查询 | 80.627 | 132.416 | 7,425.859 | 🟩 PureDb 56.08× |
| join | µs/查询 | 7.570 | 9.915 | 14.058 | 🟩 PureDb 1.42× |
| json_scan | µs/查询 | 17.649 | 20.544 | 685.577 | 🟩 PureDb 33.37× |
| jsonb | µs/查询 | 6.098 | 10.672 | 4.049 | 🟥 SQLite 2.64× |
| insert1000 | µs/行 | 32.192 | 32.734 | 62.720 | 🟩 PureDb 1.92× |
| batch10000 | µs/千行事务 | 10,460.260 | 10,675.490 | 2,972.800 | 🟥 SQLite 3.59× |

### 磁盘观察：全机设备范围

| 套件 | 引擎 | 设备 | 设备忙碌率 % | 设备读 MiB | 设备写 MiB |
| --- | --- | --- | --- | --- | --- |
| queries | Optimized | mmcblk0p6 | 6.57 | 0.00 | 23.51 |
| queries | SQLite | mmcblk0p6 | 6.21 | 0.00 | 16.40 |
| writes | Optimized | mmcblk0p6 | 32.18 | 0.00 | 41.63 |
| writes | SQLite | mmcblk0p6 | 70.78 | 0.00 | 141.25 |
| processes | Optimized | mmcblk0p6 | 10.54 | 0.00 | 3.05 |
| processes | SQLite | mmcblk0p6 | 17.38 | 0.00 | 17.52 |

设备计数包括系统服务与延迟回写，不等于引擎独占 I/O。Linux 为三轮套件期间 diskstats 增量；若内核未提供有效计数则不能排名。进程逻辑读写字节、次数、CPU 和关闭后文件列表保留在 raw JSON。

实测程序集 SHA256：`30C7DB3221A39E08DF491CB88D85EB04E6ABD226990A2C89C7E6A916E62CC510`；.NET 10.0.13；整机 8 核；[原始结果](../benchmarks/results/latest/linux)。

## 解释和限制

精确的 SQLite 计划保存在最新结果目录。CPU、内存、耗时没有全面领先；胜负计数不是业务加权总分。Full/Buffered、关闭维护、缓存初建、接口与 SQL 支持范围会影响结果。测量为冻结源码的本地发布构建；两平台使用同一份实测 DLL。GitHub 从提交重新构建时，路径和构建元数据可能改变 DLL 哈希。

本轮 ARM 测试盘接近满，df 普通可用空间为 0，root 保留块仍可写，测试未发生空间不足失败；写入与设备指标不代表空闲磁盘。硬件状态与复现边界见 [测量方法](BENCHMARKS.md)。
