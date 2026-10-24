namespace PureDb;

/// <summary>主键驱动的两表等值连接计划，保存连接键和投影而不保留查询结果。</summary>
/// <param name="Supported">该语句结构是否可使用当前专用计划。</param>
/// <param name="Key">可选 SQL 表达式；null 表示未提供该子句。</param>
/// <param name="LeftKey">左输入连接键的列序号。</param>
/// <param name="RightKey">右输入连接键的列序号。</param>
/// <param name="LeftOuter">是否保留未匹配的左侧行。</param>
/// <param name="Columns">目标列集合或结果投影，具体结构由字段类型确定。</param>
/// <param name="Names">按结果列序号排列的列名。</param>
/// <param name="Limit">可选 SQL 表达式；null 表示未提供该子句。</param>
/// <param name="Offset">可选 SQL 表达式；null 表示未提供该子句。</param>
/// <param name="Descending">是否按降序输出。</param>
internal sealed record SqlJoinPlan(bool Supported, Expr? Key, int LeftKey, int RightKey, bool LeftOuter, int[] Columns, string[] Names, Expr? Limit, Expr? Offset, bool Descending)
{
    /// <summary>识别可由主键驱动的两表等值连接；不支持时返回禁用计划。</summary>
    /// <param name="select">当前已解析的 SELECT 查询结构。</param>
    /// <param name="left">当前操作的不可变表模式。</param>
    /// <param name="right">当前操作的不可变表模式。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    internal static SqlJoinPlan Build(Select select, SqlTable left, SqlTable right)
    {
        static SqlJoinPlan No() => new(false, null, 0, 0, false, [], [], null, null, false);
        var join = select.Joins[0];
        if (join.Kind is not ("INNER" or "LEFT") || join.On?.Op != "=" || join.On.Args.Any(e => e.Op != "column") || select.Distinct || select.GroupBy.Count != 0 || select.Having is not null || select.Columns.Any(p => p.Expr.Op != "column") || select.Order.Count > 1 || select.Limit is not null && select.Limit.Op is not ("literal" or "parameter") || select.Offset is not null && select.Offset.Op is not ("literal" or "parameter"))
            return No();
        var binding = new SqlBinding();
        binding.Add(left, select.From.Alias);
        binding.Add(right, join.Source.Alias);
        var key = SqlPrimaryPredicate.Key(select.Where, binding, left.Primary);
        if (key is null)
            return No();
        int width = left.Columns.Count, a = binding.Resolve((string)join.On.Args[0].Value!), b = binding.Resolve((string)join.On.Args[1].Value!);
        if (a >= width && b < width)
            (a, b) = (b, a);
        if (a >= width || b < width)
            return No();
        var columns = select.Columns.Select(p => binding.Resolve((string)p.Expr.Value!)).ToArray();
        var names = select.Columns.Select(p => p.Alias ?? ((string)p.Expr.Value!).Split('.')[^1]).ToArray();
        if (select.Order.Count == 1)
        {
            var order = select.Order[0].Expr;
            int ordinal;
            if (order.Op == "literal" && order.Value is long n && n > 0 && n <= columns.Length)
                ordinal = columns[(int)n - 1];
            else if (order.Op == "column")
            {
                string name = (string)order.Value!;
                int projection = name.Contains('.') ? -1 : Array.FindIndex(names, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
                ordinal = projection >= 0 ? columns[projection] : binding.Resolve(name);
            }
            else
                return No();
            if (ordinal != width + right.Primary)
                return No();
        }

        return new(true, key, a, b - width, join.Kind == "LEFT", columns, names, select.Limit, select.Offset, select.Order.Count == 1 && select.Order[0].Descending);
    }
}

/// <summary>一次快照或显式事务中的 SQL 执行状态；维护私有变更、模式副本和约束映射。</summary>
internal sealed partial class SqlSession
{
    /// <summary>从主键驱动记录开始执行两表等值连接，保留所需外连接语义。</summary>
    /// <param name="plan">专用查询计划，或候选行选择返回的计划说明。</param>
    /// <param name="left">当前操作的不可变表模式。</param>
    /// <param name="right">当前操作的不可变表模式。</param>
    /// <param name="parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private SqlResult QueryPrimaryJoin(SqlJoinPlan plan, SqlTable left, SqlTable right, IReadOnlyDictionary<string, object?> parameters)
    {
        object? key = SqlRangePlan.Value(plan.Key, parameters);
        int skip = SqlRangePlan.Count(plan.Offset, parameters, 0), take = SqlRangePlan.Count(plan.Limit, parameters, int.MaxValue);
        const string description = "PrimaryKeySeek -> IndexJoin(cached binding)";
        if (!SqlPrimaryPredicate.TryId(key, out long id))
            return new(plan.Names, [], description);
        var first = Row(left, id);
        if (first is null || take == 0)
            return new(plan.Names, [], description);
        List<object?[]>? matches = null;
        if (first[plan.LeftKey] is { } value)
            Data(right).Hash(plan.RightKey).TryGetValue(value, out matches);
        int total = matches?.Count ?? (plan.LeftOuter ? 1 : 0), count = Math.Min(take, Math.Max(0, total - skip));
        var rows = new object?[count][];
        int width = left.Columns.Count;
        for (int i = 0; i < count; i++)
        {
            var second = matches is null ? null : matches[plan.Descending ? total - 1 - skip - i : skip + i];
            var row = rows[i] = new object?[plan.Columns.Length];
            for (int c = 0; c < row.Length; c++)
            {
                int column = plan.Columns[c];
                object? v = column < width ? first[column] : second?[column - width];
                row[c] = v is byte[] bytes ? bytes.ToArray() : SqlJsonFunctions.Unwrap(v);
            }
        }

        return new(plan.Names, rows, description);
    }
}
