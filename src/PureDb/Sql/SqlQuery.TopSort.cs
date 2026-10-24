namespace PureDb;
/// <remarks>本部分为小分页的非索引排序提供有界堆，并复用未入选行的临时投影缓冲区。</remarks>
internal sealed partial class SqlSession
{
    /// <summary>纯列排序的候选，引用只读输入行及其稳定序号，不生成逐行投影数组。</summary>
    /// <param name = "Values">当前读取保护期内不可变的输入行。</param>
    /// <param name = "Sequence">输入行原始序号。</param>
    private readonly record struct ColumnSortRow(object?[] Values, long Sequence);
    /// <summary>直接比较输入行的排序列，以最差候选优先出堆。</summary>
    /// <param name = "columns">已经解析为输入行位置的排序列。</param>
    /// <param name = "descending">每列是否按降序排列。</param>
    private sealed class WorstColumnComparer(int[] columns, bool[] descending) : IComparer<ColumnSortRow>
    {
        /// <summary>比较纯列候选，排序键相等时保持原始输入顺序。</summary>
        /// <param name = "x">左侧候选。</param>
        /// <param name = "y">右侧候选。</param>
        /// <returns>用于使最差行位于堆顶的反向排序结果。</returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public int Compare(ColumnSortRow x, ColumnSortRow y)
        {
            for (int i = 0; i < columns.Length; i++)
            {
                int column = columns[i];
                int result = descending[i] ? CompareColumnValue(x.Values[column], y.Values[column]) : CompareColumnValue(y.Values[column], x.Values[column]);
                if (result != 0)
                    return result;
            }

            return y.Sequence.CompareTo(x.Sequence);
        }
    }

    /// <summary>将整数及 NULL 的比较保留为可内联的小方法，其余存储类型复用完整 SQL 比较器。</summary>
    /// <param name = "left">左侧不可变列值。</param>
    /// <param name = "right">右侧不可变列值。</param>
    /// <returns>保持 SQL 排序语义的三向比较结果。</returns>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int CompareColumnValue(object? left, object? right)
    {
        if (left is long a && right is long b)
            return a.CompareTo(b);
        if (left is null)
            return right is null ? 0 : -1;
        if (right is null)
            return 1;
        return SqlValueComparer.Instance.Compare(left, right);
    }

    /// <summary>纯列投影和排序仅保留输入行引用，入选后才物化投影，避免逐行委托和缓冲区写入。</summary>
    /// <param name = "source">连接和过滤完成后产生的不可变输入行。</param>
    /// <param name = "orderColumns">排序键对应的输入行位置，已处理别名遮蔽和列序号。</param>
    /// <param name = "descending">每个排序键的方向。</param>
    /// <param name = "capacity">OFFSET 与 LIMIT 之和，范围为 1 到 4096。</param>
    /// <returns>已按最终 SQL 顺序物化的有限候选集合。</returns>
    private static IEnumerable<Output> TopSortedColumns(IEnumerable<object?[]> source, int[] orderColumns, bool[] descending, int capacity)
    {
        var comparer = new WorstColumnComparer(orderColumns, descending);
        // 候选与优先级使用同一行，不再像 PriorityQueue 那样重复保存一份行和序号。
        var heap = new ColumnSortRow[capacity];
        int count = 0;
        void SiftDown(ColumnSortRow candidate, int position)
        {
            int child;
            while ((child = position * 2 + 1) < count)
            {
                if (child + 1 < count && comparer.Compare(heap[child + 1], heap[child]) < 0)
                    child++;
                if (comparer.Compare(candidate, heap[child]) <= 0)
                    break;
                heap[position] = heap[child];
                position = child;
            }

            heap[position] = candidate;
        }

        long sequence = 0;
        foreach (var row in source)
        {
            var candidate = new ColumnSortRow(row, sequence++);
            if (count < capacity)
            {
                heap[count++] = candidate;
                if (count == capacity)
                    for (int i = count / 2 - 1; i >= 0; i--)
                        SiftDown(heap[i], i);
            }
            else if (comparer.Compare(candidate, heap[0]) > 0)
                SiftDown(candidate, 0);
        }

        if (count < capacity)
            for (int i = count / 2 - 1; i >= 0; i--)
                SiftDown(heap[i], i);
        var result = new Output[count];
        for (int i = result.Length - 1; i >= 0; i--)
        {
            result[i] = new(heap[0].Values, []);
            if (--count > 0)
                SiftDown(heap[count], 0);
        }

        return result;
    }

    /// <summary>堆内保留的排序候选；被淘汰后复用数组，避免为每个输入行分配数组。</summary>
    /// <param name = "values">按投影顺序存放已求值结果的独立缓冲区。</param>
    /// <param name = "keys">按 ORDER BY 顺序存放排序键的独立缓冲区。</param>
    private sealed class SortSlot(object?[] values, object?[] keys)
    {
        /// <summary>当前候选的投影结果；候选留在堆中时禁止修改。</summary>
        internal object?[] Values => values;
        /// <summary>当前候选的排序键；候选留在堆中时禁止修改。</summary>
        internal object?[] Keys => keys;
        /// <summary>原始输入序号，用于使相等排序键保持原有稳定顺序。</summary>
        internal long Sequence
        {
            get; set;
        }
    }

    /// <summary>反向比较 SQL 排序键，使最差候选位于最小堆根部。</summary>
    /// <param name = "descending">每个排序键是否按降序排列。</param>
    private sealed class WorstSortComparer(bool[] descending) : IComparer<SortSlot>
    {
        /// <summary>比较两个候选；负数表示左侧候选应优先被淘汰。</summary>
        /// <param name = "x">左侧候选，堆和结果排序只传入非空实例。</param>
        /// <param name = "y">右侧候选，堆和结果排序只传入非空实例。</param>
        /// <returns>反向 SQL 排序结果；排序键相等时反向比较输入序号。</returns>
        public int Compare(SortSlot? x, SortSlot? y)
        {
            for (int i = 0; i < descending.Length; i++)
            {
                // 交换参数而非取负，避免字符串或 BLOB 比较结果取负溢出。
                int result = descending[i] ? SqlValueComparer.Instance.Compare(x!.Keys[i], y!.Keys[i]) : SqlValueComparer.Instance.Compare(y!.Keys[i], x!.Keys[i]);
                if (result != 0)
                    return result;
            }

            return y!.Sequence.CompareTo(x!.Sequence);
        }
    }

    /// <summary>扫描全部候选，仅保留前 capacity 行；所有投影仍逐行求值以保留函数和异常语义。</summary>
    /// <param name = "source">已经完成连接和 WHERE 过滤的输入行。</param>
    /// <param name = "selectors">按结果列顺序绑定的投影委托。</param>
    /// <param name = "orderFunctions">没有投影别名可复用时执行的排序键委托。</param>
    /// <param name = "orderIndexes">每个排序键引用的投影位置；负数表示独立表达式。</param>
    /// <param name = "descending">每个排序键的方向。</param>
    /// <param name = "capacity">需要保留的 OFFSET 与 LIMIT 之和，必须在 1 到 4096 之间。</param>
    /// <returns>按最终 SQL 顺序排列的候选；调用方继续执行 OFFSET、LIMIT 和结果所有权复制。</returns>
    private static IEnumerable<Output> TopSortedRows(IEnumerable<object?[]> source, Func<object?[], object?>[] selectors, Func<object?[], object?>[] orderFunctions, int[] orderIndexes, bool[] descending, int capacity)
    {
        var comparer = new WorstSortComparer(descending);
        // 按实际候选数逐步增长，不为大 OFFSET 或空表预先申请大数组。
        var heap = new PriorityQueue<SortSlot, SortSlot>(comparer);
        var scratch = new SortSlot(new object?[selectors.Length], new object?[orderFunctions.Length]);
        long sequence = 0;
        foreach (var row in source)
        {
            for (int i = 0; i < selectors.Length; i++)
                scratch.Values[i] = selectors[i](row);
            for (int i = 0; i < orderFunctions.Length; i++)
                scratch.Keys[i] = orderIndexes[i] >= 0 ? scratch.Values[orderIndexes[i]] : orderFunctions[i](row);
            scratch.Sequence = sequence++;
            if (heap.Count < capacity)
            {
                heap.Enqueue(scratch, scratch);
                scratch = new(new object?[selectors.Length], new object?[orderFunctions.Length]);
            }
            else if (comparer.Compare(scratch, heap.Peek()) > 0)
            {
                // 先从堆中移除旧候选，再修改它的缓冲区，保持堆内排序键不可变。
                scratch = heap.DequeueEnqueue(scratch, scratch);
            }
        }

        var result = new Output[heap.Count];
        // 最差者先出堆，倒序填充即可得到稳定的最终顺序，无需再次排序。
        for (int i = result.Length - 1; i >= 0; i--)
        {
            var slot = heap.Dequeue();
            result[i] = new(slot.Values, slot.Keys);
        }

        return result;
    }
}
