namespace PureDb;

internal sealed record SqlJoinPlan(bool Supported, Expr? Key, int LeftKey, int RightKey, bool LeftOuter,
    int[] Columns, string[] Names, Expr? Limit, Expr? Offset, bool Descending)
{
    internal static SqlJoinPlan Build(Select select, SqlTable left, SqlTable right)
    {
        static SqlJoinPlan No() => new(false, null, 0, 0, false, [], [], null, null, false);
        var join = select.Joins[0];
        if (join.Kind is not ("INNER" or "LEFT") || join.On?.Op != "=" || join.On.Args.Any(e => e.Op != "column") ||
            select.Distinct || select.GroupBy.Count != 0 || select.Having is not null || select.Columns.Any(p => p.Expr.Op != "column") ||
            select.Order.Count > 1 || select.Limit is not null && select.Limit.Op is not ("literal" or "parameter") ||
            select.Offset is not null && select.Offset.Op is not ("literal" or "parameter")) return No();
        var binding = new SqlBinding(); binding.Add(left, select.From.Alias); binding.Add(right, join.Source.Alias);
        var key = SqlPrimaryPredicate.Key(select.Where, binding, left.Primary); if (key is null) return No();
        int width = left.Columns.Count, a = binding.Resolve((string)join.On.Args[0].Value!), b = binding.Resolve((string)join.On.Args[1].Value!);
        if (a >= width && b < width) (a, b) = (b, a);
        if (a >= width || b < width) return No();
        var columns = select.Columns.Select(p => binding.Resolve((string)p.Expr.Value!)).ToArray();
        var names = select.Columns.Select(p => p.Alias ?? ((string)p.Expr.Value!).Split('.')[^1]).ToArray();
        if (select.Order.Count == 1)
        {
            var order = select.Order[0].Expr; int ordinal;
            if (order.Op == "literal" && order.Value is long n && n > 0 && n <= columns.Length) ordinal = columns[(int)n - 1];
            else if (order.Op == "column")
            {
                string name = (string)order.Value!;
                int projection = name.Contains('.') ? -1 : Array.FindIndex(names, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
                ordinal = projection >= 0 ? columns[projection] : binding.Resolve(name);
            }
            else return No();
            if (ordinal != width + right.Primary) return No();
        }
        return new(true, key, a, b - width, join.Kind == "LEFT", columns, names, select.Limit, select.Offset, select.Order.Count == 1 && select.Order[0].Descending);
    }
}

internal sealed partial class SqlSession
{
    private SqlResult QueryPrimaryJoin(SqlJoinPlan plan, SqlTable left, SqlTable right, IReadOnlyDictionary<string, object?> parameters)
    {
        object? key = SqlRangePlan.Value(plan.Key, parameters);
        int skip = SqlRangePlan.Count(plan.Offset, parameters, 0), take = SqlRangePlan.Count(plan.Limit, parameters, int.MaxValue);
        const string description = "PrimaryKeySeek -> IndexJoin(cached binding)";
        if (!SqlPrimaryPredicate.TryId(key, out long id)) return new(plan.Names, [], description);
        var first = Row(left, id); if (first is null || take == 0) return new(plan.Names, [], description);
        List<object?[]>? matches = null;
        if (first[plan.LeftKey] is { } value) Data(right).Hash(plan.RightKey).TryGetValue(value, out matches);
        int total = matches?.Count ?? (plan.LeftOuter ? 1 : 0), count = Math.Min(take, Math.Max(0, total - skip));
        var rows = new object?[count][]; int width = left.Columns.Count;
        for (int i = 0; i < count; i++)
        {
            var second = matches is null ? null : matches[plan.Descending ? total - 1 - skip - i : skip + i];
            var row = rows[i] = new object?[plan.Columns.Length];
            for (int c = 0; c < row.Length; c++)
            {
                int column = plan.Columns[c]; object? v = column < width ? first[column] : second?[column - width];
                row[c] = v is byte[] bytes ? bytes.ToArray() : SqlJsonFunctions.Unwrap(v);
            }
        }
        return new(plan.Names, rows, description);
    }
}
