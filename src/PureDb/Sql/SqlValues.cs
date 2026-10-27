using System.Globalization;
using System.Text;

namespace PureDb;
/// <summary>SQL 存储类别排序及 NULL、整数、实数、文本和 BLOB 的一致比较与哈希。</summary>
internal sealed partial class SqlValueComparer : IComparer<object?>, IEqualityComparer<object?>
{
    /// <summary>所有 SQL 值比较和哈希共享的比较器；文本编码由同步执行作用域提供。</summary>
    public static readonly SqlValueComparer Instance = new();
    /// <summary>将受支持 CLR 标量转换为内部 SQL 存储类型，拒绝非有限实数和未知类型。</summary>
    /// <param name = "v">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    internal static object? Normalize(object? v) => v switch
    {
        SqlJson j => j,
        CachedBlob b => b,
        null => null,
        byte[] b => b,
        string s => s,
        bool b => b ? 1L : 0L,
        long => v,
        double d => double.IsFinite(d) ? v : throw new SqlException("Nonfinite REAL value"),
        sbyte or byte or short or ushort or int or uint => Convert.ToInt64(v, CultureInfo.InvariantCulture),
        float or decimal => Finite(Convert.ToDouble(v, CultureInfo.InvariantCulture)),
        _ => throw new SqlException($"Unsupported value type: {v.GetType().Name}")
    };
    /// <summary>验证实数有限，避免持久化 NaN 或无穷大造成比较不一致。</summary>
    /// <param name = "value">必须有限的实数值。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private static double Finite(double value) => double.IsFinite(value) ? value : throw new SqlException("Nonfinite REAL value");
    /// <summary>返回 SQL 存储类别的排序序号：NULL、数值、文本、BLOB。</summary>
    /// <param name = "v">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    private static int Rank(object? v) => v switch
    {
        null => 0,
        long or double => 1,
        string => 2,
        byte[] or CachedBlob => 3,
        _ => throw new SqlException("Invalid SQL value")
    };
    /// <summary>将数组或缓存 BLOB 转换为只读字节视图而不分配副本。</summary>
    /// <param name = "value">待处理的标量或参数实例，受当前接口的类型约束。</param>
    /// <returns>受当前读取保护期约束的只读字节片段。</returns>
    private static ReadOnlySpan<byte> Blob(object value) => value is CachedBlob cached ? cached.Span : (byte[])value;
    /// <summary>按 SQL 存储类别比较两个值，混合整数和实数比较保留整数精度边界。</summary>
    /// <param name = "x">左侧 SQL 值。</param>
    /// <param name = "y">右侧 SQL 值。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    public int Compare(object? x, object? y)
    {
        if (x is SqlCompositeKey cx && y is SqlCompositeKey cy)
            return cx.CompareTo(cy);
        // 整数是主键和常见排序键，先直接比较，省去 JSON 解包与两次存储类别判断。
        if (x is long l && y is long r)
            return l.CompareTo(r);
        x = x is SqlJson jx ? jx.Text : x;
        y = y is SqlJson jy ? jy.Text : y;
        int a = Rank(x), b = Rank(y);
        if (a != b)
            return a.CompareTo(b);
        if (x is null)
            return 0;
        if (a == 1)
        {
            if (x is double dx && y is double dy)
                return dx.CompareTo(dy);
            static int Mixed(long number, double real)
            {
                if (real >= 9223372036854775808d)
                    return -1;
                if (real < long.MinValue)
                    return 1;
                long integral = (long)real;
                int n = number.CompareTo(integral);
                return n != 0 ? n : ((double)integral).CompareTo(real);
            }

            return x is long integer ? Mixed(integer, (double)y!) : -Mixed((long)y!, (double)x);
        }

        if (x is string s)
            return CompareText(s, (string)y!);
        return Blob(x).SequenceCompareTo(Blob(y!));
    }

    /// <summary>按 SQL 值比较规则判断是否相等，包括 NULL 与 BLOB 内容比较。</summary>
    /// <param name = "x">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <param name = "y">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    public new bool Equals(object? x, object? y) => Compare(x, y) == 0;
    /// <summary>计算与 SQL 相等规则一致的哈希，整数和等值实数共享哈希语义。</summary>
    /// <param name = "x">需要计算 SQL 相等语义哈希的值。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    public int GetHashCode(object? x)
    {
        if (x is SqlCompositeKey composite)
            return composite.GetHashCode();
        x = x is SqlJson j ? j.Text : x;
        if (x is null)
            return 0;
        if (x is long or double)
            return Convert.ToDouble(x, CultureInfo.InvariantCulture).GetHashCode();
        if (x is byte[] or CachedBlob)
        {
            var h = new HashCode();
            foreach (byte v in Blob(x))
                h.Add(v);
            return h.ToHashCode();
        }

        return StringComparer.Ordinal.GetHashCode((string)x);
    }

    /// <summary>按 SQLite 数值前缀规则转换三值布尔；只有 SQL NULL 保留未知。</summary>
    /// <param name = "v">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <returns>数值对应的布尔结果；SQL NULL 保留为 null。</returns>
    public static bool? Truth(object? v) => v switch
    {
        null => null,
        bool b => b,
        long l => l != 0,
        double d => d != 0,
        _ => Truth(SqliteScalarFunctions.ArithmeticValue(v))
    };
    /// <summary>将可空布尔值转换为 SQL NULL、0 或 1。</summary>
    /// <param name = "value">SQL 三值逻辑中的真、假或未知。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public static object? Boolean(bool? value) => value is null ? null : SqlIntegerBoxes.Get(value.Value ? 1 : 0);
}

/// <summary>按 SQL 值语义比较的复合行键，用于分组及去重；构造后键值不可变。</summary>
/// <param name = "values">按对应模式列序或函数参数顺序排列的 SQL 标量值。</param>
internal sealed class RowKey(object?[] values) : IEquatable<RowKey>
{
    /// <summary>按 SQL 值语义逐列比较行键，NULL 值在分组或去重中相等。</summary>
    /// <param name = "other">要与当前行键比较的另一个行键。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    public bool Equals(RowKey? other) => other is not null && values.SequenceEqual(other.Values, SqlValueComparer.Instance);
    /// <summary>构造时捕获的键值；键加入哈希集合后不得修改。</summary>
    private object?[] Values => values;

    /// <summary>按 SQL 值语义逐列比较行键，NULL 值在分组或去重中相等。</summary>
    /// <param name = "obj">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    public override bool Equals(object? obj) => obj is RowKey k && Equals(k);
    /// <summary>合并各列的 SQL 哈希，以匹配行键逐列相等规则。</summary>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var v in values)
            h.Add(SqlValueComparer.Instance.GetHashCode(v));
        return h.ToHashCode();
    }
}

/// <summary>将列名、参数、内置表达式及连接级用户函数绑定为逐行执行的委托。</summary>
/// <param name = "owner">拥有函数注册和计划缓存的 SQL 数据库。</param>
/// <param name = "context">当前执行上下文，用于绑定相关子查询和外层列。</param>
/// <param name = "preferFirst">UPSERT 中的非限定列名优先绑定第一数据源，普通查询保持含糊引用报错。</param>
internal sealed class SqlBinding(SqlDatabase? owner = null, SqlSession? context = null, bool preferFirst = false)
{
    internal readonly List<(SqlTable Table, string Alias, int Offset)> Sources = [];
    /// <summary>当前已绑定数据源的总列数。</summary>
    public int Width => Sources.Sum(s => s.Table.Columns.Count);

    /// <summary>为数据源分配行数组偏移，拒绝重复别名。</summary>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <param name = "alias">当前语句内的数据源别名。</param>
    public void Add(SqlTable table, string alias)
    {
        if (Sources.Any(s => s.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)))
            throw new SqlException("Duplicate table alias");
        Sources.Add((table, alias, Width));
    }

    /// <summary>按可选表别名解析列序号，拒绝不存在或含糊的列引用。</summary>
    /// <param name = "name">要解析、规范化或查找的标识符。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    public int Resolve(string name) => FindColumn(name) is var found && found >= 0 ? found : throw new SqlException($"Unknown column: {name}");
    /// <summary>查找本层列；不存在返回负数，列引用含糊时仍然报错。</summary>
    /// <param name = "name">可带数据源别名的列引用。</param>
    /// <returns>本层行数组中的列位置，或未找到标记。</returns>
    internal int FindColumn(string name)
    {
        var parts = name.Split('.');
        int found = -1;
        foreach (var s in Sources)
        {
            if (parts.Length == 2 && !s.Alias.Equals(parts[0], StringComparison.OrdinalIgnoreCase))
                continue;
            int i = s.Table.Columns.FindIndex(c => c.Name.Equals(parts[^1], StringComparison.OrdinalIgnoreCase));
            if (i < 0)
                continue;
            if (found >= 0)
                throw new SqlException($"Ambiguous column: {name}");
            found = s.Offset + i;
            if (preferFirst && parts.Length == 1)
                return found;
        }

        return found;
    }

    /// <summary>将表达式绑定为逐行委托，并在枚举前校验列、参数和函数。</summary>
    /// <param name = "expr">待分析、绑定或求值的 SQL 表达式。</param>
    /// <param name = "parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public Func<object?[], object?> Bind(Expr expr, IReadOnlyDictionary<string, object?> parameters)
    {
        if (expr.Window is not null)
            return context?.BindWindow(expr) ?? throw new SqlException("Window requires an execution context");
        if (expr.Op == "column")
        {
            int i = FindColumn((string)expr.Value!);
            if (i < 0)
            {
                object? value = context?.OuterColumn((string)expr.Value!) ?? throw new SqlException($"Unknown column: {expr.Value}");
                return _ => value is SqlOuterValue outer ? outer.Value : value;
            }

            return r => r[i];
        }

        if (expr.Subquery is not null)
            return context?.BindSubquery(expr, this, parameters) ?? throw new SqlException("Subquery requires an execution context");
        if (expr.Op == "literal")
            return _ => expr.Value;
        if (expr.Op == "parameter")
        {
            string name = (string)expr.Value!;
            if (!parameters.TryGetValue(name, out var value) && !parameters.TryGetValue(name[1..], out value))
                throw new SqlException($"Missing parameter {name}");
            value = SqlValueComparer.Normalize(value);
            return _ => value;
        }

        var args = expr.Args.Select(e => Bind(e, parameters)).ToArray();
        if (expr.Op == "raise")
            return row => context?.Raise(args[0](row)) ?? throw new SqlException("RAISE requires a trigger");
        if (expr.Op == "composite")
            return row =>
            {
                var values = args.Select(f => f(row)).ToArray();
                return values.Any(v => v is null) ? null : new SqlCompositeKey(values);
            };
        if (TryFunction(expr.Op, args.Length, out var function))
            return function.Bind(args);
        Validate(expr.Op, args.Length);
        if (expr.Op is "case" or "casebase")
            return row =>
            {
                bool simple = expr.Op == "casebase";
                object? basis = simple ? args[0](row) : null;
                for (int i = simple ? 1 : 0; i < args.Length - 1; i += 2)
                {
                    object? condition = args[i](row);
                    if (simple ? basis is not null && condition is not null && SqlValueComparer.Instance.Equals(basis, condition) : SqlValueComparer.Truth(condition) == true)
                        return args[i + 1](row);
                }

                return args[^1](row);
            };
        if (expr.Op == "COALESCE")
            return row =>
            {
                // 绑定阶段仍检查全部参数，执行阶段只计算找到首个非 NULL 值所需的分支。
                foreach (var argument in args)
                    if (argument(row) is { } value)
                        return value;
                return null;
            };
        if (expr.Op is "IFNULL" or "IIF" or "IF")
            return row => expr.Op == "IFNULL" ? args[0](row) ?? args[1](row) : SqlValueComparer.Truth(args[0](row)) == true ? args[1](row) : args[2](row);
        if (expr.Op == "JSON_EXTRACT" && expr.Args.Length == 2 && expr.Args[1].Op == "literal" && expr.Args[1].Value is string path)
            return r => SqlJsonFunctions.ExtractCached(args[0](r), path);
        if (expr.Op is "=" or "!=" or "<>" or "<" or ">" or "<=" or ">=" or "+" or "-" or "*" or "/" or "%" or "&" or "|" or "<<" or ">>" or "IS" or "IS NOT" or "AND" or "OR")
            return r => Binary(expr.Op, args[0](r), args[1](r));
        if (expr.Op == "IS NULL")
            return r => args[0](r) is null ? 1L : 0L;
        if (expr.Op == "IS NOT NULL")
            return r => args[0](r) is null ? 0L : 1L;
        return r =>
        {
            var values = new object?[args.Length];
            for (int i = 0; i < args.Length; i++)
                values[i] = args[i](r);
            return Eval(expr.Op, values);
        };
    }

    /// <summary>执行常用二元数值或比较操作，减少通用参数数组分配。</summary>
    /// <param name = "op">当前 SQL 操作符或函数名。</param>
    /// <param name = "left">二元操作的左侧 SQL 标量。</param>
    /// <param name = "right">二元操作的右侧 SQL 标量。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    internal static object? Binary(string op, object? left, object? right)
    {
        left = left is SqlJson jl ? jl.Text : left;
        right = right is SqlJson jr ? jr.Text : right;
        if (op is "IS" or "IS NOT")
            return SqlValueComparer.Boolean(SqlValueComparer.Instance.Equals(left, right) == (op == "IS"));
        if (op is "AND" or "OR")
        {
            bool? l = SqlValueComparer.Truth(left), r = SqlValueComparer.Truth(right);
            return SqlValueComparer.Boolean(op == "AND" ? l == false || r == false ? false : l is null || r is null ? null : true : l == true || r == true ? true : l is null || r is null ? null : false);
        }

        if (left is null || right is null)
            return null;
        if (op is "=" or "!=" or "<>" or "<" or ">" or "<=" or ">=")
        {
            int c = SqlValueComparer.Instance.Compare(left, right);
            return (op switch
            {
                "=" => c == 0,
                "!=" or "<>" => c != 0,
                "<" => c < 0,
                ">" => c > 0,
                "<=" => c <= 0,
                _ => c >= 0
            }) ? 1L : 0L;
        }

        return SqlNumeric.Binary(op, left, right);
    }

    /// <summary>递归判断表达式中是否包含受支持聚合函数。</summary>
    /// <param name = "e">待分析、绑定或求值的 SQL 表达式。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    internal static bool Aggregate(Expr e) => e.Window is null && e.Op is ("COUNT" or "SUM" or "AVG" or "MIN" or "MAX") || e.Args.Any(Aggregate);
    /// <summary>从所属连接解析指定名称和参数数量的用户函数。</summary>
    /// <param name = "name">要解析、规范化或查找的标识符。</param>
    /// <param name = "arity">参数数量；-1 表示可变参数重载。</param>
    /// <param name = "function">成功时返回当前连接中匹配的函数定义。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    internal bool TryFunction(string name, int arity, out SqlFunctionDefinition function)
    {
        function = null!;
        return owner is not null && owner.TryFunction(name, arity, out function);
    }

    /// <summary>判断名称是否属于不允许用户替换的内置函数命名空间。</summary>
    /// <param name = "name">要解析、规范化或查找的标识符。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    internal static bool BuiltinFunction(string name) => SqlJsonFunctions.Handles(name) || name is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX" or "LENGTH" or "LOWER" or "UPPER" or "COALESCE" or "RAISE" or "CASE" or "EXISTS" or "ROW_NUMBER" or "RANK" or "DENSE_RANK" or "PERCENT_RANK" or "CUME_DIST" or "NTILE" or "LAG" or "LEAD" or "FIRST_VALUE" or "LAST_VALUE" or "NTH_VALUE";
    /// <summary>检查通用表达式操作符及其参数数量。</summary>
    /// <param name = "op">当前 SQL 操作符或函数名。</param>
    /// <param name = "n">表达式参数数量。</param>
    internal static void Validate(string op, int n)
    {
        if (SqliteScalarFunctions.Handles(op))
        {
            SqliteScalarFunctions.Validate(op, n);
            return;
        }
        if (op is "case" or "casebase")
        {
            if (op == "case" ? n >= 3 && n % 2 == 1 : n >= 4 && n % 2 == 0)
                return;
            throw new SqlException("Invalid CASE expression");
        }

        if (SqlJsonFunctions.Handles(op))
        {
            SqlJsonFunctions.Validate(op, n);
            return;
        }

        int expected = op switch
        {
            "NOT" or "negate" or "~" or "IS TRUE" or "IS FALSE" or "IS NOT TRUE" or "IS NOT FALSE" or "IS NULL" or "IS NOT NULL" or "LENGTH" or "LOWER" or "UPPER" => 1,
            "BETWEEN" => 3,
            "IN" or "COALESCE" => -1,
            _ => 2
        };
        if (op is not ("NOT" or "negate" or "~" or "IS" or "IS NOT" or "IS TRUE" or "IS FALSE" or "IS NOT TRUE" or "IS NOT FALSE" or "IS NULL" or "IS NOT NULL" or "LENGTH" or "LOWER" or "UPPER" or "BETWEEN" or "IN" or "COALESCE" or "AND" or "OR" or "=" or "!=" or "<>" or "<" or ">" or "<=" or ">=" or "LIKE" or "||" or "+" or "-" or "*" or "/" or "%" or "&" or "|" or "<<" or ">>"))
            throw new SqlException($"Unsupported expression: {op}");
        if (expected >= 0 && n != expected || op == "IN" && n < 1 || op == "COALESCE" && n < 2)
            throw new SqlException("Invalid function arity");
    }

    /// <summary>按 SQL 三值逻辑及存储类型规则计算通用表达式。</summary>
    /// <param name = "op">当前 SQL 操作符或函数名。</param>
    /// <param name = "a">按对应模式列序或函数参数顺序排列的 SQL 标量值。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    internal static object? Eval(string op, object?[] a)
    {
        if (SqlJsonFunctions.Handles(op))
            return SqlJsonFunctions.Eval(op, a);
        if (op == "LENGTH" && a[0] is CachedBlob cached)
            return (long)cached.Length;
        for (int i = 0; i < a.Length; i++)
            a[i] = SqlJsonFunctions.Unwrap(a[i]);
        if (SqliteScalarFunctions.Handles(op))
            return SqliteScalarFunctions.Eval(op, a);
        var cmp = SqlValueComparer.Instance;
        if (op is "IS" or "IS NOT")
            return Binary(op, a[0], a[1]);
        if (op is "IS TRUE" or "IS FALSE" or "IS NOT TRUE" or "IS NOT FALSE")
        {
            bool matches = SqlValueComparer.Truth(a[0]) == op.EndsWith("TRUE", StringComparison.Ordinal);
            return SqlValueComparer.Boolean(op.StartsWith("IS NOT", StringComparison.Ordinal) ? !matches : matches);
        }
        if (op == "IS NULL")
            return a[0] is null ? 1L : 0L;
        if (op == "IS NOT NULL")
            return a[0] is null ? 0L : 1L;
        if (op == "COALESCE")
            return a.FirstOrDefault(x => x is not null);
        if (op is "case" or "casebase")
        {
            bool simple = op == "casebase";
            for (int i = simple ? 1 : 0; i < a.Length - 1; i += 2)
                if (simple ? a[0] is not null && a[i] is not null && cmp.Equals(a[0], a[i]) : SqlValueComparer.Truth(a[i]) == true)
                    return a[i + 1];
            return a[^1];
        }

        if (op == "NOT")
            return SqlValueComparer.Boolean(!SqlValueComparer.Truth(a[0]));
        if (op is "AND" or "OR")
        {
            bool? l = SqlValueComparer.Truth(a[0]), r = SqlValueComparer.Truth(a[1]);
            return SqlValueComparer.Boolean(op == "AND" ? l == false || r == false ? false : l is null || r is null ? null : true : l == true || r == true ? true : l is null || r is null ? null : false);
        }

        if (op == "IN")
        {
            if (a.Length == 1)
                return 0L;
            if (a[0] is null)
                return null;
            if (a.Skip(1).Any(v => v is not null && cmp.Equals(a[0], v)))
                return 1L;
            return a.Skip(1).Any(v => v is null) ? null : 0L;
        }

        if (a.Any(v => v is null))
            return null;
        if (op == "BETWEEN")
            return cmp.Compare(a[0], a[1]) >= 0 && cmp.Compare(a[0], a[2]) <= 0 ? 1L : 0L;
        if (op == "LENGTH")
            return a[0] is byte[] blob ? (long)blob.Length : a[0] is string text ? (long)text.EnumerateRunes().Count() : throw new SqlException("LENGTH requires TEXT/BLOB");
        if (op is "UPPER" or "LOWER")
            return a[0] is string str ? op == "UPPER" ? str.ToUpperInvariant() : str.ToLowerInvariant() : throw new SqlException("String function requires TEXT");
        if (op == "LIKE")
        {
            if (a[0] is not string value || a[1] is not string pattern)
                throw new SqlException("LIKE requires TEXT");
            int i = 0, j = 0, star = -1, mark = 0;
            while (i < value.Length)
            {
                if (j < pattern.Length && (pattern[j] == '_' || char.ToUpperInvariant(pattern[j]) == char.ToUpperInvariant(value[i])))
                {
                    i++;
                    j++;
                }
                else if (j < pattern.Length && pattern[j] == '%')
                {
                    star = j++;
                    mark = i;
                }
                else if (star >= 0)
                {
                    j = star + 1;
                    i = ++mark;
                }
                else
                    return 0L;
            }

            while (j < pattern.Length && pattern[j] == '%')
                j++;
            return j == pattern.Length ? 1L : 0L;
        }

        if (op == "||")
            return Convert.ToString(a[0], CultureInfo.InvariantCulture) + Convert.ToString(a[1], CultureInfo.InvariantCulture);
        if (op is "=" or "!=" or "<>" or "<" or ">" or "<=" or ">=")
        {
            int c = cmp.Compare(a[0], a[1]);
            return (op switch
            {
                "=" => c == 0,
                "!=" or "<>" => c != 0,
                "<" => c < 0,
                ">" => c > 0,
                "<=" => c <= 0,
                _ => c >= 0
            }) ? 1L : 0L;
        }

        return op == "negate" ? SqlNumeric.Negate(a[0]!) : op == "~" ? SqlNumeric.Complement(a[0]!) : SqlNumeric.Binary(op, a[0]!, a[1]!);
    }
}

/// <summary>原生 SQL 行的紧凑二进制编码、按需解码和投影读取。</summary>
internal static class SqlCodec
{
    /// <summary>将一行 SQL 标量写成紧凑二进制表示，预先精确计算容量以避免缓冲扩容。</summary>
    /// <param name = "row">按存储列顺序排列、已经规范化的完整 SQL 行。</param>
    /// <returns>本次编码或复制得到的字节数组。</returns>
    public static byte[] Encode(object?[] row)
    {
        int length = 0;
        foreach (var value in row)
        {
            length = checked(length + 1);
            if (value is long or double)
                length = checked(length + 8);
            else if (value is byte[] blob)
                length = checked(length + 4 + blob.Length);
            else if (value is string text)
            {
                int count = Encoding.UTF8.GetByteCount(text);
                length = checked(length + count);
                do
                {
                    length = checked(length + 1);
                    count >>= 7;
                }
                while (count != 0);
            }
            else if (value is not null)
                throw new SqlException("Invalid stored value");
        }

        var bytes = new byte[length];
        var span = bytes.AsSpan();
        int p = 0;
        foreach (var v in row)
        {
            switch (v)
            {
                case null:
                    span[p++] = 0;
                    break;
                case long l:
                    span[p++] = 1;
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(span[p..], l);
                    p += 8;
                    break;
                case double d:
                    span[p++] = 2;
                    System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(span[p..], d);
                    p += 8;
                    break;
                case string s:
                    span[p++] = 3;
                    uint count = (uint)Encoding.UTF8.GetByteCount(s);
                    while (count >= 128)
                    {
                        span[p++] = (byte)(count | 128);
                        count >>= 7;
                    }

                    span[p++] = (byte)count;
                    p += Encoding.UTF8.GetBytes(s, span[p..]);
                    break;
                case byte[] b:
                    span[p++] = 4;
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[p..], b.Length);
                    p += 4;
                    b.CopyTo(span[p..]);
                    p += b.Length;
                    break;
                default:
                    throw new SqlException("Invalid stored value");
            }
        }

        return bytes;
    }

    /// <summary>完整解码一行并检查没有截断或多余尾部字节。</summary>
    /// <param name = "bytes">待校验或解码的只读编码字节。</param>
    /// <param name = "columns">行编码中预期存在的列数量。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public static object?[] Decode(ReadOnlySpan<byte> bytes, int columns)
    {
        var reader = new RowReader(bytes);
        var row = new object?[columns];
        for (int i = 0; i < columns; i++)
            row[i] = reader.Read(true);
        reader.Finish();
        return row;
    }

    /// <summary>解码缓存行，BLOB 可保留对已提交只读页的内部视图。</summary>
    /// <param name = "bytes">包含完整编码行的只读内存，可用于保留内部 BLOB 视图。</param>
    /// <param name = "columns">行编码中预期存在的列数量。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public static object?[] DecodeCached(ReadOnlyMemory<byte> bytes, int columns)
    {
        var reader = new RowReader(bytes.Span, bytes);
        var row = new object?[columns];
        for (int i = 0; i < columns; i++)
            row[i] = reader.Read(true);
        reader.Finish();
        return row;
    }

    /// <summary>按实际解码类型估算缓存行，不再为每列统一预留 80 字节而错误拒绝可容纳的整表。</summary>
    /// <param name="row">已经解码的内部缓存行。</param>
    /// <param name="encodedBytes">行的源编码长度，保守计入 BLOB 可能保留的只读后备数据。</param>
    /// <returns>行数组、装箱数值、UTF-16 文本、BLOB 视图和源编码的保守字节估算；不含辅助索引。</returns>
    internal static long CacheRowBytes(object?[] row, int encodedBytes)
    {
        // 源页可能由多行共享，仍按各行源编码及一个数组头计费，避免把保留的 BLOB 页视为免费。
        long bytes = encodedBytes + 32L + 24L + row.Length * (long)IntPtr.Size;
        foreach (object? value in row)
            bytes += value switch
            {
                long or double => 24L,
                string text => (24L + text.Length * 2L + 7) & ~7L,
                CachedBlob => 32L,
                byte[] blob => (24L + blob.Length + 7) & ~7L,
                null => 0,
                _ => throw new InvalidDataException("Unexpected cached SQL value")
            };
        return bytes;
    }

    /// <summary>扫描编码行并只物化请求列，重复 BLOB 投影分别复制以隔离结果。</summary>
    /// <param name = "bytes">待校验或解码的只读编码字节。</param>
    /// <param name = "columns">行编码中预期存在的列数量。</param>
    /// <param name = "projection">需要输出的列序号数组，允许同一列重复出现。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public static object?[] DecodeProjection(ReadOnlySpan<byte> bytes, int columns, int[] projection)
    {
        if (projection.Length == columns)
        {
            int ordinal = 0;
            while (ordinal < columns && projection[ordinal] == ordinal)
                ordinal++;
            if (ordinal == columns)
                return Decode(bytes, columns);
        }
        var row = new object?[projection.Length];
        var reader = new RowReader(bytes);
        for (int column = 0; column < columns; column++)
        {
            bool needed = Array.IndexOf(projection, column) >= 0;
            var value = reader.Read(needed);
            if (needed)
                for (int i = 0; i < projection.Length; i++)
                    if (projection[i] == column)
                        row[i] = value is byte[] blob && Array.IndexOf(projection, column) < i ? blob.ToArray() : value;
        }

        reader.Finish();
        return row;
    }

    /// <summary>带边界校验的栈上行读取器，支持跳过非投影列并保留已提交 BLOB 的只读视图。</summary>
    /// <param name = "bytes">待校验或解码的只读编码字节。</param>
    /// <param name = "backing">可选的已提交缓存内存，用于建立只读 BLOB 视图。</param>
    private ref struct RowReader(ReadOnlySpan<byte> bytes, ReadOnlyMemory<byte> backing = default)
    {
        private ReadOnlySpan<byte> _remaining = bytes;
        private int _consumed;
        /// <summary>检查剩余长度并消费指定字节数，禁止越界读取截断行。</summary>
        /// <param name = "length">本次处理的字节长度或请求长度。</param>
        /// <returns>受当前读取保护期约束的只读字节片段。</returns>
        private ReadOnlySpan<byte> Take(int length)
        {
            if (length < 0 || length > _remaining.Length)
                throw new InvalidDataException("Truncated SQL row");
            var result = _remaining[..length];
            _remaining = _remaining[length..];
            _consumed += length;
            return result;
        }

        /// <summary>读取一个编码标量；无需物化时只前移游标，BLOB 可保留缓存视图。</summary>
        /// <param name = "materialize">是否解码并返回值；为 false 时只跳过编码字节。</param>
        /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
        public object? Read(bool materialize)
        {
            byte tag = Take(1)[0];
            if (tag == 0)
                return null;
            if (tag == 1)
            {
                long n = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(Take(8));
                return materialize ? SqlIntegerBoxes.Get(n) : null;
            }

            if (tag == 2)
            {
                double d = System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
                return materialize ? d : null;
            }

            if (tag == 3)
            {
                uint length = 0;
                int shift = 0;
                while (true)
                {
                    byte b = Take(1)[0];
                    if (shift == 28 && (b & 0xf8) != 0)
                        throw new InvalidDataException("Invalid string length");
                    length |= (uint)(b & 127) << shift;
                    if ((b & 128) == 0)
                        break;
                    shift += 7;
                }

                var text = Take(checked((int)length));
                return materialize ? Encoding.UTF8.GetString(text) : null;
            }

            if (tag == 4)
            {
                int size = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(Take(4));
                int start = _consumed;
                var blob = Take(size);
                return !materialize ? null : backing.IsEmpty ? blob.ToArray() : new CachedBlob(backing.Slice(start, size));
            }

            throw new InvalidDataException("Invalid SQL row tag");
        }

        /// <summary>验证一行所有预期列读完后不存在额外字节。</summary>
        public void Finish()
        {
            if (!_remaining.IsEmpty)
                throw new InvalidDataException("Unexpected SQL row bytes");
        }
    }
}
