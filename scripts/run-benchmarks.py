"""Serial comparison of current native PureDb and native SQLite. Use a fresh output directory."""
import argparse, ctypes, hashlib, json, os, pathlib, platform, shutil, subprocess, time

p=argparse.ArgumentParser();p.add_argument('output');p.add_argument('dataset');p.add_argument('--tests',action='store_true');p.add_argument('--rounds',type=int,default=3);p.add_argument('--modes',default='queries,writes,processes');p.add_argument('--memory',action='store_true');p.add_argument('--ado',action='store_true')

p.add_argument('--discard-generated-writes',action='store_true')
a=p.parse_args();root=pathlib.Path.cwd();out=root/a.output;out.mkdir(parents=True,exist_ok=True)
if os.name=='nt':ctypes.windll.kernel32.SetErrorMode(0x8003)
for directory in ['raw','logs','memory','ado','steady']:(out/directory).mkdir(exist_ok=True)
assert not any((out/'raw').glob('*.json')),'Use a fresh output directory'
dataset=(root/a.dataset).resolve()
def binary(name,group='benchmarks'):return root/group/name/'bin/Release/net10.0'/(name+'.dll')

def disk():return pathlib.Path('/proc/diskstats').read_text() if platform.system()=='Linux' else None
def run(args,name):
    start=time.monotonic();before=disk();rss=anon=0
    with (out/'logs'/(name+'.log')).open('w',encoding='utf-8') as log:
        process=subprocess.Popen(['dotnet',*map(str,args)],stdout=log,stderr=subprocess.STDOUT)
        while process.poll() is None:
            if platform.system()=='Linux':
                try:
                    v=dict(line.split(':',1) for line in pathlib.Path('/proc/%d/status'%process.pid).read_text().splitlines() if ':' in line)
                    rss=max(rss,int(v.get('VmRSS','0 kB').split()[0])*1024);anon=max(anon,int(v.get('RssAnon','0 kB').split()[0])*1024)
                except (FileNotFoundError,ProcessLookupError):pass
            time.sleep(.05)
    seconds=time.monotonic()-start
    (out/'logs'/(name+'.host.json')).write_text(json.dumps(dict(Seconds=seconds,ExitCode=process.returncode,SampledPeakRSS=rss,SampledPeakAnonymousRSS=anon,DiskBefore=before,DiskAfter=disk()),indent=2))
    print(name,process.returncode,round(seconds,2),flush=True)
    if process.returncode:raise RuntimeError((out/'logs'/(name+'.log')).read_text(encoding='utf-8',errors='replace')[-6000:])
environment={'Platform':platform.platform(),'Machine':platform.machine(),'CPUs':os.cpu_count(),'Date':time.strftime('%Y-%m-%dT%H:%M:%S%z'),'Dotnet':subprocess.check_output(['dotnet','--list-runtimes'],text=True,encoding='utf-8',errors='replace').strip()}
environment['ComprehensiveSHA256']=hashlib.sha256(binary('PureDb.Comprehensive').read_bytes()).hexdigest().upper()
environment['AdoHarnessSHA256']=hashlib.sha256(binary('PureDb.AdoBenchmarks').read_bytes()).hexdigest().upper()
if platform.system()=='Linux':
    environment.update(Device=[os.major(os.stat('.').st_dev),os.minor(os.stat('.').st_dev)],Load=pathlib.Path('/proc/loadavg').read_text(),MemInfo=pathlib.Path('/proc/meminfo').read_text(),ProcessIoAvailable=pathlib.Path('/proc/self/io').exists(),Affinity=sorted(os.sched_getaffinity(0)))
    environment['CpuCapacity']={p.parent.name:int(p.read_text()) for p in pathlib.Path('/sys/devices/system/cpu').glob('cpu[0-9]*/cpu_capacity')}
(out/'environment.json').write_text(json.dumps(environment,indent=2))
if a.tests:
    for name in ['PureDb.Tests','PureDb.SqlTests','PureDb.AdoNetTests','PureDb.StorageTests','PureDb.CompatibilityTests']:run([binary(name,'tests')],name)
    run([binary('PureDb.QueryBenchmarks'),100000,1,dataset,'--verify-only'],'differential')
engines=['Optimized','SQLite']
for repeat in range(1,a.rounds+1):
    order=engines[(repeat-1)%len(engines):]+engines[:(repeat-1)%len(engines)]
    for mode in a.modes.split(','):
        for engine in order:
            name=f'{engine}-{mode}-{repeat}';harness=binary('PureDb.Comprehensive')
            run([harness,mode,engine,dataset,out/'raw'/(name+'.json'),repeat],name)
            if a.discard_generated_writes:
                data=(out/'raw/data').resolve()
                assert out.resolve() in data.parents and not (out/'raw/data').is_symlink()
                if data.is_dir():shutil.rmtree(data)
    if a.memory:
        for scenario in ['point','range','json']:
            for engine in order:
                name=f'{engine}-{scenario}-{repeat}';harness=binary('PureDb.Comprehensive')
                run([harness,'memory',engine,dataset,out/'memory'/(name+'.json'),repeat,scenario],'memory-'+name)
    if a.ado:
        for engine in ['Core','Ado','SQLiteNative']:
            name=f'{engine}-{repeat}'
            run([binary('PureDb.AdoBenchmarks'),engine,dataset,out/'ado'/(name+'.json'),repeat],'ado-'+name)
for repeat in range(1,a.rounds+1):
    for engine in (engines if repeat%2 else engines[::-1]):
        run([binary('PureDb.CacheBenchmarks'),engine,dataset,out/'steady'/(engine+'-'+str(repeat)+'.json')], 'steady-'+engine+'-'+str(repeat))
(out/'COMPLETE').write_text(time.strftime('%Y-%m-%dT%H:%M:%S%z'))
print('CORE_COMPARISON_COMPLETE',flush=True)
