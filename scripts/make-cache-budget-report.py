"""Generate the same-host cache-budget before/after report from all paired samples."""
import json, statistics
from pathlib import Path

root = Path(__file__).resolve().parents[1]
base = root / 'benchmarks/results/cache-budget/latest'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
lines = ['# BLOB / JSON 表缓存：优化前后配对验证',
         '本轮发现的主要瓶颈是缓存容量估算过高：10 万行、五列、256 字节 BLOB 和 JSON 的表，在默认 64 MiB 行缓存预算下被拒绝缓存，导致重复解码整表。修复按实际表示估算容量，保持默认预算。此报告专门验证这一阈值问题；巨大倍数不能解释为所有 SQL 的普遍加速。',
         '同一台机器、同一自包含 JIT 测试宿主、.NET 10.0.13、相同数据和查询，仅替换 PureDb.dll。优化前为 main 提交 21571bcf875303db7002f82f36a0eaa96f712940，优化后为本次 preview.2 冻结构建。三轮交替先后顺序；每条查询预热 500 ms，计时至少 750 ms。中位数先按各轮操作数归一化，再取三轮中位数。',
         '本表仅比较 PureDb 优化前后。与 SQLite 的四种运行方式对照见 [AOT 完整报告](AOT-PERFORMANCE.md)，更广的 41 项测试见 [全面报告](PERFORMANCE.md)。本表采用各平台的 AOT 对照宿主所附 JIT DLL，Linux 在本机构建导致哈希与全面报告复制的 Windows DLL 不同；下方记录各自真实哈希，不将它们冒充同一二进制。缓存首次构建不计入热查询；首次打开和首次查询另在 AOT 报告测量。',
         '行缓存估算计入行数组、装箱数值、UTF-16 文本、BLOB 视图、源编码及后备数据；辅助索引不在同一预算内，因此 64 MiB 不是进程 RSS 上限。保留缓存可以增加常驻内存，本次不声称此修复降低 RSS。回归测试覆盖缓存复用、写后失效、预算为零时不保留缓存。']
summary = {'BeforeCommit': '21571bcf875303db7002f82f36a0eaa96f712940', 'Platforms': {}}
for key, platform in [('windows', 'Windows x64'), ('linux', 'Linux ARM64')]:
    p = base / key
    environment = read(p / 'environment.json')
    reports = [(f.stem.split('-')[0], read(f)) for f in sorted((p / 'raw').glob('*.json'))]
    assert len(reports) == 6
    metrics = {}
    for stage, report in reports:
        assert report['DynamicCodeSupported'] and report['Runtime'] == '.NET 10.0.13'
        for s in report['Samples']:
            metrics.setdefault(s['Scenario'], {}).setdefault(stage, []).append(s)
    rows = []
    for name, groups in metrics.items():
        for stage, samples in groups.items():
            assert len(samples) == 3
            groups[stage] = {'Us': statistics.median(s['Milliseconds'] * 1000 / s['Operations'] for s in samples),
                             'CpuUs': statistics.median(s['CpuMilliseconds'] * 1000 / s['Operations'] for s in samples),
                             'AllocatedBytes': statistics.median(s['AllocatedBytes'] / s['Operations'] for s in samples)}
        before, after = groups['before'], groups['after']
        ratio = before['Us'] / after['Us']
        outcome = '接近' if 0.95 < ratio < 1.05 else f'优化后快 {ratio:,.2f}×' if ratio > 1 else f'优化后慢 {1/ratio:.2f}×'
        rows.append([name, *[f"{groups[st][field]:,.3f}" for field in ['Us', 'CpuUs'] for st in ['before', 'after']],
                     *[f"{groups[st]['AllocatedBytes']:,.0f}" for st in ['before', 'after']], outcome])
    lines += ['## ' + platform,
              '| 场景 | 优化前 µs | 优化后 µs | 前 CPU µs | 后 CPU µs | 前托管 B/查询 | 后托管 B/查询 | 耗时结论 |\n'
              '| --- | --- | --- | --- | --- | --- | --- | --- |\n' + '\n'.join('| ' + ' | '.join(r) + ' |' for r in rows),
              f"优化前 SHA256：`{environment['BeforeCoreSHA256']}`；优化后：`{environment['AfterCoreSHA256']}`。",
              f'[六份原始报告和宿主标识](../benchmarks/results/cache-budget/latest/{key})。']
    summary['Platforms'][platform] = {'Environment': environment, 'Metrics': metrics}
lines += ['## 结论与适用范围',
          'JSON 索引查询不再为每次调用重新解码整表。巨大收益来自跨过不合理的缓存拒绝阈值；原本已命中缓存的简单主键查询收益小得多，也可能有测量波动。超过预算的更大表仍需要流式执行或持久化页级表达式索引，目前不能假定同样倍率可延续到任意数据库规模。AOT 没有自动消除首次构建和大表常驻内存成本，相关启动、RSS 及 SQLite 优势均保留在 AOT 报告中。',
          '复现：`scripts/run-cache-budget-pair.py <published> <before-PureDb.dll> <dataset> <fresh-output>`；使用同一个发布宿主，脚本复制旧 DLL 建立对照，并检查宿主 SHA256 相同。']
(base / 'summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
(root / 'docs/CACHE-BUDGET-PERFORMANCE.md').write_text('\n\n'.join(lines) + '\n', encoding='utf-8-sig', newline='\n')
print('Generated cache budget report: 84 paired samples on Windows x64 and Linux ARM64.')
