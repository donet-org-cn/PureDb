using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PureDb;
/// <summary>把 JSON5 文本转换为严格 JSON，保留整数文本并限制递归深度。</summary>
/// <param name = "text">待解析的原始 JSON5 文本。</param>
internal sealed class SqlJson5(string text)
{
    private int _position;
    private static readonly Regex Number = new(@"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    /// <summary>优先使用标准解析器，只有 JSON5 扩展文本才执行转换。</summary>
    /// <param name = "text">标准 JSON 或 JSON5 文本。</param>
    /// <returns>调用方必须释放的文档。</returns>
    internal static JsonDocument Document(string text)
    {
        try
        {
            return JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 128 });
        }
        catch (JsonException)
        {
            return JsonDocument.Parse(Normalize(text), new JsonDocumentOptions { MaxDepth = 128 });
        }
    }

    /// <summary>读取可写节点，严格 JSON 保留原有快速解析路径。</summary>
    /// <param name = "text">标准 JSON 或 JSON5 文本。</param>
    /// <returns>解析出的节点；JSON null 返回 null。</returns>
    internal static JsonNode? Node(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: new()
            {
                MaxDepth = 128
            });
        }
        catch (JsonException)
        {
            return JsonNode.Parse(Normalize(text), documentOptions: new()
            {
                MaxDepth = 128
            });
        }
    }

    /// <summary>转换完整 JSON5 文档，拒绝有效值后的额外内容。</summary>
    /// <param name = "text">原始文本。</param>
    /// <returns>可以交给标准 JSON 解析器的规范语法文本。</returns>
    internal static string Normalize(string text)
    {
        var parser = new SqlJson5(text);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            parser.Value(writer, 0);
            parser.Space();
            if (parser._position != text.Length)
                throw new SqlException("Trailing JSON5 content");
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>跳过空白、行注释和块注释，未闭合块注释报错。</summary>
    private void Space()
    {
        while (_position < text.Length)
        {
            if (char.IsWhiteSpace(text[_position]) || text[_position] == '\uFEFF')
            {
                _position++;
                continue;
            }

            if (text[_position] != '/' || _position + 1 == text.Length)
                return;
            if (text[_position + 1] == '/')
            {
                _position += 2;
                while (_position < text.Length && text[_position] is not ('\r' or '\n' or '\u2028' or '\u2029'))
                    _position++;
            }
            else if (text[_position + 1] == '*')
            {
                int end = text.IndexOf("*/", _position + 2, StringComparison.Ordinal);
                if (end < 0)
                    throw new SqlException("Unclosed JSON5 comment");
                _position = end + 2;
            }
            else
                return;
        }
    }

    /// <summary>读取一个 JSON5 值并直接写成严格 JSON。</summary>
    /// <param name = "writer">目标 UTF-8 JSON 写入器。</param>
    /// <param name = "depth">当前嵌套深度。</param>
    private void Value(Utf8JsonWriter writer, int depth)
    {
        Space();
        if (depth > 128 || _position >= text.Length)
            throw new SqlException("Invalid JSON5 value or nesting depth");
        char c = text[_position];
        if (c is '\'' or '"')
        {
            writer.WriteStringValue(String());
            return;
        }

        if (c is '{' or '[')
        {
            bool obj = c == '{';
            _position++;
            if (obj)
                writer.WriteStartObject();
            else
                writer.WriteStartArray();
            Space();
            char end = obj ? '}' : ']';
            while (_position < text.Length && text[_position] != end)
            {
                if (obj)
                {
                    writer.WritePropertyName(text[_position] is '\'' or '"' ? String() : Identifier());
                    Space();
                    if (_position >= text.Length || text[_position++] != ':')
                        throw new SqlException("JSON5 object requires ':'");
                }

                Value(writer, depth + 1);
                Space();
                if (_position < text.Length && text[_position] == end)
                    break;
                if (_position >= text.Length || text[_position++] != ',')
                    throw new SqlException("JSON5 values require a comma");
                Space();
            }

            if (_position >= text.Length || text[_position++] != end)
                throw new SqlException("Unclosed JSON5 container");
            if (obj)
                writer.WriteEndObject();
            else
                writer.WriteEndArray();
            return;
        }

        int start = _position;
        while (_position < text.Length && !char.IsWhiteSpace(text[_position]) && !",]}:/".Contains(text[_position]))
            _position++;
        string token = text[start.._position];
        if (token is "true" or "false" or "null")
        {
            writer.WriteRawValue(token);
            return;
        }

        string number = token.StartsWith('+') ? token[1..] : token;
        string unsigned = number.StartsWith('-') ? number[1..] : number;
        if (unsigned.StartsWith('+') || unsigned.StartsWith('-') || token.StartsWith("+-", StringComparison.Ordinal))
            throw new SqlException("Invalid JSON5 number sign");
        if (unsigned.Equals("Infinity", StringComparison.OrdinalIgnoreCase) || unsigned.Equals("Inf", StringComparison.OrdinalIgnoreCase))
        {
            writer.WriteRawValue(number.StartsWith('-') ? "-9e999" : "9e999");
            return;
        }

        if (unsigned.Equals("NaN", StringComparison.OrdinalIgnoreCase) || unsigned.Equals("QNaN", StringComparison.OrdinalIgnoreCase) || unsigned.Equals("SNaN", StringComparison.OrdinalIgnoreCase))
        {
            writer.WriteNullValue();
            return;
        }

        if (unsigned.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (unsigned.Length <= 2 || !BigInteger.TryParse("0" + unsigned[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var integer))
                throw new SqlException("Invalid JSON5 hexadecimal number");
            writer.WriteRawValue((number.StartsWith('-') ? -integer : integer).ToString(CultureInfo.InvariantCulture));
            return;
        }

        if (number.StartsWith("-.", StringComparison.Ordinal))
            number = "-0" + number[1..];
        else if (number.StartsWith('.'))
            number = "0" + number;
        int exponent = number.IndexOfAny(['e', 'E']);
        if (number.EndsWith('.'))
            number += "0";
        else if (exponent > 0 && number[exponent - 1] == '.')
            number = number.Insert(exponent, "0");
        if (!Number.IsMatch(number))
            throw new SqlException("Invalid JSON5 literal");
        writer.WriteRawValue(number);
    }

    /// <summary>解析未加引号的属性名，包括 Unicode 转义和非 ASCII 字符。</summary>
    /// <returns>已经去转义的属性名称。</returns>
    private string Identifier()
    {
        var result = new StringBuilder();
        while (_position < text.Length)
        {
            char c = text[_position];
            if (c == '\\')
            {
                _position++;
                if (_position >= text.Length || text[_position++] != 'u')
                    throw new SqlException("Invalid JSON5 identifier escape");
                c = Hex(4);
                if (!(c is '_' or '$' || char.IsLetter(c) || c >= 128 && !char.IsWhiteSpace(c) || result.Length > 0 && char.IsDigit(c)))
                    throw new SqlException("Invalid JSON5 identifier escape");
            }
            else if (c is '_' or '$' || char.IsLetter(c) || c >= 128 && !char.IsWhiteSpace(c) || result.Length > 0 && char.IsDigit(c))
                _position++;
            else
                break;
            if (result.Length == 0 && char.IsDigit(c))
                throw new SqlException("Invalid JSON5 property name");
            result.Append(c);
        }

        if (result.Length == 0)
            throw new SqlException("Missing JSON5 property name");
        return result.ToString();
    }

    /// <summary>读取固定长度十六进制转义。</summary>
    /// <param name = "digits">必须读取的十六进制位数。</param>
    /// <returns>解码后的 UTF-16 字符。</returns>
    private char Hex(int digits)
    {
        if (_position + digits > text.Length || !ushort.TryParse(text.AsSpan(_position, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort value))
            throw new SqlException("Invalid JSON5 character escape");
        _position += digits;
        return (char)value;
    }

    /// <summary>解析单双引号字符串、转义及转义换行。</summary>
    /// <returns>去引号和转义后的字符串。</returns>
    private string String()
    {
        char quote = text[_position++];
        var value = new StringBuilder();
        while (_position < text.Length)
        {
            char c = text[_position++];
            if (c == quote)
                return value.ToString();
            if (c is '\r' or '\n')
                throw new SqlException("Unescaped JSON5 newline");
            if (c != '\\')
            {
                value.Append(c);
                continue;
            }

            if (_position >= text.Length)
                break;
            c = text[_position++];
            if (c is '\n' or '\u2028' or '\u2029')
                continue;
            if (c == '\r')
            {
                if (_position < text.Length && text[_position] == '\n')
                    _position++;
                continue;
            }

            if (c == '0' && _position < text.Length && char.IsDigit(text[_position]) || c is >= '1' and <= '9')
                throw new SqlException("Octal JSON5 escape is unsupported");
            value.Append(c switch
            {
                'u' => Hex(4),
                'x' => Hex(2),
                'b' => '\b',
                'f' => '\f',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'v' => '\v',
                '0' => '\0',
                _ => c
            });
        }

        throw new SqlException("Unclosed JSON5 string");
    }
}
