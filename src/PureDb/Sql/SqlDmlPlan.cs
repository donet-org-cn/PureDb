namespace PureDb;

// Only the query shape is cached. Parameter values and transaction rows are never retained.
internal static class SqlPrimaryPredicate
{
    internal static Expr? Key(Expr? where, SqlBinding binding, int primary)
    {
        if (where?.Op != "=") return null;
        var column = where.Args[0]; var key = where.Args[1];
        if (column.Op != "column") (column, key) = (key, column);
        return column.Op == "column" && key.Op is "literal" or "parameter" && binding.Resolve((string)column.Value!) == primary ? key : null;
    }
    internal static bool TryId(object? value, out long id)
    {
        if (value is long n) { id = n; return n >= 0 && n < 1L << 40; }
        if (value is double d && d >= 0 && d < 1L << 40 && d == Math.Truncate(d)) { id = (long)d; return true; }
        id = 0; return false;
    }
}

internal sealed record SqlDmlPlan(bool Supported, bool Delete, SqlRuntimeValue? Key,
    (int Column, SqlRuntimeValue Value)[] Assignments, Expr[] Parameters)
{
    internal static SqlDmlPlan Build(SqlStatement statement, SqlTable table)
    {
        var binding = new SqlBinding(); binding.Add(table, table.Name);
        Expr? key = SqlPrimaryPredicate.Key(statement is Update u ? u.Where : ((Delete)statement).Where, binding, table.Primary);
        if (key is null) return new(false, false, null, [], []);
        var parameters = new List<Expr>(); var assignments = new List<(int, SqlRuntimeValue)>();
        if (statement is Update update)
        {
            foreach (var a in update.Assignments)
                assignments.Add((table.Column(a.Column), SqlRuntimeValue.Build(a.Value, binding, parameters)));
            if (assignments.Select(a => a.Item1).Distinct().Count() != assignments.Count) throw new SqlException("Duplicate assignment");
        }
        return new(true, statement is Delete, SqlRuntimeValue.Build(key, binding, parameters), assignments.ToArray(), parameters.ToArray());
    }
}

internal sealed class SqlRuntimeValue(string op, object? constant, int slot, SqlRuntimeValue[] args)
{
    internal static SqlRuntimeValue Build(Expr expression, SqlBinding binding, List<Expr> parameters)
    {
        if (expression.Op == "column") return new("column", null, binding.Resolve((string)expression.Value!), []);
        if (expression.Op == "literal") return new("literal", expression.Value, 0, []);
        if (expression.Op == "parameter")
        {
            int index = parameters.FindIndex(e => Equals(e.Value, expression.Value));
            if (index < 0) { index = parameters.Count; parameters.Add(expression); }
            return new("parameter", null, index, []);
        }
        var children = expression.Args.Select(e => Build(e, binding, parameters)).ToArray();
        SqlBinding.Validate(expression.Op, children.Length);
        return new(expression.Op, expression.Op == "JSON_EXTRACT" && expression.Args.Length == 2 && expression.Args[1].Op == "literal" ? expression.Args[1].Value : null, 0, children);
    }
    internal object? Evaluate(object?[] row, object?[] parameters)
    {
        if (op == "column") return row[slot];
        if (op == "literal") return constant;
        if (op == "parameter") return parameters[slot];
        if (op == "JSON_EXTRACT" && constant is string path) return SqlJsonFunctions.ExtractCached(args[0].Evaluate(row, parameters), path);
        if (op is "=" or "!=" or "<>" or "<" or ">" or "<=" or ">=" or "+" or "-" or "*" or "/" or "%" or "AND" or "OR")
            return SqlBinding.Binary(op, args[0].Evaluate(row, parameters), args[1].Evaluate(row, parameters));
        if (op == "IS NULL") return args[0].Evaluate(row, parameters) is null ? 1L : 0L;
        if (op == "IS NOT NULL") return args[0].Evaluate(row, parameters) is null ? 0L : 1L;
        var values = new object?[args.Length];
        for (int i = 0; i < args.Length; i++) values[i] = args[i].Evaluate(row, parameters);
        return SqlBinding.Eval(op, values);
    }
}

internal sealed partial class SqlSession
{
    private int ExecutePrimaryDml(SqlDmlPlan plan, SqlTable table, IReadOnlyDictionary<string, object?> parameters)
    {
        // Validate all parameters even if the predicate misses, matching ordinary binding.
        object?[] values = plan.Parameters.Length == 0 ? [] : new object?[plan.Parameters.Length];
        for (int i = 0; i < values.Length; i++) values[i] = SqlPrimaryPlan.Parameter(plan.Parameters[i], parameters);
        if (!SqlPrimaryPredicate.TryId(plan.Key!.Evaluate([], values), out long id)) return 0;
        var original = Row(table, id); if (original is null) return 0;
        if (plan.Delete)
        {
            if (table.HasUniqueConstraints)
                foreach (var state in UniqueConstraints(table)) if (state.Get(original) is { } value) state.Values.Remove(value);
            long key = StorageId(table, id); Mutations[key] = Mutation.Delete(key);
        }
        else
        {
            var row = original.ToArray();
            foreach (var a in plan.Assignments) row[a.Column] = a.Value.Evaluate(original, values);
            Store(table, row, id);
        }
        return 1;
    }
}
