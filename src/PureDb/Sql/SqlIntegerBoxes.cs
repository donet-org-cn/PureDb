using System.Runtime.CompilerServices;

namespace PureDb;

/// <summary>复用常见不可变整数的装箱对象，降低解码索引列与布尔结果的托管分配。</summary>
internal static class SqlIntegerBoxes
{
    private static readonly object[] Values = Build();

    /// <summary>建立零到 1023 的只读整数对象；缓存不保存任何行或用户数据。</summary>
    /// <returns>固定大小的不可变数值对象集合。</returns>
    private static object[] Build()
    {
        var values = new object[1024];
        for (int i = 0; i < values.Length; i++)
            values[i] = (long)i;
        return values;
    }

    /// <summary>取得整数对象；超出小整数范围时保持普通 long 装箱语义。</summary>
    /// <param name="value">需要返回为 SQL INTEGER 的数值。</param>
    /// <returns>表示相同数值且不能被修改的 long 对象。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static object Get(long value) => (ulong)value < (ulong)Values.Length ? Values[(int)value] : value;
}
