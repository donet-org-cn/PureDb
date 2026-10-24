using System.Globalization;
using System.Text;

namespace PureDb;
/// <summary>SQL 词法和递归下降解析器；表达式采用优先级解析，未知语法显式失败。</summary>
internal sealed partial class SqlParser
{
    /// <summary>词法单元，区分关键字候选、引用标识符、字符串、数字、参数和符号。</summary>
    /// <param name = "Kind">语句对象类别、连接类型或词法类别。</param>
    /// <param name = "Text">JSON 文本或词法单元原始内容。</param>
    private sealed record Token(string Kind, string Text);
    private readonly List<Token> _tokens = [];
    private int _pos, _parameter;
    private readonly bool _sqliteFile;
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "WHERE",
        "INNER",
        "LEFT",
        "RIGHT",
        "FULL",
        "CROSS",
        "JOIN",
        "ON",
        "ORDER",
        "GROUP",
        "HAVING",
        "LIMIT",
        "OFFSET",
        "ASC",
        "DESC",
        "SET",
        "VALUES",
        "FROM",
        "AND",
        "OR",
        "UNION",
        "INTERSECT",
        "EXCEPT",
        "WINDOW",
        "OVER",
        "RETURNING",
        "ROWS",
        "RANGE",
        "GROUPS",
        "PARTITION",
        "EXCLUDE",
        "END",
        "WHEN",
        "THEN",
        "ELSE"
    };
    /// <summary>对 SQL 文本进行词法分析，保留引用标识符、参数和注释边界。</summary>
    /// <param name = "sql">需要词法和语法分析的 SQL 文本。</param>
    /// <param name = "sqliteFile">是否启用 SQLite 文件模式所要求的更严格类型声明校验。</param>
    public SqlParser(string sql, bool sqliteFile = false)
    {
        _sqliteFile = sqliteFile;
        for (int i = 0; i < sql.Length;)
        {
            char c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                    i++;
                continue;
            }

            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                int end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    throw Error("Unclosed comment");
                i = end + 2;
                continue;
            }

            if (c is '\'' or '"' or '[' or '`')
            {
                char end = c == '[' ? ']' : c;
                var b = new StringBuilder();
                i++;
                bool closed = false;
                while (i < sql.Length)
                {
                    if (sql[i] == end)
                    {
                        i++;
                        if (i < sql.Length && sql[i] == end)
                        {
                            b.Append(end);
                            i++;
                            continue;
                        }

                        closed = true;
                        break;
                    }

                    b.Append(sql[i++]);
                }

                if (!closed)
                    throw Error("Unclosed quoted token");
                _tokens.Add(new(c == '\'' ? "string" : "quoted", b.ToString()));
                continue;
            }

            if (c is '@' or ':' or '$' or '?')
            {
                int start = i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                    i++;
                var text = sql[start..i];
                if (text == "?")
                    text += ++_parameter;
                _tokens.Add(new("parameter", text));
                continue;
            }

            if (char.IsDigit(c) || c == '.' && i + 1 < sql.Length && char.IsDigit(sql[i + 1]))
            {
                int start = i++;
                while (i < sql.Length && (char.IsDigit(sql[i]) || sql[i] == '.'))
                    i++;
                if (i < sql.Length && sql[i] is 'e' or 'E')
                {
                    i++;
                    if (i < sql.Length && sql[i] is '+' or '-')
                        i++;
                    while (i < sql.Length && char.IsDigit(sql[i]))
                        i++;
                }

                _tokens.Add(new("number", sql[start..i]));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                    i++;
                _tokens.Add(new("word", sql[start..i]));
                continue;
            }

            if (i + 2 < sql.Length && sql.Substring(i, 3) == "->>")
            {
                _tokens.Add(new("symbol", "->>"));
                i += 3;
                continue;
            }

            string symbol = c.ToString();
            if (i + 1 < sql.Length && (sql.Substring(i, 2) is "<=" or ">=" or "<>" or "!=" or "||" or "->"))
            {
                symbol = sql.Substring(i, 2);
                i++;
            }

            if (!"(),;.*+-/%=<>!|".Contains(c))
                throw Error($"Unexpected character {c}");
            _tokens.Add(new("symbol", symbol));
            i++;
        }

        _tokens.Add(new("end", "<end>"));
    }

    /// <summary>构造包含当前语法错误说明的 SqlException。</summary>
    /// <param name = "message">SQL 语法或执行错误的说明。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private static SqlException Error(string message) => new(message);
    /// <summary>当前解析位置的词法单元。</summary>
    private Token Current => _tokens[_pos];

    /// <summary>判断当前非引用词法单元是否为指定关键字或符号。</summary>
    /// <param name = "text">待匹配的词法文本或 JSON 文本。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    private bool Is(string text) => Current.Kind is "word" or "symbol" && Current.Text.Equals(text, StringComparison.OrdinalIgnoreCase);
    /// <summary>匹配当前关键字或符号，成功时前移一个词法单元。</summary>
    /// <param name = "text">待匹配的词法文本或 JSON 文本。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    private bool Eat(string text)
    {
        if (!Is(text))
            return false;
        _pos++;
        return true;
    }

    /// <summary>要求并消费指定关键字或符号，缺失时报告明确语法错误。</summary>
    /// <param name = "text">待匹配的词法文本或 JSON 文本。</param>
    private void Need(string text)
    {
        if (!Eat(text))
            throw Error($"Expected {text}, found {Current.Text}");
    }

    /// <summary>消费一个普通或带引用的标识符。</summary>
    /// <returns>按当前方法语义生成、解析或规范化后的文本。</returns>
    private string Name()
    {
        if (Current.Kind is not ("word" or "quoted"))
            throw Error("Expected identifier");
        return _tokens[_pos++].Text;
    }

    /// <summary>按语句边界解析整段 SQL，拒绝空文本和未实现的语法。</summary>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    public List<SqlStatement> Parse()
    {
        var statements = new List<SqlStatement>();
        while (Current.Kind != "end")
        {
            if (Eat(";"))
                continue;
            statements.Add(Statement());
            if (Current.Kind != "end")
                Need(";");
        }

        if (statements.Count == 0)
            throw Error("Empty SQL");
        return statements;
    }

    /// <summary>解析当前 DDL、DML 或 SELECT 语句。</summary>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private SqlStatement Statement()
    {
        int statementStart = _pos;
        if (ReadTransaction() is { } transaction)
            return transaction;
        if (Is("WITH"))
            return ReadWithStatement();
        if (Eat("CREATE"))
        {
            bool unique = Eat("UNIQUE");
            if (Eat("VIEW"))
                return ReadView(unique);
            if (Eat("TRIGGER"))
                return ReadTrigger(statementStart, unique);
            if (Eat("TABLE"))
            {
                if (unique)
                    throw Error("CREATE UNIQUE TABLE is unsupported");
                bool optional = Eat("IF");
                if (optional)
                {
                    Need("NOT");
                    Need("EXISTS");
                }

                string name = Name();
                Need("(");
                var columns = new List<SqlColumn>();
                var foreignKeys = new List<SqlForeignKey>();
                var uniques = new List<string[]>();
                string? primary = null;
                do
                {
                    if (Eat("CONSTRAINT"))
                        Name();
                    if (Eat("FOREIGN"))
                    {
                        Need("KEY");
                        var childColumns = ReadNames();
                        Need("REFERENCES");
                        foreignKeys.Add(ReadReferences(childColumns));
                        continue;
                    }

                    if (Eat("UNIQUE"))
                    {
                        uniques.Add(ReadNames());
                        continue;
                    }

                    if (Eat("PRIMARY"))
                    {
                        Need("KEY");
                        Need("(");
                        primary = Name();
                        Need(")");
                        continue;
                    }

                    string column = Name(), type = Name().ToUpperInvariant();
                    bool pk = false, required = false, uq = false;
                    Expr? defaultValue = null;
                    if (_sqliteFile && type is not ("INTEGER" or "REAL" or "TEXT" or "BLOB" or "JSON"))
                        throw new NotSupportedException("SQLite compatibility requires exact INTEGER/REAL/TEXT/BLOB/JSON declarations; native JSONB is not yet supported.");
                    if (type is "INT" or "BIGINT" or "BOOLEAN")
                        type = "INTEGER";
                    if (type is "VARCHAR" or "STRING")
                        type = "TEXT";
                    if (type is "DOUBLE" or "FLOAT")
                        type = "REAL";
                    if (type is not ("INTEGER" or "REAL" or "TEXT" or "BLOB" or "JSON" or "JSONB"))
                        throw Error($"Unsupported type {type}");
                    while (true)
                    {
                        if (Eat("PRIMARY"))
                        {
                            Need("KEY");
                            pk = true;
                        }
                        else if (Eat("NOT"))
                        {
                            Need("NULL");
                            required = true;
                        }
                        else if (Eat("UNIQUE"))
                            uq = true;
                        else if (Eat("CONSTRAINT"))
                            Name();
                        else if (Eat("REFERENCES"))
                            foreignKeys.Add(ReadReferences([column]));
                        else if (Eat("DEFAULT"))
                        {
                            if (Eat("("))
                            {
                                defaultValue = Expression();
                                Need(")");
                            }
                            else
                                defaultValue = Expression(7);
                        }
                        else
                            break;
                    }

                    if (columns.Any(x => x.Name.Equals(column, StringComparison.OrdinalIgnoreCase)))
                        throw Error("Duplicate column");
                    columns.Add(new(column, type, pk, required, uq)
                    {
                        Default = defaultValue
                    });
                }
                while (Eat(","));
                Need(")");
                if (primary is not null)
                {
                    int at = columns.FindIndex(c => c.Name.Equals(primary, StringComparison.OrdinalIgnoreCase));
                    if (at < 0)
                        throw Error("Unknown primary key");
                    columns[at] = columns[at] with
                    {
                        Primary = true
                    };
                }

                if (columns.Count(c => c.Primary) != 1 || columns.Single(c => c.Primary).Type != "INTEGER")
                    throw Error("Exactly one INTEGER PRIMARY KEY is required");
                if (_sqliteFile && (foreignKeys.Count > 0 || uniques.Count > 0 || columns.Any(c => c.Default is not null)))
                    throw new NotSupportedException("SQLite file foreign-key, table UNIQUE and DEFAULT schema support is not implemented yet");
                return new CreateTable(name, columns, optional)
                {
                    ForeignKeys = foreignKeys,
                    UniqueColumns = uniques
                };
            }

            Need("INDEX");
            bool opt = Eat("IF");
            if (opt)
            {
                Need("NOT");
                Need("EXISTS");
            }

            string index = Name();
            Need("ON");
            string table = Name();
            Need("(");
            var expressions = new List<Expr>();
            var directions = new List<bool>();
            do
            {
                expressions.Add(Expression());
                bool desc = Eat("DESC");
                if (!desc)
                    Eat("ASC");
                directions.Add(desc);
            }
            while (Eat(","));
            Need(")");
            bool composite = expressions.Count > 1 || directions.Any(d => d);
            if (_sqliteFile && composite)
                throw new NotSupportedException("SQLite file composite index page support is not implemented");
            return new CreateIndex(index, table, composite ? new Expr("composite", Items: expressions.ToArray()) : expressions[0], unique, opt)
            {
                Descending = composite ? directions.ToArray() : null
            };
        }

        if (Eat("DROP"))
        {
            string kind = Name().ToUpperInvariant();
            if (kind is not ("TABLE" or "INDEX" or "VIEW" or "TRIGGER"))
                throw Error("Unsupported DROP");
            bool optional = Eat("IF");
            if (optional)
                Need("EXISTS");
            return new Drop(kind, Name(), optional);
        }

        if (Eat("INSERT"))
        {
            Need("INTO");
            string table = Name();
            string[]? columns = null;
            if (Eat("("))
            {
                var list = new List<string>();
                do
                {
                    list.Add(Name());
                }
                while (Eat(","));
                Need(")");
                columns = list.ToArray();
            }

            if (Is("SELECT") || Is("WITH"))
                return new Insert(table, columns, [])
                {
                    Query = ReadQuery()
                };
            if (Eat("DEFAULT"))
            {
                Need("VALUES");
                return new Insert(table, [], [[]]);
            }

            Need("VALUES");
            var values = new List<Expr[]>();
            do
            {
                Need("(");
                var row = new List<Expr>();
                do
                {
                    row.Add(Expression());
                }
                while (Eat(","));
                Need(")");
                values.Add(row.ToArray());
            }
            while (Eat(","));
            return new Insert(table, columns, values);
        }

        if (Eat("UPDATE"))
        {
            string table = Name();
            Need("SET");
            var assignments = new List<(string, Expr)>();
            do
            {
                string col = Name();
                Need("=");
                assignments.Add((col, Expression()));
            }
            while (Eat(","));
            return new Update(table, assignments, Eat("WHERE") ? Expression() : null);
        }

        if (Eat("DELETE"))
        {
            Need("FROM");
            string table = Name();
            return new Delete(table, Eat("WHERE") ? Expression() : null);
        }

        if (Is("SELECT"))
            return ReadQuery();
        throw Error($"Unsupported statement near {Current.Text}");
    }

    /// <summary>解析不含尾部集合运算、排序和分页的单个 SELECT 核心。</summary>
    /// <returns>当前简单查询的语法树。</returns>
    private Select ReadSelectCore()
    {
        Need("SELECT");
        {
            bool distinct = Eat("DISTINCT");
            if (!distinct)
                Eat("ALL");
            var cols = new List<Projection>();
            do
            {
                var expr = Expression();
                string? alias = Eat("AS") ? Name() : Current.Kind is "word" or "quoted" && !Reserved.Contains(Current.Text) ? Name() : null;
                cols.Add(new(expr, alias));
            }
            while (Eat(","));
            var from = Eat("FROM") ? ReadSource() : new Source("", "");
            var joins = new List<Join>();
            while (Is(",") || Is("JOIN") || Is("INNER") || Is("LEFT") || Is("RIGHT") || Is("FULL") || Is("CROSS"))
            {
                if (Eat(","))
                {
                    joins.Add(new("CROSS", ReadSource(), null));
                    continue;
                }

                string kind = "INNER";
                if (!Is("JOIN"))
                {
                    kind = Name().ToUpperInvariant();
                    if (kind is "LEFT" or "RIGHT" or "FULL")
                        Eat("OUTER");
                }

                Need("JOIN");
                var source = ReadSource();
                Expr? on = null;
                if (kind != "CROSS")
                {
                    Need("ON");
                    on = Expression();
                }

                joins.Add(new(kind, source, on));
            }

            var where = Eat("WHERE") ? Expression() : null;
            var groups = new List<Expr>();
            if (Eat("GROUP"))
            {
                Need("BY");
                do
                {
                    groups.Add(Expression());
                }
                while (Eat(","));
            }

            var having = Eat("HAVING") ? Expression() : null;
            var windows = new Dictionary<string, SqlWindow>(StringComparer.OrdinalIgnoreCase);
            if (Eat("WINDOW"))
            {
                do
                {
                    string name = Name();
                    Need("AS");
                    if (!windows.TryAdd(name, ReadWindow()))
                        throw Error("Duplicate window name");
                }
                while (Eat(","));
            }

            return AnalyzeQuery(new Select(cols, from, joins, where, groups, having, [], null, null, distinct) { Windows = windows });
        }
    }

    /// <summary>解析整个复合查询共用的 ORDER BY 和 LIMIT。</summary>
    /// <param name = "select">已经解析的查询核心及集合分支。</param>
    /// <returns>包含最终排序和分页的查询。</returns>
    private Select ReadQueryTail(Select select)
    {
        var order = new List<Ordering>();
        if (Eat("ORDER"))
        {
            Need("BY");
            do
            {
                var e = Expression();
                bool desc = Eat("DESC");
                if (!desc)
                    Eat("ASC");
                order.Add(new(e, desc));
            }
            while (Eat(","));
        }

        Expr? limit = null, offset = null;
        if (Eat("LIMIT"))
        {
            limit = Expression();
            if (Eat("OFFSET"))
                offset = Expression();
            else if (Eat(","))
                (offset, limit) = (limit, Expression());
        }

        return select with
        {
            Order = order,
            Limit = limit,
            Offset = offset
        };
    }

    /// <summary>解析表名及可选别名，并阻止将保留字误当作别名。</summary>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private Source ReadSource()
    {
        if (Eat("("))
        {
            var query = ReadQuery();
            Need(")");
            string derivedAlias = Eat("AS") ? Name() : Current.Kind is "word" or "quoted" && !Reserved.Contains(Current.Text) ? Name() : "subquery";
            return new Source("", derivedAlias)
            {
                Query = query
            };
        }

        string table = Name();
        Expr[]? arguments = null;
        if (Eat("("))
        {
            var list = new List<Expr>();
            if (!Eat(")"))
            {
                do
                {
                    list.Add(Expression());
                }
                while (Eat(","));
                Need(")");
            }

            arguments = list.ToArray();
        }

        string alias = Eat("AS") ? Name() : Current.Kind is "word" or "quoted" && !Reserved.Contains(Current.Text) ? Name() : table;
        return new(table, alias)
        {
            Arguments = arguments
        };
    }

    /// <summary>按最小优先级递归解析 SQL 表达式，保持操作符结合顺序。</summary>
    /// <param name = "min">允许继续结合的最小操作符优先级。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private Expr Expression(int min = 0)
    {
        Expr left;
        if (Eat("CASE"))
        {
            bool simple = !Is("WHEN");
            var branches = new List<Expr>();
            if (simple)
                branches.Add(Expression());
            int pairs = 0;
            while (Eat("WHEN"))
            {
                branches.Add(Expression());
                Need("THEN");
                branches.Add(Expression());
                pairs++;
            }

            if (pairs == 0)
                throw Error("CASE requires WHEN");
            branches.Add(Eat("ELSE") ? Expression() : Expr.Literal(null));
            Need("END");
            left = new(simple ? "casebase" : "case", Items: branches.ToArray());
        }
        else if (Eat("EXISTS"))
        {
            Need("(");
            left = new Expr("exists")
            {
                Subquery = ReadQuery()
            };
            Need(")");
        }
        else if (Eat("NOT"))
            left = new("NOT", Items: [Expression(3)]);
        else if (Eat("-"))
            left = new("negate", Items: [Expression(7)]);
        else if (Eat("+"))
            left = Expression(7);
        else if (Eat("("))
        {
            left = Is("SELECT") || Is("WITH") ? new Expr("subquery")
            {
                Subquery = ReadQuery()
            }

            : Expression();
            Need(")");
        }
        else if (Eat("*"))
            left = new("star");
        else if (Eat("NULL"))
            left = Expr.Literal(null);
        else if (Eat("TRUE"))
            left = Expr.Literal(1L);
        else if (Eat("FALSE"))
            left = Expr.Literal(0L);
        else if (Current.Kind == "number")
        {
            string n = _tokens[_pos++].Text;
            if (long.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                left = Expr.Literal(l);
            else if (double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d))
                left = Expr.Literal(d);
            else
                throw Error("Invalid number");
        }
        else if (Current.Kind == "string")
            left = Expr.Literal(_tokens[_pos++].Text);
        else if (Current.Kind == "parameter")
            left = new("parameter", _tokens[_pos++].Text);
        else
        {
            string name = Name();
            if (name.Equals("RAISE", StringComparison.OrdinalIgnoreCase) && Eat("("))
            {
                Need("ABORT");
                Need(",");
                var message = Expression();
                Need(")");
                left = new("raise", Items: [message]);
            }
            else if (name.Equals("X", StringComparison.OrdinalIgnoreCase) && Current.Kind == "string")
            {
                try
                {
                    left = Expr.Literal(Convert.FromHexString(_tokens[_pos++].Text));
                }
                catch (FormatException)
                {
                    throw Error("Invalid hex blob");
                }
            }
            else if (Eat("("))
            {
                var args = new List<Expr>();
                if (!Eat(")"))
                {
                    do
                    {
                        args.Add(Expression());
                    }
                    while (Eat(","));
                    Need(")");
                }

                left = new(name.ToUpperInvariant(), Items: args.ToArray());
            }
            else if (Eat("."))
                left = Eat("*") ? new("star", name) : new("column", name + "." + Name());
            else
                left = new("column", name);
        }

        if (Eat("OVER"))
            left = left with
            {
                Window = ReadWindow()
            };
        while (true)
        {
            string op = Current.Text.ToUpperInvariant();
            int precedence = op switch
            {
                "OR" => 1,
                "AND" => 2,
                "=" or "!=" or "<>" or "<" or ">" or "<=" or ">=" or "IS" or "BETWEEN" or "IN" or "LIKE" => 3,
                "||" or "->" or "->>" => 4,
                "+" or "-" => 5,
                "*" or "/" or "%" => 6,
                _ => -1
            };
            bool not = op == "NOT" && _pos + 1 < _tokens.Count && _tokens[_pos + 1].Text.ToUpperInvariant() is "IN" or "BETWEEN" or "LIKE";
            if (not)
                precedence = 3;
            if (precedence < min || precedence < 0)
                break;
            _pos++;
            if (not)
                op = _tokens[_pos++].Text.ToUpperInvariant();
            if (op == "IS")
            {
                bool negate = Eat("NOT");
                Need("NULL");
                left = new(negate ? "IS NOT NULL" : "IS NULL", Items: [left]);
            }
            else if (op == "BETWEEN")
            {
                var low = Expression(4);
                Need("AND");
                var high = Expression(4);
                left = new("BETWEEN", Items: [left, low, high]);
            }
            else if (op == "IN")
            {
                Need("(");
                if (Is("SELECT") || Is("WITH"))
                {
                    left = new Expr("inquery", Items: [left])
                    {
                        Subquery = ReadQuery()
                    };
                    Need(")");
                    if (not)
                        left = new("NOT", Items: [left]);
                    continue;
                }

                var list = new List<Expr>
                {
                    left
                };
                if (!Eat(")"))
                {
                    do
                    {
                        list.Add(Expression());
                    }
                    while (Eat(","));
                    Need(")");
                }

                left = new("IN", Items: list.ToArray());
            }
            else
                left = new(op, Items: [left, Expression(precedence + 1)]);
            if (not)
                left = new("NOT", Items: [left]);
        }

        return left;
    }
}
