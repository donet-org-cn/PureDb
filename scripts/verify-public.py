"""Verify latest evidence arithmetic and public Markdown links without requiring identical build paths."""
import json,re,statistics,subprocess
from pathlib import Path
root=Path(__file__).resolve().parents[1];base=root/'benchmarks/results/latest';read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
summary=read(base/'summary.json')
for platform,name in [('Windows x64','windows'),('Linux ARM64','linux')]:
 p=base/name
 rs=[read(f) for f in (p/'raw').glob('*.json')];assert len(rs)==18
 assert all(r['Engine'] in ['Optimized','SQLite'] and r['QueryWarmupMilliseconds']==500 and r['QueryMeasurementMilliseconds']==750 for r in rs)
 assert {r['LibrarySHA256'] for r in rs if r['Engine']=='Optimized'}=={summary['Platforms'][platform]['MeasuredCoreSHA256']}
 ss=[s for r in rs for s in r['Samples']];assert len(ss)==246
 for scenario,metrics in summary['Platforms'][platform]['Metrics'].items():
  for engine,m in metrics.items():
   vals=[v for v in ss if v['Scenario']==scenario and v['Engine']==engine];assert len(vals)==3 and {r['Repeat'] for r in vals}=={1,2,3}
   assert abs(statistics.median(v['Milliseconds']*1000/v['Operations'] for v in vals)-m['Us'])<1e-8
   assert abs(statistics.median((v['CpuMilliseconds']+v.get('WorkerCpuMilliseconds',0))*1000/v['Operations'] for v in vals)-m['CpuUs'])<1e-8
   assert abs(statistics.median((v['CpuMilliseconds']+v.get('WorkerCpuMilliseconds',0))/v['Milliseconds']*100/summary['Platforms'][platform]['CPUs'] for v in vals)-m['CPU'])<1e-8
 assert len([f for f in (p/'memory').glob('*.json') if f.name!='summary.json'])==18
 assert sum(len(read(f)['Samples']) for f in (p/'ado').glob('*.json'))==72
 assert len(list((p/'steady').glob('*.json')))==6
for p in [root/'README.md',*list((root/'docs').glob('*.md'))]:
 text=p.read_text(encoding='utf-8-sig');assert '\ufffd' not in text and '???' not in text,p
 for target in re.findall(r'\[[^\]]*\]\(([^)]+)\)',text):
  if not target.startswith(('http://','https://','#')):assert (p.parent/target.split('#')[0]).exists(),(p,target)
 width=None
 for line in text.splitlines():
  if line.startswith('|'):
   w=line.count('|')-1
   if width is None:width=w
   assert width==w,p
  else:width=None
assert '正在生成' not in (root/'README.md').read_text(encoding='utf-8-sig')
functions=[read(root/'benchmarks/results/functions/latest'/(name+'.json')) for name in ['windows','linux']]
assert len({r['CoreSHA256'] for r in functions})==1
function_report=(root/'docs/DEVELOPMENT-VALIDATION.md').read_text(encoding='utf-8-sig')
assert functions[0]['CoreSHA256'] in function_report
for result in functions:
 assert len(result['Samples'])==6
 for name in ['typed_reject','array_reject']:
  samples=[s for s in result['Samples'] if s['Function']==name]
  assert len(samples)==3 and {s['Round'] for s in samples}=={1,2,3}
  assert all(s['RowsPerQuery']==20000 and s['WarmupMilliseconds']==500 and s['MeasurementMilliseconds']==750 and s['ElapsedMilliseconds']>=750 and s['Repetitions']>0 for s in samples)
  assert f"{statistics.median(s['ElapsedMilliseconds']*1000/s['Repetitions'] for s in samples):,.3f}" in function_report
  assert f"{statistics.median(s['CpuMilliseconds']*1000/s['Repetitions'] for s in samples):,.3f}" in function_report
  assert f"{statistics.median(s['AllocatedBytes']/s['Repetitions'] for s in samples):,.0f}" in function_report
print('PASS latest-only evidence: 492 main, 36 memory, 144 ADO, 12 steady; arithmetic, hashes, links, tables, UTF-8.')
print('PASS user-function evidence: matched build hashes, 12 samples, normalized CPU/time/allocation table.')
sort_base=root/'benchmarks/results/sort/latest'
sort_summary=read(sort_base/'summary.json')
sort_report=(root/'docs/SORT-PERFORMANCE.md').read_text(encoding='utf-8-sig')
sort_results={name:read(sort_base/(name+'.json')) for name in ['windows-before','windows-after','linux-before','linux-after']}
for stage in ['before','after']:
 assert sort_results['windows-'+stage]['CoreSHA256']==sort_results['linux-'+stage]['CoreSHA256']
 assert sort_results['windows-'+stage]['CoreSHA256'] in sort_report
for platform,key in [('Windows x64','windows'),('Linux ARM64','linux')]:
 for label,stage,engine in [('Before','before','PureDb'),('After','after','PureDb'),('SQLite','after','SQLite')]:
  result=sort_results[key+'-'+stage]
  assert result['SQLiteVersion']=='3.54.1' and len(result['Samples'])==24
  for scenario,metrics in sort_summary['Platforms'][platform].items():
   samples=[s for s in result['Samples'] if s['Engine']==engine and s['Scenario']==scenario]
   assert len(samples)==3 and {s['Round'] for s in samples}=={1,2,3}
   assert all(s['RowsPerQuery']==100000 and s['WarmupMilliseconds']==500 and s['MeasurementMilliseconds']==750 and s['ElapsedMilliseconds']>=750 and s['Repetitions']>0 for s in samples)
   expected={'Us':statistics.median(s['ElapsedMilliseconds']*1000/s['Repetitions'] for s in samples),
             'CpuUs':statistics.median(s['CpuMilliseconds']*1000/s['Repetitions'] for s in samples),
             'AllocatedBytes':statistics.median(s['AllocatedBytes']/s['Repetitions'] for s in samples)}
   for field,value in expected.items():
    assert abs(value-metrics[label][field])<1e-8
    # SQLite 原生分配不在托管分配比较表中，不把它当作总内存指标。
    if field!='AllocatedBytes' or engine=='PureDb':
     assert format(value,',.0f' if field=='AllocatedBytes' else ',.3f') in sort_report
print('PASS sort evidence: paired Windows/Linux hashes, 96 samples, CPU/time/allocation arithmetic and report.')
optimization=root/'benchmarks/results/optimization/latest'
paired=read(optimization/'summary.json')
optimization_report=(root/'docs/OPTIMIZATION.md').read_text(encoding='utf-8-sig')
assert paired['BeforeCommit']=='09cdfffbfb4ab6f10ba373ac6e46745049397cc2'
for platform,key in [('Windows x64','windows'),('Linux ARM64','linux')]:
 result=paired['Platforms'][platform]
 assert result['AfterSHA256']==summary['Platforms'][platform]['MeasuredCoreSHA256']
 assert result['BeforeSHA256']==sort_results[key+'-before']['CoreSHA256']
 reports=[read(p) for p in (optimization/key/'before/raw').glob('*.json')]
 assert len(reports)==18 and sum(len(r['Samples']) for r in reports)==246
 assert len(list((optimization/key/'before/memory').glob('*.json')))==18
 for scenario,engines in result['Before'].items():
  for engine,metrics in engines.items():
   samples=[s for r in reports for s in r['Samples'] if s['Scenario']==scenario and s['Engine']==engine]
   assert len(samples)==3
   assert abs(statistics.median(s['Milliseconds']*1000/s['Operations'] for s in samples)-metrics['Us'])<1e-8
   assert abs(statistics.median((s['CpuMilliseconds']+s.get('WorkerCpuMilliseconds',0))*1000/s['Operations'] for s in samples)-metrics['CpuUs'])<1e-8
   if engine=='Optimized':assert f"{metrics['Us']:,.3f}" in optimization_report
 assert all(result['After'][s][e]['Us']==summary['Platforms'][platform]['Metrics'][s][e]['Us'] for s in result['After'] for e in ['Optimized','SQLite'])
 assert read(base/key/'validation.json')['TestGroups']==77
 assert read(base/key/'validation.json')['DifferentialQueries']==524
print('PASS paired optimization evidence: 984 main / 72 memory records (738 / 54 unique executions; SQLite reference shared), build boundaries, arithmetic and validation counts.')
acceptance=read(optimization/'acceptance.json')
for platform,key in [('Windows x64','windows'),('Linux ARM64','linux')]:
 import math
 result=acceptance[platform];assert len(result['Scenarios'])==19
 assert set(result['Scenarios'])=={s['Scenario'] for s in read(base/key/'raw/Optimized-queries-1.json')['Samples']}
 ratios=[paired['Platforms'][platform]['Before'][s]['Optimized']['Us']/paired['Platforms'][platform]['After'][s]['Optimized']['Us'] for s in result['Scenarios']]
 geo=math.exp(sum(map(math.log,ratios))/19)
 assert abs(geo-result['ThroughputGeomean'])<1e-12
 assert abs((1-1/geo)-result['LatencyReduction'])<1e-12
 assert result['LatencyReduction']>=.10,platform+' fixed read/reopen corpus did not meet 10% latency reduction'
 validation=read(base/key/'validation.json');assert validation['AdditionalSQLCases']==175 and validation['FileProfiles']==64
 compatibility=root/'benchmarks/results/compatibility/latest'/key
 sql=read(compatibility/'sql-parity.json');files=read(compatibility/'file-parity.json')
 assert sql['Total']==175 and sql['Passed']==sum(c['Passed'] for c in sql['Cases'])==175
 assert sql['CorpusSHA256']=='5D8E0C5FB5FCFC638965ACE2E750A8BD3B2FA75743DF3DBC37835FE179FCC325'
 assert files['Total']==64 and files['Passed']==sum(c['Passed'] for c in files['Cases'])==64
 assert len(list((compatibility/'performance/before').glob('*.json')))==18
 assert len(list((compatibility/'performance/after').glob('*.json')))==54
 assert len(read(compatibility/'performance/environment.json')['Hosts'])==2
 assert len(set(read(compatibility/'performance/environment.json')['Hosts'].values()))==1
for name in ['all-performance','before-after','cpu-memory']:
 for ext in ['png','svg']:assert (root/'docs/images/preview.3'/(name+'.'+ext)).stat().st_size>1000
assert (root/'docs/CNBLOGS-preview.3.md').exists()
print('PASS preview.3 scoped acceptance: 19 fixed cases >=10% latency reduction on both platforms; 350 SQL / 128 file-case executions; all 144 file-mode performance samples and actual charts.')
subprocess.run([__import__('sys').executable,str(root/'scripts/verify-aot.py')],check=True)
