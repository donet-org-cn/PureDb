using System.Runtime.CompilerServices;
using System.Text.Json;
namespace PureDb;
internal static partial class JsonReadCache
{
    private sealed class Parsed(JsonElement root)
    {
        public JsonElement Root => root;
        public Extraction? Last;
        public byte[]? Binary;
    }
    private sealed record Extraction(string Path, object? Value);
    private static readonly ConditionalWeakTable<string, Parsed> Documents = new();
    // Weak keys tie parsed representations to their owning cached SQL strings, rather than retaining documents forever.
    private static Parsed Document(string text)
    {
        try { return Documents.GetValue(text, static key => { using var document = JsonDocument.Parse(key, new JsonDocumentOptions { MaxDepth = 128 }); return new(document.RootElement.Clone()); }); }
        catch (JsonException e) { throw new SqlException("Malformed JSON: " + e.Message); }
    }
    public static JsonElement Root(string text) => Document(text).Root;
    public static byte[] Binary(string text, Func<JsonElement, byte[]> encode)
    {
        var parsed = Document(text); var binary = Volatile.Read(ref parsed.Binary);
        if (binary is null) { binary = encode(parsed.Root); binary = Interlocked.CompareExchange(ref parsed.Binary, binary, null) ?? binary; }
        return binary.ToArray(); // Never expose the cached mutable buffer.
    }
    public static object? Extract(string text, string path, Func<JsonElement, string, object?> get)
    {
        var parsed = Document(text); var cached = Volatile.Read(ref parsed.Last);
        if (cached is not null && cached.Path == path) return cached.Value;
        object? value = get(parsed.Root, path);
        Volatile.Write(ref parsed.Last, new Extraction(path, value)); return value;
    }
}
