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
print('PASS latest-only evidence: 492 main, 36 memory, 144 ADO, 12 steady; arithmetic, hashes, links, tables, UTF-8.')
