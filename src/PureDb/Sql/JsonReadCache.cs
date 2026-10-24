using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PureDb;
/// <summary>以原始字符串为弱引用键复用 JSON 解析结果和有限数量的路径提取结果。</summary>
internal static partial class JsonReadCache
{
    /// <summary>一个 JSON 字符串对应的只读解析根及按路径缓存的派生表示。</summary>
    private sealed class Parsed
    {
        /// <summary>完整语法树只弱引用保留，已提取标量不会使整棵树永久存活。</summary>
        public WeakReference<ParsedRoot>? Root;
        /// <summary>最近一次路径提取的缓存条目。</summary>
        public Extraction? Last;
        /// <summary>另一个常用路径，交替查询两个字段时无需反复解析。</summary>
        public Extraction? Previous;
        /// <summary>由该文档派生的只读 PureDb JSONB 编码缓存。</summary>
        public byte[]? Binary;
    }

    /// <summary>可被垃圾回收器独立回收的完整 JSON 语法树。</summary>
    /// <param name = "Value">拥有独立存储的只读 JSON 根元素。</param>
    private sealed record ParsedRoot(JsonElement Value);
    /// <summary>一个 JSON 路径与已提取标量的缓存条目。</summary>
    /// <param name = "Path">用于提取值的 JSON 路径。</param>
    /// <param name = "Value">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    private sealed record Extraction(string Path, object? Value);
    private static readonly ConditionalWeakTable<string, Parsed> Documents = new();
    // 弱引用键将解析结果的生命周期绑定到 SQL 缓存字符串，避免永久保留已淘汰文档。
    /// <summary>按原始字符串弱引用取得或创建只读 JSON 解析表示。</summary>
    /// <param name = "text">待匹配的词法文本或 JSON 文本。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private static Parsed Document(string text) => Documents.GetValue(text, static _ => new());
    /// <summary>需要遍历时才取得完整语法树；标量缓存命中无需让整棵树继续存活。</summary>
    /// <param name = "parsed">字符串对应的派生结果缓存。</param>
    /// <param name = "text">仍由当前调用持有的不可变 JSON 文本。</param>
    /// <returns>本次调用期间保持有效的只读根元素。</returns>
    private static JsonElement ParsedRootValue(Parsed parsed, string text)
    {
        if (Volatile.Read(ref parsed.Root)?.TryGetTarget(out var existing) == true)
            return existing.Value;
        try
        {
            using var document = SqlJson5.Document(text);
            var root = new ParsedRoot(document.RootElement.Clone());
            Volatile.Write(ref parsed.Root, new WeakReference<ParsedRoot>(root));
            return root.Value;
        }
        catch (JsonException e)
        {
            throw new SqlException("Malformed JSON: " + e.Message);
        }
    }

    /// <summary>取得缓存 JSON 文档的只读根元素。</summary>
    /// <param name = "text">待匹配的词法文本或 JSON 文本。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    public static JsonElement Root(string text) => ParsedRootValue(Document(text), text);
    /// <summary>按需生成并复用文档的 PureDb JSONB 表示。</summary>
    /// <param name = "text">待匹配的词法文本或 JSON 文本。</param>
    /// <param name = "encode">将已解析 JSON 根编码为二进制表示的回调。</param>
    /// <returns>本次编码或复制得到的字节数组。</returns>
    public static byte[] Binary(string text, Func<JsonElement, byte[]> encode)
    {
        var parsed = Document(text);
        var binary = Volatile.Read(ref parsed.Binary);
        if (binary is null)
        {
            binary = encode(ParsedRootValue(parsed, text));
            binary = Interlocked.CompareExchange(ref parsed.Binary, binary, null) ?? binary;
        }

        return binary.ToArray(); // Never expose the cached mutable buffer.
    }

    /// <summary>按路径取得缓存提取值，限制派生结果缓存的存活范围。</summary>
    /// <param name = "text">待匹配的词法文本或 JSON 文本。</param>
    /// <param name = "path">从缓存文档中提取值的 JSON 路径。</param>
    /// <param name = "get">根据路径读取只读 JSON 值的回调。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public static object? Extract(string text, string path, Func<JsonElement, string, object?> get)
    {
        var parsed = Document(text);
        var cached = Volatile.Read(ref parsed.Last);
        if (cached is not null && cached.Path == path)
            return cached.Value;
        var previous = Volatile.Read(ref parsed.Previous);
        if (previous is not null && previous.Path == path)
            return previous.Value;
        object? value = get(ParsedRootValue(parsed, text), path);
        Volatile.Write(ref parsed.Previous, cached);
        Volatile.Write(ref parsed.Last, new Extraction(path, value));
        return value;
    }
}
