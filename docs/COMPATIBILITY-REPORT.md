# SQL 与 SQLite 文件兼容性验收报告

以下冻结于 preview.3。当前 rc.1 已验证的 SQL 数值 / 运算语义、66 个文件 profile 及稳定性证据见 [发布评估](PRODUCTION-READINESS.md)；当前语法边界见 [SQL 清单](SQL-SUPPORT.md)。

版本 `1.0.0-preview.3`；SQLite 3.54.1；Windows x64 与指定 Linux ARM64 主机；.NET 10.0.13。两类兼容性分别验收。

本报告的通过率分母是公开、固定、可复现的测试用例，不是 SQLite 全部语法或真实世界全部文件。“达到 95%”仅能在明确分母内检验；当前不宣称整体 SQLite 兼容性达到 95%。

## 分项结果

| 平台 | 范围 | 通过 | 总数 | 通过率 |
| --- | --- | --- | --- | --- |
| Windows x64 | SQL 固定公共语料 | 175 | 175 | 100.00% |
| Windows x64 | 文件正向 profile / 故障注入 | 64 | 64 | 100.00% |
| Linux ARM64 | SQL 固定公共语料 | 175 | 175 | 100.00% |
| Linux ARM64 | 文件正向 profile / 故障注入 | 64 | 64 | 100.00% |

SQL 语料 SHA256：`5D8E0C5FB5FCFC638965ACE2E750A8BD3B2FA75743DF3DBC37835FE179FCC325`。两平台必须使用相同语料。

## SQL 覆盖与缺口

| 家族 | 通过 / 总数 |
| --- | --- |
| numeric | 21 / 21 |
| null-and-lazy | 18 / 18 |
| cast | 55 / 55 |
| text | 34 / 34 |
| json | 13 / 13 |
| select | 8 / 8 |
| aggregate | 8 / 8 |
| join | 4 / 4 |
| unicode-order | 1 / 1 |
| group | 1 / 1 |
| compound | 3 / 3 |
| cte | 2 / 2 |
| subquery | 2 / 2 |
| window | 3 / 3 |
| json-table | 2 / 2 |

该语料比较结果行、NULL、整数/实数存储类别及 BLOB 字节；实数使用相对误差 1e-13。另有 524 条既有差分及 77 组回归覆盖排序、事务、保存点、约束、用户函数、多进程和恢复；不把 AOT 重复运行计为新增 SQL 覆盖率。

SQL 仍有缺口：完整类型亲和性、隐式 rowid、ALTER TABLE、RETURNING、INSERT OR、聚合 DISTINCT/FILTER、日期时间、COLLATE、NATURAL/USING、自定义聚合/窗口/表值函数及大量 SQLite 内置函数。三参数 IIF 不能覆盖新版 SQLite 的可变参数形式；Unicode 大小写和 LIKE 的部分语义仍有差异。详见 [逐项语法清单](SQL-SUPPORT.md)。

## 文件覆盖与缺口

64 组由 8 组模式互通、24 组页大小/编码组合、16 组跨引擎进程强杀恢复、16 组增长/截短的稀疏日志恢复构成。每组含多项断言，不把断言数当作独立兼容特性百分比。

原生 SQLite 建库 → 托管读写 → 原生 integrity_check；模式组再导出、原生校验、导入原版。编码组覆盖 BMP、补充平面字符、BINARY 排序、HEX/CAST/OCTET_LENGTH 与嵌套 C# 用户函数作用域；恢复组覆盖日志刷盘后、首个数据页后、数据刷盘后和提交后，分别由原生和托管恢复。

文件仍拒绝 WAL、auto_vacuum、非零保留字节、AUTOINCREMENT/内部统计表、WITHOUT ROWID、任意类型、FTS/RTree/虚拟表及未实现热日志协议。兼容连接独占；Linux 要专用进程。完整镜像回退仍具有随库大小增长的 CPU/内存/读取成本。通过 64/64 不能外推成任意 SQLite 文件可用。详见 [文件支持范围](SQLITE-COMPATIBILITY.md)。

## 复现

```bash
python scripts/prepare-sqlite.py
dotnet build PureDb.slnx -c Release
dotnet tests/PureDb.SqliteParityTests/bin/Release/net10.0/PureDb.SqliteParityTests.dll
# --sql-only 仅 SQL；--files-only 仅文件；测试库均自动生成
```

[测试源码](../tests/PureDb.SqliteParityTests) · [各平台原始逐项报告](../benchmarks/results/compatibility/latest) · [SQLite 文件协议](https://www.sqlite.org/fileformat.html) · [SQLite 标量函数](https://www.sqlite.org/lang_corefunc.html)
