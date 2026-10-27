import os,re,sys,xml.etree.ElementTree as ET
from pathlib import Path
root=Path(__file__).resolve().parents[1]
version=ET.parse(root/'Directory.Build.props').findtext('.//Version')
tag=sys.argv[1] if len(sys.argv)>1 else os.environ.get('GITHUB_REF_NAME','')
if not re.fullmatch(r'\d+\.\d+\.\d+(?:-(?:preview|rc)\.\d+)?',version or ''):raise SystemExit('Expected a stable, rc or preview version')
if tag!='v'+version:raise SystemExit('Tag must equal v'+version)
print('Validated release tag:',tag)
