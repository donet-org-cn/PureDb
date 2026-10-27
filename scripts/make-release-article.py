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
if marker in text:text=text[:text.index(marker)].rstrip()
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

from cnblogs_article import draw_architecture, render_intro

draw_architecture(plt, save)
article = render_intro(root, version, current, cr, labels, verdict, table)
(root/'docs/CNBLOGS-preview.3.md').write_text(article, encoding='utf-8', newline='\n')
print('Generated first project introduction, compatibility report and measured figures.')
