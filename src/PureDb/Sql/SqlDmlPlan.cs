namespace PureDb;
// 只缓存语句结构，不保留参数值或事务中的行数据。
/// <summary>识别安全的整数主键等值谓词，并验证参数值能否精确表示有效主键。</summary>
internal static class SqlPrimaryPredicate
{
    /// <summary>识别主键等值比较中的常量或参数键表达式。</summary>
    /// <param name = "where">可选 SQL 表达式；null 表示未提供该子句。</param>
    /// <param name = "binding">当前数据源的列名、偏移及函数绑定上下文。</param>
    /// <param name = "primary">整数主键列的零基序号。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    internal static Expr? Key(Expr? where, SqlBinding binding, int primary)
    {
        if (where?.Op != "=")
            return null;
        var column = where.Args[0];
        var key = where.Args[1];
        if (column.Op != "column")
            (column, key) = (key, column);
        return column.Op == "column" && key.Op is "literal" or "parameter" && binding.Resolve((string)column.Value!) == primary ? key : null;
    }

    /// <summary>仅当值能精确表示受支持的非负整数主键时转换成功。</summary>
    /// <param name = "value">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <param name = "id">转换成功时返回范围内的整数主键。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    internal static bool TryId(object? value, out long id)
    {
        if (value is long n)
        {
            id = n;
            return n >= 0 && n < 1L << 40;
        }

        if (value is double d && d >= 0 && d < 1L << 40 && d == Math.Truncate(d))
        {
            id = (long)d;
            return true;
        }

        id = 0;
        return false;
    }
}

/// <summary>主键更新或删除的已编译语句结构；不捕获调用参数和事务行。</summary>
/// <param name = "Supported">该语句结构是否可使用当前专用计划。</param>
/// <param name = "Delete">是否执行删除而非更新。</param>
/// <param name = "Key">索引键或查询主键表达式。</param>
/// <param name = "Assignments">目标列序号及赋值表达式。</param>
/// <param name = "Parameters">执行前必须验证的参数名称。</param>
internal sealed record SqlDmlPlan(bool Supported, bool Delete, SqlRuntimeValue? Key, (int Column, SqlRuntimeValue Value)[] Assignments, Expr[] Parameters)
{
    /// <summary>识别主键更新或删除并编译可复用结构；不支持时返回禁用计划。</summary>
    /// <param name = "statement">本次待编译或执行的已解析语句。</param>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    internal static SqlDmlPlan Build(SqlStatement statement, SqlTable table)
    {
        var binding = new SqlBinding();
        binding.Add(table, table.Name);
        Expr? key = SqlPrimaryPredicate.Key(statement is Update u ? u.Where : ((Delete)statement).Where, binding, table.Primary);
        if (key is null)
            return new(false, false, null, [], []);
        var parameters = new List<Expr>();
        var assignments = new List<(int, SqlRuntimeValue)>();
        if (statement is Update update)
        {
            foreach (var a in update.Assignments)
                assignments.Add((table.Column(a.Column), SqlRuntimeValue.Build(a.Value, binding, parameters)));
            if (assignments.Select(a => a.Item1).Distinct().Count() != assignments.Count)
                throw new SqlException("Duplicate assignment");
        }

        return new(true, statement is Delete, SqlRuntimeValue.Build(key, binding, parameters), assignments.ToArray(), parameters.ToArray());
    }
}

/// <summary>DML 表达式的轻量运行时节点；列、参数和常量在执行时按槽位求值。</summary>
/// <param name = "op">操作符或表达式节点类型。</param>
/// <param name = "constant">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
/// <param name = "slot">列或参数的槽位。</param>
/// <param name = "args">子表达式节点。</param>
internal sealed class SqlRuntimeValue(string op, object? constant, int slot, SqlRuntimeValue[] args)
{
    /// <summary>将支持的 DML 表达式编译为轻量节点，并收集必须检查的参数名称。</summary>
    /// <param name = "expression">待分析、绑定或求值的 SQL 表达式。</param>
    /// <param name = "binding">当前数据源的列名、偏移及函数绑定上下文。</param>
    /// <param name = "parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    internal static SqlRuntimeValue Build(Expr expression, SqlBinding binding, List<Expr> parameters)
    {
        if (expression.Op == "column")
            return new("column", null, binding.Resolve((string)expression.Value!), []);
        if (expression.Op == "literal")
            return new("literal", expression.Value, 0, []);
        if (expression.Op == "parameter")
        {
            int index = parameters.FindIndex(e => Equals(e.Value, expression.Value));
            if (index < 0)
            {
                index = parameters.Count;
                parameters.Add(expression);
            }

            return new("parameter", null, index, []);
        }

        var children = expression.Args.Select(e => Build(e, binding, parameters)).ToArray();
        SqlBinding.Validate(expression.Op, children.Length);
        return new(expression.Op, expression.Op == "JSON_EXTRACT" && expression.Args.Length == 2 && expression.Args[1].Op == "literal" ? expression.Args[1].Value : null, 0, children);
    }

    /// <summary>根据当前行和本次参数递归计算表达式值，不保留本次调用结果。</summary>
    /// <param name = "row">按对应模式列序或函数参数顺序排列的 SQL 标量值。</param>
    /// <param name = "parameters">按对应模式列序或函数参数顺序排列的 SQL 标量值。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    internal object? Evaluate(object?[] row, object?[] parameters)
    {
        if (op == "column")
            return row[slot];
        if (op == "literal")
            return constant;
        if (op == "parameter")
            return parameters[slot];
        if (op == "JSON_EXTRACT" && constant is string path)
            return SqlJsonFunctions.ExtractCached(args[0].Evaluate(row, parameters), path);
        if (op is "=" or "!=" or "<>" or "<" or ">" or "<=" or ">=" or "+" or "-" or "*" or "/" or "%" or "AND" or "OR")
            return SqlBinding.Binary(op, args[0].Evaluate(row, parameters), args[1].Evaluate(row, parameters));
        if (op == "IS NULL")
            return args[0].Evaluate(row, parameters) is null ? 1L : 0L;
        if (op == "IS NOT NULL")
            return args[0].Evaluate(row, parameters) is null ? 0L : 1L;
        if (op == "COALESCE")
        {
            // 主键 DML 专用计划也必须保持与通用绑定器相同的惰性求值。
            foreach (var argument in args)
                if (argument.Evaluate(row, parameters) is { } value)
                    return value;
            return null;
        }

        if (op is "case" or "casebase")
        {
            bool simple = op == "casebase";
            object? basis = simple ? args[0].Evaluate(row, parameters) : null;
            for (int i = simple ? 1 : 0; i < args.Length - 1; i += 2)
            {
                object? condition = args[i].Evaluate(row, parameters);
                if (simple ? basis is not null && condition is not null && SqlValueComparer.Instance.Equals(basis, condition) : SqlValueComparer.Truth(condition) == true)
                    return args[i + 1].Evaluate(row, parameters);
            }

            return args[^1].Evaluate(row, parameters);
        }

        var values = new object?[args.Length];
        for (int i = 0; i < args.Length; i++)
            values[i] = args[i].Evaluate(row, parameters);
        return SqlBinding.Eval(op, values);
    }
}

/// <remarks>本部分执行可复用的主键 DML 计划，并维护当前事务的唯一约束映射。</remarks>
internal sealed partial class SqlSession
{
    /// <summary>按编译计划验证参数并只更新或删除命中的主键行。</summary>
    /// <param name = "plan">专用查询计划，或候选行选择返回的计划说明。</param>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <param name = "parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    private int ExecutePrimaryDml(SqlDmlPlan plan, SqlTable table, IReadOnlyDictionary<string, object?> parameters)
    {
        // 即使主键谓词没有命中，也校验全部参数，保持与普通绑定路径一致。
        object?[] values = plan.Parameters.Length == 0 ? [] : new object?[plan.Parameters.Length];
        for (int i = 0; i < values.Length; i++)
            values[i] = SqlPrimaryPlan.Parameter(plan.Parameters[i], parameters);
        if (!SqlPrimaryPredicate.TryId(plan.Key!.Evaluate([], values), out long id))
            return 0;
        var original = Row(table, id);
        if (original is null)
            return 0;
        if (plan.Delete)
        {
            if (table.HasUniqueConstraints)
                foreach (var state in UniqueConstraints(table))
                    if (state.Get(original) is { } value)
                        state.Values.Remove(value);
            long key = StorageId(table, id);
            Stage(Mutation.Delete(key));
        }
        else
        {
            var row = original.ToArray();
            foreach (var a in plan.Assignments)
                row[a.Column] = a.Value.Evaluate(original, values);
            Store(table, row, id);
        }

        return 1;
    }
}
