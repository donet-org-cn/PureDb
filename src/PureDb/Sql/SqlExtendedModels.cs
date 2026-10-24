namespace PureDb;
/// <remarks>本部分保存表达式中的只读子查询，不把运行时查询树写入索引模式。</remarks>
internal sealed partial record Expr
{
    /// <summary>标量、EXISTS 或 IN 表达式包含的 SELECT。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Select? Subquery
    {
        get; init;
    }

    /// <summary>OVER 子句定义；为 null 时为普通标量或聚合表达式。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SqlWindow? Window
    {
        get; init;
    }
}

/// <remarks>本部分保存派生表和表值函数参数，作用域由查询执行器管理。</remarks>
internal sealed partial record Source
{
    /// <summary>FROM 括号中的派生查询；普通持久表为 null。</summary>
    public Select? Query
    {
        get; init;
    }
    /// <summary>表值函数参数；普通表和派生表为 null。</summary>
    public Expr[]? Arguments
    {
        get; init;
    }
}

/// <remarks>本部分保存公共表表达式和从左向右结合的集合操作。</remarks>
internal sealed partial record Select
{
    /// <summary>解析时计算的扩展查询标记，使普通主键查询免于逐次遍历语法树。</summary>
    public bool RequiresContext
    {
        get; init;
    }
    /// <summary>本层 SELECT 或 ORDER BY 是否包含窗口函数。</summary>
    public bool HasWindows
    {
        get; init;
    }
    /// <summary>按声明顺序可见的公共表表达式。</summary>
    public List<SqlCte> Ctes { get; init; } = [];
    /// <summary>是否允许当前 WITH 中的公共表表达式递归引用自身。</summary>
    public bool Recursive
    {
        get; init;
    }
    /// <summary>首个 SELECT 之后从左向右执行的集合操作分支。</summary>
    public List<SqlCompound> Compounds { get; init; } = [];
    /// <summary>当前 SELECT 的 WINDOW 命名定义。</summary>
    public Dictionary<string, SqlWindow> Windows { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>窗口分区、顺序和帧定义，未显式指定帧时使用 SQL 默认帧。</summary>
/// <param name = "Base">可选命名窗口。</param>
/// <param name = "Partition">分区表达式。</param>
/// <param name = "Order">分区内排序。</param>
/// <param name = "Frame">ROWS、RANGE、GROUPS；未指定为 null。</param>
/// <param name = "Start">可选起始边界。</param>
/// <param name = "End">可选结束边界。</param>
/// <param name = "Exclude">排除规则。</param>
internal sealed record SqlWindow(string? Base, List<Expr> Partition, List<Ordering> Order, string? Frame = null, SqlWindowBound? Start = null, SqlWindowBound? End = null, string Exclude = "NO OTHERS");
/// <summary>窗口边界的方向和可选偏移。</summary>
/// <param name = "Kind">UNBOUNDED PRECEDING、PRECEDING、CURRENT ROW、FOLLOWING 或 UNBOUNDED FOLLOWING。</param>
/// <param name = "Offset">PRECEDING/FOLLOWING 的非负常量偏移。</param>
internal sealed record SqlWindowBound(string Kind, Expr? Offset = null);
/// <summary>只在一条语句内可见的命名查询，允许显式指定输出列名。</summary>
/// <param name = "Name">公共表表达式名称。</param>
/// <param name = "Columns">显式结果列名；未指定时为 null。</param>
/// <param name = "Query">公共表表达式的查询定义。</param>
internal sealed record SqlCte(string Name, string[]? Columns, Select Query);
/// <summary>UNION、INTERSECT 或 EXCEPT 的右侧查询。</summary>
/// <param name = "Operation">集合操作名称，保留 UNION ALL 与 UNION 的区别。</param>
/// <param name = "Query">本操作的右侧简单查询。</param>
internal sealed record SqlCompound(string Operation, Select Query);
/// <summary>把公共表表达式作用域附加到 INSERT、UPDATE 或 DELETE。</summary>
/// <param name = "Ctes">公共表表达式。</param>
/// <param name = "Recursive">是否允许递归。</param>
/// <param name = "Statement">需要在此作用域执行的增删改。</param>
internal sealed record SqlWithStatement(List<SqlCte> Ctes, bool Recursive, SqlStatement Statement) : SqlStatement;
