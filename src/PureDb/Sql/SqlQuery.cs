namespace PureDb;
/// <remarks>本部分选择查询计划，并提供通用连接、分组、投影、排序和分页执行器。</remarks>
internal sealed partial class SqlSession
{
    /// <summary>将 AND 表达式展开为可独立分析的合取条件。</summary>
    /// <param name = "e">可选 SQL 表达式；null 表示未提供该子句。</param>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    private static IEnumerable<Expr> Conjuncts(Expr? e) => e is null ? [] : e.Op == "AND" ? Conjuncts(e.Args[0]).Concat(Conjuncts(e.Args[1])) : [e];
    /// <summary>判断表达式能否在读取行之前安全求值；易变用户函数不满足条件。</summary>
    /// <param name = "e">待分析、绑定或求值的 SQL 表达式。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    private bool Constant(Expr e) => e.Subquery is null && e.Window is null && e.Op != "column" && e.Op != "star" && (!owner.TryFunction(e.Op, e.Args.Length, out var function) || function.Deterministic) && e.Args.All(Constant) && !SqlBinding.Aggregate(e);
    /// <summary>识别属于指定数据源别名的列引用，排除其他连接输入。</summary>
    /// <param name = "e">待分析、绑定或求值的 SQL 表达式。</param>
    /// <param name = "alias">只允许列引用所属的当前数据源别名。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private static string? OwnColumn(Expr e, string alias)
    {
        if (e.Op != "column")
            return null;
        var parts = ((string)e.Value!).Split('.');
        return parts.Length == 1 || parts[0].Equals(alias, StringComparison.OrdinalIgnoreCase) ? parts[^1] : null;
    }

    /// <summary>根据安全谓词选择主键、普通索引或表达式索引的候选行，并报告排序性质。</summary>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <param name = "alias">当前语句内的数据源别名。</param>
    /// <param name = "where">可选 SQL 表达式；null 表示未提供该子句。</param>
    /// <param name = "order">查询要求的排序表达式及方向。</param>
    /// <param name = "parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <param name = "ordered">返回候选行是否已经满足查询要求的排序。</param>
    /// <param name = "plan">返回候选选择实际使用的索引或扫描路径。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private IEnumerable<object?[]> Candidates(SqlTable table, string alias, Expr? where, List<Ordering> order, IReadOnlyDictionary<string, object?> parameters, out bool ordered, out string plan)
    {
        if (table.Id < 0)
        {
            ordered = false;
            plan = "VirtualTableScan";
            return Data(table).Rows;
        }

        if (CompositeCandidates(table, alias, where, order, parameters, out ordered, out plan) is { } composite)
            return composite;
        var binding = new SqlBinding(owner, this);
        int orderColumn = order.Count == 1 && OwnColumn(order[0].Expr, alias) is { } name ? table.Columns.FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) : -1;
        ordered = false;
        plan = "TableScan";
        bool IndexOrder(int column, string op)
        {
            if (order.Count == 0)
                return true;
            if (order.Count == 1 && OwnColumn(order[0].Expr, alias) is { } n)
                return n.Equals(table.Columns[column].Name, StringComparison.OrdinalIgnoreCase) || op == "=" && n.Equals(table.Columns[table.Primary].Name, StringComparison.OrdinalIgnoreCase);
            return order.Count == 2 && order[0].Descending == order[1].Descending && OwnColumn(order[0].Expr, alias)?.Equals(table.Columns[column].Name, StringComparison.OrdinalIgnoreCase) == true && OwnColumn(order[1].Expr, alias)?.Equals(table.Columns[table.Primary].Name, StringComparison.OrdinalIgnoreCase) == true;
        }

        foreach (var term in Conjuncts(where))
        {
            if (term.Op is not ("=" or "<" or ">" or "<=" or ">=" or "BETWEEN"))
                continue;
            var args = term.Args;
            string? col = OwnColumn(args[0], alias);
            Expr val = args[1];
            string op = term.Op;
            if (col is null && op != "BETWEEN" && (col = OwnColumn(args[1], alias)) is not null)
            {
                val = args[0];
                op = op switch
                {
                    "<" => ">",
                    ">" => "<",
                    "<=" => ">=",
                    ">=" => "<=",
                    _ => op
                };
            }

            if (col is null && Constant(val) && (op != "BETWEEN" || Constant(args[2])))
            {
                var expressionIndex = table.Indexes.FirstOrDefault(i => i.Expression is not null && SqlAst.Equal(i.Expression, args[0]));
                if (expressionIndex is not null)
                {
                    // 带限定名的表达式必须属于当前数据源，不能引用 JOIN 的其他别名。
                    bool Local(Expr e) => e.Op != "column" ? e.Args.All(Local) : OwnColumn(e, alias) is not null;
                    if (!Local(args[0]))
                        continue;
                    ordered = order.Count == 0 || order.Count == 1 && (SqlAst.Equal(order[0].Expr, args[0]) || op == "=" && OwnColumn(order[0].Expr, alias)?.Equals(table.Columns[table.Primary].Name, StringComparison.OrdinalIgnoreCase) == true);
                    plan = "ExpressionIndexSeek(" + expressionIndex.Name + ")";
                    return Data(table).SeekExpression(expressionIndex, op, binding.Bind(val, parameters)([]), op == "BETWEEN" ? binding.Bind(args[2], parameters)([]) : null, ordered && order.Count > 0 && order[0].Descending);
                }
            }

            if (col is null || !Constant(val) || op == "BETWEEN" && !Constant(args[2]))
                continue;
            int column = table.Columns.FindIndex(c => c.Name.Equals(col, StringComparison.OrdinalIgnoreCase));
            if (column < 0)
                continue;
            bool indexed = column == table.Primary || table.Columns[column].Unique || table.Indexes.Any(i => i.Expression is null && table.Column(i.Column) == column);
            if (!indexed)
                continue;
            object? value = binding.Bind(val, parameters)([]);
            if (column == table.Primary && op == "=")
            {
                ordered = true;
                plan = "PrimaryKeySeek";
                if (value is not (long or double))
                    return [];
                double d = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                if (d < 0 || d >= 1L << 40 || d != Math.Truncate(d))
                    return [];
                var row = Row(table, (long)d);
                return row is null ? [] : [row];
            }

            ordered = IndexOrder(column, op);
            plan = "IndexRangeSeek(" + table.Columns[column].Name + ")";
            object? high = op == "BETWEEN" ? binding.Bind(args[2], parameters)([]) : null;
            if (column == table.Primary && snapshot.HasPersistentIndexes && !HasMutations && value is long or double && (op != "BETWEEN" || high is long or double))
            {
                plan = "PrimaryKeyRangeSeek";
                return PrimaryRangeRows(table, op, value, high, ordered && order.Count > 0 && order[0].Descending);
            }

            return Data(table).Seek(column, op, value, high, ordered && order.Count > 0 && order[0].Descending);
        }

        var data = Data(table);
        if (orderColumn >= 0 && (orderColumn == table.Primary || table.Indexes.Any(i => i.Expression is null && table.Column(i.Column) == orderColumn) || table.Columns[orderColumn].Unique))
        {
            ordered = true;
            plan = "OrderedIndexScan(" + table.Columns[orderColumn].Name + ")";
            var sorted = data.Ordered(orderColumn);
            return order[0].Descending ? sorted.Reverse() : sorted;
        }

        return data.Rows;
    }

    /// <summary>使用持久化主索引读取整数主键范围，精确处理实数边界和开闭区间。</summary>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <param name = "op">当前 SQL 操作符或函数名。</param>
    /// <param name = "value">待处理的标量或参数实例，受当前接口的类型约束。</param>
    /// <param name = "high">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <param name = "descending">是否按降序枚举结果。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private IEnumerable<object?[]> PrimaryRangeRows(SqlTable table, string op, object value, object? high, bool descending)
    {
        double bound = value is long n ? n : (double)value;
        double min = op switch
        {
            ">=" or "BETWEEN" => Math.Ceiling(bound),
            ">" => Math.Floor(bound) + 1,
            _ => 0
        };
        double max = op switch
        {
            "<=" => Math.Floor(bound),
            "<" => Math.Ceiling(bound) - 1,
            "BETWEEN" => Math.Floor(high is long h ? h : (double)high!),
            _ => (1L << 40) - 1
        };
        min = Math.Max(0, min);
        max = Math.Min((1L << 40) - 1, max);
        if (min > max)
            yield break;
        long prefix = (long)table.Id << 40;
        var entries = snapshot.TableEntriesRange(table.Id, prefix + (long)min, prefix + (long)max);
        if (descending)
            entries = entries.Reverse();
        foreach (var entry in entries)
            yield return SqlCodec.Decode(snapshot.IndexValue(entry), table.Columns.Count);
    }

    /// <summary>将左右输入行拼接为连接行。</summary>
    /// <param name = "left">连接左侧的当前输入行。</param>
    /// <param name = "right">连接右侧的当前输入行。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private static object?[] Combine(object?[] left, object?[] right)
    {
        var row = new object?[left.Length + right.Length];
        left.CopyTo(row, 0);
        right.CopyTo(row, left.Length);
        return row;
    }

    /// <summary>按可用等值键使用哈希连接，否则执行嵌套循环，并补齐外连接的 NULL 行。</summary>
    /// <param name = "left">按当前执行顺序枚举的输入行。</param>
    /// <param name = "right">当前版本解码后的行及辅助查找结构。</param>
    /// <param name = "oldWidth">加入右输入前，左侧行数组的总列数。</param>
    /// <param name = "kind">INNER、LEFT、RIGHT、FULL 或 CROSS 连接类型。</param>
    /// <param name = "condition">连接 ON 条件的逐行求值委托。</param>
    /// <param name = "leftKey">左输入连接键的列序号。</param>
    /// <param name = "rightKey">右输入连接键的列序号。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private static IEnumerable<object?[]> JoinRows(IEnumerable<object?[]> left, SqlTableData right, int oldWidth, string kind, Func<object?[], object?>? condition, int leftKey, int rightKey)
    {
        bool hash = leftKey >= 0;
        var lookup = hash ? right.Hash(rightKey) : null;
        var matched = kind is "RIGHT" or "FULL" ? new HashSet<object?[]>(ReferenceEqualityComparer.Instance) : null;
        foreach (var l in left)
        {
            IEnumerable<object?[]> candidates = right.Rows;
            if (hash)
                candidates = l[leftKey] is { } key && lookup!.TryGetValue(key, out var found) ? found : [];
            bool any = false;
            foreach (var r in candidates)
            {
                var joined = Combine(l, r);
                if (condition is not null && SqlValueComparer.Truth(condition(joined)) != true)
                    continue;
                any = true;
                matched?.Add(r);
                yield return joined;
            }

            if (!any && kind is "LEFT" or "FULL")
                yield return Combine(l, new object?[right.Schema.Columns.Count]);
        }

        if (matched is not null)
            foreach (var r in right.Rows)
                if (!matched.Contains(r))
                    yield return Combine(new object?[oldWidth], r);
    }

    /// <summary>递归比较两个表达式的结构及字面量值。</summary>
    /// <param name = "a">待分析、绑定或求值的 SQL 表达式。</param>
    /// <param name = "b">待分析、绑定或求值的 SQL 表达式。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    private static bool Same(Expr a, Expr b) => a.Op == b.Op && Equals(a.Value, b.Value) && a.Args.Length == b.Args.Length && a.Args.Zip(b.Args).All(p => Same(p.First, p.Second));
    /// <summary>绑定分组后表达式，计算聚合并拒绝不属于分组键的裸列。</summary>
    /// <param name = "expr">待分析、绑定或求值的 SQL 表达式。</param>
    /// <param name = "select">当前已解析的 SELECT 查询结构。</param>
    /// <param name = "binding">当前数据源的列名、偏移及函数绑定上下文。</param>
    /// <param name = "parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private static Func<object?[][], object?> GroupExpression(Expr expr, Select select, SqlBinding binding, IReadOnlyDictionary<string, object?> parameters)
    {
        if (expr.Op is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX")
        {
            if (expr.Args.Length != 1 || expr.Op != "COUNT" && expr.Args[0].Op == "star" || SqlBinding.Aggregate(expr.Args[0]))
                throw new SqlException("Invalid aggregate");
            if (expr.Args[0].Op == "star")
                return group => (long)group.Length;
            var get = binding.Bind(expr.Args[0], parameters);
            return group =>
            {
                var values = group.Select(get).Where(v => v is not null).ToArray();
                if (expr.Op == "COUNT")
                    return (long)values.Length;
                if (values.Length == 0)
                    return null;
                if (expr.Op == "MIN")
                    return values.Min(SqlValueComparer.Instance);
                if (expr.Op == "MAX")
                    return values.Max(SqlValueComparer.Instance);
                if (values.Any(v => v is not (long or double)))
                    throw new SqlException("SUM/AVG requires numeric values");
                if (expr.Op == "SUM" && values.All(v => v is long))
                {
                    long sum = 0;
                    foreach (long value in values.Cast<long>())
                        sum = checked(sum + value);
                    return sum;
                }

                double d = values.Sum(v => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture));
                return expr.Op == "AVG" ? d / values.Length : d;
            };
        }

        if (SameGroup(expr) || expr.Op is "literal" or "parameter")
        {
            var get = binding.Bind(expr, parameters);
            return group => get(group.Length > 0 ? group[0] : new object?[binding.Width]);
        }

        if (expr.Op == "column")
            throw new SqlException("Nonaggregate columns must appear in GROUP BY");
        var children = expr.Args.Select(e => GroupExpression(e, select, binding, parameters)).ToArray();
        if (expr.Subquery is not null)
        {
            var subquery = binding.Bind(expr, parameters);
            return group => subquery(group.Length == 0 ? [] : group[0]);
        }

        if (expr.Op is "case" or "casebase")
            return group =>
            {
                bool simple = expr.Op == "casebase";
                object? basis = simple ? children[0](group) : null;
                for (int i = simple ? 1 : 0; i < children.Length - 1; i += 2)
                {
                    object? condition = children[i](group);
                    if (simple ? basis is not null && condition is not null && SqlValueComparer.Instance.Equals(basis, condition) : SqlValueComparer.Truth(condition) == true)
                        return children[i + 1](group);
                }

                return children[^1](group);
            };
        if (binding.TryFunction(expr.Op, children.Length, out var function))
            return group => function.Invoke(children.Select(f => f(group)).ToArray());
        SqlBinding.Validate(expr.Op, children.Length);
        if (expr.Op == "COALESCE")
            return group =>
            {
                foreach (var child in children)
                    if (child(group) is { } value)
                        return value;
                return null;
            };
        return group => SqlBinding.Eval(expr.Op, children.Select(f => f(group)).ToArray());
        bool SameGroup(Expr e) => select.GroupBy.Any(g => Same(e, g));
    }

    /// <summary>通用查询执行器的投影值和排序键，避免排序时重复计算表达式。</summary>
    /// <param name = "Values">按对应模式列序或函数参数顺序排列的 SQL 标量值。</param>
    /// <param name = "Keys">按对应模式列序或函数参数顺序排列的 SQL 标量值。</param>
    private sealed record Output(object?[] Values, object?[] Keys);
    /// <summary>为当前 SELECT 选择专用查询计划；用户函数等结构保留通用语义路径。</summary>
    /// <param name = "select">当前已解析的 SELECT 查询结构。</param>
    /// <param name = "parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private SqlResult QueryCore(Select select, IReadOnlyDictionary<string, object?> parameters)
    {
        if (select.HasWindows && (select.GroupBy.Count > 0 || select.Having is not null || select.Columns.Any(p => SqlBinding.Aggregate(p.Expr))))
            return QueryGroupedWindows(select, parameters);
        var first = SourceTable(select.From, parameters);
        if (first.Id < 0 || select.RequiresContext || _namedSources is not null || _outerRows is { Count: > 0 } || owner.UsesFunction(select))
            return QueryGeneral(select, first, parameters);
        if (first.Id != 0 && select.Joins.Count == 0 && select.Where is null && select.GroupBy.Count == 0 && select.Having is null && !select.Distinct && select.Order.Count == 0 && select.Columns.Count == 1 && select.Columns[0].Expr.Op == "COUNT" && select.Columns[0].Expr.Args.Length == 1 && select.Columns[0].Expr.Args[0].Op == "star")
        {
            long count = snapshot.CountKey(first.Id);
            if (_mutations is not null)
                foreach (var m in _mutations.Values)
                {
                    if (m.Id < 0 || (m.Id >> 40) != first.Id)
                        continue;
                    bool existed = !snapshot.Value(m.Id).IsEmpty;
                    if (m.IsDelete && existed)
                        count--;
                    else if (!m.IsDelete && !existed)
                        count++;
                }

            int take = LimitNumber(select.Limit, int.MaxValue), skip = LimitNumber(select.Offset, 0);
            return new([select.Columns[0].Alias ?? "count"], take > 0 && skip == 0 ? [new object?[] { count }] : [], "CountMetadata");
            int LimitNumber(Expr? e, int fallback)
            {
                if (e is null)
                    return fallback;
                object? v = new SqlBinding(owner, this).Bind(e, parameters)([]);
                return v is long n ? n < 0 ? fallback : (int)Math.Min(n, int.MaxValue) : throw new SqlException("Invalid LIMIT/OFFSET");
            }
        }

        var aggregate = AggregateFast(select, first, parameters) ?? GroupCountFast(select, first, parameters);
        if (aggregate is not null)
            return aggregate;
        var primaryPlan = owner.PrimaryPlan(select, first);
        if (primaryPlan.Supported)
        {
            object? key = primaryPlan.Key!.Op == "literal" ? primaryPlan.Key.Value : SqlPrimaryPlan.Parameter(primaryPlan.Key, parameters);
            object?[]? row = null;
            if (SqlPrimaryPredicate.TryId(key, out long id))
                row = ProjectRow(first, id, primaryPlan.Columns, primaryPlan.IndexOnly);
            return new(primaryPlan.Names, row is null ? [] : [row], primaryPlan.IndexOnly && snapshot.HasPersistentIndexes ? "PrimaryKeySeek(covering)" : "PrimaryKeySeek");
        }

        var scalarPlan = owner.ScalarPlan(select, first);
        if (scalarPlan.Supported)
        {
            object? key = SqlRangePlan.Value(scalarPlan.Key, parameters);
            object?[]? row = null;
            if (SqlPrimaryPredicate.TryId(key, out long id))
                row = Row(first, id);
            if (row is null)
                return new(scalarPlan.Names, [], "PrimaryScalarSeek");
            var values = new object?[scalarPlan.Selectors.Length];
            for (int i = 0; i < values.Length; i++)
            {
                object? value = scalarPlan.Selectors[i](row);
                values[i] = value is byte[] b ? b.ToArray() : SqlJsonFunctions.Unwrap(value);
            }

            return new(scalarPlan.Names, [values], "PrimaryScalarSeek");
        }

        var rangePlan = owner.RangePlan(select, first);
        if (rangePlan.Supported)
        {
            object? low = SqlRangePlan.Value(rangePlan.Low, parameters), high = SqlRangePlan.Value(rangePlan.High, parameters);
            int skip = SqlRangePlan.Count(rangePlan.Offset, parameters, 0), take = SqlRangePlan.Count(rangePlan.Limit, parameters, int.MaxValue);
            if (!HasMutations && owner.DiskRange(snapshot, first, rangePlan, low, high, skip, take) is { } diskRows)
                return new(rangePlan.Names, diskRows, "PersistentIndexRangeSeek(" + first.Columns[rangePlan.Column].Name + ")");
            var result = Data(first).Seek(rangePlan.Column, rangePlan.Op, low, high, rangePlan.Descending).Skip(skip).Take(take).Select(row => rangePlan.Columns.Select(i => row[i] is byte[] bytes ? bytes.ToArray() : SqlJsonFunctions.Unwrap(row[i])).ToArray()).ToArray();
            return new(rangePlan.Names, result, "IndexRangeSeek(" + first.Columns[rangePlan.Column].Name + ")" + (rangePlan.Limit is null ? "" : " -> Limit"));
        }

        if (select.Joins.Count == 1)
        {
            var right = SourceTable(select.Joins[0].Source, parameters);
            if (owner.JoinPlan(select, first, right) is { Supported: true } joinPlan)
                return QueryPrimaryJoin(joinPlan, first, right, parameters);
        }

        if (select.Joins.Count == 0 && owner.ExpressionPlan(select, first) is { Supported: true } expressionPlan)
            return QueryExpressionRange(expressionPlan, first, parameters);
        return QueryGeneral(select, first, parameters);
    }

    // 将通用解释器放在独立方法中，减少索引读取热点路径的 JIT 编译工作。
    /// <summary>执行通用连接、过滤、分组、投影、去重、排序和分页。</summary>
    /// <param name = "select">当前已解析的 SELECT 查询结构。</param>
    /// <param name = "first">SELECT 第一个数据源对应的不可变表模式。</param>
    /// <param name = "parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private SqlResult QueryGeneral(Select select, SqlTable first, IReadOnlyDictionary<string, object?> parameters)
    {
        var binding = new SqlBinding(owner, this);
        binding.Add(first, select.From.Alias);
        bool outerRight = select.Joins.Any(j => j.Kind is "RIGHT" or "FULL");
        bool ordered = false;
        string plan = "ConstantRow";
        IEnumerable<object?[]> source = first.Id == 0 ? [Array.Empty<object?>()] : Candidates(first, select.From.Alias, outerRight ? null : select.Where, select.Joins.Count == 0 ? select.Order : [], parameters, out ordered, out plan);
        // 枚举行之前先绑定所有 JOIN 别名和 ON 表达式，空输入也必须检查绑定错误。
        foreach (var join in select.Joins)
        {
            var arguments = join.Source.Arguments?.Select(e => binding.Bind(e, parameters)).ToArray();
            var table = arguments is null ? SourceTable(join.Source, parameters) : JsonTableSchema(join.Source);
            if (arguments is not null)
                (_sourceCache ??= [])[join.Source] = table;
            int oldWidth = binding.Width;
            binding.Add(table, join.Source.Alias);
            var on = join.On is null ? null : binding.Bind(join.On, parameters);
            if (arguments is not null)
            {
                source = JsonJoinRows(source, arguments, join.Source.Table.Equals("JSON_TREE", StringComparison.OrdinalIgnoreCase), join.Kind, on);
                ordered = false;
                plan += " -> JsonTableJoin";
                continue;
            }

            int leftKey = -1, rightKey = -1;
            foreach (var term in Conjuncts(join.On))
            {
                if (term.Op != "=" || term.Args.Any(a => a.Op != "column"))
                    continue;
                int a = binding.Resolve((string)term.Args[0].Value!), b = binding.Resolve((string)term.Args[1].Value!);
                if (a >= oldWidth && b < oldWidth)
                    (a, b) = (b, a);
                if (a < oldWidth && b >= oldWidth)
                {
                    leftKey = a;
                    rightKey = b - oldWidth;
                    break;
                }
            }

            bool declared = rightKey >= 0 && (rightKey == table.Primary || table.Indexes.Any(i => i.Expression is null && table.Column(i.Column) == rightKey) || table.Columns[rightKey].Unique);
            plan += " -> " + (leftKey >= 0 ? declared ? "IndexJoin" : "HashJoin" : "NestedLoopJoin") + "(" + join.Kind + "," + join.Source.Table + ")";
            bool pureEquality = leftKey >= 0 && join.On?.Op == "=";
            bool joinOrdered = select.Joins.Count == 1 && plan.StartsWith("PrimaryKeySeek", StringComparison.Ordinal) && pureEquality && select.Order.Count == 1 && !select.Order[0].Descending && select.Order[0].Expr.Op == "column" && binding.Resolve((string)select.Order[0].Expr.Value!) == oldWidth + table.Primary;
            source = JoinRows(source, Data(table), oldWidth, join.Kind, pureEquality ? null : on, leftKey, rightKey);
            ordered = joinOrdered;
        }

        if (select.Where is not null && !(select.Joins.Count == 0 && Conjuncts(select.Where).Count() == 1 && (plan.StartsWith("IndexRangeSeek", StringComparison.Ordinal) || plan.StartsWith("ExpressionIndexSeek", StringComparison.Ordinal))))
        {
            var filter = binding.Bind(select.Where, parameters);
            source = source.Where(row => SqlValueComparer.Truth(filter(row)) == true);
        }

        var projections = new List<Projection>();
        foreach (var p in select.Columns)
        {
            if (p.Expr.Op != "star")
            {
                projections.Add(p);
                continue;
            }

            if (p.Alias is not null)
                throw new SqlException("Star projection cannot have an alias");
            var sources = binding.Sources.Where(s => p.Expr.Value is null || s.Alias.Equals((string)p.Expr.Value, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sources.Length == 0)
                throw new SqlException("Unknown star qualifier");
            foreach (var s in sources)
                foreach (var c in s.Table.Columns.Where(c => !c.Hidden))
                    projections.Add(new(new("column", s.Alias + "." + c.Name), c.Name));
        }

        string[] names = projections.Select((p, i) => p.Alias ?? (p.Expr.Op == "column" ? ((string)p.Expr.Value!).Split('.')[^1] : p.Expr.Op is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX" ? p.Expr.Op.ToLowerInvariant() : "expr" + (i + 1))).ToArray();
        bool grouped = select.GroupBy.Count > 0 || projections.Any(p => SqlBinding.Aggregate(p.Expr)) || select.Having is not null || select.Order.Any(o => SqlBinding.Aggregate(o.Expr));
        source = PrepareWindows(select, binding, source, parameters);
        int OrderProjection(Expr expr)
        {
            if (expr.Op == "literal" && expr.Value is long ordinal)
            {
                if (ordinal < 1 || ordinal > projections.Count)
                    throw new SqlException("ORDER BY ordinal out of range");
                return (int)ordinal - 1;
            }

            if (expr.Op == "column" && !((string)expr.Value!).Contains('.'))
                return Array.FindIndex(names, n => n.Equals((string)expr.Value, StringComparison.OrdinalIgnoreCase));
            return -1;
        }

        int offset = Number(select.Offset, 0), limit = Number(select.Limit, int.MaxValue);
        bool topSorted = false;
        int[]? directProjection = null;
        IEnumerable<Output> outputs;
        if (grouped)
        {
            var selectors = projections.Select(p => GroupExpression(p.Expr, select, binding, parameters)).ToArray();
            var groupKeys = select.GroupBy.Select(e => binding.Bind(e, parameters)).ToArray();
            var having = select.Having is null ? null : GroupExpression(select.Having, select, binding, parameters);
            var orderFunctions = select.Order.Select(o => OrderProjection(o.Expr) is var index && index >= 0 ? (Func<object?[][], object?>)(g => selectors[index](g)) : GroupExpression(o.Expr, select, binding, parameters)).ToArray();
            IEnumerable<object?[][]> groups = groupKeys.Length == 0 ? [source.ToArray()] : source.GroupBy(row => new RowKey(groupKeys.Select(f => f(row)).ToArray())).Select(g => g.ToArray());
            if (having is not null)
                groups = groups.Where(g => SqlValueComparer.Truth(having(g)) == true);
            var orderIndexes = select.Order.Select(o => OrderProjection(o.Expr)).ToArray();
            outputs = groups.Select(g =>
            {
                var values = selectors.Select(f => f(g)).ToArray();
                var keys = new object?[orderFunctions.Length];
                for (int i = 0; i < keys.Length; i++)
                    keys[i] = orderIndexes[i] >= 0 ? values[orderIndexes[i]] : orderFunctions[i](g);
                return new Output(values, keys);
            });
            ordered = false;
            plan += " -> Aggregate";
        }
        else
        {
            var selectors = projections.Select(p => binding.Bind(p.Expr, parameters)).ToArray();
            var orderFunctions = select.Order.Select(o => OrderProjection(o.Expr) is var index && index >= 0 ? selectors[index] : binding.Bind(o.Expr, parameters)).ToArray();
            // ORDER BY 的别名可能遮蔽不同的物理列；只有列完全匹配时才能省略排序。
            if (ordered && select.Order.Any(o => o.Expr.Op == "column" && OrderProjection(o.Expr) is var i && i >= 0 && (projections[i].Expr.Op != "column" || binding.Resolve((string)o.Expr.Value!) != binding.Resolve((string)projections[i].Expr.Value!))))
                ordered = false;
            var orderIndexes = select.Order.Select(o => OrderProjection(o.Expr)).ToArray();
            // 大分页、分组和 DISTINCT 保留通用路径；使用 long 求和防止分页整数溢出。
            if (!ordered && !select.Distinct && select.Order.Count > 0 && limit > 0 && (long)offset + limit <= 4096)
            {
                bool[] descending = select.Order.Select(o => o.Descending).ToArray();
                int[]? columns = projections.All(p => p.Expr.Op == "column") ? projections.Select(p => binding.Resolve((string)p.Expr.Value!)).ToArray() : null;
                int[]? orderColumns = columns is null ? null : select.Order.Select((o, i) => orderIndexes[i] >= 0 ? columns[orderIndexes[i]] : o.Expr.Op == "column" ? binding.Resolve((string)o.Expr.Value!) : -1).ToArray();
                bool columnSort = orderColumns is not null && orderColumns.All(c => c >= 0);
                outputs = columnSort ? TopSortedColumns(source, orderColumns!, descending, offset + limit) : TopSortedRows(source, selectors, orderFunctions, orderIndexes, descending, offset + limit);
                if (columnSort)
                    directProjection = columns;
                topSorted = true;
                plan += (columnSort ? " -> TopSortColumns(" : " -> TopSort(") + (offset + limit) + ")";
            }
            else
            {
                outputs = source.Select(row =>
                {
                    // ORDER BY 别名复用已经得到的投影值，易变函数每行只计算一次。
                    var values = selectors.Select(f => f(row)).ToArray();
                    var keys = ordered ? [] : new object?[orderFunctions.Length];
                    for (int i = 0; i < keys.Length; i++)
                        keys[i] = orderIndexes[i] >= 0 ? values[orderIndexes[i]] : orderFunctions[i](row);
                    return new Output(values, keys);
                });
            }
        }

        if (select.Distinct)
        {
            outputs = outputs.DistinctBy(o => new RowKey(o.Values));
            plan += " -> Distinct";
        }

        if (select.Order.Count > 0 && !ordered && !topSorted)
        {
            IOrderedEnumerable<Output>? sorted = null;
            for (int i = 0; i < select.Order.Count; i++)
            {
                int at = i;
                bool desc = select.Order[i].Descending;
                sorted = sorted is null ? desc ? outputs.OrderByDescending(o => o.Keys[at], SqlValueComparer.Instance) : outputs.OrderBy(o => o.Keys[at], SqlValueComparer.Instance) : desc ? sorted.ThenByDescending(o => o.Keys[at], SqlValueComparer.Instance) : sorted.ThenBy(o => o.Keys[at], SqlValueComparer.Instance);
            }

            outputs = sorted!;
            plan += " -> Sort";
        }

        int Number(Expr? expr, int fallback)
        {
            if (expr is null)
                return fallback;
            object? value = new SqlBinding(owner, this).Bind(expr, parameters)([]);
            if (value is not long n)
                throw new SqlException("LIMIT/OFFSET requires INTEGER");
            return n < 0 ? fallback : (int)Math.Min(n, int.MaxValue);
        }

        var rows = MaterializeOutputs(outputs, offset, limit, directProjection);
        return new(names, rows, plan + (select.Limit is not null ? " -> Limit" : ""));
    }

    /// <summary>按最终分页复制结果，不在查询绑定作用域捕获投影列或创建逐行 LINQ 委托。</summary>
    /// <param name="outputs">已经排序和去重的候选结果。</param>
    /// <param name="offset">需要跳过的行数。</param>
    /// <param name="limit">最多返回的行数。</param>
    /// <param name="columns">纯列路径的原始列位置；null 表示已经求值的投影。</param>
    /// <returns>与内部缓存隔离的结果数组。</returns>
    private static object?[][] MaterializeOutputs(IEnumerable<Output> outputs, int offset, int limit, int[]? columns)
    {
        if (limit == 0)
            return [];
        if (outputs is Output[] array)
        {
            int count = Math.Min(limit, Math.Max(0, array.Length - offset));
            var result = new object?[count][];
            for (int i = 0; i < count; i++)
                result[i] = CopyProjection(array[offset + i].Values, columns);
            return result;
        }
        var rows = new List<object?[]>();
        foreach (var output in outputs)
        {
            if (offset > 0)
            {
                offset--;
                continue;
            }
            rows.Add(CopyProjection(output.Values, columns));
            if (rows.Count == limit)
                break;
        }
        return rows.ToArray();
    }

    /// <summary>仅为实际返回的行复制投影，并将 BLOB 与内部缓存隔离，避免逐行 LINQ 中间对象。</summary>
    /// <param name = "row">内部只读行或已经求值的投影。</param>
    /// <param name = "columns">需要选择的列位置；null 表示复制所有投影值。</param>
    /// <returns>由调用方独立持有的公开结果行。</returns>
    private static object?[] CopyProjection(object?[] row, int[]? columns = null)
    {
        var result = new object?[columns?.Length ?? row.Length];
        for (int i = 0; i < result.Length; i++)
        {
            object? value = row[columns is null ? i : columns[i]];
            result[i] = value is byte[] bytes ? bytes.ToArray() : SqlJsonFunctions.Unwrap(value);
        }

        return result;
    }
}

/// <summary>可直接按主键获取所需列的查询计划，避免整表解码。</summary>
/// <param name = "Supported">该语句结构是否可使用当前专用计划。</param>
/// <param name = "Key">可选 SQL 表达式；null 表示未提供该子句。</param>
/// <param name = "Columns">目标列集合或结果投影，具体结构由字段类型确定。</param>
/// <param name = "Names">按结果列序号排列的列名。</param>
internal sealed record SqlPrimaryPlan(bool Supported, Expr? Key, int[] Columns, IReadOnlyList<string> Names)
{
    /// <summary>投影是否仅需主键值，可避免读取或解码行载荷。</summary>
    internal bool IndexOnly
    {
        get; init;
    }

    /// <summary>按原始名称或去前缀名称取得并规范化参数，缺失时失败。</summary>
    /// <param name = "key">主键谓词中的常量或参数表达式。</param>
    /// <param name = "parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public static object? Parameter(Expr key, IReadOnlyDictionary<string, object?> parameters)
    {
        string name = (string)key.Value!;
        if (!parameters.TryGetValue(name, out var value) && !parameters.TryGetValue(name[1..], out value))
            throw new SqlException("Missing parameter " + name);
        return SqlValueComparer.Normalize(value);
    }

    /// <summary>识别可直接按主键投影的 SELECT；不支持时返回禁用计划。</summary>
    /// <param name = "select">当前已解析的 SELECT 查询结构。</param>
    /// <param name = "table">当前操作的不可变表模式。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    public static SqlPrimaryPlan Build(Select select, SqlTable table)
    {
        SqlPrimaryPlan No() => new(false, null, [], []);
        if (select.Joins.Count > 0 || select.GroupBy.Count > 0 || select.Having is not null || select.Distinct || select.Limit is not null || select.Offset is not null || select.Order.Count > 0 || select.Where?.Op != "=")
            return No();
        var binding = new SqlBinding();
        binding.Add(table, select.From.Alias);
        Expr column = select.Where.Args[0], key = select.Where.Args[1];
        if (column.Op != "column")
            (column, key) = (key, column);
        if (column.Op != "column" || key.Op is not ("literal" or "parameter") || binding.Resolve((string)column.Value!) != table.Primary)
            return No();
        var cols = new List<int>();
        var names = new List<string>();
        foreach (var projection in select.Columns)
        {
            if (projection.Expr.Op == "star")
            {
                if (projection.Alias is not null)
                    return No();
                if (projection.Expr.Value is string alias && !alias.Equals(select.From.Alias, StringComparison.OrdinalIgnoreCase))
                    throw new SqlException("Unknown star qualifier");
                for (int i = 0; i < table.Columns.Count; i++)
                {
                    cols.Add(i);
                    names.Add(table.Columns[i].Name);
                }
            }
            else if (projection.Expr.Op == "column")
            {
                int i = binding.Resolve((string)projection.Expr.Value!);
                cols.Add(i);
                names.Add(projection.Alias ?? table.Columns[i].Name);
            }
            else
                return No();
        }

        return new(true, key, cols.ToArray(), names.AsReadOnly())
        {
            IndexOnly = cols.All(c => c == table.Primary)
        };
    }
}
