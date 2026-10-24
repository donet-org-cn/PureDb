using System.Globalization;
using System.Text;
namespace PureDb;

internal sealed class SqlValueComparer : IComparer<object?>, IEqualityComparer<object?>
{
    public static readonly SqlValueComparer Instance = new();
    internal static object? Normalize(object? v) => v switch
    {
        SqlJson j => j, CachedBlob b => b, null => null, byte[] b => b, string s => s, bool b => b ? 1L : 0L,
        long => v,
        double d => double.IsFinite(d) ? v : throw new SqlException("Nonfinite REAL value"),
        sbyte or byte or short or ushort or int or uint => Convert.ToInt64(v, CultureInfo.InvariantCulture),
        float or decimal => Finite(Convert.ToDouble(v, CultureInfo.InvariantCulture)),
        _ => throw new SqlException($"Unsupported value type: {v.GetType().Name}")
    };
    private static double Finite(double value) => double.IsFinite(value) ? value : throw new SqlException("Nonfinite REAL value");
    private static int Rank(object? v) => v switch { null => 0, long or double => 1, string => 2, byte[] or CachedBlob => 3, _ => throw new SqlException("Invalid SQL value") };
    private static ReadOnlySpan<byte> Blob(object value) => value is CachedBlob cached ? cached.Span : (byte[])value;
    public int Compare(object? x, object? y)
    {
        x = x is SqlJson jx ? jx.Text : x; y = y is SqlJson jy ? jy.Text : y;
        int a = Rank(x), b = Rank(y); if (a != b) return a.CompareTo(b);
        if (x is null) return 0;
        if (x is long l && y is long r) return l.CompareTo(r);
        if (a == 1)
        {
            if (x is double dx && y is double dy) return dx.CompareTo(dy);
            static int Mixed(long number, double real)
            {
                if (real >= 9223372036854775808d) return -1;
                if (real < long.MinValue) return 1;
                long integral = (long)real;
                int n = number.CompareTo(integral);
                return n != 0 ? n : ((double)integral).CompareTo(real);
            }
            return x is long integer ? Mixed(integer, (double)y!) : -Mixed((long)y!, (double)x);
        }
        if (x is string s) return StringComparer.Ordinal.Compare(s, (string)y!);
        return Blob(x).SequenceCompareTo(Blob(y!));
    }
    public new bool Equals(object? x, object? y) => Compare(x, y) == 0;
    public int GetHashCode(object? x)
    {
        x = x is SqlJson j ? j.Text : x;
        if (x is null) return 0;
        if (x is long or double) return Convert.ToDouble(x, CultureInfo.InvariantCulture).GetHashCode();
        if (x is byte[] or CachedBlob) { var h = new HashCode(); foreach (byte v in Blob(x)) h.Add(v); return h.ToHashCode(); }
        return StringComparer.Ordinal.GetHashCode((string)x);
    }
    public static bool? Truth(object? v) => v switch { null => null, bool b => b, long l => l != 0, double d => d != 0, _ => throw new SqlException("Predicate requires a numeric/boolean value") };
    public static object? Boolean(bool? value) => value is null ? null : value.Value ? 1L : 0L;
}
internal sealed class RowKey(object?[] values) : IEquatable<RowKey>
{
    public bool Equals(RowKey? other) => other is not null && values.SequenceEqual(other.Values, SqlValueComparer.Instance);
    private object?[] Values => values;
    public override bool Equals(object? obj) => obj is RowKey k && Equals(k);
    public override int GetHashCode() { var h = new HashCode(); foreach (var v in values) h.Add(SqlValueComparer.Instance.GetHashCode(v)); return h.ToHashCode(); }
}
internal sealed class SqlBinding
{
    internal readonly List<(SqlTable Table, string Alias, int Offset)> Sources = [];
    public int Width => Sources.Sum(s => s.Table.Columns.Count);
    public void Add(SqlTable table, string alias)
    {
        if (Sources.Any(s => s.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase))) throw new SqlException("Duplicate table alias");
        Sources.Add((table, alias, Width));
    }
    public int Resolve(string name)
    {
        var parts = name.Split('.'); int found = -1;
        foreach (var s in Sources)
        {
            if (parts.Length == 2 && !s.Alias.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) continue;
            int i = s.Table.Columns.FindIndex(c => c.Name.Equals(parts[^1], StringComparison.OrdinalIgnoreCase));
            if (i < 0) continue;
            if (found >= 0) throw new SqlException($"Ambiguous column: {name}"); found = s.Offset + i;
        }
        return found < 0 ? throw new SqlException($"Unknown column: {name}") : found;
    }
    public Func<object?[], object?> Bind(Expr expr, IReadOnlyDictionary<string, object?> parameters)
    {
        if (expr.Op == "column") { int i = Resolve((string)expr.Value!); return r => r[i]; }
        if (expr.Op == "literal") return _ => expr.Value;
        if (expr.Op == "parameter")
        {
            string name = (string)expr.Value!;
            if (!parameters.TryGetValue(name, out var value) && !parameters.TryGetValue(name[1..], out value)) throw new SqlException($"Missing parameter {name}");
            value = SqlValueComparer.Normalize(value); return _ => value;
        }
        var args = expr.Args.Select(e => Bind(e, parameters)).ToArray();
        Validate(expr.Op, args.Length);
        if (expr.Op == "JSON_EXTRACT" && expr.Args.Length == 2 && expr.Args[1].Op == "literal" && expr.Args[1].Value is string path)
            return r => SqlJsonFunctions.ExtractCached(args[0](r), path);
        if (expr.Op is "=" or "!=" or "<>" or "<" or ">" or "<=" or ">=" or "+" or "-" or "*" or "/" or "%" or "AND" or "OR")
            return r => Binary(expr.Op, args[0](r), args[1](r));
        if (expr.Op == "IS NULL") return r => args[0](r) is null ? 1L : 0L;
        if (expr.Op == "IS NOT NULL") return r => args[0](r) is null ? 0L : 1L;
        return r => {var values=new object?[args.Length];for(int i=0;i<args.Length;i++)values[i]=args[i](r);return Eval(expr.Op,values);};
    }
    internal static object? Binary(string op, object? left, object? right)
    {
        left = left is SqlJson jl ? jl.Text : left; right = right is SqlJson jr ? jr.Text : right;
        if (op is "AND" or "OR")
        {
            bool? l = SqlValueComparer.Truth(left), r = SqlValueComparer.Truth(right);
            return SqlValueComparer.Boolean(op == "AND" ? l == false || r == false ? false : l is null || r is null ? null : true : l == true || r == true ? true : l is null || r is null ? null : false);
        }
        if (left is null || right is null) return null;
        if (op is "=" or "!=" or "<>" or "<" or ">" or "<=" or ">=")
        {
            int c = SqlValueComparer.Instance.Compare(left, right);
            return (op switch { "=" => c == 0, "!=" or "<>" => c != 0, "<" => c < 0, ">" => c > 0, "<=" => c <= 0, _ => c >= 0 }) ? 1L : 0L;
        }
        if (left is long x && right is long y)
            return op switch { "+" => checked(x+y), "-" => checked(x-y), "*" => checked(x*y), "/" => y == 0 ? null : x/y, "%" => y == 0 ? null : x%y, _ => throw new SqlException("Unknown operator") };
        if (left is not (long or double) || right is not (long or double)) throw new SqlException("Arithmetic requires numeric operands");
        double a = Convert.ToDouble(left, CultureInfo.InvariantCulture), b = Convert.ToDouble(right, CultureInfo.InvariantCulture);
        return op switch { "+" => a+b, "-" => a-b, "*" => a*b, "/" => b == 0 ? null : a/b, "%" => b == 0 ? null : a%b, _ => throw new SqlException("Unknown operator") };
    }
    internal static bool Aggregate(Expr e) => e.Op is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX" || e.Args.Any(Aggregate);
    internal static void Validate(string op, int n)
    {
        if (SqlJsonFunctions.Handles(op)) { SqlJsonFunctions.Validate(op, n); return; }
        int expected = op switch { "NOT" or "negate" or "IS NULL" or "IS NOT NULL" or "LENGTH" or "LOWER" or "UPPER" => 1, "BETWEEN" => 3, "IN" or "COALESCE" => -1, _ => 2 };
        if (op is not ("NOT" or "negate" or "IS NULL" or "IS NOT NULL" or "LENGTH" or "LOWER" or "UPPER" or "BETWEEN" or "IN" or "COALESCE" or "AND" or "OR" or "=" or "!=" or "<>" or "<" or ">" or "<=" or ">=" or "LIKE" or "||" or "+" or "-" or "*" or "/" or "%")) throw new SqlException($"Unsupported expression: {op}");
        if (expected >= 0 && n != expected || op == "IN" && n < 1 || op == "COALESCE" && n < 2) throw new SqlException("Invalid function arity");
    }
    internal static object? Eval(string op, object?[] a)
    {
        if (SqlJsonFunctions.Handles(op)) return SqlJsonFunctions.Eval(op, a);
        if (op == "LENGTH" && a[0] is CachedBlob cached) return (long)cached.Length;
        for(int i=0;i<a.Length;i++)a[i]=SqlJsonFunctions.Unwrap(a[i]);
        var cmp = SqlValueComparer.Instance;
        if (op == "IS NULL") return a[0] is null ? 1L : 0L;
        if (op == "IS NOT NULL") return a[0] is null ? 0L : 1L;
        if (op == "COALESCE") return a.FirstOrDefault(x => x is not null);
        if (op == "NOT") return SqlValueComparer.Boolean(!SqlValueComparer.Truth(a[0]));
        if (op is "AND" or "OR")
        {
            bool? l = SqlValueComparer.Truth(a[0]), r = SqlValueComparer.Truth(a[1]);
            return SqlValueComparer.Boolean(op == "AND" ? l == false || r == false ? false : l is null || r is null ? null : true : l == true || r == true ? true : l is null || r is null ? null : false);
        }
        if (op == "IN")
        {
            if (a.Length == 1) return 0L;
            if (a[0] is null) return null;
            if (a.Skip(1).Any(v => v is not null && cmp.Equals(a[0], v))) return 1L;
            return a.Skip(1).Any(v => v is null) ? null : 0L;
        }
        if (a.Any(v => v is null)) return null;
        if (op == "BETWEEN") return cmp.Compare(a[0], a[1]) >= 0 && cmp.Compare(a[0], a[2]) <= 0 ? 1L : 0L;
        if (op == "LENGTH") return a[0] is byte[] blob ? (long)blob.Length : a[0] is string text ? (long)text.EnumerateRunes().Count() : throw new SqlException("LENGTH requires TEXT/BLOB");
        if (op is "UPPER" or "LOWER") return a[0] is string str ? op == "UPPER" ? str.ToUpperInvariant() : str.ToLowerInvariant() : throw new SqlException("String function requires TEXT");
        if (op == "LIKE")
        {
            if (a[0] is not string value || a[1] is not string pattern) throw new SqlException("LIKE requires TEXT");
            int i = 0, j = 0, star = -1, mark = 0;
            while (i < value.Length)
            {
                if (j < pattern.Length && (pattern[j] == '_' || char.ToUpperInvariant(pattern[j]) == char.ToUpperInvariant(value[i]))) { i++; j++; }
                else if (j < pattern.Length && pattern[j] == '%') { star = j++; mark = i; }
                else if (star >= 0) { j = star + 1; i = ++mark; }
                else return 0L;
            }
            while (j < pattern.Length && pattern[j] == '%') j++; return j == pattern.Length ? 1L : 0L;
        }
        if (op == "||") return Convert.ToString(a[0], CultureInfo.InvariantCulture) + Convert.ToString(a[1], CultureInfo.InvariantCulture);
        if (op is "=" or "!=" or "<>" or "<" or ">" or "<=" or ">=")
        {
            int c = cmp.Compare(a[0], a[1]); return (op switch { "=" => c == 0, "!=" or "<>" => c != 0, "<" => c < 0, ">" => c > 0, "<=" => c <= 0, _ => c >= 0 }) ? 1L : 0L;
        }
        if (a.Any(x => x is not (long or double))) throw new SqlException("Arithmetic requires numeric operands");
        if (a.All(x => x is long))
        {
            long x = (long)a[0]!, y = a.Length > 1 ? (long)a[1]! : 0;
            return op switch { "negate" => checked(-x), "+" => checked(x + y), "-" => checked(x - y), "*" => checked(x * y), "/" => y == 0 ? null : x / y, "%" => y == 0 ? null : x % y, _ => throw new SqlException("Unknown operator") };
        }
        double dx = Convert.ToDouble(a[0], CultureInfo.InvariantCulture), dy = a.Length > 1 ? Convert.ToDouble(a[1], CultureInfo.InvariantCulture) : 0;
        return op switch { "negate" => -dx, "+" => dx + dy, "-" => dx - dy, "*" => dx * dy, "/" => dy == 0 ? null : dx / dy, "%" => dy == 0 ? null : dx % dy, _ => throw new SqlException("Unknown operator") };
    }
}
internal static class SqlCodec
{
    public static byte[] Encode(object?[] row)
    {
        int length = 0;
        foreach (var value in row)
        {
            length = checked(length + 1);
            if (value is long or double) length = checked(length + 8);
            else if (value is byte[] blob) length = checked(length + 4 + blob.Length);
            else if (value is string text)
            {
                int count = Encoding.UTF8.GetByteCount(text); length = checked(length + count);
                do { length = checked(length + 1); count >>= 7; } while (count != 0);
            }
            else if (value is not null) throw new SqlException("Invalid stored value");
        }
        var bytes = new byte[length]; var span = bytes.AsSpan(); int p = 0;
        foreach (var v in row)
        {
            switch (v)
            {
                case null: span[p++] = 0; break;
                case long l: span[p++] = 1; System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(span[p..], l); p+=8; break;
                case double d: span[p++] = 2; System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(span[p..], d); p+=8; break;
                case string s:
                    span[p++] = 3; uint count = (uint)Encoding.UTF8.GetByteCount(s);
                    while (count >= 128) { span[p++] = (byte)(count | 128); count >>= 7; }
                    span[p++] = (byte)count; p += Encoding.UTF8.GetBytes(s, span[p..]); break;
                case byte[] b: span[p++] = 4; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[p..], b.Length); p+=4; b.CopyTo(span[p..]); p+=b.Length; break;
                default: throw new SqlException("Invalid stored value");
            }
        }
        return bytes;
    }
    public static object?[] Decode(ReadOnlySpan<byte> bytes, int columns)
    {
        var reader = new RowReader(bytes); var row = new object?[columns];
        for (int i=0;i<columns;i++) row[i]=reader.Read(true);
        reader.Finish(); return row;
    }
    public static object?[] DecodeCached(ReadOnlyMemory<byte> bytes, int columns)
    {
        var reader = new RowReader(bytes.Span,bytes); var row = new object?[columns];
        for (int i=0;i<columns;i++) row[i] = reader.Read(true);
        reader.Finish(); return row;
    }
    public static object?[] DecodeProjection(ReadOnlySpan<byte> bytes, int columns, int[] projection)
    {
        var row = new object?[projection.Length]; var reader = new RowReader(bytes);
        for(int column=0;column<columns;column++)
        {
            bool needed=Array.IndexOf(projection,column)>=0; var value=reader.Read(needed);
            if(needed) for(int i=0;i<projection.Length;i++) if(projection[i]==column)
                row[i]=value is byte[] blob && Array.IndexOf(projection,column)<i?blob.ToArray():value;
        }
        reader.Finish(); return row;
    }
    private ref struct RowReader(ReadOnlySpan<byte> bytes, ReadOnlyMemory<byte> backing = default)
    {
        private ReadOnlySpan<byte> _remaining=bytes;
        private int _consumed;
        private ReadOnlySpan<byte> Take(int length)
        {
            if(length<0 || length>_remaining.Length)throw new InvalidDataException("Truncated SQL row");
            var result=_remaining[..length];_remaining=_remaining[length..];_consumed+=length;return result;
        }
        public object? Read(bool materialize)
        {
            byte tag=Take(1)[0];
            if(tag==0)return null;
            if(tag==1){long n=System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(Take(8));return materialize?n:null;}
            if(tag==2){double d=System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(Take(8));return materialize?d:null;}
            if(tag==3)
            {
                uint length=0;int shift=0;
                while(true){byte b=Take(1)[0];if(shift==28 && (b&0xf8)!=0)throw new InvalidDataException("Invalid string length");length|=(uint)(b&127)<<shift;if((b&128)==0)break;shift+=7;}
                var text=Take(checked((int)length));return materialize?Encoding.UTF8.GetString(text):null;
            }
            if(tag==4){int size=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(Take(4));int start=_consumed;var blob=Take(size);return !materialize?null:backing.IsEmpty?blob.ToArray():new CachedBlob(backing.Slice(start,size));}
            throw new InvalidDataException("Invalid SQL row tag");
        }
        public void Finish(){if(!_remaining.IsEmpty)throw new InvalidDataException("Unexpected SQL row bytes");}
    }
}
