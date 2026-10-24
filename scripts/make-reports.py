"""Generate only the latest comparison and README tables from preserved raw samples."""
import csv,datetime,hashlib,json,re,statistics,xml.etree.ElementTree as ET
from pathlib import Path
root=Path(__file__).resolve().parents[1];base=root/'benchmarks/results/latest'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'))
med=statistics.median
version=ET.parse(root/'Directory.Build.props').findtext('.//Version')
labels={'kv_get':'KV 主键命中','kv_miss':'KV 主键未命中','kv_range_20':'KV 范围 20 行','kv_range_1000':'KV 范围 1000 行','sql_point':'SQL 主键完整行','sql_projection':'SQL 主键覆盖投影','sql_range20':'SQL 范围 20 行','sql_range1000':'SQL 范围 1000 行','sql_count':'COUNT(*)','sql_sum_scan':'SUM 扫描','sql_group':'分组计数','sql_join':'INNER JOIN','sql_left_join':'LEFT JOIN','json_index':'JSON 表达式索引','json_scan':'JSON 整数取余计数','json_binary_conversion':'JSONB 转换提取','parallel_get_8':'八线程 KV 读取','reopen_and_first_range':'重开并首次范围查询','cache_limited_2MiB':'2 MiB 页缓存读取','shared_read':'四进程读取','shared_update':'四进程原子更新'}
for mode in ['Full','Buffered']:
 for key,title in {'single':'逐条新增','concurrent8':'八线程新增','batch1000':'批量新增','batch_unique':'唯一索引批量新增','batch_update':'批量更新','batch_delete':'批量删除','update':'逐条更新','delete':'逐条删除','mixed':'一次更新加五次读取','compact':'压缩'}.items():labels[mode+'_'+key]=mode+' '+title
def table(h,rows):return '\n'.join(['| '+' | '.join(h)+' |','| '+' | '.join('---' for _ in h)+' |']+['| '+' | '.join(map(str,r))+' |' for r in rows])
def winner(a,b):
 ratio=max(a,b)/min(a,b)
 return '🟨 接近' if ratio<1.05 else ('🟩 PureDb ' if a<b else '🟥 SQLite ')+f'{ratio:.2f}×'
def higher(a,b,unit,digits=2):
 if abs(a-b)<.5*10**-digits:return '显示精度内相同'
 relative=f'，高 {(max(a,b)/min(a,b)-1)*100:.1f}%' if min(a,b)>0 else ''
 return f'**{"PureDb" if a>b else "SQLite"} 更高**：多 {abs(a-b):,.{digits}f} {unit}{relative}'
parts=['# PureDb 与 SQLite 最新全面对比',f'PureDb `{version}` 原生格式，对照原版 SQLite 3.54.1。每平台 41 场景 × 2 引擎 × 3 轮 = 246 主样本，另有 18 个独立内存、72 个 ADO 和 6 个充分预热样本。只保留最新结果。',
 '主套件串行查询预热至少 500 ms 且至少 64 次，测量至少 750 ms；三轮取中位数，低于 5% 标接近，不是统计等价证明。KV 与 SQL、Core 与 ADO、Full 与 Buffered 分开解释。数据集为 10 万行，缓存与 OS 文件缓存已预热。',
 'PureDb 对原生 SQLite 的 .NET C API 封装，双方实际读取并物化结果；包含托管/原生边界开销。ADO 补充对 SQLite C API，不是对 Microsoft.Data.Sqlite 驱动。每次调用单位见表，批量按行，压缩按次，重开按 opens。',
 '6 项使用 PureDb KV API 对 SQLite SQL：kv_get、kv_miss、kv_range_20、kv_range_1000、parallel_get_8、cache_limited_2MiB。聚合、JOIN 和 JSON 等重复查询可复用按表版本失效的缓存；初建和维护成本可能在计时之外。',
 'Full 是各自强制刷盘路径；Buffered 与 SQLite WAL/NORMAL 的 checkpoint 和断电语义不同。写入计时不含关闭，PureDb 部分索引维护在查询或关闭时执行。重开在同一进程连续执行 5 次，允许有界目录缓存，不代表全新进程冷启动。',
 '[资源占用直观表](RESOURCES.md) · [Windows 交互面板](PERFORMANCE.html) · [复现方法](BENCHMARKS.md)']
resources=['# CPU 与内存：谁更高，高多少',
 'CPU 使用率与每次操作 CPU 时间分开。前者按对应数据库进程及工作子进程 CPU 时间除以计时区间和整机逻辑核数；不包含其他应用。满吞吐测试没有固定相同 QPS，使用率高的一方可能完成更多工作。每次操作 CPU 时间用于比较相同工作量成本。',
 '内存使用独立进程、单个 SQL 库完成操作并 GC 后的 RSS/工作集，包含托管和原生内存；峰值另列。主键访问 10 万行，范围和 JSON 各 1000 次。不能以托管堆大小评价 SQLite 总内存。两边页缓存预算均为 64 MiB；PureDb 还有行缓存和辅助结构。',
 '高多少百分比以较低方为分母；CPU 占用差额用百分点。差额按未舍入数值计算；小差异可能是噪声。Linux 8 核按整机折算，基准绑定 CPU 4–7；原始 .NET CpuMachinePercent 按可用 4 核折算。']
overview=[];summary={'Version':version,'SQLite':'3.54.1','Platforms':{}};all_memory=[];readme=[]
for platform,name in [('Windows x64','windows'),('Linux ARM64','linux')]:
 p=base/name;env=read(p/'environment.json');cpus=env['CPUs']
 reports=[read(f) for f in sorted((p/'raw').glob('*.json')) if f.name.startswith(('Optimized-','SQLite-'))];assert len(reports)==18
 samples=[s for r in reports for s in r['Samples']];assert len(samples)==246
 with (p/'samples.csv').open('w',encoding='utf-8-sig',newline='') as output:
  writer=csv.DictWriter(output,fieldnames=list(dict.fromkeys(k for s in samples for k in s)));writer.writeheader();writer.writerows(samples)
 assert len({r['LibrarySHA256'] for r in reports if r['Engine']=='Optimized'})==1
 metrics={}
 for scenario in dict.fromkeys(s['Scenario'] for s in samples):
  metrics[scenario]={}
  for engine in ['Optimized','SQLite']:
   rows=[s for s in samples if s['Scenario']==scenario and s['Engine']==engine];assert len(rows)==3
   metrics[scenario][engine]={'Us':med(s['Milliseconds']*1000/s['Operations'] for s in rows),'CpuUs':med((s['CpuMilliseconds']+s.get('WorkerCpuMilliseconds',0))*1000/s['Operations'] for s in rows),'CpuMs':med(s['CpuMilliseconds']+s.get('WorkerCpuMilliseconds',0) for s in rows),'CPU':med((s['CpuMilliseconds']+s.get('WorkerCpuMilliseconds',0))/s['Milliseconds']*100/cpus for s in rows),'FileMiB':med(s['StorageAfterBytes']/1048576 for s in rows),'Unit':rows[0]['Unit']}
 wins=sum(v['SQLite']['Us']/v['Optimized']['Us']>=1.05 for v in metrics.values());losses=sum(v['Optimized']['Us']/v['SQLite']['Us']>=1.05 for v in metrics.values());score=[wins,losses,41-wins-losses];overview.append([platform,*score])
 summary['Platforms'][platform]={'Score':score,'CPUs':cpus,'MeasuredCoreSHA256':next(r['LibrarySHA256'] for r in reports if r['Engine']=='Optimized'),'Metrics':metrics}
 parts+=['## '+platform,table(['场景','单位','PureDb µs','SQLite µs','耗时胜方'],[[labels[s]+' · '+s,v['Optimized']['Unit'],f'{v["Optimized"]["Us"]:,.3f}',f'{v["SQLite"]["Us"]:,.3f}',winner(v['Optimized']['Us'],v['SQLite']['Us'])] for s,v in metrics.items()])]
 parts+=[table(['场景','PureDb 文件 MiB','SQLite 文件 MiB'],[[labels[s],f'{v["Optimized"]["FileMiB"]:.3f}',f'{v["SQLite"]["FileMiB"]:.3f}'] for s,v in metrics.items()]),'文件含侧文件，是场景结束时占用而非物理写入字节。查询套件均记录 SQL 文件，KV 行不代表 KV 文件本身体积。进程逻辑 I/O 见原始样本；Linux 定制内核没有 /proc/self/io 时记录 -1，不当成 0。设备计数含全机其他活动，不能归因于数据库进程。']
 resources+=['## '+platform,table(['场景','PureDb CPU %','SQLite CPU %','谁更高、高多少'],[[labels[s],f'{v["Optimized"]["CPU"]:.3f}',f'{v["SQLite"]["CPU"]:.3f}',higher(v['Optimized']['CPU'],v['SQLite']['CPU'],'个百分点',3) if min(v[e]['CpuMs'] for e in v)>=(100 if name=='windows' else 5) else 'CPU 时间太短，不可靠排名'] for s,v in metrics.items()]),table(['场景','每单位','PureDb CPU µs','SQLite CPU µs','谁消耗更多、多多少'],[[labels[s],v['Optimized']['Unit'],f'{v["Optimized"]["CpuUs"]:.3f}',f'{v["SQLite"]["CpuUs"]:.3f}',higher(v['Optimized']['CpuUs'],v['SQLite']['CpuUs'],'µs',3) if min(v[e]['CpuMs'] for e in v)>=(100 if name=='windows' else 5) else 'CPU 时间太短，不可靠排名'] for s,v in metrics.items()])]
 mem=[read(f) for f in (p/'memory').glob('*.json') if f.name.startswith(('Optimized-','SQLite-'))];assert len(mem)==18
 memory_summary=[];rows=[]
 for scenario in ['point','range','json']:
  vals={e:{k:med(r[k] for r in mem if r['Engine']==e and r['Scenario']==scenario) for k in ['WorkingSetBytes','PrivateBytes','ManagedLiveBytes','PeakWorkingSetBytes']} for e in ['Optimized','SQLite']}
  a,b=[vals[e]['WorkingSetBytes']/1048576 for e in ['Optimized','SQLite']]
  rows.append([scenario,f'{a:.2f}',f'{b:.2f}',higher(a,b,'MiB'),f'{vals["Optimized"]["PeakWorkingSetBytes"]/1048576:.2f} / {vals["SQLite"]["PeakWorkingSetBytes"]/1048576:.2f}'])
  for e in vals:memory_summary.append({'Scenario':scenario,'Engine':e,**vals[e]})
 resources+=[table(['独立内存场景','PureDb RSS MiB','SQLite RSS MiB','谁更高、高多少','峰值 MiB：PureDb / SQLite'],rows)]
 (p/'memory/summary.json').write_text(json.dumps(memory_summary,indent=2),encoding='utf-8')
 steady=[read(f) for f in (p/'steady').glob('*.json')];assert len(steady)==6
 sv={e:{'Us':med(r['Milliseconds']*1000/r['Operations'] for r in steady if r['Engine']==e),'CpuUs':med(r['CpuMilliseconds']*1000/r['Operations'] for r in steady if r['Engine']==e),'RSS':med(r['WorkingSetBytes']/1048576 for r in steady if r['Engine']==e)} for e in ['Optimized','SQLite']}
 resources+=['### 全量预热后的随机查询',table(['指标','PureDb','SQLite','谁更高、高多少'],[[title,f'{sv["Optimized"][k]:.3f}',f'{sv["SQLite"][k]:.3f}',higher(sv['Optimized'][k],sv['SQLite'][k],unit,3)] for k,title,unit in [('Us','耗时 µs/查询','µs'),('CpuUs','CPU µs/查询','µs'),('RSS','驻留 MiB','MiB')]]),'先按相同伪随机顺序访问全部 10 万个主键两遍，再测 50 万次完整行 SQL 查询；三轮中位数。与单遍顺序内存测试是不同负载。']
 ado=[read(f) for f in (p/'ado').glob('*.json')];assert len(ado)==9
 ag={}
 for r in ado:
  for s in r['Samples']:ag.setdefault((s['Scenario'],r['Engine']),[]).append(s)
 ar=[]
 for scenario in dict.fromkeys(s for s,e in ag):
  a=[med(r['Milliseconds']*1000/r['Operations'] for r in ag[scenario,e]) for e in ['Core','Ado','SQLiteNative']]
  ar.append([scenario,'µs/千行事务' if scenario=='batch10000' else 'µs/行' if scenario=='insert1000' else 'µs/查询',*[f'{v:,.3f}' for v in a],winner(a[1],a[2])])
 parts+=['### ADO.NET 补充',table(['场景','单位','Core','ADO.NET','SQLite C API','ADO 胜方'],ar)]
 parts.append('### 磁盘观察：全机设备范围')
 io=[]
 if name=='linux':
  def counters(text):
   for line in text.splitlines():
    f=line.split()
    if list(map(int,f[:2]))==env['Device']:return f[2],list(map(int,f[3:]))
   raise ValueError('Missing device')
  for mode in ['queries','writes','processes']:
   for engine in ['Optimized','SQLite']:
    vals=[]
    for repeat in [1,2,3]:
     r=read(p/'logs'/f'{engine}-{mode}-{repeat}.host.json');device,a=counters(r['DiskBefore']);_,b=counters(r['DiskAfter']);d=[y-x for x,y in zip(a,b)];vals.append([d[9]/r['Seconds']/10,d[2]*512/1048576,d[6]*512/1048576])
    io.append([mode,engine,device,*[f'{med(v[i] for v in vals):.2f}' for i in range(3)]])
  parts.append(table(['套件','引擎','设备','设备忙碌率 %','设备读 MiB','设备写 MiB'],io))
 elif (p/'physical/windows.json').exists():
  windows=read(p/'physical/windows.json')
  for r in windows:
   start=datetime.datetime.fromisoformat(r['StartUtc'].replace('Z','+00:00'));end=datetime.datetime.fromisoformat(r['EndUtc'].replace('Z','+00:00'))
   data=[json.loads(line) for line in (p/'physical'/r['CounterFile']).read_text(encoding='utf-8-sig').splitlines() if line.strip()]
   active=[s for s in data if s['Status']==0 and start<=datetime.datetime.fromisoformat(s['Utc'].replace('Z','+00:00'))<=end]
   for device in sorted({s['Instance'] for s in active}):
    def values(suffix):return [s['Value'] for s in active if s['Instance']==device and s['Path'].lower().endswith(suffix)]
    io.append([r['Engine'],device,f'{statistics.mean(max(0,min(100,100-v)) for v in values("% idle time")):.2f}',f'{statistics.mean(values("disk read bytes/sec"))/1048576:.2f}',f'{statistics.mean(values("disk write bytes/sec"))/1048576:.2f}'])
  parts.append(table(['引擎','全机设备','忙碌率 %','读 MiB/s','写 MiB/s'],io))
  parts.append('Windows 物理设备数据为每引擎一次独立写入套件补测，不是三轮主样本的 I/O，也不纳入主成绩。')
 else:
  parts.append('本轮 Windows 未采集物理磁盘设备计数，不沿用旧构建的忙碌率。进程逻辑 I/O 和文件体积仍在本轮样本中。')
 parts.append('设备计数包括系统服务与延迟回写，不等于引擎独占 I/O。Linux 为三轮套件期间 diskstats 增量；若内核未提供有效计数则不能排名。进程逻辑读写字节、次数、CPU 和关闭后文件列表保留在 raw JSON。')
 readme+=['### '+platform,table(['项目','PureDb','SQLite','谁更低'],[[labels[s]+'（µs/'+metrics[s]['Optimized']['Unit']+'）',f'{metrics[s]["Optimized"]["Us"]:,.3f}',f'{metrics[s]["SQLite"]["Us"]:,.3f}',winner(metrics[s]['Optimized']['Us'],metrics[s]['SQLite']['Us'])] for s in ['sql_point','sql_range20','sql_join','json_index','json_scan','Full_single','Full_update','Full_delete','Full_batch1000','Full_batch_update','Full_batch_delete','Buffered_batch1000','Buffered_batch_update','Buffered_batch_delete','shared_read','shared_update','reopen_and_first_range']]),table(['内存场景','PureDb RSS MiB','SQLite RSS MiB','谁更高、高多少'],[r[:4] for r in rows])]
 readme.append(table(['主键查询 CPU 指标','PureDb','SQLite','谁更高、高多少'],[['占整机 CPU %',f'{metrics["sql_point"]["Optimized"]["CPU"]:.3f}',f'{metrics["sql_point"]["SQLite"]["CPU"]:.3f}',higher(metrics['sql_point']['Optimized']['CPU'],metrics['sql_point']['SQLite']['CPU'],'个百分点',3)],['每次查询 CPU µs',f'{metrics["sql_point"]["Optimized"]["CpuUs"]:.3f}',f'{metrics["sql_point"]["SQLite"]["CpuUs"]:.3f}',higher(metrics['sql_point']['Optimized']['CpuUs'],metrics['sql_point']['SQLite']['CpuUs'],'µs',3)]]))
 if name=='windows':
  metadata={'Date':env['Date'][:10],'Runtime':reports[0]['Runtime'],'SQLite':reports[0]['SQLite'],'LogicalCPUs':cpus,'LibraryHashes':{'Optimized':[summary['Platforms'][platform]['MeasuredCoreSHA256']]}}
  (p/'summary.json').write_text(json.dumps({'Metadata':metadata},indent=2),encoding='utf-8')
 stamp=f'实测程序集 SHA256：`{summary["Platforms"][platform]["MeasuredCoreSHA256"]}`；.NET {reports[0]["Runtime"]}；整机 {cpus} 核；[原始结果](../benchmarks/results/latest/{name})。'
 parts.append(stamp);resources.append(stamp)
parts.insert(2,table(['平台','PureDb 耗时胜','SQLite 耗时胜','接近'],overview))
readme.insert(0,table(['平台','PureDb 耗时胜','SQLite 耗时胜','接近'],overview))
readme.insert(0,f'主分支 `{version}` 开发构建原生格式 vs SQLite 3.54.1，同机三轮中位数。该版本尚未发布至 NuGet。CPU 为满吞吐进程占用，内存为单库独立测量。使用率高不代表每次查询更费 CPU。以下是代表场景，全部 41 项、ADO、CPU 和文件数据见完整报告。')
parts+=['## 解释和限制','精确的 SQLite 计划保存在最新结果目录。CPU、内存、耗时没有全面领先；胜负计数不是业务加权总分。Full/Buffered、关闭维护、缓存初建、接口与 SQL 支持范围会影响结果。测量为同版本源码的本地预览版构建；测后空白格式化的方法 IL、签名及类型属性指纹一致，DLL 哈希变化见 [构建一致性记录](../benchmarks/results/optimization/latest/build-equivalence.json)。GitHub 从提交重新构建时，路径和构建元数据也可能改变 DLL 哈希。']
for name,items in [('PERFORMANCE.md',parts),('RESOURCES.md',resources)]:
 (root/'docs'/name).write_text('\n\n'.join(items)+'\n',encoding='utf-8-sig')
(base/'summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
p=root/'README.md';text=p.read_text(encoding='utf-8-sig');text=re.sub(r'<!-- PERFORMANCE:START -->.*?<!-- PERFORMANCE:END -->','<!-- PERFORMANCE:START -->\n\n'+'\n\n'.join(readme)+'\n\n<!-- PERFORMANCE:END -->',text,flags=re.S);p.write_text(text,encoding='utf-8-sig')
print('Generated latest comparison: 492 main, 36 memory, 144 ADO and 12 steady samples.')
