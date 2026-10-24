using System.Text.Json;
using System.Text.Json.Nodes;

namespace PureDb;
/// <remarks>本部分实现 JSON_EACH 与 JSON_TREE 的可见列、路径和父子关系。</remarks>
internal static partial class SqlJsonFunctions
{
    /// <summary>根据 JSON 文档和可选根路径生成表值函数行，ID 只保证本次遍历唯一。</summary>
    /// <param name = "value">JSON 文本或 PureDb JSONB 值；SQL NULL 产生空表。</param>
    /// <param name = "rootPath">起始 JSON 路径。</param>
    /// <param name = "tree">为 true 时递归遍历，否则只展开直接子项。</param>
    /// <returns>包含八个公开列和 json、root 两个隐藏列的行数组。</returns>
    internal static object?[][] TableRows(object? value, object? rootPath, bool tree)
    {
        if (value is null || rootPath is null)
            return [];
        if (rootPath is not string path)
            throw new SqlException("JSON table root must be TEXT");
        var parts = Path(path);
        var document = Parse(value);
        var root = Find(document, parts);
        if (!root.Exists)
            return [];
        var rows = new List<object?[]>();
        long nextId = 0;
        string original = value is SqlJson json ? json.Text : value is string text ? text : Canonical(value);
        string Property(string prefix, string name) => prefix + "." + (name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_') && name.All(c => char.IsLetterOrDigit(c) || c == '_') ? name : JsonSerializer.Serialize(name));
        void Visit(JsonNode? node, object? key, long? parent, string fullkey, string parentPath, bool descend)
        {
            string type;
            object? scalar;
            if (node is JsonArray)
            {
                type = "array";
                scalar = node.ToJsonString();
            }
            else if (node is JsonObject)
            {
                type = "object";
                scalar = node.ToJsonString();
            }
            else if (node is null)
            {
                type = "null";
                scalar = null;
            }
            else
            {
                using var parsed = JsonDocument.Parse(node.ToJsonString());
                var element = parsed.RootElement;
                type = element.ValueKind switch
                {
                    JsonValueKind.String => "text",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => element.TryGetInt64(out _) ? "integer" : "real"
                };
                scalar = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.True => 1L,
                    JsonValueKind.False => 0L,
                    _ => element.TryGetInt64(out long integer) ? integer : (object)element.GetDouble()
                };
            }

            long id = nextId++;
            rows.Add([key, scalar, type, node is JsonArray or JsonObject ? null : scalar, id, parent, fullkey, parentPath, original, path]);
            if (!descend)
                return;
            if (node is JsonObject obj)
                foreach (var pair in obj)
                    Visit(pair.Value, pair.Key, id, Property(fullkey, pair.Key), fullkey, true);
            else if (node is JsonArray array)
                for (int i = 0; i < array.Count; i++)
                    Visit(array[i], (long)i, id, fullkey + "[" + i + "]", fullkey, true);
        }

        if (tree)
        {
            var (key, parentPath) = JsonRootMetadata(path);
            Visit(root.Node, key, null, path, parentPath, true);
        }
        else if (root.Node is JsonObject obj)
            foreach (var pair in obj)
                Visit(pair.Value, pair.Key, null, Property(path, pair.Key), path, false);
        else if (root.Node is JsonArray array)
            for (int i = 0; i < array.Count; i++)
                Visit(array[i], (long)i, null, path + "[" + i + "]", path, false);
        else
            Visit(root.Node, null, null, path, path, false);
        return rows.ToArray();
    }

    /// <summary>按 SQLite 根路径的词法规则生成根行 key 和 path，避免误拆引号内的点号和方括号。</summary>
    /// <param name = "path">已经验证且实际存在的根路径。</param>
    /// <returns>根行的键及其父路径；倒数下标的根键遵循 SQLite 的词法值零。</returns>
    private static (object? Key, string Parent) JsonRootMetadata(string path)
    {
        if (path == "$")
            return (null, path);
        bool quoted = false, escaped = false;
        int dot = -1, bracket = -1;
        for (int i = 1; i < path.Length; i++)
        {
            char c = path[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (quoted && c == '\\')
            {
                escaped = true;
                continue;
            }

            if (c == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (quoted)
                continue;
            if (c == '.')
                dot = i;
            else if (c == '[')
                bracket = i;
        }

        if (dot >= 0)
        {
            string key = path[(dot + 1)..];
            if (key.StartsWith('"'))
                key = key.EndsWith('"') ? JsonSerializer.Deserialize<string>(key)! : key[1..^1];
            return (key, path[..dot]);
        }

        string index = path[(bracket + 1)..^1];
        return (long.TryParse(index, out long value) ? value : 0L, path[..bracket]);
    }
}

/// <remarks>本部分将 JSON 表值函数接入普通查询和逐行相关连接。</remarks>
internal sealed partial class SqlSession
{
    /// <summary>创建 JSON 表值函数的固定模式，不把隐藏列放进星号结果。</summary>
    /// <param name = "source">表值函数数据源。</param>
    /// <returns>具有固定十列的临时表。</returns>
    private SqlTable JsonTableSchema(Source source)
    {
        string name = source.Table.ToUpperInvariant();
        if (name is not ("JSON_EACH" or "JSON_TREE") || source.Arguments is not { Length: 1 or 2 })
            throw new SqlException("Unsupported table-valued function or arity: " + source.Table);
        var table = new SqlTable
        {
            Id = --_virtualId,
            Name = source.Alias,
            Columns = [new("key", "ANY"), new("value", "ANY"), new("type", "TEXT"), new("atom", "ANY"), new("id", "INTEGER"), new("parent", "INTEGER"), new("fullkey", "TEXT"), new("path", "TEXT"), new("json", "ANY", Hidden: true), new("root", "TEXT", Hidden: true)]
        };
        return table;
    }

    /// <summary>对左侧每一行调用 JSON 表值函数，并处理 CROSS、INNER、LEFT 连接。</summary>
    /// <param name = "left">左侧已过滤输入。</param>
    /// <param name = "arguments">基于左侧列绑定的参数。</param>
    /// <param name = "tree">是否递归遍历 JSON。</param>
    /// <param name = "kind">连接类型。</param>
    /// <param name = "condition">对拼接行执行的 ON 条件。</param>
    /// <returns>完整拼接行；LEFT 无匹配时补齐右侧 NULL。</returns>
    private static IEnumerable<object?[]> JsonJoinRows(IEnumerable<object?[]> left, Func<object?[], object?>[] arguments, bool tree, string kind, Func<object?[], object?>? condition)
    {
        if (kind is "RIGHT" or "FULL")
            throw new SqlException("Correlated JSON table functions support INNER/CROSS/LEFT joins");
        foreach (var row in left)
        {
            bool matched = false;
            foreach (var item in SqlJsonFunctions.TableRows(arguments[0](row), arguments.Length == 2 ? arguments[1](row) : "$", tree))
            {
                var joined = Combine(row, item);
                if (condition is not null && SqlValueComparer.Truth(condition(joined)) != true)
                    continue;
                matched = true;
                yield return joined;
            }

            if (!matched && kind == "LEFT")
                yield return Combine(row, new object?[10]);
        }
    }
}
