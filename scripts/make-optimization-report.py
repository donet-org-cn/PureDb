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
         '2026-10-27，`1.0.0-preview.3` 本轮发布构建。优化前为已发布 preview.2（`09cdfff`）。同一平台使用完全相同的测试宿主，仅替换 PureDb.dll，三轮串行轮换旧版、新版和 SQLite，采用各场景中位数；两平台使用相同的一组内核 DLL。',
         '[完整 41 场景与 ADO.NET](PERFORMANCE.md) · [CPU / RSS 谁更高](RESOURCES.md) · [兼容性范围](COMPATIBILITY-REPORT.md) · [博客园文章与性能图](CNBLOGS-preview.3.md)',
         '## 改了什么，为什么可能更快',
         '1. 计划键：对已缓存 AST 使用对象身份及表/模式上下文，避免每次命中缓存时递归计算整棵不可变语法树哈希。仍按模式和函数版本失效；没有缓存业务查询最终结果。',
         '2. 解码：复用 0–1023 的不可变 long 装箱；全列顺序投影直接顺序解码，避免逐字段查找和回填。修改、重复列、重排列和 BLOB 复制仍走正确分支。',
         '3. JSON 计数：弱引用缓存不可变的规范化表达式，避免反复重建表达式树；派生列按数据版本失效，参数值和最终计数没有被该弱缓存固化。原有 SIMD 逐次计算继续执行。',
         '4. 检查点：复用已排序的基础索引，将增量排序后归并，避免重新排序全部主键。表计数更新采用单次字典定位；日志 CRC、边界与持久化顺序不变。',
         '密集 KV 主键的热点页增加有界数组目录，减少并发字典散列；按已有记录数和预算限制容量，稀疏高位键保留字典路径。编码作用域只在编码实际变化时写线程状态；跨编码嵌套调用仍恢复原值。并发初始化、边界键、写后失效和压缩重开均验证。',
         '5. SQLite 文件模式：新增持久模式、页尺寸及编码；完整镜像回退也只备份实际改变/截断的旧页，减少日志写放大。镜像构建仍随数据库规模增长，单独测试，不能算作原版内核的性能。',
         '本轮没有新增 SIMD 指令路径；已有 ARM CRC32 和 JSON 向量计数已存在于基线。文本排序、CAST 和 BLOB 默认值的改动用于语义兼容，不能全部归因于性能收益。',
         '## 测量边界',
         '固定 19 项读取/重开场景的吞吐倍率用每项前后中位数比值的几何平均；耗时下降为 1−1/倍率。同时列出全部 41 场景、CPU、RSS 及退化项，不把 19 项收益声称为所有增删改都改善。',
         '同机差值包含调度、JIT、GC、页缓存、温度和存储变化；三轮不是统计显著性证明。小于 5% 标为接近。首次缓存构建、关闭维护、Full/Buffered 语义与接口成本见全面报告。']
lines += ['每轮只执行旧版、新版和 SQLite 三种引擎；优化前后报告共用当轮 SQLite 对照，复制其 JSON 便于分别汇总。两平台合计 738 次主场景执行；984 是含重复 SQLite 对照的证据记录数，不是独立执行次数。']
output = {'BeforeCommit': '09cdfffbfb4ab6f10ba373ac6e46745049397cc2', 'Platforms': {}}
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
        lines += [f'沿用上一轮 Shared 协议，本轮工作进程逻辑读取字节三轮中位数：{b:,.0f} → {a:,.0f}，减少 {(1 - a / b) * 100:.1f}%。这是操作系统进程逻辑 I/O，不是物理盘读取，也不等于相同比例的耗时降低。']
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
