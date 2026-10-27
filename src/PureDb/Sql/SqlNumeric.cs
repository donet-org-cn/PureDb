namespace PureDb;

/// <summary>统一快速、通用和 DML 表达式的数值运算，保持 SQLite 的整数溢出提升规则。</summary>
internal static class SqlNumeric
{
    /// <summary>对整数或实数取反，最小有符号整数提升为 REAL。</summary>
    /// <param name="value">非空的数值操作数。</param>
    /// <returns>INTEGER 或 REAL 结果。</returns>
    internal static object Negate(object value) => SqliteScalarFunctions.ArithmeticValue(value) switch
    {
        long n when n != long.MinValue => (object)(-n),
        long n => -(double)n,
        double d => -d,
        _ => throw new SqlException("Arithmetic requires numeric operands")
    };

    /// <summary>执行数值二元运算；除零返回 NULL，整数加减乘及除法溢出提升为 REAL。</summary>
    /// <param name="op">已验证的算术操作符。</param>
    /// <param name="left">左侧非空数值。</param>
    /// <param name="right">右侧非空数值。</param>
    /// <returns>保留 SQL 存储类别的结果或 SQL NULL。</returns>
    internal static object? Binary(string op, object left, object right)
    {
        left = SqliteScalarFunctions.ArithmeticValue(left);
        right = SqliteScalarFunctions.ArithmeticValue(right);
        if (op is "&" or "|" or "<<" or ">>")
        {
            long bitLeft = left is long bitIntegerLeft ? bitIntegerLeft : ToInteger((double)left);
            long bitRight = right is long bitIntegerRight ? bitIntegerRight : ToInteger((double)right);
            return op switch
            {
                "&" => bitLeft & bitRight,
                "|" => bitLeft | bitRight,
                _ => Shift(bitLeft, bitRight, op == "<<")
            };
        }
        if (left is long x && right is long y)
        {
            try
            {
                return op switch
                {
                    "+" => checked(x + y),
                    "-" => checked(x - y),
                    "*" => checked(x * y),
                    "/" => y == 0 ? null : x == long.MinValue && y == -1 ? (object)9223372036854775808d : x / y,
                    "%" => y == 0 ? null : y == -1 ? 0L : x % y,
                    _ => throw new SqlException("Unknown arithmetic operator")
                };
            }
            catch (OverflowException)
            {
                // 溢出非常少见，常用整数路径不引入 Int128 除法或逐次宽类型装箱。
            }
        }
        if (left is not (long or double) || right is not (long or double))
            throw new SqlException("Arithmetic requires numeric operands");
        double a = left is long l ? l : (double)left;
        double b = right is long r ? r : (double)right;
        if (op == "%")
        {
            long dividend = left is long exactLeft ? exactLeft : ToInteger(a);
            long divisor = right is long exactRight ? exactRight : ToInteger(b);
            return divisor == 0 ? null : (double)(divisor == -1 ? 0L : dividend % divisor);
        }
        double? result = op switch
        {
            "+" => a + b,
            "-" => a - b,
            "*" => a * b,
            "/" => b == 0 ? null : a / b,
            _ => throw new SqlException("Unknown arithmetic operator")
        };
        // SQLite 将 IEEE NaN 作为 SQL NULL，仍保留表达式计算产生的正负无穷。
        return result is double number && !double.IsNaN(number) ? (object)number : null;
    }

    /// <summary>将实数截断并饱和到 SQLite INTEGER 范围，用于混合类型取余。</summary>
    /// <param name="value">有限数值或溢出后的 IEEE 实数。</param>
    /// <returns>截断到零方向且不会越界的有符号整数。</returns>
    private static long ToInteger(double value) => value >= 9223372036854775808d ? long.MaxValue : value <= long.MinValue ? long.MinValue : (long)value;

    /// <summary>按 SQLite 规则反向处理负位移，拒绝 CLR 对位数取模的默认行为。</summary>
    /// <param name="value">待位移整数。</param>
    /// <param name="bits">可为负数的位移数量。</param>
    /// <param name="left">初始是否向左位移。</param>
    /// <returns>有符号 64 位位移结果。</returns>
    private static long Shift(long value, long bits, bool left)
    {
        if (bits < 0)
        {
            left = !left;
            bits = bits == long.MinValue ? long.MaxValue : -bits;
        }
        if (bits >= 64)
            return !left && value < 0 ? -1 : 0;
        return left ? value << (int)bits : value >> (int)bits;
    }

    /// <summary>将数值、文本或 BLOB 转换为整数后逐位取反。</summary>
    /// <param name="value">非空 SQL 标量。</param>
    /// <returns>64 位有符号按位取反结果。</returns>
    internal static long Complement(object value)
    {
        object number = SqliteScalarFunctions.ArithmeticValue(value);
        return ~(number is long integer ? integer : ToInteger((double)number));
    }

    /// <summary>累加纯整数 SUM；聚合溢出通过数据库异常报告，不按标量算术提升。</summary>
    /// <param name="sum">已累计的整数。</param>
    /// <param name="value">下一项整数。</param>
    /// <returns>精确的有符号整数和。</returns>
    internal static long AddSum(long sum, long value)
    {
        try
        {
            return checked(sum + value);
        }
        catch (OverflowException error) { throw new SqlException("Integer SUM overflow", error); }
    }
}
