namespace PureDb;
/// <summary>包裹已解析的外层列值，保留 SQL NULL 与未知列的区别。</summary>
/// <param name = "Value">当前外层行的列值。</param>
internal sealed record SqlOuterValue(object? Value);
/// <remarks>本部分管理只读派生数据源、CTE、集合运算及相关子查询作用域。</remarks>
internal sealed partial class SqlSession
{
    /// <summary>判断表达式是否需要扩展执行上下文，避免将子查询交给无上下文的旧计划。</summary>
    /// <param name = "expression">待检查的可选表达式。</param>
    /// <returns>包含子查询或窗口时为 true。</returns>
    private static bool ExtendedExpression(Expr? expression) => expression is not null && (expression.Window is not null || expression.Subquery is not null || expression.Args.Any(ExtendedExpression));
    /// <summary>读取最近一层可见的相关列；同层含糊列不会向外层回退。</summary>
    /// <param name = "name">可带别名的外层列引用。</param>
    /// <returns>已找到列的包裹值，未知列返回 null。</returns>
    internal SqlOuterValue? OuterColumn(string name)
    {
        for (int i = (_outerRows?.Count ?? 0) - 1; i >= 0; i--)
        {
            int column = _outerRows![i].Binding.FindColumn(name);
            if (column >= 0)
                return new(_outerRows[i].Row[column]);
        }

        return null;
    }

    /// <summary>绑定标量、EXISTS 或 IN 子查询，在同一存储快照和外层行作用域中执行。</summary>
    /// <param name = "expression">包含查询树的表达式。</param>
    /// <param name = "binding">调用位置的列绑定。</param>
    /// <param name = "parameters">本次查询参数。</param>
    /// <returns>处理 SQL NULL、空集和单列校验的逐行委托。</returns>
    internal Func<object?[], object?> BindSubquery(Expr expression, SqlBinding binding, IReadOnlyDictionary<string, object?> parameters)
    {
        if (expression.Op != "exists" && expression.Subquery!.Columns.All(p => p.Expr.Op != "star") && expression.Subquery.Columns.Count != 1)
            throw new SqlException("Scalar and IN subqueries require one result column");
        ValidateSubquery(expression.Subquery!, binding, parameters);
        var probe = expression.Op == "inquery" ? binding.Bind(expression.Args[0], parameters) : null;
        return row =>
        {
            object? probed = probe?.Invoke(row);
            (_outerRows ??= []).Add((binding, row));
            try
            {
                var query = expression.Subquery!;
                if (expression.Op == "exists" && query.Compounds.Count == 0 && query.GroupBy.Count == 0 && query.Having is null && !query.Columns.Any(p => SqlBinding.Aggregate(p.Expr)))
                    query = query with
                    {
                        Columns = [new(Expr.Literal(1L), null)],
                        Distinct = false,
                        Order = []
                    };
                if (expression.Op is "exists" or "subquery" && query.Limit is null)
                    query = query with
                    {
                        Limit = Expr.Literal(1L)
                    };
                var result = Query(query, parameters);
                if (expression.Op == "exists")
                    return result.Rows.Count > 0 ? 1L : 0L;
                if (result.Columns.Count != 1)
                    throw new SqlException("Scalar and IN subqueries require one result column");
                if (expression.Op == "subquery")
                    return result.Rows.Count == 0 ? null : result.Rows[0][0];
                object? value = probed;
                bool unknown = false;
                foreach (var candidate in result.Rows)
                {
                    if (candidate[0] is null || value is null)
                    {
                        unknown = true;
                        continue;
                    }

                    if (SqlValueComparer.Instance.Equals(value, candidate[0]))
                        return 1L;
                }

                return unknown ? null : 0L;
            }
            finally
            {
                _outerRows.RemoveAt(_outerRows.Count - 1);
            }
        };
    }

    /// <summary>对普通表子查询预绑定列和函数，外层空集也拒绝未知列；不读取数据或调用业务函数。</summary>
    /// <param name = "query">需要预绑定的子查询。</param>
    /// <param name = "outer">外层绑定。</param>
    /// <param name = "parameters">当前参数。</param>
    private void ValidateSubquery(Select query, SqlBinding outer, IReadOnlyDictionary<string, object?> parameters)
    {
        if (query.Ctes.Count > 0 || query.HasWindows || query.From.Query is not null || query.From.Arguments is not null || query.Joins.Any(j => j.Source.Query is not null || j.Source.Arguments is not null))
            return;
        var sources = new[]
        {
            query.From
        }.Concat(query.Joins.Select(j => j.Source)).ToArray();
        if (sources.Any(s => Catalog.Views.Any(v => v.Name.Equals(s.Table, StringComparison.OrdinalIgnoreCase))))
            return;
        var binding = new SqlBinding(owner, this);
        foreach (var source in sources)
        {
            SqlTable table = source.Table.Length == 0 ? new() : _namedSources?.TryGetValue(source.Table, out var named) == true ? named : Catalog.Table(source.Table);
            binding.Add(table, source.Alias);
        }

        (_outerRows ??= []).Add((outer, new object?[outer.Width]));
        try
        {
            bool grouped = query.GroupBy.Count > 0 || query.Having is not null || query.Columns.Any(p => SqlBinding.Aggregate(p.Expr));
            foreach (var projection in query.Columns)
                if (projection.Expr.Op != "star")
                {
                    if (grouped)
                        _ = GroupExpression(projection.Expr, query, binding, parameters);
                    else
                        _ = binding.Bind(projection.Expr, parameters);
                }

            if (query.Where is not null)
                _ = binding.Bind(query.Where, parameters);
            foreach (var join in query.Joins)
                if (join.On is not null)
                    _ = binding.Bind(join.On, parameters);
            foreach (var key in query.GroupBy)
                _ = binding.Bind(key, parameters);
            if (query.Having is not null)
                _ = GroupExpression(query.Having, query, binding, parameters);
            foreach (var order in query.Order)
                if (!(order.Expr.Op == "literal" || order.Expr.Op == "column" && query.Columns.Any(p => (p.Alias ?? (p.Expr.Op == "column" ? ((string)p.Expr.Value!).Split('.')[^1] : null))?.Equals((string)order.Expr.Value!, StringComparison.OrdinalIgnoreCase) == true)))
                {
                    if (grouped)
                        _ = GroupExpression(order.Expr, query, binding, parameters);
                    else
                        _ = binding.Bind(order.Expr, parameters);
                }

            foreach (var branch in query.Compounds)
                ValidateSubquery(branch.Query, outer, parameters);
        }
        finally
        {
            _outerRows.RemoveAt(_outerRows.Count - 1);
        }
    }

    /// <summary>创建只在当前语句有效的只读表；不生成持久化模式或记录变更。</summary>
    /// <param name = "name">派生数据源名称。</param>
    /// <param name = "result">需要物化的查询结果。</param>
    /// <param name = "columns">可覆盖结果列名的显式名称。</param>
    /// <returns>具有独立负数 ID 的虚拟表模式。</returns>
    private SqlTable VirtualTable(string name, SqlResult result, string[]? columns = null)
    {
        if (columns is not null && columns.Length != result.Columns.Count)
            throw new SqlException("CTE column count mismatch");
        var table = new SqlTable
        {
            Id = --_virtualId,
            Name = name
        };
        for (int i = 0; i < result.Columns.Count; i++)
        {
            Type type = i < result.FieldTypes.Count ? result.FieldTypes[i] : typeof(object);
            string sqlType = type == typeof(long) ? "INTEGER" : type == typeof(double) ? "REAL" : type == typeof(string) ? "TEXT" : type == typeof(byte[]) ? "BLOB" : "ANY";
            table.Columns.Add(new(columns?[i] ?? result.Columns[i], sqlType));
        }

        (_virtualData ??= [])[table.Id] = new(table, result.Rows.ToArray(), 0);
        return table;
    }

    /// <summary>解析真实表、CTE 或 FROM 派生表；同一查询的派生表只执行一次。</summary>
    /// <param name = "source">待解析的数据源。</param>
    /// <param name = "parameters">查询参数。</param>
    /// <returns>当前作用域可见的数据源模式。</returns>
    private SqlTable SourceTable(Source source, IReadOnlyDictionary<string, object?> parameters)
    {
        if (source.Query is not null)
        {
            _sourceCache ??= [];
            if (!_sourceCache.TryGetValue(source, out var table))
            {
                var result = Query(source.Query, parameters);
                if (result.FieldTypes.Count != result.Columns.Count)
                    result.FieldTypes = Describe(source.Query, parameters);
                _sourceCache[source] = table = VirtualTable(source.Alias, result);
            }

            return table;
        }

        if (source.Arguments is not null)
        {
            _sourceCache ??= [];
            if (!_sourceCache.TryGetValue(source, out var table))
            {
                table = JsonTableSchema(source);
                var binding = new SqlBinding(owner, this);
                var args = new object?[source.Arguments.Length];
                for (int i = 0; i < args.Length; i++)
                    args[i] = binding.Bind(source.Arguments[i], parameters)([]);
                (_virtualData ??= [])[table.Id] = new(table, SqlJsonFunctions.TableRows(args[0], args.Length == 2 ? args[1] : "$", source.Table.Equals("JSON_TREE", StringComparison.OrdinalIgnoreCase)), 0);
                _sourceCache[source] = table;
            }

            return table;
        }

        if (_namedSources?.TryGetValue(source.Table, out var named) == true)
            return named;
        foreach (var view in Catalog.Views)
            if (view.Name.Equals(source.Table, StringComparison.OrdinalIgnoreCase))
                return ViewTable(source, view, parameters);
        return source.Table.Length == 0 ? new SqlTable() : Catalog.Table(source.Table);
    }

    /// <summary>执行扩展查询并恢复调用者作用域，递归深度限制避免栈溢出。</summary>
    /// <param name = "select">完整查询树。</param>
    /// <param name = "parameters">本次参数。</param>
    /// <returns>物化结果及不执行业务函数推断的列类型。</returns>
    internal SqlResult Query(Select select, IReadOnlyDictionary<string, object?> parameters)
    {
        bool complex = select.RequiresContext || _namedSources is not null || _outerRows is { Count: > 0 } || Catalog.Views.Count > 0 && UsesView(select);
        if (!complex)
            return QueryCore(select, parameters);
        if (++_queryDepth > 64)
        {
            _queryDepth--;
            throw new SqlException("Query nesting exceeds 64 levels");
        }

        var savedNames = _namedSources;
        var savedCache = _sourceCache;
        var savedWindows = _windowValues;
        int virtualScope = _virtualId;
        _sourceCache = [];
        try
        {
            if (select.Ctes.Count > 0)
            {
                _namedSources = savedNames is null ? new(StringComparer.OrdinalIgnoreCase) : new(savedNames, StringComparer.OrdinalIgnoreCase);
                foreach (var cte in select.Ctes)
                {
                    int? maximum = null;
                    if (select.From.Table.Equals(cte.Name, StringComparison.OrdinalIgnoreCase) && select.Joins.Count == 0 && select.Where is null && select.GroupBy.Count == 0 && select.Having is null && select.Order.Count == 0 && !select.Distinct && !select.HasWindows && select.Compounds.Count == 0 && select.Columns.All(p => !ExtendedExpression(p.Expr) && !SqlBinding.Aggregate(p.Expr)) && select.Limit is not null && select.Limit.Op is "literal" or "parameter" or "negate")
                    {
                        int take = SqlRangePlan.Count(select.Limit, parameters, int.MaxValue), skip = SqlRangePlan.Count(select.Offset, parameters, 0);
                        if ((long)take + skip <= 1_000_000)
                            maximum = take + skip;
                    }

                    _namedSources[cte.Name] = MaterializeCte(cte, select.Recursive, parameters, maximum);
                }
            }

            var result = select.Compounds.Count > 0 ? QueryCompound(select, parameters) : QueryCore(select, parameters);
            if (result.FieldTypes.Count != result.Columns.Count)
                result.FieldTypes = Describe(select, parameters);
            return result;
        }
        finally
        {
            _namedSources = savedNames;
            _sourceCache = savedCache;
            _windowValues = savedWindows;
            ReleaseVirtualTables(virtualScope);
            _queryDepth--;
        }
    }

    /// <summary>释放当前嵌套作用域创建的临时表，事务内重复查询不会永久保留结果。</summary>
    /// <param name = "scope">进入作用域前的虚拟表 ID。</param>
    private void ReleaseVirtualTables(int scope)
    {
        if (_virtualData is null)
            return;
        foreach (int id in _virtualData.Keys.Where(id => id < scope).ToArray())
            _virtualData.Remove(id);
    }

    /// <summary>检查本层数据源是否包含视图，不为普通查询创建捕获闭包。</summary>
    /// <param name = "select">当前查询。</param>
    /// <returns>需要独立视图作用域时返回 true。</returns>
    private bool UsesView(Select select)
    {
        foreach (var view in Catalog.Views)
        {
            if (view.Name.Equals(select.From.Table, StringComparison.OrdinalIgnoreCase))
                return true;
            foreach (var join in select.Joins)
                if (view.Name.Equals(join.Source.Table, StringComparison.OrdinalIgnoreCase))
                    return true;
        }

        return false;
    }

    /// <summary>判断查询分支是否直接引用指定的递归表名。</summary>
    /// <param name = "query">待检查分支。</param>
    /// <param name = "name">递归表名称。</param>
    /// <returns>从该表读取时返回 true。</returns>
    private static bool References(Select query, string name) => query.From.Table.Equals(name, StringComparison.OrdinalIgnoreCase) || query.Joins.Any(j => j.Source.Table.Equals(name, StringComparison.OrdinalIgnoreCase));
    /// <summary>物化普通 CTE 或使用工作队列求值递归 CTE，限制生成行数以控制资源。</summary>
    /// <param name = "cte">公共表表达式定义。</param>
    /// <param name = "recursive">当前 WITH 是否声明递归。</param>
    /// <param name = "parameters">查询参数。</param>
    /// <param name = "maximumRows">可安全下推的外层分页行数上限。</param>
    /// <returns>在当前语句内可见的派生表。</returns>
    private SqlTable MaterializeCte(SqlCte cte, bool recursive, IReadOnlyDictionary<string, object?> parameters, int? maximumRows = null)
    {
        var query = cte.Query;
        if (!query.Compounds.Any(c => References(c.Query, cte.Name)))
            return VirtualTable(cte.Name, Query(query, parameters), cte.Columns);
        if (References(query, cte.Name))
            throw new SqlException("Recursive CTE requires a nonrecursive anchor");
        int firstRecursive = query.Compounds.FindIndex(c => References(c.Query, cte.Name));
        var anchor = Query(query with
        {
            Compounds = query.Compounds.Take(firstRecursive).ToList(),
            Order = [],
            Limit = null,
            Offset = null
        }, parameters);
        var table = VirtualTable(cte.Name, anchor, cte.Columns);
        var branches = query.Compounds.Skip(firstRecursive).ToArray();
        bool all = branches[0].Operation == "UNION ALL";
        if (branches.Any(c => c.Operation != (all ? "UNION ALL" : "UNION")))
            throw new SqlException("Recursive CTE branches must use the same UNION operator");
        foreach (var branch in branches)
            if ((branch.Query.From.Table.Equals(cte.Name, StringComparison.OrdinalIgnoreCase) ? 1 : 0) + branch.Query.Joins.Count(j => j.Source.Table.Equals(cte.Name, StringComparison.OrdinalIgnoreCase)) != 1 || branch.Query.GroupBy.Count > 0 || branch.Query.HasWindows || branch.Query.Columns.Any(p => SqlBinding.Aggregate(p.Expr)))
                throw new SqlException("Recursive CTE branches require one direct recursive reference and no aggregates/windows");
        var seen = all ? null : new HashSet<RowKey>();
        var rows = new List<object?[]>();
        int take = query.Limit is null ? int.MaxValue : SqlRangePlan.Count(query.Limit, parameters, int.MaxValue);
        int skip = SqlRangePlan.Count(query.Offset, parameters, 0);
        if (maximumRows is not null)
            take = Math.Min(take, maximumRows.Value);
        var order = query.Order.Select(o =>
        {
            int at = o.Expr.Op == "literal" && o.Expr.Value is long ordinal ? checked((int)ordinal - 1) : o.Expr.Op == "column" ? anchor.Columns.ToList().FindIndex(c => c.Equals((string)o.Expr.Value!, StringComparison.OrdinalIgnoreCase)) : -1;
            if (at < 0 || at >= anchor.Columns.Count)
                throw new SqlException("Recursive ORDER BY must reference an output column or ordinal");
            return (at, o.Descending);
        }).ToArray();
        long sequence = 0, generated = 0;
        var queue = new PriorityQueue<(object?[] Row, long Sequence), (object?[] Row, long Sequence)>(Comparer<(object?[] Row, long Sequence)>.Create((a, b) =>
        {
            foreach (var (column, desc) in order)
            {
                int compare = desc ? SqlValueComparer.Instance.Compare(b.Row[column], a.Row[column]) : SqlValueComparer.Instance.Compare(a.Row[column], b.Row[column]);
                if (compare != 0)
                    return compare;
            }

            return a.Sequence.CompareTo(b.Sequence);
        }));
        void Enqueue(object?[] row)
        {
            if (seen is not null && !seen.Add(new(row)))
                return;
            if (++generated > 1_000_000)
                throw new SqlException("Recursive CTE exceeds 1000000 generated rows");
            var entry = (row, sequence++);
            queue.Enqueue(entry, entry);
        }

        foreach (var row in anchor.Rows)
            Enqueue(row);
        _namedSources![cte.Name] = table;
        int processed = 0;
        while (queue.Count > 0 && rows.Count < take)
        {
            var row = queue.Dequeue().Row;
            if (processed++ >= skip)
                rows.Add(row);
            if (rows.Count >= take)
                break;
            _virtualData![table.Id] = new(table, [row], 0);
            foreach (var branch in branches)
            {
                var next = Query(branch.Query, parameters);
                if (next.Columns.Count != table.Columns.Count)
                    throw new SqlException("Recursive CTE column count mismatch");
                foreach (var item in next.Rows)
                    Enqueue(item);
            }
        }

        _virtualData![table.Id] = new(table, rows.ToArray(), 0);
        return table;
    }

    /// <summary>按 SQL 行相等语义执行集合运算，最终排序和分页作用于整个集合。</summary>
    /// <param name = "select">左侧核心、后续分支及最终子句。</param>
    /// <param name = "parameters">查询参数。</param>
    /// <returns>以最左侧列名命名的集合结果。</returns>
    private SqlResult QueryCompound(Select select, IReadOnlyDictionary<string, object?> parameters)
    {
        var left = Query(select with
        {
            Ctes = [],
            Compounds = [],
            Order = [],
            Limit = null,
            Offset = null
        }, parameters);
        var types = left.FieldTypes.ToArray();
        IEnumerable<object?[]> rows = left.Rows;
        foreach (var branch in select.Compounds)
        {
            var right = Query(branch.Query, parameters);
            if (left.Columns.Count != right.Columns.Count)
                throw new SqlException("Compound SELECT column count mismatch");
            for (int i = 0; i < types.Length; i++)
                if (i >= right.FieldTypes.Count || types[i] != right.FieldTypes[i])
                    types[i] = typeof(object);
            rows = branch.Operation switch
            {
                "UNION ALL" => rows.Concat(right.Rows),
                "UNION" => rows.Concat(right.Rows).DistinctBy(r => new RowKey(r)),
                "INTERSECT" => IntersectRows(rows, right.Rows, false),
                "EXCEPT" => IntersectRows(rows, right.Rows, true),
                _ => throw new SqlException("Unknown compound operator")
            };
        }

        IOrderedEnumerable<object?[]>? sorted = null;
        foreach (var order in select.Order)
        {
            int column = order.Expr.Op == "literal" && order.Expr.Value is long ordinal ? checked((int)ordinal - 1) : order.Expr.Op == "column" ? left.Columns.ToList().FindIndex(n => n.Equals((string)order.Expr.Value!, StringComparison.OrdinalIgnoreCase)) : select.Columns.FindIndex(p => Same(p.Expr, order.Expr));
            if (column < 0 || column >= left.Columns.Count)
                throw new SqlException("Compound ORDER BY must name a result column");
            int at = column;
            sorted = sorted is null ? order.Descending ? rows.OrderByDescending(r => r[at], SqlValueComparer.Instance) : rows.OrderBy(r => r[at], SqlValueComparer.Instance) : order.Descending ? sorted.ThenByDescending(r => r[at], SqlValueComparer.Instance) : sorted.ThenBy(r => r[at], SqlValueComparer.Instance);
        }

        int take = select.Limit is null ? int.MaxValue : SqlRangePlan.Count(select.Limit, parameters, int.MaxValue);
        int skip = select.Offset is null ? 0 : SqlRangePlan.Count(select.Offset, parameters, 0);
        return new(left.Columns, (sorted ?? rows).Skip(skip).Take(take).ToArray(), "CompoundSelect")
        {
            FieldTypes = types
        };
    }

    /// <summary>按整行值执行去重交集或差集。</summary>
    /// <param name = "left">左侧输入。</param>
    /// <param name = "right">右侧输入。</param>
    /// <param name = "except">为 true 时求差集，否则求交集。</param>
    /// <returns>首次出现顺序组织的唯一结果行。</returns>
    private static IEnumerable<object?[]> IntersectRows(IEnumerable<object?[]> left, IReadOnlyList<object?[]> right, bool except)
    {
        var keys = right.Select(r => new RowKey(r)).ToHashSet();
        var seen = new HashSet<RowKey>();
        foreach (var row in left)
        {
            var key = new RowKey(row);
            if (keys.Contains(key) != except && seen.Add(key))
                yield return row;
        }
    }
}
