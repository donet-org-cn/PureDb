"""从本轮公开 JSON 生成博客园 Markdown 和真实数据图；绘图仅在生成文档时需要 matplotlib。"""
import collections, hashlib, json, math, os, statistics, sys, xml.etree.ElementTree as ET
from pathlib import Path

root=Path(__file__).resolve().parents[1]
os.environ.setdefault('MPLCONFIGDIR',str(root/'artifacts/mpl-config'))
if (root/'artifacts/plot-lib').exists():sys.path.insert(0,str(root/'artifacts/plot-lib'))
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.font_manager import FontProperties
font=Path('C:/Windows/Fonts/msyh.ttc')
if font.exists():plt.rcParams['font.family']=FontProperties(fname=font).get_name()
plt.rcParams.update({'axes.unicode_minus':False,'svg.hashsalt':'puredb-preview3','font.size':10})
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
version=ET.parse(root/'Directory.Build.props').findtext('.//Version')
base=root/'benchmarks/results'; current=read(base/'latest/summary.json'); paired=read(base/'optimization/latest/summary.json')
images=root/'docs/images/preview.3';images.mkdir(parents=True,exist_ok=True)
table=lambda h,rs:'\n'.join(['| '+' | '.join(h)+' |','| '+' | '.join(['---']*len(h))+' |']+['| '+' | '.join(map(str,r))+' |' for r in rs])
def save(fig,name):
 fig.savefig(images/(name+'.png'),dpi=160,bbox_inches='tight',facecolor='white');svg=images/(name+'.svg');fig.savefig(svg,bbox_inches='tight',metadata={'Date':None});plt.close(fig)
 svg.write_text('\n'.join(line.rstrip() for line in svg.read_text(encoding='utf-8').splitlines())+'\n',encoding='utf-8',newline='\n')
def verdict(a,b):
 return '🟨 接近' if max(a,b)/min(a,b)<1.05 else f'🟩 PureDb {b/a:.2f}×' if a<b else f'🟥 SQLite {a/b:.2f}×'
labels={'kv_get':'KV 主键命中','kv_miss':'KV 主键未命中','kv_range_20':'KV 范围 20 行','kv_range_1000':'KV 范围 1000 行','sql_point':'SQL 主键完整行','sql_projection':'SQL 主键覆盖投影','sql_range20':'SQL 范围 20 行','sql_range1000':'SQL 范围 1000 行','sql_count':'全表计数','sql_sum_scan':'全表求和','sql_group':'分组计数','sql_join':'INNER JOIN','sql_left_join':'LEFT JOIN','json_index':'JSON 表达式索引','json_scan':'JSON 整数取余计数','json_binary_conversion':'JSONB 转换提取','parallel_get_8':'八线程 KV 读取','reopen_and_first_range':'重开与首次范围读取','cache_limited_2MiB':'2 MiB 缓存读取','shared_read':'四进程读取','shared_update':'四进程原子更新'}
for mode in ['Full','Buffered']:
 for name,title in {'single':'逐条新增','concurrent8':'八线程新增','batch1000':'批量新增','batch_unique':'唯一索引批量新增','batch_update':'批量更新','batch_delete':'批量删除','update':'逐条更新','delete':'逐条删除','mixed':'一次更新加五读','compact':'压缩'}.items():labels[mode+'_'+name]=mode+' '+title

names=[('windows','Windows x64'),('linux','Linux ARM64')]; acceptance={};rows=[]
for key,platform in names:
 data=paired['Platforms'][platform];reports=[read(f) for f in (base/'latest'/key/'raw').glob('Optimized-queries-*.json')]
 scenarios=[s['Scenario'] for s in reports[0]['Samples']];assert len(scenarios)==19
 ratios=[data['Before'][s]['Optimized']['Us']/data['After'][s]['Optimized']['Us'] for s in scenarios]
 geo=math.exp(sum(map(math.log,ratios))/len(ratios)); reduction=1-1/geo
 acceptance[platform]={'Scenarios':scenarios,'ThroughputGeomean':geo,'LatencyReduction':reduction,'IndividualRatios':dict(zip(scenarios,ratios)),'MeetsTenPercentLatencyReduction':reduction>=.10}
 rows.append([platform,f'{geo:.3f}×',f'{(geo-1)*100:.2f}%',f'{reduction*100:.2f}%', '✅' if reduction>=.10 else '❌'])
(base/'optimization/latest/acceptance.json').write_text(json.dumps(acceptance,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
opt=root/'docs/OPTIMIZATION.md';text=opt.read_text(encoding='utf-8-sig');marker='## 固定 19 场景验收'
if marker in text:text=text[:text.index(marker)]
text+='\n\n'+marker+'\n\n'+table(['平台','吞吐几何平均倍率','吞吐提升','归一化耗时下降','耗时下降至少 10%'],rows)+'\n\n包含全部固定 19 项读取/重开用例；没有挑掉退化项。阈值不表示每个场景改善 10%，更不代表全部 41 项增删改性能提升 10%。\n'
opt.write_text(text,encoding='utf-8',newline='\n')

# 全部 41 项，无省略失败项；倍率 >1 为 PureDb 更快。
fig,axes=plt.subplots(1,2,figsize=(17,16),sharey=True)
for ax,(key,platform) in zip(axes,names):
 metrics=current['Platforms'][platform]['Metrics'];scenarios=acceptance[platform]['Scenarios']+[s for s in metrics if s not in acceptance[platform]['Scenarios']]; ratios=[metrics[s]['SQLite']['Us']/metrics[s]['Optimized']['Us'] for s in scenarios]
 colors=['#18835b' if r>=1.05 else '#c64545' if r<=1/1.05 else '#9099a1' for r in ratios]
 ax.barh(range(len(ratios)),ratios,color=colors,height=.75);ax.set_xscale('log');ax.axvline(1,color='#273746',lw=1)
 ax.set_yticks(range(len(scenarios)),[labels[s] for s in scenarios]);ax.set_title(platform);ax.grid(axis='x',alpha=.18);ax.set_axisbelow(True)
 for i,r in enumerate(ratios):ax.text(r*1.04,i,f'{r:.2f}×',va='center',fontsize=8)
 ax.set_xlim(min(ratios)*.65,max(ratios)*2);ax.set_xlabel('SQLite 耗时 ÷ PureDb 耗时；>1 PureDb 更快，<1 SQLite 更快')
axes[0].invert_yaxis()
fig.suptitle(f'PureDb {version} vs SQLite 3.54.1：全部 41 场景\n同机三轮中位数；原版格式；绿色 PureDb 胜、红色 SQLite 胜、灰色差距 <5%',fontsize=15,y=.99)
fig.tight_layout(rect=[0,0,1,.96]);save(fig,'all-performance')

fig,axes=plt.subplots(1,2,figsize=(16,9),sharey=True)
for ax,(key,platform) in zip(axes,names):
 item=acceptance[platform];ratios=item['IndividualRatios'];values=[(1-1/r)*100 for r in ratios.values()]
 ax.barh(range(19),values,color=['#18835b' if v>=0 else '#c64545' for v in values]);ax.set_yticks(range(19),[labels[s] for s in ratios]);ax.axvline(0,color='#333333');ax.grid(axis='x',alpha=.2)
 ax.set_title(f'{platform}；耗时几何归一化下降 {item["LatencyReduction"]*100:.2f}%');ax.set_xlabel('单项耗时下降 %；负值为退化')
axes[0].invert_yaxis()
fig.suptitle('preview.2 → preview.3：固定 19 项读取/重开，未剔除退化项',fontsize=15);fig.tight_layout(rect=[0,0,1,.96]);save(fig,'before-after')

fig,axes=plt.subplots(2,2,figsize=(15,10))
chosen=['sql_point','sql_range20','sql_range1000','sql_join','sql_left_join','json_index','json_scan']
for col,(key,platform) in enumerate(names):
 metrics=current['Platforms'][platform]['Metrics'];ax=axes[0,col]
 for offset,engine,color in [(-.18,'Optimized','#18835b'),(.18,'SQLite','#4b78af')]:
  ax.bar([i+offset for i in range(len(chosen))],[metrics[s][engine]['CpuUs'] for s in chosen],width=.36,label='PureDb' if engine=='Optimized' else engine,color=color)
 ax.set_xticks(range(len(chosen)),[labels[s] for s in chosen],rotation=35,ha='right');ax.set_yscale('log');ax.set_ylabel('CPU µs/查询（对数坐标，越低越省 CPU）');ax.set_title(platform);ax.legend();ax.grid(axis='y',alpha=.18)
 memory=[read(f) for f in (base/'latest'/key/'memory').glob('*.json') if f.name!='summary.json'];ax=axes[1,col]
 for offset,engine,color in [(-.18,'Optimized','#18835b'),(.18,'SQLite','#4b78af')]:
  vals=[statistics.median(r['WorkingSetBytes']/1048576 for r in memory if r['Engine']==engine and r['Scenario']==s) for s in ['point','range','json']]
  ax.bar([i+offset for i in range(3)],vals,width=.36,label='PureDb' if engine=='Optimized' else engine,color=color)
  for i,v in enumerate(vals):ax.text(i+offset,v+1,f'{v:.1f}',ha='center',fontsize=9)
 ax.set_xticks(range(3),['主键','范围','JSON']);ax.set_ylabel('独立进程 RSS MiB（越低越省内存）');ax.grid(axis='y',alpha=.18);ax.legend()
fig.suptitle('CPU 成本与内存分开比较：更快不等于 RSS 更小',fontsize=15);fig.tight_layout(rect=[0,0,1,.96]);save(fig,'cpu-memory')

compatlines=['# SQL 与 SQLite 文件兼容性验收报告',f'版本 `{version}`；SQLite 3.54.1；Windows x64 与指定 Linux ARM64 主机；.NET 10.0.13。两类兼容性分别验收。',
 '本报告的通过率分母是公开、固定、可复现的测试用例，不是 SQLite 全部语法或真实世界全部文件。“达到 95%”仅能在明确分母内检验；当前不宣称整体 SQLite 兼容性达到 95%。',
 '## 分项结果']
cr=[];sqlreports={};filereports={}
for key,platform in names:
 s=read(base/'compatibility/latest'/key/'sql-parity.json');f=read(base/'compatibility/latest'/key/'file-parity.json');sqlreports[platform]=s;filereports[platform]=f
 cr += [[platform,'SQL 固定公共语料',s['Passed'],s['Total'],f'{s["Rate"]*100:.2f}%'],[platform,'文件正向 profile / 故障注入',f['Passed'],f['Total'],f'{f["Passed"]/f["Total"]*100:.2f}%']]
compatlines += [table(['平台','范围','通过','总数','通过率'],cr),'SQL 语料 SHA256：`'+sqlreports['Windows x64']['CorpusSHA256']+'`。两平台必须使用相同语料。',
 '## SQL 覆盖与缺口']
assert len({s['CorpusSHA256'] for s in sqlreports.values()})==1
families=collections.defaultdict(lambda:[0,0])
for case in sqlreports['Windows x64']['Cases']:families[case['Family']][0]+=case['Passed'];families[case['Family']][1]+=1
compatlines += [table(['家族','通过 / 总数'],[[family,f'{v[0]} / {v[1]}'] for family,v in families.items()]),
 '该语料比较结果行、NULL、整数/实数存储类别及 BLOB 字节；实数使用相对误差 1e-13。另有 524 条既有差分及 77 组回归覆盖排序、事务、保存点、约束、用户函数、多进程和恢复；不把 AOT 重复运行计为新增 SQL 覆盖率。',
 'SQL 仍有缺口：完整类型亲和性、隐式 rowid、ALTER TABLE、RETURNING、INSERT OR、聚合 DISTINCT/FILTER、日期时间、COLLATE、NATURAL/USING、自定义聚合/窗口/表值函数及大量 SQLite 内置函数。三参数 IIF 不能覆盖新版 SQLite 的可变参数形式；Unicode 大小写和 LIKE 的部分语义仍有差异。详见 [逐项语法清单](SQL-SUPPORT.md)。',
 '## 文件覆盖与缺口',
 '64 组由 8 组模式互通、24 组页大小/编码组合、16 组跨引擎进程强杀恢复、16 组增长/截短的稀疏日志恢复构成。每组含多项断言，不把断言数当作独立兼容特性百分比。',
 '原生 SQLite 建库 → 托管读写 → 原生 integrity_check；模式组再导出、原生校验、导入原版。编码组覆盖 BMP、补充平面字符、BINARY 排序、HEX/CAST/OCTET_LENGTH 与嵌套 C# 用户函数作用域；恢复组覆盖日志刷盘后、首个数据页后、数据刷盘后和提交后，分别由原生和托管恢复。',
 '文件仍拒绝 WAL、auto_vacuum、非零保留字节、AUTOINCREMENT/内部统计表、WITHOUT ROWID、任意类型、FTS/RTree/虚拟表及未实现热日志协议。兼容连接独占；Linux 要专用进程。完整镜像回退仍具有随库大小增长的 CPU/内存/读取成本。通过 64/64 不能外推成任意 SQLite 文件可用。详见 [文件支持范围](SQLITE-COMPATIBILITY.md)。',
 '## 复现',
 '```bash\npython scripts/prepare-sqlite.py\ndotnet build PureDb.slnx -c Release\ndotnet tests/PureDb.SqliteParityTests/bin/Release/net10.0/PureDb.SqliteParityTests.dll\n# --sql-only 仅 SQL；--files-only 仅文件；测试库均自动生成\n```',
 '[测试源码](../tests/PureDb.SqliteParityTests) · [各平台原始逐项报告](../benchmarks/results/compatibility/latest) · [SQLite 文件协议](https://www.sqlite.org/fileformat.html) · [SQLite 标量函数](https://www.sqlite.org/lang_corefunc.html)']
(root/'docs/COMPATIBILITY-REPORT.md').write_text('\n\n'.join(compatlines)+'\n',encoding='utf-8')

article=[f'# 用纯 .NET 10 写一个文件数据库：PureDb {version} 的查询优化、SQLite 兼容与实测',
 'PureDb 是一个 MIT 开源的嵌入式文件数据库：核心和可选 SQLite 兼容包都使用 C# / .NET 10 标准库，不依赖原生 SQLite 运行库。本文记录本次真实优化和测试，也公开仍落后及尚不兼容的部分。',
 '[GitHub 源码](https://github.com/hnlyf168/PureDb) · [NuGet 主库](https://www.nuget.org/packages/PureDb/'+version+') · [可选兼容包](https://www.nuget.org/packages/PureDb.SqliteCompatibility/'+version+')',
 '## 先看结果：原版格式查询变快了多少',table(['平台','吞吐倍率','吞吐提升','归一化耗时下降','至少下降 10%'],rows),
 '以上采用预先保留的固定 19 项读取/重开集合，计算各项三轮中位数比值的几何平均，包含退化项。不是每一项提升 10%，也不是全部增删改提升 10%。旧版是已发布 preview.2，使用同一个新测试宿主，仅替换内核 DLL。',
 '![固定 19 场景优化前后](images/preview.3/before-after.png)',
 '## 与 SQLite 全面对照：所有 41 项都保留',
 '数值是 SQLite 耗时 ÷ PureDb 耗时；大于 1 表示 PureDb 更快，小于 1 表示 SQLite 更快。绿色是 PureDb 领先，红色是 SQLite 领先，灰色为差距小于 5%。这些是原版格式的成绩。',
 '![全部 41 项性能对比](images/preview.3/all-performance.png)']
for key,platform in names:
 metrics=current['Platforms'][platform]['Metrics'];score=current['Platforms'][platform]['Score'];article += ['### '+platform,f'41 项耗时：PureDb 领先 **{score[0]}**、SQLite 领先 **{score[1]}**、接近 **{score[2]}**。胜负计数不是业务加权总分。',table(['场景','PureDb µs/单位','SQLite µs/单位','谁更快'],[[s,f'{metrics[s]["Optimized"]["Us"]:,.3f}',f'{metrics[s]["SQLite"]["Us"]:,.3f}',verdict(metrics[s]['Optimized']['Us'],metrics[s]['SQLite']['Us'])] for s in ['sql_point','sql_range20','sql_join','json_scan','Full_single','Full_batch1000','Full_batch_update','Full_batch_delete','shared_update','reopen_and_first_range']])]
article += ['查询单位为每次查询，逐条与批量写入为每行，重开为每次 open。完整 41 项、ADO.NET、CPU 和文件数据见 [全面报告](PERFORMANCE.md)。',
 '## CPU、内存和落盘：谁高，高多少',
 '![每次查询 CPU 与独立 RSS](images/preview.3/cpu-memory.png)',
 'CPU 使用率是满吞吐时占整机的比例；完成的请求数不同，不能只用这个百分比判断效率。CPU µs/次才是完成相同工作量的 CPU 成本。RSS 是独立进程、单个 SQL 库访问后 GC 的驻留内存，包含托管与原生；SQLite 原生内存不能用 .NET 分配计数代替。']
for key,platform in names:
 metrics=current['Platforms'][platform]['Metrics'];mem=[read(f) for f in (base/'latest'/key/'memory').glob('*.json') if f.name!='summary.json'];mr=[]
 for scenario in ['point','range','json']:
  vals=[statistics.median(r['WorkingSetBytes']/1048576 for r in mem if r['Engine']==e and r['Scenario']==scenario) for e in ['Optimized','SQLite']];a,b=vals
  mr.append([scenario,f'{a:.2f}',f'{b:.2f}',f'{"PureDb" if a>b else "SQLite"} 更高 {abs(a-b):.2f} MiB（{(max(a,b)/min(a,b)-1)*100:.1f}%）'])
 article += ['### '+platform+' 资源',table(['场景','PureDb RSS MiB','SQLite RSS MiB','谁占更多'],mr)]
 a,b=[metrics['sql_point'][e]['CpuUs'] for e in ['Optimized','SQLite']];article += [f'主键完整行每次 CPU：PureDb **{a:.3f} µs**，SQLite **{b:.3f} µs**；{("PureDb" if a>b else "SQLite")} 多消耗 {abs(a-b):.3f} µs（{(max(a,b)/min(a,b)-1)*100:.1f}%）。']
article += ['文件体积含索引和日志侧文件；进程逻辑写入量、设备物理写入量和磁盘忙碌率是不同指标。[资源直观表](RESOURCES.md) 列明 CPU 差额和 RSS 差额，[完整报告](PERFORMANCE.md) 列明各场景文件体积及全机磁盘观察。Linux 内核缺失进程 I/O 计数时记录 -1，不能当作零；全机设备计数也不能全部归因于数据库。',
 '## 这次具体优化了什么',
 '第一，计划缓存从递归 record 哈希改成缓存 AST 的对象身份，加上表、模式等上下文。重复 SQL 命中缓存时少遍历一次语法树，仍在模式或用户函数变化时失效。第二，0–1023 的 long 装箱共享不可变对象，全列投影直接顺序解码，避免逐列定位与回填；BLOB 仍按所有权要求复制。',
 '第三，JSON 派生计数弱缓存规范化表达式，减少每次生成表达式树；它不缓存参数值或最终计数，数据列仍按版本失效，现有 SIMD 路径继续实际计算。第四，检查点复用已排序的基础索引，归并新增部分，避免重新排序全部主键；计数更新减少一次字典定位。',
 '第五，密集 KV 主键的热点页使用有界数组目录，减少字典散列；目录按已存记录数和预算分配，稀疏高位键不扩张数组。第六，相同编码的查询不反复写线程状态，跨 UTF-16 文件与原版 UTF-8 的嵌套用户函数调用仍保存并恢复编码。',
 '原版内核采用追加日志、有界写队列、单写入者分组刷盘、主键与数值范围持久索引。批量写入把多行放进同一个原子帧。查询采用缓存绑定和专用计划，复合索引等结构按版本惰性建立。开库恢复从可信检查点与日志尾部追赶，保留 CRC 和边界检查。',
 '已有 ARM CRC32 和特定 JSON 整数计数 SIMD 并非本轮新增，不能算成这次的 SIMD 收益。分支较多的 B-tree、锁和 SQL 绑定不能仅靠向量化解决。',
 '## 为什么某些查询能比 SQLite 快很多',
 '热查询可复用 PureDb 的已解码行、索引和派生列；主键覆盖投影不读取不需要的 BLOB，COUNT 可以使用维护的计数。SQLite C API 对照要经过原生语句执行及逐行逐列跨边界读取。两边都物化结果，但执行路径和可缓存结构不同，测得的是 .NET 应用调用成本。',
 'PureDb 同时限制主键、类型亲和性和 SQL 子集，SQLite 支持的通用语义更多。追加格式与 B-tree/WAL 的维护和关闭成本也不同。因此，热缓存里几十倍的个别结果不能推导出所有工作负载、冷启动和大库都更快。首次构建缓存、关闭维护、超内存及兼容后端尤其需要另外测量。',
 '## 功能对照',table(['能力','PureDb preview.3','SQLite 3.54.1'],[
 ['纯 .NET','✅ 核心与兼容库纯托管','原生 C 内核，.NET 常用原生绑定'],['ADO.NET / Native AOT','✅ 内置 Provider；真实 AOT 验证','通过所选 .NET Provider'],['复合索引','⚠️ 定义持久化、按版本内存构建','✅ 分页增量 B-tree'],['外键 / CHECK / 视图 / 触发器','⚠️ 已支持公开子集并接入兼容文件','✅ 更完整控制与模式'],['CTE / 窗口 / 子查询 / UNION','⚠️ 已支持，仍有语法和优化边界','✅ 更成熟完整'],['JSON5 / json_each / json_tree','⚠️ 支持；JSONB 为自有格式，节点 ID 有差异','✅ 原生支持'],['C# 用户函数','✅ 标量注册；聚合/窗口回调未支持','原生扩展，经绑定提供 .NET 入口'],['SAVEPOINT / SQL 事务','⚠️ 保存点和乐观事务；锁语义有差异','✅ 原生文件事务与锁协议'],['SQLite 文件原地读写','⚠️ 可选包，受限回滚日志与独占','✅ 原生完整协议'],['WAL / 任意 SQLite 文件','❌ 尚未覆盖','✅ 原生支持']]),
 '## 兼容性目标如何验收',table(['平台','范围','通过','总数','通过率'],cr),
 'SQL 比较固定 175 条公开语料；文件比较 64 组正向 profile 和故障注入。它们分别超过 95% 的用例通过门槛，但**不能写成整体 SQLite 兼容性 95%**。WAL、类型亲和性、AUTOINCREMENT、FTS 等缺项并没有消失。[兼容性报告](COMPATIBILITY-REPORT.md) 公开分母、家族、语料哈希、源码及缺口。',
 '兼容包新增 512–65536 的八种页大小、UTF-8/UTF-16LE/UTF-16BE，以及复合索引、外键、默认值、CHECK、视图和行触发器。修复编码影响的 BINARY 排序；BLOB 默认值和整数形 REAL 常量重开后保持其存储类别，避免 CHECK/TYPEOF 语义变化。',
 '兼容写入保持原地文件句柄及回滚提交顺序。完整镜像路径也仅备份实际覆盖和截断的旧页，减少日志写放大；镜像计算仍随库大小增长，不能承诺大库点写很快。实验增量模式只在已实现的页更新条件成立时生效，其他情况回退。[兼容模式专项](SQLITE-FILE-PERFORMANCE.md) 单独比较，避免拿原版成绩替兼容模式背书。',
 '## Native AOT 的体积、启动与性能',
 '本轮重新发布两个独立宿主，实测 AOT、自包含 JIT 及 SQLite，运行文件体积包含可达运行时；SQLite 体积含其原生库。体积取决于应用可达代码，不是通用的“数据库库大小”。完整清单、CPU/RSS、热查询、写入及新进程启动见 [AOT 报告](AOT-PERFORMANCE.md)。',
 '## 如何使用与复现',
 f'```bash\ndotnet add package PureDb --version {version}\n# 需要受限的 SQLite 文件原地读写才安装：\ndotnet add package PureDb.SqliteCompatibility --version {version}\n```',
 '```csharp\nusing PureDb;\nawait using var db = new SqlDatabase("app.pdb");\nawait db.ExecuteAsync("CREATE TABLE users(id INTEGER PRIMARY KEY,name TEXT)");\nawait db.ExecuteAsync("INSERT INTO users VALUES(1,\'Alice\')");\ndb.CreateFunction<string?,bool>("RELIGIBLE", user => user == "Alice");\nvar eligible = db.Query("SELECT * FROM users WHERE RELIGIBLE(name)");\n```',
 '性能使用 Windows x64 与指定 Linux ARM64 同机串行三轮；.NET SDK 10.0.402 / runtime 10.0.13，SQLite 3.54.1，10 万行热查询，预热 500ms、测量至少 750ms。Full 是各自强制刷盘路径；Buffered 与 SQLite WAL/NORMAL 的检查点和断电语义不同。写入计时不含关闭，不能忽略 PureDb 关闭时可能完成的索引维护。ARM 绑定大核 4–7，两边一致。',
 '复现入口：`scripts/run-release-pair.py`、`scripts/run-file-pair.py`、`scripts/publish-deployment.py`、`scripts/run-deployment.py`。所有公开样本、程序集 SHA256、时间和环境保存在 benchmarks/results；[方法与局限](BENCHMARKS.md) 说明缓存、接口及持久化口径。源码采用中文 XML 注释与格式检查。',
 '当前适合接受明确 SQL 子集、需要纯 .NET 和可控部署的应用验证。需要完整 SQLite 方言、任意现有文件或成熟 WAL 并发时，应继续选 SQLite；纯托管兼容模式仍在推进。',
 '## 博客园排版说明',
 '本文是独立 Markdown 草稿，未代发博客。图片在同目录 `images/preview.3/`，同时提供 PNG 和 SVG；上传博客园时将 PNG 上传到图床，再替换相对图片路径。图由公开 JSON 绘制，非示意数据。']
(root/'docs/CNBLOGS-preview.3.md').write_text('\n\n'.join(article)+'\n',encoding='utf-8')
print('Generated compatibility report, acceptance, Cnblogs article and three PNG/SVG figures.')
