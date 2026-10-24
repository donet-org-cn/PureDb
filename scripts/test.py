"""Run correctness groups and SQLite differential queries; no performance ranking in CI."""
import os,pathlib,platform,subprocess,uuid
root=pathlib.Path(__file__).resolve().parents[1];os.chdir(root)
if os.name=='posix':
 import resource
 resource.setrlimit(resource.RLIMIT_CORE,(0,0))
name='sqlite3.dll' if platform.system()=='Windows' else 'libsqlite3.dylib' if platform.system()=='Darwin' else 'libsqlite3.so'
library=root/'artifacts/sqlite/3.54.1'/name
if not library.exists():raise SystemExit('Run python scripts/prepare-sqlite.py first')
env=os.environ.copy();env['SQLITE_LIBRARY']=str(library)
for name in ['PureDb.Tests','PureDb.SqlTests','PureDb.AdoNetTests','PureDb.StorageTests','PureDb.CompatibilityTests','PureDb.FeatureTests','PureDb.SortTests','PureDb.SqlCompatibilityTests','PureDb.AotTests']:
 subprocess.run(['dotnet',str(root/'tests'/name/'bin/Release/net10.0'/(name+'.dll'))],env=env,check=True)
subprocess.run(['dotnet',str(root/'benchmarks/PureDb.QueryBenchmarks/bin/Release/net10.0/PureDb.QueryBenchmarks.dll'),'100000','1',str(root/'artifacts'/('ci-fixture-'+uuid.uuid4().hex)),'--create','--verify-only'],env=env,check=True)
