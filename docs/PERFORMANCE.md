# PureDb 与 SQLite 最新全面对比

PureDb `1.0.0-preview.1` 原生格式，对照原版 SQLite 3.54.1。每平台 41 场景 × 2 引擎 × 3 轮 = 246 主样本，另有 18 个独立内存、72 个 ADO 和 6 个充分预热样本。只保留最新结果。

| 平台 | PureDb 耗时胜 | SQLite 耗时胜 | 接近 |
| --- | --- | --- | --- |
| Windows x64 | 31 | 7 | 3 |
| Linux ARM64 | 25 | 16 | 0 |

主套件串行查询预热至少 500 ms 且至少 64 次，测量至少 750 ms；三轮取中位数，低于 5% 标接近，不是统计等价证明。KV 与 SQL、Core 与 ADO、Full 与 Buffered 分开解释。数据集为 10 万行，缓存与 OS 文件缓存已预热。

PureDb 对原生 SQLite 的 .NET C API 封装，双方实际读取并物化结果；包含托管/原生边界开销。ADO 补充对 SQLite C API，不是对 Microsoft.Data.Sqlite 驱动。每次调用单位见表，批量按行，压缩按次，重开按 opens。

6 项使用 PureDb KV API 对 SQLite SQL：kv_get、kv_miss、kv_range_20、kv_range_1000、parallel_get_8、cache_limited_2MiB。聚合、JOIN 和 JSON 等重复查询可复用按表版本失效的缓存；初建和维护成本可能在计时之外。

Full 是各自强制刷盘路径；Buffered 与 SQLite WAL/NORMAL 的 checkpoint 和断电语义不同。写入计时不含关闭，PureDb 部分索引维护在查询或关闭时执行。重开在同一进程连续执行 5 次，允许有界目录缓存，不代表全新进程冷启动。

[资源占用直观表](RESOURCES.md) · [Windows 交互面板](PERFORMANCE.html) · [复现方法](BENCHMARKS.md)

## Windows x64

| 场景 | 单位 | PureDb µs | SQLite µs | 耗时胜方 |
| --- | --- | --- | --- | --- |
| 四进程读取 · shared_read | queries | 25.435 | 13.656 | 🟥 SQLite 1.86× |
| 四进程原子更新 · shared_update | updates | 208.144 | 113.001 | 🟥 SQLite 1.84× |
| KV 主键命中 · kv_get | queries | 0.293 | 5.640 | 🟩 PureDb 19.24× |
| KV 主键未命中 · kv_miss | queries | 0.050 | 4.785 | 🟩 PureDb 95.76× |
| KV 范围 20 行 · kv_range_20 | queries | 2.569 | 16.346 | 🟩 PureDb 6.36× |
| KV 范围 1000 行 · kv_range_1000 | queries | 102.870 | 515.102 | 🟩 PureDb 5.01× |
| SQL 主键完整行 · sql_point | queries | 0.354 | 5.442 | 🟩 PureDb 15.38× |
| SQL 主键覆盖投影 · sql_projection | queries | 0.271 | 5.076 | 🟩 PureDb 18.71× |
| SQL 范围 20 行 · sql_range20 | queries | 2.771 | 14.653 | 🟩 PureDb 5.29× |
| SQL 范围 1000 行 · sql_range1000 | queries | 117.579 | 492.830 | 🟩 PureDb 4.19× |
| COUNT(*) · sql_count | queries | 0.162 | 17.688 | 🟩 PureDb 109.43× |
| SUM 扫描 · sql_sum_scan | queries | 1,127.206 | 2,905.624 | 🟩 PureDb 2.58× |
| 分组计数 · sql_group | queries | 17.904 | 4,423.797 | 🟩 PureDb 247.08× |
| INNER JOIN · sql_join | queries | 1.159 | 10.213 | 🟩 PureDb 8.81× |
| LEFT JOIN · sql_left_join | queries | 0.996 | 7.073 | 🟩 PureDb 7.10× |
| JSON 表达式索引 · json_index | queries | 2.357 | 20.775 | 🟩 PureDb 8.81× |
| JSON 整数取余计数 · json_scan | queries | 3.641 | 492.273 | 🟩 PureDb 135.20× |
| JSONB 转换提取 · json_binary_conversion | queries | 1.010 | 5.902 | 🟩 PureDb 5.85× |
| 八线程 KV 读取 · parallel_get_8 | queries | 0.197 | 1.941 | 🟩 PureDb 9.86× |
| 重开并首次范围查询 · reopen_and_first_range | opens | 623.820 | 629.320 | 🟨 接近 |
| 2 MiB 页缓存读取 · cache_limited_2MiB | queries | 5.588 | 10.466 | 🟩 PureDb 1.87× |
| Full 逐条新增 · Full_single | rows | 23,598.465 | 25,021.599 | 🟩 PureDb 1.06× |
| Full 八线程新增 · Full_concurrent8 | rows | 5,227.421 | 29,171.001 | 🟩 PureDb 5.58× |
| Full 批量新增 · Full_batch1000 | rows | 37.403 | 34.074 | 🟥 SQLite 1.10× |
| Full 压缩 · Full_compact | compactions | 64,369.200 | 49,307.700 | 🟥 SQLite 1.31× |
| Full 唯一索引批量新增 · Full_batch_unique | rows | 45.511 | 24.989 | 🟥 SQLite 1.82× |
| Full 批量更新 · Full_batch_update | rows | 28.976 | 41.536 | 🟩 PureDb 1.43× |
| Full 批量删除 · Full_batch_delete | rows | 24.862 | 43.214 | 🟩 PureDb 1.74× |
| Full 逐条更新 · Full_update | rows | 25,221.880 | 24,063.731 | 🟨 接近 |
| Full 逐条删除 · Full_delete | rows | 28,646.337 | 21,991.937 | 🟥 SQLite 1.30× |
| Full 一次更新加五次读取 · Full_mixed | write+5reads | 29,624.813 | 28,905.309 | 🟨 接近 |
| Buffered 逐条新增 · Buffered_single | rows | 4.731 | 500.457 | 🟩 PureDb 105.79× |
| Buffered 八线程新增 · Buffered_concurrent8 | rows | 6.823 | 347.367 | 🟩 PureDb 50.91× |
| Buffered 批量新增 · Buffered_batch1000 | rows | 1.216 | 1.714 | 🟩 PureDb 1.41× |
| Buffered 压缩 · Buffered_compact | compactions | 84,967.100 | 26,491.900 | 🟥 SQLite 3.21× |
| Buffered 唯一索引批量新增 · Buffered_batch_unique | rows | 1.504 | 1.629 | 🟩 PureDb 1.08× |
| Buffered 批量更新 · Buffered_batch_update | rows | 1.897 | 24.513 | 🟩 PureDb 12.92× |
| Buffered 批量删除 · Buffered_batch_delete | rows | 1.352 | 22.452 | 🟩 PureDb 16.61× |
| Buffered 逐条更新 · Buffered_update | rows | 5.000 | 340.165 | 🟩 PureDb 68.04× |
| Buffered 逐条删除 · Buffered_delete | rows | 4.254 | 361.727 | 🟩 PureDb 85.03× |
| Buffered 一次更新加五次读取 · Buffered_mixed | write+5reads | 10.819 | 340.735 | 🟩 PureDb 31.49× |

| 场景 | PureDb 文件 MiB | SQLite 文件 MiB |
| --- | --- | --- |
| 四进程读取 | 0.012 | 0.008 |
| 四进程原子更新 | 0.249 | 0.008 |
| KV 主键命中 | 44.808 | 30.863 |
| KV 主键未命中 | 44.808 | 30.863 |
| KV 范围 20 行 | 44.808 | 30.863 |
| KV 范围 1000 行 | 44.808 | 30.863 |
| SQL 主键完整行 | 44.808 | 30.863 |
| SQL 主键覆盖投影 | 44.808 | 30.863 |
| SQL 范围 20 行 | 44.808 | 30.863 |
| SQL 范围 1000 行 | 44.808 | 30.863 |
| COUNT(*) | 44.808 | 30.863 |
| SUM 扫描 | 44.808 | 30.863 |
| 分组计数 | 44.808 | 30.863 |
| INNER JOIN | 44.808 | 30.863 |
| LEFT JOIN | 44.808 | 30.863 |
| JSON 表达式索引 | 44.808 | 30.863 |
| JSON 整数取余计数 | 44.808 | 30.863 |
| JSONB 转换提取 | 44.808 | 30.863 |
| 八线程 KV 读取 | 44.808 | 30.863 |
| 重开并首次范围查询 | 44.808 | 30.863 |
| 2 MiB 页缓存读取 | 44.808 | 30.863 |
| Full 逐条新增 | 0.031 | 0.888 |
| Full 八线程新增 | 0.031 | 1.096 |
| Full 批量新增 | 2.890 | 3.399 |
| Full 压缩 | 1.445 | 4.137 |
| Full 唯一索引批量新增 | 0.289 | 0.361 |
| Full 批量更新 | 5.780 | 7.021 |
| Full 批量删除 | 3.119 | 6.703 |
| Full 逐条更新 | 0.060 | 0.876 |
| Full 逐条删除 | 0.034 | 1.021 |
| Full 一次更新加五次读取 | 0.060 | 0.876 |
| Buffered 逐条新增 | 0.308 | 4.234 |
| Buffered 八线程新增 | 0.308 | 11.919 |
| Buffered 批量新增 | 2.890 | 3.399 |
| Buffered 压缩 | 1.445 | 4.137 |
| Buffered 唯一索引批量新增 | 0.289 | 0.361 |
| Buffered 批量更新 | 5.780 | 7.021 |
| Buffered 批量删除 | 3.119 | 6.703 |
| Buffered 逐条更新 | 0.597 | 4.250 |
| Buffered 逐条删除 | 0.331 | 4.253 |
| Buffered 一次更新加五次读取 | 0.597 | 4.250 |

文件含侧文件，是场景结束时占用而非物理写入字节。查询套件均记录 SQL 文件，KV 行不代表 KV 文件本身体积。进程逻辑 I/O 见原始样本；Linux 定制内核没有 /proc/self/io 时记录 -1，不当成 0。设备计数含全机其他活动，不能归因于数据库进程。

### ADO.NET 补充

| 场景 | 单位 | Core | ADO.NET | SQLite C API | ADO 胜方 |
| --- | --- | --- | --- | --- | --- |
| point | µs/查询 | 0.667 | 1.394 | 5.588 | 🟩 PureDb 4.01× |
| range20 | µs/查询 | 4.005 | 4.892 | 14.450 | 🟩 PureDb 2.95× |
| group | µs/查询 | 25.996 | 44.191 | 4,279.650 | 🟩 PureDb 96.84× |
| join | µs/查询 | 1.981 | 2.829 | 10.863 | 🟩 PureDb 3.84× |
| json_scan | µs/查询 | 5.141 | 5.855 | 485.856 | 🟩 PureDb 82.98× |
| jsonb | µs/查询 | 1.438 | 1.964 | 5.820 | 🟩 PureDb 2.96× |
| insert1000 | µs/行 | 19.557 | 16.072 | 332.505 | 🟩 PureDb 20.69× |
| batch10000 | µs/千行事务 | 5,978.730 | 3,446.630 | 1,798.290 | 🟥 SQLite 1.92× |

### 磁盘观察：全机设备范围

| 引擎 | 全机设备 | 忙碌率 % | 读 MiB/s | 写 MiB/s |
| --- | --- | --- | --- | --- |
| Optimized | 0 d: | 0.05 | 0.00 | 0.00 |
| Optimized | 1 | 93.34 | 0.01 | 2.16 |
| Optimized | 2 c: | 1.09 | 0.04 | 0.20 |
| Optimized | _total | 31.49 | 0.05 | 2.37 |
| SQLite | 0 d: | 0.04 | 0.00 | 0.01 |
| SQLite | 1 | 94.51 | 0.00 | 5.29 |
| SQLite | 2 c: | 1.40 | 0.02 | 0.14 |
| SQLite | _total | 31.98 | 0.02 | 5.45 |

设备计数包括系统服务与延迟回写，不等于引擎独占 I/O。Windows 每引擎一次写入套件补测；Linux 为三轮套件期间 diskstats 增量。进程逻辑读写字节、次数、CPU 和关闭后文件列表保留在 raw JSON。

实测程序集 SHA256：`AD447F6E305574AF2EC459BE2BF4E6B59768E46ED6B8C516961244F103DA10B3`；.NET 10.0.13；整机 12 核；[原始结果](../benchmarks/results/latest/windows)。

## Linux ARM64

| 场景 | 单位 | PureDb µs | SQLite µs | 耗时胜方 |
| --- | --- | --- | --- | --- |
| 四进程读取 · shared_read | queries | 53.665 | 29.495 | 🟥 SQLite 1.82× |
| 四进程原子更新 · shared_update | updates | 319.900 | 54.381 | 🟥 SQLite 5.88× |
| KV 主键命中 · kv_get | queries | 1.100 | 4.408 | 🟩 PureDb 4.01× |
| KV 主键未命中 · kv_miss | queries | 0.116 | 2.298 | 🟩 PureDb 19.89× |
| KV 范围 20 行 · kv_range_20 | queries | 15.124 | 28.745 | 🟩 PureDb 1.90× |
| KV 范围 1000 行 · kv_range_1000 | queries | 284.583 | 983.308 | 🟩 PureDb 3.46× |
| SQL 主键完整行 · sql_point | queries | 0.952 | 3.586 | 🟩 PureDb 3.77× |
| SQL 主键覆盖投影 · sql_projection | queries | 0.703 | 2.929 | 🟩 PureDb 4.17× |
| SQL 范围 20 行 · sql_range20 | queries | 6.542 | 19.422 | 🟩 PureDb 2.97× |
| SQL 范围 1000 行 · sql_range1000 | queries | 343.176 | 935.613 | 🟩 PureDb 2.73× |
| COUNT(*) · sql_count | queries | 0.427 | 20.196 | 🟩 PureDb 47.29× |
| SUM 扫描 · sql_sum_scan | queries | 2,709.271 | 5,096.418 | 🟩 PureDb 1.88× |
| 分组计数 · sql_group | queries | 58.531 | 7,073.029 | 🟩 PureDb 120.84× |
| INNER JOIN · sql_join | queries | 5.565 | 12.184 | 🟩 PureDb 2.19× |
| LEFT JOIN · sql_left_join | queries | 5.558 | 6.808 | 🟩 PureDb 1.22× |
| JSON 表达式索引 · json_index | queries | 5.961 | 30.270 | 🟩 PureDb 5.08× |
| JSON 整数取余计数 · json_scan | queries | 12.621 | 668.860 | 🟩 PureDb 53.00× |
| JSONB 转换提取 · json_binary_conversion | queries | 2.845 | 3.977 | 🟩 PureDb 1.40× |
| 八线程 KV 读取 · parallel_get_8 | queries | 0.857 | 1.398 | 🟩 PureDb 1.63× |
| 重开并首次范围查询 · reopen_and_first_range | opens | 1,263.260 | 387.040 | 🟥 SQLite 3.26× |
| 2 MiB 页缓存读取 · cache_limited_2MiB | queries | 4.181 | 7.140 | 🟩 PureDb 1.71× |
| Full 逐条新增 · Full_single | rows | 2,445.134 | 1,271.681 | 🟥 SQLite 1.92× |
| Full 八线程新增 · Full_concurrent8 | rows | 416.395 | 3,871.924 | 🟩 PureDb 9.30× |
| Full 批量新增 · Full_batch1000 | rows | 37.841 | 16.340 | 🟥 SQLite 2.32× |
| Full 压缩 · Full_compact | compactions | 40,427.400 | 32,326.200 | 🟥 SQLite 1.25× |
| Full 唯一索引批量新增 · Full_batch_unique | rows | 44.319 | 10.312 | 🟥 SQLite 4.30× |
| Full 批量更新 · Full_batch_update | rows | 32.937 | 12.878 | 🟥 SQLite 2.56× |
| Full 批量删除 · Full_batch_delete | rows | 13.077 | 9.523 | 🟥 SQLite 1.37× |
| Full 逐条更新 · Full_update | rows | 819.842 | 964.580 | 🟩 PureDb 1.18× |
| Full 逐条删除 · Full_delete | rows | 1,057.950 | 899.698 | 🟥 SQLite 1.18× |
| Full 一次更新加五次读取 · Full_mixed | write+5reads | 1,530.087 | 1,055.613 | 🟥 SQLite 1.45× |
| Buffered 逐条新增 · Buffered_single | rows | 24.655 | 118.914 | 🟩 PureDb 4.82× |
| Buffered 八线程新增 · Buffered_concurrent8 | rows | 70.834 | 176.701 | 🟩 PureDb 2.49× |
| Buffered 批量新增 · Buffered_batch1000 | rows | 11.059 | 7.665 | 🟥 SQLite 1.44× |
| Buffered 压缩 · Buffered_compact | compactions | 50,740.100 | 18,709.800 | 🟥 SQLite 2.71× |
| Buffered 唯一索引批量新增 · Buffered_batch_unique | rows | 9.228 | 5.795 | 🟥 SQLite 1.59× |
| Buffered 批量更新 · Buffered_batch_update | rows | 13.558 | 11.436 | 🟥 SQLite 1.19× |
| Buffered 批量删除 · Buffered_batch_delete | rows | 9.542 | 7.743 | 🟥 SQLite 1.23× |
| Buffered 逐条更新 · Buffered_update | rows | 33.394 | 82.170 | 🟩 PureDb 2.46× |
| Buffered 逐条删除 · Buffered_delete | rows | 18.706 | 91.498 | 🟩 PureDb 4.89× |
| Buffered 一次更新加五次读取 · Buffered_mixed | write+5reads | 48.216 | 116.801 | 🟩 PureDb 2.42× |

| 场景 | PureDb 文件 MiB | SQLite 文件 MiB |
| --- | --- | --- |
| 四进程读取 | 0.012 | 0.008 |
| 四进程原子更新 | 0.249 | 0.008 |
| KV 主键命中 | 47.894 | 30.863 |
| KV 主键未命中 | 47.894 | 30.863 |
| KV 范围 20 行 | 47.894 | 30.863 |
| KV 范围 1000 行 | 47.894 | 30.863 |
| SQL 主键完整行 | 47.894 | 30.863 |
| SQL 主键覆盖投影 | 47.894 | 30.863 |
| SQL 范围 20 行 | 47.894 | 30.863 |
| SQL 范围 1000 行 | 47.894 | 30.863 |
| COUNT(*) | 47.894 | 30.863 |
| SUM 扫描 | 47.894 | 30.863 |
| 分组计数 | 47.894 | 30.863 |
| INNER JOIN | 47.894 | 30.863 |
| LEFT JOIN | 47.894 | 30.863 |
| JSON 表达式索引 | 47.894 | 30.863 |
| JSON 整数取余计数 | 47.894 | 30.863 |
| JSONB 转换提取 | 47.894 | 30.863 |
| 八线程 KV 读取 | 47.894 | 30.863 |
| 重开并首次范围查询 | 47.894 | 30.863 |
| 2 MiB 页缓存读取 | 47.894 | 30.863 |
| Full 逐条新增 | 0.031 | 0.888 |
| Full 八线程新增 | 0.031 | 1.175 |
| Full 批量新增 | 2.890 | 3.399 |
| Full 压缩 | 1.445 | 4.137 |
| Full 唯一索引批量新增 | 0.289 | 0.361 |
| Full 批量更新 | 5.780 | 7.021 |
| Full 批量删除 | 3.119 | 6.703 |
| Full 逐条更新 | 0.060 | 0.876 |
| Full 逐条删除 | 0.034 | 1.021 |
| Full 一次更新加五次读取 | 0.060 | 0.876 |
| Buffered 逐条新增 | 0.308 | 4.234 |
| Buffered 八线程新增 | 0.308 | 8.207 |
| Buffered 批量新增 | 2.890 | 3.399 |
| Buffered 压缩 | 1.445 | 4.137 |
| Buffered 唯一索引批量新增 | 0.289 | 0.361 |
| Buffered 批量更新 | 5.780 | 7.021 |
| Buffered 批量删除 | 3.119 | 6.703 |
| Buffered 逐条更新 | 0.597 | 4.250 |
| Buffered 逐条删除 | 0.331 | 4.253 |
| Buffered 一次更新加五次读取 | 0.597 | 4.250 |

文件含侧文件，是场景结束时占用而非物理写入字节。查询套件均记录 SQL 文件，KV 行不代表 KV 文件本身体积。进程逻辑 I/O 见原始样本；Linux 定制内核没有 /proc/self/io 时记录 -1，不当成 0。设备计数含全机其他活动，不能归因于数据库进程。

### ADO.NET 补充

| 场景 | 单位 | Core | ADO.NET | SQLite C API | ADO 胜方 |
| --- | --- | --- | --- | --- | --- |
| point | µs/查询 | 2.340 | 6.533 | 3.959 | 🟥 SQLite 1.65× |
| range20 | µs/查询 | 10.076 | 12.153 | 19.044 | 🟩 PureDb 1.57× |
| group | µs/查询 | 79.121 | 153.031 | 7,128.452 | 🟩 PureDb 46.58× |
| join | µs/查询 | 7.704 | 11.251 | 13.893 | 🟩 PureDb 1.23× |
| json_scan | µs/查询 | 21.103 | 25.483 | 670.675 | 🟩 PureDb 26.32× |
| jsonb | µs/查询 | 5.716 | 8.745 | 3.906 | 🟥 SQLite 2.24× |
| insert1000 | µs/行 | 34.627 | 31.937 | 64.530 | 🟩 PureDb 2.02× |
| batch10000 | µs/千行事务 | 14,930.630 | 9,324.160 | 6,233.880 | 🟥 SQLite 1.50× |

### 磁盘观察：全机设备范围

| 套件 | 引擎 | 设备 | 设备忙碌率 % | 设备读 MiB | 设备写 MiB |
| --- | --- | --- | --- | --- | --- |
| queries | Optimized | mmcblk0p6 | 3.15 | 12.08 | 8.28 |
| queries | SQLite | mmcblk0p6 | 3.80 | 14.40 | 1.08 |
| writes | Optimized | mmcblk0p6 | 25.73 | 3.71 | 39.90 |
| writes | SQLite | mmcblk0p6 | 55.01 | 2.41 | 139.75 |
| processes | Optimized | mmcblk0p6 | 4.29 | 0.38 | 0.98 |
| processes | SQLite | mmcblk0p6 | 10.59 | 0.14 | 16.46 |

设备计数包括系统服务与延迟回写，不等于引擎独占 I/O。Windows 每引擎一次写入套件补测；Linux 为三轮套件期间 diskstats 增量。进程逻辑读写字节、次数、CPU 和关闭后文件列表保留在 raw JSON。

实测程序集 SHA256：`AD447F6E305574AF2EC459BE2BF4E6B59768E46ED6B8C516961244F103DA10B3`；.NET 10.0.1；整机 8 核；[原始结果](../benchmarks/results/latest/linux)。

## 解释和限制

精确的 SQLite 计划保存在最新结果目录。CPU、内存、耗时没有全面领先；胜负计数不是业务加权总分。Full/Buffered、关闭维护、缓存初建、接口与 SQL 支持范围会影响结果。测量为同版本源码的本地预览版构建，GitHub 发布流水线会从提交重新构建，构建元数据可能改变 DLL 哈希。
