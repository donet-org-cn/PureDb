using System.Globalization;
using System.Text;

namespace PureDb;

/// <summary>SQLite 常用标量函数及显式 CAST 转换，不改变普通持久列的严格类型校验。</summary>
internal static class SqliteScalarFunctions
{
    /// <summary>判断是否由本实现处理标量表达式。</summary>
    /// <param name="name">已规范化的函数或 CAST 操作名称。</param>
    /// <returns>函数是否属于受支持集合。</returns>
    internal static bool Handles(string name) => name.StartsWith("CAST:", StringComparison.Ordinal) || name is "ABS" or "SIGN" or "ROUND" or "TYPEOF" or "IFNULL" or "NULLIF" or "IIF" or "IF" or "HEX" or "ZEROBLOB" or "UNICODE" or "CHAR" or "SUBSTR" or "SUBSTRING" or "INSTR" or "REPLACE" or "TRIM" or "LTRIM" or "RTRIM" or "OCTET_LENGTH" or "CONCAT" or "CONCAT_WS";

    /// <summary>在开始枚举结果前验证函数参数数量。</summary>
    /// <param name="name">函数名称。</param>
    /// <param name="count">SQL 中提供的参数数量。</param>
    internal static void Validate(string name, int count)
    {
        bool valid = name.StartsWith("CAST:", StringComparison.Ordinal) ? count == 1 : name switch
        {
            "ABS" or "SIGN" or "TYPEOF" or "HEX" or "ZEROBLOB" or "UNICODE" or "OCTET_LENGTH" => count == 1,
            "IFNULL" or "NULLIF" or "INSTR" => count == 2,
            "IIF" or "IF" or "REPLACE" => count == 3,
            "ROUND" or "TRIM" or "LTRIM" or "RTRIM" => count is 1 or 2,
            "SUBSTR" or "SUBSTRING" => count is 2 or 3,
            "CHAR" => count >= 0,
            "CONCAT" => count >= 1,
            "CONCAT_WS" => count >= 2,
            _ => false
        };
        if (!valid)
            throw new SqlException("Invalid SQLite scalar function arity: " + name);
    }

    /// <summary>使用 SQLite 文本转换形式表示已规范化的 SQL 标量。</summary>
    /// <param name="value">输入标量。</param>
    /// <returns>文本表示；SQL NULL 对应空文本，仅供已处理 NULL 语义的函数使用。</returns>
    private static string Text(object? value) => value switch
    {
        null => "",
        byte[] bytes => SqlValueComparer.TextEncoding.GetString(bytes),
        double d => d.ToString("G15", CultureInfo.InvariantCulture) is var s && s.IndexOfAny(['.', 'E', 'e']) < 0 ? s + ".0" : s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!
    };

    /// <summary>截取 SQLite 接受的十进制数值前缀；INTEGER 转换不读取小数点或指数。</summary>
    /// <param name="value">输入文本或标量。</param>
    /// <param name="integer">是否仅允许整数前缀。</param>
    /// <returns>可供数值解析的前缀。</returns>
    private static string NumericPrefix(object value, bool integer)
    {
        string text = Text(value).TrimStart(' ', '\t', '\n', '\r', '\f', '\v');
        int at = 0;
        if (at < text.Length && text[at] is '+' or '-')
            at++;
        bool digits = false;
        while (at < text.Length && char.IsAsciiDigit(text[at]))
        {
            at++;
            digits = true;
        }
        if (!integer && at < text.Length && text[at] == '.')
        {
            at++;
            while (at < text.Length && char.IsAsciiDigit(text[at]))
            {
                at++;
                digits = true;
            }
        }
        if (!digits)
            return "0";
        if (!integer && at < text.Length && text[at] is 'e' or 'E')
        {
            int start = at++;
            if (at < text.Length && text[at] is '+' or '-')
                at++;
            int first = at;
            while (at < text.Length && char.IsAsciiDigit(text[at]))
                at++;
            if (at == first)
                at = start;
        }
        return text[..at];
    }

    /// <summary>执行明确的 SQLite CAST；整数溢出饱和，不通过该路径放宽持久列类型。</summary>
    /// <param name="value">已解包的 SQL 标量。</param>
    /// <param name="type">INTEGER、REAL、TEXT、BLOB 或 NUMERIC。</param>
    /// <returns>转换后的 SQL 标量。</returns>
    internal static object? Cast(object? value, string type)
    {
        if (value is null)
            return null;
        if (type == "TEXT")
            return Text(value);
        if (type == "BLOB")
            return value is byte[] bytes ? bytes.ToArray() : SqlValueComparer.TextEncoding.GetBytes(Text(value));
        if (type == "INTEGER")
        {
            if (value is long n)
                return n;
            string integerPrefix = NumericPrefix(value, true);
            if (value is not double && long.TryParse(integerPrefix, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long exactInteger))
                return exactInteger;
            double number = value is double d ? d : double.Parse(integerPrefix, CultureInfo.InvariantCulture);
            return number >= long.MaxValue ? long.MaxValue : number <= long.MinValue ? long.MinValue : (long)number;
        }
        string prefix = NumericPrefix(value, false);
        if (type == "NUMERIC" && value is long)
            return value;
        if (type == "NUMERIC" && long.TryParse(prefix, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long exact))
            return exact;
        double real = value is long integer ? integer : value is double scalar ? scalar : double.Parse(prefix, CultureInfo.InvariantCulture);
        if (!double.IsFinite(real))
            throw new SqlException("Nonfinite CAST result");
        if (type == "NUMERIC" && value is not double && real >= long.MinValue && real < 9223372036854775808d && real == Math.Truncate(real))
            return (long)real;
        return real;
    }

    /// <summary>按 Unicode 标量或 BLOB 字节执行 SQLite 的一基、负位置和负长度子串规则。</summary>
    /// <param name="args">子串值、位置及可选长度。</param>
    /// <returns>独立的文本或 BLOB 子串。</returns>
    private static object Substring(object?[] args)
    {
        object value = args[0]!;
        Rune[]? runes = value is byte[]? null : Text(value).EnumerateRunes().ToArray();
        int size = value is byte[] bytes ? bytes.Length : runes!.Length;
        long start = (long)Cast(args[1], "INTEGER")!, count = args.Length == 3 ? (long)Cast(args[2], "INTEGER")! : int.MaxValue;
        long begin = start > 0 ? start - 1 : start < 0 ? size + start : 0;
        if (start == 0 && count > 0)
            count--;
        if (count < 0)
        {
            begin += count;
            count = count == long.MinValue ? long.MaxValue : -count;
        }
        if (begin < 0)
        {
            count = Math.Max(0, count + begin);
            begin = 0;
        }
        begin = Math.Min(size, begin);
        count = Math.Min(size - begin, Math.Max(0, count));
        return value is byte[] blob ? blob.AsSpan((int)begin, (int)count).ToArray() : string.Concat(runes!.AsSpan((int)begin, (int)count).ToArray().Select(r => r.ToString()));
    }

    /// <summary>计算通过参数数量校验的标量函数。</summary>
    /// <param name="name">函数名称。</param>
    /// <param name="args">当前行已求值、已解包的参数。</param>
    /// <returns>符合对应函数 NULL 和类型规则的结果。</returns>
    internal static object? Eval(string name, object?[] args)
    {
        if (name.StartsWith("CAST:", StringComparison.Ordinal))
            return Cast(args[0], name[5..]);
        if (name == "TYPEOF")
            return args[0] switch
            {
                null => "null",
                long => "integer",
                double => "real",
                byte[] => "blob",
                _ => "text"
            };
        if (name == "IFNULL")
            return args[0] ?? args[1];
        if (name == "NULLIF")
            return SqlValueComparer.Instance.Equals(args[0], args[1]) ? null : args[0];
        if (name == "HEX")
            return Convert.ToHexString(args[0] is byte[] blob ? blob : SqlValueComparer.TextEncoding.GetBytes(Text(args[0])));
        if (name == "CONCAT")
            return string.Concat(args.Select(Text));
        if (name == "CONCAT_WS")
            return args[0] is null ? null : string.Join(Text(args[0]), args.Skip(1).Where(v => v is not null).Select(Text));
        if (name == "CHAR")
            return string.Concat(args.Select(v => Cast(v, "INTEGER") is long n && n >= 0 && n <= 0x10ffff && Rune.IsValid((int)n) ? new Rune((int)n).ToString() : "\ufffd"));
        if (args.Any(v => v is null))
            return null;
        if (name is "SUBSTR" or "SUBSTRING")
            return Substring(args);
        if (name == "ZEROBLOB")
        {
            long length = (long)Cast(args[0], "INTEGER")!;
            if (length > 64 * 1024 * 1024)
                throw new SqlException("ZEROBLOB exceeds 64 MiB");
            return new byte[(int)Math.Max(0, length)];
        }
        if (name == "UNICODE")
            return Text(args[0]).EnumerateRunes().FirstOrDefault().Value is var first && Text(args[0]).Length > 0 ? (long)first : null;
        if (name == "OCTET_LENGTH")
            return (long)(args[0] is byte[] b ? b.Length : SqlValueComparer.TextEncoding.GetByteCount(Text(args[0])));
        if (name == "INSTR")
        {
            if (args[0] is byte[] a && args[1] is byte[] b)
                return (long)a.AsSpan().IndexOf(b) + 1;
            string text = Text(args[0]), needle = Text(args[1]);
            int at = text.IndexOf(needle, StringComparison.Ordinal);
            return at < 0 ? 0L : (long)text[..at].EnumerateRunes().Count() + 1;
        }
        if (name == "REPLACE")
            return Text(args[1]).Length == 0 ? Text(args[0]) : Text(args[0]).Replace(Text(args[1]), Text(args[2]), StringComparison.Ordinal);
        if (name is "TRIM" or "LTRIM" or "RTRIM")
        {
            var chars = (args.Length == 1 ? " " : Text(args[1])).EnumerateRunes().ToHashSet();
            var input = Text(args[0]).EnumerateRunes().ToArray();
            int begin = 0, end = input.Length;
            if (name != "RTRIM")
                while (begin < end && chars.Contains(input[begin]))
                    begin++;
            if (name != "LTRIM")
                while (end > begin && chars.Contains(input[end - 1]))
                    end--;
            return string.Concat(input.AsSpan(begin, end - begin).ToArray().Select(r => r.ToString()));
        }
        object number = Cast(args[0], "NUMERIC")!;
        if (name == "ABS")
        {
            if (args[0] is long integer)
                return integer == long.MinValue ? throw new SqlException("integer overflow") : Math.Abs(integer);
            return Math.Abs((double)Cast(args[0], "REAL")!);
        }
        if (name == "SIGN")
        {
            if (args[0] is not (long or double) && !double.TryParse(Text(args[0]), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                return null;
            return (long)(number is long integer ? Math.Sign(integer) : Math.Sign((double)number));
        }
        if (name == "ROUND")
        {
            int digits = args.Length == 1 ? 0 : (int)Math.Clamp((long)Cast(args[1], "INTEGER")!, 0, 30);
            double value = Convert.ToDouble(number, CultureInfo.InvariantCulture);
            return digits > 15 ? value : Math.Round(value, digits, MidpointRounding.AwayFromZero);
        }
        throw new SqlException("Unsupported SQLite scalar: " + name);
    }
}
