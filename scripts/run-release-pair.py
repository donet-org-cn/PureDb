"""同一宿主仅替换内核，串行轮换旧版、新版和 SQLite，保留完整资源样本。"""
import argparse, ctypes, hashlib, json, os, pathlib, platform, shutil, subprocess, time

root = pathlib.Path(__file__).resolve().parents[1]
p = argparse.ArgumentParser()
p.add_argument('output', type=pathlib.Path)
p.add_argument('dataset', type=pathlib.Path)
p.add_argument('--before', required=True, type=pathlib.Path)
p.add_argument('--dotnet', default='dotnet')
p.add_argument('--rounds', type=int, default=3)
a = p.parse_args()
os.chdir(root)
if os.name == 'nt': ctypes.windll.kernel32.SetErrorMode(0x8003)
else:
    import resource
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
out = a.output.resolve(); fixture = a.dataset.resolve()
assert not out.exists(), 'Use a fresh output directory'
out.mkdir(parents=True)
sha = lambda f: hashlib.sha256(f.read_bytes()).hexdigest().upper()
names = ['PureDb.Comprehensive', 'PureDb.AdoBenchmarks', 'PureDb.CacheBenchmarks']
hosts = {}
for stage in ['before', 'after']:
    hosts[stage] = {}
    for name in names:
        src = root / 'benchmarks' / name / 'bin/Release/net10.0'
        dst = out / 'hosts' / stage / name
        shutil.copytree(src, dst)
        if stage == 'before': shutil.copyfile(a.before, dst / 'PureDb.dll')
        hosts[stage][name] = dst / (name + '.dll')
    for part in ['raw', 'logs', 'memory', 'ado', 'steady']:
        (out / stage / part).mkdir(parents=True)
env = {'Date': time.strftime('%Y-%m-%dT%H:%M:%S%z'), 'Platform': platform.platform(),
       'Machine': platform.machine(), 'CPUs': os.cpu_count(), 'BeforeCommit': '09cdfffbfb4ab6f10ba373ac6e46745049397cc2',
       'BeforeSHA256': sha(a.before), 'AfterSHA256': sha(hosts['after'][names[0]].parent / 'PureDb.dll'),
       'IdenticalHarnessSHA256': {name: sha(hosts['after'][name]) for name in names},
       'Dotnet': subprocess.check_output([a.dotnet, '--list-runtimes'], text=True).strip(),
       'RoundOrder': [], 'Method': 'Identical current harness; only PureDb.dll differs; sequential rotated three-engine order'}
if platform.system() == 'Linux':
    env.update(Device=[os.major(os.stat('.').st_dev), os.minor(os.stat('.').st_dev)],
               Load=pathlib.Path('/proc/loadavg').read_text(), Affinity=sorted(os.sched_getaffinity(0)),
               MemInfo=pathlib.Path('/proc/meminfo').read_text())
for name in names: assert sha(hosts['before'][name]) == sha(hosts['after'][name])
for stage in hosts: (out / stage / 'environment.json').write_text(json.dumps(env, indent=2), encoding='utf-8')

def disk():
    return pathlib.Path('/proc/diskstats').read_text() if platform.system() == 'Linux' else None

def run(stage, name, command, dest):
    start = time.monotonic(); before = disk(); rss = anon = 0
    log = out / stage / 'logs' / (name + '.log')
    with log.open('w', encoding='utf-8') as stream:
        proc = subprocess.Popen([a.dotnet, *map(str, command)], stdout=stream, stderr=subprocess.STDOUT)
        while proc.poll() is None:
            if platform.system() == 'Linux':
                try:
                    status = dict(line.split(':', 1) for line in pathlib.Path(f'/proc/{proc.pid}/status').read_text().splitlines() if ':' in line)
                    rss = max(rss, int(status.get('VmRSS', '0 kB').split()[0]) * 1024)
                    anon = max(anon, int(status.get('RssAnon', '0 kB').split()[0]) * 1024)
                except (FileNotFoundError, ProcessLookupError): pass
            time.sleep(.05)
    host = dict(Seconds=time.monotonic()-start, ExitCode=proc.returncode, SampledPeakRSS=rss,
                SampledPeakAnonymousRSS=anon, DiskBefore=before, DiskAfter=disk())
    log.with_suffix('.host.json').write_text(json.dumps(host, indent=2), encoding='utf-8')
    print(stage, name, proc.returncode, round(host['Seconds'], 2), flush=True)
    if proc.returncode: raise RuntimeError(log.read_text(encoding='utf-8')[-6000:])
    assert dest.is_file()

for repeat in range(1, a.rounds + 1):
    order = ['before', 'after', 'SQLite']; order = order[(repeat-1)%3:] + order[:(repeat-1)%3]
    env['RoundOrder'].append(order)
    for mode in ['queries', 'writes', 'processes']:
        for entry in order:
            stage = 'after' if entry == 'SQLite' else entry; engine = 'SQLite' if entry == 'SQLite' else 'Optimized'
            name = f'{engine}-{mode}-{repeat}'; dest = out / stage / 'raw' / (name + '.json')
            run(stage, name, [hosts[stage][names[0]], mode, engine, fixture, dest, repeat], dest)
            # 仅清理本次测试输出目录内自动生成的数据文件，数值 JSON 和日志全部保留。
            rawdata = out / stage / 'raw/data'; data = rawdata.resolve()
            assert out in data.parents and not rawdata.is_symlink()
            if data.is_dir(): shutil.rmtree(data)
    for scenario in ['point', 'range', 'json']:
        for entry in order:
            stage = 'after' if entry == 'SQLite' else entry; engine = 'SQLite' if entry == 'SQLite' else 'Optimized'
            name = f'{engine}-{scenario}-{repeat}'; dest = out / stage / 'memory' / (name + '.json')
            run(stage, 'memory-'+name, [hosts[stage][names[0]], 'memory', engine, fixture, dest, repeat, scenario], dest)
    for entry in order:
        stage = 'after' if entry == 'SQLite' else entry
        for engine in (['SQLiteNative'] if entry == 'SQLite' else ['Core', 'Ado']):
            dest = out / stage / 'ado' / f'{engine}-{repeat}.json'
            run(stage, f'ado-{engine}-{repeat}', [hosts[stage][names[1]], engine, fixture, dest, repeat], dest)
        engine = 'SQLite' if entry == 'SQLite' else 'Optimized'; dest = out / stage / 'steady' / f'{engine}-{repeat}.json'
        run(stage, f'steady-{engine}-{repeat}', [hosts[stage][names[2]], engine, fixture, dest], dest)

for part in ['raw', 'memory', 'ado', 'steady', 'logs']:
    for f in (out / 'after' / part).glob('SQLite*'):
        if f.is_file(): shutil.copyfile(f, out / 'before' / part / f.name)
for stage in ['before', 'after']:
    (out / stage / 'environment.json').write_text(json.dumps(env, indent=2), encoding='utf-8')
    (out / stage / 'COMPLETE').write_text('Three rounds; identical harness; serialized before/after/SQLite\n', encoding='utf-8')
(out / 'COMPLETE').write_text('Paired complete\n', encoding='utf-8')
