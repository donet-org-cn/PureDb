namespace PureDb;

/// <remarks>本部分根据模式及表达式推断结果类型，不执行用户函数探测类型。</remarks>
internal sealed partial class SqlSession
{
    /// <summary>当前读取快照的模式版本。</summary>
    internal long SchemaVersion => snapshot.KeyVersion(-1);
    /// <summary>当前会话是否暂存了模式目录变更。</summary>
    internal bool SchemaChanged => _mutations is not null && _mutations.ContainsKey(-1);

    /// <summary>仅根据模式和表达式推断结果 CLR 类型，不执行用户函数来探测类型。</summary>
    /// <param name="select">当前已解析的 SELECT 查询结构。</param>
    /// <param name="parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    internal IReadOnlyList<Type> Describe(Select select, IReadOnlyDictionary<string, object?> parameters)
    {
        var binding = new SqlBinding(owner);
        if (select.From.Table.Length != 0)
            binding.Add(Catalog.Table(select.From.Table), select.From.Alias);
        foreach (var join in select.Joins)
            binding.Add(Catalog.Table(join.Source.Table), join.Source.Alias);
        Type Column(string name)
        {
            int ordinal = binding.Resolve(name);
            foreach (var source in binding.Sources)
                if (ordinal >= source.Offset && ordinal < source.Offset + source.Table.Columns.Count)
                    return source.Table.Columns[ordinal - source.Offset].Type switch
                    {
                        "INTEGER" => typeof(long),
                        "REAL" => typeof(double),
                        "TEXT" or "JSON" => typeof(string),
                        "BLOB" or "JSONB" => typeof(byte[]),
                        _ => typeof(object)
                    };
            return typeof(object);
        }

        Type Expression(Expr e) => e.Op switch
        {
            "column" => Column((string)e.Value!),
            "literal" => e.Value?.GetType() ?? typeof(object),
            "parameter" => SqlPrimaryPlan.Parameter(e, parameters)?.GetType() ?? typeof(object),
            "COUNT" or "LENGTH" or "JSON_VALID" or "JSONB_VALID" or "JSON_ARRAY_LENGTH" or "JSONB_ARRAY_LENGTH" or "=" or "!=" or "<>" or "<" or ">" or "<=" or ">=" or "AND" or "OR" or "NOT" or "IS NULL" or "IS NOT NULL" => typeof(long),
            "AVG" => typeof(double),
            "SUM" or "MIN" or "MAX" or "negate" => Expression(e.Args[0]),
            "+" or "-" or "*" or "/" or "%" => e.Args.All(a => Expression(a) == typeof(long)) ? typeof(long) : typeof(double),
            "LOWER" or "UPPER" or "||" or "JSON_TYPE" or "JSONB_TYPE" => typeof(string),
            _ when e.Op.StartsWith("JSONB", StringComparison.Ordinal) && e.Op != "JSONB_EXTRACT" => typeof(byte[]),
            _ when owner.TryFunction(e.Op, e.Args.Length, out var function) => function.ResultType,
            _ => typeof(object)
        };
        var types = new List<Type>();
        foreach (var p in select.Columns)
        {
            if (p.Expr.Op != "star")
                types.Add(Expression(p.Expr));
            else
                foreach (var source in binding.Sources.Where(s => p.Expr.Value is null || s.Alias.Equals((string)p.Expr.Value, StringComparison.OrdinalIgnoreCase)))
                    foreach (var column in source.Table.Columns)
                        types.Add(Column(source.Alias + "." + column.Name));
        }

        return types;
    }
}
