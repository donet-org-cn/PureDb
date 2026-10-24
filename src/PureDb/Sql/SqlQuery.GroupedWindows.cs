namespace PureDb;
/// <remarks>本部分先完成 GROUP BY/HAVING，再把分组结果作为窗口输入，保持 SQL 阶段顺序。</remarks>
internal sealed partial class SqlSession
{
    /// <summary>将聚合与窗口组合的查询分成共享快照内的两个执行阶段。</summary>
    /// <param name = "select">含窗口和聚合的原始查询。</param>
    /// <param name = "parameters">当前参数。</param>
    /// <returns>在分组结果上求窗口、排序和分页的最终结果。</returns>
    private SqlResult QueryGroupedWindows(Select select, IReadOnlyDictionary<string, object?> parameters)
    {
        var grouped = new List<Projection>();
        Expr Materialize(Expr expression)
        {
            int index = grouped.FindIndex(p => Same(p.Expr, expression));
            if (index < 0)
            {
                index = grouped.Count;
                grouped.Add(new(expression, "__group_" + index));
            }

            return new("column", "__group_" + index);
        }

        Expr Rewrite(Expr expression)
        {
            if (expression.Subquery is not null)
                throw new SqlException("Correlated subqueries in grouped-window projections require a derived table");
            if (expression.Window is not null)
            {
                var spec = ResolveWindow(expression.Window, select);
                spec = spec with
                {
                    Partition = spec.Partition.Select(Rewrite).ToList(),
                    Order = spec.Order.Select(o => o with { Expr = Rewrite(o.Expr) }).ToList()
                };
                return expression with
                {
                    Items = expression.Args.Select(a => a.Op == "star" ? a : Rewrite(a)).ToArray(),
                    Window = spec
                };
            }

            if (expression.Op == "column" || expression.Op is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX")
                return Materialize(expression);
            return expression with
            {
                Items = expression.Args.Select(Rewrite).ToArray()
            };
        }

        var columns = select.Columns.Select((p, i) => new Projection(Rewrite(p.Expr), p.Alias ?? (p.Expr.Op == "column" ? ((string)p.Expr.Value!).Split('.')[^1] : p.Expr.Op is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX" ? p.Expr.Op.ToLowerInvariant() : "expr" + (i + 1)))).ToList();
        var order = select.Order.Select(o => o.Expr.Op == "literal" || o.Expr.Op == "column" && columns.Any(p => p.Alias!.Equals((string)o.Expr.Value!, StringComparison.OrdinalIgnoreCase)) ? o : o with { Expr = Rewrite(o.Expr) }).ToList();
        if (grouped.Count == 0)
            grouped.Add(new(Expr.Literal(1L), "__group_empty"));
        var first = select with
        {
            Columns = grouped,
            Ctes = [],
            Compounds = [],
            Order = [],
            Limit = null,
            Offset = null,
            Distinct = false,
            HasWindows = false,
            Windows = new(StringComparer.OrdinalIgnoreCase)
        };
        var result = Query(first, parameters);
        string name = "__puredb_grouped_" + (-_virtualId);
        var table = VirtualTable(name, result);
        (_namedSources ??= new(StringComparer.OrdinalIgnoreCase))[name] = table;
        var second = select with
        {
            Columns = columns,
            From = new(name, name),
            Joins = [],
            Where = null,
            GroupBy = [],
            Having = null,
            Ctes = [],
            Compounds = [],
            Order = order,
            Windows = new(StringComparer.OrdinalIgnoreCase),
            HasWindows = true,
            RequiresContext = true
        };
        var output = Query(second, parameters);
        return output with
        {
            Plan = "GroupedWindow -> " + output.Plan
        };
    }
}
