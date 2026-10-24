namespace PureDb;
/// <summary>建立在文件存储上的 SQL 数据库，提供查询、原子 DDL/DML 批处理及连接级 C# 函数。</summary>
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
