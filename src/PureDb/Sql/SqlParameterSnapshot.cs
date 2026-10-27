using System.Collections;

namespace PureDb;

/// <summary>少量命名参数的独立只读快照，避免为批量写入的每一行分配散列表及桶数组。</summary>
internal sealed class SqlParameterSnapshot : IReadOnlyDictionary<string, object?>
{
    private readonly KeyValuePair<string, object?>[] _entries;

    /// <summary>在写入准入前复制参数及需要独立持有的 BLOB，不保留调用方字典。</summary>
    /// <param name="source">调用方提供的参数字典。</param>
    /// <param name="copy">复制可变值并保留现有参数类型的函数。</param>
    internal SqlParameterSnapshot(IReadOnlyDictionary<string, object?> source, Func<object?, object?> copy)
    {
        _entries = new KeyValuePair<string, object?>[source.Count];
        int at = 0;
        foreach (var pair in source)
        {
            if (at == _entries.Length)
                throw new InvalidOperationException("Parameter collection changed while being captured.");
            _entries[at++] = new(pair.Key, copy(pair.Value));
        }
        if (at != _entries.Length)
            throw new InvalidOperationException("Parameter collection changed while being captured.");
    }

    /// <summary>快照中的参数数量。</summary>
    public int Count => _entries.Length;

    /// <summary>按捕获顺序枚举参数名，不暴露可修改的后备数组。</summary>
    public IEnumerable<string> Keys => _entries.Select(p => p.Key);

    /// <summary>按捕获顺序枚举已复制的参数值。</summary>
    public IEnumerable<object?> Values => _entries.Select(p => p.Value);

    /// <summary>按区分大小写的完整参数名读取值，与原有 Ordinal 字典保持一致。</summary>
    /// <param name="key">需要匹配的参数名。</param>
    /// <returns>已复制的参数值；参数不存在时抛出键缺失异常。</returns>
    public object? this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

    /// <summary>判断是否存在精确同名参数。</summary>
    /// <param name="key">待查找的参数名。</param>
    /// <returns>参数是否存在。</returns>
    public bool ContainsKey(string key) => TryGetValue(key, out _);

    /// <summary>线性检查少量参数，避免散列、桶数组及额外节点的成本。</summary>
    /// <param name="key">按 Ordinal 比较的参数名。</param>
    /// <param name="value">找到时返回捕获值，未找到时返回 null。</param>
    /// <returns>是否找到该参数。</returns>
    public bool TryGetValue(string key, out object? value)
    {
        foreach (var pair in _entries)
            if (string.Equals(pair.Key, key, StringComparison.Ordinal))
            {
                value = pair.Value;
                return true;
            }
        value = null;
        return false;
    }

    /// <summary>枚举独立捕获的参数键值对。</summary>
    /// <returns>参数的只读枚举器。</returns>
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)_entries).GetEnumerator();

    /// <summary>通过非泛型接口枚举参数。</summary>
    /// <returns>参数的只读枚举器。</returns>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
