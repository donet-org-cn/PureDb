"""Run the identical scale host serially with baseline/current cores and SQLite."""
import argparse
import ctypes
import os
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument("before", type=Path)
parser.add_argument("after", type=Path)
parser.add_argument("output", type=Path)
parser.add_argument("--rows", type=int, nargs="+", default=[100000, 1000000])
parser.add_argument("--rounds", type=int, default=3)
args = parser.parse_args()
if os.name == "nt":
    ctypes.windll.kernel32.SetErrorMode(0x8003)
args.output.mkdir(parents=True, exist_ok=True)
for rows in args.rows:
    for repeat in range(1, args.rounds + 1):
        choices = [("before", args.before, "PureDb"), ("after", args.after, "PureDb"), ("sqlite", args.after, "SQLite")]
        # 轮换运行顺序，所有引擎使用相同宿主、事务粒度和新建文件。
        rotation = (repeat - 1) % len(choices)
        for name, host, engine in choices[rotation:] + choices[:rotation]:
            result = args.output / f"{rows}-{name}-{repeat}.json"
            if result.exists():
                raise SystemExit(f"Refusing to overwrite evidence: {result}")
            command = ["dotnet", str(host / "PureDb.ScaleBenchmarks.dll"), engine, str(rows), str(result), str(args.output / "data")]
            print("RUN", name, rows, repeat, flush=True)
            with result.with_suffix(".log").open("w", encoding="utf-8") as log:
                subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=900)
            print(result.with_suffix(".log").read_text(encoding="utf-8"), flush=True)
