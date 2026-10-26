using System.Globalization;

namespace PureDb;

/// <summary>把已验证的托管模式编码为 SQLite 模式 SQL，不丢弃默认值、检查约束和外键。</summary>
internal static class SqliteSchema
{
    /// <summary>将原生模式中无法保持语义的 SQL 统一报告为不支持，避免把它当作可执行的新模式。</summary>
    /// <param name="sql">sqlite_schema 保存的完整定义。</param>
    /// <param name="strict">是否要求原地读写所需的精确类型声明。</param>
    /// <returns>通过词法与语法校验的模式语句。</returns>
    internal static List<SqlStatement> Parse(string sql, bool strict = true)
    {
        try
        {
            return new SqlParser(sql, sqliteFile: strict).Parse();
        }
        catch (SqlException error) { throw new NotSupportedException("Unsupported SQLite schema syntax.", error); }
    }

    /// <summary>引用标识符，保留名称中的空格、引号和非 ASCII 字符。</summary>
    /// <param name="name">模式对象或列名。</param>
    /// <returns>可安全嵌入模式 SQL 的双引号标识符。</returns>
    internal static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>输出可往返的有限实数 SQL 字面量，并保留整数形实数的 REAL 存储类别。</summary>
    /// <param name="value">已验证有限的实数。</param>
    /// <returns>包含小数点或指数的 SQLite 数字文本。</returns>
    private static string Real(double value)
    {
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.IndexOfAny(['.', 'E', 'e']) < 0 ? text + ".0" : text;
    }

    /// <summary>生成 SQLite 能保持含义的持久表达式；不支持的运行时表达式明确拒绝。</summary>
    /// <param name="expression">常量、列、约束或索引表达式。</param>
    /// <returns>带明确括号及转义的 SQL 表达式。</returns>
    internal static string Expression(Expr expression)
    {
        var args = expression.Args;
        string E(int i) => Expression(args[i]);
        if (expression.Subquery is not null || expression.Window is not null)
            throw new NotSupportedException("Subqueries and windows cannot be serialized as schema expressions.");
        if (expression.Op.StartsWith("CAST:", StringComparison.Ordinal))
            return "CAST(" + E(0) + " AS " + expression.Op[5..] + ")";
        if (expression.Op is "case" or "casebase")
        {
            bool simple = expression.Op == "casebase";
            var parts = new List<string> { "CASE" + (simple ? " " + E(0) : "") };
            for (int i = simple ? 1 : 0; i < args.Length - 1; i += 2)
                parts.Add("WHEN " + E(i) + " THEN " + E(i + 1));
            parts.Add("ELSE " + E(args.Length - 1) + " END");
            return "(" + string.Join(' ', parts) + ")";
        }
        return expression.Op switch
        {
            "column" => string.Join('.', ((string)expression.Value!).Split('.').Select(Quote)),
            "literal" => expression.Value switch
            {
                null => "NULL",
                long n => n.ToString(CultureInfo.InvariantCulture),
                double d when double.IsFinite(d) => Real(d),
                string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
                byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
                _ => throw new NotSupportedException("Unsupported SQLite schema literal.")
            },
            "negate" => "(-" + E(0) + ")",
            "NOT" => "(NOT " + E(0) + ")",
            "IS NULL" or "IS NOT NULL" => "(" + E(0) + " " + expression.Op + ")",
            "BETWEEN" => "(" + E(0) + " BETWEEN " + E(1) + " AND " + E(2) + ")",
            "IN" => "(" + E(0) + " IN (" + string.Join(',', args.Skip(1).Select(Expression)) + "))",
            "+" or "-" or "*" or "/" or "%" or "||" or "=" or "!=" or "<>" or "<" or ">" or "<=" or ">=" or "AND" or "OR" or "LIKE" => "(" + E(0) + " " + expression.Op + " " + E(1) + ")",
            "parameter" or "star" or "composite" or "case" or "casebase" or "raise" => throw new NotSupportedException("Unsupported SQLite schema expression: " + expression.Op),
            _ when expression.Op.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') => expression.Op + "(" + string.Join(',', args.Select(Expression)) + ")",
            _ => throw new NotSupportedException("Unsupported SQLite schema expression: " + expression.Op)
        };
    }

    /// <summary>编码表定义；唯一性由显式唯一索引保存，避免重建隐式索引编号时产生歧义。</summary>
    /// <param name="table">不可变的表模式。</param>
    /// <returns>包含列、默认值、CHECK 和外键的建表 SQL。</returns>
    internal static string Table(SqlTable table)
    {
        var parts = table.Columns.Select(c => Quote(c.Name) + " " + (c.Type is "JSON" or "JSONB" ? "JSON" : c.Type)
            + (c.Primary ? " PRIMARY KEY" : "") + (c.NotNull ? " NOT NULL" : "")
            + (c.Default is null ? "" : " DEFAULT (" + Expression(c.Default) + ")")).ToList();
        parts.AddRange(table.Checks.Select(c => "CHECK(" + Expression(c) + ")"));
        foreach (var fk in table.ForeignKeys)
            parts.Add("FOREIGN KEY(" + string.Join(',', fk.Columns.Select(Quote)) + ") REFERENCES " + Quote(fk.Parent)
                + (fk.ParentColumns is null ? "" : "(" + string.Join(',', fk.ParentColumns.Select(Quote)) + ")")
                + " ON DELETE " + fk.OnDelete + " ON UPDATE " + fk.OnUpdate
                + (fk.Deferred ? " DEFERRABLE INITIALLY DEFERRED" : ""));
        return "CREATE TABLE " + Quote(table.Name) + "(" + string.Join(',', parts) + ")";
    }

    /// <summary>把表级唯一约束转换为相同键顺序的显式索引定义。</summary>
    /// <param name="table">需要添加约束索引的模式对象。</param>
    /// <param name="constraints">按声明顺序保存的唯一列集合。</param>
    internal static void AddUniqueConstraints(SqlTable table, List<string[]> constraints)
    {
        for (int i = 0; i < constraints.Count; i++)
            table.Indexes.Add(new("puredb_constraint_" + table.Id + "_" + i, "", true,
                new("composite", Items: constraints[i].Select(n => new Expr("column", n)).ToArray()))
            {
                Descending = new bool[constraints[i].Length]
            });
    }
}
