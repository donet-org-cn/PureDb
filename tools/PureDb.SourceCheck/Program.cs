using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// 检查实际源码声明，避免仅检查编译器生成的 XML 而漏掉尚未写文档的成员。
string root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
int declarations = 0;
int errors = 0;
var strictUtf8 = new UTF8Encoding(false, true);

foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
    .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj")))
{
    string source;
    try
    {
        source = strictUtf8.GetString(File.ReadAllBytes(file));
    }
    catch (DecoderFallbackException) { Fail(file, 0, "源文件必须是有效 UTF-8。"); continue; }

    var syntax = CSharpSyntaxTree.ParseText(source);
    foreach (var node in syntax.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>())
    {
        // 全部类型、方法和属性都检查；字段仅检查显式公开或受保护的声明。
        bool field = node is FieldDeclarationSyntax &&
            (node.Modifiers.Any(SyntaxKind.PublicKeyword) || node.Modifiers.Any(SyntaxKind.ProtectedKeyword));
        if (!field && node is not (BaseTypeDeclarationSyntax or BaseMethodDeclarationSyntax or
            PropertyDeclarationSyntax or IndexerDeclarationSyntax or DelegateDeclarationSyntax or EnumMemberDeclarationSyntax))
            continue;

        declarations++;
        int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        var comments = node.GetLeadingTrivia().Where(t => t.HasStructure && t.GetStructure() is DocumentationCommentTriviaSyntax).ToArray();
        if (comments.Length == 0)
        {
            Fail(file, line, "声明缺少中文 XML 注释。");
            continue;
        }

        string xml = string.Join("\n", comments.Select(t => source.Substring(t.FullSpan.Start, t.FullSpan.Length)));
        xml = Regex.Replace(xml, @"(?m)^\s*/// ?", "");
        XElement document;
        try
        {
            document = XElement.Parse("<doc>" + xml + "</doc>");
        }
        catch (System.Xml.XmlException) { Fail(file, line, "XML 注释格式错误。"); continue; }

        CheckText(document.Element("summary"), "summary");
        var parameters = node switch
        {
            BaseMethodDeclarationSyntax m => m.ParameterList.Parameters,
            TypeDeclarationSyntax t => t.ParameterList?.Parameters ?? default,
            DelegateDeclarationSyntax d => d.ParameterList.Parameters,
            IndexerDeclarationSyntax i => i.ParameterList.Parameters,
            _ => default
        };
        var typeParameters = node switch
        {
            TypeDeclarationSyntax t => t.TypeParameterList?.Parameters ?? default,
            MethodDeclarationSyntax m => m.TypeParameterList?.Parameters ?? default,
            DelegateDeclarationSyntax d => d.TypeParameterList?.Parameters ?? default,
            _ => default
        };
        CheckNames("param", parameters.Select(p => p.Identifier.Text).ToArray());
        CheckNames("typeparam", typeParameters.Select(p => p.Identifier.Text).ToArray());
        if (node is MethodDeclarationSyntax method && method.ReturnType.ToString() != "void" ||
            node is DelegateDeclarationSyntax callback && callback.ReturnType.ToString() != "void")
            CheckText(document.Element("returns"), "returns");

        void CheckNames(string tag, string[] names)
        {
            foreach (string name in names)
            {
                var entries = document.Elements(tag).Where(e => (string?)e.Attribute("name") == name).ToArray();
                if (entries.Length != 1)
                    Fail(file, line, $"{tag} 必须且只能说明一次参数 {name}。");
                else
                    CheckText(entries[0], tag + ":" + name);
            }
            foreach (var entry in document.Elements(tag))
                if (!names.Contains((string?)entry.Attribute("name")))
                    Fail(file, line, $"{tag} 引用了签名中不存在的参数。");
        }

        void CheckText(XElement? entry, string name)
        {
            string text = entry?.Value ?? "";
            if (!text.Any(c => c is >= '\u4e00' and <= '\u9fff'))
                Fail(file, line, $"{name} 必须包含中文说明。");
            if (text.Contains('\ufffd') || text.Contains("???", StringComparison.Ordinal))
                Fail(file, line, $"{name} 包含疑似乱码。");
        }
    }
}

Console.WriteLine($"Checked {declarations} documented declarations; errors: {errors}.");
return errors == 0 ? 0 : 1;

void Fail(string file, int line, string message)
{
    Console.Error.WriteLine($"{Path.GetRelativePath(root, file)}:{line}: {message}");
    errors++;
}
