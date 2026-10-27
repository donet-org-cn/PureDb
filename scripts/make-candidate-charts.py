"""使用可复算 JSON 生成当前候选版性能图；图表不进入产品包。"""
import json
from pathlib import Path
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

root = Path(__file__).resolve().parents[1]
summary = json.loads((root/"benchmarks/results/candidate/latest/summary.json").read_text(encoding="utf-8"))
scale = json.loads((root/"benchmarks/results/scale/latest/summary.json").read_text(encoding="utf-8"))
output = root/"docs/images/rc.1"
output.mkdir(parents=True, exist_ok=True)
plt.rcParams.update({"font.sans-serif": ["Microsoft YaHei", "DejaVu Sans"], "axes.unicode_minus": False, "font.size": 9})
def save(fig, name):
    for ext in ["png", "svg"]:
        target = output/(name+"."+ext)
        fig.savefig(target, dpi=165, bbox_inches="tight", facecolor="white")
        if ext == "svg": target.write_text("\n".join(line.rstrip() for line in target.read_text(encoding="utf-8").splitlines())+"\n", encoding="utf-8")
    plt.close(fig)

fig, axes = plt.subplots(1, 2, figsize=(14, 16), sharey=True)
for ax, (platform, data) in zip(axes, summary["Platforms"].items()):
    names = list(data["Metrics"])
    ratios = [data["Metrics"][name]["Optimized"]["Us"]/data["Metrics"][name]["SQLite"]["Us"] for name in names]
    colors = ["#168455" if ratio < 1/1.05 else "#c44848" if ratio > 1.05 else "#bd9336" for ratio in ratios]
    y = np.arange(len(names))
    ax.barh(y, ratios, color=colors, height=.7)
    ax.set_xscale("log")
    ax.axvline(1, color="#343d47", lw=1)
    ax.set_yticks(y, names)
    ax.set_title(platform)
    ax.set_xlabel("PureDb / SQLite 耗时比，越小越好")
    ax.grid(axis="x", alpha=.2)
    for i, ratio in enumerate(ratios): ax.text(ratio*1.08, i, f"{ratio:.3g}×", va="center", fontsize=7)
    ax.set_xlim(min(ratios)*.5, max(max(ratios)*2.2, 2))
axes[0].invert_yaxis()
fig.suptitle("PureDb rc.1 vs SQLite 3.54.1：全部 41 项\n绿色 PureDb 更快，红色 SQLite 更快；三轮中位数，不同场景不合并为总倍数", y=1.005)
fig.tight_layout()
save(fig, "all-performance")

fig, axes = plt.subplots(2, 2, figsize=(13, 9))
for column, (platform, data) in enumerate(summary["Platforms"].items()):
    names = ["point", "range", "json"]
    x = np.arange(3)
    for engine, shift, label, color in [("Optimized", -.18, "PureDb", "#3278ad"), ("SQLite", .18, "SQLite", "#d38a38")]:
        values = [data["Memory"][name][engine]["WorkingSetBytes"] for name in names]
        bars = axes[0, column].bar(x+shift, values, .36, label=label, color=color)
        axes[0, column].bar_label(bars, fmt="%.1f", padding=3)
    axes[0, column].set_xticks(x, names)
    axes[0, column].set_ylabel("独立进程 RSS MiB")
    axes[0, column].set_title(platform+"：内存越低越好")
    axes[0, column].legend()
    queries = ["sql_point", "sql_range20", "sql_sum_scan", "sql_join", "json_index"]
    ratios = [data["Metrics"][name]["Optimized"]["CpuUs"]/data["Metrics"][name]["SQLite"]["CpuUs"] for name in queries]
    axes[1, column].barh(queries, ratios, color=["#168455" if value < 1 else "#c44848" for value in ratios])
    axes[1, column].set_xscale("log")
    axes[1, column].axvline(1, color="#343d47")
    axes[1, column].set_xlabel("每查询 CPU 时间 PureDb / SQLite，越小越省")
    axes[1, column].grid(axis="x", alpha=.2)
    for i, value in enumerate(ratios): axes[1, column].text(value*1.05, i, f"{value:.3g}×", va="center")
    axes[1, column].set_xlim(min(ratios)*.5, max(1.5, max(ratios)*2))
fig.suptitle("CPU 成本与 RSS 分开：满吞吐 CPU 使用率不等于每次操作 CPU 成本")
fig.tight_layout()
save(fig, "cpu-memory")

fig, axes = plt.subplots(1, 2, figsize=(12, 4.5))
for ax, (platform, data) in zip(axes, scale["Platforms"].items()):
    x = np.arange(2)
    for stage, shift, label, color in [("before", -.25, "优化前 PureDb", "#9ba8b5"), ("after", 0, "当前 PureDb", "#3278ad"), ("sqlite", .25, "SQLite", "#d38a38")]:
        values = [data["Sizes"][str(rows)]["Files"][stage]["TotalFileBytes"]/1048576 for rows in [100000, 1000000]]
        bars = ax.bar(x+shift, values, .25, label=label, color=color)
        ax.bar_label(bars, fmt="%.1f", padding=3, fontsize=8)
    ax.set_xticks(x, ["10 万行", "100 万行"])
    ax.set_title(platform)
    ax.set_ylabel("关闭后主文件及全部侧文件 MiB")
    ax.legend()
fig.suptitle("文件总占用：减少重复索引，主数据日志未改为压缩格式")
fig.tight_layout()
save(fig, "file-size")
print("PASS rc.1 charts generated from full and scale summaries")
