"""Publish and run the seven AOT feature groups against the actual local NuGet packages."""
import argparse,pathlib,subprocess,uuid,xml.etree.ElementTree as ET,os
root=pathlib.Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser();p.add_argument('--rid',required=True);a=p.parse_args()
version=ET.parse(root/'Directory.Build.props').findtext('.//Version')
folder=root/'artifacts'/('package-aot-'+uuid.uuid4().hex);folder.mkdir()
project=folder/'PackagedAot.csproj'
project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><PublishAot>true</PublishAot><IsAotCompatible>true</IsAotCompatible><RuntimeFrameworkVersion>10.0.13</RuntimeFrameworkVersion></PropertyGroup><ItemGroup>'+''.join('<PackageReference Include="'+name+'" Version="'+version+'" />' for name in ['PureDb','PureDb.SqliteCompatibility'])+'<Compile Include="'+str(root/'tests/PureDb.AotTests/Program.cs')+'" /></ItemGroup></Project>',encoding='utf-8')
config=folder/'NuGet.Config'
settings=ET.Element('configuration');sources=ET.SubElement(settings,'packageSources');ET.SubElement(sources,'clear')
ET.SubElement(sources,'add',key='local',value=str(root/'artifacts/packages'))
ET.SubElement(sources,'add',key='nuget.org',value='https://api.nuget.org/v3/index.json')
ET.ElementTree(settings).write(config,encoding='utf-8',xml_declaration=True)
subprocess.run(['dotnet','restore',str(project),'-r',a.rid,'--configfile',str(config),'--packages',str(folder/'packages')],check=True)
subprocess.run(['dotnet','publish',str(project),'-c','Release','-r',a.rid,'--no-restore','-o',str(folder/'published'),'-p:IlcOptimizationPreference=Speed','-p:StripSymbols=true'],check=True)
exe=folder/'published'/('PackagedAot.exe' if a.rid.startswith('win') else 'PackagedAot')
result=subprocess.check_output([str(exe)],cwd=folder,text=True,encoding='utf-8',errors='replace')
assert 'PASS 7 AOT feature groups; dynamic code=False' in result,result
print('PASS actual NuGet Native AOT: '+version+' '+a.rid+'; 7 feature groups, core and managed compatibility')
