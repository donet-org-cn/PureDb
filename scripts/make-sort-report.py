"""Publish one paired sorting experiment; keep its raw samples and version boundaries."""
import argparse
import json
from pathlib import Path
import statistics

root = Path(__file__).resolve().parents[1]
scenarios = {
    'sort_limit_20': '排序取 20 行',
    'sort_offset_100': '降序取 20 行，跳过 100 行',
    'sort_limit_4096': '排序取 4096 行',
    'indexed_range_control': '主键范围取 20 行（对照）',
}


def metrics(result, engine, scenario):
    samples = [s for s in result['Samples'] if s['Engine'] == engine and s['Scenario'] == scenario]
    assert len(samples) == 3 and {s['Round'] for s in samples} == {1, 2, 3}
    return {
        'Us': statistics.median(s['ElapsedMilliseconds'] * 1000 / s['Repetitions'] for s in samples),
        'CpuUs': statistics.median(s['CpuMilliseconds'] * 1000 / s['Repetitions'] for s in samples),
        'AllocatedBytes': statistics.median(s['AllocatedBytes'] / s['Repetitions'] for s in samples),
    }


def winner(pure, sqlite):
    if max(pure, sqlite) / min(pure, sqlite) <= 1.05:
        return '🟨 接近（差距 ≤5%）'
    return f'🟩 PureDb {sqlite / pure:.2f}×' if pure < sqlite else f'🟥 SQLite {pure / sqlite:.2f}×'


def generate():
    parser = argparse.ArgumentParser()
    for name in ['windows-before', 'windows-after', 'linux-before', 'linux-after']:
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    destination = root / 'benchmarks/results/sort/latest'
    destination.mkdir(parents=True, exist_ok=True)
    results = {}
    for name, source in vars(args).items():
        data = json.loads(source.read_text(encoding='utf-8-sig'))
        assert data['SQLiteVersion'] == '3.54.1' and len(data['Samples']) == 24
        assert all(s['RowsPerQuery'] == 100000 and s['WarmupMilliseconds'] == 500 and
                   s['MeasurementMilliseconds'] == 750 and s['ElapsedMilliseconds'] >= 750 for s in data['Samples'])
        results[name] = data
        (destination / (name.replace('_', '-') + '.json')).write_text(
            json.dumps(data, indent=2, ensure_ascii=False) + '\n', encoding='utf-8', newline='\n')
    assert results['windows_before']['CoreSHA256'] == results['linux_before']['CoreSHA256']
    assert results['windows_after']['CoreSHA256'] == results['linux_after']['CoreSHA256']
    summary = {'BeforeCommit': '26ff4dab629bc2c0f8c75eed6871a1434d700c61', 'Platforms': {}}
    lines = ['# 小分页排序：优化前后与 SQLite 对比', '',
             '日期：2026-10-24。PureDb `1.0.0-preview.2` 开发构建，尚未发布。本次只测排序分页及一个索引对照项，不替代已发布预览版的 [完整对比](PERFORMANCE.md)。', '',
             '## 测量方法', '',
             '- 相同的 100,000 行数据：整数主键，未建索引的整数 n（每 13 行一个 NULL）、文本和 BLOB。n 使用固定排列 `(id * 7919) % 100000`。',
             '- 优化前基于提交 `26ff4da`；优化后增加有界堆、纯列延迟投影和表达式候选缓冲区复用。两平台分别运行完全相同的前后 DLL，校验值见下文。',
             '- Release 构建，缓存预热后，每项每引擎预热 500 ms，测量至少 750 ms，三轮交替 PureDb / SQLite 顺序，取归一化指标中位数。每台主机的前后测量串行运行。',
             '- PureDb 复用 SQL 解析缓存，SQLite 复用 prepared statement；双方均生成结果行数组，并核对全部结果。数据创建、JIT 和首次表缓存加载不在热查询时间内。',
             '- 测量连接在语义检查后保留相同的用户函数注册状态；测量 SQL 不调用这些函数。',
             '- CPU 是完成一次查询的进程 CPU 时间，不是占用百分比。分配是查询线程累计申请的托管字节，不是 RSS 或峰值内存；SQLite 内核的原生分配不在该指标内，因此不拿双方托管分配判断总内存胜负。',
             '- 此专项是热缓存只读查询，不测落盘、重开、大库首次读取或新增 SQL 功能。Linux ARM64 使用同一组 4 个大核。', '']
    readme = ['10 万行热缓存排序，三轮中位数；数值越低越好。此表对应主分支 `preview.2` 源码，CPU 是每次查询消耗的时间。', '',
              '| 平台 | 场景 | PureDb 耗时 µs | SQLite 耗时 µs | 耗时谁更低 | PureDb CPU µs | SQLite CPU µs | CPU 谁更低 |',
              '| --- | --- | ---: | ---: | --- | ---: | ---: | --- |']
    for key, platform in [('windows', 'Windows x64'), ('linux', 'Linux ARM64')]:
        before, after = results[key + '_before'], results[key + '_after']
        table = {s: {'Before': metrics(before, 'PureDb', s), 'After': metrics(after, 'PureDb', s),
                     'SQLite': metrics(after, 'SQLite', s)} for s in scenarios}
        summary['Platforms'][platform] = table
        lines += [f'## {platform}', '', f"环境：{after['OS']}；.NET {after['Runtime']}；SQLite {after['SQLiteVersion']}。", '',
                  '| 场景 | 优化前耗时 µs | 优化后耗时 µs | 加速 | 优化前 CPU µs | 优化后 CPU µs | CPU 减少 |',
                  '| --- | ---: | ---: | ---: | ---: | ---: | ---: |']
        for scenario, title in scenarios.items():
            b, a = table[scenario]['Before'], table[scenario]['After']
            lines.append(f"| {title} | {b['Us']:,.3f} | {a['Us']:,.3f} | {b['Us']/a['Us']:.2f}× | {b['CpuUs']:,.3f} | {a['CpuUs']:,.3f} | {(1-a['CpuUs']/b['CpuUs'])*100:.1f}% |")
        lines += ['', '| 场景 | 优化前托管分配 B/次 | 优化后托管分配 B/次 | 分配变化 |', '| --- | ---: | ---: | ---: |']
        for scenario, title in scenarios.items():
            b, a = table[scenario]['Before'], table[scenario]['After']
            change = (1-a['AllocatedBytes']/b['AllocatedBytes'])*100
            delta = '持平' if abs(change)<0.005 else f'减少 {change:.2f}%' if change>0 else f'增加 {-change:.2f}%'
            lines.append(f"| {title} | {b['AllocatedBytes']:,.0f} | {a['AllocatedBytes']:,.0f} | {delta} |")
        lines += ['', '| 场景 | PureDb 耗时 µs | SQLite 耗时 µs | 耗时谁更低 | PureDb CPU µs | SQLite CPU µs | CPU 谁更低 |',
                  '| --- | ---: | ---: | --- | ---: | ---: | --- |']
        for scenario, title in scenarios.items():
            a, s = table[scenario]['After'], table[scenario]['SQLite']
            cells = f"{a['Us']:,.3f} | {s['Us']:,.3f} | {winner(a['Us'], s['Us'])} | {a['CpuUs']:,.3f} | {s['CpuUs']:,.3f} | {winner(a['CpuUs'], s['CpuUs'])}"
            lines.append(f'| {title} | {cells} |')
            readme.append(f'| {platform} | {title} | {cells} |')
        lines += ['', f"原始样本：[优化前](../benchmarks/results/sort/latest/{key}-before.json)、[优化后及同轮 SQLite](../benchmarks/results/sort/latest/{key}-after.json)。", '']
    lines += ['## 本次结论', '']
    for platform, table in summary['Platforms'].items():
        sorts = [v for k, v in table.items() if k != 'indexed_range_control']
        faster = sum(v['SQLite']['Us'] / v['After']['Us'] > 1.05 for v in sorts)
        less_cpu = sum(v['SQLite']['CpuUs'] / v['After']['CpuUs'] > 1.05 for v in sorts)
        speedups = [v['Before']['Us'] / v['After']['Us'] for v in sorts]
        lines += [f'{platform} 的三类排序相对优化前加速 **{min(speedups):.2f}–{max(speedups):.2f}×**；与同轮 SQLite 相比，耗时领先 **{faster}/3** 项，CPU 时间更低 **{less_cpu}/3** 项。逐项的领先、接近及落后均保留在表格中。', '']
    lines += ['三类排序的托管分配均显著低于优化前；此结论只涉及托管分配。当前没有测量本次排序的独立进程 RSS 或 SQLite 原生堆，不能据此认定总内存领先，也不能推导所有 SQL、写入或落盘都已超过 SQLite。', '',
              '## 原理与适用范围', '',
              '原执行器为所有候选行生成投影、排序键和中间对象，再交给 LINQ 排序分页。新路径以最差行作为 PriorityQueue 堆顶，仅保留 K = OFFSET + LIMIT 个候选。投影和排序均为列引用时，直接保留不可变输入行及序号，只为入选行生成结果投影；含计算表达式时，被淘汰候选的两个数组继续作为下一行的工作缓冲区。候选缓冲空间为 O(K)，比较工作为 O(N log K)。最终按出堆逆序生成结果，无需再次排序。', '',
              '此路径只处理输入尚未有序、无分组和 DISTINCT、K 在 1 到 4096 之间的查询。已满足索引排序的查询保留原路径；大分页、分组、去重回退。SQL 比较器和原始行序号共同保留 NULL、混合数值、BLOB、升降序及相等键的稳定语义。', '',
              '含表达式的路径仍为所有行执行投影及排序键求值，别名只复用已求值结果，避免改变易变用户函数的调用次数或隐藏未入选行异常。纯列路径只跳过没有副作用的列复制，列位置仍提前解析和验证。表达式装箱、函数产生的对象、连接输入、表缓存及最终返回数据仍会占用内存，不能把 O(K) 解释为数据库总内存上限。', '',
              '新增 160 条 SQLite 差分查询，覆盖随机排序、NULL、文本、BLOB、多列方向、表达式、别名、序号、JOIN、分组/去重回退和大分页；另测函数异常、易变别名、参数重用、并发缓冲区、稳定顺序及结果 BLOB 所有权。正确性检查进入 Windows / Linux CI，计时不作为 CI 阈值。', '',
              '## 构建与复现', '',
              f"优化前 DLL SHA256：`{results['windows_before']['CoreSHA256']}`。", '',
              f"优化后 DLL SHA256：`{results['windows_after']['CoreSHA256']}`。", '',
              '构建路径与元数据可能影响 DLL 哈希，上述值只标识这次实际测量的二进制。两套测量的全部原始样本保留在同一最新专项记录中。', '',
              '```bash', 'dotnet build PureDb.slnx -c Release', 'python scripts/prepare-sqlite.py',
              'dotnet run --project tests/PureDb.SortTests -c Release --no-build -- --measure', '```', '',
              '报告生成脚本为 `scripts/make-sort-report.py`，输入两平台前后四份程序输出 JSON；`scripts/verify-public.py` 校验样本、中位数与公开表格。', '']
    readme += ['', '托管分配相对优化前的变化、全部四个场景及方法见 [排序专项报告](docs/SORT-PERFORMANCE.md)。分配减少不等于 RSS 降低；此专项不代表全部 SQL 已超过 SQLite。']
    (destination / 'summary.json').write_text(json.dumps(summary, indent=2, ensure_ascii=False) + '\n', encoding='utf-8', newline='\n')
    (root / 'docs/SORT-PERFORMANCE.md').write_text('\n'.join(lines), encoding='utf-8-sig', newline='\n')
    readme_path = root / 'README.md'
    text = readme_path.read_text(encoding='utf-8-sig')
    start, end = '<!-- SORT:START -->', '<!-- SORT:END -->'
    a, b = text.index(start) + len(start), text.index(end)
    readme_path.write_text(text[:a] + '\n\n' + '\n'.join(readme) + '\n\n' + text[b:], encoding='utf-8-sig', newline='\n')


if __name__ == '__main__':
    generate()
