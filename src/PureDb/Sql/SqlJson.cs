using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace PureDb;
internal sealed record SqlJson(string Text);
internal static class SqlJsonFunctions
{
    private static readonly byte[] Magic = "PJB1"u8.ToArray();
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 128 };
    public static bool Handles(string op) => op.StartsWith("JSON", StringComparison.Ordinal) || op is "->" or "->>";
    public static object? Unwrap(object? value) => value switch { SqlJson j => j.Text, CachedBlob b => b.Copy(), _ => value };
    public static void Validate(string op, int n)
    {
        string textOp = op.Replace("JSONB", "JSON", StringComparison.Ordinal);
        bool valid = textOp switch
        {
            "JSON" or "JSON_QUOTE" => n == 1,
            "JSON_VALID" => n is 1 or 2,
            "JSON_EXTRACT" => n >= 2,
            "JSON_TYPE" or "JSON_ARRAY_LENGTH" => n is 1 or 2,
            "JSON_SET" or "JSON_INSERT" or "JSON_REPLACE" or "JSON_ARRAY_INSERT" => n >= 1 && n % 2 == 1,
            "JSON_REMOVE" => n >= 1,
            "JSON_OBJECT" => n % 2 == 0,
            "JSON_ARRAY" => true,
            "JSON_PATCH" or "->" or "->>" => n == 2,
            _ => false
        };
        if (!valid) throw new SqlException($"Unsupported JSON function or arity: {op}");
    }
    public static string Canonical(object value) => Parse(value)?.ToJsonString() ?? "null";
    public static byte[] Binary(object value)
    {
        if (value is string or SqlJson)
        {
            return JsonReadCache.Binary(value is string s ? s : ((SqlJson)value).Text, static root =>
            {
                using var bytes = new MemoryStream(); bytes.Write(Magic);
                using var w = new BinaryWriter(bytes, Encoding.UTF8); WriteElement(w, root); return bytes.ToArray();
            });
        }
        var node = Parse(value); using var stream = new MemoryStream(); stream.Write(Magic);
        using var writer = new BinaryWriter(stream, Encoding.UTF8); Write(writer, node); return stream.ToArray();
    }
    private static void WriteElement(BinaryWriter w, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null: w.Write((byte)0); break;
            case JsonValueKind.False: w.Write((byte)1); break;
            case JsonValueKind.True: w.Write((byte)2); break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out long n)) { w.Write((byte)3); w.Write(n); }
                else { double d = element.GetDouble(); if (!double.IsFinite(d)) throw new SqlException("JSONB numeric value out of range"); w.Write((byte)4); w.Write(d); } break;
            case JsonValueKind.String: w.Write((byte)5); w.Write(element.GetString()!); break;
            case JsonValueKind.Array: w.Write((byte)6); w.Write(element.GetArrayLength()); foreach (var item in element.EnumerateArray()) WriteElement(w, item); break;
            case JsonValueKind.Object:
                // JsonNode-backed JSONB historically rejects duplicate keys. Keep that validation.
                var members = element.EnumerateObject().ToArray(); var names = new HashSet<string>(StringComparer.Ordinal);
                w.Write((byte)7); w.Write(members.Length);
                foreach (var member in members) { if (!names.Add(member.Name)) throw new SqlException("Duplicate JSON object key"); w.Write(member.Name); WriteElement(w, member.Value); } break;
            default: throw new SqlException("Invalid JSON value");
        }
    }
    public static object? ExtractCached(object? value, string path)
    {
        if (value is null) return null;
        if (value is CachedBlob cached) value = cached.Copy();
        if (value is byte[] binary) return ExtractBinary(binary, Path(path));
        string? text = value is string s ? s : value is SqlJson j ? j.Text : null;
        if (text is null) return Eval("JSON_EXTRACT", [value, path]);
        return JsonReadCache.Extract(text, path, static (root, key) =>
        {
            var element = FindElement(root, Path(key));
            return element.ValueKind switch
            {
                JsonValueKind.Undefined or JsonValueKind.Null => null,
                JsonValueKind.String => element.GetString(), JsonValueKind.True => 1L, JsonValueKind.False => 0L,
                JsonValueKind.Number => element.TryGetInt64(out long n) ? n : (object)element.GetDouble(),
                _ => new SqlJson(JsonSerializer.Serialize(element))
            };
        });
    }
    private static object? ExtractBinary(byte[] bytes, List<Part> parts)
    {
        if (!bytes.AsSpan().StartsWith(Magic)) throw new SqlException("Not PureDb JSONB");
        using var stream = new MemoryStream(bytes, false); stream.Position = Magic.Length;
        using var reader = new BinaryReader(stream, Encoding.UTF8); object? result = null; bool composite = false;
        void Read(int depth, int at, bool match)
        {
            if (depth > 128) throw new SqlException("JSONB nesting exceeds 128");
            byte tag = reader.ReadByte(); bool target = match && at == parts.Count;
            if (target && tag is 6 or 7) composite = true;
            object? scalar = tag switch
            {
                0 => null, 1 => 0L, 2 => 1L, 3 => reader.ReadInt64(),
                4 => reader.ReadDouble(), 5 => reader.ReadString(), 6 or 7 => null,
                _ => throw new SqlException("Invalid JSONB tag")
            };
            if (scalar is double d && !double.IsFinite(d)) throw new SqlException("Invalid JSONB number");
            if (target && tag < 6) result = scalar;
            if (tag < 6) return;
            int count = reader.ReadInt32(); if (count < 0 || count > bytes.Length - stream.Position) throw new SqlException("Invalid JSONB count");
            Part part = match && at < parts.Count ? parts[at] : default;
            for (int i = 0; i < count; i++)
            {
                string? key = tag == 7 ? reader.ReadString() : null;
                bool child = match && at < parts.Count && (tag == 7 ? part.Name == key : part.Name is null && !part.Append && i == (part.FromEnd ? count - part.Index : part.Index));
                Read(depth + 1, at + 1, child);
            }
        }
        try { Read(0, 0, true); if (stream.Position != bytes.Length) throw new SqlException("Trailing JSONB bytes"); }
        catch (EndOfStreamException) { throw new SqlException("Truncated JSONB"); }
        return composite ? Scalar(Find(ReadBinary(bytes), parts).Node) : result;
    }
    private static JsonNode? Parse(object? value)
    {
        try
        {
            return value switch
            {
                SqlJson j => JsonNode.Parse(j.Text, documentOptions: DocumentOptions),
                string s => JsonNode.Parse(s, documentOptions: DocumentOptions),
                byte[] b => ReadBinary(b),
                long or double => JsonSerializer.SerializeToNode(value),
                null => null,
                _ => throw new SqlException("Expected JSON text or PureDb JSONB")
            };
        }
        catch (JsonException e) { throw new SqlException("Malformed JSON: " + e.Message); }
        catch (EndOfStreamException) { throw new SqlException("Truncated JSONB"); }
    }
    private static void Write(BinaryWriter w, JsonNode? node)
    {
        if (node is null) { w.Write((byte)0); return; }
        if (node is JsonArray array) { w.Write((byte)6); w.Write(array.Count); foreach (var child in array) Write(w, child); return; }
        if (node is JsonObject obj) { w.Write((byte)7); w.Write(obj.Count); foreach (var (key, child) in obj) { w.Write(key); Write(w, child); } return; }
        var element = JsonSerializer.SerializeToElement(node);
        switch (element.ValueKind)
        {
            case JsonValueKind.False: w.Write((byte)1); break;
            case JsonValueKind.True: w.Write((byte)2); break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out long n)) { w.Write((byte)3); w.Write(n); }
                else { double d = element.GetDouble(); if (!double.IsFinite(d)) throw new SqlException("JSONB numeric value out of range"); w.Write((byte)4); w.Write(d); }
                break;
            case JsonValueKind.String: w.Write((byte)5); w.Write(element.GetString()!); break;
            default: throw new SqlException("Invalid JSON scalar");
        }
    }
    private static JsonNode? ReadBinary(byte[] bytes)
    {
        if (!bytes.AsSpan().StartsWith(Magic)) throw new SqlException("Not PureDb JSONB (SQLite JSONB binary layout is different)");
        using var stream = new MemoryStream(bytes, false); stream.Position = Magic.Length; using var reader = new BinaryReader(stream, Encoding.UTF8);
        JsonNode? Read(int depth)
        {
            if (depth > 128) throw new SqlException("JSONB nesting exceeds 128");
            byte tag = reader.ReadByte();
            if (tag == 0) return null;
            if (tag == 1) return JsonValue.Create(false);
            if (tag == 2) return JsonValue.Create(true);
            if (tag == 3) return JsonValue.Create(reader.ReadInt64());
            if (tag == 4) { double value = reader.ReadDouble(); if (!double.IsFinite(value)) throw new SqlException("Invalid JSONB number"); return JsonValue.Create(value); }
            if (tag == 5) return JsonValue.Create(reader.ReadString());
            int count = reader.ReadInt32(); if (count < 0 || count > stream.Length - stream.Position) throw new SqlException("Invalid JSONB count");
            if (tag == 6) { var array = new JsonArray(); for (int i = 0; i < count; i++) array.Add(Read(depth + 1)); return array; }
            if (tag == 7) { var obj = new JsonObject(); for (int i = 0; i < count; i++) obj.Add(reader.ReadString(), Read(depth + 1)); return obj; }
            throw new SqlException("Invalid JSONB tag");
        }
        var result = Read(0); if (stream.Position != stream.Length) throw new SqlException("Trailing JSONB bytes"); return result;
    }
    private static readonly ConditionalWeakTable<string, List<Part>> Paths = new();
    private readonly record struct Part(string? Name, int Index = 0, bool FromEnd = false, bool Append = false);
    private static List<Part> Path(object? value, bool shorthand = false)
    {
        if (value is string text && !shorthand) return Paths.GetValue(text, static s => ParsePath(s));
        return ParsePath(value, shorthand);
    }
    private static List<Part> ParsePath(object? value, bool shorthand = false)
    {
        if (shorthand && value is long shorthandIndex) return [new(null, checked((int)Math.Abs(shorthandIndex)), shorthandIndex < 0)];
        if (value is not string text) throw new SqlException("JSON path must be TEXT");
        if (shorthand && !text.StartsWith('$')) return [new(text)];
        if (text.Length == 0 || text[0] != '$') throw new SqlException("JSON path must begin with $");
        var result = new List<Part>();
        for (int p = 1; p < text.Length;)
        {
            if (text[p] == '.')
            {
                int start = ++p;
                if (p < text.Length && text[p] == '"')
                {
                    p++; bool escape = false;
                    while (p < text.Length) { if (!escape && text[p] == '"') break; if (!escape && text[p] == '\\') escape = true; else escape = false; p++; }
                    if (p == text.Length) throw new SqlException("Unclosed JSON path key");
                    string name; try { name = JsonSerializer.Deserialize<string>(text[start..++p])!; } catch (JsonException) { throw new SqlException("Invalid quoted JSON key"); }
                    result.Add(new(name));
                }
                else { while (p < text.Length && text[p] is not ('.' or '[')) p++; if (p == start) throw new SqlException("Empty JSON path key"); result.Add(new(text[start..p])); }
            }
            else if (text[p] == '[')
            {
                int start = ++p; while (p < text.Length && text[p] != ']') p++; if (p == text.Length) throw new SqlException("Unclosed JSON subscript");
                string sub = text[start..p++];
                if (sub == "#") result.Add(new(null, Append: true));
                else if (sub.StartsWith("#-", StringComparison.Ordinal) && int.TryParse(sub[2..], out int back) && back > 0) result.Add(new(null, back, true));
                else if (int.TryParse(sub, out int index) && index >= 0) result.Add(new(null, index));
                else throw new SqlException("Invalid JSON array subscript");
            }
            else throw new SqlException("Invalid JSON path");
        }
        return result;
    }
    private static (bool Exists, JsonNode? Node) Find(JsonNode? root, IEnumerable<Part> parts)
    {
        var node = root;
        foreach (var part in parts)
        {
            if (part.Name is { } name) { if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out node)) return (false, null); }
            else
            {
                if (node is not JsonArray array || part.Append) return (false, null);
                int at = part.FromEnd ? array.Count - part.Index : part.Index;
                if (at < 0 || at >= array.Count) return (false, null); node = array[at];
            }
        }
        return (true, node);
    }
    private static JsonNode? FromSql(object? value) => value switch
    {
        SqlJson or byte[] => Parse(value)?.DeepClone(),
        _ => JsonSerializer.SerializeToNode(value)
    };
    private static object? Scalar(JsonNode? node, bool binary = false)
    {
        if (node is null) return null;
        if (node is JsonArray or JsonObject) return Output(node, binary);
        var element = JsonSerializer.SerializeToElement(node);
        return element.ValueKind switch { JsonValueKind.String => element.GetString(), JsonValueKind.True => 1L, JsonValueKind.False => 0L, JsonValueKind.Number => element.TryGetInt64(out long n) ? n : (object)element.GetDouble(), _ => null };
    }
    private static object Output(JsonNode? node, bool binary) => binary ? Binary(new SqlJson(node?.ToJsonString() ?? "null")) : new SqlJson(node?.ToJsonString() ?? "null");
    private static string Type(JsonNode? node)
    {
        if (node is null) return "null";
        if (node is JsonObject) return "object"; if (node is JsonArray) return "array";
        var e = JsonSerializer.SerializeToElement(node);
        return e.ValueKind switch { JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.Number => e.TryGetInt64(out _) ? "integer" : "real", JsonValueKind.String => "text", _ => "null" };
    }
    private static JsonNode? Edit(JsonNode? root, List<Part> path, object? value, string mode)
    {
        if (path.Count == 0) return mode == "JSON_REMOVE" ? null : mode == "JSON_INSERT" || mode == "JSON_ARRAY_INSERT" ? root : FromSql(value);
        JsonNode? node = root;
        for (int i = 0; i < path.Count; i++)
        {
            var part = path[i]; bool last = i == path.Count - 1; JsonNode? next = null;
            bool create = mode is "JSON_SET" or "JSON_INSERT" or "JSON_ARRAY_INSERT";
            JsonNode NewContainer() => path[i + 1].Name is not null ? new JsonObject() : new JsonArray();
            if (part.Name is { } name)
            {
                if (node is not JsonObject obj) return root;
                bool exists = obj.TryGetPropertyValue(name, out next);
                if (last)
                {
                    if (mode == "JSON_REMOVE") obj.Remove(name);
                    else if (mode != "JSON_ARRAY_INSERT" && (mode == "JSON_SET" || mode == "JSON_INSERT" && !exists || mode == "JSON_REPLACE" && exists)) obj[name] = FromSql(value);
                }
                else if (!exists && create) obj[name] = next = NewContainer();
            }
            else
            {
                if (node is not JsonArray array) return root;
                int at = part.Append ? array.Count : part.FromEnd ? array.Count - part.Index : part.Index;
                bool exists = at >= 0 && at < array.Count;
                if (last)
                {
                    if (mode == "JSON_REMOVE" && exists) array.RemoveAt(at);
                    else if (mode == "JSON_ARRAY_INSERT" && at >= 0 && at <= array.Count) array.Insert(at, FromSql(value));
                    else if ((mode == "JSON_SET" || mode == "JSON_REPLACE") && exists) array[at] = FromSql(value);
                    else if ((mode == "JSON_SET" || mode == "JSON_INSERT") && at == array.Count) array.Add(FromSql(value));
                }
                else if (exists) next = array[at];
                else if (create && at == array.Count) { next = NewContainer(); array.Add(next); }
            }
            if (last) return root;
            node = next; if (node is null) return root;
        }
        return root;
    }
    private static JsonNode? Patch(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject p) return patch?.DeepClone();
        var obj = target as JsonObject ?? new JsonObject();
        foreach (var (key, value) in p) { if (value is null) obj.Remove(key); else { obj.TryGetPropertyValue(key, out var old); obj[key] = Patch(old?.DeepClone(), value); } }
        return obj;
    }
    private static JsonElement FindElement(JsonElement root, List<Part> parts)
    {
        foreach (var part in parts)
        {
            if (part.Name is { } name)
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out root)) return default;
            }
            else
            {
                if (root.ValueKind != JsonValueKind.Array || part.Append) return default;
                int count = root.GetArrayLength(), at = part.FromEnd ? count - part.Index : part.Index;
                if (at < 0 || at >= count) return default; root = root[at];
            }
        }
        return root;
    }
    private static object? ReadElement(string op, string text, object?[] args)
    {
        if (args.Length > 1 && args[1] is null) return null;
        var element = JsonReadCache.Root(text);
        if (args.Length > 1) element = FindElement(element, Path(args[1], op is "->" or "->>"));
        if (element.ValueKind == JsonValueKind.Undefined) return null;
        if (op == "JSON_TYPE") return element.ValueKind switch { JsonValueKind.Null => "null", JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.String => "text", JsonValueKind.True => "true", JsonValueKind.False => "false", _ => element.TryGetInt64(out _) ? "integer" : "real" };
        if (op == "JSON_ARRAY_LENGTH") return element.ValueKind == JsonValueKind.Array ? (long)element.GetArrayLength() : 0L;
        if (op == "->") return new SqlJson(JsonSerializer.Serialize(element));
        object? result = element.ValueKind switch
        {
            JsonValueKind.Null => null, JsonValueKind.String => element.GetString(), JsonValueKind.True => 1L, JsonValueKind.False => 0L,
            JsonValueKind.Number => element.TryGetInt64(out long n) ? n : (object)element.GetDouble(),
            _ => new SqlJson(JsonSerializer.Serialize(element))
        };
        return op == "->>" ? Unwrap(result) : result;
    }
    public static object? Eval(string op, object?[] args)
    {
        for (int i=0;i<args.Length;i++) if (args[i] is CachedBlob cached) args[i] = cached.Copy();
        bool binary = op.StartsWith("JSONB", StringComparison.Ordinal); string name = op.Replace("JSONB", "JSON", StringComparison.Ordinal);
        if (name == "JSON_VALID")
        {
            long flags = args.Length == 1 ? 1 : args[1] is long f && f >= 1 && f <= 15 ? f : throw new SqlException("JSON_VALID flags must be in [1,15]");
            if ((flags & 2) != 0) throw new SqlException("JSON5 validation is not implemented");
            if (args[0] is null) return null;
            try
            {
                if (args[0] is byte[] blob)
                {
                    if ((flags & 4) != 0 && blob.AsSpan().StartsWith(Magic)) return 1L;
                    if ((flags & 8) == 0) return 0L;
                }
                else if ((flags & 1) == 0) return 0L;
                Parse(args[0]); return 1L;
            }
            catch (Exception e) when (e is SqlException or JsonException or ArgumentException) { return 0L; }
        }
        if (name == "JSON_QUOTE")
        {
            if (args[0] is byte[]) throw new SqlException("JSON_QUOTE does not accept BLOB values");
            return args[0] is SqlJson j ? j : new SqlJson(JsonSerializer.Serialize(args[0]));
        }
        if (name == "JSON_ARRAY") { var array = new JsonArray(); foreach (var v in args) array.Add(FromSql(v)); return Output(array, binary); }
        if (name == "JSON_OBJECT")
        {
            var obj = new JsonObject(); for (int i = 0; i < args.Length; i += 2) { if (args[i] is not string key) throw new SqlException("JSON_OBJECT keys must be TEXT"); obj[key] = FromSql(args[i + 1]); } return Output(obj, binary);
        }
        if (args[0] is null) return null;
        if (name == "JSON" && binary) return Binary(args[0]!);
        string? readText = args[0] is string t ? t : args[0] is SqlJson jsonText ? jsonText.Text : null;
        if (!binary && readText is not null && (name is "JSON_TYPE" or "JSON_ARRAY_LENGTH" or "->" or "->>" || name == "JSON_EXTRACT" && args.Length == 2)) return ReadElement(name, readText, args);
        var root = Parse(args[0]);
        if (name == "JSON") return Output(root, binary);
        if (name is "->" or "->>")
        {
            if (args[1] is null) return null;
            var found = Find(root, Path(args[1], true));
            if (!found.Exists) return null;
            return name == "->" ? Output(found.Node, false) : Unwrap(Scalar(found.Node));
        }
        if (name == "JSON_EXTRACT")
        {
            if (args.Skip(1).Any(a => a is null)) return null;
            if (args.Length == 2) return Scalar(Find(root, Path(args[1])).Node, binary);
            var array = new JsonArray(); foreach (var path in args.Skip(1)) array.Add(Find(root, Path(path)).Node?.DeepClone()); return Output(array, binary);
        }
        if (name is "JSON_TYPE" or "JSON_ARRAY_LENGTH")
        {
            if (args.Length > 1 && args[1] is null) return null;
            var found = args.Length == 1 ? (Exists: true, Node: root) : Find(root, Path(args[1]));
            if (!found.Exists) return null;
            return name == "JSON_TYPE" ? Type(found.Node) : found.Node is JsonArray array ? (long)array.Count : 0L;
        }
        if (name == "JSON_PATCH") return args[1] is null ? null : Output(Patch(root, Parse(args[1])), binary);
        if (name == "JSON_REMOVE")
        {
            foreach (var path in args.Skip(1)) { if (path is null) return null; var parts = Path(path); if (parts.Count == 0) return null; root = Edit(root, parts, null, name); }
            return Output(root, binary);
        }
        for (int i = 1; i < args.Length; i += 2) { if (args[i] is null) continue; root = Edit(root, Path(args[i]), args[i + 1], name); }
        return Output(root, binary);
    }
}
