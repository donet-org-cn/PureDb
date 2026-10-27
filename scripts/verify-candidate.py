"""校验当前候选版报告的原始样本、缓存预算、哈希、算术及稳定性证据。"""
import hashlib
import json
from pathlib import Path
import statistics

root=Path(__file__).resolve().parents[1]
read=lambda p:json.loads(p.read_text(encoding="utf-8-sig"))
median=statistics.median
full=root/"benchmarks/results/candidate/latest"
scale=root/"benchmarks/results/scale/latest"
stability=root/"benchmarks/results/stability/latest"
summary=read(full/"summary.json")
sizes=read(scale/"summary.json")
hashes=set()
for key,platform in [("windows","Windows x64"),("linux","Linux ARM64")]:
    current=summary["Platforms"][platform]
    hashes.add(current["MeasuredCoreSHA256"])
    reports=[read(p) for p in (full/key/"raw").glob("*.json")]
    assert len(reports)==18 and sum(len(r["Samples"]) for r in reports)==246
    assert {r["LibrarySHA256"] for r in reports if r["Engine"]=="Optimized"}=={current["MeasuredCoreSHA256"]}
    for scenario,engines in current["Metrics"].items():
        for engine,metrics in engines.items():
            samples=[s for r in reports for s in r["Samples"] if s["Scenario"]==scenario and s["Engine"]==engine]
            assert len(samples)==3
            expected={"Us":median(s["Milliseconds"]*1000/s["Operations"] for s in samples),"CpuUs":median((s["CpuMilliseconds"]+s.get("WorkerCpuMilliseconds",0))*1000/s["Operations"] for s in samples),"CpuMachinePercent":median((s["CpuMilliseconds"]+s.get("WorkerCpuMilliseconds",0))/s["Milliseconds"]*100/current["CPUs"] for s in samples)}
            for field,value in expected.items():assert abs(value-metrics[field])<1e-8,(platform,scenario,field)
    for rows in [100000,1000000]:
        records=[read(p) for p in (scale/key).glob(str(rows)+"-*.json")]
        assert len(records)==9 and len({r["HostSHA256"] for r in records})==1
        assert all(r["PageCacheBudgetBytes"]==67108864 and r["AutomaticCheckpointBytes"]==0 and len(r["Samples"])==11 for r in records)
        after_reports=[read(p) for p in (scale/key).glob(f"{rows}-after-*.json")]
        assert len(after_reports)==3 and {r["CoreSHA256"] for r in after_reports}=={current["MeasuredCoreSHA256"]}
        for name,engines in sizes["Platforms"][platform]["Sizes"][str(rows)]["Metrics"].items():
            for stage,metrics in engines.items():
                values=[read(p) for p in (scale/key).glob(f"{rows}-{stage}-*.json")]
                samples=[next(s for s in r["Samples"] if s["Name"]==name) for r in values]
                assert len(samples)==3
                for field in ["Milliseconds","CpuMilliseconds","AllocatedBytes","ManagedBytes","RssBytes","FileBytes"]:
                    assert median(s[field] for s in samples)==metrics[field],(platform,rows,name,stage,field)
    sql=read(stability/key/"sql-parity.json");files=read(stability/key/"file-parity.json")
    assert sql["Total"]==sql["Passed"]==175 and files["Total"]==files["Passed"]==66
    assert read(stability/key/"soak.json")["Operations"]==1000000
    assert read(stability/key/"soak.json")["CoreSHA256"]==current["MeasuredCoreSHA256"]
    aot=read(stability/key/"aot-manifest.json")
    assert aot["AotFeatureGroups"]==7 and aot["AotDifferentialQueries"]==298 and aot["AotWarnings"]==0
    assert "dynamic code=False" in (stability/key/"PureDb.AotTests.txt").read_text(encoding="utf-8")
assert len(hashes)==1
frozen=read(stability/"frozen-core-inputs.json")
assert frozen["AfterCoreSHA256"]==next(iter(hashes))
for name,expected in frozen["SourceSHA256"].items():
    normalized=(root/name).read_text(encoding="utf-8-sig").replace("\r\n","\n")
    assert hashlib.sha256(normalized.encode("utf-8")).hexdigest().upper()==expected,name
native=read(stability/"native-format.json")
assert native["AfterSHA256"]==next(iter(hashes)) and len(native["Cases"])==2 and all(c["Passed"] for c in native["Cases"])
io=read(stability/"linux/io-result.json")
assert io["LimitBytes"]==io["PartialFileBytes"]==8388608 and io["AcknowledgedPrefixPreserved"] and io["PartialTransactionRejected"] and io["ReopenIntegrityAndNextCommit"]
for name in ["all-performance","cpu-memory","file-size"]:
    for extension in ["png","svg"]:assert (root/"docs/images/rc.1"/(name+"."+extension)).stat().st_size>1000
for name in ["RC-PERFORMANCE.md","SCALE-PERFORMANCE.md","PRODUCTION-READINESS.md"]:
    text=(root/"docs"/name).read_text(encoding="utf-8")
    assert "\ufffd" not in text and "???" not in text
print("PASS rc.1: 492 main, 396 scale-stage, actual AOT, two million changes, native format and real EFBIG recovery")
