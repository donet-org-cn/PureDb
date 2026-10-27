namespace PureDb;
/// <remarks>本部分管理 SQL 持久化范围索引、按需读取和关闭时的检查点。</remarks>
public sealed partial class SqlDatabase
{
    /// <summary>保存原生存储的主索引和可用的 SQL 范围索引；不支持的存储后端会显式报错。</summary>
    /// <param name="cancellationToken">取消请求；已准入写入不会因取消而返回不明确的提交结果。</param>
    /// <returns>完成时表示该操作结束的任务。</returns>
    public async ValueTask CheckpointAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PrepareDiskIndexes(false, cancellationToken);
        await _storage.CheckpointAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>为受支持的非空整数列保存可重建范围索引，按需要跳过未改变的索引。</summary>
    /// <param name="changedOnly">是否仅更新所属表版本已经变化的索引。</param>
    /// <param name="token">取消请求；原子提交开始后需完成提交或回滚。</param>
    private void PrepareDiskIndexes(bool changedOnly, CancellationToken token = default)
    {
        _storage.Read(snapshot =>
        {
            if (!snapshot.HasPersistentIndexes)
                return 0;
            if (changedOnly && snapshot.Length == _initialLength && snapshot.Generation == _initialGeneration)
                return 0;
            foreach (var table in Catalog(snapshot).Tables)
            {
                var columns = table.Indexes.Where(i => i.Expression is null).Select(i => table.Column(i.Column)).Concat(Enumerable.Range(0, table.Columns.Count).Where(c => table.Columns[c].Unique && !table.Columns[c].Primary)).Distinct();
                foreach (int column in columns)
                {
                    token.ThrowIfCancellationRequested();
                    if (table.Columns[column].Type != "INTEGER")
                        continue;
                    DiskRange(snapshot, table, new(true, column, ">=", Expr.Literal(0L), null, [], [], Expr.Literal(0L), null, false), 0L, null, 0, 0);
                }
            }

            return 0;
        });
    }

    private readonly Dictionary<(int Table, int Column), (int Generation, long Length, DiskIndex? Index)> _diskRanges = [];
    /// <summary>在表尚未解码时使用有效的持久化范围索引，直接定位并投影所需记录。</summary>
    /// <param name="snapshot">在当前存储保护期内有效的已提交快照。</param>
    /// <param name="table">当前操作的不可变表模式。</param>
    /// <param name="plan">专用查询计划，或候选行选择返回的计划说明。</param>
    /// <param name="low">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <param name="high">待计算或转换的 SQL 标量；SQL NULL 使用 null。</param>
    /// <param name="skip">跳过的匹配行数量。</param>
    /// <param name="take">最多返回的匹配行数量。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    internal object?[][]? DiskRange(ISqlSnapshot snapshot, SqlTable table, SqlRangePlan plan, object? low, object? high, int skip, int take)
    {
        // 可空列、文本、REAL 和表达式索引继续使用通用查询路径。
        if (!snapshot.HasPersistentIndexes || table.Columns[plan.Column].Type != "INTEGER")
            return null;
        if (plan.Column == table.Primary)
            return PrimaryDiskRange(snapshot, table, plan, low, high, skip, take);
        lock (_cacheGate)
        {
            var key = (table.Id, plan.Column);
            bool fresh = !_diskRanges.TryGetValue(key, out var cached) || cached.Generation != snapshot.Generation || cached.Length != snapshot.Length;
            if (fresh)
            {
                cached.Index?.Dispose();
                string path = snapshot.Path + $".sidx-{table.Id}-{plan.Column}";
                var index = DiskIndex.Open(path, snapshot.Generation, snapshot.Length, snapshot.SourceTime);
                if (index is not null && (index.SourceLength != snapshot.Length || index.Metadata.Length != 8 || System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(index.Metadata) != table.Id || System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(index.Metadata.AsSpan(4)) != plan.Column))
                {
                    index.Dispose();
                    index = null;
                }

                if (index is null)
                {
                    var entries = new DiskIndex.Item[snapshot.CountKey(table.Id)];
                    int at = 0;
                    bool supported = true;
                    int[] projection = [plan.Column];
                    foreach (var item in snapshot.TableEntries(table.Id))
                    {
                        object? value = SqlCodec.DecodeProjection(snapshot.IndexValue(item), table.Columns.Count, projection)[0];
                        if (value is not long number)
                        {
                            supported = false;
                            break;
                        }

                        entries[at++] = item with
                        {
                            Key = number
                        };
                    }

                    if (supported)
                    {
                        if (at != entries.Length)
                            throw new InvalidDataException("Table index row count mismatch.");
                        Array.Sort(entries, (a, b) =>
                        {
                            int n = a.Key.CompareTo(b.Key);
                            return n == 0 ? a.Id.CompareTo(b.Id) : n;
                        });
                        var metadata = new byte[8];
                        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(metadata, table.Id);
                        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(4), plan.Column);
                        DiskIndex.Write(path, snapshot.Generation, snapshot.Length, snapshot.SourceTime, metadata, entries);
                        index = DiskIndex.Open(path, snapshot.Generation, snapshot.Length);
                    }
                }

                _diskRanges[key] = cached = (snapshot.Generation, snapshot.Length, index);
            }

            if (cached.Index is not { } disk)
                return null;
            // 表已经解码时继续使用更快的内存查询路径，避免额外磁盘索引访问。
            if (_tables.TryGetValue(table.Id, out var warm) && warm.Version == snapshot.KeyVersion(table.Id) && warm.SchemaVersion == snapshot.KeyVersion(-1))
                return null;
            if (low is null || plan.Op == "BETWEEN" && high is null || take == 0)
                return [];
            int Bound(object? value, bool upper)
            {
                if (value is long integer)
                    return disk.Bound(integer, upper);
                int lo = 0, hi = disk.Count;
                while (lo < hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    int cmp = SqlValueComparer.Instance.Compare(disk.At(mid).Key, value);
                    if (cmp < 0 || upper && cmp == 0)
                        lo = mid + 1;
                    else
                        hi = mid;
                }

                return lo;
            }

            int Compare(long keyValue, object? value) => value is long n ? keyValue.CompareTo(n) : SqlValueComparer.Instance.Compare(keyValue, value);
            bool hasLow = plan.Op is "=" or ">=" or ">" or "BETWEEN";
            bool hasHigh = plan.Op is "=" or "<=" or "<" or "BETWEEN";
            object? highValue = plan.Op == "BETWEEN" ? high : low;
            int begin, end;
            if (!plan.Descending)
            {
                begin = hasLow ? Bound(low, plan.Op == ">") : 0;
                // LIMIT 足以确定更小的结束位置时，无需搜索整个匹配区间的尾部。
                // 跳过第二次查找前检查最后一个候选，包含 OFFSET 和重复索引键的边界情况。
                int candidate = (int)Math.Min(disk.Count, (long)begin + skip + take);
                int cmp = candidate > begin && hasHigh ? Compare(disk.At(candidate - 1).Key, highValue) : 0;
                end = !hasHigh || candidate > begin && (cmp < 0 || cmp == 0 && plan.Op != "<") ? candidate : Bound(highValue, plan.Op != "<");
            }
            else
            {
                end = hasHigh ? Bound(highValue, plan.Op != "<") : disk.Count;
                int candidate = (int)Math.Max(0, (long)end - skip - take);
                int cmp = candidate < end && hasLow ? Compare(disk.At(candidate).Key, low) : 0;
                begin = !hasLow || candidate < end && (cmp > 0 || cmp == 0 && plan.Op != ">") ? candidate : Bound(low, plan.Op == ">");
            }

            int available = (int)Math.Max(0, (long)end - begin - skip), count = Math.Min(take, available);
            var result = new object?[count][];
            for (int i = 0; i < count; i++)
            {
                int ordinal = plan.Descending ? end - 1 - skip - i : begin + skip + i;
                var entry = disk.At(ordinal);
                var bytes = snapshot.IndexValue(entry);
                result[i] = SqlCodec.DecodeProjection(bytes, table.Columns.Count, plan.Columns);
            }

            return result;
        }
    }

    /// <summary>直接复用原生主索引读取主键范围，不扫描全表或生成重复的二级索引。</summary>
    /// <param name="snapshot">当前提交版本的受保护快照。</param>
    /// <param name="table">当前表模式。</param>
    /// <param name="plan">已验证的范围、排序和投影计划。</param>
    /// <param name="low">比较值或 BETWEEN 下界。</param>
    /// <param name="high">BETWEEN 上界。</param>
    /// <param name="skip">跳过的匹配记录数。</param>
    /// <param name="take">最多返回的记录数。</param>
    /// <returns>独立的结果行；非数值边界交给通用路径处理。</returns>
    private static object?[][]? PrimaryDiskRange(ISqlSnapshot snapshot, SqlTable table, SqlRangePlan plan, object? low, object? high, int skip, int take)
    {
        if (take == 0 || low is null || plan.Op == "BETWEEN" && high is null)
            return [];
        if (low is not (long or double) || plan.Op == "BETWEEN" && high is not (long or double))
            return null;
        double bound = low is long integer ? integer : (double)low;
        double min = plan.Op switch
        {
            "=" or ">=" or "BETWEEN" => Math.Ceiling(bound),
            ">" => Math.Floor(bound) + 1,
            _ => 0
        };
        double max = plan.Op switch
        {
            "=" or "<=" => Math.Floor(bound),
            "<" => Math.Ceiling(bound) - 1,
            "BETWEEN" => Math.Floor(high is long n ? n : (double)high!),
            _ => (1L << 40) - 1
        };
        min = Math.Max(0, min);
        max = Math.Min((1L << 40) - 1, max);
        if (min > max)
            return [];
        long prefix = (long)table.Id << 40;
        bool covering = true;
        foreach (int column in plan.Columns)
            covering &= column == table.Primary;
        if (snapshot is Database.Snapshot native && native.UnchangedPrimaryIndex is { } index &&
            PrimaryBaseRange(snapshot, index, table, plan, prefix + (long)min, prefix + (long)max, skip, take, covering) is { } direct)
            return direct;
        var result = new List<object?[]>(Math.Min(take, 64));
        foreach (var entry in snapshot.TableEntriesRange(table.Id, prefix + (long)min, prefix + (long)max, plan.Descending))
        {
            if (skip > 0)
            {
                skip--;
                continue;
            }
            if (covering)
            {
                object id = SqlIntegerBoxes.Get(entry.Id - prefix);
                var row = new object?[plan.Columns.Length];
                Array.Fill(row, id);
                result.Add(row);
            }
            else
                result.Add(SqlCodec.DecodeProjection(snapshot.IndexValue(entry), table.Columns.Count, plan.Columns));
            if (result.Count == take)
                break;
        }
        return result.ToArray();
    }

    /// <summary>从无增量的不可变主索引直接投影分页，避免多层迭代器和中间 List 数组。</summary>
    /// <param name="snapshot">当前受读锁保护的快照。</param>
    /// <param name="index">与快照完全对应、没有被尾部覆盖的主索引。</param>
    /// <param name="table">已验证的 SQL 表模式。</param>
    /// <param name="plan">主键范围与投影计划。</param>
    /// <param name="min">包含的原生记录 ID 下界。</param>
    /// <param name="max">包含的原生记录 ID 上界。</param>
    /// <param name="skip">需要跳过的记录数。</param>
    /// <param name="take">最多投影的记录数。</param>
    /// <param name="covering">投影是否只包含主键。</param>
    /// <returns>独立的结果数组；键空间不符合 SQL 布局时回退原有过滤路径。</returns>
    private static object?[][]? PrimaryBaseRange(ISqlSnapshot snapshot, DiskIndex index, SqlTable table, SqlRangePlan plan, long min, long max, int skip, int take, bool covering)
    {
        int begin = index.Bound(min, false), end = index.Bound(max, true);
        if (plan.Descending)
        {
            end = (int)Math.Max(begin, (long)end - skip);
            begin = (int)Math.Max(begin, (long)end - take);
        }
        else
        {
            begin = (int)Math.Min(end, (long)begin + skip);
            end = (int)Math.Min(end, (long)begin + take);
        }
        var result = new object?[end - begin][];
        long prefix = (long)table.Id << 40;
        int at = 0;
        foreach (var entry in index.Range(begin, end, plan.Descending))
        {
            if (entry.Id != table.Id)
                return null;
            if (covering)
            {
                var row = new object?[plan.Columns.Length];
                Array.Fill(row, SqlIntegerBoxes.Get(entry.Key - prefix));
                result[at++] = row;
            }
            else
                result[at++] = SqlCodec.DecodeProjection(snapshot.IndexValue(entry), table.Columns.Count, plan.Columns);
        }
        return result;
    }

    /// <summary>释放本实例打开的持久化 SQL 范围索引。</summary>
    private void CloseDiskRanges()
    {
        lock (_cacheGate)
        {
            foreach (var item in _diskRanges.Values)
                item.Index?.Dispose();
            _diskRanges.Clear();
        }
    }
}
