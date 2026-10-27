using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Threading.Channels;
using System.IO.MemoryMappedFiles;

namespace PureDb;
/// <summary>纯托管的追加日志文件数据库，提供原子批量写入、索引查询和可选的多进程协调。</summary>
public sealed partial class Database : IDisposable, IAsyncDisposable, ISqlStorage
{
    private const int HeaderSize = 16, FrameHeader = 16, OpHeader = 24, MaxFrame = 256 * 1024 * 1024;
    /// <summary>原生数据文件的固定格式标识。</summary>
    private static ReadOnlySpan<byte> Magic => "PUREDB01"u8;

    private readonly string _path;
    private readonly DatabaseOptions _options;
    private readonly Snapshot _snapshot;
    private readonly FileStream? _ownership;
    private readonly Mutex? _processMutex;
    private readonly ConcurrentDictionary<long, byte[]> _pages = new();
    private readonly ConcurrentQueue<long> _pageOrder = new();
    private int _cachedPages;
    private const int PageSize = 4096;
    private long _scanOffset, _version, _resetVersion;
    private readonly Dictionary<long, long> _keyVersions = [];
    private readonly Dictionary<long, int> _keyCounts = [];
    private bool _abandoned;
    private SafeFileHandle? _coordHandle;
    private MemoryMappedFile? _coordMap;
    private MemoryMappedViewAccessor? _coordView;
    private int _generation;
    /// <summary>当前已提交版本，用于 SQL 表缓存失效和乐观事务校验。</summary>
    internal long Version => _version;
    /// <summary>当前文件的实例访问方式，供 SQL 事务验证锁定能力。</summary>
    internal DatabaseAccess Access => _options.Access;

    private FileStream _file = null!;
    private SafeFileHandle _handle = null!;
    private readonly ReaderWriterLockSlim _gate = new();
    private readonly PrimaryIndex _primary = new();
    private SortedSet<(long Key, long Id)>? _secondary;
    private readonly object _secondarySync = new();
    /// <summary>按需构建并维护的索引键到记录 ID 排序映射。</summary>
    private SortedSet<(long Key, long Id)> Secondary
    {
        get
        {
            if (Volatile.Read(ref _secondary) is { } existing)
                return existing;
            lock (_secondarySync)
                return _secondary ??= new SortedSet<(long Key, long Id)>(_primary.Select(p => (p.Value.Key, p.Key)));
        }
    }

    private readonly Channel<Request> _queue;
    private readonly Lazy<Task> _writer;
    private int _closing;
    private bool _closed;
    private Exception? _fault;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;
    /// <summary>记录在追加日志中的位置、索引键和按需校验信息。</summary>
    /// <param name = "Key">索引键或查询主键表达式。</param>
    /// <param name = "Offset">数据在文件中的字节偏移，或查询的 OFFSET 表达式。</param>
    /// <param name = "Length">编码值的字节长度。</param>
    /// <param name = "Crc">IEEE CRC-32 校验和。</param>
    /// <param name = "Verify">首次读取时是否需要校验数据。</param>
    private readonly record struct Entry(long Key, long Offset, int Length, uint Crc = 0, bool Verify = false);
    /// <summary>写入队列中的事务、变换或压缩请求；完成信号对应明确的最终结果。</summary>
    /// <param name = "frame">已编码事务帧；变换或压缩请求可为 null。</param>
    /// <param name = "compact">是否请求压缩数据库。</param>
    /// <param name = "completion">发布最终提交结果的完成信号。</param>
    private sealed class Request(byte[]? frame, bool compact, TaskCompletionSource completion)
    {
        /// <summary>准入前已经编码的事务帧；变换请求可在消费时生成。</summary>
        public byte[]? Frame = frame;
        /// <summary>是否要求执行数据库压缩。</summary>
        public bool Compact = compact;
        /// <summary>提交成功或失败后的最终完成信号。</summary>
        public TaskCompletionSource Completion = completion;
        /// <summary>在写入保护期内根据一致快照生成事务变更的回调。</summary>
        public Func<ISqlSnapshot, (Mutation[] Mutations, int Affected)>? Transform;
        /// <summary>SQL 变换返回的直接受影响行数。</summary>
        public int Affected;
    }

    /// <summary>打开或创建原生格式数据库，并恢复已提交事务；访问方式由选项指定。</summary>
    /// <param name = "path">当前数据库或索引文件路径。</param>
    /// <param name = "options">可选配置；为 null 时使用默认值。</param>
    /// <exception cref = "System.IO.InvalidDataException">文件头、已提交日志或恢复数据不合法。</exception>
    public Database(string path, DatabaseOptions? options = null)
    {
        _options = options ?? new();
        // 该不可变快照外观只在数据库读取保护期内访问底层状态。
        _snapshot = new(this);
        if (_options.QueueCapacity < 1 || _options.MaxGroupRequests < 1 || _options.MaxTransactionBytes < 4 || _options.MaxTransactionBytes > MaxFrame || !Enum.IsDefined(_options.Durability) || !Enum.IsDefined(_options.Access) || _options.CacheBytes < 0 || _options.CheckpointIntervalBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        _path = Path.GetFullPath(path);
        _primary.MaxHotEntries = (int)Math.Min(int.MaxValue, _options.CacheBytes / 128);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (_options.Access == DatabaseAccess.Shared)
        {
            var canonical = OperatingSystem.IsWindows() ? _path.ToUpperInvariant() : _path;
            _processMutex = new Mutex(false, "PureDb-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
        }

        // 独占实例以只读句柄持有协调文件，共享实例以可写句柄持有。
        // FileShare 的兼容性检查阻止混用两种访问模式。
        _ownership = new FileStream(_path + ".lock", FileMode.OpenOrCreate, _processMutex is null ? FileAccess.Read : FileAccess.ReadWrite, _processMutex is null ? FileShare.None : FileShare.ReadWrite);
        if (_processMutex is not null)
            _coordHandle = _ownership.SafeFileHandle;
        EnterProcess();
        try
        {
            _file = OpenFile();
            Recover();
            PublishCoordination();
        }
        catch
        {
            _primary.Dispose();
            _handle?.Dispose();
            _file?.Dispose();
            _coordView?.Dispose();
            _coordMap?.Dispose();
            _ownership.Dispose();
            throw;
        }
        finally
        {
            ExitProcess();
        }

        _queue = Channel.CreateBounded<Request>(new BoundedChannelOptions(_options.QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _writer = new(() => Task.Run(WriterLoop));
    }

    /// <summary>打开允许原子替换的数据库句柄，并初始化顺序读取状态。</summary>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private FileStream OpenFile()
    {
        var file = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.RandomAccess);
        _handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        return file;
    }

    /// <summary>取得共享模式的进程间互斥量；锁被遗弃时强制刷新存储状态。</summary>
    private void EnterProcess()
    {
        if (_processMutex is null)
            return;
        try
        {
            _processMutex.WaitOne();
            _abandoned = false;
        }
        catch (AbandonedMutexException)
        {
            _abandoned = true;
        } // Recovery under the acquired lock follows.
    }

    /// <summary>释放已取得的进程间互斥量；独占模式不执行进程间解锁。</summary>
    private void ExitProcess() => _processMutex?.ReleaseMutex();
    // 协调标记仅用于快速发现变化，不能作为恢复依据。打开和写入仍以数据文件为准；
    // 互斥量被遗弃或标记无效时，必须先恢复，再读取缓存。
    /// <summary>发布数据文件的当前代次和提交位置，供其他实例快速判断是否需要刷新。</summary>
    private void PublishCoordination()
    {
        if (_coordHandle is null)
            return;
        Span<byte> stamp = stackalloc byte[24];
        stamp.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(stamp, 0x32524450);
        BinaryPrimitives.WriteInt32LittleEndian(stamp[4..], _generation);
        BinaryPrimitives.WriteInt64LittleEndian(stamp[8..], _scanOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(stamp[16..], Crc32.Compute(stamp[..16]));
        if (_coordView is null)
        {
            _coordMap = MemoryMappedFile.CreateFromFile(_ownership!, null, 24, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
            _coordView = _coordMap.CreateViewAccessor(0, 24, MemoryMappedFileAccess.ReadWrite);
        }

        _coordView.Write(0, BinaryPrimitives.ReadInt64LittleEndian(stamp));
        _coordView.Write(8, _scanOffset);
        Thread.MemoryBarrier();
        _coordView.Write(16, BinaryPrimitives.ReadInt64LittleEndian(stamp[16..]));
    }

    /// <summary>读取协调标记并校验其稳定性；标记不是数据恢复依据。</summary>
    /// <param name = "stamp">成功时返回通过稳定性校验的协调标记。</param>
    private void ReadCoordination(Span<byte> stamp)
    {
        BinaryPrimitives.WriteInt64LittleEndian(stamp, _coordView!.ReadInt64(0));
        BinaryPrimitives.WriteInt64LittleEndian(stamp[8..], _coordView.ReadInt64(8));
        BinaryPrimitives.WriteInt64LittleEndian(stamp[16..], _coordView.ReadInt64(16));
    }

    /// <summary>判断协调文件是否仍指向当前已读取的已提交状态。</summary>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    private bool CoordinationMatches()
    {
        if (_abandoned)
            return false;
        Span<byte> stamp = stackalloc byte[24];
        ReadCoordination(stamp);
        return BinaryPrimitives.ReadInt32LittleEndian(stamp) == 0x32524450 && BinaryPrimitives.ReadUInt32LittleEndian(stamp[16..]) == Crc32.Compute(stamp[..16]) && BinaryPrimitives.ReadInt32LittleEndian(stamp[4..]) == _generation && BinaryPrimitives.ReadInt64LittleEndian(stamp[8..]) == _scanOffset;
    }

    /// <summary>重新定位必要的数据文件并恢复其他进程的提交，丢弃失效缓存。</summary>
    /// <param name = "force">是否忽略快速协调提示，强制检查真实数据文件。</param>
    private void RefreshShared(bool force = false)
    {
        if (_processMutex is null)
            return;
        bool sameFile = false;
        if (!_abandoned)
        {
            Span<byte> stamp = stackalloc byte[24];
            ReadCoordination(stamp);
            if (BinaryPrimitives.ReadInt32LittleEndian(stamp) == 0x32524450 && BinaryPrimitives.ReadUInt32LittleEndian(stamp[16..]) == Crc32.Compute(stamp[..16]) && BinaryPrimitives.ReadInt32LittleEndian(stamp[4..]) == _generation)
            {
                sameFile = true;
                if (!force && BinaryPrimitives.ReadInt64LittleEndian(stamp[8..]) == _scanOffset)
                    return;
            }
        }

        // 命名互斥量覆盖文件替换和标记发布；中途崩溃会使锁被遗弃，迫使下次重新解析文件路径。
        // 即使协调标记未变化，写入方也检查真实日志。观察其他进程追加的尾部前先丢弃旧预读缓存；
        // 同一次恢复内保留预读缓存，将零碎的帧读取合并。
        _file.Flush(false);
        if (!sameFile)
        {
            _handle.Dispose();
            _file.Dispose();
            _file = OpenFile();
        }
        else
        {
            long length = _file.Length;
            if (length != _scanOffset)
                ReplaySharedTail(length);
            else
                _file.Position = _scanOffset;
            PublishCoordination();
            return;
        }

        _file.Position = 0;
        Recover();
        PublishCoordination();
    }

    /// <summary>在文件代次已确认且持有进程锁时，只重放新增日志，避免每次追赶预读整块文件头。</summary>
    /// <param name = "length">在进程锁内读取的实际数据文件长度；协调戳不作为恢复真值。</param>
    private void ReplaySharedTail(long length)
    {
        if (length < _scanOffset)
            throw new InvalidDataException("Committed database prefix was truncated.");
        if (length == _scanOffset)
            return;
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent((int)Math.Min(65536, Math.Max(FrameHeader, length - _scanOffset)));
        long offset = _scanOffset;
        int position = 0, available = 0;
        try
        {
            while (length - offset >= FrameHeader)
            {
                if (available - position < FrameHeader)
                {
                    available = (int)Math.Min(buffer.Length, length - offset);
                    position = 0;
                    ReadAt(_handle, buffer.AsSpan(0, available), offset);
                }

                var header = buffer.AsSpan(position, FrameHeader);
                int size = BinaryPrimitives.ReadInt32LittleEndian(header);
                if (size < 4 || size > MaxFrame || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != ~size)
                    throw new InvalidDataException($"Invalid frame header at {offset}.");
                if (length - offset - FrameHeader < size)
                    break;
                int total = FrameHeader + size;
                ReadOnlySpan<byte> frame;
                if (total <= buffer.Length)
                {
                    if (available - position < total)
                    {
                        available = (int)Math.Min(buffer.Length, length - offset);
                        position = 0;
                        ReadAt(_handle, buffer.AsSpan(0, available), offset);
                    }

                    frame = buffer.AsSpan(position, total);
                    position += total;
                }
                else
                {
                    var large = GC.AllocateUninitializedArray<byte>(total);
                    ReadAt(_handle, large, offset);
                    frame = large;
                    available = position = 0;
                }

                if (Crc32.Compute(frame[FrameHeader..]) != BinaryPrimitives.ReadUInt32LittleEndian(frame[4..]))
                    throw new InvalidDataException($"Checksum mismatch at {offset}; refusing silent data loss.");
                ValidateFrame(frame);
                Apply(frame, offset);
                offset += total;
            }

            if (offset != length)
            {
                _file.SetLength(offset);
                _file.Flush(true);
            }

            _file.Position = offset;
            _scanOffset = offset;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>取得当前有效文件的读取句柄，避免文件替换后继续使用错误路径。</summary>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private SafeFileHandle ReadHandle()
    {
        if (_handle.IsClosed)
            _handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        return _handle;
    }

    /// <summary>读取期间持有的进程内及必要的进程间保护；释放时按取得顺序逆序解锁。</summary>
    private readonly ref struct ReadLease
    {
        private readonly Database _db;
        private readonly bool _readLock;
        /// <summary>取得数据库读取保护，并在需要时刷新其他进程的已提交变更。</summary>
        /// <param name = "db">提供存储状态和读取保护的所属数据库。</param>
        public ReadLease(Database db)
        {
            _db = db;
            _readLock = true;
            db._gate.EnterReadLock();
            try
            {
                db.CheckReadable();
                // 稳定标记确定缓存读取在并发发布之前的可见时点。追加后不再修改的值和允许删除共享的
                // 旧文件句柄，使该快照在其他进程执行压缩替换期间仍可读取。
                if (db._processMutex is null || db.CoordinationMatches())
                    return;
            }
            catch
            {
                db._gate.ExitReadLock();
                throw;
            }

            db._gate.ExitReadLock();
            db.EnterProcess();
            db._gate.EnterWriteLock();
            _readLock = false;
            try
            {
                db.CheckReadable();
                db.RefreshShared();
            }
            catch
            {
                db._gate.ExitWriteLock();
                db.ExitProcess();
                throw;
            }
        }

        /// <summary>释放本次读取持有的进程内锁及必要的进程间保护。</summary>
        public void Dispose()
        {
            if (_readLock)
                _db._gate.ExitReadLock();
            else
            {
                _db._gate.ExitWriteLock();
                _db.ExitProcess();
            }
        }
    }

    /// <summary>在读取租约内调用快照回调；回调不能将受保护的内存视图泄露到租约之外。</summary>
    /// <param name = "action">只在读取保护期内执行的快照回调。</param>
    /// <typeparam name = "T">回调返回值或请求转换的结果类型。</typeparam>
    /// <returns>回调或类型转换得到的结果。</returns>
    T ISqlStorage.Read<T>(Func<ISqlSnapshot, T> action) => Read(action);
    /// <summary>在读取租约内调用快照回调；回调不能将受保护的内存视图泄露到租约之外。</summary>
    /// <param name = "action">只在读取保护期内执行的快照回调。</param>
    /// <typeparam name = "T">回调返回值或请求转换的结果类型。</typeparam>
    /// <returns>回调或类型转换得到的结果。</returns>
    internal T Read<T>(Func<ISqlSnapshot, T> action)
    {
        using var text = new SqlValueComparer.TextScope(null);
        using var lease = new ReadLease(this);
        return action(_snapshot);
    }

    /// <summary>在读取租约内调用快照回调；回调不能将受保护的内存视图泄露到租约之外。</summary>
    /// <param name = "action">只在读取保护期内执行的快照回调。</param>
    /// <param name = "state">传给快照回调的调用状态；避免额外捕获闭包。</param>
    /// <typeparam name = "T">回调返回值或请求转换的结果类型。</typeparam>
    /// <typeparam name = "TState">传给快照回调的调用状态类型。</typeparam>
    /// <returns>回调或类型转换得到的结果。</returns>
    internal T Read<T, TState>(Func<ISqlSnapshot, TState, T> action, TState state)
    {
        using var text = new SqlValueComparer.TextScope(null);
        using var lease = new ReadLease(this);
        return action(_snapshot, state);
    }

    /// <summary>受数据库读取保护约束的存储快照外观，不独立延长文件或锁的生命周期。</summary>
    /// <param name = "db">提供读取保护和存储状态的所属数据库。</param>
    internal sealed class Snapshot(Database db) : ISqlSnapshot
    {
        /// <summary>是否支持原生持久化索引；兼容或叠加快照可关闭专用路径。</summary>
        public bool HasPersistentIndexes => true;

        /// <summary>当前没有任何尾部变更的主索引；专用于一致快照内的批量投影。</summary>
        internal DiskIndex? UnchangedPrimaryIndex => db._primary.UnchangedBase;
        /// <summary>当前数据文件的规范路径。</summary>
        public string Path => db._path;
        /// <summary>用于识别文件替换的存储代次。</summary>
        public int Generation => db._generation;
        /// <summary>当前快照可见的数据文件提交长度。</summary>
        public long Length => db._scanOffset;
        /// <summary>当前数据文件的修改时间戳。</summary>
        public long SourceTime => File.GetLastWriteTimeUtc(db._path).Ticks;

        /// <summary>取得指定记录对应的日志位置及校验信息。</summary>
        /// <param name = "id">原生记录 ID 或受支持的整数主键。</param>
        /// <param name = "key">用于查询或统计的内部表键或索引键。</param>
        /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
        public DiskIndex.Item IndexItem(long id, long key)
        {
            var e = db._primary[id];
            return new(key, id, e.Offset, e.Length, e.Crc);
        }

        /// <summary>读取一个索引项对应的只读记录字节。</summary>
        /// <param name = "entry">已定位的日志索引项，包含偏移、长度及校验和。</param>
        /// <returns>受当前读取保护期约束的只读字节片段。</returns>
        public ReadOnlySpan<byte> IndexValue(DiskIndex.Item entry)
        {
            if (entry.Offset < HeaderSize || entry.Length < 0 || entry.Offset > db._scanOffset - entry.Length)
                throw new InvalidDataException("Invalid persistent record address.");
            return db.ReadValue(new(0, entry.Offset, entry.Length, entry.Crc, true));
        }

        /// <summary>读取索引项对应的只读记录内存，供 SQL 缓存在版本约束下使用。</summary>
        /// <param name = "entry">已定位的日志索引项，包含偏移、长度及校验和。</param>
        /// <returns>只读字节内存；不得修改，且必须遵守所属存储的生命周期。</returns>
        public ReadOnlyMemory<byte> IndexMemory(DiskIndex.Item entry) => db.ReadMemory(new(0, entry.Offset, entry.Length, entry.Crc, true), entry.Id);
        /// <summary>按原生记录 ID 枚举指定表的索引项。</summary>
        /// <param name = "tableId">表在模式目录中的内部 ID。</param>
        /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
        public IEnumerable<DiskIndex.Item> TableEntries(int tableId)
        {
            long min = (long)tableId << 40, max = min + ((1L << 40) - 1);
            return TableEntriesRange(tableId, min, max);
        }

        /// <summary>按原生记录 ID 的闭区间枚举指定表的索引项。</summary>
        /// <param name = "tableId">表在模式目录中的内部 ID。</param>
        /// <param name = "min">查询闭区间的下界，包含该值。</param>
        /// <param name = "max">查询闭区间的上界，包含该值。</param>
        /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
        public IEnumerable<DiskIndex.Item> TableEntriesRange(int tableId, long min, long max)
            => TableEntriesRange(tableId, min, max, false);

        /// <summary>按指定方向惰性枚举表主键区间，倒序 LIMIT 不物化整个区间。</summary>
        /// <param name="tableId">表在模式目录中的内部 ID。</param>
        /// <param name="min">包含的记录 ID 下界。</param>
        /// <param name="max">包含的记录 ID 上界。</param>
        /// <param name="descending">是否从最大记录 ID 向下读取。</param>
        /// <returns>当前快照中按主键排序的有效索引项。</returns>
        public IEnumerable<DiskIndex.Item> TableEntriesRange(int tableId, long min, long max, bool descending)
        {
            foreach (var pair in db._primary.Range(min, max, descending))
                if (pair.Value.Key == tableId)
                    yield return new(tableId, pair.Key, pair.Value.Offset, pair.Value.Length, pair.Value.Crc);
        }

        /// <summary>当前视图对应的已提交版本。</summary>
        public long Version => db.Version;

        /// <summary>获取指定表或键空间的变化版本，用于缓存失效。</summary>
        /// <param name = "key">用于查询或统计的内部表键或索引键。</param>
        /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
        public long KeyVersion(long key) => db._keyVersions.GetValueOrDefault(key, db._resetVersion);
        /// <summary>统计指定表或键空间中的有效记录数。</summary>
        /// <param name = "key">用于查询或统计的内部表键或索引键。</param>
        /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
        public int CountKey(long key) => db._keyCounts.GetValueOrDefault(key);
        /// <summary>枚举指定表或键空间内的有效记录 ID。</summary>
        /// <param name = "key">用于查询或统计的内部表键或索引键。</param>
        /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
        public IEnumerable<long> Ids(long key)
        {
            if (CountKey(key) == 0)
                yield break;
            if (key > 0 && key < 1 << 23)
            {
                foreach (var entry in TableEntries((int)key))
                    yield return entry.Id;
                yield break;
            }

            foreach (var pair in db.Secondary.GetViewBetween((key, long.MinValue), (key, long.MaxValue)))
                yield return pair.Id;
        }

        /// <summary>取得记录的只读字节片段；仅在读取保护期间有效，不得修改。</summary>
        /// <param name = "id">原生记录 ID 或受支持的整数主键。</param>
        /// <returns>受当前读取保护期约束的只读字节片段。</returns>
        public ReadOnlySpan<byte> Value(long id) => db._primary.TryGetValue(id, out var entry) ? db.ReadValue(entry, id) : default;
        /// <summary>取得记录的只读内存；不得修改或越过所属读取保护期持有。</summary>
        /// <param name = "id">原生记录 ID 或受支持的整数主键。</param>
        /// <returns>只读字节内存；不得修改，且必须遵守所属存储的生命周期。</returns>
        public ReadOnlyMemory<byte> ValueMemory(long id) => db._primary.TryGetValue(id, out var entry) ? db.ReadMemory(entry, id) : default;
        /// <summary>当前快照中的有效记录总数。</summary>
        public long Count => db._primary.Count;

        /// <summary>按记录 ID 读取独立记录；不存在时返回 null。</summary>
        /// <param name = "id">原生记录 ID 或受支持的整数主键。</param>
        /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
        public DbRecord? Get(long id) => db._primary.TryGetValue(id, out var e) ? db.ReadRecord(id, e) : null;
        /// <summary>判断当前视图是否存在指定记录 ID。</summary>
        /// <param name = "id">原生记录 ID 或受支持的整数主键。</param>
        /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
        public bool Contains(long id) => db._primary.TryGetValue(id, out _);
        /// <summary>按索引键闭区间枚举当前视图中的有效记录。</summary>
        /// <param name = "min">查询闭区间的下界，包含该值。</param>
        /// <param name = "max">查询闭区间的上界，包含该值。</param>
        /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
        public IEnumerable<DbRecord> Range(long min, long max)
        {
            if (min > max)
                yield break;
            foreach (var pair in db.Secondary.GetViewBetween((min, long.MinValue), (max, long.MaxValue)))
                yield return db.ReadRecord(pair.Id, db._primary[pair.Id]);
        }
    }

    /// <summary>在受保护的读取快照中获取当前有效记录数。</summary>
    public long Count
    {
        get
        {
            using var lease = new ReadLease(this);
            return _primary.Count;
        }
    }

    /// <summary>按记录 ID 读取一条记录；返回的数据与内部页缓存独立。</summary>
    /// <param name = "id">记录的唯一 ID。</param>
    /// <returns>找到的独立记录；不存在时为 null。</returns>
    public DbRecord? Get(long id)
    {
        using var lease = new ReadLease(this);
        return _primary.TryGetValue(id, out var entry) ? ReadRecord(id, entry) : null;
    }

    /// <summary>按 IndexKey 的闭区间查询记录，并按索引键及记录 ID 升序返回。</summary>
    /// <param name = "min">查询闭区间的下界，包含该值。</param>
    /// <param name = "max">查询闭区间的上界，包含该值。</param>
    /// <param name = "limit">返回记录的最大数量。</param>
    /// <returns>按索引顺序排列、最多包含 limit 条记录的列表。</returns>
    public IReadOnlyList<DbRecord> Query(long min, long max, int limit = 1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        using var lease = new ReadLease(this);
        var result = new List<DbRecord>(Math.Min(128, limit));
        if (min > max || limit == 0)
            return result;
        foreach (var pair in Secondary.GetViewBetween((min, long.MinValue), (max, long.MaxValue)))
        {
            result.Add(ReadRecord(pair.Id, _primary[pair.Id]));
            if (result.Count == limit)
                break;
        }

        return result;
    }

    /// <summary>取得满足所需前缀长度的数据页缓存；不会读取尚未提交的尾部。</summary>
    /// <param name = "page">当前数据页或热点页；具体类型由当前签名确定。</param>
    /// <param name = "required">本次读取需要的页内有效前缀长度。</param>
    /// <returns>本次编码或复制得到的字节数组。</returns>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private byte[] Page(long page, int required)
    {
        if (_pages.TryGetValue(page, out var bytes) && bytes.Length >= required)
            return bytes;
        return LoadPage(page);
    }

    /// <summary>从当前提交范围读取一个数据页并加入受预算限制的缓存。</summary>
    /// <param name = "page">当前数据页或热点页；具体类型由当前签名确定。</param>
    /// <returns>本次编码或复制得到的字节数组。</returns>
    private byte[] LoadPage(long page)
    {
        int available = (int)Math.Min(PageSize, _scanOffset - page * PageSize);
        var bytes = new byte[available];
        ReadAt(ReadHandle(), bytes, page * PageSize);
        if (_pages.TryAdd(page, bytes))
        {
            Interlocked.Increment(ref _cachedPages);
            _pageOrder.Enqueue(page);
            while (Volatile.Read(ref _cachedPages) > _options.CacheBytes / PageSize && _pageOrder.TryDequeue(out var victim))
                if (_pages.TryRemove(victim, out _))
                    Interlocked.Decrement(ref _cachedPages);
        }
        else if (_pages.TryGetValue(page, out var old) && old.Length < bytes.Length)
            _pages.TryUpdate(page, bytes, old);
        return bytes;
    }

    /// <summary>读取、校验并复制一条记录，返回的数据可交给公开调用方。</summary>
    /// <param name = "id">原生记录 ID 或受支持的整数主键。</param>
    /// <param name = "e">已定位记录的日志偏移、长度、索引键及 CRC 信息。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private DbRecord ReadRecord(long id, Entry e)
    {
        var value = new byte[e.Length];
        long offset = e.Offset;
        int copied = 0;
        while (copied < value.Length)
        {
            long page = offset / PageSize;
            int within = (int)(offset % PageSize), count = Math.Min(PageSize - within, value.Length - copied);
            // 已缓存的尾页前缀保持不可变；追加记录发布后才请求更长前缀，
            // 读取器不会把尚未提交的尾部字节放入缓存。
            if (_options.CacheBytes >= PageSize)
                Page(page, within + count).AsSpan(within, count).CopyTo(value.AsSpan(copied));
            else
                ReadAt(ReadHandle(), value.AsSpan(copied, count), offset);
            copied += count;
            offset += count;
        }

        VerifyValue(e, value, id);
        return new(id, e.Key, value);
    }

    /// <summary>读取记录的只读字节片段并按需校验；片段仅在读取保护期内有效。</summary>
    /// <param name = "e">已定位记录的日志偏移、长度、索引键及 CRC 信息。</param>
    /// <param name = "id">原生记录 ID 或受支持的整数主键。</param>
    /// <returns>受当前读取保护期约束的只读字节片段。</returns>
    private ReadOnlySpan<byte> ReadValue(Entry e, long? id = null)
    {
        long page = e.Offset / PageSize;
        int within = (int)(e.Offset % PageSize);
        if (_options.CacheBytes >= PageSize && within + e.Length <= PageSize)
        {
            var value = Page(page, within + e.Length).AsSpan(within, e.Length);
            VerifyValue(e, value, id);
            return value;
        }

        return ReadRecord(id ?? 0, e).Value;
    }

    /// <summary>取得可缓存的只读记录内存；跨页记录使用独立副本。</summary>
    /// <param name = "e">已定位记录的日志偏移、长度、索引键及 CRC 信息。</param>
    /// <param name = "id">原生记录 ID 或受支持的整数主键。</param>
    /// <returns>只读字节内存；不得修改，且必须遵守所属存储的生命周期。</returns>
    private ReadOnlyMemory<byte> ReadMemory(Entry e, long? id = null)
    {
        long page = e.Offset / PageSize;
        int within = (int)(e.Offset % PageSize);
        if (_options.CacheBytes >= PageSize && within + e.Length <= PageSize)
        {
            var value = Page(page, within + e.Length).AsMemory(within, e.Length);
            VerifyValue(e, value.Span, id);
            return value;
        }

        return ReadRecord(id ?? 0, e).Value;
    }

    /// <summary>原子写入或替换一条记录；返回的任务完成时可确定提交结果。</summary>
    /// <param name = "id">记录的唯一 ID。</param>
    /// <param name = "indexKey">用于范围查询的索引键。</param>
    /// <param name = "value">要写入的数据；写入接口在准入前复制，构造 Mutation 本身不复制。</param>
    /// <param name = "cancellationToken">取消请求；已准入写入不会因取消而返回不明确的提交结果。</param>
    /// <returns>完成时表示该操作结束的任务。</returns>
    public ValueTask PutAsync(long id, long indexKey, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) => WriteAsync([Mutation.Put(id, indexKey, value)], cancellationToken);
    /// <summary>原子删除指定 ID 的记录；删除不存在的记录不会失败。</summary>
    /// <param name = "id">记录的唯一 ID。</param>
    /// <param name = "cancellationToken">取消请求；已准入写入不会因取消而返回不明确的提交结果。</param>
    /// <returns>完成时表示该操作结束的任务。</returns>
    public ValueTask DeleteAsync(long id, CancellationToken cancellationToken = default) => WriteAsync([Mutation.Delete(id)], cancellationToken);
    /// <summary>复制输入后将整批操作作为一个原子事务提交；取消仅影响队列准入，已准入事务等待明确的提交结果。</summary>
    /// <param name = "mutations">按顺序执行的写入和删除操作。</param>
    /// <param name = "cancellationToken">取消请求；已准入写入不会因取消而返回不明确的提交结果。</param>
    /// <returns>完成时表示该操作结束的任务。</returns>
    /// <exception cref = "System.ArgumentException">编码事务超过配置允许的最大大小。</exception>
    public ValueTask WriteAsync(IReadOnlyList<Mutation> mutations, CancellationToken cancellationToken = default)
    {
        CheckWritable();
        cancellationToken.ThrowIfCancellationRequested();
        if (mutations.Count == 0)
            return ValueTask.CompletedTask;
        return Enqueue(new(Encode(mutations), false, new(TaskCreationOptions.RunContinuationsAsynchronously)), cancellationToken);
    }

    /// <summary>在准入前编码完整原子事务帧，并检查事务大小限制和校验和。</summary>
    /// <param name = "mutations">按事务顺序排列的写入和删除操作。</param>
    /// <returns>本次编码或复制得到的字节数组。</returns>
    private byte[] Encode(IReadOnlyList<Mutation> mutations)
    {
        long size = 4;
        foreach (var m in mutations)
            size = checked(size + OpHeader + (m.IsDelete ? 0 : m.Value.Length));
        if (size > _options.MaxTransactionBytes)
            throw new ArgumentException("Transaction exceeds MaxTransactionBytes.", nameof(mutations));
        var frame = new byte[FrameHeader + (int)size];
        BinaryPrimitives.WriteInt32LittleEndian(frame, (int)size);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), ~(int)size);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(FrameHeader), mutations.Count);
        int p = FrameHeader + 4;
        foreach (var m in mutations)
        {
            BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(p), m.Id);
            BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(p + 8), m.IndexKey);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(p + 16), m.IsDelete ? -1 : m.Value.Length);
            if (!m.IsDelete)
                m.Value.Span.CopyTo(frame.AsSpan(p + OpHeader));
            p += OpHeader + (m.IsDelete ? 0 : m.Value.Length);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), Crc32.Compute(frame.AsSpan(FrameHeader)));
        return frame;
    }

    /// <summary>在写入保护期间运行 SQL 变换并提交生成的变更，防止约束校验与写入之间发生竞态。</summary>
    /// <param name = "transform">在存储保护期内从一致快照生成原子变更的回调。</param>
    /// <param name = "token">取消请求；原子提交开始后需完成提交或回滚。</param>
    /// <returns>完成时提供直接受影响行数的任务。</returns>
    ValueTask<int> ISqlStorage.TransformAsync(Func<ISqlSnapshot, (Mutation[] Mutations, int Affected)> transform, CancellationToken token) => TransformAsync(transform, token);
    /// <summary>在写入保护期间运行 SQL 变换并提交生成的变更，防止约束校验与写入之间发生竞态。</summary>
    /// <param name = "transform">在存储保护期内从一致快照生成原子变更的回调。</param>
    /// <param name = "token">取消请求；原子提交开始后需完成提交或回滚。</param>
    /// <returns>完成时提供直接受影响行数的任务。</returns>
    internal async ValueTask<int> TransformAsync(Func<ISqlSnapshot, (Mutation[] Mutations, int Affected)> transform, CancellationToken token)
    {
        CheckWritable();
        if (_options.Durability == Durability.Buffered)
            return TransformBuffered(transform, token);
        var request = new Request(null, false, new(TaskCreationOptions.RunContinuationsAsynchronously))
        {
            Transform = transform
        };
        await Enqueue(request, token).ConfigureAwait(false);
        return request.Affected;
    }

    /// <summary>在写锁内校验并编码 SQL 变更，追加成功后发布版本；错误将写入器置为故障状态。</summary>
    /// <param name = "transform">在存储保护期内从一致快照生成原子变更的回调。</param>
    /// <param name = "token">取消请求；原子提交开始后需完成提交或回滚。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    private int TransformBuffered(Func<ISqlSnapshot, (Mutation[] Mutations, int Affected)> transform, CancellationToken token)
    {
        using var text = new SqlValueComparer.TextScope(null);
        token.ThrowIfCancellationRequested();
        EnterProcess();
        _gate.EnterWriteLock();
        try
        {
            CheckWritable();
            token.ThrowIfCancellationRequested();
            try
            {
                RefreshShared(force: true);
            }
            catch (Exception error)
            {
                Volatile.Write(ref _fault, error);
                throw;
            }

            // SQL 约束及编码先完成校验，失败时尚未向日志追加任何字节。
            var result = transform(_snapshot);
            if (result.Mutations.Length == 0)
                return result.Affected;
            byte[] frame = Encode(result.Mutations);
            try
            {
                long offset = _file.Position;
                _file.Write(frame);
                _file.Flush(false);
                Apply(frame, offset);
                _scanOffset = offset + frame.Length;
                PublishCoordination();
                MaybeCheckpoint();
            }
            catch (Exception error)
            {
                Volatile.Write(ref _fault, error);
                throw;
            }

            return result.Affected;
        }
        finally
        {
            _gate.ExitWriteLock();
            ExitProcess();
        }
    }

    /// <summary>排队重写有效记录以回收日志空间，原子替换文件并重建索引。</summary>
    /// <param name = "cancellationToken">取消请求；已准入写入不会因取消而返回不明确的提交结果。</param>
    /// <returns>完成时表示该操作结束的任务。</returns>
    public ValueTask CompactAsync(CancellationToken cancellationToken = default)
    {
        CheckWritable();
        return Enqueue(new(null, true, new(TaskCreationOptions.RunContinuationsAsynchronously)), cancellationToken);
    }

    /// <summary>等待写入队列准入，然后等待请求的明确最终结果；准入后的取消不改变提交结果。</summary>
    /// <param name = "request">等待准入或执行的写入请求。</param>
    /// <param name = "token">取消请求；原子提交开始后需完成提交或回滚。</param>
    /// <returns>表示操作最终完成的任务。</returns>
    private async ValueTask Enqueue(Request request, CancellationToken token)
    {
        // 只读使用不启动写入任务；首次准入前启动，确保关闭时能排空所有已准入请求，
        // 或者在请求准入之前拒绝它。
        _ = _writer.Value;
        await _queue.Writer.WriteAsync(request, token).ConfigureAwait(false);
        await request.Completion.Task.ConfigureAwait(false);
    }

    /// <summary>检查数据库尚未关闭且写入器未发生不可恢复错误。</summary>
    private void CheckWritable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing) != 0, this);
        if (Volatile.Read(ref _fault) is { } fault)
            throw new IOException("Writer failed; reopen the database.", fault);
    }

    /// <summary>检查数据库可读且未因先前写入故障失效。</summary>
    private void CheckReadable()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_fault is { } fault)
            throw new IOException("Writer failed; reopen the database.", fault);
    }

    /// <summary>串行消费写入请求并合并刷盘，按请求发布提交结果或一致的失败结果。</summary>
    /// <returns>表示操作最终完成的任务。</returns>
    private async Task WriterLoop()
    {
        var group = new List<Request>();
        try
        {
            await foreach (var first in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                group.Clear();
                group.Add(first);
                if (!first.Compact)
                    while (group.Count < _options.MaxGroupRequests && _queue.Reader.TryPeek(out var next) && !next.Compact)
                    {
                        if (_queue.Reader.TryRead(out var r))
                            group.Add(r);
                    }

                EnterProcess();
                _gate.EnterWriteLock();
                try
                {
                    RefreshShared(force: true);
                    if (first.Compact)
                        CompactCore();
                    else if (group.Any(r => r.Transform is not null))
                    {
                        bool written = false;
                        foreach (var r in group)
                        {
                            if (r.Transform is not null)
                            {
                                try
                                {
                                    using var text = new SqlValueComparer.TextScope(null);
                                    var result = r.Transform(_snapshot);
                                    r.Affected = result.Affected;
                                    r.Frame = result.Mutations.Length == 0 ? null : Encode(result.Mutations);
                                }
                                catch (Exception ex)
                                {
                                    r.Completion.SetException(ex);
                                    continue;
                                }
                            }

                            if (r.Frame is not null)
                            {
                                long offset = _file.Position;
                                _file.Write(r.Frame);
                                _file.Flush(false);
                                Apply(r.Frame, offset);
                                _scanOffset = offset + r.Frame.Length;
                                written = true;
                            }
                        }

                        if (written)
                            _file.Flush(_options.Durability == Durability.Full);
                    }
                    else
                    {
                        long offset = _file.Position;
                        bool unlock = _processMutex is null && _options.Durability == Durability.Full;
                        if (unlock)
                            _gate.ExitWriteLock();
                        try
                        {
                            foreach (var r in group)
                                if (r.Frame is not null)
                                    _file.Write(r.Frame);
                            _file.Flush(_options.Durability == Durability.Full);
                        }
                        finally
                        {
                            if (unlock)
                                _gate.EnterWriteLock();
                        }

                        foreach (var r in group)
                            if (r.Frame is not null)
                            {
                                Apply(r.Frame, offset);
                                offset += r.Frame.Length;
                            }

                        _scanOffset = offset;
                    }

                    PublishCoordination();
                    MaybeCheckpoint();
                }
                finally
                {
                    _gate.ExitWriteLock();
                    ExitProcess();
                }

                foreach (var r in group)
                    r.Completion.TrySetResult();
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _fault, ex);
            _queue.Writer.TryComplete(ex);
            foreach (var r in group)
                r.Completion.TrySetException(ex);
            while (_queue.Reader.TryRead(out var r))
                r.Completion.TrySetException(ex);
        }
    }

    /// <summary>将已提交帧中的操作应用到主索引、计数和缓存版本。</summary>
    /// <param name = "frame">包含完整事务头及操作的编码帧。</param>
    /// <param name = "offset">文件或缓冲区中的字节偏移。</param>
    private void Apply(ReadOnlySpan<byte> frame, long offset)
    {
        _version++;
        int count = BinaryPrimitives.ReadInt32LittleEndian(frame[FrameHeader..]), p = FrameHeader + 4;
        for (int i = 0; i < count; i++)
        {
            long id = BinaryPrimitives.ReadInt64LittleEndian(frame[p..]);
            long key = BinaryPrimitives.ReadInt64LittleEndian(frame[(p + 8)..]);
            int length = BinaryPrimitives.ReadInt32LittleEndian(frame[(p + 16)..]);
            bool exists = _primary.TryGetValue(id, out var old);
            if (exists && (length < 0 || old.Key != key))
            {
                _secondary?.Remove((old.Key, id));
                System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_keyCounts, old.Key, out _)--;
                _keyVersions[old.Key] = _version;
            }

            if (length >= 0)
            {
                _primary.SetKnown(id, new(key, offset + p + OpHeader, length, Crc32.Compute(frame.Slice(p + OpHeader, length))), exists);
                if (!exists || old.Key != key)
                {
                    _secondary?.Add((key, id));
                    System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_keyCounts, key, out _)++;
                }

                _keyVersions[key] = _version;
            }
            else if (exists)
                _primary.RemoveKnown(id);
            p += OpHeader + Math.Max(0, length);
        }
    }

    /// <summary>写入原生文件头和代次，用于识别格式及原子替换后的新文件。</summary>
    /// <param name = "stream">接收原生文件头的输出流。</param>
    /// <param name = "generation">数据文件代次，用于识别原子替换。</param>
    private void WriteHeader(Stream stream, int? generation = null)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], generation ?? _generation);
        stream.Write(header);
    }

    /// <summary>验证文件头和日志帧，重放已提交事务并处理未完成的尾部。</summary>
    private void Recover()
    {
        long fileLength = _file.Length;
        if (fileLength == 0)
        {
            _generation = Random.Shared.Next(1, int.MaxValue);
            WriteHeader(_file);
            _file.Flush(true);
            _scanOffset = HeaderSize;
            return;
        }

        if (fileLength < HeaderSize)
            throw new InvalidDataException("Incomplete database header.");
        Span<byte> header = stackalloc byte[HeaderSize];
        _file.ReadExactly(header);
        if (!header[..8].SequenceEqual(Magic) || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != 1)
            throw new InvalidDataException("Unsupported database format.");
        int generation = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        if (_scanOffset == 0 || generation != _generation || fileLength < _scanOffset)
        {
            _primary.Clear();
            _secondary = null;
            _pages.Clear();
            _pageOrder.Clear();
            _cachedPages = 0;
            _keyCounts.Clear();
            _keyVersions.Clear();
            _resetVersion = ++_version;
            _checkpointOffset = 0;
            _verifiedValues.Clear();
            _scanOffset = HeaderSize;
            _generation = generation;
            _version++;
            if (!_options.VerifyOnOpen)
                TryLoadCheckpoint(fileLength);
        }

        long offset = _scanOffset;
        _file.Position = offset;
        while (offset < fileLength)
        {
            if (fileLength - offset < FrameHeader)
                break;
            byte[] h = new byte[FrameHeader];
            _file.ReadExactly(h);
            int length = BinaryPrimitives.ReadInt32LittleEndian(h);
            if (length < 4 || length > MaxFrame || BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(8)) != ~length)
                throw new InvalidDataException($"Invalid frame header at {offset}.");
            if (fileLength - offset - FrameHeader < length)
                break;
            byte[] frame = new byte[FrameHeader + length];
            h.CopyTo(frame, 0);
            _file.ReadExactly(frame.AsSpan(FrameHeader));
            if (Crc32.Compute(frame.AsSpan(FrameHeader)) != BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)))
                throw new InvalidDataException($"Checksum mismatch at {offset}; refusing silent data loss.");
            ValidateFrame(frame);
            Apply(frame, offset);
            offset += frame.Length;
        }

        if (offset != fileLength)
        {
            _file.SetLength(offset);
            _file.Flush(true);
        }

        _file.Position = offset;
        _scanOffset = offset;
    }

    /// <summary>验证事务帧内操作数量、长度和边界，不允许越过帧结尾。</summary>
    /// <param name = "frame">包含完整事务头及操作的编码帧。</param>
    private static void ValidateFrame(ReadOnlySpan<byte> frame)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(frame[FrameHeader..]);
        if (count < 0 || count > (frame.Length - FrameHeader - 4) / OpHeader)
            throw new InvalidDataException("Invalid operation count.");
        int p = FrameHeader + 4;
        for (int i = 0; i < count; i++)
        {
            if (frame.Length - p < OpHeader)
                throw new InvalidDataException("Truncated operation.");
            int length = BinaryPrimitives.ReadInt32LittleEndian(frame[(p + 16)..]);
            if (length < -1 || length > frame.Length - p - OpHeader)
                throw new InvalidDataException("Invalid value length.");
            p += OpHeader + Math.Max(0, length);
        }

        if (p != frame.Length)
            throw new InvalidDataException("Unexpected frame bytes.");
    }

    /// <summary>从指定文件位置完整读取目标缓冲区；意外结束时抛出异常。</summary>
    /// <param name = "handle">本操作使用的安全文件句柄。</param>
    /// <param name = "destination">接收文件读取结果的目标缓冲区，必须完整填满。</param>
    /// <param name = "offset">文件或缓冲区中的字节偏移。</param>
    private static void ReadAt(SafeFileHandle handle, Span<byte> destination, long offset)
    {
        while (!destination.IsEmpty)
        {
            int n = RandomAccess.Read(handle, destination, offset);
            if (n == 0)
                throw new EndOfStreamException();
            destination = destination[n..];
            offset += n;
        }
    }

    /// <summary>在写入保护期间复制当前有效记录并原子替换日志文件。</summary>
    private void CompactCore()
    {
        string temp = _path + ".compact";
        DiskIndex? replacement = null;
        try
        {
            var entries = new DiskIndex.Item[_primary.Count];
            int entryCount = 0;
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            {
                WriteHeader(output, unchecked(_generation + 1));
                byte[] frame = new byte[1024 * 1024];
                int position = FrameHeader + 4, count = 0;
                void FlushFrame()
                {
                    if (count == 0)
                        return;
                    frame.AsSpan(0, FrameHeader).Clear();
                    int length = position - FrameHeader;
                    BinaryPrimitives.WriteInt32LittleEndian(frame, length);
                    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), ~length);
                    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(FrameHeader), count);
                    BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), Crc32.Compute(frame.AsSpan(FrameHeader, length)));
                    output.Write(frame.AsSpan(0, position));
                    position = FrameHeader + 4;
                    count = 0;
                }

                foreach (var (id, entry) in _primary.Range(long.MinValue, long.MaxValue))
                {
                    int needed = checked(OpHeader + entry.Length);
                    if (position + needed > frame.Length)
                    {
                        FlushFrame();
                        if (position + needed > frame.Length)
                            frame = new byte[position + needed];
                    }

                    frame.AsSpan(position, OpHeader).Clear();
                    BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(position), id);
                    BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(position + 8), entry.Key);
                    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(position + 16), entry.Length);
                    var destination = frame.AsSpan(position + OpHeader, entry.Length);
                    if (entry.Length > PageSize)
                    {
                        // 大值直接读入复用的事务帧，省去跨页记录的中间数组和逐页复制。
                        ReadAt(ReadHandle(), destination, entry.Offset);
                        VerifyValue(entry, destination, id);
                    }
                    else
                        ReadValue(entry, id).CopyTo(destination);
                    entries[entryCount++] = new(id, entry.Key, output.Position + position + OpHeader, entry.Length, entry.Crc);
                    position += needed;
                    count++;
                }

                FlushFrame();
                output.Flush(true);
            }

            MaintenanceStage?.Invoke("compact-data-synced");
            // 索引是可丢弃的旁路文件。先完成新数据及其索引，再替换数据文件；
            // 任一阶段崩溃时，代次不匹配的索引会被拒绝并从对应日志恢复。
            if (entryCount != entries.Length)
                throw new InvalidDataException("Compaction entry count mismatch.");
            long compactLength = new FileInfo(temp).Length;
            DiskIndex.Write(_path + ".pidx", unchecked(_generation + 1), compactLength, File.GetLastWriteTimeUtc(temp).Ticks, CheckpointMetadata(), entries);
            replacement = DiskIndex.Open(_path + ".pidx", unchecked(_generation + 1), compactLength)
                ?? throw new IOException("Could not reopen the completed compacted index.");
            MaintenanceStage?.Invoke("compact-index-synced");
            _handle.Dispose();
            _file.Dispose();
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Replace(temp, _path, null);
                    break;
                }
                catch (IOException error) when (OperatingSystem.IsWindows() && attempt < 5 && (error.HResult & 65535) is 5 or 32 or 1175 or 1176)
                {
                    Thread.Sleep(10 << attempt);
                }
            }

            _generation = unchecked(_generation + 1);
            MaintenanceStage?.Invoke("compact-data-replaced");
            _file = OpenFile();
            _file.Position = _file.Length;
            _primary.Load(replacement);
            replacement = null;
            _scanOffset = _file.Length;
            _checkpointOffset = _scanOffset;
            _verifiedValues.Clear();
            _verifiedCount = 0;
            _pages.Clear();
            _pageOrder.Clear();
            _cachedPages = 0;
            _version++;
        }
        finally
        {
            replacement?.Dispose();
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>等待已准入写入完成，保存检查点并释放文件、锁及缓存资源。</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    /// <summary>异步等待已准入写入完成，保存检查点并释放资源；重复释放共用同一关闭任务。</summary>
    /// <returns>完成时表示该操作结束的任务。</returns>
    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
            return new(_disposeTask ??= CloseAsync());
    }

    /// <summary>停止新请求，等待已准入写入结束，保存检查点并释放锁和文件资源。</summary>
    /// <returns>表示操作最终完成的任务。</returns>
    private async Task CloseAsync()
    {
        await DrainForCloseAsync().ConfigureAwait(false);
        EnterProcess();
        _gate.EnterWriteLock();
        try
        {
            _closed = true;
            try
            {
                if (_fault is null)
                {
                    RefreshShared(force: true);
                    TrySaveCheckpoint();
                }
            }
            finally
            {
                _primary.Dispose();
                _handle.Dispose();
                _file.Dispose();
                _coordView?.Dispose();
                _coordMap?.Dispose();
                _ownership?.Dispose();
            }
        }
        finally
        {
            _gate.ExitWriteLock();
            ExitProcess();
        }
    }

    /// <summary>关闭写入准入并排空已接受的队列，供上层在释放索引前等待提交完成。</summary>
    /// <returns>表示操作最终完成的任务。</returns>
    internal async ValueTask DrainForCloseAsync()
    {
        Interlocked.Exchange(ref _closing, 1);
        _queue.Writer.TryComplete();
        await (_writer.IsValueCreated ? _writer.Value : Task.CompletedTask).ConfigureAwait(false);
    }

    /// <summary>关闭写入准入并排空已接受的队列，供上层在释放索引前等待提交完成。</summary>
    /// <returns>表示操作最终完成的任务。</returns>
    ValueTask ISqlStorage.DrainForCloseAsync() => DrainForCloseAsync();
}
