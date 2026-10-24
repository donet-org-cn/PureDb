# 最新基准与复现

只保留 `benchmarks/results/latest`。其中 windows / linux 分别有三轮主样本、隔离内存、ADO、充分预热查询、环境、SQLite 执行计划及设备观察数据。汇总与 README 来自这些原始 JSON，未采用历史版成绩。

## 环境与计时

Windows x64 12 逻辑核、Linux ARM64 8 逻辑核。Linux 测量绑定 CPU 4–7 同规格大核，子进程继承亲和性；报告 CPU 占用仍按整机 8 核折算。运行时和程序集哈希在每平台环境及原始数据中记录。

主套件的串行查询预热至少 500 ms、至少 64 次，计时至少 750 ms；按操作耗时计算三轮中位数。固定工作量的写入、压缩、并发和重开使用各自单位。另有双方访问全部 10 万个主键两遍后测量 50 万次随机 SQL 查询的补充测试。基准进程在同一主机串行运行，轮换引擎次序。操作系统文件缓存未清空。

CPU 是进程及工作子进程 CPU 时间总和。Windows 任一引擎区间 CPU 时间不足 100 ms、Linux 不足 5 ms 时不排名；0 不能解读为零成本。隔离内存进程只打开一个 SQL 库，主键 10 万次，范围/JSON 各 1000 次，GC 后取 RSS；该结果不是峰值，不能代表所有长期负载。

Full / Buffered、缓存、批量与关闭成本、接口及 SQL 能力的条件见 [全面报告](PERFORMANCE.md)。设备计数是全机观察；进程逻辑 I/O 不是物理磁盘流量，也不是 NAND 写放大。未测试真实断电后的持久性。

## 运行

```bash
dotnet build PureDb.slnx -c Release
python scripts/prepare-sqlite.py
# Linux/macOS：将 SQLITE_LIBRARY 设为 prepare-sqlite 输出的绝对路径。
python scripts/test.py
dotnet run -c Release --no-build --project benchmarks/PureDb.QueryBenchmarks -- 100000 1
python scripts/run-benchmarks.py artifacts/new-comparison DATASET --tests --memory --ado
```

DATASET 使用 QueryBenchmarks 打印的目录。也可显式指定新路径并追加 `--create` 创建数据集。正式结果输出目录必须为空，不会覆盖既有测量。Linux 主机若需相同 CPU 限制，在运行命令前加适合本机的 taskset 参数，不能照搬不属于本机的 CPU 编号。

SQLite 官方测试依赖固定为 3.54.1，并校验下载 ZIP 的 SHA256。Windows 使用官方 x64 DLL；Linux CI 从官方 amalgamation 编译共享库。生产项目和 NuGet 包不依赖这些二进制。共享库构建选项不同可能影响测试性能，因此 CI 只验证正确性，不发布其跑分。

结果整理到 `benchmarks/results/latest/windows`、`linux` 后，运行：

```bash
python scripts/make-reports.py
python scripts/make-dashboard.py benchmarks/results/latest/windows docs/PERFORMANCE.html
python scripts/verify-public.py
```

只发布结果 JSON、CSV 和必要的环境/验证信息；不提交数据文件、SSH 信息、API 密钥或历史比较目录。
