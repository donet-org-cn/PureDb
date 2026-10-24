namespace PureDb;

/// <summary>主键命中后执行已绑定标量投影的查询计划。</summary>
/// <param name="Supported">该语句结构是否可使用当前专用计划。</param>
/// <param name="Key">可选 SQL 表达式；null 表示未提供该子句。</param>
/// <param name="Selectors">已绑定的结果投影函数。</param>
/// <param name="Names">按结果列序号排列的列名。</param>
internal sealed record SqlScalarPlan(bool Supported, Expr? Key, Func<object?[], object?>[] Selectors, string[] Names)
{
    /// <summary>识别主键定位后的标量投影并预先绑定表达式。</summary>
    /// <param name="select">当前已解析的 SELECT 查询结构。</param>
    /// <param name="table">当前操作的不可变表模式。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    public static SqlScalarPlan Build(Select select, SqlTable table)
    {
        static bool Fixed(Expr e) => e.Op != "parameter" && e.Op != "star" && !SqlBinding.Aggregate(e) && e.Args.All(Fixed);
        if (select.Columns.Any(p => !Fixed(p.Expr)))
            return new(false, null, [], []);
        var primary = SqlPrimaryPlan.Build(select with
        {
            Columns = [new(new("star"), null)]
        }, table);
        if (!primary.Supported)
            return new(false, null, [], []);
        var bind = new SqlBinding();
        bind.Add(table, select.From.Alias);
        return new(true, primary.Key, select.Columns.Select(p => bind.Bind(p.Expr, SqlDatabase.EmptyParameters)).ToArray(), select.Columns.Select((p, i) => p.Alias ?? (p.Expr.Op == "column" ? ((string)p.Expr.Value!).Split('.')[^1] : "expr" + (i + 1))).ToArray());
    }
}
