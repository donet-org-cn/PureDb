"""SQLite DELETE/FULL 文件后端专项；不与原版格式性能混算。"""
import argparse, ctypes, hashlib, json, os, pathlib, shutil, subprocess
root = pathlib.Path(__file__).resolve().parents[1]
p = argparse.ArgumentParser(); p.add_argument('output', type=pathlib.Path); p.add_argument('--before', required=True, type=pathlib.Path); a = p.parse_args()
os.chdir(root)
if os.name == 'nt': ctypes.windll.kernel32.SetErrorMode(0x8003)
out = a.output.resolve(); assert not out.exists(); out.mkdir(parents=True)
fixture = out/'fixture'; hosts = {}
for stage in ['before', 'after']:
    folder = out/'hosts'/stage
    shutil.copytree(root/'benchmarks/PureDb.CompatibilityBenchmarks/bin/Release/net10.0', folder)
    if stage == 'before':
        for name in ['PureDb.dll','PureDb.SqliteCompatibility.dll']: shutil.copyfile(a.before/name,folder/name)
    hosts[stage] = folder/'PureDb.CompatibilityBenchmarks.dll'
    (out/stage).mkdir()
subprocess.run(['dotnet',str(hosts['after']),'create',str(fixture)],check=True)
for repeat in range(1,4):
    order = [('before','Compatibility'),('after','Compatibility'),('after','Incremental'),('after','SQLite')]
    if repeat%2 == 0: order.reverse()
    for scenario in ['point','range20','count','reopen','single_update','batch1000']:
        for stage,engine in order:
            dest=out/stage/f'{engine}-{scenario}-{repeat}.json'
            with dest.with_suffix('.log').open('w',encoding='utf-8') as stream:
                subprocess.run(['dotnet',str(hosts[stage]),engine,scenario,str(fixture),str(dest),str(repeat)],stdout=stream,stderr=subprocess.STDOUT,check=True)
            print(stage,engine,scenario,repeat,flush=True)
            rawdata=out/stage/'data';data=rawdata.resolve()
            assert out in data.parents and not rawdata.is_symlink()
            if data.is_dir():shutil.rmtree(data)
sha=lambda f:hashlib.sha256(f.read_bytes()).hexdigest().upper()
(out/'environment.json').write_text(json.dumps({'Method':'Same current host, 2000 rows of 256-byte BLOB, 4096 UTF-8, SQLite DELETE/FULL exclusive, three rounds','Hosts':{s:sha(h) for s,h in hosts.items()},'Cores':{s:sha(h.parent/'PureDb.dll') for s,h in hosts.items()},'Compatibility':{s:sha(h.parent/'PureDb.SqliteCompatibility.dll') for s,h in hosts.items()}},indent=2),encoding='utf-8')
(out/'COMPLETE').write_text('Complete\n',encoding='utf-8')
