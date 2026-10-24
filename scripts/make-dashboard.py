import csv, html, json, os, statistics, sys
from pathlib import Path

run=Path(sys.argv[1]).resolve(); output=Path(sys.argv[2]).resolve()
summary=json.loads((run/'summary.json').read_text(encoding='utf-8-sig')); meta=summary['Metadata']
groups={}
for path in (p for p in (run/'raw').glob('*.json') if p.name.startswith(('Optimized-','SQLite-'))):
    for r in json.loads(path.read_text(encoding='utf-8-sig'))['Samples']:groups.setdefault((r['Scenario'],r['Engine']),[]).append(r)
names={'kv_get':'键值主键命中','kv_miss':'键值未命中','kv_range_20':'键值范围 20 行','kv_range_1000':'键值范围 1000 行','sql_point':'SQL 主键完整行','sql_projection':'SQL 主键仅 ID','sql_range20':'SQL 范围 20 行','sql_range1000':'SQL 范围 1000 行','sql_count':'全表 COUNT(*)','sql_sum_scan':'全表 SUM 扫描','sql_group':'分组计数','sql_join':'INNER JOIN','sql_left_join':'LEFT JOIN','json_index':'JSON 索引查询','json_scan':'JSON 全表过滤','json_binary_conversion':'JSONB 转换并提取','parallel_get_8':'8 线程键值读取','cache_limited_2MiB':'2 MiB 缓存读取','reopen_and_first_range':'打开数据库并首次查询','shared_read':'4 进程读取','shared_update':'4 进程原子更新'}
writes={'single':'逐条新增','concurrent8':'8 线程新增','batch1000':'批量新增','batch_update':'批量修改','batch_delete':'批量删除','batch_unique':'唯一索引批量新增','update':'逐条修改','delete':'逐条删除','mixed':'1 写 + 5 读','compact':'压缩文件'}
rows=[]
def med(rs,fn):return statistics.median(fn(r) for r in rs)
for scenario in dict.fromkeys(key[0] for key in groups):
    p,s=groups[scenario,'Optimized'],groups[scenario,'SQLite']
    def cpu(rs):return med(rs,lambda r:(r['CpuMilliseconds']+r.get('WorkerCpuMilliseconds',0))*1000/r['Operations'])
    prefix=scenario.split('_',1)[0]
    cat=prefix if prefix in ('Full','Buffered') else 'processes' if scenario.startswith('shared_') else 'sql' if scenario.startswith(('sql_','json_')) else 'kv'
    label=prefix+' / '+writes[scenario.split('_',1)[1]] if prefix in ('Full','Buffered') else names[scenario]
    work=f"{int(p[0]['Operations']):,} {p[0]['Unit']}" if p[0]['Mode']!='queries' or scenario in ('parallel_get_8','reopen_and_first_range') else '自适应次数 / 热缓存'
    meaningful=all(med(rs,lambda r:r['CpuMilliseconds']+r.get('WorkerCpuMilliseconds',0))>=100 for rs in (p,s))
    rows.append(dict(name=label,id=scenario,cat=cat,work=work,timing=[med(rs,lambda r:r['Milliseconds']*1000/r['Operations']) for rs in (p,s)],
                     cpu=[cpu(p),cpu(s)] if meaningful else None,disk=[med(rs,lambda r:r['StorageAfterBytes']/1048576) for rs in (p,s)],unit='µs / '+p[0]['Unit']))
counts=[sum(r['timing'][1]/r['timing'][0]>=1.05 for r in rows),sum(r['timing'][0]/r['timing'][1]>=1.05 for r in rows)]
for prefix in ('Full','Buffered'):
    for suffix,label in writes.items():
        if suffix=='compact':continue
        sizes=[]
        for engine in ('Optimized','SQLite'):
            filename=prefix+'-'+suffix+('.pdb' if engine=='Optimized' else '.sqlite')
            reports=[json.loads(path.read_text(encoding='utf-8-sig')) for path in (run/'raw').glob(engine+'-writes-*.json')]
            sizes.append(statistics.median(sum(f['Bytes'] for f in r['Files'] if f['Path'].startswith(filename)) for r in reports)/1048576)
        state='删除半表并压缩，剩余 5000 行' if suffix=='batch1000' else label+'结束并关闭连接'
        rows.append(dict(name='关闭后 / '+prefix+' / '+label,id='closed_'+prefix+'_'+suffix,cat='files',work=state,closed=sizes))
memory=json.loads((run/'memory/summary.json').read_text(encoding='utf-8-sig'))
for scenario,label in [('point','主键查询后'),('range','范围查询后'),('json','JSON 查询后')]:
    p,s=([r for r in memory if r['Scenario']==scenario and r['Engine']==e][0] for e in ('Optimized','SQLite'))
    rows.append(dict(name='隔离内存 / '+label,id='memory_'+scenario,cat='memory',work='独立进程、单 SQL 数据库',
                     memory=[p['PrivateBytes']/1048576,s['PrivateBytes']/1048576],working=[p['WorkingSetBytes']/1048576,s['WorkingSetBytes']/1048576]))
ado={}
for path in (run/'ado').glob('*.json'):
    for r in json.loads(path.read_text(encoding='utf-8-sig'))['Samples']:ado.setdefault((r['Scenario'],r['Engine']),[]).append(r)
for scenario,label in [('point','主键查询'),('range20','范围 20 行'),('group','分组计数'),('join','JOIN'),('json_scan','JSON 扫描'),('jsonb','JSONB 转换提取'),('insert1000','逐条新增 1000 行'),('batch10000','批量新增 10000 行')]:
    p,s=ado[scenario,'Ado'],ado[scenario,'SQLiteNative']
    cpuvals=[med(rs,lambda r:r['CpuMilliseconds']*1000/r['Operations']) for rs in (p,s)]
    meaningful=all(med(rs,lambda r:r['CpuMilliseconds'])>=100 for rs in (p,s))
    rows.append(dict(name='ADO.NET / '+label,id='ado_'+scenario,cat='ado',work='PureDb ADO.NET vs SQLite C API；不是双 ADO 驱动排名',
                     timing=[med(rs,lambda r:r['Milliseconds']*1000/r['Operations']) for rs in (p,s)],cpu=cpuvals if meaningful else None,
                     unit='µs / '+('1000 行事务' if scenario=='batch10000' else '行' if scenario=='insert1000' else '查询')))
esc=html.escape; rendered=[]; exported=[]
for metric in ['timing','cpu','memory','working','disk','closed']:
    for row in rows:
        if metric not in row:continue
        values=row[metric]; unit='MiB' if metric in ('memory','working','disk','closed') else row['unit'].replace('µs','CPU µs') if metric=='cpu' else row['unit']
        common=f'<tr data-metric="{metric}" data-cat="{row["cat"]}"'
        title='<td>'+esc(row['name'])+'<small>'+esc(row['id']+' · '+row['work'])+'</small></td><td>'+esc(unit)+'</td>'
        if values is None:
            rendered.append(common+' data-score="1">'+title+'<td colspan="5">CPU 未分辨或场景太短，不判胜负</td></tr>');continue
        pure,sqlite=values; low,high=sorted(values); ratio=high/low if low>0 else 1
        near=ratio<1.05; winner='PureDb' if pure<sqlite else 'SQLite'; cls='close' if near else 'good' if pure<sqlite else 'bad'
        badge=('接近；' if near else '')+winner+('略优' if near else '更好')
        cells=''.join(f'<td class="value">{v:,.3f}<div class="bar" style="width:{max(1,v/high*100) if high else 1:.1f}%"></div></td>' for v in values)
        saved=(1-low/high)*100 if high else 0; score=sqlite/pure if pure else 1
        rendered.append(common+f' data-score="{score}">'+title+cells+f'<td><span class="badge {cls}">{badge}</span></td><td>{ratio:.2f}×</td><td>{saved:.1f}%</td></tr>')
        exported.append(dict(Scenario=row['name'],Metric=metric,Unit=unit,PureDb=pure,SQLite=sqlite,Winner=winner,Ratio=ratio,CostReductionPercent=saved))
csvpath=output.with_suffix('.csv')
with csvpath.open('w',encoding='utf-8-sig',newline='') as f:
    writer=csv.DictWriter(f,fieldnames=list(exported[0]));writer.writeheader();writer.writerows(exported)
relative=lambda path:os.path.relpath(path,output.parent).replace(os.sep,'/')
style='''body{margin:0;background:#f2f5f9;color:#19283c;font:15px/1.6 system-ui,"Microsoft YaHei",sans-serif}main{max-width:1500px;margin:28px auto;padding:0 24px}a{color:#175baf}.cards{display:flex;gap:14px;flex-wrap:wrap;margin:22px 0}.card{padding:18px 24px;border:1px solid #d8e1ec;border-radius:12px;flex:1;min-width:160px}.card b{font-size:32px}.good{color:#08784d;background:#e3f5eb}.bad{color:#b42332;background:#fff0f1}.close{color:#866100;background:#fff6d5}.toolbar{display:flex;gap:12px;flex-wrap:wrap;margin:18px 0}select,input{padding:10px;border:1px solid #b9c7d9;border-radius:6px;background:white}table{border-collapse:collapse;width:100%;background:white}th,td{padding:10px 12px;border-bottom:1px solid #e2e8f1;text-align:right}th{background:#e8eef7;position:sticky;top:0}th:first-child,td:first-child{text-align:left;min-width:220px}small{display:block;color:#5b6b80;font-size:12px}.value{min-width:155px}.bar{height:5px;background:#a1b9df;min-width:1px}.badge{display:inline-block;padding:3px 8px;border-radius:5px;white-space:nowrap;font-weight:650}.scroll{overflow:auto;max-height:72vh;border:1px solid #d8e1ec}.note{padding:16px;background:white;border-radius:10px;margin:14px 0}'''
intro=f'''<h1>PureDb 与 SQLite：性能胜负总览</h1><p>{meta['Date']} · .NET {meta['Runtime']} · SQLite {meta['SQLite']} · {meta['LogicalCPUs']} 逻辑 CPU · 每项三轮中位数</p><p><a href="PERFORMANCE.md">完整 Markdown 报告</a> · <a href="SQLITE-COMPATIBILITY.md">SQLite 文件兼容</a> · <a href="ADO.NET.md">ADO.NET 使用说明</a> · <a href="BENCHMARKS.md">启动与 SQLite 文件兼容</a> · <a href="{relative(csvpath)}">完整对比 CSV</a> · <a href="{relative(run/'samples.csv')}">原始样本 CSV</a> · <a href="{relative(run/'summary.json')}">版本与证据</a></p><div class="cards"><div class="card good">PureDb 领先<br><b>{counts[0]}</b> / 41 项</div><div class="card bad">SQLite 领先<br><b>{counts[1]}</b> / 41 项</div><div class="card close">差距小于 5%<br><b>{41-sum(counts)}</b> / 41 项</div></div><div class="note"><b>仍未全面超越 SQLite。</b>绿色表示 PureDb 更好，红色表示 SQLite 更好；黄色仅表示中位数接近，不是显著性证明。上方是主场景计数，不含补充 ADO.NET/内存项，不是业务加权总分。Full 和 Buffered 分开看。</div>'''
controls='''<div class="toolbar"><select id="metric"><option value="timing">耗时 / 操作</option><option value="cpu">CPU 时间 / 操作</option><option value="memory">单库私有内存（独立测量）</option><option value="working">单库工作集（独立测量）</option><option value="disk">操作后文件占用（含 WAL/SHM）</option><option value="closed">关闭连接后的文件占用</option></select><select id="category"><option value="all">全部</option><option value="sql">SQL / JOIN / JSON</option><option value="Full">Full 强制刷盘</option><option value="Buffered">Buffered 缓冲写入</option><option value="processes">四进程</option><option value="kv">键值 / 启动 / 小缓存</option><option value="ado">ADO.NET 补充</option></select><select id="order"><option value="original">默认顺序</option><option value="pure">PureDb 优势最大</option><option value="sqlite">SQLite 优势最大</option></select><input id="search" placeholder="筛选场景，如 新增 / JSON"></div><p id="status"></p><div class="scroll"><table><thead><tr><th>场景 / 工作量</th><th>单位</th><th>PureDb</th><th>SQLite 原生接口</th><th>胜方</th><th>胜方倍率</th><th>胜方成本减少</th></tr></thead><tbody>'''
notes='''</tbody></table></div><div class="note">查询为热缓存；缓存/派生结构的初建可能在预热阶段完成，随数据版本失效。键值、SQL、ADO.NET 是不同接口，已标明；ADO.NET 列对 SQLite C API，不是 Microsoft.Data.Sqlite 驱动对比。CPU 按全部线程求和，短区间或零增量不排名。内存仅用独立进程单数据库结果。文件占用不是物理磁盘写流量。Full/Buffered 持久化机制与其他限制见 Markdown。</div>'''
script='''const rows=Array.from(document.querySelectorAll('tbody tr'));function render(){const m=document.querySelector('#metric').value,c=document.querySelector('#category').value,q=document.querySelector('#search').value.toLowerCase(),o=document.querySelector('#order').value;let n=0;const ordered=rows.slice();if(o!=='original')ordered.sort((a,b)=>o==='pure'?Number(b.dataset.score)-Number(a.dataset.score):Number(a.dataset.score)-Number(b.dataset.score));const body=document.querySelector('tbody');for(const r of ordered){const show=r.dataset.metric===m&&((m==='memory'||m==='working'||m==='closed')||c==='all'||r.dataset.cat===c)&&r.textContent.toLowerCase().includes(q);r.hidden=!show;if(show)n++;body.appendChild(r)}document.querySelector('#status').textContent='显示 '+n+' 项，数值越低越好；查询单位 µs，内存/文件单位 MiB。'}document.querySelectorAll('select,input').forEach(e=>e.addEventListener('input',render));render();'''
intro+='<div class="note"><a href="RESOURCES.md"><b>CPU / 内存谁更高、高多少：看直观对比表</b></a>。CPU 使用率与每次操作 CPU 时间是不同指标；本面板的 CPU 页展示每次操作 CPU 时间。驻留工作集 RSS 表示实际在内存中的进程页面，优先用它比较内存占用。</div>'
controls=controls.replace('单库工作集（独立测量）','单库驻留内存 RSS（独立测量）')
notes=notes.replace('短区间或零增量不排名','任一引擎区间 CPU 时间中位数不足 100 ms 不排名')
page='<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>PureDb vs SQLite 实测对比</title><style>'+style+'</style></head><body><main>'+intro+controls+''.join(rendered)+notes+'</main><script>'+script+'</script></body></html>'
output.write_text(page,encoding='utf-8-sig')
print(f'Scoreboard: {output}; 41 main scenarios, 8 ADO scenarios, 3 isolated memory scenarios')
