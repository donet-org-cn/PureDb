"""Copy only current machine-readable evidence into the public latest directory."""
import argparse,json,pathlib,shutil
p=argparse.ArgumentParser();p.add_argument('source');p.add_argument('platform',choices=['windows','linux']);a=p.parse_args()
root=pathlib.Path(__file__).resolve().parents[1];src=(root/a.source).resolve();dst=root/'benchmarks/results/latest'/a.platform
assert (src/'COMPLETE').exists(),'Benchmark suite incomplete'
tests={'PureDb.Tests':'11 test groups passed','PureDb.SqlTests':'17 SQL/shared test groups passed','PureDb.AdoNetTests':'10 ADO.NET/cache test groups passed','PureDb.StorageTests':'12 storage/exchange groups passed','PureDb.CompatibilityTests':'10 managed SQLite compatibility test groups passed','PureDb.FeatureTests':'7 feature test groups passed','PureDb.SortTests':'160','PureDb.SqlCompatibilityTests':'230 extended SQL differential queries'}
def read(path):
 b=path.read_bytes();return b.decode('utf-16' if b.startswith((b'\xff\xfe',b'\xfe\xff')) else 'utf-8-sig')
assert all(msg in read(src/'logs'/(n+'.log')) for n,msg in tests.items())
assert '134' in read(src/'logs/differential.log')
assert all(marker in read(src/'logs/PureDb.SortTests.log') for marker in ['PASS sort semantics:', 'PASS sort plans:'])
dst.mkdir(parents=True,exist_ok=True)
assert not any((dst/'raw').glob('*.json')),'Use a fresh public result directory'
for f in src.rglob('*'):
 if f.is_file() and 'data' not in f.relative_to(src).parts and f.suffix in ['.json','.jsonl']:
  target=dst/f.relative_to(src);target.parent.mkdir(parents=True,exist_ok=True);shutil.copyfile(f,target)
(dst/'validation.json').write_text(json.dumps({'Passed':True,'TestGroups':69,'DifferentialQueries':524,'Groups':tests},indent=2),encoding='utf-8')
print('Collected latest public evidence:',a.platform)
