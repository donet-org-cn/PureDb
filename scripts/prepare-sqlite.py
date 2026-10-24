"""Download pinned official SQLite test dependencies; production packages stay managed."""
import hashlib,io,os,pathlib,platform,subprocess,urllib.request,zipfile
root=pathlib.Path(__file__).resolve().parents[1]
out=root/'artifacts/sqlite/3.54.1';out.mkdir(parents=True,exist_ok=True)
windows=platform.system()=='Windows'
name='sqlite-dll-win-x64-3540100.zip' if windows else 'sqlite-amalgamation-3540100.zip'
expected='f436f53ad8f965af43f3817b42c99c0689e6787f6b7ace208fe38dab7ffc4971' if windows else '0a9c8e34352dad46006aa82836d5383bff9dc6bc9ce5a60553c05aa30e6ad1f9'
with urllib.request.urlopen('https://www.sqlite.org/2026/'+name,timeout=90) as r:data=r.read()
if hashlib.sha256(data).hexdigest()!=expected:raise RuntimeError('SQLite archive checksum mismatch')
with zipfile.ZipFile(io.BytesIO(data)) as z:
 if windows:
  item=next(n for n in z.namelist() if n.endswith('sqlite3.dll'));target=out/'sqlite3.dll';target.write_bytes(z.read(item))
 else:
  item=next(n for n in z.namelist() if n.endswith('/sqlite3.c'));source=out/'sqlite3.c';source.write_bytes(z.read(item))
  target=out/('libsqlite3.dylib' if platform.system()=='Darwin' else 'libsqlite3.so')
  subprocess.run(['cc','-O3','-shared','-fPIC','-DSQLITE_THREADSAFE=1',str(source),'-o',str(target),'-lm','-ldl','-lpthread'],check=True)
print('SQLite 3.54.1 test dependency:',target.relative_to(root))
