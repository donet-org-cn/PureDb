namespace PureDb;
/// <remarks>本部分计算分区窗口，排名使用同值组，数值聚合使用前缀和避免逐帧重扫。</remarks>
internal sealed partial class SqlSession
{
    /// <summary>返回已经在当前查询阶段计算的窗口值。</summary>
    /// <param name = "expression">当前窗口函数调用。</param>
    /// <returns>按当前输入行读取结果的委托。</returns>
    internal Func<object?[], object?> BindWindow(Expr expression)
    {
        if (_windowValues is null || !_windowValues.TryGetValue(expression, out var values))
            throw new SqlException("Window function is only valid in SELECT or ORDER BY");
        return row => values[row];
    }

    /// <summary>枚举本层表达式中的窗口函数，不进入子查询。</summary>
    /// <param name = "expression">待分析的表达式。</param>
    /// <returns>按出现顺序排列的窗口调用。</returns>
    private static IEnumerable<Expr> WindowExpressions(Expr expression)
    {
        if (expression.Window is not null)
        {
            yield return expression;
            yield break;
        }

        foreach (var child in expression.Args)
            foreach (var window in WindowExpressions(child))
                yield return window;
    }

    /// <summary>解析命名窗口及继承，防止循环定义和覆盖已有分区或排序。</summary>
    /// <param name = "window">当前窗口定义。</param>
    /// <param name = "select">包含命名 WINDOW 子句的 SELECT。</param>
    /// <param name = "depth">继承深度。</param>
    /// <returns>合并后的完整窗口定义。</returns>
    private static SqlWindow ResolveWindow(SqlWindow window, Select select, int depth = 0)
    {
        if (window.Base is null)
            return window;
        if (depth > 32 || !select.Windows.TryGetValue(window.Base, out var parent))
            throw new SqlException("Unknown or cyclic named window");
        parent = ResolveWindow(parent, select, depth + 1);
        if (window.Partition.Count > 0 && parent.Partition.Count > 0 || window.Order.Count > 0 && parent.Order.Count > 0 || window.Frame is not null && parent.Frame is not null)
            throw new SqlException("Cannot override a named window clause");
        return window with
        {
            Base = null,
            Partition = window.Partition.Count > 0 ? window.Partition : parent.Partition,
            Order = window.Order.Count > 0 ? window.Order : parent.Order,
            Frame = window.Frame ?? parent.Frame,
            Start = window.Start ?? parent.Start,
            End = window.End ?? parent.End,
            Exclude = window.Frame is null ? parent.Exclude : window.Exclude
        };
    }

    /// <summary>在过滤后为当前输入行计算所有窗口，排名和帧在最终 ORDER BY/LIMIT 之前执行。</summary>
    /// <param name = "select">当前查询。</param>
    /// <param name = "binding">已经绑定所有数据源的列映射。</param>
    /// <param name = "source">WHERE 过滤后的输入。</param>
    /// <param name = "parameters">本次参数。</param>
    /// <returns>具有独立行身份的窗口输入；无窗口时原样返回。</returns>
    private IEnumerable<object?[]> PrepareWindows(Select select, SqlBinding binding, IEnumerable<object?[]> source, IReadOnlyDictionary<string, object?> parameters)
    {
        if (!select.HasWindows)
            return source;
        var windows = select.Columns.SelectMany(p => WindowExpressions(p.Expr)).Concat(select.Order.SelectMany(o => WindowExpressions(o.Expr))).Distinct().ToArray();
        if (windows.Length == 0)
            return source;
        if (select.GroupBy.Count > 0 || select.Having is not null || select.Columns.Any(p => SqlBinding.Aggregate(p.Expr)))
            throw new SqlException("Window functions combined with GROUP BY require a derived-table query");
        var rows = source.Select(r => r.ToArray()).ToArray();
        _windowValues = [];
        foreach (var expression in windows)
        {
            var spec = ResolveWindow(expression.Window!, select);
            var partitions = spec.Partition.Select(e => binding.Bind(e, parameters)).ToArray();
            var orders = spec.Order.Select(o => binding.Bind(o.Expr, parameters)).ToArray();
            var arguments = expression.Args.Where(a => a.Op != "star").Select(e => binding.Bind(e, parameters)).ToArray();
            var output = new Dictionary<object?[], object?>(ReferenceEqualityComparer.Instance);
            if (rows.Length == 0)
                _ = WindowPartition(expression, spec, [], arguments, [], [], [], parameters);
            foreach (var group in rows.GroupBy(r => new RowKey(partitions.Select(f => f(r)).ToArray())))
            {
                var items = group.Select((row, sequence) => (Row: row, Sequence: sequence, Keys: orders.Select(f => f(row)).ToArray())).ToArray();
                Array.Sort(items, (a, b) =>
                {
                    for (int i = 0; i < spec.Order.Count; i++)
                    {
                        int c = spec.Order[i].Descending ? SqlValueComparer.Instance.Compare(b.Keys[i], a.Keys[i]) : SqlValueComparer.Instance.Compare(a.Keys[i], b.Keys[i]);
                        if (c != 0)
                            return c;
                    }

                    return a.Sequence.CompareTo(b.Sequence);
                });
                var orderedRows = items.Select(i => i.Row).ToArray();
                var peerStart = new int[items.Length];
                var peerEnd = new int[items.Length];
                var groupNumber = new int[items.Length];
                int number = 0;
                for (int i = 0; i < items.Length;)
                {
                    int end = i + 1;
                    while (end < items.Length && items[i].Keys.SequenceEqual(items[end].Keys, SqlValueComparer.Instance))
                        end++;
                    for (int p = i; p < end; p++)
                    {
                        peerStart[p] = i;
                        peerEnd[p] = end - 1;
                        groupNumber[p] = number;
                    }

                    number++;
                    i = end;
                }

                var results = WindowPartition(expression, spec, orderedRows, arguments, peerStart, peerEnd, groupNumber, parameters, spec.Order.Count == 1 ? items.Select(item => item.Keys[0]).ToArray() : []);
                for (int i = 0; i < results.Length; i++)
                    output[orderedRows[i]] = results[i];
            }

            _windowValues[expression] = output;
        }

        return rows;
    }

    /// <summary>将窗口偏移绑定为非负整数；不允许引用当前输入行。</summary>
    /// <param name = "expression">边界偏移或窗口参数。</param>
    /// <param name = "parameters">本次参数。</param>
    /// <returns>合法的非负整数。</returns>
    private long WindowOffset(Expr expression, IReadOnlyDictionary<string, object?> parameters)
    {
        object? value = new SqlBinding(owner, this).Bind(expression, parameters)([]);
        return value is long n && n >= 0 ? n : throw new SqlException("Window frame offset requires a nonnegative integer");
    }

    /// <summary>对一个已经排序的分区计算排名、偏移、取值和聚合窗口。</summary>
    /// <param name = "expression">窗口函数表达式。</param>
    /// <param name = "spec">完整窗口规则。</param>
    /// <param name = "rows">按窗口顺序排列的分区行。</param>
    /// <param name = "arguments">参数逐行委托。</param>
    /// <param name = "peerStart">每行同值组的起始位置。</param>
    /// <param name = "peerEnd">每行同值组的结束位置。</param>
    /// <param name = "groupNumber">每行的同值组序号。</param>
    /// <param name = "parameters">本次查询参数。</param>
    /// <param name = "rangeKeys">单一 ORDER BY 的排序值，用于数值 RANGE 边界。</param>
    /// <returns>按分区行序排列的窗口结果。</returns>
    private object?[] WindowPartition(Expr expression, SqlWindow spec, object?[][] rows, Func<object?[], object?>[] arguments, int[] peerStart, int[] peerEnd, int[] groupNumber, IReadOnlyDictionary<string, object?> parameters, object?[]? rangeKeys = null)
    {
        string op = expression.Op;
        int arity = expression.Args.Length;
        bool aggregate = op is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX";
        bool valid = op switch
        {
            "ROW_NUMBER" or "RANK" or "DENSE_RANK" or "PERCENT_RANK" or "CUME_DIST" => arity == 0,
            "NTILE" or "FIRST_VALUE" or "LAST_VALUE" or "SUM" or "AVG" or "MIN" or "MAX" or "COUNT" => arity == 1,
            "NTH_VALUE" => arity == 2,
            "LAG" or "LEAD" => arity is >= 1 and <= 3,
            _ => false
        };
        if (!valid)
            throw new SqlException("Unsupported window function or arity: " + op);
        int count = rows.Length;
        var values = arguments.Length == 0 ? new object?[count] : rows.Select(r => arguments[0](r)).ToArray();
        var output = new object?[count];
        string frame = spec.Frame ?? "RANGE";
        var start = spec.Start ?? new SqlWindowBound("UNBOUNDED PRECEDING");
        var end = spec.End ?? new SqlWindowBound(spec.Order.Count > 0 ? "CURRENT ROW" : "UNBOUNDED FOLLOWING");
        bool numericRange = frame == "RANGE" && (start.Offset is not null || end.Offset is not null);
        if (numericRange && spec.Order.Count != 1)
            throw new SqlException("RANGE offsets require exactly one ORDER BY expression");
        object RangeOffset(Expr? expr)
        {
            object? value = expr is null ? 0L : new SqlBinding(owner, this).Bind(expr, parameters)([]);
            return value is long n && n >= 0 ? value : value is double d && d >= 0 && double.IsFinite(d) ? value : throw new SqlException("RANGE offset requires a nonnegative number");
        }

        object rangeStart = numericRange ? RangeOffset(start.Offset) : 0L, rangeEnd = numericRange ? RangeOffset(end.Offset) : 0L;
        long startOffset = start.Offset is null || numericRange ? 0 : WindowOffset(start.Offset, parameters);
        long endOffset = end.Offset is null || numericRange ? 0 : WindowOffset(end.Offset, parameters);
        int numericStart = 0, numericEnd = count;
        if (numericRange)
        {
            while (numericStart < count && rangeKeys![numericStart] is not (long or double))
                numericStart++;
            numericEnd = numericStart;
            while (numericEnd < count && rangeKeys![numericEnd] is long or double)
                numericEnd++;
        }

        static int BoundRank(string kind) => kind switch
        {
            "UNBOUNDED PRECEDING" => 0,
            "PRECEDING" => 1,
            "CURRENT ROW" => 2,
            "FOLLOWING" => 3,
            _ => 4
        };
        if (start.Kind == "UNBOUNDED FOLLOWING" || end.Kind == "UNBOUNDED PRECEDING" || BoundRank(start.Kind) > BoundRank(end.Kind))
            throw new SqlException("Invalid window frame boundaries");
        var starts = Enumerable.Range(0, count).Where(i => i == peerStart[i]).ToArray();
        var ends = Enumerable.Range(0, count).Where(i => i == peerEnd[i]).ToArray();
        var nonnull = aggregate ? new int[count + 1] : [];
        var realCount = aggregate ? new int[count + 1] : [];
        var integers = op is "SUM" or "AVG" ? new Int128[count + 1] : [];
        var reals = op is "SUM" or "AVG" ? new double[count + 1] : [];
        int treeSize = 1;
        while (treeSize < count)
            treeSize *= 2;
        var tree = op is "MIN" or "MAX" ? new object?[treeSize * 2] : [];
        object? Best(object? a, object? b) => a is null ? b : b is null ? a : (SqlValueComparer.Instance.Compare(a, b) < 0) == (op == "MIN") ? a : b;
        if (aggregate)
        {
            for (int i = 0; i < count; i++)
            {
                nonnull[i + 1] = nonnull[i] + (expression.Args[0].Op == "star" || values[i] is not null ? 1 : 0);
                realCount[i + 1] = realCount[i] + (values[i] is double ? 1 : 0);
                if (integers.Length > 0)
                {
                    if (values[i] is not (null or long or double))
                        throw new SqlException("Window SUM/AVG requires numeric values");
                    integers[i + 1] = integers[i] + (values[i] is long n ? n : 0);
                    reals[i + 1] = reals[i] + (values[i] is null ? 0 : Convert.ToDouble(values[i], System.Globalization.CultureInfo.InvariantCulture));
                }

                if (tree.Length > 0)
                    tree[treeSize + i] = values[i];
            }

            if (tree.Length > 0)
                for (int i = treeSize - 1; i > 0; i--)
                    tree[i] = Best(tree[i * 2], tree[i * 2 + 1]);
        }

        int Boundary(SqlWindowBound bound, long offset, int position, bool upper)
        {
            if (bound.Kind == "UNBOUNDED PRECEDING")
                return 0;
            if (bound.Kind == "UNBOUNDED FOLLOWING")
                return count - 1;
            if (frame == "RANGE")
            {
                if (bound.Offset is null || rangeKeys![position] is not (long or double))
                    return upper ? peerEnd[position] : peerStart[position];
                object center = rangeKeys[position]!;
                object distance = upper ? rangeEnd : rangeStart;
                int sign = (bound.Kind == "PRECEDING" ? -1 : 1) * (spec.Order[0].Descending ? -1 : 1);
                object target;
                if (center is long integer && distance is long step)
                {
                    Int128 exact = (Int128)integer + sign * (Int128)step;
                    target = exact >= long.MinValue && exact <= long.MaxValue ? (object)(long)exact : (double)exact;
                }
                else
                    target = Convert.ToDouble(center, System.Globalization.CultureInfo.InvariantCulture) + sign * Convert.ToDouble(distance, System.Globalization.CultureInfo.InvariantCulture);
                int low = numericStart, high = numericEnd;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    int compare = spec.Order[0].Descending ? SqlValueComparer.Instance.Compare(target, rangeKeys[middle]) : SqlValueComparer.Instance.Compare(rangeKeys[middle], target);
                    if (compare < 0 || upper && compare == 0)
                        low = middle + 1;
                    else
                        high = middle;
                }

                return upper ? low - 1 : low;
            }

            long basis = frame == "GROUPS" ? groupNumber[position] : position;
            long at = bound.Kind == "PRECEDING" ? basis - Math.Min(offset, int.MaxValue) : bound.Kind == "FOLLOWING" ? basis + Math.Min(offset, int.MaxValue) : basis;
            if (frame == "GROUPS")
                return at < 0 ? upper ? -1 : 0 : at >= starts.Length ? upper ? count - 1 : count : upper ? ends[(int)at] : starts[(int)at];
            return (int)Math.Clamp(at, upper ? -1 : 0, upper ? count - 1 : count);
        }

        Span<(int Low, int High)> ranges = stackalloc (int, int)[3];
        for (int i = 0; i < count; i++)
        {
            if (op == "ROW_NUMBER")
            {
                output[i] = (long)i + 1;
                continue;
            }

            if (op == "RANK")
            {
                output[i] = (long)peerStart[i] + 1;
                continue;
            }

            if (op == "DENSE_RANK")
            {
                output[i] = (long)groupNumber[i] + 1;
                continue;
            }

            if (op == "PERCENT_RANK")
            {
                output[i] = count <= 1 ? 0d : (double)peerStart[i] / (count - 1);
                continue;
            }

            if (op == "CUME_DIST")
            {
                output[i] = (double)(peerEnd[i] + 1) / count;
                continue;
            }

            object? extra = arguments.Length > 1 ? arguments[1](rows[i]) : null;
            if (op == "NTILE")
            {
                if (values[i] is not long buckets || buckets <= 0)
                    throw new SqlException("NTILE requires a positive integer");
                long size = count / buckets, remainder = count % buckets, cutoff = (size + 1) * remainder;
                output[i] = i < cutoff ? i / (size + 1) + 1 : remainder + (i - cutoff) / size + 1;
                continue;
            }

            if (op is "LAG" or "LEAD")
            {
                long offset = arguments.Length < 2 ? 1 : extra is long n ? n : throw new SqlException("LAG/LEAD offset requires INTEGER");
                long target = (long)i + (op == "LEAD" ? Math.Clamp(offset, -int.MaxValue, int.MaxValue) : -Math.Clamp(offset, -int.MaxValue, int.MaxValue));
                object? fallback = arguments.Length > 2 ? arguments[2](rows[i]) : null;
                output[i] = target < 0 || target >= count ? fallback : values[(int)target];
                continue;
            }

            int low = Boundary(start, startOffset, i, false), high = Boundary(end, endOffset, i, true);
            int segments = WindowRanges(low, high, i, peerStart[i], peerEnd[i], spec.Exclude, ranges);
            if (!aggregate)
            {
                long nth = op == "NTH_VALUE" ? extra is long n && n > 0 ? n - 1 : throw new SqlException("NTH_VALUE requires a positive integer") : 0;
                if (op == "LAST_VALUE")
                {
                    nth = -1;
                    for (int s = 0; s < segments; s++)
                        nth += ranges[s].High - ranges[s].Low + 1;
                }

                for (int s = 0; s < segments && nth >= 0; s++)
                {
                    int length = ranges[s].High - ranges[s].Low + 1;
                    if (nth < length)
                    {
                        output[i] = values[ranges[s].Low + (int)nth];
                        break;
                    }

                    nth -= length;
                }

                continue;
            }

            int present = 0, doubles = 0;
            Int128 sum = 0;
            double realSum = 0;
            object? best = null;
            for (int s = 0; s < segments; s++)
            {
                int a = ranges[s].Low, b = ranges[s].High + 1;
                present += nonnull[b] - nonnull[a];
                doubles += realCount[b] - realCount[a];
                if (integers.Length > 0)
                {
                    sum += integers[b] - integers[a];
                    realSum += reals[b] - reals[a];
                }

                if (tree.Length > 0)
                    for (int l = a + treeSize, r = b + treeSize; l < r; l /= 2, r /= 2)
                    {
                        if ((l & 1) != 0)
                            best = Best(best, tree[l++]);
                        if ((r & 1) != 0)
                            best = Best(best, tree[--r]);
                    }
            }

            output[i] = op == "COUNT" ? (object)(long)present : present == 0 ? null : op is "MIN" or "MAX" ? best : op == "AVG" ? realSum / present : doubles > 0 ? realSum : sum < long.MinValue || sum > long.MaxValue ? throw new SqlException("Integer SUM overflow") : (object)(long)sum;
        }

        return output;
    }

    /// <summary>把 EXCLUDE 规则化为最多三段连续帧，避免为每行生成帧列表。</summary>
    /// <param name = "low">帧起点。</param>
    /// <param name = "high">帧终点。</param>
    /// <param name = "position">当前行位置。</param>
    /// <param name = "peerStart">同值组起点。</param>
    /// <param name = "peerEnd">同值组终点。</param>
    /// <param name = "exclude">排除规则。</param>
    /// <param name = "ranges">由调用者复用的三段缓冲区。</param>
    /// <returns>有效帧段数量。</returns>
    private static int WindowRanges(int low, int high, int position, int peerStart, int peerEnd, string exclude, Span<(int Low, int High)> ranges)
    {
        if (low > high)
            return 0;
        if (exclude == "NO OTHERS")
        {
            ranges[0] = (low, high);
            return 1;
        }

        int start = exclude == "CURRENT ROW" ? position : peerStart;
        int end = exclude == "CURRENT ROW" ? position : peerEnd;
        int count = 0;
        if (low <= Math.Min(high, start - 1))
            ranges[count++] = (low, Math.Min(high, start - 1));
        if (exclude == "TIES" && position >= low && position <= high)
            ranges[count++] = (position, position);
        if (Math.Max(low, end + 1) <= high)
            ranges[count++] = (Math.Max(low, end + 1), high);
        return count;
    }
}
