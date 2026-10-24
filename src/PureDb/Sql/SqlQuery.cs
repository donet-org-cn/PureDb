namespace PureDb;
internal sealed partial class SqlSession
{
    private static IEnumerable<Expr> Conjuncts(Expr? e) => e is null ? [] : e.Op == "AND" ? Conjuncts(e.Args[0]).Concat(Conjuncts(e.Args[1])) : [e];
    private static bool Constant(Expr e) => e.Op != "column" && e.Op != "star" && e.Args.All(Constant) && !SqlBinding.Aggregate(e);
    private static string? OwnColumn(Expr e, string alias)
    {
        if (e.Op != "column") return null;
        var parts = ((string)e.Value!).Split('.'); return parts.Length == 1 || parts[0].Equals(alias, StringComparison.OrdinalIgnoreCase) ? parts[^1] : null;
    }
    private IEnumerable<object?[]> Candidates(SqlTable table, string alias, Expr? where, List<Ordering> order,
        IReadOnlyDictionary<string, object?> parameters, out bool ordered, out string plan)
    {
        var binding = new SqlBinding();
        int orderColumn = order.Count == 1 && OwnColumn(order[0].Expr, alias) is { } name ? table.Columns.FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) : -1;
        ordered = false; plan = "TableScan";
        bool IndexOrder(int column, string op)
        {
            if (order.Count == 0) return true;
            if (order.Count == 1 && OwnColumn(order[0].Expr, alias) is { } n)
                return n.Equals(table.Columns[column].Name, StringComparison.OrdinalIgnoreCase) || op == "=" && n.Equals(table.Columns[table.Primary].Name, StringComparison.OrdinalIgnoreCase);
            return order.Count == 2 && order[0].Descending == order[1].Descending &&
                OwnColumn(order[0].Expr, alias)?.Equals(table.Columns[column].Name, StringComparison.OrdinalIgnoreCase) == true &&
                OwnColumn(order[1].Expr, alias)?.Equals(table.Columns[table.Primary].Name, StringComparison.OrdinalIgnoreCase) == true;
        }

        foreach (var term in Conjuncts(where))
        {
            if (term.Op is not ("=" or "<" or ">" or "<=" or ">=" or "BETWEEN")) continue;
            var args = term.Args; string? col = OwnColumn(args[0], alias); Expr val = args[1]; string op = term.Op;
            if (col is null && op != "BETWEEN" && (col = OwnColumn(args[1], alias)) is not null)
            { val = args[0]; op = op switch { "<" => ">", ">" => "<", "<=" => ">=", ">=" => "<=", _ => op }; }
            if (col is null && Constant(val) && (op != "BETWEEN" || Constant(args[2])))
            {
                var expressionIndex = table.Indexes.FirstOrDefault(i => i.Expression is not null && SqlAst.Equal(i.Expression, args[0]));
                if (expressionIndex is not null)
                {
                    // Qualified expressions must refer to this source, never another JOIN alias.
                    bool Local(Expr e) => e.Op != "column" ? e.Args.All(Local) : OwnColumn(e, alias) is not null;
                    if (!Local(args[0])) continue;
                    ordered = order.Count == 0 || order.Count == 1 && (SqlAst.Equal(order[0].Expr, args[0]) || op == "=" && OwnColumn(order[0].Expr, alias)?.Equals(table.Columns[table.Primary].Name, StringComparison.OrdinalIgnoreCase) == true);
                    plan = "ExpressionIndexSeek(" + expressionIndex.Name + ")";
                    return Data(table).SeekExpression(expressionIndex, op, binding.Bind(val, parameters)([]), op == "BETWEEN" ? binding.Bind(args[2], parameters)([]) : null, ordered && order.Count > 0 && order[0].Descending);
                }
            }
            if (col is null || !Constant(val) || op == "BETWEEN" && !Constant(args[2])) continue;
            int column = table.Columns.FindIndex(c => c.Name.Equals(col, StringComparison.OrdinalIgnoreCase));
            if (column < 0) continue;
            bool indexed = column == table.Primary || table.Columns[column].Unique || table.Indexes.Any(i => i.Expression is null && table.Column(i.Column) == column);
            if (!indexed) continue;
            object? value = binding.Bind(val, parameters)([]);
            if (column == table.Primary && op == "=")
            {
                ordered = true; plan = "PrimaryKeySeek";
                if (value is not (long or double)) return [];
                double d = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                if (d < 0 || d >= 1L << 40 || d != Math.Truncate(d)) return [];
                var row = Row(table, (long)d); return row is null ? [] : [row];
            }
            ordered = IndexOrder(column, op);
            plan = "IndexRangeSeek(" + table.Columns[column].Name + ")";
            object? high = op == "BETWEEN" ? binding.Bind(args[2], parameters)([]) : null;
            if (column == table.Primary && snapshot.HasPersistentIndexes && !HasMutations && value is long or double &&
                (op != "BETWEEN" || high is long or double))
            {
                plan = "PrimaryKeyRangeSeek";
                return PrimaryRangeRows(table, op, value, high, ordered && order.Count > 0 && order[0].Descending);
            }
            return Data(table).Seek(column, op, value, high, ordered && order.Count > 0 && order[0].Descending);
        }
        var data = Data(table);
        if (orderColumn >= 0 && (orderColumn == table.Primary || table.Indexes.Any(i => i.Expression is null && table.Column(i.Column) == orderColumn) || table.Columns[orderColumn].Unique))
        { ordered = true; plan = "OrderedIndexScan(" + table.Columns[orderColumn].Name + ")"; var sorted = data.Ordered(orderColumn); return order[0].Descending ? sorted.Reverse() : sorted; }
        return data.Rows;
    }
    private IEnumerable<object?[]> PrimaryRangeRows(SqlTable table, string op, object value, object? high, bool descending)
    {
        double bound = value is long n ? n : (double)value;
        double min = op switch { ">=" or "BETWEEN" => Math.Ceiling(bound), ">" => Math.Floor(bound) + 1, _ => 0 };
        double max = op switch { "<=" => Math.Floor(bound), "<" => Math.Ceiling(bound) - 1,
            "BETWEEN" => Math.Floor(high is long h ? h : (double)high!), _ => (1L << 40) - 1 };
        min = Math.Max(0, min); max = Math.Min((1L << 40) - 1, max);
        if (min > max) yield break;
        long prefix = (long)table.Id << 40;
        var entries = snapshot.TableEntriesRange(table.Id, prefix + (long)min, prefix + (long)max);
        if (descending) entries = entries.Reverse();
        foreach (var entry in entries) yield return SqlCodec.Decode(snapshot.IndexValue(entry), table.Columns.Count);
    }
    private static object?[] Combine(object?[] left, object?[] right)
    {
        var row = new object?[left.Length + right.Length]; left.CopyTo(row, 0); right.CopyTo(row, left.Length); return row;
    }
    private static IEnumerable<object?[]> JoinRows(IEnumerable<object?[]> left, SqlTableData right, int oldWidth, string kind,
        Func<object?[], object?>? condition, int leftKey, int rightKey)
    {
        bool hash = leftKey >= 0; var lookup = hash ? right.Hash(rightKey) : null;
        var matched = kind is "RIGHT" or "FULL" ? new HashSet<object?[]>(ReferenceEqualityComparer.Instance) : null;
        foreach (var l in left)
        {
            IEnumerable<object?[]> candidates = right.Rows;
            if (hash) candidates = l[leftKey] is { } key && lookup!.TryGetValue(key, out var found) ? found : [];
            bool any = false;
            foreach (var r in candidates)
            {
                var joined = Combine(l, r);
                if (condition is not null && SqlValueComparer.Truth(condition(joined)) != true) continue;
                any = true; matched?.Add(r); yield return joined;
            }
            if (!any && kind is "LEFT" or "FULL") yield return Combine(l, new object?[right.Schema.Columns.Count]);
        }
        if (matched is not null) foreach (var r in right.Rows) if (!matched.Contains(r)) yield return Combine(new object?[oldWidth], r);
    }
    private static bool Same(Expr a, Expr b) => a.Op == b.Op && Equals(a.Value, b.Value) && a.Args.Length == b.Args.Length && a.Args.Zip(b.Args).All(p => Same(p.First, p.Second));
    private static Func<object?[][], object?> GroupExpression(Expr expr, Select select, SqlBinding binding, IReadOnlyDictionary<string, object?> parameters)
    {
        if (expr.Op is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX")
        {
            if (expr.Args.Length != 1 || expr.Op != "COUNT" && expr.Args[0].Op == "star" || SqlBinding.Aggregate(expr.Args[0])) throw new SqlException("Invalid aggregate");
            if (expr.Args[0].Op == "star") return group => (long)group.Length;
            var get = binding.Bind(expr.Args[0], parameters);
            return group =>
            {
                var values = group.Select(get).Where(v => v is not null).ToArray();
                if (expr.Op == "COUNT") return (long)values.Length;
                if (values.Length == 0) return null;
                if (expr.Op == "MIN") return values.Min(SqlValueComparer.Instance);
                if (expr.Op == "MAX") return values.Max(SqlValueComparer.Instance);
                if (values.Any(v => v is not (long or double))) throw new SqlException("SUM/AVG requires numeric values");
                if (expr.Op == "SUM" && values.All(v => v is long)) { long sum = 0; foreach (long value in values.Cast<long>()) sum = checked(sum + value); return sum; }
                double d = values.Sum(v => Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture)); return expr.Op == "AVG" ? d / values.Length : d;
            };
        }
        if (SameGroup(expr) || expr.Op is "literal" or "parameter")
        { var get = binding.Bind(expr, parameters); return group => get(group.Length > 0 ? group[0] : new object?[binding.Width]); }
        if (expr.Op == "column") throw new SqlException("Nonaggregate columns must appear in GROUP BY");
        var children = expr.Args.Select(e => GroupExpression(e, select, binding, parameters)).ToArray(); SqlBinding.Validate(expr.Op, children.Length);
        return group => SqlBinding.Eval(expr.Op, children.Select(f => f(group)).ToArray());
        bool SameGroup(Expr e) => select.GroupBy.Any(g => Same(e, g));
    }
    private sealed record Output(object?[] Values, object?[] Keys);
    internal SqlResult Query(Select select, IReadOnlyDictionary<string, object?> parameters)
    {
        var first = select.From.Table.Length == 0 ? new SqlTable() : Catalog.Table(select.From.Table);
        if (first.Id != 0 && select.Joins.Count == 0 && select.Where is null && select.GroupBy.Count == 0 && select.Having is null &&
            !select.Distinct && select.Order.Count == 0 && select.Columns.Count == 1 && select.Columns[0].Expr.Op == "COUNT" &&
            select.Columns[0].Expr.Args.Length == 1 && select.Columns[0].Expr.Args[0].Op == "star")
        {
            long count = snapshot.CountKey(first.Id);
            if (_mutations is not null) foreach(var m in _mutations.Values)
            {
                if(m.Id<0 || (m.Id>>40)!=first.Id) continue;
                bool existed = !snapshot.Value(m.Id).IsEmpty;
                if(m.IsDelete && existed) count--; else if(!m.IsDelete && !existed) count++;
            }
            int take=LimitNumber(select.Limit,int.MaxValue),skip=LimitNumber(select.Offset,0);
            return new([select.Columns[0].Alias ?? "count"],take>0 && skip==0 ? [new object?[]{count}] : [],"CountMetadata");
            int LimitNumber(Expr? e,int fallback)
            {
                if(e is null)return fallback;object? v=new SqlBinding().Bind(e,parameters)([]);
                return v is long n && n>=0 && n<=int.MaxValue?(int)n:throw new SqlException("Invalid LIMIT/OFFSET");
            }
        }
        var aggregate=AggregateFast(select,first,parameters)??GroupCountFast(select,first,parameters);
        if(aggregate is not null)return aggregate;
        var primaryPlan = owner.PrimaryPlan(select, first);
        if (primaryPlan.Supported)
        {
            object? key = primaryPlan.Key!.Op == "literal" ? primaryPlan.Key.Value : SqlPrimaryPlan.Parameter(primaryPlan.Key, parameters);
            object?[]? row = null;
            if (SqlPrimaryPredicate.TryId(key, out long id)) row = ProjectRow(first, id, primaryPlan.Columns, primaryPlan.IndexOnly);
            return new(primaryPlan.Names, row is null ? [] : [row], primaryPlan.IndexOnly && snapshot.HasPersistentIndexes ? "PrimaryKeySeek(covering)" : "PrimaryKeySeek");
        }
        var scalarPlan = owner.ScalarPlan(select, first);
        if (scalarPlan.Supported)
        {
            object? key = SqlRangePlan.Value(scalarPlan.Key, parameters); object?[]? row = null;
            if (SqlPrimaryPredicate.TryId(key, out long id)) row = Row(first, id);
            if (row is null) return new(scalarPlan.Names, [], "PrimaryScalarSeek");
            var values = new object?[scalarPlan.Selectors.Length];
            for (int i = 0; i < values.Length; i++) { object? value = scalarPlan.Selectors[i](row); values[i] = value is byte[] b ? b.ToArray() : SqlJsonFunctions.Unwrap(value); }
            return new(scalarPlan.Names, [values], "PrimaryScalarSeek");
        }
        var rangePlan = owner.RangePlan(select, first);
        if (rangePlan.Supported)
        {
            object? low = SqlRangePlan.Value(rangePlan.Low, parameters), high = SqlRangePlan.Value(rangePlan.High, parameters);
            int skip = SqlRangePlan.Count(rangePlan.Offset, parameters, 0), take = SqlRangePlan.Count(rangePlan.Limit, parameters, int.MaxValue);
            if (!HasMutations && owner.DiskRange(snapshot, first, rangePlan, low, high, skip, take) is { } diskRows)
                return new(rangePlan.Names, diskRows, "PersistentIndexRangeSeek(" + first.Columns[rangePlan.Column].Name + ")");
            var result = Data(first).Seek(rangePlan.Column, rangePlan.Op, low, high, rangePlan.Descending).Skip(skip).Take(take)
                .Select(row => rangePlan.Columns.Select(i => row[i] is byte[] bytes ? bytes.ToArray() : SqlJsonFunctions.Unwrap(row[i])).ToArray()).ToArray();
            return new(rangePlan.Names, result, "IndexRangeSeek(" + first.Columns[rangePlan.Column].Name + ")" + (rangePlan.Limit is null ? "" : " -> Limit"));
        }
        if (select.Joins.Count == 1)
        {
            var right = Catalog.Table(select.Joins[0].Source.Table);
            if (owner.JoinPlan(select, first, right) is { Supported: true } joinPlan) return QueryPrimaryJoin(joinPlan, first, right, parameters);
        }
        if (select.Joins.Count == 0 && owner.ExpressionPlan(select, first) is { Supported: true } expressionPlan)
            return QueryExpressionRange(expressionPlan, first, parameters);
        return QueryGeneral(select, first, parameters);
    }
    // Keep the generic interpreter out of the JIT path for indexed reads.
    private SqlResult QueryGeneral(Select select, SqlTable first, IReadOnlyDictionary<string, object?> parameters)
    {
        var binding = new SqlBinding(); binding.Add(first, select.From.Alias);
        bool outerRight = select.Joins.Any(j => j.Kind is "RIGHT" or "FULL");
        bool ordered = false; string plan = "ConstantRow";
        IEnumerable<object?[]> source = select.From.Table.Length == 0 ? [Array.Empty<object?>()] : Candidates(first, select.From.Alias, outerRight ? null : select.Where,
            select.Joins.Count == 0 ? select.Order : [], parameters, out ordered, out plan);
        // Join aliases and ON expressions are bound before enumeration, including on empty inputs.
        foreach (var join in select.Joins)
        {
            var table = Catalog.Table(join.Source.Table); int oldWidth = binding.Width; binding.Add(table, join.Source.Alias);
            var on = join.On is null ? null : binding.Bind(join.On, parameters);
            int leftKey = -1, rightKey = -1;
            foreach (var term in Conjuncts(join.On))
            {
                if (term.Op != "=" || term.Args.Any(a => a.Op != "column")) continue;
                int a = binding.Resolve((string)term.Args[0].Value!), b = binding.Resolve((string)term.Args[1].Value!);
                if (a >= oldWidth && b < oldWidth) (a, b) = (b, a);
                if (a < oldWidth && b >= oldWidth) { leftKey = a; rightKey = b - oldWidth; break; }
            }
            bool declared = rightKey >= 0 && (rightKey == table.Primary || table.Indexes.Any(i => i.Expression is null && table.Column(i.Column) == rightKey) || table.Columns[rightKey].Unique);
            plan += " -> " + (leftKey >= 0 ? declared ? "IndexJoin" : "HashJoin" : "NestedLoopJoin") + "(" + join.Kind + "," + join.Source.Table + ")";
            bool pureEquality = leftKey >= 0 && join.On?.Op == "=";
            bool joinOrdered = select.Joins.Count == 1 && plan.StartsWith("PrimaryKeySeek", StringComparison.Ordinal) && pureEquality && select.Order.Count == 1 && !select.Order[0].Descending && select.Order[0].Expr.Op == "column" && binding.Resolve((string)select.Order[0].Expr.Value!) == oldWidth + table.Primary;
            source = JoinRows(source, Data(table), oldWidth, join.Kind, pureEquality ? null : on, leftKey, rightKey);
            ordered = joinOrdered;
        }
        if (select.Where is not null && !(select.Joins.Count == 0 && Conjuncts(select.Where).Count() == 1 && (plan.StartsWith("IndexRangeSeek", StringComparison.Ordinal) || plan.StartsWith("ExpressionIndexSeek", StringComparison.Ordinal)))) { var filter = binding.Bind(select.Where, parameters); source = source.Where(row => SqlValueComparer.Truth(filter(row)) == true); }
        var projections = new List<Projection>();
        foreach (var p in select.Columns)
        {
            if (p.Expr.Op != "star") { projections.Add(p); continue; }
            if (p.Alias is not null) throw new SqlException("Star projection cannot have an alias");
            var sources = binding.Sources.Where(s => p.Expr.Value is null || s.Alias.Equals((string)p.Expr.Value, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sources.Length == 0) throw new SqlException("Unknown star qualifier");
            foreach (var s in sources) foreach (var c in s.Table.Columns) projections.Add(new(new("column", s.Alias + "." + c.Name), c.Name));
        }
        string[] names = projections.Select((p, i) => p.Alias ?? (p.Expr.Op == "column" ? ((string)p.Expr.Value!).Split('.')[^1] : p.Expr.Op is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX" ? p.Expr.Op.ToLowerInvariant() : "expr" + (i + 1))).ToArray();
        bool grouped = select.GroupBy.Count > 0 || projections.Any(p => SqlBinding.Aggregate(p.Expr)) || select.Having is not null || select.Order.Any(o => SqlBinding.Aggregate(o.Expr));
        int OrderProjection(Expr expr)
        {
            if (expr.Op == "literal" && expr.Value is long ordinal)
            { if (ordinal < 1 || ordinal > projections.Count) throw new SqlException("ORDER BY ordinal out of range"); return (int)ordinal - 1; }
            if (expr.Op == "column" && !((string)expr.Value!).Contains('.')) return Array.FindIndex(names, n => n.Equals((string)expr.Value, StringComparison.OrdinalIgnoreCase));
            return -1;
        }
        IEnumerable<Output> outputs;
        if (grouped)
        {
            var selectors = projections.Select(p => GroupExpression(p.Expr, select, binding, parameters)).ToArray();
            var groupKeys = select.GroupBy.Select(e => binding.Bind(e, parameters)).ToArray();
            var having = select.Having is null ? null : GroupExpression(select.Having, select, binding, parameters);
            var orderFunctions = select.Order.Select(o => OrderProjection(o.Expr) is var index && index >= 0 ? (Func<object?[][], object?>)(g => selectors[index](g)) : GroupExpression(o.Expr, select, binding, parameters)).ToArray();
            IEnumerable<object?[][]> groups = groupKeys.Length == 0 ? [source.ToArray()] : source.GroupBy(row => new RowKey(groupKeys.Select(f => f(row)).ToArray())).Select(g => g.ToArray());
            if (having is not null) groups = groups.Where(g => SqlValueComparer.Truth(having(g)) == true);
            outputs = groups.Select(g => new Output(selectors.Select(f => f(g)).ToArray(), orderFunctions.Select(f => f(g)).ToArray()));
            ordered = false; plan += " -> Aggregate";
        }
        else
        {
            var selectors = projections.Select(p => binding.Bind(p.Expr, parameters)).ToArray();
            var orderFunctions = select.Order.Select(o => OrderProjection(o.Expr) is var index && index >= 0 ? selectors[index] : binding.Bind(o.Expr, parameters)).ToArray();
            // Alias ORDER BY may name a different physical column; only skip sorting on an exact column match.
            if (ordered && select.Order.Any(o => o.Expr.Op == "column" && OrderProjection(o.Expr) is var i && i >= 0 && (projections[i].Expr.Op != "column" || binding.Resolve((string)o.Expr.Value!) != binding.Resolve((string)projections[i].Expr.Value!)))) ordered = false;
            outputs = source.Select(row => new Output(selectors.Select(f => f(row)).ToArray(), ordered ? [] : orderFunctions.Select(f => f(row)).ToArray()));
        }
        if (select.Distinct) { outputs = outputs.DistinctBy(o => new RowKey(o.Values)); plan += " -> Distinct"; }
        if (select.Order.Count > 0 && !ordered)
        {
            IOrderedEnumerable<Output>? sorted = null;
            for (int i = 0; i < select.Order.Count; i++)
            {
                int at = i; bool desc = select.Order[i].Descending;
                sorted = sorted is null ? desc ? outputs.OrderByDescending(o => o.Keys[at], SqlValueComparer.Instance) : outputs.OrderBy(o => o.Keys[at], SqlValueComparer.Instance) : desc ? sorted.ThenByDescending(o => o.Keys[at], SqlValueComparer.Instance) : sorted.ThenBy(o => o.Keys[at], SqlValueComparer.Instance);
            }
            outputs = sorted!; plan += " -> Sort";
        }
        int Number(Expr? expr, int fallback)
        {
            if (expr is null) return fallback;
            object? value = new SqlBinding().Bind(expr, parameters)([]);
            if (value is not long n || n < 0 || n > int.MaxValue) throw new SqlException("LIMIT/OFFSET requires a nonnegative integer <= Int32.MaxValue");
            return (int)n;
        }
        int offset = Number(select.Offset, 0), limit = Number(select.Limit, int.MaxValue);
        var rows = outputs.Skip(offset).Take(limit).Select(o => o.Values.Select(v => v is byte[] b ? b.ToArray() : SqlJsonFunctions.Unwrap(v)).ToArray()).ToArray();
        return new(names, rows, plan + (select.Limit is not null ? " -> Limit" : ""));
    }
}

internal sealed record SqlPrimaryPlan(bool Supported, Expr? Key, int[] Columns, IReadOnlyList<string> Names)
{
    internal bool IndexOnly { get; init; }
    public static object? Parameter(Expr key, IReadOnlyDictionary<string, object?> parameters)
    {
        string name = (string)key.Value!;
        if (!parameters.TryGetValue(name, out var value) && !parameters.TryGetValue(name[1..], out value)) throw new SqlException("Missing parameter " + name);
        return SqlValueComparer.Normalize(value);
    }
    public static SqlPrimaryPlan Build(Select select, SqlTable table)
    {
        SqlPrimaryPlan No() => new(false, null, [], []);
        if (select.Joins.Count > 0 || select.GroupBy.Count > 0 || select.Having is not null || select.Distinct || select.Limit is not null || select.Offset is not null || select.Order.Count > 0 || select.Where?.Op != "=") return No();
        var binding = new SqlBinding(); binding.Add(table, select.From.Alias);
        Expr column = select.Where.Args[0], key = select.Where.Args[1];
        if (column.Op != "column") (column, key) = (key, column);
        if (column.Op != "column" || key.Op is not ("literal" or "parameter") || binding.Resolve((string)column.Value!) != table.Primary) return No();
        var cols = new List<int>(); var names = new List<string>();
        foreach (var projection in select.Columns)
        {
            if (projection.Expr.Op == "star")
            {
                if (projection.Alias is not null) return No();
                if (projection.Expr.Value is string alias && !alias.Equals(select.From.Alias, StringComparison.OrdinalIgnoreCase)) throw new SqlException("Unknown star qualifier");
                for (int i = 0; i < table.Columns.Count; i++) { cols.Add(i); names.Add(table.Columns[i].Name); }
            }
            else if (projection.Expr.Op == "column") { int i = binding.Resolve((string)projection.Expr.Value!); cols.Add(i); names.Add(projection.Alias ?? table.Columns[i].Name); }
            else return No();
        }
        return new(true, key, cols.ToArray(), names.AsReadOnly()) { IndexOnly = cols.All(c => c == table.Primary) };
    }
}
