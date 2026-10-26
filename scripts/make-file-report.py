"""公开 SQLite 文件模式专项的全部前后/原生结果，严格区分原版内核。"""
from pathlib import Path
import json,statistics
root=Path(__file__).resolve().parents[1];base=root/'benchmarks/results/compatibility/latest'
read=lambda p:json.loads(p.read_text(encoding='utf-8-sig'));med=statistics.median
table=lambda h,rs:'\n'.join(['| '+' | '.join(h)+' |','| '+' | '.join(['---']*len(h))+' |']+['| '+' | '.join(map(str,r))+' |' for r in rs])
lines=['# SQLite 原地文件读写：独立性能专项',
 'PureDb 1.0.0-preview.3 的可选纯 .NET 文件后端，与已发布 preview.2 和原生 SQLite 3.54.1 对比。这里不使用原版格式的成绩。',
 '固定 2000 行、256 字节 BLOB、4096 字节页、UTF-8、单列整数索引。SQLite DELETE / synchronous=FULL / EXCLUSIVE，兼容模式也强制刷盘且独占。相同宿主，仅替换前后两个库；三轮交替顺序中位数。查询热身 64 次、计时至少 700ms；逐条写 10 行、批量写 1000 行，重开 31 次。这里的短 CPU 和写入区间可能有较大噪声；不是大库增长曲线。']
summary={}
for key,platform in [('windows','Windows x64'),('linux','Linux ARM64')]:
 p=base/key/'performance';env=read(p/'environment.json');summary[platform]={};rows=[];resource=[]
 def metrics(stage,engine,scenario):
  rs=[read(f) for f in (p/stage).glob(f'{engine}-{scenario}-*.json')];assert len(rs)==3
  return {'Us':med(r['MicrosecondsPerOperation'] for r in rs),'CpuUs':med(r['CpuMilliseconds']*1000/r['Operations'] for r in rs),'CpuReliable':min(r['CpuMilliseconds'] for r in rs)>=(100 if key=='windows' else 5),
          'RSSMiB':med(r['WorkingSet64']/1048576 for r in rs),'WriteBytesPerOperation':med(r['IO']['WriteBytes']/r['Operations'] for r in rs),
          'AllocatedBytesPerOperation':med(r['AllocatedBytes']/r['Operations'] for r in rs)}
 for scenario in ['point','range20','count','reopen','single_update','batch1000']:
  v={label:metrics(stage,engine,scenario) for label,stage,engine in [('Before','before','Compatibility'),('After','after','Compatibility'),('Incremental','after','Incremental'),('SQLite','after','SQLite')]};summary[platform][scenario]=v
  a,b=v['After']['Us'],v['SQLite']['Us'];winner='接近' if max(a,b)/min(a,b)<1.05 else f'PureDb {b/a:.2f}×' if a<b else f'SQLite {a/b:.2f}×'
  rows.append([scenario,*[f'{v[e]["Us"]:,.3f}' for e in ['Before','After','Incremental','SQLite']],winner,f'{v["Before"]["Us"]/a:.2f}×'])
  for label in ['After','Incremental','SQLite']:
   d=v[label];resource.append([scenario,label,f'{d["CpuUs"]:,.3f}' if d['CpuReliable'] else '短区间，不可靠',f'{d["RSSMiB"]:.2f}',f'{d["WriteBytesPerOperation"]:,.1f}' if d['WriteBytesPerOperation']>=0 else '不可用'])
 lines += ['## '+platform,table(['场景','旧兼容 µs/次或行','新默认 µs/次或行','新实验增量 µs/次或行','SQLite µs/次或行','默认模式谁更快','默认前后倍率'],rows),
           table(['场景','后端','CPU µs/次或行','套件结束 RSS MiB','进程逻辑写入 B/次或行'],resource),
           'RSS 是各独立专项进程计时结束的工作集，未做稳态单库 GC 对齐；只用作本专项观察，不与主报告的隔离内存表混用。逻辑写入不是设备物理写入，Linux 缺失计数为不可用。',
           '相同宿主 SHA256：`'+env['Hosts']['after']+'`；前后核心及兼容库 SHA256 保存在本平台原始 environment.json。']
lines += ['## 结论与边界',
 '部分旧页日志减少镜像回退中的日志备份量，但默认路径仍构建和比较完整镜像，点写并未成为通用增量 B-tree 更新。实验路径只支持已实现的页操作，不能当作所有 SQL 都能增量。原生 SQLite 在写入和大库文件协议方面仍有明显优势；不要因为原版热查询领先就替换任意现有 SQLite 工作负载。',
 '[原始样本](../benchmarks/results/compatibility/latest) · [测试脚本](../scripts/run-file-pair.py) · [兼容范围](SQLITE-COMPATIBILITY.md)']
(base/'file-performance-summary.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
(root/'docs/SQLITE-FILE-PERFORMANCE.md').write_text('\n\n'.join(lines)+'\n',encoding='utf-8')
print('Generated all 144 file-mode samples, four variants, six scenarios, two platforms.')
