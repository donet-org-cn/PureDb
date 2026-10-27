using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;

namespace PureDb;
/// <remarks>本部分管理可重建主索引检查点、按需 CRC 校验和热点页缓存。</remarks>
public sealed partial class Database
{
    /// <summary>供隔离进程故障测试观察维护阶段的内部回调；普通运行保持为空。</summary>
    internal static Action<string>? MaintenanceStage;
    private long _checkpointOffset;
    private readonly ConcurrentDictionary<long, byte> _verifiedValues = new();
    private int _verifiedCount;
    /// <summary>按需校验不可变记录，并记住校验状态以避免重复计算 CRC。</summary>
    /// <param name="entry">已定位记录的日志偏移、长度、索引键及 CRC 信息。</param>
    /// <param name="value">当前已提交记录的只读字节，供 CRC 校验。</param>
    /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
    private void VerifyValue(Entry entry, ReadOnlySpan<byte> value, long? id = null)
    {
        if (!entry.Verify)
            return;
        bool known = _verifiedValues.ContainsKey(entry.Offset);
        if (!known && Crc32.Compute(value) != entry.Crc)
            throw new InvalidDataException($"Value checksum mismatch at {entry.Offset}.");
        // 主索引热点页已为每条记录提供原子校验标记，
        // 不再为同一个不可变值额外分配字典节点。
        if (id is long key && _primary.RememberVerified(key, entry))
            return;
        if (!known)
        {
            if (Volatile.Read(ref _verifiedCount) > 16384)
            {
                _verifiedValues.Clear();
                Interlocked.Exchange(ref _verifiedCount, 0);
            }

            if (_verifiedValues.TryAdd(entry.Offset, 0))
                Interlocked.Increment(ref _verifiedCount);
        }
    }

    /// <summary>尝试载入与数据文件代次、长度和时间匹配的索引检查点；不可信检查点被丢弃。</summary>
    /// <param name="length">当前数据文件的已提交长度，单位为字节。</param>
    private void TryLoadCheckpoint(long length)
    {
        var index = DiskIndex.Open(_path + ".pidx", _generation, length, File.GetLastWriteTimeUtc(_path).Ticks);
        if (index is null)
            return;
        var bytes = index.Metadata.AsSpan();
        if (bytes.Length % 16 != 0)
        {
            index.Dispose();
            return;
        }

        for (int p = 0; p < bytes.Length; p += 16)
        {
            long key = BinaryPrimitives.ReadInt64LittleEndian(bytes[p..]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[(p + 8)..]);
            if (count <= 0 || _keyCounts.ContainsKey(key))
            {
                _keyCounts.Clear();
                index.Dispose();
                return;
            }

            _keyCounts[key] = count;
            _keyVersions[key] = _version;
        }

        _primary.Load(index);
        _scanOffset = _checkpointOffset = index.SourceLength;
    }

    /// <summary>先刷入数据文件，再原子保存主索引及各表计数元数据。</summary>
    private void SaveCheckpoint()
    {
        if (_scanOffset == _checkpointOffset && File.Exists(_path + ".pidx"))
            return;
        _file.Flush(true); // The index may only reference a durable data prefix.
        var metadata = CheckpointMetadata();
        // 基索引已经有序，只排序尚未写入检查点的增量，再归并，避免反复排序整个大库。
        var entries = _primary.Range(long.MinValue, long.MaxValue)
            .Select(pair => new DiskIndex.Item(pair.Key, pair.Value.Key, pair.Value.Offset, pair.Value.Length, pair.Value.Crc));
        string path = _path + ".pidx";
        DiskIndex.Write(path, _generation, _scanOffset, File.GetLastWriteTimeUtc(_path).Ticks, metadata, _primary.Count, entries);
        MaintenanceStage?.Invoke("checkpoint-index-synced");
        // 仅在新检查点完整写入并验证头部后切换基索引。失败时保留原有内存状态，
        // 数据日志仍是恢复依据；切换发生在写锁内，不与读取器共享可变增量。
        if (DiskIndex.Open(path, _generation, _scanOffset) is not { } index)
            throw new IOException("Could not reopen the completed primary checkpoint.");
        _primary.Load(index);
        _verifiedValues.Clear();
        _verifiedCount = 0;
        _checkpointOffset = _scanOffset;
    }

    /// <summary>编码当前有效的表或键空间计数，供检查点及压缩后的索引共享。</summary>
    /// <returns>包含非空键空间及其计数的独立元数据。</returns>
    private byte[] CheckpointMetadata()
    {
        var metadata = new byte[_keyCounts.Count(p => p.Value > 0) * 16];
        int p = 0;
        foreach (var pair in _keyCounts.Where(p => p.Value > 0))
        {
            BinaryPrimitives.WriteInt64LittleEndian(metadata.AsSpan(p), pair.Key);
            BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(p + 8), pair.Value);
            p += 16;
        }

        return metadata;
    }

    /// <summary>最近一次后台检查点的 I/O 或权限错误；成功保存后清除，不代表事务日志丢失。</summary>
    public Exception? CheckpointError
    {
        get; private set;
    }

    /// <summary>保存检查点并记录可恢复的 I/O 或权限错误，不将旁路索引失败视为日志提交丢失。</summary>
    private void TrySaveCheckpoint()
    {
        try
        {
            SaveCheckpoint();
            CheckpointError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            CheckpointError = error;
        }
    }

    /// <summary>追加量达到配置阈值时尝试保存检查点。</summary>
    private void MaybeCheckpoint()
    {
        if (_options.CheckpointIntervalBytes > 0 && _scanOffset - _checkpointOffset >= _options.CheckpointIntervalBytes)
            TrySaveCheckpoint();
    }

    /// <summary>保存当前主索引检查点；检查点丢失时仍可通过数据日志恢复提交。</summary>
    /// <param name="cancellationToken">取消请求；已准入写入不会因取消而返回不明确的提交结果。</param>
    /// <returns>完成时表示该操作结束的任务。</returns>
    public ValueTask CheckpointAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckWritable();
        EnterProcess();
        _gate.EnterWriteLock();
        try
        {
            RefreshShared(force: true);
            SaveCheckpoint();
            return ValueTask.CompletedTask;
        }
        finally
        {
            _gate.ExitWriteLock();
            ExitProcess();
        }
    }

    /// <summary>扫描全部已提交日志帧并校验结构和校验和，包括已覆盖、已删除的历史记录。</summary>
    /// <param name="cancellationToken">取消请求；已准入写入不会因取消而返回不明确的提交结果。</param>
    /// <exception cref = "System.IO.InvalidDataException">检测到损坏的事务帧或不匹配的校验和。</exception>
    public void VerifyIntegrity(CancellationToken cancellationToken = default)
    {
        using var lease = new ReadLease(this);
        long offset = HeaderSize;
        while (offset < _scanOffset)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var h = new byte[FrameHeader];
            ReadAt(_handle, h, offset);
            int size = BinaryPrimitives.ReadInt32LittleEndian(h);
            if (size < 4 || size > MaxFrame || BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(8)) != ~size || size > _scanOffset - offset - FrameHeader)
                throw new InvalidDataException("Invalid transaction frame.");
            var frame = new byte[FrameHeader + size];
            h.CopyTo(frame, 0);
            ReadAt(_handle, frame.AsSpan(FrameHeader), offset + FrameHeader);
            if (Crc32.Compute(frame.AsSpan(FrameHeader)) != BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)))
                throw new InvalidDataException($"Checksum mismatch at {offset}.");
            ValidateFrame(frame);
            offset += frame.Length;
        }
    }

    /// <summary>将不可变磁盘索引与内存增量合并的主索引，热点页中的校验状态通过原子标记发布。</summary>
    private sealed class PrimaryIndex : IEnumerable<KeyValuePair<long, Entry>>, IDisposable
    {
        private DiskIndex? _base;
        private Dictionary<long, Entry?> _changes = [];
        private long[]? _sortedChanges;
        // 页内直接存放不可变索引项，初始化完成后才发布标记；
        // 读取方不能观察到只写入一部分字段的 Entry 结构。
        /// <summary>主索引热点页，内联保存索引项和独立的初始化、校验标记。</summary>
        private sealed class HotPage
        {
            internal readonly Entry[] Entries = new Entry[256];
            internal readonly int[] Flags = new int[256];
        }

        private readonly ConcurrentDictionary<long, HotPage> _hotPages = new();
        /// <summary>有界低位页号目录，密集 KV 主键避免并发字典散列；稀疏及 SQL 编码键保留字典路径。</summary>
        private HotPage?[]? _denseHotPages;
        private readonly object _hotGate = new();
        private int _hotPageCount;
        internal int MaxHotEntries = 524288;
        /// <summary>从有界密集目录或稀疏目录定位已发布的不可变热点页。</summary>
        /// <param name="key">记录 ID 右移八位后的页号。</param>
        /// <returns>已发布的热点页；未缓存时为空。</returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private HotPage? FindHot(long key)
        {
            var dense = Volatile.Read(ref _denseHotPages);
            if (dense is not null && (ulong)key < (ulong)dense.Length)
                return Volatile.Read(ref dense[(int)key]);
            return _hotPages.TryGetValue(key, out var page) ? page : null;
        }
        /// <summary>将不可变记录已通过校验的状态发布到热点页，避免另外创建校验字典节点。</summary>
        /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
        /// <param name="entry">已定位记录的日志偏移、长度、索引键及 CRC 信息。</param>
        /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
        public bool RememberVerified(long id, Entry entry)
        {
            if (FindHot(id >> 8) is { } page)
            {
                int slot = (int)(id & 255);
                if (Volatile.Read(ref page.Flags[slot]) != 0 && page.Entries[slot] == entry)
                {
                    Interlocked.CompareExchange(ref page.Flags[slot], 2, 1);
                    return true;
                }
            }

            return false;
        }

        private int _count;
        /// <summary>磁盘基索引叠加内存增量后的有效记录数。</summary>
        public int Count => _count;

        /// <summary>没有内存增量时的不可变基索引；仅能在外层读取保护期内使用。</summary>
        internal DiskIndex? UnchangedBase => _changes.Count == 0 ? _base : null;

        /// <summary>载入新的不可变基索引并重置相关热点缓存和增量状态。</summary>
        /// <param name="index">要作为新基索引载入的不可变持久化索引。</param>
        public void Load(DiskIndex index)
        {
            Clear();
            _base = index;
            _count = index.Count;
        }

        /// <summary>先检查事务后增量，再查询热点页及磁盘索引。</summary>
        /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
        /// <param name="value">找到时返回当前有效的日志索引项。</param>
        /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue(long id, out Entry value)
        {
            if (_changes.Count > 0 && _changes.TryGetValue(id, out var change))
            {
                value = change.GetValueOrDefault();
                return change.HasValue;
            }

            var hot = FindHot(id >> 8);
            if (hot is not null)
            {
                int slot = (int)(id & 255), flag = Volatile.Read(ref hot.Flags[slot]);
                if (flag != 0)
                {
                    var entry = hot.Entries[slot];
                    value = new(entry.Key, entry.Offset, entry.Length, entry.Crc, flag == 1);
                    return true;
                }
            }

            return FindBase(id, hot, out value);
        }

        /// <summary>在不可变磁盘基索引中定位记录，并按需填充热点页。</summary>
        /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
        /// <param name="hot">是否启用热点页查询或填充。</param>
        /// <param name="value">找到时返回不可变基索引中的日志索引项。</param>
        /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
        private bool FindBase(long id, HotPage? hot, out Entry value)
        {
            if (_base is not null && _base.Count > 0 && id >= _base.MinKey && id <= _base.MaxKey)
            {
                int at = id == _base.MinKey ? 0 : id == _base.MaxKey ? _base.Count - 1 : _base.Bound(id, false);
                if (at < _base.Count && _base.At(at) is var e && e.Key == id)
                {
                    value = new(e.Id, e.Offset, e.Length, e.Crc, true);
                    Remember(id, value, hot);
                    return true;
                }
            }

            value = default;
            return false;
        }

        /// <summary>在初始化完成后原子发布一个热点索引项。</summary>
        /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
        /// <param name="entry">已定位记录的日志偏移、长度、索引键及 CRC 信息。</param>
        /// <param name="page">当前数据页或热点页；具体类型由当前签名确定。</param>
        private void Remember(long id, Entry entry, HotPage? page)
        {
            if (page is null)
            {
                if (Volatile.Read(ref _hotPageCount) >= MaxHotEntries / 256)
                    return;
                lock (_hotGate)
                {
                    long key = id >> 8;
                    page = FindHot(key);
                    if (page is null)
                    {
                        if (_hotPageCount >= MaxHotEntries / 256)
                            return;
                        page = new();
                        // 小库只分配与已有记录数量相称的目录，稀疏高位键不扩张数组。
                        int denseLength = (int)Math.Min(Math.Min(2048, MaxHotEntries / 256), ((_base?.Count ?? 1) + 255L) / 256);
                        if ((ulong)key < (ulong)denseLength)
                        {
                            var dense = _denseHotPages;
                            if (dense is null)
                            {
                                dense = new HotPage?[denseLength];
                                Volatile.Write(ref _denseHotPages, dense);
                            }
                            Volatile.Write(ref dense[(int)key], page);
                        }
                        else
                            _hotPages[key] = page;
                        _hotPageCount++;
                    }
                }
            }

            int slot = (int)(id & 255);
            lock (page)
                if (page.Flags[slot] == 0)
                {
                    page.Entries[slot] = entry;
                    Volatile.Write(ref page.Flags[slot], entry.Verify ? 1 : 2);
                }
        }

        /// <summary>按记录 ID 读取或更新主索引中的当前索引项。</summary>
        /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
        public Entry this[long id]
        {
            get => TryGetValue(id, out var entry) ? entry : throw new KeyNotFoundException();
            set
            {
                if (!TryGetValue(id, out _))
                    _count++;
                _changes[id] = value;
                _sortedChanges = null;
            }
        }

        /// <summary>向主索引加入一个记录 ID 与日志索引项。</summary>
        /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
        /// <param name="entry">已定位记录的日志偏移、长度、索引键及 CRC 信息。</param>
        public void Add(long id, Entry entry) => this[id] = entry;
        /// <summary>在已知记录是否存在的前提下更新索引，减少重复查找。</summary>
        /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
        /// <param name="entry">已定位记录的日志偏移、长度、索引键及 CRC 信息。</param>
        /// <param name="existed">调用方是否已确认该记录原本存在。</param>
        public void SetKnown(long id, Entry entry, bool existed)
        {
            if (!existed)
                _count++;
            _changes[id] = entry;
            _sortedChanges = null;
        }

        /// <summary>删除已知存在的记录 ID 并更新计数。</summary>
        /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
        public void RemoveKnown(long id)
        {
            _changes[id] = null;
            _sortedChanges = null;
            _count--;
        }

        /// <summary>删除指定记录 ID；保留必要的增量墓碑以遮蔽磁盘旧项。</summary>
        /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
        /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
        public bool Remove(long id)
        {
            if (!TryGetValue(id, out _))
                return false;
            _changes[id] = null;
            _sortedChanges = null;
            _count--;
            return true;
        }

        /// <summary>将磁盘基索引与内存变更合并，枚举索引键闭区间内的有效项。</summary>
        /// <param name="min">查询闭区间的下界，包含该值。</param>
        /// <param name="max">查询闭区间的上界，包含该值。</param>
        /// <param name="descending">是否从上界向下界惰性枚举。</param>
        /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
        public IEnumerable<KeyValuePair<long, Entry>> Range(long min, long max, bool descending = false)
        {
            if (min > max)
                yield break;
            // 同一提交版本只排序一次长整型键，不复制整条 Entry；窄范围直接二分定位。
            // 所有写入持有外层写锁，读取器可以安全复用已发布的不可变数组。
            var changes = Volatile.Read(ref _sortedChanges);
            if (changes is null)
            {
                changes = _changes.Keys.ToArray();
                // 顺序批量导入通常已经有序，线性验证后即可复用；不依赖字典枚举顺序契约。
                for (int i = 1; i < changes.Length; i++)
                    if (changes[i - 1] > changes[i])
                    {
                        Array.Sort(changes);
                        break;
                    }
                changes = Interlocked.CompareExchange(ref _sortedChanges, changes, null) ?? changes;
            }
            int at = Array.BinarySearch(changes, descending ? max : min);
            if (at < 0)
                at = descending ? ~at - 1 : ~at;
            int step = descending ? -1 : 1;
            bool InRange() => (uint)at < (uint)changes.Length && changes[at] >= min && changes[at] <= max;
            if (_base is not null)
            {
                int begin = _base.Bound(min, false), end = _base.Bound(max, true);
                foreach (var e in _base.Range(begin, end, descending))
                {
                    while (InRange() && (descending ? changes[at] > e.Key : changes[at] < e.Key))
                    {
                        long key = changes[at];
                        at += step;
                        if (_changes[key] is Entry value)
                            yield return new(key, value);
                    }

                    if (!_changes.ContainsKey(e.Key))
                        yield return new(e.Key, new(e.Id, e.Offset, e.Length, e.Crc, true));
                }
            }

            while (InRange())
            {
                long key = changes[at];
                at += step;
                if (_changes[key] is Entry value)
                    yield return new(key, value);
            }
        }

        /// <summary>清空基索引和内存增量，为重放或文件替换重建索引。</summary>
        public void Clear()
        {
            _base?.Dispose();
            _base = null;
            _changes = [];
            _sortedChanges = null;
            _hotPages.Clear();
            Volatile.Write(ref _denseHotPages, null);
            _hotPageCount = 0;
            _count = 0;
        }

        /// <summary>合并枚举当前有效主索引项，过滤被删除或覆盖的磁盘记录。</summary>
        /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
        public IEnumerator<KeyValuePair<long, Entry>> GetEnumerator()
        {
            if (_base is not null)
                foreach (var e in _base.Range(0, _base.Count))
                {
                    if (!_changes.ContainsKey(e.Key))
                        yield return new(e.Key, new(e.Id, e.Offset, e.Length, e.Crc, true));
                }

            foreach (var pair in _changes)
                if (pair.Value is Entry e)
                    yield return new(pair.Key, e);
        }

        /// <summary>合并枚举当前有效主索引项，过滤被删除或覆盖的磁盘记录。</summary>
        /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        /// <summary>释放持久化基索引句柄和热点缓存资源。</summary>
        public void Dispose() => Clear();
    }
}
