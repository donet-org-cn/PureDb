"""Measure the cache regression on the same published JIT host/runtime, swapping only PureDb.dll."""
import argparse,pathlib,shutil,subprocess,hashlib,json,os
p=argparse.ArgumentParser();p.add_argument('published',type=pathlib.Path);p.add_argument('before_core',type=pathlib.Path);p.add_argument('dataset',type=pathlib.Path);p.add_argument('output',type=pathlib.Path);p.add_argument('--before-commit',required=True);a=p.parse_args()
published=a.published.resolve();output=a.output.resolve();output.mkdir(parents=True,exist_ok=True)
assert not (output/'environment.json').exists(),'Use fresh output'
manifest=json.loads((published/'manifest.json').read_text());variant=next(v for v in manifest['Variants'] if v['Engine']=='PureDb' and v['Mode']=='jit')
after=published/variant['Directory'];before=output/'host-before';shutil.copytree(after,before);shutil.copyfile(a.before_core,before/'PureDb.dll')
if os.name=='nt':
 import ctypes
 ctypes.windll.kernel32.SetErrorMode(0x8003)
else:
 import resource
 resource.setrlimit(resource.RLIMIT_CORE,(0,0))
hash=lambda p:hashlib.sha256(p.read_bytes()).hexdigest().upper()
assert hash(before/variant['Executable'])==hash(after/variant['Executable'])
environment={'BeforeCommit':a.before_commit,'BeforeCoreSHA256':hash(before/'PureDb.dll'),'AfterCoreSHA256':hash(after/'PureDb.dll'),'HostSHA256':hash(after/variant['Executable']),'RuntimeVersion':manifest['RuntimeVersion'],'Rows':100000,'SQLCacheBytes':64*1024*1024,'Note':'Same self-contained JIT host and runtime; only PureDb.dll differs.'}
(output/'environment.json').write_text(json.dumps(environment,indent=2)+'\n')
(output/'raw').mkdir();(output/'logs').mkdir()
for round in range(1,4):
 for stage in (['before','after'] if round%2 else ['after','before']):
  host=before if stage=='before' else after;dest=output/'raw'/f'{stage}-{round}.json'
  with (output/'logs'/f'{stage}-{round}.log').open('w') as log:
   subprocess.run([str(host/variant['Executable']),'queries',str(a.dataset.resolve()),str(dest),str(round)],stdout=log,stderr=subprocess.STDOUT,check=True)
  print('PASS',stage,round,flush=True)
(output/'COMPLETE').write_text('Paired three-round JIT cache budget regression measurement\n')
