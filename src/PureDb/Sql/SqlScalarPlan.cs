namespace PureDb;

internal sealed record SqlScalarPlan(bool Supported, Expr? Key, Func<object?[], object?>[] Selectors, string[] Names)
{
    public static SqlScalarPlan Build(Select select, SqlTable table)
    {
        static bool Fixed(Expr e) => e.Op != "parameter" && e.Op != "star" && !SqlBinding.Aggregate(e) && e.Args.All(Fixed);
        if (select.Columns.Any(p => !Fixed(p.Expr))) return new(false, null, [], []);
        var primary = SqlPrimaryPlan.Build(select with { Columns = [new(new("star"), null)] }, table);
        if (!primary.Supported) return new(false, null, [], []);
        var bind = new SqlBinding(); bind.Add(table, select.From.Alias);
        return new(true, primary.Key, select.Columns.Select(p => bind.Bind(p.Expr, SqlDatabase.EmptyParameters)).ToArray(),
            select.Columns.Select((p, i) => p.Alias ?? (p.Expr.Op == "column" ? ((string)p.Expr.Value!).Split('.')[^1] : "expr" + (i + 1))).ToArray());
    }
}
