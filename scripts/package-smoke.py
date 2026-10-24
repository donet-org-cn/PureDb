"""Restore and exercise the actual preview packages from the local package output."""
import os,pathlib,subprocess,uuid,xml.etree.ElementTree as ET
root=pathlib.Path(__file__).resolve().parents[1];version=ET.parse(root/'Directory.Build.props').findtext('.//Version')
p=root/'artifacts'/('package-smoke-'+uuid.uuid4().hex);p.mkdir(parents=True)
(p/'Smoke.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup>'+''.join('<PackageReference Include="'+n+'" Version="'+version+'" />' for n in ['PureDb','PureDb.SqliteCompatibility'])+'</ItemGroup></Project>')
(p/'Program.cs').write_text('''using PureDb;
using PureDb.AdoNet;
using System.Reflection;
Environment.SetEnvironmentVariable("SQLITE_LIBRARY", "nonexistent-native-sqlite-library");
string path=Path.Combine(AppContext.BaseDirectory,"store.puredb");
using(var db=new SqlDatabase(path)) {
 db.ExecuteAsync("CREATE TABLE r(id INTEGER PRIMARY KEY,body JSON)").AsTask().GetAwaiter().GetResult();
 db.ExecuteAsync("INSERT INTO r VALUES(1,'{\\"age\\":25}')").AsTask().GetAwaiter().GetResult();
 if(!Equals(db.Query("SELECT json_extract(body,'$.age') FROM r WHERE id=1").Rows[0][0],25L))throw new Exception("JSON roundtrip");
 db.CreateFunction<long,long>("increment",value=>value+1);
 if(!Equals(db.Query("SELECT increment(json_extract(body,'$.age')) FROM r WHERE id=1").Rows[0][0],26L))throw new Exception("Packaged C# function");
}
using(var c=new PureDbConnection("Data Source="+path+";Access=Exclusive")) {
 c.CreateFunction<string,bool>("is_target",value=>value=="ok");
 c.Open(); using var tx=c.BeginTransaction();using var command=c.CreateCommand();command.Transaction=tx;
 command.CommandText="DELETE FROM r WHERE id=1 AND is_target('ok')";if(command.ExecuteNonQuery()!=1)throw new Exception("ADO delete/function");tx.Commit();
}
using(var db=new SqlDatabase(path))if(db.Query("SELECT id FROM r").Rows.Count!=0)throw new Exception("Reopen");
foreach(var a in new[]{typeof(Database).Assembly,Assembly.Load("PureDb.SqliteCompatibility")}) {
 if(a.GetTypes().SelectMany(t=>t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance)).Any(m=>(m.Attributes&MethodAttributes.PinvokeImpl)!=0))throw new Exception("Unexpected native import");
}
Console.WriteLine("PASS actual preview packages: JSON/CRUD/ADO transaction/reopen; no native imports");
''',encoding='utf-8')
subprocess.run(['dotnet','restore',str(p/'Smoke.csproj'),'--source',str(root/'artifacts/packages'),'--packages',str(p/'packages')],check=True)
subprocess.run(['dotnet','run','-c','Release','--no-restore','--project',str(p/'Smoke.csproj')],check=True)
