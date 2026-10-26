"""Generate the deployment report from published-file manifests and actual AOT/JIT processes."""
import json,statistics
from pathlib import Path

root=Path(__file__).resolve().parents[1];base=root/'benchmarks/results/aot/latest'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
med=statistics.median
def table(headers,rows):
 return '\n'.join(['| '+' | '.join(headers)+' |','| '+' | '.join(['---']*len(headers))+' |']+['| '+' | '.join(map(str,r))+' |' for r in rows])
def winner(p,s):
 ratio=max(p,s)/min(p,s)
 return '🟨 接近' if ratio<1.05 else f'🟩 PureDb {s/p:.2f}×' if p<s else f'🟥 SQLite {p/s:.2f}×'
def higher(p,s):
 return f'**{"PureDb" if p>s else "SQLite"} 更高**：多 {abs(p-s):.2f} MiB，高 {(max(p,s)/min(p,s)-1)*100:.1f}%'
lines=['# Native AOT：文件大小、启动、CPU、内存和 SQLite 对比',
 '版本：`1.0.0-preview.3`。Windows x64 与指定 Linux ARM64 主机，.NET SDK 10.0.402 / 运行时 10.0.13。AOT 和自包含 JIT 使用相同源码、相同数据、相同 SQL 和相同适配层；每台机器串行、三轮交替顺序，不能跨平台比较绝对耗时。',
 '## 实际支持与验证',
 '核心与可选 SQLite 兼容库启用 IsAotCompatible；内置 JSON 及兼容目录序列化改用源生成元数据，ADO.NET GetFieldType 保留所需的标量类型元数据。每个平台真正发布并运行 AOT，报告验证 DynamicCodeSupported=false；编译及裁剪警告为 0。额外运行 7 组 AOT 功能测试和 230 条 SQLite 差分查询，覆盖 JSON 修改、模式重开、CHECK/UPSERT、CTE/窗口/子查询、外键/触发器/保存点、ADO.NET 模式表和纯托管 SQLite 文件交换。',
 'GetSchemaTable 有一处精确限定的 IL2111 抑制：DataColumn 保存 Type 对象，不会访问 Type.TypeInitializer；该路径通过实际 AOT 运行验证。没有关闭全局裁剪分析或全局屏蔽 AOT 警告。这里的零警告指本轮完整发布日志未产生 IL 警告，不表示应用可以任意使用反射。',
 '## 对比口径',
 '- 应用文件大小是本次 SQL 测量宿主的实际发布闭包，不是一个通用的“PureDb AOT 库大小”。AOT 只保留应用可达代码，引用更多 API、兼容库或业务代码会改变大小。调试符号和 XML 文档不计入运行文件，但清单另外保留全部文件字节。',
 '- 自包含 JIT 包含 .NET 运行时；框架依赖 JIT 不包含共享运行时，只作体积参考。主性能比较使用自包含 JIT 与 AOT，版本均为 10.0.13。SQLite 宿主独立编译、不引用 PureDb，运行体积计入 SQLite 3.54.1 动态库；系统库不计入双方。',
 '- 10 万行，整数主键、整数索引、未索引排序列、256 字节 BLOB 及 JSON，另有 JSON 表达式索引。查询预热 500 ms，计时至少 750 ms；两边均物化结果行，复用参数与 SQLite prepared statement。PureDb 的表和派生结构可按版本缓存，首次构建不在热查询计时内。',
 '- Full 为各自强制刷盘路径；Buffered 与 SQLite WAL/NORMAL 的 checkpoint 和断电语义不同。逐条写入逐次等待，批量以 1000 行为一个事务；写入计时不含关闭及关闭维护。新增及删除各核对行数。',
 '- CPU µs/单位比较相同工作量；满吞吐 CPU 占用可能相近而完成量不同。RSS 为独立进程、单个库、执行访问后 GC 的驻留量；不能用托管分配判断 SQLite 原生堆大小。短 CPU 区间不排名。',
 '- 新进程启动每组合 21 次，OS 文件缓存已热。全进程耗时由父进程测量，包含进程创建、运行时、库加载、查询、JSON 报告输出及退出；内部打开/首次查询/关闭另列。不是断电后或清空 OS 缓存的冷盘启动。',
 '- 编译使用 Release、IlcOptimizationPreference=Speed，保留正常全球化，未使用仅适用于当前 CPU 的 native 指令集选项。CI 只检查正确性，CI 机器不参与性能排名。',
 '## 本轮实现优化',
 '本轮主要优化 AST 身份计划缓存、小整数装箱、全列顺序投影、JSON 表达式规范化缓存和检查点归并。兼容库同步增加页大小、编码及持久模式；生产代码仍为纯托管。详细前后对照见 [优化报告](OPTIMIZATION.md)。本报告重新发布并实测 preview.3，不复用 preview.2 的 AOT 体积或耗时。']
summary={'Version':'1.0.0-preview.3','Platforms':{}};readme=[]
for key,platform in [('windows','Windows x64'),('linux','Linux ARM64')]:
 p=base/key;environment=read(p/'environment.json');manifest=environment['Manifest'];cpus=environment['CPUs']
 variants={(v['Engine'],v['Mode']):v for v in manifest['Variants']};assert len(variants)==6
 lines+=['## '+platform,'### 实际发布文件体积',table(['引擎','部署方式','主程序 MiB','运行文件合计 MiB','含符号及文档 MiB','说明'],
 [[e,m,f'{v["ExecutableBytes"]/1048576:.3f}',f'{v["RuntimeBytes"]/1048576:.3f}',f'{v["AllPublishedBytes"]/1048576:.3f}',
   '不含共享 .NET 运行时' if m=='framework' else '含 .NET 运行时' if m=='jit' else '含可达运行时与引擎；SQLite 行还含原生库'] for (e,m),v in variants.items()])]
 records=[(f,read(f)) for f in (p/'raw').glob('*.json')];assert len(records)==24
 groups={}
 for path,r in records:
  assert r['DynamicCodeSupported']==('-jit-' in path.name)
  for sample in r['Samples']:
   mode='jit' if r['DynamicCodeSupported'] else 'aot'
   groups.setdefault((sample['Scenario'],r['Engine'],mode),[]).append(sample)
 metrics={}
 for (name,engine,mode),samples in groups.items():
  assert len(samples)==3
  metrics.setdefault(name,{}).setdefault(engine,{})[mode]={
   'Us':med(s['Milliseconds']*1000/s['Operations'] for s in samples),
   'CpuUs':med(s['CpuMilliseconds']*1000/s['Operations'] for s in samples),
   'CpuTotalMs':med(s['CpuMilliseconds'] for s in samples),
   'CpuPercent':med(s['CpuMilliseconds']/s['Milliseconds']*100/cpus for s in samples),
   'AllocatedBytes':med(s['AllocatedBytes']/s['Operations'] for s in samples)}
 lines+=['### 查询与增删改：同引擎 JIT/AOT，以及双方 AOT',table(['场景','PureDb JIT µs','PureDb AOT µs','SQLite JIT µs','SQLite AOT µs','AOT 胜方'],
 [[name,*[f'{v[e][m]["Us"]:,.3f}' for e,m in [('PureDb','jit'),('PureDb','aot'),('SQLite','jit'),('SQLite','aot')]],winner(v['PureDb']['aot']['Us'],v['SQLite']['aot']['Us'])] for name,v in metrics.items()]),
 '普通查询单位为 µs/查询，增删改为 µs/行。AOT 与 JIT 的正反收益都保留，不把更快启动解读为所有热查询更快。',
 '### AOT 的 CPU：每单位成本与满吞吐占用',table(['场景','PureDb CPU µs','SQLite CPU µs','CPU 成本胜方','PureDb / SQLite CPU %'],
 [[name,f'{v["PureDb"]["aot"]["CpuUs"]:,.3f}',f'{v["SQLite"]["aot"]["CpuUs"]:,.3f}',
   winner(v['PureDb']['aot']['CpuUs'],v['SQLite']['aot']['CpuUs']) if min(v[e]['aot']['CpuTotalMs'] for e in v)>=(100 if key=='windows' else 5) else '短区间，不排名',
   f'{v["PureDb"]["aot"]["CpuPercent"]:.3f} / {v["SQLite"]["aot"]["CpuPercent"]:.3f}'] for name,v in metrics.items()])]
 memory={};memoryrows=[]
 for scenario in ['point','range20','json_index']:
  values={}
  for engine in ['PureDb','SQLite']:
   for mode in ['jit','aot']:
    reports=[read(f) for f in (p/'memory').glob(engine+'-'+mode+'-*.json')]
    samples=[r['Samples'][0] for r in reports if r['Samples'][0]['Scenario']==scenario];assert len(samples)==3
    values[engine,mode]={field:med(s[field]/1048576 for s in samples) for field in ['WorkingSetBytes','PrivateBytes','PeakWorkingSetBytes','ManagedLiveBytes']}
  memory[scenario]={e:{m:values[e,m] for m in ['jit','aot']} for e in ['PureDb','SQLite']}
  memoryrows.append([scenario,*[f'{values[e,m]["WorkingSetBytes"]:.2f}' for e,m in [('PureDb','jit'),('PureDb','aot'),('SQLite','jit'),('SQLite','aot')]],higher(values['PureDb','aot']['WorkingSetBytes'],values['SQLite','aot']['WorkingSetBytes'])])
 lines+=['### 独立进程 RSS：谁更高，高多少',table(['场景','PureDb JIT MiB','PureDb AOT MiB','SQLite JIT MiB','SQLite AOT MiB','AOT 当前谁更高'],memoryrows)]
 parent=read(p/'startup/parent.json');assert len(parent)==84
 startup={};startrows=[]
 for engine in ['PureDb','SQLite']:
  startup[engine]={}
  for mode in ['jit','aot']:
   rows=[r for r in parent if r['Engine']==engine and r['Mode']==mode];assert len(rows)==21
   internal=[read(p/'startup'/r['Result'])['Samples'][0]['Milliseconds'] for r in rows]
   startup[engine][mode]={'ProcessMs':med(r['ProcessMilliseconds'] for r in rows),'OpenQueryCloseMs':med(internal)}
   startrows.append([engine,mode,f'{startup[engine][mode]["ProcessMs"]:.3f}',f'{startup[engine][mode]["OpenQueryCloseMs"]:.3f}'])
 lines+=['### 每次新进程启动与首次范围查询',table(['引擎','部署','全进程 ms','内部打开/首查/关闭 ms'],startrows)]
 files=read(p/'fixture-files.json');sizes={e:sum(f['Bytes'] for f in files if f['Path'].startswith(prefix)) for e,prefix in [('PureDb','pure.pdb'),('SQLite','sqlite.sqlite')]}
 lines+=['### 同一 10 万行数据的关闭后数据库文件',table(['PureDb MiB（含侧文件）','SQLite MiB（含侧文件）','谁更小'],[[f'{sizes["PureDb"]/1048576:.3f}',f'{sizes["SQLite"]/1048576:.3f}',winner(sizes['PureDb'],sizes['SQLite'])]]),
 'AOT 改变应用部署方式，数据文件格式相同。这一行只统计最初查询库及其侧文件，不累计各写入场景生成的临时数据库。文件占用不是物理写入流量。']
 wins=sum(v['SQLite']['aot']['Us']/v['PureDb']['aot']['Us']>=1.05 for v in metrics.values())
 losses=sum(v['PureDb']['aot']['Us']/v['SQLite']['aot']['Us']>=1.05 for v in metrics.values())
 summary['Platforms'][platform]={'Metrics':metrics,'Memory':memory,'Startup':startup,'DatabaseBytes':sizes,'AotScore':[wins,losses,len(metrics)-wins-losses]}
 pure=variants['PureDb','aot']['RuntimeBytes']/1048576;sqlite=variants['SQLite','aot']['RuntimeBytes']/1048576
 readme.append([platform,f'{pure:.2f} / {sqlite:.2f}',f'{startup["PureDb"]["jit"]["ProcessMs"]:.2f} → {startup["PureDb"]["aot"]["ProcessMs"]:.2f}',f'{wins} / {losses} / {len(metrics)-wins-losses}'])
 lines+=['### 本平台结论',f'PureDb AOT 的运行文件比自身自包含 JIT 减少 {(1-variants["PureDb","aot"]["RuntimeBytes"]/variants["PureDb","jit"]["RuntimeBytes"])*100:.1f}%；全进程启动中位数由 {startup["PureDb"]["jit"]["ProcessMs"]:.2f} ms 变为 {startup["PureDb"]["aot"]["ProcessMs"]:.2f} ms。AOT 对比 SQLite：{wins} 项耗时领先、{losses} 项落后、{len(metrics)-wins-losses} 项接近。体积、热查询、写入、CPU 与 RSS 仍需分别判断，不能宣称全面领先。',f'[原始清单、各轮样本及环境](../benchmarks/results/aot/latest/{key})。']
lines+=['## 使用已发布包启用 AOT','```xml\n<PropertyGroup>\n  <TargetFramework>net10.0</TargetFramework>\n  <PublishAot>true</PublishAot>\n  <IlcOptimizationPreference>Speed</IlcOptimizationPreference>\n</PropertyGroup>\n```',
 '```bash\ndotnet add package PureDb --version 1.0.0-preview.3\ndotnet publish -c Release -r win-x64\n# Linux ARM64 应在对应 Linux 编译环境发布：\ndotnet publish -c Release -r linux-arm64\n```',
 'Native AOT 是应用发布选项，NuGet 仍分发托管库，主库和可选兼容包均无原生 SQLite 依赖。应用自己的反射或 JSON 模型仍需满足 AOT 要求。官方说明见 [Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) 和 [JSON 源生成](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)。',
 '复现：先运行 `scripts/publish-deployment.py` 发布两种引擎和三种部署方式，再用 `scripts/run-deployment.py` 串行测量；`scripts/make-aot-report.py` 从本轮公开原始结果生成本页。CI 及发布流水线在 Windows x64、Linux x64、Linux ARM64 真正编译和运行 AOT，发布前还以实际 NuGet 包运行 AOT 功能检查。']
(base/'summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
(root/'docs/AOT-PERFORMANCE.md').write_text('\n\n'.join(lines)+'\n',encoding='utf-8-sig',newline='\n')
block='<!-- AOT:START -->\n\n'+table(['平台','AOT 运行文件 MiB：PureDb / SQLite','PureDb 全进程启动 ms：JIT → AOT','AOT 耗时：领先 / 落后 / 接近'],readme)+'\n\nSQLite 体积包含其原生库；AOT 体积是本次应用发布闭包。详细 CPU、RSS、增删改及方法见 [AOT 完整报告](docs/AOT-PERFORMANCE.md)。\n\n<!-- AOT:END -->'
readme_path=root/'README.md';content=readme_path.read_text(encoding='utf-8-sig')
if '<!-- AOT:START -->' in content:
 import re
 content=re.sub(r'<!-- AOT:START -->.*?<!-- AOT:END -->',lambda _:block,content,flags=re.S)
else:content=content.replace('## 主分支 preview.3 排序专项','## Native AOT 发布对比\n\n'+block+'\n\n## 主分支 preview.3 排序专项')
readme_path.write_text(content,encoding='utf-8-sig',newline='\n')
print('Generated AOT comparison: 360 main, 72 memory and 168 fresh-process startup samples.')
