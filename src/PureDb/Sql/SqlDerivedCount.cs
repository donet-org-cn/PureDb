using System.Numerics;

namespace PureDb;

/// <remarks>本部分实现确定性派生列的缓存及保持 NULL 语义的 SIMD 谓词计数。</remarks>
internal sealed partial class SqlSession
{
    /// <summary>按不可变表达式实例弱缓存去限定名的 JSON 列，不保存行值或最终计数。</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Expr, Expr> NormalizedJsonColumns = new();
    // 缓存确定性的派生列，不缓存最终筛选结果；每次查询重新计算整数谓词，
    // 避免表达式树每个节点、每一行都创建装箱临时值。
    // 派生列的有效期由所属表的已提交版本控制。
    /// <summary>对受支持的整数 JSON 派生列谓词执行专用计数，不缓存最终查询结果。</summary>
    /// <param name="table">当前操作的不可变表模式。</param>
    /// <param name="filter">待分析、绑定或求值的 SQL 表达式。</param>
    /// <param name="parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private long? DerivedCount(SqlTable table, Expr filter, IReadOnlyDictionary<string, object?> parameters)
    {
        Expr? variable = null;
        bool incompatible = false;
        void Find(Expr e)
        {
            if (e.Op == "JSON_EXTRACT" && e.Args.Length == 2 && e.Args[0].Op == "column" && e.Args[1].Op == "literal" && e.Args[1].Value is string)
            {
                if (variable is null)
                    variable = e;
                else if (!SqlAst.Equal(variable, e))
                    incompatible = true;
                return;
            }

            if (e.Op == "column")
                incompatible = true;
            foreach (var child in e.Args)
                Find(child);
        }

        Find(filter);
        if (variable is null || incompatible)
            return null;
        // 当前变量已确认是 JSON_EXTRACT(column, literal_path)，只规范化列节点。
        // 弱引用键允许解析缓存淘汰后释放计划，不缓存本次查询的参数或结果。
        var normalized = NormalizedJsonColumns.GetValue(variable, static e => e with
        {
            Items = [e.Args[0] with { Value = ((string)e.Args[0].Value!).Split('.')[^1] }, e.Args[1]]
        });
        // 正的二次幂除数取余等于零，可精确转换为位测试，负整数也成立。
        // NULL 槽位先置零，统计后再扣除，保持 SQL NULL 语义。
        if (filter.Op == "=" && filter.Args.Length == 2)
        {
            var modulo = filter.Args[0];
            var expected = filter.Args[1];
            if (modulo.Op != "%")
                (modulo, expected) = (expected, modulo);
            object? Value(Expr e) => e.Op == "literal" ? e.Value : SqlPrimaryPlan.Parameter(e, parameters);
            if (modulo.Op == "%" && modulo.Args.Length == 2 && SqlAst.Equal(modulo.Args[0], variable) && modulo.Args[1].Op is "literal" or "parameter" && expected.Op is "literal" or "parameter" && Value(expected) is long zero && zero == 0 && Value(modulo.Args[1]) is long divisor && divisor > 0 && (divisor & (divisor - 1)) == 0)
            {
                var derived = Data(table).Derived(normalized);
                if (derived.Integers)
                {
                    var numbers = derived.Numbers();
                    return CountDivisible(numbers.Values, divisor - 1) - numbers.NullCount;
                }
            }
        }

        Func<object?, long?>? Number(Expr e)
        {
            if (SqlAst.Equal(e, variable))
                return v => (long?)v;
            if (e.Op is "literal" or "parameter")
            {
                object? value = e.Op == "literal" ? e.Value : SqlPrimaryPlan.Parameter(e, parameters);
                if (value is null)
                    return _ => null;
                if (value is long n)
                    return _ => n;
                return null;
            }

            if (e.Args.Length != 2 || e.Op is not ("+" or "-" or "*" or "/" or "%"))
                return null;
            var left = Number(e.Args[0]);
            var right = Number(e.Args[1]);
            if (left is null || right is null)
                return null;
            return value =>
            {
                long? l = left(value), r = right(value);
                if (l is null || r is null)
                    return null;
                long a = l.Value, b = r.Value;
                return e.Op switch
                {
                    "+" => checked(a + b),
                    "-" => checked(a - b),
                    "*" => checked(a * b),
                    "/" => b == 0 ? null : a / b,
                    _ => b == 0 ? null : a % b
                };
            };
        }

        Func<object?, bool?>? Predicate(Expr e)
        {
            if (e.Op is "AND" or "OR")
            {
                var l = Predicate(e.Args[0]);
                var r = Predicate(e.Args[1]);
                if (l is null || r is null)
                    return null;
                return v =>
                {
                    bool? a = l(v), b = r(v);
                    return e.Op == "AND" ? a == false || b == false ? false : a is null || b is null ? null : true : a == true || b == true ? true : a is null || b is null ? null : false;
                };
            }

            if (e.Op is "IS NULL" or "IS NOT NULL")
            {
                var value = Number(e.Args[0]);
                return value is null ? null : v => e.Op == "IS NULL" ? value(v) is null : value(v) is not null;
            }

            if (e.Args.Length != 2 || e.Op is not ("=" or "!=" or "<>" or "<" or ">" or "<=" or ">="))
                return null;
            var left = Number(e.Args[0]);
            var right = Number(e.Args[1]);
            if (left is null || right is null)
                return null;
            return value =>
            {
                long? l = left(value), r = right(value);
                if (l is null || r is null)
                    return null;
                return e.Op switch
                {
                    "=" => l.Value == r.Value,
                    "!=" or "<>" => l.Value != r.Value,
                    "<" => l.Value < r.Value,
                    ">" => l.Value > r.Value,
                    "<=" => l.Value <= r.Value,
                    _ => l.Value >= r.Value
                };
            };
        }

        var predicate = Predicate(filter);
        if (predicate is null)
            return null;
        // 先按实际源别名解析，再持久化不依赖查询别名的表达式。
        var column = Data(table).Derived(normalized);
        if (!column.Integers)
            return null;
        long count = 0;
        foreach (var value in column.Values)
            if (predicate(value) == true)
                count++;
        return count;
    }

    /// <summary>通过向量位测试统计能被正二次幂整除的整数，调用方负责 NULL 修正。</summary>
    /// <param name="values">用于向量计数的连续整数列，NULL 槽位已置零。</param>
    /// <param name="mask">二次幂除数减一得到的位掩码。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    private static long CountDivisible(long[] values, long mask)
    {
        int i = 0;
        long count = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var masks = new Vector<long>(mask);
            var ones = Vector<long>.One;
            var sums = Vector<long>.Zero;
            for (; i <= values.Length - Vector<long>.Count; i += Vector<long>.Count)
                sums += Vector.Equals(new Vector<long>(values, i) & masks, Vector<long>.Zero) & ones;
            count = Vector.Sum(sums);
        }

        for (; i < values.Length; i++)
            if ((values[i] & mask) == 0)
                count++;
        return count;
    }
}
