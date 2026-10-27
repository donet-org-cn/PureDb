"""Validate actual package identities, release version, license/readme, and managed-only deps."""
import hashlib,json,xml.etree.ElementTree as ET,zipfile
from pathlib import Path
root=Path(__file__).resolve().parents[1];version=ET.parse(root/'Directory.Build.props').findtext('.//Version')
rows=[]
for name in ['PureDb','PureDb.SqliteCompatibility']:
 p=root/'artifacts/packages'/(name+'.'+version+'.nupkg')
 with zipfile.ZipFile(p) as z:
  tree=ET.fromstring(z.read(name+'.nuspec'));ns={'n':tree.tag.split('}')[0].lstrip('{')}
  meta=tree.find('n:metadata',ns)
  assert meta.findtext('n:id',namespaces=ns)==name
  assert meta.findtext('n:version',namespaces=ns)==version
  assert meta.findtext('n:license',namespaces=ns)=='MIT'
  assert meta.findtext('n:readme',namespaces=ns)=='README.md'
  assert {'README.md','LICENSE','THIRD-PARTY-NOTICES.md'}<=set(z.namelist())
  assert 'lib/net10.0/'+name+'.dll' in z.namelist()
  documentation=ET.fromstring(z.read('lib/net10.0/'+name+'.xml'))
  assert any('\u4e00'<=c<='\u9fff' for c in ''.join(documentation.itertext())), 'Missing Chinese XML documentation'
  assert all(len(m.findall('summary'))<=1 for m in documentation.findall('./members/member')), 'Duplicate XML summaries in a partial type'
  deps=tree.findall('.//n:dependency',ns)
  if name=='PureDb':assert not deps
  else:assert len(deps)==1 and deps[0].get('id')=='PureDb' and version in deps[0].get('version','')
  assert not any('/native/' in n or n.endswith(('.so','.dylib','.exe')) for n in z.namelist())
 rows.append({'Package':p.name,'SHA256':hashlib.sha256(p.read_bytes()).hexdigest().upper()})
(root/'artifacts/packages/checksums.json').write_text(json.dumps(rows,indent=2))
print('PASS two managed-only packages, identities/dependencies/MIT/readme:',version)
