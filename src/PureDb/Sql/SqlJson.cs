using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PureDb;
/// <summary>携带 JSON 文本子类型的内部值，使 JSON 构造函数能区分文档与普通字符串。</summary>
/// <param name = "Text">JSON 文本或词法单元原始内容。</param>
internal sealed record SqlJson(string Text);
/// <summary>JSON 文本、PureDb JSONB、路径查询及修改函数的语义实现。</summary>
internal static partial class SqlJsonFunctions
{
    private static readonly byte[] Magic = "PJB1"u8.ToArray();
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        MaxDepth = 128
    };
    /// <summary>判断操作符是否属于当前 JSON 函数命名空间或 JSON 箭头操作符。</summary>
    /// <param name = "op">当前 SQL 操作符或函数名。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    public static bool Handles(string op) => op.StartsWith("JSON", StringComparison.Ordinal) || op is "->" or "->>";
    /// <summary>将内部 JSON 子类型或只读 BLOB 视图转为公开标量，BLOB 返回副本。</summary>
    /// <param name = "value">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public static object? Unwrap(object? value) => value switch
    {
        SqlJson j => j.Text,
        CachedBlob b => b.Copy(),
        _ => value
    };
    /// <summary>校验 JSON 函数名称与参数数量。</summary>
    /// <param name = "op">当前 SQL 操作符或函数名。</param>
    /// <param name = "n">表达式参数数量。</param>
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
        if (!valid)
            throw new SqlException($"Unsupported JSON function or arity: {op}");
    }

    /// <summary>将受支持输入解析并转换为规范 JSON 文本。</summary>
    /// <param name = "value">待处理的标量或参数实例，受当前接口的类型约束。</param>
    /// <returns>按当前方法语义生成、解析或规范化后的文本。</returns>
    public static string Canonical(object value) => Parse(value)?.ToJsonString() ?? "null";
    /// <summary>将输入转换为 PureDb 的 PJB1 二进制 JSON；该编码不等同于 SQLite JSONB。</summary>
    /// <param name = "value">待处理的标量或参数实例，受当前接口的类型约束。</param>
    /// <returns>本次编码或复制得到的字节数组。</returns>
    public static byte[] Binary(object value)
    {
        if (value is string or SqlJson)
        {
            return JsonReadCache.Binary(value is string s ? s : ((SqlJson)value).Text, static root =>
            {
                using var bytes = new MemoryStream();
                bytes.Write(Magic);
                using var w = new BinaryWriter(bytes, Encoding.UTF8);
                WriteElement(w, root);
                return bytes.ToArray();
            });
        }

        var node = Parse(value);
        using var stream = new MemoryStream();
        stream.Write(Magic);
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        Write(writer, node);
        return stream.ToArray();
    }

    /// <summary>将只读 JSON 元素写入 PureDb JSONB 输出流。</summary>
    /// <param name = "w">接收 PureDb JSONB 编码的输出写入器。</param>
    /// <param name = "element">只读 JSON 文档的根元素。</param>
    private static void WriteElement(BinaryWriter w, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                w.Write((byte)0);
                break;
            case JsonValueKind.False:
                w.Write((byte)1);
                break;
            case JsonValueKind.True:
                w.Write((byte)2);
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out long n))
                {
                    w.Write((byte)3);
                    w.Write(n);
                }
                else
                {
                    double d = element.GetDouble();
                    if (!double.IsFinite(d))
                        throw new SqlException("JSONB numeric value out of range");
                    w.Write((byte)4);
                    w.Write(d);
                }

                break;
            case JsonValueKind.String:
                w.Write((byte)5);
                w.Write(element.GetString()!);
                break;
            case JsonValueKind.Array:
                w.Write((byte)6);
                w.Write(element.GetArrayLength());
                foreach (var item in element.EnumerateArray())
                    WriteElement(w, item);
                break;
            case JsonValueKind.Object:
                // JsonNode 支撑的 JSONB 一直拒绝重复键，继续保留该校验。
                var members = element.EnumerateObject().ToArray();
                var names = new HashSet<string>(StringComparer.Ordinal);
                w.Write((byte)7);
                w.Write(members.Length);
                foreach (var member in members)
                {
                    if (!names.Add(member.Name))
                        throw new SqlException("Duplicate JSON object key");
                    w.Write(member.Name);
                    WriteElement(w, member.Value);
                }

                break;
            default:
                throw new SqlException("Invalid JSON value");
        }
    }

    /// <summary>从缓存解析表示或二进制表示按路径提取标量，避免重复解析文档。</summary>
    /// <param name = "value">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <param name = "path">从输入 JSON 中提取值的 JSON 路径。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public static object? ExtractCached(object? value, string path)
    {
        if (value is null)
            return null;
        if (value is CachedBlob cached)
            value = cached.Copy();
        if (value is byte[] binary)
            return ExtractBinary(binary, Path(path));
        string? text = value is string s ? s : value is SqlJson j ? j.Text : null;
        if (text is null)
            return Eval("JSON_EXTRACT", [value, path]);
        return JsonReadCache.Extract(text, path, static (root, key) =>
        {
            var element = FindElement(root, Path(key));
            return element.ValueKind switch
            {
                JsonValueKind.Undefined or JsonValueKind.Null => null,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.True => 1L,
                JsonValueKind.False => 0L,
                JsonValueKind.Number => element.TryGetInt64(out long n) ? n : (object)element.GetDouble(),
                _ => new SqlJson(JsonSerializer.Serialize(element))
            };
        });
    }

    /// <summary>沿已解析路径直接定位 PureDb JSONB 中的值。</summary>
    /// <param name = "bytes">当前调用持有的完整页、记录或模式编码字节。</param>
    /// <param name = "parts">已经解析的 JSON 路径分量。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private static object? ExtractBinary(byte[] bytes, List<Part> parts)
    {
        if (!bytes.AsSpan().StartsWith(Magic))
            throw new SqlException("Not PureDb JSONB");
        using var stream = new MemoryStream(bytes, false);
        stream.Position = Magic.Length;
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        object? result = null;
        bool composite = false;
        void Read(int depth, int at, bool match)
        {
            if (depth > 128)
                throw new SqlException("JSONB nesting exceeds 128");
            byte tag = reader.ReadByte();
            bool target = match && at == parts.Count;
            if (target && tag is 6 or 7)
                composite = true;
            object? scalar = tag switch
            {
                0 => null,
                1 => 0L,
                2 => 1L,
                3 => reader.ReadInt64(),
                4 => reader.ReadDouble(),
                5 => reader.ReadString(),
                6 or 7 => null,
                _ => throw new SqlException("Invalid JSONB tag")
            };
            if (scalar is double d && !double.IsFinite(d))
                throw new SqlException("Invalid JSONB number");
            if (target && tag < 6)
                result = scalar;
            if (tag < 6)
                return;
            int count = reader.ReadInt32();
            if (count < 0 || count > bytes.Length - stream.Position)
                throw new SqlException("Invalid JSONB count");
            Part part = match && at < parts.Count ? parts[at] : default;
            for (int i = 0; i < count; i++)
            {
                string? key = tag == 7 ? reader.ReadString() : null;
                bool child = match && at < parts.Count && (tag == 7 ? part.Name == key : part.Name is null && !part.Append && i == (part.FromEnd ? count - part.Index : part.Index));
                Read(depth + 1, at + 1, child);
            }
        }

        try
        {
            Read(0, 0, true);
            if (stream.Position != bytes.Length)
                throw new SqlException("Trailing JSONB bytes");
        }
        catch (EndOfStreamException)
        {
            throw new SqlException("Truncated JSONB");
        }

        return composite ? Scalar(Find(ReadBinary(bytes), parts).Node) : result;
    }

    /// <summary>将 JSON 文本、内部子类型或 PureDb JSONB 转换为 JSON 节点树。</summary>
    /// <param name = "value">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private static JsonNode? Parse(object? value)
    {
        try
        {
            return value switch
            {
                SqlJson j => SqlJson5.Node(j.Text),
                string s => SqlJson5.Node(s),
                byte[] b => ReadBinary(b),
                long or double => JsonSerializer.SerializeToNode(value),
                null => null,
                _ => throw new SqlException("Expected JSON text or PureDb JSONB")
            };
        }
        catch (JsonException e)
        {
            throw new SqlException("Malformed JSON: " + e.Message);
        }
        catch (EndOfStreamException)
        {
            throw new SqlException("Truncated JSONB");
        }
    }

    /// <summary>将可变 JSON 节点递归编码到 PureDb JSONB 输出流。</summary>
    /// <param name = "w">接收 PureDb JSONB 编码的输出写入器。</param>
    /// <param name = "node">JSON 根节点或节点值；null 表示 JSON null。</param>
    private static void Write(BinaryWriter w, JsonNode? node)
    {
        if (node is null)
        {
            w.Write((byte)0);
            return;
        }

        if (node is JsonArray array)
        {
            w.Write((byte)6);
            w.Write(array.Count);
            foreach (var child in array)
                Write(w, child);
            return;
        }

        if (node is JsonObject obj)
        {
            w.Write((byte)7);
            w.Write(obj.Count);
            foreach (var (key, child) in obj)
            {
                w.Write(key);
                Write(w, child);
            }

            return;
        }

        var element = JsonSerializer.SerializeToElement(node);
        switch (element.ValueKind)
        {
            case JsonValueKind.False:
                w.Write((byte)1);
                break;
            case JsonValueKind.True:
                w.Write((byte)2);
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out long n))
                {
                    w.Write((byte)3);
                    w.Write(n);
                }
                else
                {
                    double d = element.GetDouble();
                    if (!double.IsFinite(d))
                        throw new SqlException("JSONB numeric value out of range");
                    w.Write((byte)4);
                    w.Write(d);
                }

                break;
            case JsonValueKind.String:
                w.Write((byte)5);
                w.Write(element.GetString()!);
                break;
            default:
                throw new SqlException("Invalid JSON scalar");
        }
    }

    /// <summary>验证并解码 PureDb JSONB，包括长度边界和尾部完整性。</summary>
    /// <param name = "bytes">当前调用持有的完整页、记录或模式编码字节。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private static JsonNode? ReadBinary(byte[] bytes)
    {
        if (!bytes.AsSpan().StartsWith(Magic))
            throw new SqlException("Not PureDb JSONB (SQLite JSONB binary layout is different)");
        using var stream = new MemoryStream(bytes, false);
        stream.Position = Magic.Length;
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        JsonNode? Read(int depth)
        {
            if (depth > 128)
                throw new SqlException("JSONB nesting exceeds 128");
            byte tag = reader.ReadByte();
            if (tag == 0)
                return null;
            if (tag == 1)
                return JsonValue.Create(false);
            if (tag == 2)
                return JsonValue.Create(true);
            if (tag == 3)
                return JsonValue.Create(reader.ReadInt64());
            if (tag == 4)
            {
                double value = reader.ReadDouble();
                if (!double.IsFinite(value))
                    throw new SqlException("Invalid JSONB number");
                return JsonValue.Create(value);
            }

            if (tag == 5)
                return JsonValue.Create(reader.ReadString());
            int count = reader.ReadInt32();
            if (count < 0 || count > stream.Length - stream.Position)
                throw new SqlException("Invalid JSONB count");
            if (tag == 6)
            {
                var array = new JsonArray();
                for (int i = 0; i < count; i++)
                    array.Add(Read(depth + 1));
                return array;
            }

            if (tag == 7)
            {
                var obj = new JsonObject();
                for (int i = 0; i < count; i++)
                    obj.Add(reader.ReadString(), Read(depth + 1));
                return obj;
            }

            throw new SqlException("Invalid JSONB tag");
        }

        var result = Read(0);
        if (stream.Position != stream.Length)
            throw new SqlException("Trailing JSONB bytes");
        return result;
    }

    private static readonly ConditionalWeakTable<string, List<Part>> Paths = new();
    /// <summary>JSON 路径中的对象名、数组下标、倒数下标或追加位置。</summary>
    /// <param name = "Name">模式对象名或对象路径分量名。</param>
    /// <param name = "Index">目标索引或数组下标。</param>
    /// <param name = "FromEnd">数组下标是否从末尾倒数。</param>
    /// <param name = "Append">是否表示数组追加位置。</param>
    private readonly record struct Part(string? Name, int Index = 0, bool FromEnd = false, bool Append = false);
    /// <summary>取得或解析 JSON 路径，支持按操作符要求的简写形式。</summary>
    /// <param name = "value">JSON 路径文本，或箭头操作符允许的字段名、数组下标简写。</param>
    /// <param name = "shorthand">是否允许箭头操作符的字段名或数组下标简写。</param>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    private static List<Part> Path(object? value, bool shorthand = false)
    {
        if (value is string text && !shorthand)
            return Paths.GetValue(text, static s => ParsePath(s));
        return ParsePath(value, shorthand);
    }

    /// <summary>将 JSON 路径解析为对象字段、数组下标、倒数下标或追加位置。</summary>
    /// <param name = "value">JSON 路径文本，或箭头操作符允许的字段名、数组下标简写。</param>
    /// <param name = "shorthand">是否允许箭头操作符的字段名或数组下标简写。</param>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    private static List<Part> ParsePath(object? value, bool shorthand = false)
    {
        if (shorthand && value is long shorthandIndex)
            return [new(null, checked((int)Math.Abs(shorthandIndex)), shorthandIndex < 0)];
        if (value is not string text)
            throw new SqlException("JSON path must be TEXT");
        if (shorthand && !text.StartsWith('$'))
            return [new(text)];
        if (text.Length == 0 || text[0] != '$')
            throw new SqlException("JSON path must begin with $");
        var result = new List<Part>();
        for (int p = 1; p < text.Length;)
        {
            if (text[p] == '.')
            {
                int start = ++p;
                if (p < text.Length && text[p] == '"')
                {
                    p++;
                    bool escape = false;
                    while (p < text.Length)
                    {
                        if (!escape && text[p] == '"')
                            break;
                        if (!escape && text[p] == '\\')
                            escape = true;
                        else
                            escape = false;
                        p++;
                    }

                    if (p == text.Length)
                        throw new SqlException("Unclosed JSON path key");
                    string name;
                    try
                    {
                        name = JsonSerializer.Deserialize<string>(text[start..++p])!;
                    }
                    catch (JsonException)
                    {
                        throw new SqlException("Invalid quoted JSON key");
                    }

                    result.Add(new(name));
                }
                else
                {
                    while (p < text.Length && text[p] is not ('.' or '['))
                        p++;
                    if (p == start)
                        throw new SqlException("Empty JSON path key");
                    result.Add(new(text[start..p]));
                }
            }
            else if (text[p] == '[')
            {
                int start = ++p;
                while (p < text.Length && text[p] != ']')
                    p++;
                if (p == text.Length)
                    throw new SqlException("Unclosed JSON subscript");
                string sub = text[start..p++];
                if (sub == "#")
                    result.Add(new(null, Append: true));
                else if (sub.StartsWith("#-", StringComparison.Ordinal) && int.TryParse(sub[2..], out int back) && back > 0)
                    result.Add(new(null, back, true));
                else if (int.TryParse(sub, out int index) && index >= 0)
                    result.Add(new(null, index));
                else
                    throw new SqlException("Invalid JSON array subscript");
            }
            else
                throw new SqlException("Invalid JSON path");
        }

        return result;
    }

    /// <summary>沿路径定位节点，同时区分缺失值与 JSON null。</summary>
    /// <param name = "root">JSON 根节点或节点值；null 表示 JSON null。</param>
    /// <param name = "parts">已经解析的 JSON 路径分量。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private static (bool Exists, JsonNode? Node) Find(JsonNode? root, IEnumerable<Part> parts)
    {
        var node = root;
        foreach (var part in parts)
        {
            if (part.Name is { } name)
            {
                if (node is not JsonObject obj || !obj.TryGetPropertyValue(name, out node))
                    return (false, null);
            }
            else
            {
                if (node is not JsonArray array || part.Append)
                    return (false, null);
                int at = part.FromEnd ? array.Count - part.Index : part.Index;
                if (at < 0 || at >= array.Count)
                    return (false, null);
                node = array[at];
            }
        }

        return (true, node);
    }

    /// <summary>按 SQL 值和 JSON 子类型规则构造可写 JSON 节点。</summary>
    /// <param name = "value">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private static JsonNode? FromSql(object? value) => value switch
    {
        SqlJson or byte[] => Parse(value)?.DeepClone(),
        _ => JsonSerializer.SerializeToNode(value)
    };
    /// <summary>将 JSON 节点转换为 SQL 标量；对象和数组保留文档子类型。</summary>
    /// <param name = "node">JSON 根节点或节点值；null 表示 JSON null。</param>
    /// <param name = "binary">是否输出 PureDb JSONB 而非 JSON 文本。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private static object? Scalar(JsonNode? node, bool binary = false)
    {
        if (node is null)
            return null;
        if (node is JsonArray or JsonObject)
            return Output(node, binary);
        var element = JsonSerializer.SerializeToElement(node);
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => 1L,
            JsonValueKind.False => 0L,
            JsonValueKind.Number => element.TryGetInt64(out long n) ? n : (object)element.GetDouble(),
            _ => null
        };
    }

    /// <summary>按调用要求输出 JSON 文本子类型或 PureDb JSONB。</summary>
    /// <param name = "node">JSON 根节点或节点值；null 表示 JSON null。</param>
    /// <param name = "binary">是否输出 PureDb JSONB 而非 JSON 文本。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private static object Output(JsonNode? node, bool binary) => binary ? Binary(new SqlJson(node?.ToJsonString() ?? "null")) : new SqlJson(node?.ToJsonString() ?? "null");
    /// <summary>取得 JSON 节点对应的 SQL JSON 类型名称。</summary>
    /// <param name = "node">JSON 根节点或节点值；null 表示 JSON null。</param>
    /// <returns>按当前方法语义生成、解析或规范化后的文本。</returns>
    private static string Type(JsonNode? node)
    {
        if (node is null)
            return "null";
        if (node is JsonObject)
            return "object";
        if (node is JsonArray)
            return "array";
        var e = JsonSerializer.SerializeToElement(node);
        return e.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => e.TryGetInt64(out _) ? "integer" : "real",
            JsonValueKind.String => "text",
            _ => "null"
        };
    }

    /// <summary>按 insert、replace 或 set 规则修改路径并保留相应缺失路径语义。</summary>
    /// <param name = "root">JSON 根节点或节点值；null 表示 JSON null。</param>
    /// <param name = "path">需要修改的已解析 JSON 路径分量。</param>
    /// <param name = "value">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <param name = "mode">JSON 修改模式：insert、replace 或 set。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private static JsonNode? Edit(JsonNode? root, List<Part> path, object? value, string mode)
    {
        if (path.Count == 0)
            return mode == "JSON_REMOVE" ? null : mode == "JSON_INSERT" || mode == "JSON_ARRAY_INSERT" ? root : FromSql(value);
        JsonNode? node = root;
        for (int i = 0; i < path.Count; i++)
        {
            var part = path[i];
            bool last = i == path.Count - 1;
            JsonNode? next = null;
            bool create = mode is "JSON_SET" or "JSON_INSERT" or "JSON_ARRAY_INSERT";
            JsonNode NewContainer() => path[i + 1].Name is not null ? new JsonObject() : new JsonArray();
            if (part.Name is { } name)
            {
                if (node is not JsonObject obj)
                    return root;
                bool exists = obj.TryGetPropertyValue(name, out next);
                if (last)
                {
                    if (mode == "JSON_REMOVE")
                        obj.Remove(name);
                    else if (mode != "JSON_ARRAY_INSERT" && (mode == "JSON_SET" || mode == "JSON_INSERT" && !exists || mode == "JSON_REPLACE" && exists))
                        obj[name] = FromSql(value);
                }
                else if (!exists && create)
                    obj[name] = next = NewContainer();
            }
            else
            {
                if (node is not JsonArray array)
                    return root;
                int at = part.Append ? array.Count : part.FromEnd ? array.Count - part.Index : part.Index;
                bool exists = at >= 0 && at < array.Count;
                if (last)
                {
                    if (mode == "JSON_REMOVE" && exists)
                        array.RemoveAt(at);
                    else if (mode == "JSON_ARRAY_INSERT" && at >= 0 && at <= array.Count)
                        array.Insert(at, FromSql(value));
                    else if ((mode == "JSON_SET" || mode == "JSON_REPLACE") && exists)
                        array[at] = FromSql(value);
                    else if ((mode == "JSON_SET" || mode == "JSON_INSERT") && at == array.Count)
                        array.Add(FromSql(value));
                }
                else if (exists)
                    next = array[at];
                else if (create && at == array.Count)
                {
                    next = NewContainer();
                    array.Add(next);
                }
            }

            if (last)
                return root;
            node = next;
            if (node is null)
                return root;
        }

        return root;
    }

    /// <summary>按 JSON Merge Patch 规则递归合并对象并处理 null 删除。</summary>
    /// <param name = "target">JSON 根节点或节点值；null 表示 JSON null。</param>
    /// <param name = "patch">JSON 根节点或节点值；null 表示 JSON null。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private static JsonNode? Patch(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject p)
            return patch?.DeepClone();
        var obj = target as JsonObject ?? new JsonObject();
        foreach (var (key, value) in p)
        {
            if (value is null)
                obj.Remove(key);
            else
            {
                obj.TryGetPropertyValue(key, out var old);
                obj[key] = Patch(old?.DeepClone(), value);
            }
        }

        return obj;
    }

    /// <summary>在只读 JSON 元素上定位路径，避免构造可变节点树。</summary>
    /// <param name = "root">只读 JSON 文档的根元素。</param>
    /// <param name = "parts">已经解析的 JSON 路径分量。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private static JsonElement FindElement(JsonElement root, List<Part> parts)
    {
        foreach (var part in parts)
        {
            if (part.Name is { } name)
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out root))
                    return default;
            }
            else
            {
                if (root.ValueKind != JsonValueKind.Array || part.Append)
                    return default;
                int count = root.GetArrayLength(), at = part.FromEnd ? count - part.Index : part.Index;
                if (at < 0 || at >= count)
                    return default;
                root = root[at];
            }
        }

        return root;
    }

    /// <summary>对只读 JSON 文档执行可直接完成的读取函数。</summary>
    /// <param name = "op">当前 SQL 操作符或函数名。</param>
    /// <param name = "text">待匹配的词法文本或 JSON 文本。</param>
    /// <param name = "args">按对应模式列序或函数参数顺序排列的 SQL 标量值。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private static object? ReadElement(string op, string text, object?[] args)
    {
        if (args.Length > 1 && args[1] is null)
            return null;
        var element = JsonReadCache.Root(text);
        if (args.Length > 1)
            element = FindElement(element, Path(args[1], op is "->" or "->>"));
        if (element.ValueKind == JsonValueKind.Undefined)
            return null;
        if (op == "JSON_TYPE")
            return element.ValueKind switch
            {
                JsonValueKind.Null => "null",
                JsonValueKind.Object => "object",
                JsonValueKind.Array => "array",
                JsonValueKind.String => "text",
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => element.TryGetInt64(out _) ? "integer" : "real"
            };
        if (op == "JSON_ARRAY_LENGTH")
            return element.ValueKind == JsonValueKind.Array ? (long)element.GetArrayLength() : 0L;
        if (op == "->")
            return new SqlJson(JsonSerializer.Serialize(element));
        object? result = element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => 1L,
            JsonValueKind.False => 0L,
            JsonValueKind.Number => element.TryGetInt64(out long n) ? n : (object)element.GetDouble(),
            _ => new SqlJson(JsonSerializer.Serialize(element))
        };
        return op == "->>" ? Unwrap(result) : result;
    }

    /// <summary>分派 JSON 构造、读取、修改、校验及二进制转换函数。</summary>
    /// <param name = "op">当前 SQL 操作符或函数名。</param>
    /// <param name = "args">按对应模式列序或函数参数顺序排列的 SQL 标量值。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public static object? Eval(string op, object?[] args)
    {
        for (int i = 0; i < args.Length; i++)
            if (args[i] is CachedBlob cached)
                args[i] = cached.Copy();
        bool binary = op.StartsWith("JSONB", StringComparison.Ordinal);
        string name = op.Replace("JSONB", "JSON", StringComparison.Ordinal);
        if (name == "JSON_VALID")
        {
            long flags = args.Length == 1 ? 1 : args[1] is long f && f >= 1 && f <= 15 ? f : throw new SqlException("JSON_VALID flags must be in [1,15]");
            if (args[0] is null)
                return null;
            try
            {
                if (args[0] is byte[] blob)
                {
                    if ((flags & 4) != 0 && blob.AsSpan().StartsWith(Magic))
                        return 1L;
                    if ((flags & 8) == 0)
                        return 0L;
                }
                else if ((flags & 3) == 0)
                    return 0L;
                else if ((flags & 2) == 0)
                {
                    using var strict = JsonDocument.Parse(args[0] is SqlJson json ? json.Text : args[0] is string text ? text : JsonSerializer.Serialize(args[0]), DocumentOptions);
                    return 1L;
                }

                Parse(args[0]);
                return 1L;
            }
            catch (Exception e) when (e is SqlException or JsonException or ArgumentException)
            {
                return 0L;
            }
        }

        if (name == "JSON_QUOTE")
        {
            if (args[0] is byte[])
                throw new SqlException("JSON_QUOTE does not accept BLOB values");
            return args[0] is SqlJson j ? j : new SqlJson(JsonSerializer.Serialize(args[0]));
        }

        if (name == "JSON_ARRAY")
        {
            var array = new JsonArray();
            foreach (var v in args)
                array.Add(FromSql(v));
            return Output(array, binary);
        }

        if (name == "JSON_OBJECT")
        {
            var obj = new JsonObject();
            for (int i = 0; i < args.Length; i += 2)
            {
                if (args[i] is not string key)
                    throw new SqlException("JSON_OBJECT keys must be TEXT");
                obj[key] = FromSql(args[i + 1]);
            }

            return Output(obj, binary);
        }

        if (args[0] is null)
            return null;
        if (name == "JSON" && binary)
            return Binary(args[0]!);
        string? readText = args[0] is string t ? t : args[0] is SqlJson jsonText ? jsonText.Text : null;
        if (!binary && readText is not null && (name is "JSON_TYPE" or "JSON_ARRAY_LENGTH" or "->" or "->>" || name == "JSON_EXTRACT" && args.Length == 2))
            return ReadElement(name, readText, args);
        var root = Parse(args[0]);
        if (name == "JSON")
            return Output(root, binary);
        if (name is "->" or "->>")
        {
            if (args[1] is null)
                return null;
            var found = Find(root, Path(args[1], true));
            if (!found.Exists)
                return null;
            return name == "->" ? Output(found.Node, false) : Unwrap(Scalar(found.Node));
        }

        if (name == "JSON_EXTRACT")
        {
            if (args.Skip(1).Any(a => a is null))
                return null;
            if (args.Length == 2)
                return Scalar(Find(root, Path(args[1])).Node, binary);
            var array = new JsonArray();
            foreach (var path in args.Skip(1))
                array.Add(Find(root, Path(path)).Node?.DeepClone());
            return Output(array, binary);
        }

        if (name is "JSON_TYPE" or "JSON_ARRAY_LENGTH")
        {
            if (args.Length > 1 && args[1] is null)
                return null;
            var found = args.Length == 1 ? (Exists: true, Node: root) : Find(root, Path(args[1]));
            if (!found.Exists)
                return null;
            return name == "JSON_TYPE" ? Type(found.Node) : found.Node is JsonArray array ? (long)array.Count : 0L;
        }

        if (name == "JSON_PATCH")
            return args[1] is null ? null : Output(Patch(root, Parse(args[1])), binary);
        if (name == "JSON_REMOVE")
        {
            foreach (var path in args.Skip(1))
            {
                if (path is null)
                    return null;
                var parts = Path(path);
                if (parts.Count == 0)
                    return null;
                root = Edit(root, parts, null, name);
            }

            return Output(root, binary);
        }

        for (int i = 1; i < args.Length; i += 2)
        {
            if (args[i] is null)
                continue;
            root = Edit(root, Path(args[i]), args[i + 1], name);
        }

        return Output(root, binary);
    }
}
