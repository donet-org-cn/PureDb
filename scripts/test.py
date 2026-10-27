"""Run correctness groups and SQLite differential queries; no performance ranking in CI."""
import os,pathlib,platform,subprocess,uuid,sys
root=pathlib.Path(__file__).resolve().parents[1];os.chdir(root)
if os.name=='nt':
 import ctypes
 ctypes.windll.kernel32.SetErrorMode(0x8003)
if os.name=='posix':
 import resource
 resource.setrlimit(resource.RLIMIT_CORE,(0,0))
name='sqlite3.dll' if platform.system()=='Windows' else 'libsqlite3.dylib' if platform.system()=='Darwin' else 'libsqlite3.so'
library=root/'artifacts/sqlite/3.54.1'/name
if not library.exists():raise SystemExit('Run python scripts/prepare-sqlite.py first')
env=os.environ.copy();env['SQLITE_LIBRARY']=str(library)
for name in ['PureDb.Tests','PureDb.SqlTests','PureDb.AdoNetTests','PureDb.StorageTests','PureDb.CompatibilityTests','PureDb.FeatureTests','PureDb.SortTests','PureDb.SqlCompatibilityTests','PureDb.AotTests','PureDb.SqliteParityTests']:
 subprocess.run(['dotnet',str(root/'tests'/name/'bin/Release/net10.0'/(name+'.dll'))],env=env,check=True)
subprocess.run(['dotnet',str(root/'tests/PureDb.StorageTests/bin/Release/net10.0/PureDb.StorageTests.dll'),'--soak','100000'],env=env,check=True)
fixture = pathlib.Path(sys.argv[1]).resolve() if len(sys.argv)>1 else root/'artifacts'/('ci-fixture-'+uuid.uuid4().hex)
subprocess.run(['dotnet',str(root/'benchmarks/PureDb.QueryBenchmarks/bin/Release/net10.0/PureDb.QueryBenchmarks.dll'),'100000','1',str(fixture),* ([] if len(sys.argv)>1 else ['--create']),'--verify-only'],env=env,check=True)
