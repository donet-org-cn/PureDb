namespace PureDb;

/// <summary>JSON 表达式索引范围查询计划，保存边界、投影、排序及分页结构。</summary>
/// <param name="Supported">该语句结构是否可使用当前专用计划。</param>
/// <param name="Index">目标索引或数组下标。</param>
/// <param name="Op">操作符名称。</param>
/// <param name="Low">可选 SQL 表达式；null 表示未提供该子句。</param>
/// <param name="High">可选 SQL 表达式；null 表示未提供该子句。</param>
/// <param name="Selectors">已绑定的结果投影函数。</param>
/// <param name="Names">按结果列序号排列的列名。</param>
/// <param name="Limit">可选 SQL 表达式；null 表示未提供该子句。</param>
/// <param name="Offset">可选 SQL 表达式；null 表示未提供该子句。</param>
/// <param name="Descending">是否按降序输出。</param>
internal sealed record SqlExpressionRangePlan(bool Supported, SqlIndex? Index, string Op, Expr? Low, Expr? High, Func<object?[], object?>[] Selectors, IReadOnlyList<string> Names, Expr? Limit, Expr? Offset, bool Descending)
{
    /// <summary>识别 JSON 表达式索引范围结构并绑定投影；不支持时返回禁用计划。</summary>
    /// <param name="select">当前已解析的 SELECT 查询结构。</param>
    /// <param name="table">当前操作的不可变表模式。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    internal static SqlExpressionRangePlan Build(Select select, SqlTable table)
    {
        static SqlExpressionRangePlan No() => new(false, null, "", null, null, [], [], null, null, false);
        static bool Simple(Expr e) => e.Op is "literal" or "parameter";
        static bool Fixed(Expr e) => e.Op is not ("parameter" or "star") && !SqlBinding.Aggregate(e) && e.Args.All(Fixed);
        if (select.Joins.Count != 0 || select.GroupBy.Count != 0 || select.Having is not null || select.Distinct || select.Where?.Op is not ("=" or "<" or ">" or "<=" or ">=" or "BETWEEN") || select.Order.Count > 1 || select.Limit is not null && !Simple(select.Limit) || select.Offset is not null && !Simple(select.Offset) || select.Columns.Any(p => !Fixed(p.Expr)))
            return No();
        var where = select.Where;
        string op = where.Op;
        Expr expression = where.Args[0], low = where.Args[1];
        Expr? high = op == "BETWEEN" ? where.Args[2] : null;
        if (Simple(expression) && op != "BETWEEN")
        {
            (expression, low) = (low, expression);
            op = op switch
            {
                "<" => ">",
                ">" => "<",
                "<=" => ">=",
                ">=" => "<=",
                _ => op
            };
        }

        if (!Simple(low) || high is not null && !Simple(high))
            return No();
        var index = table.Indexes.FirstOrDefault(i => i.Expression is not null && SqlAst.Equal(i.Expression, expression));
        if (index is null)
            return No();
        var binding = new SqlBinding();
        binding.Add(table, select.From.Alias);
        binding.Bind(expression, SqlDatabase.EmptyParameters); // validate qualifiers even on an empty table
        var selectors = select.Columns.Select(p => binding.Bind(p.Expr, SqlDatabase.EmptyParameters)).ToArray();
        var names = select.Columns.Select((p, i) => p.Alias ?? (p.Expr.Op == "column" ? ((string)p.Expr.Value!).Split('.')[^1] : "expr" + (i + 1))).ToArray();
        if (select.Order.Count == 1)
        {
            var order = select.Order[0].Expr;
            if (order.Op == "literal" && order.Value is long n && n > 0 && n <= select.Columns.Count)
                order = select.Columns[(int)n - 1].Expr;
            else if (order.Op == "column" && !((string)order.Value!).Contains('.'))
            {
                int projection = Array.FindIndex(names, name => name.Equals((string)order.Value!, StringComparison.OrdinalIgnoreCase));
                if (projection >= 0)
                    order = select.Columns[projection].Expr;
            }

            binding.Bind(order, SqlDatabase.EmptyParameters);
            if (!SqlAst.Equal(order, expression) && !(op == "=" && order.Op == "column" && binding.Resolve((string)order.Value!) == table.Primary))
                return No();
        }

        return new(true, index, op, low, high, selectors, Array.AsReadOnly(names), select.Limit, select.Offset, select.Order.Count == 1 && select.Order[0].Descending);
    }
}

/// <summary>一次快照或显式事务中的 SQL 执行状态；维护私有变更、模式副本和约束映射。</summary>
internal sealed partial class SqlSession
{
    /// <summary>执行受支持的 JSON 表达式索引范围查询及分页投影。</summary>
    /// <param name="plan">专用查询计划，或候选行选择返回的计划说明。</param>
    /// <param name="table">当前操作的不可变表模式。</param>
    /// <param name="parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private SqlResult QueryExpressionRange(SqlExpressionRangePlan plan, SqlTable table, IReadOnlyDictionary<string, object?> parameters)
    {
        object? low = SqlRangePlan.Value(plan.Low, parameters), high = SqlRangePlan.Value(plan.High, parameters);
        int skip = SqlRangePlan.Count(plan.Offset, parameters, 0), take = SqlRangePlan.Count(plan.Limit, parameters, int.MaxValue);
        var range = Data(table).ExpressionRange(plan.Index!, plan.Op, low, high);
        int count = Math.Min(take, Math.Max(0, range.End - range.Begin - skip));
        var rows = new object?[count][];
        for (int i = 0; i < count; i++)
        {
            var source = range.Rows[plan.Descending ? range.End - 1 - skip - i : range.Begin + skip + i];
            var row = rows[i] = new object?[plan.Selectors.Length];
            for (int c = 0; c < row.Length; c++)
            {
                object? v = plan.Selectors[c](source);
                row[c] = v is byte[] b ? b.ToArray() : SqlJsonFunctions.Unwrap(v);
            }
        }

        return new(plan.Names, rows, "ExpressionIndexSeek(" + plan.Index!.Name + ") -> CachedProjection");
    }
}
