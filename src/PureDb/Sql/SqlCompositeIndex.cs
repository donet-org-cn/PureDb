using System.Collections.Concurrent;

namespace PureDb;
/// <summary>复合唯一键，按 SQL 标量相等语义比较，NULL 在构造前被排除。</summary>
/// <param name = "values">按索引列序排列的不可变键值。</param>
internal sealed class SqlCompositeKey(object?[] values) : IComparable<SqlCompositeKey>
{
    /// <summary>比较两个复合键，数值和 BLOB 比较复用 SQL 语义。</summary>
    /// <param name = "other">右侧复合键。</param>
    /// <returns>逐列比较结果，字段数不等时按长度比较。</returns>
    public int CompareTo(SqlCompositeKey? other)
    {
        if (other is null)
            return 1;
        for (int i = 0; i < Math.Min(values.Length, other.Values.Length); i++)
        {
            int result = SqlValueComparer.Instance.Compare(values[i], other.Values[i]);
            if (result != 0)
                return result;
        }

        return values.Length.CompareTo(other.Values.Length);
    }

    /// <summary>构造后不再修改的复合键值。</summary>
    private object?[] Values => values;

    /// <summary>合并与 SQL 标量相等规则一致的哈希。</summary>
    /// <returns>完整复合键的哈希值。</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in values)
            hash.Add(SqlValueComparer.Instance.GetHashCode(value));
        return hash.ToHashCode();
    }

    /// <summary>判断是否为相同列值的复合键。</summary>
    /// <param name = "other">待比较对象。</param>
    /// <returns>键值逐列相等时为 true。</returns>
    public override bool Equals(object? other) => other is SqlCompositeKey key && CompareTo(key) == 0;
}

/// <remarks>本部分维护随表版本失效的复合有序索引。</remarks>
internal sealed partial class SqlTableData
{
    private ConcurrentDictionary<string, (object?[][] Rows, object?[][] Keys)>? _composites;
    /// <summary>为复合索引生成一次有序键数组，包含显式方向及主键平局顺序。</summary>
    /// <param name = "index">持久化复合索引定义。</param>
    /// <returns>排序后的原始行引用和对应复合键。</returns>
    internal (object?[][] Rows, object?[][] Keys) Composite(SqlIndex index)
    {
        var cache = LazyInitializer.EnsureInitialized(ref _composites, static () => new());
        return cache.GetOrAdd(index.Name, _ =>
        {
            var binding = new SqlBinding();
            binding.Add(Schema, Schema.Name);
            var getters = index.Expression!.Args.Select(e => binding.Bind(e, SqlDatabase.EmptyParameters)).ToArray();
            var entries = Rows.Select(row => (Row: row, Keys: getters.Select(f => f(row)).ToArray())).ToArray();
            Array.Sort(entries, (a, b) =>
            {
                for (int i = 0; i < getters.Length; i++)
                {
                    int c = index.Descending?[i] == true ? SqlValueComparer.Instance.Compare(b.Keys[i], a.Keys[i]) : SqlValueComparer.Instance.Compare(a.Keys[i], b.Keys[i]);
                    if (c != 0)
                        return c;
                }

                return ((long)a.Row[Schema.Primary]!).CompareTo((long)b.Row[Schema.Primary]!);
            });
            return (entries.Select(e => e.Row).ToArray(), entries.Select(e => e.Keys).ToArray());
        });
    }
}

/// <remarks>本部分选择复合索引的左前缀、范围边界和排序扫描。</remarks>
internal sealed partial class SqlSession
{
    /// <summary>尝试用复合索引完成左前缀和下一列范围查找，并报告结果是否满足 ORDER BY。</summary>
    /// <param name = "table">目标表。</param>
    /// <param name = "alias">当前表别名。</param>
    /// <param name = "where">完整过滤条件。</param>
    /// <param name = "order">最终排序要求。</param>
    /// <param name = "parameters">参数值。</param>
    /// <param name = "ordered">返回是否无需额外排序。</param>
    /// <param name = "plan">返回选用的复合索引名称。</param>
    /// <returns>命中时返回有界候选行，未命中返回 null。</returns>
    private IEnumerable<object?[]>? CompositeCandidates(SqlTable table, string alias, Expr? where, List<Ordering> order, IReadOnlyDictionary<string, object?> parameters, out bool ordered, out string plan)
    {
        ordered = false;
        plan = "";
        if (!table.Indexes.Any(i => i.Expression?.Op == "composite"))
            return null;
        var terms = Conjuncts(where).ToArray();
        var constants = new SqlBinding(owner, this);
        foreach (var index in table.Indexes.Where(i => i.Expression?.Op == "composite"))
        {
            var fields = index.Expression!.Args;
            bool Local(Expr expression) => expression.Op == "column" ? OwnColumn(expression, alias) is not null : expression.Args.All(Local);
            bool Matches(Expr a, Expr b) => Local(a) && SqlAst.Equal(a, b);
            var prefix = new List<object?>();
            for (int i = 0; i < fields.Length; i++)
            {
                var equal = terms.FirstOrDefault(t => t.Op == "=" && t.Args.Length == 2 && Matches(t.Args[0], fields[i]) && Constant(t.Args[1]));
                if (equal is null)
                    break;
                prefix.Add(constants.Bind(equal.Args[1], parameters)([]));
            }

            int rangeColumn = prefix.Count;
            var ranges = rangeColumn >= fields.Length ? [] : terms.Where(t => t.Op is ">" or ">=" or "<" or "<=" or "BETWEEN" && t.Args.Length >= 2 && Matches(t.Args[0], fields[rangeColumn]) && t.Args.Skip(1).All(Constant)).ToArray();
            bool orderMatch = false, reverse = false;
            foreach (int start in new[]
            {
                0,
                prefix.Count
            }

            )
            {
                if (order.Count == 0)
                    break;
                bool? direction = null;
                bool match = true;
                for (int i = 0; i < order.Count; i++)
                {
                    int column = start + i;
                    Expr field = column < fields.Length ? fields[column] : new("column", table.Columns[table.Primary].Name);
                    if (column > fields.Length || !Matches(order[i].Expr, field))
                    {
                        match = false;
                        break;
                    }

                    bool change = order[i].Descending != (column < fields.Length && index.Descending?[column] == true);
                    if (direction is not null && change != direction)
                    {
                        match = false;
                        break;
                    }

                    direction = change;
                }

                if (match)
                {
                    orderMatch = true;
                    reverse = direction == true;
                    break;
                }
            }

            if (prefix.Count == 0 && ranges.Length == 0 && !orderMatch)
                continue;
            var data = Data(table).Composite(index);
            int ComparePrefix(object?[] key)
            {
                for (int i = 0; i < prefix.Count; i++)
                {
                    int c = index.Descending?[i] == true ? SqlValueComparer.Instance.Compare(prefix[i], key[i]) : SqlValueComparer.Instance.Compare(key[i], prefix[i]);
                    if (c != 0)
                        return c;
                }

                return 0;
            }

            int PrefixBound(bool upper)
            {
                int low = 0, high = data.Keys.Length;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    int c = ComparePrefix(data.Keys[middle]);
                    if (c < 0 || upper && c == 0)
                        low = middle + 1;
                    else
                        high = middle;
                }

                return low;
            }

            int begin = PrefixBound(false), end = PrefixBound(true);
            int Bound(object? value, bool upper)
            {
                int low = begin, high = end;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    int c = index.Descending?[rangeColumn] == true ? SqlValueComparer.Instance.Compare(value, data.Keys[middle][rangeColumn]) : SqlValueComparer.Instance.Compare(data.Keys[middle][rangeColumn], value);
                    if (c < 0 || upper && c == 0)
                        low = middle + 1;
                    else
                        high = middle;
                }

                return low;
            }

            void Restrict(string op, object? value)
            {
                if (value is null)
                {
                    end = begin;
                    return;
                }

                bool desc = index.Descending?[rangeColumn] == true;
                if (op == ">=")
                {
                    if (desc)
                        end = Bound(value, true);
                    else
                        begin = Bound(value, false);
                }

                if (op == ">")
                {
                    if (desc)
                        end = Bound(value, false);
                    else
                        begin = Bound(value, true);
                }

                if (op == "<=")
                {
                    if (desc)
                        begin = Bound(value, false);
                    else
                        end = Bound(value, true);
                }

                if (op == "<")
                {
                    if (desc)
                        begin = Bound(value, true);
                    else
                        end = Bound(value, false);
                }
            }

            foreach (var range in ranges)
            {
                object? low = constants.Bind(range.Args[1], parameters)([]);
                Restrict(range.Op == "BETWEEN" ? ">=" : range.Op, low);
                if (range.Op == "BETWEEN")
                    Restrict("<=", constants.Bind(range.Args[2], parameters)([]));
            }

            ordered = orderMatch;
            plan = "CompositeIndexSeek(" + index.Name + ")";
            if (prefix.Any(v => v is null))
                return [];
            return Enumerate();
            IEnumerable<object?[]> Enumerate()
            {
                if (reverse)
                {
                    for (int i = end - 1; i >= begin; i--)
                        yield return data.Rows[i];
                }
                else
                    for (int i = begin; i < end; i++)
                        yield return data.Rows[i];
            }
        }

        return null;
    }
}
