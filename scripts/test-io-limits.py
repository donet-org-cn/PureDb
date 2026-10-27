"""Inject a real Linux EFBIG write failure in a child, without filling any disk."""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import uuid

parser = argparse.ArgumentParser()
parser.add_argument("--host", type=Path, default=Path("tests/PureDb.StorageTests/bin/Release/net10.0/PureDb.StorageTests.dll"))
parser.add_argument("--output", type=Path)
args = parser.parse_args()
if not sys.platform.startswith("linux"):
    print("SKIP OS file-limit injection: requires Linux RLIMIT_FSIZE")
    raise SystemExit(0)
import resource
folder = (args.output or Path("artifacts")/("io-limits-"+uuid.uuid4().hex)).resolve()
folder.mkdir(parents=True, exist_ok=False)
database = folder/"limited.pdb"
limit = 8 * 1024 * 1024
def constrain_child():
    signal.signal(signal.SIGXFSZ, signal.SIG_IGN)
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
process = subprocess.Popen(["dotnet", str(args.host.resolve()), "--io-limit", "write", str(database)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, preexec_fn=constrain_child)
try:
    first = process.stdout.readline()
    if first.strip() != "READY": raise RuntimeError("Write-limit child did not initialize: "+first)
    resource.prlimit(process.pid, resource.RLIMIT_FSIZE, (limit, limit))
    transcript, _ = process.communicate("GO\n", timeout=60)
    (folder/"write.log").write_text(first+transcript, encoding="utf-8")
    if process.returncode: raise subprocess.CalledProcessError(process.returncode, process.args)
finally:
    if process.poll() is None: process.kill(); process.wait()
partial = database.stat().st_size
assert partial == limit, partial
with (folder/"recovery.log").open("w", encoding="utf-8") as log:
    subprocess.run(["dotnet", str(args.host.resolve()), "--io-limit", "read", str(database)], stdout=log, stderr=subprocess.STDOUT, check=True)
report = {"Scope": "Real Linux EFBIG during a partially written transaction, not physical power loss or actual ENOSPC", "LimitBytes": limit, "PartialFileBytes": partial, "AcknowledgedPrefixPreserved": True, "PartialTransactionRejected": True, "FaultedWriterRejectedNextCommit": True, "ReopenIntegrityAndNextCommit": True}
(folder/"result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
print("PASS real OS write failure and acknowledged-prefix recovery:", folder)
