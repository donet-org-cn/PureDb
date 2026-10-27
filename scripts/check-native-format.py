"""Check baseline -> current and current -> baseline native data/index readability."""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument("before", type=Path)
parser.add_argument("after", type=Path)
parser.add_argument("output", type=Path)
args = parser.parse_args()
if os.name == "nt":
    ctypes.windll.kernel32.SetErrorMode(0x8003)
args.output = args.output.resolve()
args.output.mkdir(parents=True, exist_ok=False)
project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
properties = ET.SubElement(project, "PropertyGroup")
for key, value in {"OutputType": "Exe", "TargetFramework": "net10.0", "ImplicitUsings": "enable", "Nullable": "enable"}.items():
    ET.SubElement(properties, key).text = value
reference = ET.SubElement(ET.SubElement(project, "ItemGroup"), "Reference", Include="PureDb")
ET.SubElement(reference, "HintPath").text = str(args.after.resolve())
ET.ElementTree(project).write(args.output/"FormatProbe.csproj", encoding="utf-8", xml_declaration=True)
source = r'''
using PureDb;
string path = args[1];
long Id(int i) => i == 0 ? long.MinValue : i == 1 ? long.MaxValue : (i % 2 == 0 ? 1L : -1L) * i * 1000000007L;
void Check(bool value) { if (!value) throw new InvalidDataException("Cross-version native format mismatch"); }
if (args[0] == "write")
{
    Directory.CreateDirectory(path);
    using (var db = new Database(Path.Combine(path,"kv.pdb"),new(){Durability=Durability.Buffered}))
    {
        await db.WriteAsync(Enumerable.Range(0,20000).Select(i=>Mutation.Put(Id(i),i%17,BitConverter.GetBytes(Id(i)))).ToArray());
        await db.CheckpointAsync(); await db.CompactAsync();
    }
    using var sql = new SqlDatabase(Path.Combine(path,"sql.pdb"),new(){Storage=new(){Durability=Durability.Buffered}});
    await sql.ExecuteAsync("CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER,s TEXT);CREATE INDEX ix ON t(n)");
    await sql.ExecuteTransactionAsync(Enumerable.Range(0,2000).Select(i=>new SqlCommand("INSERT INTO t VALUES(@id,@n,@s)",new Dictionary<string,object?>{["id"]=(long)i,["n"]=Id(i),["s"]="cross-version"})).ToArray());
    await sql.CheckpointAsync(); await sql.CompactAsync();
}
else
{
    foreach (bool verify in new[]{false,true})
    {
        using(var db = new Database(Path.Combine(path,"kv.pdb"),new(){VerifyOnOpen=verify}))
        {
            Check(db.Count==20000); db.VerifyIntegrity();
            for(int i=0;i<20000;i++) Check(BitConverter.ToInt64(db.Get(Id(i))!.Value)==Id(i));
        }
        using var sql=new SqlDatabase(Path.Combine(path,"sql.pdb"),new(){Storage=new(){VerifyOnOpen=verify}});
        Check(Equals(sql.Query("SELECT COUNT(*) FROM t").Rows[0][0],2000L));
        Check(Equals(sql.Query("SELECT n FROM t WHERE id=0").Rows[0][0],long.MinValue));
        Check(Equals(sql.Query("SELECT n FROM t WHERE id=1").Rows[0][0],long.MaxValue));
        Check(sql.Query("SELECT id FROM t WHERE n>=0 ORDER BY n LIMIT 20").Rows.Count==20);
    }
}
Console.WriteLine("PASS native cross-version "+args[0]);
'''
(args.output/"Program.cs").write_text(source, encoding="utf-8")
subprocess.run(["dotnet", "build", str(args.output/"FormatProbe.csproj"), "-c", "Release"], check=True)
binary = args.output/"bin/Release/net10.0"
for name, core in [("before", args.before), ("after", args.after)]:
    host = args.output/name
    shutil.copytree(binary, host)
    shutil.copyfile(core, host/"PureDb.dll")
results = []
for writer, reader in [("before", "after"), ("after", "before")]:
    data = args.output/(writer+"-data")
    for engine, mode in [(writer, "write"), (reader, "read")]:
        subprocess.run(["dotnet", str(args.output/engine/"FormatProbe.dll"), mode, str(data)], check=True)
    results.append({"Writer": writer, "Reader": reader, "KVRows": 20000, "SQLRows": 2000, "LazyAndFullVerification": True, "Passed": True})
report = {"BeforeSHA256": hashlib.sha256(args.before.read_bytes()).hexdigest().upper(), "AfterSHA256": hashlib.sha256(args.after.read_bytes()).hexdigest().upper(), "Cases": results, "Scope": "Native basic KV/SQL data and rebuildable indexes; new SQL schema operators are not promised on older engines"}
(args.output/"result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print("PASS native format in both directions")
