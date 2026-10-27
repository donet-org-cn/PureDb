"""将当前候选版表格同步到 README，历史专项继续标明其真实版本。"""
import json
from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
summary = json.loads((root/"benchmarks/results/candidate/latest/summary.json").read_text(encoding="utf-8"))
version = summary["Version"]
def table(headers, rows):
    return "\n".join(["| "+" | ".join(headers)+" |", "| "+" | ".join("---" for _ in headers)+" |"]+["| "+" | ".join(map(str,row))+" |" for row in rows])
def winner(a, b):
    ratio=max(a,b)/min(a,b)
    return "🟨 接近" if ratio<1.05 else ("🟩 PureDb " if a<b else "🟥 SQLite ")+f"{ratio:.2f}×"
def higher(a,b):
    return f"**{'PureDb' if a>b else 'SQLite'} 更高**：多 {abs(a-b):.2f} MiB，高 {(max(a,b)/min(a,b)-1)*100:.1f}%"
scenarios={"sql_point":"SQL 主键完整行", "sql_range20":"SQL 范围 20 行", "sql_join":"INNER JOIN", "json_index":"JSON 表达式索引", "Full_single":"Full 逐条新增", "Full_update":"Full 逐条更新", "Full_delete":"Full 逐条删除", "Full_batch1000":"Full 批量新增", "Full_batch_update":"Full 批量更新", "Full_batch_delete":"Full 批量删除", "Buffered_batch1000":"Buffered 批量新增", "Buffered_batch_update":"Buffered 批量更新", "Buffered_batch_delete":"Buffered 批量删除", "Buffered_compact":"Buffered 压缩", "shared_read":"四进程读取", "shared_update":"四进程原子更新", "reopen_and_first_range":"重开并首次范围查询"}
parts=[f"`{version}` 当前冻结原生内核 vs SQLite 3.54.1，同机三轮中位数，差距不足 5% 标为接近。下表包括仍落后的项目，完整 41 项、CPU、RSS、ADO.NET 见 [当前报告](docs/RC-PERFORMANCE.md)，百万行与文件大小见 [规模报告](docs/SCALE-PERFORMANCE.md)。",table(["平台","PureDb 耗时胜","SQLite 耗时胜","接近"],[[platform,*data["Wins"]] for platform,data in summary["Platforms"].items()])]
for platform,data in summary["Platforms"].items():
    parts.append("### "+platform)
    parts.append(table(["场景","单位","PureDb µs","SQLite µs","谁更低"],[[label,data["Metrics"][name]["Optimized"]["Unit"],f'{data["Metrics"][name]["Optimized"]["Us"]:,.3f}',f'{data["Metrics"][name]["SQLite"]["Us"]:,.3f}',winner(data["Metrics"][name]["Optimized"]["Us"],data["Metrics"][name]["SQLite"]["Us"])] for name,label in scenarios.items()]))
    parts.append(table(["独立单库内存场景","PureDb RSS MiB","SQLite RSS MiB","谁更高、高多少"],[[name,f'{values["Optimized"]["WorkingSetBytes"]:.2f}',f'{values["SQLite"]["WorkingSetBytes"]:.2f}',higher(values["Optimized"]["WorkingSetBytes"],values["SQLite"]["WorkingSetBytes"])] for name,values in data["Memory"].items()]))
text=(root/"README.md").read_text(encoding="utf-8-sig")
text=re.sub(r"<!-- PERFORMANCE:START -->.*?<!-- PERFORMANCE:END -->","<!-- PERFORMANCE:START -->\n\n"+"\n\n".join(parts)+"\n\n<!-- PERFORMANCE:END -->",text,flags=re.S)
text=text.replace("## 主分支 preview.3 与 SQLite 全面对比","## 当前 rc.1 与 SQLite 全面对比")
text=text.replace("**下方完整对比对应本轮 `preview.3` 发布构建。** Windows 与 Linux ARM64 使用同一份 DLL；旧的全面对比记录已替换。本轮优化的收益与仍落后项目见 [优化报告](docs/OPTIMIZATION.md)。","下方为当前候选版的真实对照；历史 preview.3 的性能、排序和 AOT 记录保留其原版本标记，不混用为 rc.1 测量。")
(root/"README.md").write_text(text,encoding="utf-8")
print("PASS README current performance synchronized")
