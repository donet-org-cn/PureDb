"""从本轮冻结构建的原始数据生成候选版性能报告，保留全部胜负与测量边界。"""
import hashlib
import json
import math
from pathlib import Path
import statistics

root = Path(__file__).resolve().parents[1]
full = root / "benchmarks/results/candidate/latest"
scale = root / "benchmarks/results/scale/latest"
med = statistics.median
read = lambda p: json.loads(p.read_text(encoding="utf-8-sig"))
platforms = [("windows", "Windows x64"), ("linux", "Linux ARM64")]
labels = {"kv_get": "KV 主键命中", "kv_miss": "KV 主键未命中", "kv_range_20": "KV 范围 20 行", "kv_range_1000": "KV 范围 1000 行", "sql_point": "SQL 主键完整行", "sql_projection": "SQL 主键覆盖投影", "sql_range20": "SQL 范围 20 行", "sql_range1000": "SQL 范围 1000 行", "sql_count": "COUNT(*)", "sql_sum_scan": "SUM 扫描", "sql_group": "分组计数", "sql_join": "INNER JOIN", "sql_left_join": "LEFT JOIN", "json_index": "JSON 表达式索引", "json_scan": "JSON 取余计数", "json_binary_conversion": "JSONB 转换", "parallel_get_8": "八线程 KV 读取", "reopen_and_first_range": "重开并首次范围查询", "cache_limited_2MiB": "2 MiB 页缓存读取", "shared_read": "四进程读取", "shared_update": "四进程原子更新"}
for durability in ["Full", "Buffered"]:
    for name, title in {"single": "逐条新增", "concurrent8": "八线程新增", "batch1000": "批量新增", "batch_unique": "唯一索引批量新增", "batch_update": "批量更新", "batch_delete": "批量删除", "update": "逐条更新", "delete": "逐条删除", "mixed": "更新加五次读取", "compact": "压缩"}.items():
        labels[durability+"_"+name] = durability+" "+title
scale_labels = {"insert_batches_1000": "批量新增全部行", "range20_after_ingest": "导入后范围查询 30 次（含首查）", "checkpoint": "显式检查点", "range20_after_checkpoint": "检查点后范围查询 30 次（含首查）", "range20_hot": "预热后范围查询 10000 次", "update_range_10000": "半开区间更新 10000 行", "delete_range_10000": "半开区间删除 10000 行", "compact": "压缩", "close": "关闭", "reopen_and_first_range": "重开并首次范围查询", "point_10000": "主键查询 10000 次"}


def table(headers, rows):
    return "\n".join(["| "+" | ".join(headers)+" |", "| "+" | ".join("---" for _ in headers)+" |"] + ["| "+" | ".join(map(str, row))+" |" for row in rows])


def winner(a, b):
    if min(a, b) <= 0:
        return "不排名"
    ratio = max(a, b)/min(a, b)
    return "🟨 接近" if ratio < 1.05 else ("🟩 PureDb " if a < b else "🟥 SQLite ")+f"{ratio:.2f}×"


def higher(a, b, unit="MiB"):
    if abs(a-b) < .005:
        return "显示精度内相同"
    percentage = f"，高 {(max(a,b)/min(a,b)-1)*100:.1f}%" if min(a,b) > 0 else ""
    return f"**{'PureDb' if a>b else 'SQLite'} 更高**：多 {abs(a-b):,.2f} {unit}"+percentage


def improvement(before, after):
    return (f"快 {before/after:.2f}×，减少 {(1-after/before)*100:.1f}%" if after < before
            else f"慢 {after/before:.2f}×，增加 {(after/before-1)*100:.1f}%")


summary = {"Version": "1.0.0-rc.1", "SQLite": "3.54.1", "Platforms": {}}
parts = ["# PureDb rc.1 与 SQLite 全面对照", "本报告使用当前候选版冻结内核，重新测量 Windows x64 与 Linux ARM64。数值越低越好，绿色为 PureDb 更快，红色为 SQLite 更快，差距不足 5% 为接近。接近不是统计等价证明。没有隐藏落后项。", "[十万 / 百万行及前后对照](SCALE-PERFORMANCE.md) · [稳定性与正式发布评估](PRODUCTION-READINESS.md) · [SQL 支持清单](SQL-SUPPORT.md)", "## 测量口径", "每平台 41 场景、2 引擎、3 轮，共 246 个主样本；另有 18 个单库独立内存、72 个 ADO 场景样本及 6 个稳态样本。查询预热至少 500 ms、至少 64 次，测量至少 750 ms。顺序轮换引擎，三轮取中位数。没有同时运行其他本轮性能任务。", "KV 对 SQLite SQL 的六项比较、SQL 重复查询缓存、参数绑定与结果物化均保留接口成本。Full 和 Buffered 分列；Buffered 与 SQLite WAL/NORMAL 的断电、检查点语义不同。写入计时不含关闭，完整维护成本另外看规模报告。重开是在同一进程内连续 5 次，不是新进程冷启动。", "CPU 列是完成同样一次工作消耗的 CPU 时间，包含多进程场景的工作子进程。CPU 总区间不足 100 ms 的项目只给原数值，不按粗粒度计数器排名。独立内存表包含托管及原生 RSS，不能用托管分配量代表 SQLite 总内存。", "ARM64 测试盘普通可用空间为 0，root 保留空间仍可写；本轮没有向业务目录写入。该条件会影响落盘耗时，不外推到其他 ARM64 设备。进程 I/O 接口不可用时保留缺测，不伪造零值。"]
scale_parts = ["# PureDb rc.1：十万与百万行、文件大小及优化前后对照", "同一测试宿主分别加载优化前 preview.3 源码构建、当前 rc.1 冻结构建和原版 SQLite 3.54.1；两平台使用同一组托管 DLL。每个规模、每轮、每种引擎使用独立进程与新文件，串行轮换三轮。", "[当前完整 41 项对照](RC-PERFORMANCE.md) · [稳定性与发布评估](PRODUCTION-READINESS.md)", "## 数据与边界", "表结构为 INTEGER 主键、INTEGER 数值及固定 UTF-8 中英文文本。按 1000 行一个事务导入 10 万 / 100 万行；两边页缓存预算均为 64 MiB，PureDb 额外的 SQL 缓存预算为 64 MiB。关闭 PureDb 周期检查点以分开测量显式维护，因此导入数字不等于生产默认自动检查点配置的完整成本。PureDb 为 Buffered，SQLite 为 WAL/NORMAL。", "范围查询分为含首次编译和页面载入的 30 次区间，以及至少 500 ms 预热后的 10000 次稳定查询。热查询中 SQLite 复用 prepared statement，双方物化两列、20 行结果。更新、删除各执行一条半开主键区间语句，保留原有约束及触发器路径。最大实测规模为一百万行，不代表亿级、超内存、TB 级或完整 SQLite 方言。", "每项耗时为表中整个工作量的总毫秒数。RSS 为相同阶段结束时的进程采样，包含先前阶段和 GC 保留页，不能当成该语句独占内存或最大峰值。托管累计分配只用于 PureDb 前后对照；SQLite 的原生分配不计入其中。"]
readme_rows = []
scale_summary = {"Version": "1.0.0-rc.1", "Platforms": {}}
for key, title in platforms:
    directory = full/key
    environment = read(directory/"environment.json")
    reports = [read(file) for file in sorted((directory/"raw").glob("*.json"))]
    assert len(reports) == 18
    samples = [sample for report in reports for sample in report["Samples"]]
    assert len(samples) == 246
    hashes = {report["LibrarySHA256"] for report in reports if report["Engine"] == "Optimized"}
    assert len(hashes) == 1
    core = next(iter(hashes))
    metrics = {}
    for name in dict.fromkeys(sample["Scenario"] for sample in samples):
        metrics[name] = {}
        for engine in ["Optimized", "SQLite"]:
            values = [sample for sample in samples if sample["Scenario"] == name and sample["Engine"] == engine]
            assert len(values) == 3 and {s["Repeat"] for s in values} == {1, 2, 3}
            cpu = lambda s: s["CpuMilliseconds"]+s.get("WorkerCpuMilliseconds", 0)
            metrics[name][engine] = {"Us": med(s["Milliseconds"]*1000/s["Operations"] for s in values), "CpuUs": med(cpu(s)*1000/s["Operations"] for s in values), "CpuMs": med(cpu(s) for s in values), "CpuMachinePercent": med(cpu(s)/s["Milliseconds"]*100/environment["CPUs"] for s in values), "Unit": values[0]["Unit"], "WriteBytes": med(s["WriteBytes"]+s.get("WorkerWriteBytes", 0) for s in values), "ReadBytes": med(s["ReadBytes"]+s.get("WorkerReadBytes", 0) for s in values), "FileBytes": med(s["StorageAfterBytes"] for s in values)}
    assert len(metrics) == 41
    wins = [0, 0, 0]
    for name, engines in metrics.items():
        a, b = engines["Optimized"], engines["SQLite"]
        verdict = winner(a["Us"], b["Us"])
        wins[2 if "接近" in verdict else 0 if a["Us"] < b["Us"] else 1] += 1
    memory = {}
    for scenario in ["point", "range", "json"]:
        memory[scenario] = {}
        for engine in ["Optimized", "SQLite"]:
            values = [read(f) for f in (directory/"memory").glob(engine+"-"+scenario+"-*.json")]
            assert len(values) == 3
            memory[scenario][engine] = {field: med(v[field]/1048576 for v in values) for field in ["WorkingSetBytes", "ManagedLiveBytes", "PeakWorkingSetBytes"]}
    summary["Platforms"][title] = {"MeasuredCoreSHA256": core, "CPUs": environment["CPUs"], "Wins": wins, "Metrics": metrics, "Memory": memory}
    readme_rows.append([title, *wins])
    parts.extend([f"## {title}", f"PureDb 更快 {wins[0]} 项，SQLite 更快 {wins[1]} 项，接近 {wins[2]} 项。不同单位的项目不相加制造一个总性能倍数。", table(["场景", "单位", "PureDb µs", "SQLite µs", "耗时胜方", "PureDb CPU µs", "SQLite CPU µs", "CPU 胜方"], [[labels[name], values["Optimized"]["Unit"], f'{values["Optimized"]["Us"]:,.3f}', f'{values["SQLite"]["Us"]:,.3f}', winner(values["Optimized"]["Us"], values["SQLite"]["Us"]), f'{values["Optimized"]["CpuUs"]:,.3f}', f'{values["SQLite"]["CpuUs"]:,.3f}', winner(values["Optimized"]["CpuUs"], values["SQLite"]["CpuUs"]) if min(values[e]["CpuMs"] for e in values) >= 100 else "短区间，不排名"] for name, values in metrics.items()]), "### 谁的内存更高", table(["单库独立场景", "PureDb RSS MiB", "SQLite RSS MiB", "谁更高、高多少"], [[name, f'{values["Optimized"]["WorkingSetBytes"]:.2f}', f'{values["SQLite"]["WorkingSetBytes"]:.2f}', higher(values["Optimized"]["WorkingSetBytes"], values["SQLite"]["WorkingSetBytes"])] for name, values in memory.items()])])
    point = metrics["sql_point"]
    parts.append(table(["主键查询 CPU 指标", "PureDb", "SQLite", "解释"], [["整机平均 CPU %", f'{point["Optimized"]["CpuMachinePercent"]:.3f}', f'{point["SQLite"]["CpuMachinePercent"]:.3f}', higher(point["Optimized"]["CpuMachinePercent"], point["SQLite"]["CpuMachinePercent"], "个百分点")], ["每次查询 CPU µs", f'{point["Optimized"]["CpuUs"]:.3f}', f'{point["SQLite"]["CpuUs"]:.3f}', higher(point["Optimized"]["CpuUs"], point["SQLite"]["CpuUs"], "µs")]]))
    parts.extend(["### 写入与文件占用", "下表为计时结束时的逻辑进程 I/O 与打开状态的文件总大小；不含关闭后维护，不等于物理写放大或磁盘忙碌率。-1 表示操作系统没有提供进程 I/O 计数。", table(["场景", "PureDb 逻辑写 MiB", "SQLite 逻辑写 MiB", "PureDb 打开时文件 MiB", "SQLite 打开时文件 MiB"], [[labels[name], "未提供" if values["Optimized"]["WriteBytes"] < 0 else f'{values["Optimized"]["WriteBytes"]/1048576:.3f}', "未提供" if values["SQLite"]["WriteBytes"] < 0 else f'{values["SQLite"]["WriteBytes"]/1048576:.3f}', f'{values["Optimized"]["FileBytes"]/1048576:.3f}', f'{values["SQLite"]["FileBytes"]/1048576:.3f}'] for name, values in metrics.items() if name.startswith(("Full_", "Buffered_"))]), f"测量内核 SHA-256：`{core}`。原始记录见 [本平台完整数据](../benchmarks/results/candidate/latest/{key})。"])
    ado_reports = [read(file) for file in (directory/"ado").glob("*.json")]
    ado = {}
    for scenario in dict.fromkeys(sample["Scenario"] for report in ado_reports for sample in report["Samples"]):
        ado[scenario] = {}
        for engine in ["Core", "Ado", "SQLiteNative"]:
            values = [sample for report in ado_reports for sample in report["Samples"] if report["Engine"] == engine and sample["Scenario"] == scenario]
            assert len(values) == 3
            ado[scenario][engine] = {"Us": med(s["Milliseconds"]*1000/s["Operations"] for s in values), "CpuUs": med(s["CpuMilliseconds"]*1000/s["Operations"] for s in values)}
    summary["Platforms"][title]["AdoMetrics"] = ado
    parts.extend(["### ADO.NET 专项", "SQLite 列是同一测试宿主的 SQLite C API 封装，不是 Microsoft.Data.Sqlite 提供程序。", table(["场景", "PureDb Core µs", "PureDb ADO µs", "SQLite C API µs", "ADO 相对 SQLite"], [[name, f'{value["Core"]["Us"]:.3f}', f'{value["Ado"]["Us"]:.3f}', f'{value["SQLiteNative"]["Us"]:.3f}', winner(value["Ado"]["Us"], value["SQLiteNative"]["Us"])] for name, value in ado.items()])])
    steady = {}
    for engine in ["Optimized", "SQLite"]:
        values = [read(file) for file in (directory/"steady").glob(engine+"-*.json")]
        assert len(values) == 3
        steady[engine] = {"Milliseconds": med(v["Milliseconds"] for v in values), "CpuMs": med(v["CpuMilliseconds"] for v in values), "RssMiB": med(v["WorkingSetBytes"]/1048576 for v in values), "ManagedMiB": med(v["ManagedLiveBytes"]/1048576 for v in values)}
    summary["Platforms"][title]["Steady"] = steady
    parts.extend(["### 相同 50 万次主键查询的稳态成本", table(["引擎", "耗时 ms", "CPU ms", "RSS MiB", "托管存活 MiB"], [["PureDb" if name == "Optimized" else "SQLite", *[f'{v[field]:,.3f}' for field in ["Milliseconds", "CpuMs", "RssMiB", "ManagedMiB"]]] for name, v in steady.items()]), "RSS："+higher(steady["Optimized"]["RssMiB"], steady["SQLite"]["RssMiB"])+"。CPU 时间："+higher(steady["Optimized"]["CpuMs"], steady["SQLite"]["CpuMs"], "ms")+"。"])
    sized = {}
    scale_parts.append(f"## {title}")
    for rows in [100000, 1000000]:
        buckets = {stage: [read(p) for p in (scale/key).glob(f"{rows}-{stage}-*.json")] for stage in ["before", "after", "sqlite"]}
        assert all(len(v) == 3 for v in buckets.values())
        assert {r["CoreSHA256"] for r in buckets["after"]} == {core}
        assert all(r["PageCacheBudgetBytes"] == 67108864 for values in buckets.values() for r in values)
        assert len({r["HostSHA256"] for values in buckets.values() for r in values}) == 1
        names = [s["Name"] for s in buckets["after"][0]["Samples"]]
        assert len(names) == 11
        values = {}
        for name in names:
            values[name] = {}
            for stage, reports in buckets.items():
                ss = [next(s for s in report["Samples"] if s["Name"] == name) for report in reports]
                values[name][stage] = {field: med(s[field] for s in ss) for field in ["Milliseconds", "CpuMilliseconds", "AllocatedBytes", "ManagedBytes", "RssBytes", "FileBytes", "Operations"]}
                values[name][stage]["MinMilliseconds"] = min(s["Milliseconds"] for s in ss)
                values[name][stage]["MaxMilliseconds"] = max(s["Milliseconds"] for s in ss)
        size = {stage: {field: med(r[field] for r in reports) for field in ["MainFileBytes", "TotalFileBytes"]} for stage, reports in buckets.items()}
        sized[str(rows)] = {"Metrics": values, "Files": size}
        scale_parts.extend([f"### {rows:,} 行", table(["场景与工作量", "优化前 ms", "当前 ms", "SQLite ms", "当前相对旧版", "当前与 SQLite"], [[scale_labels[name], f'{v["before"]["Milliseconds"]:,.3f}', f'{v["after"]["Milliseconds"]:,.3f}', f'{v["sqlite"]["Milliseconds"]:,.3f}', improvement(v["before"]["Milliseconds"], v["after"]["Milliseconds"]), winner(v["after"]["Milliseconds"], v["sqlite"]["Milliseconds"])] for name, v in values.items()]), "#### CPU 与 RSS：相同工作量、相同阶段", table(["场景", "当前 CPU ms", "SQLite CPU ms", "当前 RSS MiB", "SQLite RSS MiB", "谁的内存更高"], [[scale_labels[name], f'{v["after"]["CpuMilliseconds"]:,.3f}', f'{v["sqlite"]["CpuMilliseconds"]:,.3f}', f'{v["after"]["RssBytes"]/1048576:,.2f}', f'{v["sqlite"]["RssBytes"]/1048576:,.2f}', higher(v["after"]["RssBytes"]/1048576, v["sqlite"]["RssBytes"]/1048576)] for name, v in values.items()]), "#### PureDb 分配变化", table(["场景", "优化前累计托管分配 MiB", "当前累计托管分配 MiB", "前后变化"], [[scale_labels[name], f'{v["before"]["AllocatedBytes"]/1048576:,.3f}', f'{v["after"]["AllocatedBytes"]/1048576:,.3f}', improvement(v["before"]["AllocatedBytes"], v["after"]["AllocatedBytes"])] for name, v in values.items()])])
        comparable_cpu = [(name, value) for name, value in values.items() if min(value[stage]["CpuMilliseconds"] for stage in ["after", "sqlite"]) >= 100]
        scale_parts.extend(["CPU 高低的明确比较（总 CPU 时间至少 100 ms）：", table(["场景", "当前 CPU ms", "SQLite CPU ms", "谁高、高多少"], [[scale_labels[name], f'{v["after"]["CpuMilliseconds"]:.3f}', f'{v["sqlite"]["CpuMilliseconds"]:.3f}', higher(v["after"]["CpuMilliseconds"], v["sqlite"]["CpuMilliseconds"], "ms")] for name, v in comparable_cpu]), "其余短区间 CPU 只展示原值，不用量化误差推断高低。更充分的查询 CPU 对照见完整 41 项表。"])
        combined = {stage: med(sum(next(s["Milliseconds"] for s in r["Samples"] if s["Name"] == name) for name in ["compact", "close"]) for r in reports) for stage, reports in buckets.items()}
        scale_parts.append(f'压缩加关闭的合计中位数：旧版 **{combined["before"]:,.3f} ms**，当前 **{combined["after"]:,.3f} ms**，SQLite **{combined["sqlite"]:,.3f} ms**。当前压缩已包含新主索引落盘，旧版有一部分在关闭阶段完成；不能只比较单独压缩项。')
        scale_parts.append(table(["关闭后文件", "优化前 MiB", "当前 MiB", "SQLite MiB", "当前与 SQLite"], [[label, f'{size["before"][field]/1048576:.3f}', f'{size["after"][field]/1048576:.3f}', f'{size["sqlite"][field]/1048576:.3f}', higher(size["after"][field]/1048576, size["sqlite"][field]/1048576)] for field, label in [("MainFileBytes", "主数据文件"), ("TotalFileBytes", "主文件及全部侧文件")]]))
        scale_parts.append(f'本组新建文件总占用相对旧版减少 **{(1-size["after"]["TotalFileBytes"]/size["before"]["TotalFileBytes"])*100:.1f}%**。主数据日志没有改变编码或压缩，主要收益来自不再生成重复主键索引。已有旧库遗留的主键侧文件不会在打开时自动删除；不能把新建样本收益直接当成所有旧库已缩小。')
    scale_summary["Platforms"][title] = {"Sizes": sized, "AfterSHA256": core, "BeforeSHA256": buckets["before"][0]["CoreSHA256"], "HostSHA256": buckets["after"][0]["HostSHA256"]}

parts.insert(3, "![全部 41 项真实测量](images/rc.1/all-performance.png)\n\n![CPU 成本与 RSS](images/rc.1/cpu-memory.png)")
scale_parts.insert(3, "![关闭后文件总占用](images/rc.1/file-size.png)")
parts.extend(["## 结论", "按上表逐项判断。读取、批量、强制刷盘、并发、CPU 成本、RSS 和文件占用没有共同的单一胜方；规模测试中已经明确保留 SQLite 更好的项。当前候选版没有达到所有指标全面超越 SQLite，也没有提供完整 SQLite 文件协议和 SQL 方言。", "## 复算", "运行 `python scripts/make-candidate-report.py` 从仓库 JSON 复算表格。完整套件见 `scripts/run-benchmarks.py`，规模配对见 `scripts/run-scale-pair.py`；测试用原生 SQLite 仅属于基准宿主，两个产品包保持纯托管。"])
scale_parts.extend(["## 为什么能改善", "1. 主键范围直接读取已有主索引，避免首次全表扫描、排序和重复侧文件；有持久基索引时直接分配最终分页结果，减少多层迭代器和中间 List。", "2. 检查点流式写入、切换基索引并释放增量字典；只排序尚未持久化的增量，已顺序导入时线性检查即可复用顺序。索引头空闲区加入稀疏定位点，不增加文件大小，并继续执行页 CRC。", "3. 半开主键区间同时使用两个端点，避免为了更新或删除一万行先解码近百万行。没有合入绕过 DeleteRow 的直接索引删除补丁；约束、外键和触发器仍走原有执行路径。", "4. 小参数批量命令使用独立紧凑快照，保留 BLOB 复制及参数不可变边界；压缩使用连续数组，大值直接读入复用帧，关闭不重复建立刚完成的检查点。", "本轮没有新增凭名称宣传的 SIMD 优化。继续使用已有的 .NET 硬件 CRC 和向量路径，实际硬件能力保存在原始 JSON 的 Simd 字段；算法减少工作量、分配和重复 I/O 是主要改动。", "## 边界与复算", "三轮给出中位数，原始 JSON 保留各轮值；小幅变化可能是调度、JIT、GC、温度及存储噪声。检查点后含首查的 30 次结果不等于充分预热结果，不能混淆。ARM64 近满盘与缺少进程 I/O 计数的限制同完整报告。", "使用 `dotnet build benchmarks/PureDb.ScaleBenchmarks -c Release` 构建宿主；复制成 before / after 两个目录，仅替换 before 中的 PureDb.dll，再运行 `python scripts/run-scale-pair.py before after output`。JSON 含核心与宿主哈希、版本、缓存预算、运行时、CPU 数和 SIMD 可用性。"])
full.mkdir(parents=True, exist_ok=True)
(full/"summary.json").write_text(json.dumps(summary, indent=2, ensure_ascii=False)+"\n", encoding="utf-8")
(scale/"summary.json").write_text(json.dumps(scale_summary, indent=2, ensure_ascii=False)+"\n", encoding="utf-8")
(root/"docs/RC-PERFORMANCE.md").write_text("\n\n".join(parts)+"\n", encoding="utf-8")
(root/"docs/SCALE-PERFORMANCE.md").write_text("\n\n".join(scale_parts)+"\n", encoding="utf-8")
overview = table(["平台", "PureDb 耗时更低", "SQLite 耗时更低", "接近"], readme_rows)
(full/"readme-overview.md").write_text(overview+"\n", encoding="utf-8")
print(overview)
print("PASS candidate full and scale report arithmetic")
