using System.Numerics;
namespace PureDb;

internal sealed partial class SqlSession
{
    // Cache a deterministic derived column, never the filter result. Re-evaluate
    // integer predicates for every query without boxing one temporary per node/row.
    // The owner table's committed version controls the lifetime of this column.
    private long? DerivedCount(SqlTable table, Expr filter, IReadOnlyDictionary<string, object?> parameters)
    {
        Expr? variable = null; bool incompatible = false;
        void Find(Expr e)
        {
            if (e.Op == "JSON_EXTRACT" && e.Args.Length == 2 && e.Args[0].Op == "column" && e.Args[1].Op == "literal" && e.Args[1].Value is string)
            { if (variable is null) variable = e; else if (!SqlAst.Equal(variable, e)) incompatible = true; return; }
            if (e.Op == "column") incompatible = true;
            foreach (var child in e.Args) Find(child);
        }
        Find(filter); if (variable is null || incompatible) return null;
        Expr Unqualify(Expr e) => e.Op == "column" ? e with { Value = ((string)e.Value!).Split('.')[^1] } : e with { Items = e.Args.Select(Unqualify).ToArray() };
        // Integer remainder == 0 for a positive power of two is an exact bit test,
        // including negative integers. NULL slots are zeroed then subtracted.
        if (filter.Op == "=" && filter.Args.Length == 2)
        {
            var modulo = filter.Args[0]; var expected = filter.Args[1];
            if (modulo.Op != "%") (modulo, expected) = (expected, modulo);
            object? Value(Expr e) => e.Op == "literal" ? e.Value : SqlPrimaryPlan.Parameter(e, parameters);
            if (modulo.Op == "%" && modulo.Args.Length == 2 && SqlAst.Equal(modulo.Args[0], variable) &&
                modulo.Args[1].Op is "literal" or "parameter" && expected.Op is "literal" or "parameter" &&
                Value(expected) is long zero && zero == 0 && Value(modulo.Args[1]) is long divisor &&
                divisor > 0 && (divisor & (divisor - 1)) == 0)
            {
                var derived = Data(table).Derived(Unqualify(variable));
                if (derived.Integers)
                {
                    var numbers = derived.Numbers();
                    return CountDivisible(numbers.Values, divisor - 1) - numbers.NullCount;
                }
            }
        }
        Func<object?, long?>? Number(Expr e)
        {
            if (SqlAst.Equal(e, variable)) return v => (long?)v;
            if (e.Op is "literal" or "parameter")
            {
                object? value = e.Op == "literal" ? e.Value : SqlPrimaryPlan.Parameter(e, parameters);
                if (value is null) return _ => null;
                if (value is long n) return _ => n;
                return null;
            }
            if (e.Args.Length != 2 || e.Op is not ("+" or "-" or "*" or "/" or "%")) return null;
            var left = Number(e.Args[0]); var right = Number(e.Args[1]); if (left is null || right is null) return null;
            return value =>
            {
                long? l = left(value), r = right(value); if (l is null || r is null) return null;
                long a = l.Value, b = r.Value;
                return e.Op switch { "+" => checked(a+b), "-" => checked(a-b), "*" => checked(a*b), "/" => b == 0 ? null : a/b, _ => b == 0 ? null : a%b };
            };
        }
        Func<object?, bool?>? Predicate(Expr e)
        {
            if (e.Op is "AND" or "OR")
            {
                var l = Predicate(e.Args[0]); var r = Predicate(e.Args[1]); if (l is null || r is null) return null;
                return v => { bool? a = l(v), b = r(v); return e.Op == "AND" ? a == false || b == false ? false : a is null || b is null ? null : true : a == true || b == true ? true : a is null || b is null ? null : false; };
            }
            if (e.Op is "IS NULL" or "IS NOT NULL")
            {
                var value = Number(e.Args[0]); return value is null ? null : v => e.Op == "IS NULL" ? value(v) is null : value(v) is not null;
            }
            if (e.Args.Length != 2 || e.Op is not ("=" or "!=" or "<>" or "<" or ">" or "<=" or ">=")) return null;
            var left = Number(e.Args[0]); var right = Number(e.Args[1]); if (left is null || right is null) return null;
            return value =>
            {
                long? l = left(value), r = right(value); if (l is null || r is null) return null;
                return e.Op switch { "=" => l.Value == r.Value, "!=" or "<>" => l.Value != r.Value, "<" => l.Value < r.Value, ">" => l.Value > r.Value, "<=" => l.Value <= r.Value, _ => l.Value >= r.Value };
            };
        }
        var predicate = Predicate(filter); if (predicate is null) return null;
        // Resolve using the actual source alias, then persist an alias-independent expression.
        var column = Data(table).Derived(Unqualify(variable)); if (!column.Integers) return null;
        long count = 0; foreach (var value in column.Values) if (predicate(value) == true) count++; return count;
    }
    private static long CountDivisible(long[] values, long mask)
    {
        int i = 0; long count = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var masks = new Vector<long>(mask); var ones = Vector<long>.One; var sums = Vector<long>.Zero;
            for (; i <= values.Length - Vector<long>.Count; i += Vector<long>.Count)
                sums += Vector.Equals(new Vector<long>(values, i) & masks, Vector<long>.Zero) & ones;
            count = Vector.Sum(sums);
        }
        for (; i < values.Length; i++) if ((values[i] & mask) == 0) count++;
        return count;
    }
}
