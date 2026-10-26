using System.Text;

namespace PureDb;

/// <remarks>原版格式使用 UTF-8；SQLite 文件模式在同步回调期间使用文件声明的编码。</remarks>
internal sealed partial class SqlValueComparer
{
    /// <summary>当前同步 SQL 回调的文件编码；空值表示原版 UTF-8。</summary>
    [ThreadStatic] private static Encoding? _textEncoding;
    /// <summary>当前 SQL 文本与 BLOB 转换所使用的编码。</summary>
    internal static Encoding TextEncoding => _textEncoding ?? Encoding.UTF8;

    /// <summary>保存并恢复同步调用的编码，允许用户函数嵌套访问其他编码的数据库。</summary>
    internal readonly struct TextScope : IDisposable
    {
        /// <summary>进入作用域前的编码。</summary>
        private readonly Encoding? _previous;
        /// <summary>只有跨编码调用才需要写入并恢复线程状态。</summary>
        private readonly bool _changed;
        /// <summary>进入指定编码的同步执行作用域。</summary>
        /// <param name="encoding">文件编码；空值表示原版 UTF-8。</param>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal TextScope(Encoding? encoding)
        {
            _previous = _textEncoding;
            Encoding? next = encoding?.CodePage == 65001 ? null : encoding;
            _changed = !ReferenceEquals(_previous, next);
            if (_changed)
                _textEncoding = next;
        }
        /// <summary>恢复进入作用域前的编码。</summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public void Dispose()
        {
            if (_changed)
                _textEncoding = _previous;
        }
    }

    /// <summary>按文件编码的 SQLite BINARY 字节顺序比较有效 Unicode 文本，不分配临时编码数组。</summary>
    /// <param name="left">左侧文本。</param>
    /// <param name="right">右侧文本。</param>
    /// <returns>小于、等于或大于零的排序结果。</returns>
    private static int CompareText(string left, string right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        int codePage = _textEncoding?.CodePage ?? 65001;
        if (codePage == 1201)
            return string.CompareOrdinal(left, right);
        int length = Math.Min(left.Length, right.Length);
        for (int i = 0; i < length; i++)
        {
            int a = left[i], b = right[i];
            if (a == b)
                continue;
            if (codePage == 1200)
            {
                int low = (a & 255).CompareTo(b & 255);
                return low != 0 ? low : a.CompareTo(b);
            }
            // UTF-8 以码点排序，补充平面字符位于所有有效 BMP 字符之后。
            if (a is >= 0xD800 and <= 0xDBFF && b >= 0xE000)
                return 1;
            if (b is >= 0xD800 and <= 0xDBFF && a >= 0xE000)
                return -1;
            return a.CompareTo(b);
        }
        return left.Length.CompareTo(right.Length);
    }
}
