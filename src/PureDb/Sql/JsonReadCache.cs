using System.Runtime.CompilerServices;
using System.Text.Json;

namespace PureDb;

/// <summary>以原始字符串为弱引用键复用 JSON 解析结果和有限数量的路径提取结果。</summary>
internal static partial class JsonReadCache
{
    /// <summary>一个 JSON 字符串对应的只读解析根及按路径缓存的派生表示。</summary>
    /// <param name="root">只读 JSON 文档的根元素。</param>
    private sealed class Parsed(JsonElement root)
    {
        /// <summary>当前 JSON 文档的只读解析根。</summary>
        public JsonElement Root => root;

        /// <summary>最近一次路径提取的缓存条目。</summary>
        public Extraction? Last;
        /// <summary>由该文档派生的只读 PureDb JSONB 编码缓存。</summary>
        public byte[]? Binary;
    }

    /// <summary>一个 JSON 路径与已提取标量的缓存条目。</summary>
    /// <param name="Path">用于提取值的 JSON 路径。</param>
    /// <param name="Value">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    private sealed record Extraction(string Path, object? Value);
    private static readonly ConditionalWeakTable<string, Parsed> Documents = new();
    // 弱引用键将解析结果的生命周期绑定到 SQL 缓存字符串，避免永久保留已淘汰文档。
    /// <summary>按原始字符串弱引用取得或创建只读 JSON 解析表示。</summary>
    /// <param name="text">待匹配的词法文本或 JSON 文本。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private static Parsed Document(string text)
    {
        try
        {
            return Documents.GetValue(text, static key =>
            {
                using var document = JsonDocument.Parse(key, new JsonDocumentOptions { MaxDepth = 128 });
                return new(document.RootElement.Clone());
            });
        }
        catch (JsonException e)
        {
            throw new SqlException("Malformed JSON: " + e.Message);
        }
    }

    /// <summary>取得缓存 JSON 文档的只读根元素。</summary>
    /// <param name="text">待匹配的词法文本或 JSON 文本。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    public static JsonElement Root(string text) => Document(text).Root;
    /// <summary>按需生成并复用文档的 PureDb JSONB 表示。</summary>
    /// <param name="text">待匹配的词法文本或 JSON 文本。</param>
    /// <param name="encode">将已解析 JSON 根编码为二进制表示的回调。</param>
    /// <returns>本次编码或复制得到的字节数组。</returns>
    public static byte[] Binary(string text, Func<JsonElement, byte[]> encode)
    {
        var parsed = Document(text);
        var binary = Volatile.Read(ref parsed.Binary);
        if (binary is null)
        {
            binary = encode(parsed.Root);
            binary = Interlocked.CompareExchange(ref parsed.Binary, binary, null) ?? binary;
        }

        return binary.ToArray(); // Never expose the cached mutable buffer.
    }

    /// <summary>按路径取得缓存提取值，限制派生结果缓存的存活范围。</summary>
    /// <param name="text">待匹配的词法文本或 JSON 文本。</param>
    /// <param name="path">从缓存文档中提取值的 JSON 路径。</param>
    /// <param name="get">根据路径读取只读 JSON 值的回调。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public static object? Extract(string text, string path, Func<JsonElement, string, object?> get)
    {
        var parsed = Document(text);
        var cached = Volatile.Read(ref parsed.Last);
        if (cached is not null && cached.Path == path)
            return cached.Value;
        object? value = get(parsed.Root, path);
        Volatile.Write(ref parsed.Last, new Extraction(path, value));
        return value;
    }
}
