namespace PureDb;
internal sealed record SqlRangePlan(bool Supported, int Column, string Op, Expr? Low, Expr? High,
    int[] Columns, IReadOnlyList<string> Names, Expr? Limit, Expr? Offset, bool Descending)
{
    public static object? Value(Expr? e, IReadOnlyDictionary<string, object?> parameters) => e?.Op == "parameter" ? SqlPrimaryPlan.Parameter(e, parameters) : e?.Value;
    public static int Count(Expr? e, IReadOnlyDictionary<string, object?> parameters, int fallback)
    {
        if (e is null) return fallback;
        object? value = Value(e, parameters);
        if (value is not long n || n < 0 || n > int.MaxValue) throw new SqlException("LIMIT/OFFSET requires a nonnegative integer <= Int32.MaxValue"); return (int)n;
    }
    public static SqlRangePlan Build(Select select, SqlTable table)
    {
        SqlRangePlan No() => new(false, 0, "", null, null, [], [], null, null, false);
        bool Simple(Expr e) => e.Op is "literal" or "parameter";
        if (select.Joins.Count > 0 || select.GroupBy.Count > 0 || select.Having is not null || select.Distinct || select.Where is null || select.Limit is not null && !Simple(select.Limit) || select.Offset is not null && !Simple(select.Offset)) return No();
        var binding = new SqlBinding(); binding.Add(table, select.From.Alias);
        var where = select.Where; Expr col; Expr low; Expr? high = null; string op = where.Op;
        if (op == "AND" && where.Args[0].Op == ">=" && where.Args[1].Op == "<=" && SqlAst.Equal(where.Args[0].Args[0], where.Args[1].Args[0]))
        { col = where.Args[0].Args[0]; low = where.Args[0].Args[1]; high = where.Args[1].Args[1]; op = "BETWEEN"; }
        else if (op is "=" or ">=" or "<=" or "<" or ">" or "BETWEEN")
        { col = where.Args[0]; low = where.Args[1]; if (op == "BETWEEN") high = where.Args[2]; }
        else return No();
        if (col.Op != "column" || !Simple(low) || high is not null && !Simple(high)) return No();
        int column = binding.Resolve((string)col.Value!);
        if (column != table.Primary && !table.Columns[column].Unique && !table.Indexes.Any(i => i.Expression is null && table.Column(i.Column) == column)) return No();
        var cols = new List<int>(); var names = new List<string>();
        foreach (var projection in select.Columns)
        {
            if (projection.Expr.Op == "star" && projection.Alias is null)
            {
                if (projection.Expr.Value is string alias && !alias.Equals(select.From.Alias, StringComparison.OrdinalIgnoreCase)) return No();
                for (int i = 0; i < table.Columns.Count; i++) { cols.Add(i); names.Add(table.Columns[i].Name); }
            }
            else if (projection.Expr.Op == "column") { int i = binding.Resolve((string)projection.Expr.Value!); cols.Add(i); names.Add(projection.Alias ?? table.Columns[i].Name); }
            else return No();
        }
        if (select.Order.Count > 0)
        {
            try
            {
                if (select.Order.Any(o => o.Expr.Op != "column")) return No();
                foreach (var ordering in select.Order)
                {
                    string text = (string)ordering.Expr.Value!;
                    int projected = names.FindIndex(n => n.Equals(text, StringComparison.OrdinalIgnoreCase));
                    if (projected >= 0 && cols[projected] != binding.Resolve(text)) return No();
                }
                int first = binding.Resolve((string)select.Order[0].Expr.Value!);
                int projection = names.FindIndex(n => n.Equals((string)select.Order[0].Expr.Value!, StringComparison.OrdinalIgnoreCase));
                if (projection >= 0 && cols[projection] != first) return No();
                if (select.Order.Count == 1 && first != column && !(op == "=" && first == table.Primary)) return No();
                if (select.Order.Count == 2 && (first != column || binding.Resolve((string)select.Order[1].Expr.Value!) != table.Primary || select.Order[0].Descending != select.Order[1].Descending)) return No();
                if (select.Order.Count > 2) return No();
            }
            catch (SqlException) { return No(); }
        }
        return new(true, column, op, low, high, cols.ToArray(), names.AsReadOnly(), select.Limit, select.Offset, select.Order.Count > 0 && select.Order[0].Descending);
    }
}
