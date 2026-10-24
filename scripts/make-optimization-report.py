"""Compare the current paired experiment, using preserved baseline and final raw JSON."""
import json, statistics
from pathlib import Path

root = Path(__file__).resolve().parents[1]
base = root / 'benchmarks/results/optimization/latest'
final = root / 'benchmarks/results/latest'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
median = statistics.median
summary = read(final / 'summary.json')


def table(headers, rows):
    return '\n'.join(['| ' + ' | '.join(headers) + ' |', '| ' + ' | '.join(['---'] * len(headers)) + ' |'] +
                     ['| ' + ' | '.join(map(str, row)) + ' |' for row in rows])


def metrics(path):
    reports = [read(f) for f in (path / 'raw').glob('*.json')]
    assert len(reports) == 18
    groups = {}
    for report in reports:
        for sample in report['Samples']:
            groups.setdefault((sample['Scenario'], sample['Engine']), []).append(sample)
    values = {}
    for (scenario, engine), samples in groups.items():
        assert len(samples) == 3
        values.setdefault(scenario, {})[engine] = {
            'Us': median(s['Milliseconds'] * 1000 / s['Operations'] for s in samples),
            'CpuUs': median((s['CpuMilliseconds'] + s.get('WorkerCpuMilliseconds', 0)) * 1000 / s['Operations'] for s in samples),
            'CpuMs': median(s['CpuMilliseconds'] + s.get('WorkerCpuMilliseconds', 0) for s in samples),
            'AllocatedBytes': median(s['AllocatedBytes'] / s['Operations'] for s in samples),
            'WorkerReadBytes': median(s.get('WorkerReadBytes', -1) for s in samples),
        }
    hashes = {r['LibrarySHA256'] for r in reports if r['Engine'] == 'Optimized'}
    assert len(hashes) == 1
    return hashes.pop(), values


def verdict(a, b):
    if max(a, b) / min(a, b) < 1.05:
        return '🟨 接近'
    return f'🟩 PureDb {b / a:.2f}×' if a < b else f'🟥 SQLite {a / b:.2f}×'


lines = ['# 本轮优化前后及 SQLite 对照',
         '2026-10-24，主分支 `1.0.0-preview.2` 开发构建，尚未发布至 NuGet。优化前基于 `804f6ef`，已含上一轮 SQL 扩展；优化后增加 CHECK、单分支 UPSERT，以及本页所述性能优化。两平台使用同一组前后 DLL，各场景三轮中位数；同一台机器串行测量，轮换引擎次序。保留本轮一组前后证据，旧全面对比已替换。',
         '[完整 41 场景与 ADO.NET](PERFORMANCE.md) · [CPU / RSS 谁更高](RESOURCES.md) · [排序专项](SORT-PERFORMANCE.md) · [SQL 支持清单](SQL-SUPPORT.md)',
         '## 改了什么，为什么可能更快',
         '1. Shared 日志追赶：已确认同一代文件并持有进程锁时，从本地提交偏移读取真实新增尾部。使用独立随机读句柄及池化缓冲区，避免为几十字节更新重新预读 64 KiB 文件头。CRC、帧及操作边界逐项验证；异常协调戳、文件换代或锁遗弃仍走完整恢复。没有减少刷盘或合并原本独立的事务。',
         '2. 非索引小分页排序：基线已有有界堆，本轮进一步以紧凑二叉堆取代纯列 PriorityQueue，候选只存一份行引用和序号。首批自底向上建堆，整数 / NULL 内联比较；仅对经过 OFFSET / LIMIT 的最终结果复制投影与 BLOB。计算表达式继续逐行求值，保留业务函数和异常语义。',
         '3. JSON 内存：完整解析树使用弱引用，源文本仍缓存时也可被 GC 回收；保留两个常用标量路径，交替查字段时避免重复解析。再次遍历且树已回收时会重新解析，因此是保留空间与重解析成本的取舍。JSONB 返回值仍复制，防止修改结果污染缓存。',
         '4. 新增 CHECK 和 UPSERT：CHECK 在统一 Store 入口检查，事务内复用已绑定委托，函数注册变化后按语句失效。UPSERT 复用唯一键映射，冲突后进入标准 UPDATE 流程，避免逐行整表扫描并保留外键、触发器和原子撤销。',
         '本轮没有新增 SIMD 指令路径。现有 CRC 使用 ARM CRC32 硬件指令，指定整数 JSON 计数路径使用可用的向量指令；这些已存在于基线，不能冒充本轮 SIMD 收益。排序的比较、分支和共享锁成本不适合直接用 SIMD 替代。',
         '## 测量边界',
         '计时完成后仅执行代码空白格式化。格式化构建与实测构建的方法 IL、签名和类型属性指纹一致；DLL 本身因调试及构建元数据而不同，具体 SHA256 和校验结果见 [构建一致性记录](../benchmarks/results/optimization/latest/build-equivalence.json)。报告保留实际计时 DLL 的哈希，不将格式化构建伪装成重新测量。',
         '同机前后差值包含调度、JIT、GC、页缓存、温度和存储状态变化，三轮中位数不是因果或统计显著性证明；小于 5% 只标为接近。下列 CPU 是完成同样单位工作消耗的时间，RSS 是独立进程单库 GC 后驻留量，累计托管分配不是 RSS。SQLite 的原生堆不计入托管分配。Full / Buffered、关闭维护、缓存初建和接口差异详见全面报告。']
output = {'BeforeCommit': '804f6ef35d5d9c9a486db890b688b137e9bd9ad8', 'Platforms': {}}
before_hashes, after_hashes = set(), set()
for key, platform in [('windows', 'Windows x64'), ('linux', 'Linux ARM64')]:
    bpath, apath = base / key / 'before', final / key
    bh, before = metrics(bpath)
    ah, after = metrics(apath)
    assert ah == summary['Platforms'][platform]['MeasuredCoreSHA256']
    before_hashes.add(bh); after_hashes.add(ah)
    output['Platforms'][platform] = {'BeforeSHA256': bh, 'AfterSHA256': ah, 'Before': before, 'After': after}
    lines += ['## ' + platform, '### 全部主场景：前后耗时及当前 SQLite',
              table(['场景', '优化前 PureDb µs', '优化后 PureDb µs', '前后倍率', 'SQLite µs', '当前胜方'],
                    [[name, f'{before[name]["Optimized"]["Us"]:,.3f}', f'{v["Optimized"]["Us"]:,.3f}',
                      f'{before[name]["Optimized"]["Us"] / v["Optimized"]["Us"]:.2f}×', f'{v["SQLite"]["Us"]:,.3f}',
                      verdict(v['Optimized']['Us'], v['SQLite']['Us'])] for name, v in after.items()]),
              '单位沿用全面报告：普通查询为 µs/查询，批量为 µs/行，压缩为 µs/次，重开为 µs/open。前后倍率大于 1 表示变快，小于 1 表示退化。',
              '### 全部主场景：每单位 CPU 成本',
              table(['场景', '优化前 CPU µs', '优化后 CPU µs', 'SQLite CPU µs', '当前 CPU 胜方'],
                    [[name, f'{before[name]["Optimized"]["CpuUs"]:,.3f}', f'{v["Optimized"]["CpuUs"]:,.3f}',
                      f'{v["SQLite"]["CpuUs"]:,.3f}', verdict(v['Optimized']['CpuUs'], v['SQLite']['CpuUs'])
                      if min(v[e]['CpuMs'] for e in v) >= (100 if key == 'windows' else 5) else '短区间，不排名'] for name, v in after.items()])]
    memrows = []
    for scenario in ['point', 'range', 'json']:
        def mem(path, engine, field):
            return median(read(f)[field] / 1048576 for f in (path / 'memory').glob(f'{engine}-{scenario}-*.json'))
        b, a, s = mem(bpath, 'Optimized', 'WorkingSetBytes'), mem(apath, 'Optimized', 'WorkingSetBytes'), mem(apath, 'SQLite', 'WorkingSetBytes')
        high = f'**{"PureDb" if a > s else "SQLite"} 更高**：多 {abs(a - s):.2f} MiB，高 {(max(a, s) / min(a, s) - 1) * 100:.1f}%'
        memrows.append([scenario, f'{b:.2f}', f'{a:.2f}', f'{s:.2f}', high,
                        f'{mem(bpath,"Optimized","ManagedLiveBytes"):.2f} → {mem(apath,"Optimized","ManagedLiveBytes"):.2f}'])
    lines += ['### 内存：RSS 与托管存活量', table(['场景', '优化前 RSS MiB', '优化后 RSS MiB', 'SQLite RSS MiB', '当前谁更高', 'PureDb 托管存活 MiB'], memrows)]
    name = 'shared_update'
    b, a = before[name]['Optimized']['WorkerReadBytes'], after[name]['Optimized']['WorkerReadBytes']
    if min(a, b) >= 0:
        lines += [f'Shared 更新工作进程逻辑读取字节三轮中位数：{b:,.0f} → {a:,.0f}，减少 {(1 - a / b) * 100:.1f}%。这是操作系统进程逻辑 I/O，不是物理盘读取，也不等于相同比例的耗时降低。']
    else:
        lines += ['该 Linux 内核不提供有效的进程逻辑 I/O 字节，不能从 -1 推断为零；不发布该指标的前后降低比例。']
    losses = sorted(((v['Optimized']['Us'] / v['SQLite']['Us'], n, v) for n, v in after.items()
                     if v['Optimized']['Us'] / v['SQLite']['Us'] >= 1.05), reverse=True)
    lines += ['### 当前仍落后的项目（按差距排列）',
              table(['场景', 'PureDb µs', 'SQLite µs', 'SQLite 倍率'],
                    [[n, f'{v["Optimized"]["Us"]:,.3f}', f'{v["SQLite"]["Us"]:,.3f}', f'{ratio:.2f}×'] for ratio, n, v in losses]),
              f'优化前 DLL：`{bh}`；优化后 DLL：`{ah}`。本轮 [前置样本](../benchmarks/results/optimization/latest/{key}/before) 与 [最终样本](../benchmarks/results/latest/{key}) 均保留实际构建哈希。']
assert len(before_hashes) == len(after_hashes) == 1
lines += ['## 结论与后续重点',
          '性能、CPU、RSS 没有全面超过 SQLite。应分别看排序分配、Shared 逻辑读取减少及实际耗时；没有收益和出现退化的场景也完整保留。托管运行时、行对象缓存及辅助索引占用仍是 RSS 成本，减少 JSON 树存活不能保证进程驻留量按相同幅度下降。',
          '仍需处理 Shared 锁竞争与 SQL 状态刷新、批量唯一键和完整持久化路径、压缩复制成本、重开索引目录成本及行缓存对象开销。复合索引仍是按版本惰性内存构建，尚未实现分页增量维护；相关子查询、视图、CTE 与窗口仍有文档列明的物化和优化边界。',
          '恢复验证包括每个字节位置截断、完整坏帧拒绝、大帧和缓冲区边界、已提交前缀缩短、四进程原子更新、压缩代次变化及强杀；Windows 和 ARM64 执行相同 DLL，Linux x64 由 CI 验证。详见 [批准的 Shared 方案](SHARED-REPLAY-PLAN.md) 及 [开发验证](DEVELOPMENT-VALIDATION.md)。']
(base / 'summary.json').write_text(json.dumps(output, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
(root / 'docs/OPTIMIZATION.md').write_text('\n\n'.join(lines) + '\n', encoding='utf-8-sig', newline='\n')
print('Generated paired optimization report from two platforms, 984 main samples and 72 memory samples.')
