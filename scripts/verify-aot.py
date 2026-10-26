"""Check deployment size accounting, actual AOT execution and all measured medians."""
import json, statistics
from pathlib import Path

root = Path(__file__).resolve().parents[1]
base = root / 'benchmarks/results/aot/latest'
read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
summary = read(base / 'summary.json')
current = read(root / 'benchmarks/results/latest/summary.json')
for key, platform in [('windows', 'Windows x64'), ('linux', 'Linux ARM64')]:
    p = base / key
    manifest = read(p / 'environment.json')['Manifest']
    assert manifest['SDK'] == '10.0.402' and manifest['RuntimeVersion'] == '10.0.13'
    assert len(manifest['Variants']) == 6
    for v in manifest['Variants']:
        assert v['AllPublishedBytes'] == sum(f['Bytes'] for f in v['Files'])
        assert v['RuntimeBytes'] == sum(f['Bytes'] for f in v['Files'] if not f['DebugOrDocumentation'])
        assert v['ExecutableBytes'] == next(f['Bytes'] for f in v['Files'] if f['Path'] == v['Executable'])
        assert all(len(f['SHA256']) == 64 for f in v['Files'])
        if v['Engine'] == 'PureDb':
            assert v['SQLiteLibrary'] is None
            assert not any('sqlite' in f['Path'].lower() for f in v['Files'])
        else:
            assert not any(f['Path'] == 'PureDb.dll' for f in v['Files'])
            assert v['SQLiteLibrary'] is not None
    validation = read(p / 'validation.json')
    assert validation['FeatureGroups'] == 7 and validation['DifferentialQueries'] == 230
    assert validation['CompilerWarnings'] == 0 and 'dynamic code=False' in validation['FeatureTranscript']
    assert 'PASS 230' in validation['DifferentialTranscript']
    groups = {}
    files = list((p / 'raw').glob('*.json'))
    assert len(files) == 24
    for f in files:
        r = read(f)
        assert r['DynamicCodeSupported'] == ('-jit-' in f.name)
        mode = 'jit' if r['DynamicCodeSupported'] else 'aot'
        assert r['Runtime'] == '.NET 10.0.13'
        for s in r['Samples']:
            assert s['Milliseconds'] > 0 and s['Operations'] > 0
            groups.setdefault((s['Scenario'], r['Engine'], mode), []).append((r['Round'], s))
    assert len(groups) == 60
    for (name, engine, mode), samples in groups.items():
        assert {r for r, s in samples} == {1, 2, 3} and len(samples) == 3
        expected = summary['Platforms'][platform]['Metrics'][name][engine][mode]
        for field, expression in [('Us', lambda s: s['Milliseconds'] * 1000 / s['Operations']),
                                  ('CpuUs', lambda s: s['CpuMilliseconds'] * 1000 / s['Operations']),
                                  ('AllocatedBytes', lambda s: s['AllocatedBytes'] / s['Operations'])]:
            assert abs(statistics.median(expression(s) for r, s in samples) - expected[field]) < 1e-8
    assert len(list((p / 'memory').glob('*.json'))) == 36
    for name, engines in summary['Platforms'][platform]['Memory'].items():
        for engine, modes in engines.items():
            for mode, metrics in modes.items():
                ss = [read(f)['Samples'][0] for f in (p / 'memory').glob(f'{engine}-{mode}-*.json')]
                ss = [s for s in ss if s['Scenario'] == name]
                assert len(ss) == 3
                for field, expected in metrics.items():
                    assert abs(statistics.median(s[field] / 1048576 for s in ss) - expected) < 1e-8
    parents = read(p / 'startup/parent.json')
    assert len(parents) == 84
    for engine, modes in summary['Platforms'][platform]['Startup'].items():
        for mode, metrics in modes.items():
            rows = [r for r in parents if r['Engine'] == engine and r['Mode'] == mode]
            assert len(rows) == 21
            assert statistics.median(r['ProcessMilliseconds'] for r in rows) == metrics['ProcessMs']
            children = [read(p / 'startup' / r['Result']) for r in rows]
            assert all(r['DynamicCodeSupported'] == (mode == 'jit') for r in children)
            assert statistics.median(r['Samples'][0]['Milliseconds'] for r in children) == metrics['OpenQueryCloseMs']
print('PASS actual AOT: 12 size manifests, 360 main / 72 RSS / 168 startup samples, 14 groups and 460 differential executions.')
