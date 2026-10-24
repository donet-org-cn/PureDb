namespace PureDb;
/// <remarks>本部分解析 WITH、派生查询和集合运算，共用原有词法与表达式解析。</remarks>
internal sealed partial class SqlParser
{
    /// <summary>解析可带 WITH 的完整 SELECT，集合运算按从左到右结合。</summary>
    /// <returns>包含公共表表达式、集合运算和最终分页的查询树。</returns>
    private Select ReadQuery()
    {
        var (ctes, recursive) = ReadCtes();
        return ReadQueryBody(ctes, recursive);
    }

    /// <summary>解析公共表表达式定义，不消费其后的查询或增删改语句。</summary>
    /// <returns>公共表表达式和 RECURSIVE 标记。</returns>
    private (List<SqlCte> Ctes, bool Recursive) ReadCtes()
    {
        var ctes = new List<SqlCte>();
        bool recursive = false;
        if (Eat("WITH"))
        {
            recursive = Eat("RECURSIVE");
            do
            {
                string name = Name();
                string[]? columns = null;
                if (Eat("("))
                {
                    var names = new List<string>();
                    do
                    {
                        names.Add(Name());
                    }
                    while (Eat(","));
                    Need(")");
                    columns = names.ToArray();
                }

                Need("AS");
                // 当前始终按语句物化；易变函数的调用次数可能与 SQLite 的提示选择不同。
                if (Eat("NOT"))
                    Need("MATERIALIZED");
                else
                    Eat("MATERIALIZED");
                Need("(");
                var query = ReadQuery();
                Need(")");
                if (ctes.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw Error("Duplicate CTE name");
                ctes.Add(new(name, columns, query));
            }
            while (Eat(","));
        }

        return (ctes, recursive);
    }

    /// <summary>解析已经取得 WITH 定义之后的查询主体。</summary>
    /// <param name = "ctes">当前语句的公共表表达式。</param>
    /// <param name = "recursive">是否允许递归。</param>
    /// <returns>包含最终排序分页的查询。</returns>
    private Select ReadQueryBody(List<SqlCte> ctes, bool recursive)
    {
        var first = ReadSelectCore();
        var compounds = new List<SqlCompound>();
        while (Is("UNION") || Is("INTERSECT") || Is("EXCEPT"))
        {
            string op = Name().ToUpperInvariant();
            if (op == "UNION" && Eat("ALL"))
                op = "UNION ALL";
            compounds.Add(new(op, ReadSelectCore()));
        }

        var result = ReadQueryTail(first with
        {
            Ctes = ctes,
            Recursive = recursive,
            Compounds = compounds
        });
        return AnalyzeQuery(result);
    }

    /// <summary>解析时分析查询核心的扩展执行标记，集合的每个分支也独立分析。</summary>
    /// <param name = "result">已经解析的查询。</param>
    /// <returns>带快速分派标记的查询。</returns>
    private static Select AnalyzeQuery(Select result)
    {
        static bool Extended(Expr? e) => e is not null && (e.Window is not null || e.Subquery is not null || e.Args.Any(Extended));
        static bool Window(Expr e) => e.Window is not null || e.Args.Any(Window);
        return result with
        {
            HasWindows = result.Columns.Any(p => Window(p.Expr)) || result.Order.Any(o => Window(o.Expr)),
            RequiresContext = result.Ctes.Count > 0 || result.Compounds.Count > 0 || result.From.Query is not null || result.From.Arguments is not null || result.Joins.Any(j => j.Source.Query is not null || j.Source.Arguments is not null || Extended(j.On)) || result.Columns.Any(p => Extended(p.Expr)) || Extended(result.Where) || Extended(result.Having) || result.GroupBy.Any(Extended) || result.Order.Any(o => Extended(o.Expr))
        };
    }

    /// <summary>解析 WITH 后的 SELECT、INSERT、UPDATE 或 DELETE。</summary>
    /// <returns>带语句作用域的查询或增删改节点。</returns>
    private SqlStatement ReadWithStatement()
    {
        var (ctes, recursive) = ReadCtes();
        if (Is("SELECT"))
            return ReadQueryBody(ctes, recursive);
        var statement = Statement();
        if (statement is not (Insert or Update or Delete))
            throw Error("WITH requires SELECT/INSERT/UPDATE/DELETE");
        return new SqlWithStatement(ctes, recursive, statement);
    }

    /// <summary>解析命名窗口引用或 OVER 括号中的分区、排序与帧。</summary>
    /// <returns>保留默认帧区别的窗口定义。</returns>
    private SqlWindow ReadWindow()
    {
        if (!Eat("("))
            return new(Name(), [], []);
        string? parent = Current.Kind is "word" or "quoted" && !Reserved.Contains(Current.Text) ? Name() : null;
        var partition = new List<Expr>();
        var order = new List<Ordering>();
        if (Eat("PARTITION"))
        {
            Need("BY");
            do
            {
                partition.Add(Expression());
            }
            while (Eat(","));
        }

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

        string? frame = Is("ROWS") || Is("RANGE") || Is("GROUPS") ? Name().ToUpperInvariant() : null;
        SqlWindowBound? start = null, end = null;
        string exclude = "NO OTHERS";
        if (frame is not null)
        {
            bool between = Eat("BETWEEN");
            start = ReadWindowBound();
            if (between)
            {
                Need("AND");
                end = ReadWindowBound();
            }
            else
                end = new("CURRENT ROW");
            if (Eat("EXCLUDE"))
            {
                if (Eat("CURRENT"))
                {
                    Need("ROW");
                    exclude = "CURRENT ROW";
                }
                else if (Eat("NO"))
                {
                    Need("OTHERS");
                }
                else if (Eat("GROUP"))
                    exclude = "GROUP";
                else
                {
                    Need("TIES");
                    exclude = "TIES";
                }
            }
        }

        Need(")");
        return new(parent, partition, order, frame, start, end, exclude);
    }

    /// <summary>解析窗口的单个边界，偏移表达式在执行时验证。</summary>
    /// <returns>边界方向和偏移。</returns>
    private SqlWindowBound ReadWindowBound()
    {
        if (Eat("UNBOUNDED"))
            return new(Eat("PRECEDING") ? "UNBOUNDED PRECEDING" : ReadFollowing());
        if (Eat("CURRENT"))
        {
            Need("ROW");
            return new("CURRENT ROW");
        }

        var offset = Expression(4);
        if (Eat("PRECEDING"))
            return new("PRECEDING", offset);
        Need("FOLLOWING");
        return new("FOLLOWING", offset);
        string ReadFollowing()
        {
            Need("FOLLOWING");
            return "UNBOUNDED FOLLOWING";
        }
    }
}
