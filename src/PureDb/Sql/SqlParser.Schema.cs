namespace PureDb;
/// <remarks>本部分解析外键、视图和行级触发器，并保留可重开执行的 SQL 文本。</remarks>
internal sealed partial class SqlParser
{
    /// <summary>解析括号包围的标识符列表。</summary>
    /// <returns>按声明顺序排列的名称。</returns>
    private string[] ReadNames()
    {
        Need("(");
        var names = new List<string>();
        do
        {
            names.Add(Name());
        }
        while (Eat(","));
        Need(")");
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            throw Error("Duplicate constraint column");
        return names.ToArray();
    }

    /// <summary>解析 REFERENCES 的目标、级联动作和延迟检查选项。</summary>
    /// <param name = "columns">子表列名。</param>
    /// <returns>完整外键定义。</returns>
    private SqlForeignKey ReadReferences(string[] columns)
    {
        string parent = Name();
        string[]? parentColumns = Is("(") ? ReadNames() : null;
        string delete = "NO ACTION", update = "NO ACTION";
        bool deferrable = false, deferred = false;
        while (true)
        {
            if (Eat("ON"))
            {
                bool deleting = Eat("DELETE");
                if (!deleting)
                    Need("UPDATE");
                string action;
                if (Eat("SET"))
                {
                    if (Eat("NULL"))
                        action = "SET NULL";
                    else
                    {
                        Need("DEFAULT");
                        action = "SET DEFAULT";
                    }
                }
                else if (Eat("NO"))
                {
                    Need("ACTION");
                    action = "NO ACTION";
                }
                else if (Eat("CASCADE"))
                    action = "CASCADE";
                else
                {
                    Need("RESTRICT");
                    action = "RESTRICT";
                }

                if (deleting)
                    delete = action;
                else
                    update = action;
            }
            else if (Eat("MATCH"))
                Name();
            else if (Eat("DEFERRABLE"))
                deferrable = true;
            else if (Is("NOT") && _tokens[_pos + 1].Text.Equals("DEFERRABLE", StringComparison.OrdinalIgnoreCase))
            {
                _pos += 2;
                deferrable = false;
            }
            else if (Eat("INITIALLY"))
            {
                if (Eat("DEFERRED"))
                    deferred = true;
                else
                {
                    Need("IMMEDIATE");
                    deferred = false;
                }
            }
            else
                break;
        }

        if (parentColumns is not null && columns.Length != parentColumns.Length)
            throw Error("Foreign-key column count mismatch");
        return new(columns, parent, parentColumns, delete, update, deferrable && deferred);
    }

    /// <summary>按原始词法单元重建可重新解析的 SQL，保留字符串和引用标识符。</summary>
    /// <param name = "start">起始单元位置。</param>
    /// <param name = "end">不包含的结束位置。</param>
    /// <returns>不含参数的模式 SQL 文本。</returns>
    private string Render(int start, int end)
    {
        var tokens = _tokens.GetRange(start, end - start);
        if (tokens.Any(t => t.Kind == "parameter"))
            throw Error("Schema SQL cannot contain parameters");
        return string.Join(" ", tokens.Select(t => t.Kind == "string" ? "'" + t.Text.Replace("'", "''") + "'" : t.Kind == "quoted" ? "\"" + t.Text.Replace("\"", "\"\"") + "\"" : t.Text));
    }

    /// <summary>解析 CREATE VIEW，查询在使用时绑定以允许视图之间的依赖。</summary>
    /// <param name = "unique">是否错误地声明了 UNIQUE。</param>
    /// <returns>视图定义语句。</returns>
    private CreateView ReadView(bool unique)
    {
        if (unique)
            throw Error("CREATE UNIQUE VIEW is invalid");
        bool optional = Eat("IF");
        if (optional)
        {
            Need("NOT");
            Need("EXISTS");
        }

        string name = Name();
        string[]? columns = Is("(") ? ReadNames() : null;
        Need("AS");
        int start = _pos;
        _ = ReadQuery();
        return new(new(name, columns, Render(start, _pos)), optional);
    }

    /// <summary>解析行级触发器，限制体内语句为查询和增删改。</summary>
    /// <param name = "start">CREATE 关键字的位置。</param>
    /// <param name = "unique">是否错误地声明了 UNIQUE。</param>
    /// <returns>可持久化并可执行的触发器定义。</returns>
    private CreateTrigger ReadTrigger(int start, bool unique)
    {
        if (unique)
            throw Error("CREATE UNIQUE TRIGGER is invalid");
        bool optional = Eat("IF");
        if (optional)
        {
            Need("NOT");
            Need("EXISTS");
        }

        string name = Name();
        string timing = Eat("AFTER") ? "AFTER" : Eat("INSTEAD") ? "INSTEAD OF" : "BEFORE";
        if (timing == "INSTEAD OF")
            Need("OF");
        else if (timing == "BEFORE")
            Eat("BEFORE");
        string action = Name().ToUpperInvariant();
        if (action is not ("INSERT" or "UPDATE" or "DELETE"))
            throw Error("Invalid trigger event");
        string[]? columns = null;
        if (action == "UPDATE" && Eat("OF"))
        {
            var names = new List<string>();
            do
            {
                names.Add(Name());
            }
            while (Eat(","));
            columns = names.ToArray();
        }

        Need("ON");
        string table = Name();
        if (Eat("FOR"))
        {
            Need("EACH");
            Need("ROW");
        }

        Expr? when = Eat("WHEN") ? Expression() : null;
        Need("BEGIN");
        var body = new List<SqlStatement>();
        while (!Eat("END"))
        {
            if (Current.Kind == "end")
                throw Error("Unclosed trigger body");
            var statement = Statement();
            if (statement is not (Insert or Update or Delete or Select))
                throw Error("Trigger body accepts INSERT/UPDATE/DELETE/SELECT only");
            body.Add(statement);
            Need(";");
        }

        if (body.Count == 0)
            throw Error("Empty trigger body");
        return new(new(name, table, timing, action, Render(start, _pos)), optional, columns, when, body);
    }
}
