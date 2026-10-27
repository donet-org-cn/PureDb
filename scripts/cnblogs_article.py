"""首次项目介绍的正文渲染；静态叙述与公开测量数据分开维护。"""
import json
import statistics
from pathlib import Path


def render_intro(root, version, current, compatibility_rows, labels, verdict, table):
    """用现有测量结果填充介绍文章，不重新测量或修改原始样本。"""
    root = Path(root)
    base = root / 'benchmarks/results'
    read = lambda path: json.loads(path.read_text(encoding='utf-8-sig'))
    platforms = [('windows', 'Windows x64'), ('linux', 'Linux ARM64')]
    feature_rows = [
        ['纯 .NET / 原生依赖', '✅ 主库与可选兼容库纯托管', '原版 C 内核；.NET 通常通过原生绑定使用'],
        ['嵌入式文件存储', '✅ 自有格式及索引侧文件', '✅ SQLite 文件和日志协议'],
        ['KV / SQL API', '✅ 独立 KV 与 SQL 入口', '通过 SQL 表实现相应操作'],
        ['SQL 类型与主键', '⚠️ 严格类型；单列整数主键及范围限制', '更完整的类型、rowid 及主键语义'],
        ['单列、唯一、JSON 路径索引', '✅ 已实现的索引形状', '✅ 支持更多索引形态'],
        ['复合索引', '⚠️ 定义持久化；数据按版本在内存构建', '✅ 分页增量 B-tree'],
        ['JOIN / 聚合 / 排序 / 分页', '⚠️ 常见语法及专用执行计划', '✅ 更完整的方言与优化'],
        ['外键 / CHECK / 视图 / 触发器', '⚠️ 已实现的语法和动作子集', '✅ 更完整的模式与控制选项'],
        ['CTE / 窗口 / 子查询 / 集合', '⚠️ 支持清单内的语法', '✅ 支持相应能力及更广语义'],
        ['JSON / JSON5 / JSON 表函数', '⚠️ 已实现函数与路径；节点 ID 有差异', '✅ 原生 JSON 能力'],
        ['JSONB', '⚠️ 自有 PJB1 格式', 'SQLite 自有格式，两者不互通'],
        ['应用自定义函数', '⚠️ C# 标量委托', '标量、聚合、窗口及表值扩展机制'],
        ['批量增删改', '✅ SQL 批次与原子事务', '✅ SQL 与事务批处理'],
        ['多线程写入', '✅ 有界队列、单写入者与分组提交', '多连接协调，写事务串行'],
        ['多进程访问', '⚠️ 原版 Shared 模式', '✅ 文件锁和 WAL 等协议'],
        ['事务 / 保存点', '⚠️ 乐观 Serializable；嵌套保存点', '✅ SQLite 事务及锁语义'],
        ['ADO.NET', '✅ 内置 Provider；ORM 需分别验证', '通过所选 .NET Provider'],
        ['Native AOT', '✅ 实际编译运行验证', 'C 内核与所选 .NET 宿主分别考虑'],
        ['SQLite 文件原地读写', '⚠️ 可选纯托管兼容包，独占及格式子集', '✅ 原生协议'],
        ['SQLite WAL / 任意现有模式', '❌ 文件兼容后端尚未覆盖', '✅ 原生能力；具体构建及扩展有差异'],
    ]
    performance = []
    resources = []
    scenarios = [
        ('sql_point', 'µs/查询'), ('sql_range20', 'µs/查询'),
        ('sql_join', 'µs/查询'), ('json_scan', 'µs/查询'),
        ('Full_single', 'µs/行'), ('Full_batch1000', 'µs/行'),
        ('Full_batch_update', 'µs/行'), ('Full_batch_delete', 'µs/行'),
        ('shared_update', 'µs/更新'), ('reopen_and_first_range', 'µs/open'),
    ]
    memory_names = {'point': '主键读取', 'range': '范围读取', 'json': 'JSON 查询'}
    for key, platform in platforms:
        result = current['Platforms'][platform]
        metrics = result['Metrics']
        wins, losses, close = result['Score']
        rows = []
        for scenario, unit in scenarios:
            a, b = [metrics[scenario][engine]['Us'] for engine in ['Optimized', 'SQLite']]
            rows.append([labels[scenario], unit, f'{a:,.3f}', f'{b:,.3f}', verdict(a, b)])
        performance.extend([
            '### ' + platform,
            f'41 项耗时：PureDb 领先 **{wins}** 项，SQLite 领先 **{losses}** 项，接近 **{close}** 项。',
            table(['场景', '单位', 'PureDb', 'SQLite', '耗时谁更好'], rows),
        ])
        reports = [read(path) for path in (base / 'latest' / key / 'memory').glob('*.json')
                   if path.name != 'summary.json']
        rows = []
        for scenario, name in memory_names.items():
            a, b = [statistics.median(r['WorkingSetBytes'] / 1048576 for r in reports
                                     if r['Engine'] == engine and r['Scenario'] == scenario)
                    for engine in ['Optimized', 'SQLite']]
            rows.append([name, f'{a:.2f}', f'{b:.2f}',
                         f'{"PureDb" if a > b else "SQLite"} 更高 {abs(a-b):.2f} MiB（{(max(a,b)/min(a,b)-1)*100:.1f}%）'])
        a, b = [metrics['sql_point'][engine]['CpuUs'] for engine in ['Optimized', 'SQLite']]
        resources.extend([
            '### ' + platform + ' 资源',
            table(['场景', 'PureDb RSS MiB', 'SQLite RSS MiB', '谁占更多'], rows),
            f'主键完整行的每次 CPU：PureDb **{a:.3f} µs**，SQLite **{b:.3f} µs**；'
            f'{"PureDb" if a > b else "SQLite"} 多消耗 {abs(a-b):.3f} µs（{(max(a,b)/min(a,b)-1)*100:.1f}%）。',
        ])
    aot = read(base / 'aot/latest/summary.json')
    aot_rows = []
    file_rows = []
    for key, platform in platforms:
        manifest = read(base / 'aot/latest' / key / 'environment.json')['Manifest']
        variants = {(v['Engine'], v['Mode']): v for v in manifest['Variants']}
        startup = aot['Platforms'][platform]['Startup']
        for engine in ['PureDb', 'SQLite']:
            aot_rows.append([
                platform, engine,
                f'{variants[engine,"jit"]["RuntimeBytes"]/1048576:.2f}',
                f'{variants[engine,"aot"]["RuntimeBytes"]/1048576:.2f}',
                f'{startup[engine]["jit"]["ProcessMs"]:.2f}',
                f'{startup[engine]["aot"]["ProcessMs"]:.2f}',
            ])
        a, b = [aot['Platforms'][platform]['DatabaseBytes'][engine] / 1048576
                for engine in ['PureDb', 'SQLite']]
        file_rows.append([platform, f'{a:.2f}', f'{b:.2f}',
                          f'{"PureDb" if a > b else "SQLite"} 多 {abs(a-b):.2f} MiB（{(max(a,b)/min(a,b)-1)*100:.1f}%）'])
    replacements = {
        'VERSION': version,
        'FEATURE_MATRIX': table(['能力', 'PureDb ' + version, 'SQLite 3.54.1'], feature_rows),
        'COMPATIBILITY_RESULTS': table(['平台', '测试集合', '通过', '总数', '通过率'], compatibility_rows),
        'PERFORMANCE_RESULTS': '\n\n'.join(performance),
        'RESOURCE_RESULTS': '\n\n'.join(resources),
        'DATABASE_SIZE_RESULTS': table(['平台', 'PureDb MiB', 'SQLite MiB', '谁更大'], file_rows),
        'AOT_RESULTS': table(['平台', '引擎', '自包含 JIT MiB', 'AOT MiB', 'JIT 启动 ms', 'AOT 启动 ms'], aot_rows),
    }
    article = (root / 'scripts/templates/cnblogs-introduction.md').read_text(encoding='utf-8')
    for name, value in replacements.items():
        article = article.replace('{{' + name + '}}', value)
    return article


def draw_architecture(plt, save):
    """绘制可上传博客园的静态 PNG/SVG 架构图。"""
    from matplotlib.patches import FancyBboxPatch

    fig, ax = plt.subplots(figsize=(13, 8))
    ax.set_xlim(0, 1)
    ax.set_ylim(0, 1)
    ax.axis('off')

    def box(x, y, width, height, text, color):
        ax.add_patch(FancyBboxPatch((x, y), width, height,
                                   boxstyle='round,pad=0.012', linewidth=1.1,
                                   edgecolor='#536475', facecolor=color))
        ax.text(x + width / 2, y + height / 2, text, ha='center', va='center', fontsize=12)

    def arrow(start, end):
        ax.annotate('', xy=end, xytext=start,
                    arrowprops={'arrowstyle': '-|>', 'color': '#536475', 'lw': 1.5,
                                'shrinkA': 6, 'shrinkB': 6})

    box(.16, .87, .68, .085, '业务应用：C# / .NET 10', '#eaf2fa')
    box(.04, .69, .23, .09, 'KV API\nDatabase', '#eef7ef')
    box(.37, .69, .23, .09, 'SQL API\nSqlDatabase', '#eaf2fa')
    box(.70, .69, .26, .09, 'ADO.NET\nConnection / Command / Reader', '#eaf2fa')
    for x in [.155, .485, .83]:
        arrow((.50, .87), (x, .78))
    box(.35, .45, .61, .13,
        'SQL 解析、绑定、计划缓存与执行\nJOIN / 聚合 / JSON / 事务 / C# 标量函数', '#eaf2fa')
    arrow((.485, .69), (.485, .58))
    arrow((.83, .69), (.83, .58))
    box(.04, .20, .43, .13, '默认原版存储\n追加日志 / 持久索引 / 检查点', '#eef7ef')
    box(.55, .20, .41, .13, '可选纯托管 SQLite 兼容后端\nB-tree 页 / 回滚日志子集', '#fff3df')
    arrow((.155, .69), (.155, .33))
    arrow((.48, .45), (.32, .33))
    arrow((.80, .45), (.76, .33))
    ax.text(.255, .13, 'PureDb 数据文件及侧文件', ha='center', fontsize=11)
    ax.text(.755, .13, '受支持的 SQLite 文件及日志', ha='center', fontsize=11)
    ax.text(.50, .04, '兼容包依赖主库；主库不反向依赖兼容包。两种后端的协议和性能分别验证。',
            ha='center', fontsize=11, color='#536475')
    fig.suptitle('PureDb 的 API 与存储分层', fontsize=18)
    fig.tight_layout(rect=[0, 0, 1, .95])
    save(fig, 'architecture')
