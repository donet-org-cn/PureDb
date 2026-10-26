"""Run serialized, alternating deployment comparisons; preserve per-process startup and memory."""
import argparse,json,os,pathlib,platform,subprocess,time,xml.etree.ElementTree as ET

root=pathlib.Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser();p.add_argument('published',type=pathlib.Path);p.add_argument('output',type=pathlib.Path);p.add_argument('--rounds',type=int,default=3);p.add_argument('--reuse-fixture',type=pathlib.Path);p.add_argument('--discard-generated-writes',action='store_true');a=p.parse_args()
published=a.published.resolve();output=a.output.resolve();output.mkdir(parents=True,exist_ok=True)
assert not (output/'environment.json').exists(),'Use a fresh result directory'
manifest=json.loads((published/'manifest.json').read_text())
if os.name=='nt':
 import ctypes
 ctypes.windll.kernel32.SetErrorMode(0x8003)
else:
 import resource
 resource.setrlimit(resource.RLIMIT_CORE,(0,0))
variants={(v['Engine'],v['Mode']):v for v in manifest['Variants']}
logs=output/'logs';logs.mkdir()
fixture=output/'fixture';fixture.mkdir()

def command(engine,mode,args):
 variant=variants[engine,mode];directory=published/variant['Directory']
 env=os.environ.copy()
 if variant['SQLiteLibrary']:env['SQLITE_LIBRARY']=str(directory/variant['SQLiteLibrary'])
 return [str(directory/variant['Executable']),*map(str,args)],env

def run(engine,mode,args,name):
 cmd,env=command(engine,mode,args);start=time.perf_counter()
 previous=set(fixture.iterdir()) if a.discard_generated_writes and args[0]=='writes' else None
 with (logs/(name+'.log')).open('w',encoding='utf-8') as stream:
  subprocess.run(cmd,env=env,cwd=root,stdout=stream,stderr=subprocess.STDOUT,check=True)
 elapsed=(time.perf_counter()-start)*1000
 if previous is not None:
  # 子进程已正常退出，JSON 已落盘；只移除本次调用新建的写入测试文件，初始查询库不动。
  for file in set(fixture.iterdir())-previous:
   assert file.is_file() and not file.is_symlink() and file.resolve().parent==fixture.resolve() and file.name.startswith(engine+'-') and '.db' in file.name
   file.unlink()
 print(name,round(elapsed,2),'ms',flush=True)
 return elapsed

if a.reuse_fixture:
 # 仅复用已完成测试的初始查询库；不复用旧写入场景数据，目标仍是新的隔离目录。
 import shutil
 source=a.reuse_fixture.resolve()
 for prefix in ['pure.pdb','sqlite.sqlite']:
  assert (source/prefix).is_file(),source/prefix
  for file in source.glob(prefix+'*'):
   if file.is_file():shutil.copyfile(file,fixture/file.name)
else:
 for engine in ['PureDb','SQLite']:run(engine,'jit',['create',fixture],engine+'-create')
environment={'Date':time.strftime('%Y-%m-%dT%H:%M:%S%z'),'Platform':platform.platform(),'CPUs':os.cpu_count(),
 'Affinity':sorted(os.sched_getaffinity(0)) if hasattr(os,'sched_getaffinity') else None,
 'Version':ET.parse(root/'Directory.Build.props').findtext('.//Version'),'Manifest':manifest,'Rounds':a.rounds,
 'QueryWarmupMilliseconds':500,'QueryMeasurementMilliseconds':750,'Rows':100000}
(output/'environment.json').write_text(json.dumps(environment,indent=2)+'\n')
for folder in ['raw','memory','startup']:(output/folder).mkdir()
for repeat in range(1,a.rounds+1):
 order=[('PureDb','jit'),('PureDb','aot'),('SQLite','jit'),('SQLite','aot')]
 if repeat%2==0:order.reverse()
 for kind in ['queries','writes']:
  for engine,mode in order:
   name=f'{engine}-{mode}-{kind}-{repeat}';dest=output/'raw'/(name+'.json')
   run(engine,mode,[kind,fixture,dest,repeat],name)
   data=json.loads(dest.read_text());assert data['DynamicCodeSupported']==(mode!='aot')
 for scenario in [0,1,2]:
  for engine,mode in order:
   name=f'{engine}-{mode}-{scenario}-{repeat}';dest=output/'memory'/(name+'.json')
   run(engine,mode,['memory',fixture,dest,repeat,scenario],'memory-'+name)
startup=[]
for repeat in range(21):
 order=[('PureDb','jit'),('PureDb','aot'),('SQLite','jit'),('SQLite','aot')]
 if repeat%2:order.reverse()
 for engine,mode in order:
  name=f'{engine}-{mode}-{repeat}';dest=output/'startup'/(name+'.json')
  elapsed=run(engine,mode,['startup',fixture,dest,repeat],'startup-'+name)
  startup.append({'Engine':engine,'Mode':mode,'Round':repeat,'ProcessMilliseconds':elapsed,'Result':dest.name})
(output/'startup/parent.json').write_text(json.dumps(startup,indent=2)+'\n')
# 数据文件体积在双方连接关闭后统计，避免把临时 WAL 与常驻文件混淆。
(output/'fixture-files.json').write_text(json.dumps([{'Path':f.name,'Bytes':f.stat().st_size} for f in fixture.iterdir() if f.is_file()],indent=2)+'\n')
(output/'COMPLETE').write_text('Three rounds, fresh-process startup, isolated memory and closed fixture sizes\n')
print('DEPLOYMENT_MEASUREMENT_COMPLETE',flush=True)
