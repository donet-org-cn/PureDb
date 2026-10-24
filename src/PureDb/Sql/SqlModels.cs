namespace PureDb;
/// <summary>SQL 解析、绑定、类型、约束或用户函数执行失败时抛出的数据库异常。</summary>
public sealed class SqlException : System.Data.Common.DbException
{
    /// <summary>创建 SQL 执行异常。</summary>
    /// <param name = "message">用于说明语法、约束或执行错误的消息。</param>
    public SqlException(string message) : base(message)
    {
    }

    /// <summary>创建保留原始 C# 异常的 SQL 执行异常。</summary>
    /// <param name = "message">SQL 执行错误消息。</param>
    /// <param name = "innerException">导致 SQL 执行失败的底层异常。</param>
    public SqlException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>一段 SQL 文本及其命名或位置参数，用于原子批量执行。</summary>
/// <param name = "Text">SQL 文本。</param>
/// <param name = "Parameters">命名或位置参数；为 null 时表示无参数。</param>
public sealed record SqlCommand(string Text, IReadOnlyDictionary<string, object?>? Parameters = null);
/// <summary>已物化的查询结果，包含列名、行数据及执行计划说明；SQL NULL 使用 null 表示。</summary>
/// <param name = "Columns">按结果序号排列的列名。</param>
/// <param name = "Rows">结果行；每个数组按列顺序保存标量值。</param>
/// <param name = "Plan">实际执行路径的诊断说明。</param>
public sealed record SqlResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, string Plan)
{
    /// <summary>ADO.NET 的列类型描述；直接查询时可能为空，未知或混合类型使用 object。</summary>
    public IReadOnlyList<Type> FieldTypes { get; internal set; } = [];
}

/// <summary>SQL 查询缓存和底层原生存储的配置。</summary>
public sealed class SqlDatabaseOptions
{
    /// <summary>已解码 SQL 表数据的缓存预算，单位为字节，默认 64 MiB；不包含结果和所有辅助索引的内存。</summary>
    public long CacheBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>底层文件存储的访问、持久性、队列和缓存选项。</summary>
    public DatabaseOptions Storage { get; init; } = new();
}

/// <summary>持久化列模式，包含存储类型及主键、非空和唯一约束。</summary>
/// <param name = "Name">模式对象名或对象路径分量名。</param>
/// <param name = "Type">声明的 SQL 存储类型。</param>
/// <param name = "Primary">是否为整数主键列。</param>
/// <param name = "NotNull">是否禁止 SQL NULL。</param>
/// <param name = "Unique">是否要求非 NULL 值唯一。</param>
/// <param name = "Hidden">是否只允许显式引用，SELECT 星号时不展开。</param>
internal sealed record SqlColumn(string Name, string Type, bool Primary = false, bool NotNull = false, bool Unique = false, bool Hidden = false)
{
    /// <summary>常量默认值表达式；未声明为 null。</summary>
    public Expr? Default
    {
        get; init;
    }
}

/// <summary>持久化索引模式；普通列索引使用 Column，表达式索引使用 Expression。</summary>
/// <param name = "Name">模式对象名或对象路径分量名。</param>
/// <param name = "Column">普通索引列名或已解析列序号。</param>
/// <param name = "Unique">是否要求非 NULL 值唯一。</param>
/// <param name = "Expression">可选 SQL 表达式；null 表示未提供该子句。</param>
internal sealed record SqlIndex(string Name, string Column, bool Unique, Expr? Expression = null)
{
    /// <summary>复合索引每个字段的降序标记；旧索引为 null。</summary>
    public bool[]? Descending
    {
        get; init;
    }
}

/// <summary>持久化表模式及列名解析，表 ID 用于隔离底层记录键空间。</summary>
internal sealed class SqlTable
{
    /// <summary>模式目录分配的表 ID，用于组合底层记录键。</summary>
    public int Id
    {
        get; set;
    }
    /// <summary>不区分大小写查找的表名。</summary>
    public string Name { get; set; } = "";
    /// <summary>按存储列序排列的列模式。</summary>
    public List<SqlColumn> Columns { get; set; } = [];
    /// <summary>显式声明的普通列或受支持表达式索引。</summary>
    public List<SqlIndex> Indexes { get; set; } = [];
    /// <summary>当前表声明的外键约束。</summary>
    public List<SqlForeignKey> ForeignKeys { get; set; } = [];
    /// <summary>整数主键列在行数组中的序号。</summary>
    public int Primary => Columns.FindIndex(c => c.Primary);
    /// <summary>是否存在主键之外的唯一约束或唯一索引。</summary>
    internal bool HasUniqueConstraints => Columns.Any(c => c.Unique && !c.Primary) || Indexes.Any(i => i.Unique);

    /// <summary>按不区分大小写的名称解析列序号；不存在时抛出 SQL 异常。</summary>
    /// <param name = "name">要解析、规范化或查找的标识符。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    public int Column(string name)
    {
        int i = Columns.FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? throw new SqlException($"Unknown column: {name}") : i;
    }
}

/// <summary>持久化 SQL 模式目录；共享目录保持不可变，DDL 修改前克隆。</summary>
internal sealed class SqlCatalog
{
    /// <summary>下一张新表可使用的表 ID。</summary>
    public int NextId { get; set; } = 1;
    /// <summary>当前模式目录中的表定义集合。</summary>
    public List<SqlTable> Tables { get; set; } = [];
    /// <summary>持久化只读视图定义。</summary>
    public List<SqlView> Views { get; set; } = [];
    /// <summary>持久化行级触发器定义。</summary>
    public List<SqlTrigger> Triggers { get; set; } = [];

    private int _managedWrites;
    /// <summary>当前不可变目录是否需要外键或触发器写入流水线。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool ManagedWrites
    {
        get
        {
            int state = Volatile.Read(ref _managedWrites);
            if (state == 0)
            {
                state = Triggers.Count > 0 || Tables.Any(t => t.ForeignKeys.Count > 0) ? 2 : 1;
                Volatile.Write(ref _managedWrites, state);
            }

            return state == 2;
        }
    }

    private Dictionary<string, SqlTable>? _lookup;
    /// <summary>按不区分大小写的名称查找表，首次访问时发布不可变查找字典。</summary>
    /// <param name = "name">要解析、规范化或查找的标识符。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    public SqlTable Table(string name)
    {
        var lookup = Volatile.Read(ref _lookup);
        if (lookup is null)
        {
            lookup = Tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
            lookup = Interlocked.CompareExchange(ref _lookup, lookup, null) ?? lookup;
        }

        return lookup.TryGetValue(name, out var table) ? table : throw new SqlException($"Unknown table: {name}");
    }
}

/// <summary>不可变 SQL 表达式节点，记录操作符、可选字面量及子表达式。</summary>
/// <param name = "Op">操作符名称。</param>
/// <param name = "Value">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
/// <param name = "Items">按执行顺序排列的子表达式。</param>
internal sealed partial record Expr(string Op, object? Value = null, Expr[]? Items = null)
{
    /// <summary>子表达式数组；没有子节点时返回空数组。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Expr[] Args => Items ?? [];

    /// <summary>构造一个携带 SQL 标量的字面量节点。</summary>
    /// <param name = "value">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    public static Expr Literal(object? value) => new("literal", value);
}

/// <summary>所有已解析 SQL 语句节点的基类。</summary>
internal abstract record SqlStatement;
/// <summary>建表语句节点，保存列定义和 IF NOT EXISTS 行为。</summary>
/// <param name = "Name">模式对象名或对象路径分量名。</param>
/// <param name = "Columns">目标列集合或结果投影，具体结构由字段类型确定。</param>
/// <param name = "IfNotExists">目标存在时是否忽略创建。</param>
internal sealed record CreateTable(string Name, List<SqlColumn> Columns, bool IfNotExists) : SqlStatement
{
    /// <summary>表级和列级外键定义。</summary>
    public List<SqlForeignKey> ForeignKeys { get; init; } = [];
    /// <summary>表级 UNIQUE 约束的列名。</summary>
    public List<string[]> UniqueColumns { get; init; } = [];
}

/// <summary>建索引语句节点，保存目标表、索引表达式和唯一性要求。</summary>
/// <param name = "Name">模式对象名或对象路径分量名。</param>
/// <param name = "Table">目标表名。</param>
/// <param name = "Expression">待分析、绑定或求值的 SQL 表达式。</param>
/// <param name = "Unique">是否要求非 NULL 值唯一。</param>
/// <param name = "IfNotExists">目标存在时是否忽略创建。</param>
internal sealed record CreateIndex(string Name, string Table, Expr Expression, bool Unique, bool IfNotExists) : SqlStatement
{
    /// <summary>与复合表达式子项一一对应的排序方向。</summary>
    public bool[]? Descending
    {
        get; init;
    }
}

/// <summary>删除模式对象的语句节点。</summary>
/// <param name = "Kind">语句对象类别、连接类型或词法类别。</param>
/// <param name = "Name">模式对象名或对象路径分量名。</param>
/// <param name = "IfExists">目标不存在时是否忽略删除。</param>
internal sealed record Drop(string Kind, string Name, bool IfExists) : SqlStatement;
/// <summary>多行 VALUES 插入语句节点，保留目标列及每行表达式。</summary>
/// <param name = "Table">目标表名。</param>
/// <param name = "Columns">目标列集合或结果投影，具体结构由字段类型确定。</param>
/// <param name = "Values">列值、行列表或唯一键映射；具体形态由当前记录的字段类型决定。</param>
internal sealed record Insert(string Table, string[]? Columns, List<Expr[]> Values) : SqlStatement
{
    /// <summary>INSERT SELECT 的源查询；VALUES 插入为 null。</summary>
    public Select? Query
    {
        get; init;
    }
}

/// <summary>更新语句节点，赋值表达式基于更新前的原始行计算。</summary>
/// <param name = "Table">目标表名。</param>
/// <param name = "Assignments">目标列序号及赋值表达式。</param>
/// <param name = "Where">可选 SQL 表达式；null 表示未提供该子句。</param>
internal sealed record Update(string Table, List<(string Column, Expr Value)> Assignments, Expr? Where) : SqlStatement;
/// <summary>按可选 WHERE 条件删除记录的语句节点。</summary>
/// <param name = "Table">目标表名。</param>
/// <param name = "Where">可选 SQL 表达式；null 表示未提供该子句。</param>
internal sealed record Delete(string Table, Expr? Where) : SqlStatement;
/// <summary>查询的表名及其在当前语句中的别名。</summary>
/// <param name = "Table">目标表名。</param>
/// <param name = "Alias">当前语句内的数据源或结果列别名。</param>
internal sealed partial record Source(string Table, string Alias);
/// <summary>连接类型、右侧数据源及可选 ON 表达式。</summary>
/// <param name = "Kind">语句对象类别、连接类型或词法类别。</param>
/// <param name = "Source">连接右侧数据源。</param>
/// <param name = "On">可选 SQL 表达式；null 表示未提供该子句。</param>
internal sealed record Join(string Kind, Source Source, Expr? On);
/// <summary>结果投影表达式及可选列别名。</summary>
/// <param name = "Expr">待分析、绑定或求值的 SQL 表达式。</param>
/// <param name = "Alias">当前语句内的数据源或结果列别名。</param>
internal sealed record Projection(Expr Expr, string? Alias);
/// <summary>排序表达式及升降序标记。</summary>
/// <param name = "Expr">待分析、绑定或求值的 SQL 表达式。</param>
/// <param name = "Descending">是否按降序输出。</param>
internal sealed record Ordering(Expr Expr, bool Descending);
/// <summary>SELECT 的不可变查询结构，包含连接、过滤、分组、投影、排序及分页。</summary>
/// <param name = "Columns">目标列集合或结果投影，具体结构由字段类型确定。</param>
/// <param name = "From">首个查询数据源。</param>
/// <param name = "Joins">按顺序执行的连接。</param>
/// <param name = "Where">可选 SQL 表达式；null 表示未提供该子句。</param>
/// <param name = "GroupBy">分组键表达式。</param>
/// <param name = "Having">可选 SQL 表达式；null 表示未提供该子句。</param>
/// <param name = "Order">排序规则。</param>
/// <param name = "Limit">可选 SQL 表达式；null 表示未提供该子句。</param>
/// <param name = "Offset">可选 SQL 表达式；null 表示未提供该子句。</param>
/// <param name = "Distinct">是否按整行去重。</param>
internal sealed partial record Select(List<Projection> Columns, Source From, List<Join> Joins, Expr? Where, List<Expr> GroupBy, Expr? Having, List<Ordering> Order, Expr? Limit, Expr? Offset, bool Distinct) : SqlStatement;
/// <summary>持久化表达式规范化、语义比较和受预算约束的不可变模式缓存。</summary>
internal static class SqlAst
{
    // 这里返回的模式对象由多个实例共享且必须保持不可变。DDL 修改前必须通过 Load 克隆；
    // 禁止将可变的 DDL 副本放入共享缓存。
    private static readonly object CatalogGate = new();
    private static readonly Dictionary<string, (byte[] Bytes, SqlCatalog Catalog)> CatalogCache = [];
    private static readonly Queue<string> CatalogOrder = new();
    private static int _catalogBytes;
    /// <summary>按模式字节摘要复用不可变目录，并对缓存条目数和字节总量施加限制。</summary>
    /// <param name = "bytes">当前调用持有的完整页、记录或模式编码字节。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    internal static SqlCatalog LoadCached(byte[] bytes)
    {
        if (bytes.Length > 65536)
            return Load(bytes);
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        lock (CatalogGate)
            if (CatalogCache.TryGetValue(key, out var hit) && bytes.AsSpan().SequenceEqual(hit.Bytes))
                return hit.Catalog;
        var parsed = Load(bytes);
        lock (CatalogGate)
        {
            if (CatalogCache.TryGetValue(key, out var hit))
                return bytes.AsSpan().SequenceEqual(hit.Bytes) ? hit.Catalog : parsed;
            while (CatalogCache.Count >= 16 || _catalogBytes + bytes.Length > 256 * 1024)
            {
                string old = CatalogOrder.Dequeue();
                _catalogBytes -= CatalogCache[old].Bytes.Length;
                CatalogCache.Remove(old);
            }

            CatalogCache.Add(key, (bytes.ToArray(), parsed));
            CatalogOrder.Enqueue(key);
            _catalogBytes += bytes.Length;
        }

        return parsed;
    }

    /// <summary>将反序列化的表达式字面量转回 SQL 标量，递归规范化子节点。</summary>
    /// <param name = "e">待分析、绑定或求值的 SQL 表达式。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    public static Expr Normalize(Expr e) => e with
    {
        Value = e.Value is System.Text.Json.JsonElement j ? j.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => j.GetString(),
            System.Text.Json.JsonValueKind.Number => j.TryGetInt64(out long n) ? n : (object)j.GetDouble(),
            System.Text.Json.JsonValueKind.Null => null,
            _ => throw new SqlException("Invalid persisted index expression")
        } : e.Value,
        Items = e.Args.Select(Normalize).ToArray()
    };
    /// <summary>比较受支持索引表达式的语义结构，列名比较忽略限定别名和大小写。</summary>
    /// <param name = "a">待分析、绑定或求值的 SQL 表达式。</param>
    /// <param name = "b">待分析、绑定或求值的 SQL 表达式。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    public static bool Equal(Expr a, Expr b) => a.Op == b.Op && Equals(a.Subquery, b.Subquery) && Equals(a.Window, b.Window) && (a.Op == "column" ? ((string)a.Value!).Split('.')[^1].Equals(((string)b.Value!).Split('.')[^1], StringComparison.OrdinalIgnoreCase) : Equals(a.Value, b.Value)) && a.Args.Length == b.Args.Length && a.Args.Zip(b.Args).All(p => Equal(p.First, p.Second));
    /// <summary>使用生成的 JSON 元数据读取模式目录，并规范化持久化索引表达式。</summary>
    /// <param name = "bytes">当前调用持有的完整页、记录或模式编码字节。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    public static SqlCatalog Load(byte[] bytes)
    {
        var catalog = System.Text.Json.JsonSerializer.Deserialize(bytes, SqlCatalogJsonContext.Default.SqlCatalog) ?? throw new InvalidDataException("Invalid catalog");
        foreach (var table in catalog.Tables)
        {
            for (int i = 0; i < table.Columns.Count; i++)
                if (table.Columns[i].Default is { } d)
                    table.Columns[i] = table.Columns[i] with
                    {
                        Default = Normalize(d)
                    };
            for (int i = 0; i < table.Indexes.Count; i++)
                if (table.Indexes[i].Expression is { } e)
                    table.Indexes[i] = table.Indexes[i] with
                    {
                        Expression = Normalize(e)
                    };
        }

        return catalog;
    }
}
