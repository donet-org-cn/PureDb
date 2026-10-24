"""Publish isolated PureDb/SQLite hosts in three deployment modes and run real AOT tests."""
import argparse, hashlib, json, os, pathlib, platform, shutil, subprocess, sys, xml.etree.ElementTree as ET

root = pathlib.Path(__file__).resolve().parents[1]
p = argparse.ArgumentParser()
p.add_argument('output', type=pathlib.Path)
p.add_argument('--rid', required=True)
p.add_argument('--dotnet', default='dotnet')
p.add_argument('--sqlite', required=True, type=pathlib.Path)
p.add_argument('--validate-only', action='store_true')
a = p.parse_args()
output = a.output.resolve(); output.mkdir(parents=True, exist_ok=True)
assert a.sqlite.is_file()
os.chdir(root)
if os.name == 'nt':
    import ctypes
    ctypes.windll.kernel32.SetErrorMode(0x8003)
else:
    import resource
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))

def run(command, log):
    with log.open('w', encoding='utf-8') as stream:
        result = subprocess.run(list(map(str, command)), stdout=stream, stderr=subprocess.STDOUT)
    text = log.read_text(encoding='utf-8', errors='replace')
    if result.returncode:
        print('\n'.join(text.splitlines()[-50:]), flush=True)
        raise subprocess.CalledProcessError(result.returncode, command)
    assert 'warning IL' not in text and 'error IL' not in text, log


def publish(project, target, mode):
    args = [a.dotnet, 'publish', root / project, '-c', 'Release', '-r', a.rid, '-o', target,
            '-p:UseAppHost=true', '-p:RuntimeFrameworkVersion=10.0.13', '-p:PublishSingleFile=false',
            '-p:PublishAot=' + str(mode == 'aot').lower(), '--self-contained', str(mode != 'framework').lower()]
    if mode == 'aot':
        args += ['-p:IlcOptimizationPreference=Speed', '-p:StripSymbols=true', '-p:TrimmerSingleWarn=false']
    else:
        args += ['-p:PublishTrimmed=false']
    run(args, target.parent / (target.name + '.build.log'))


variants = []
for engine, host in ([] if a.validate_only else [('PureDb', 'PureDbHost'), ('SQLite', 'SqliteHost')]):
    name = 'PureDb.Deployment.' + ('PureDb' if engine == 'PureDb' else 'Sqlite')
    project = f'benchmarks/PureDb.DeploymentBenchmarks/{host}/{name}.csproj'
    for mode in ['framework', 'jit', 'aot']:
        target = output / (engine + '-' + mode)
        assert not target.exists(), 'Use a fresh publish output directory'
        print('PUBLISH', engine, mode, a.rid, flush=True)
        publish(project, target, mode)
        native = None
        if engine == 'SQLite':
            native = 'sqlite3.dll' if a.rid.startswith('win') else 'libsqlite3.so'
            shutil.copyfile(a.sqlite, target / native)
        executable = name + ('.exe' if a.rid.startswith('win') else '')
        files = [{'Path': f.name, 'Bytes': f.stat().st_size,
                  'SHA256': hashlib.sha256(f.read_bytes()).hexdigest().upper(),
                  'DebugOrDocumentation': f.suffix in ['.pdb', '.dbg', '.xml']}
                 for f in sorted(target.iterdir()) if f.is_file()]
        variants.append({'Engine': engine, 'Mode': mode, 'Directory': target.name,
                         'Executable': executable, 'ExecutableBytes': (target / executable).stat().st_size,
                         'SQLiteLibrary': native, 'RuntimeBytes': sum(f['Bytes'] for f in files if not f['DebugOrDocumentation']),
                         'AllPublishedBytes': sum(f['Bytes'] for f in files), 'Files': files})

environment = os.environ.copy(); environment['SQLITE_LIBRARY'] = str(a.sqlite.resolve())
for name in ['PureDb.AotTests', 'PureDb.SqlCompatibilityTests']:
    target = output / name
    print('AOT TEST', name, a.rid, flush=True)
    publish(f'tests/{name}/{name}.csproj', target, 'aot')
    executable = target / (name + ('.exe' if a.rid.startswith('win') else ''))
    with (output / (name + '.run.log')).open('w', encoding='utf-8') as stream:
        subprocess.run([str(executable)], cwd=output, env=environment, stdout=stream, stderr=subprocess.STDOUT, check=True)
    content = (output / (name + '.run.log')).read_text(encoding='utf-8')
    assert 'dynamic code=False' in content if name == 'PureDb.AotTests' else '230 extended SQL differential queries' in content

manifest = {'RID': a.rid, 'Host': platform.platform(), 'Version': ET.parse(root/'Directory.Build.props').findtext('.//Version'), 'SDK': subprocess.check_output([a.dotnet, '--version'], text=True).strip(),
            'RuntimeVersion': '10.0.13', 'Variants': variants,
            'AotFeatureGroups': 7, 'AotDifferentialQueries': 230, 'AotWarnings': 0, 'ValidationOnly': a.validate_only}
(output / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
(output / 'PUBLISH_COMPLETE').write_text('Validated AOT binaries and separate engine deployment closures\n')
print('DEPLOYMENT_PUBLISH_COMPLETE', flush=True)
