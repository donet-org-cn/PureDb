using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.Threading.Channels;
using System.IO.MemoryMappedFiles;
namespace PureDb;

/// <summary>Concurrent file database with exclusive or coordinated shared access. Values on disk; indexes in memory.</summary>
public sealed partial class Database : IDisposable, IAsyncDisposable, ISqlStorage
{
    private const int HeaderSize = 16, FrameHeader = 16, OpHeader = 24, MaxFrame = 256 * 1024 * 1024;
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
    private readonly Dictionary<long,long> _keyVersions = [];
    private readonly Dictionary<long,int> _keyCounts = [];
    private bool _abandoned;
    private SafeFileHandle? _coordHandle;
    private MemoryMappedFile? _coordMap;
    private MemoryMappedViewAccessor? _coordView;
    private int _generation;
    internal long Version => _version;
    private FileStream _file = null!;
    private SafeFileHandle _handle = null!;
    private readonly ReaderWriterLockSlim _gate = new();
    private readonly PrimaryIndex _primary = new();
    private SortedSet<(long Key, long Id)>? _secondary;
    private readonly object _secondarySync = new();
    private SortedSet<(long Key, long Id)> Secondary
    {
        get
        {
            if (Volatile.Read(ref _secondary) is { } existing) return existing;
            lock (_secondarySync) return _secondary ??= new SortedSet<(long Key, long Id)>(_primary.Select(p => (p.Value.Key, p.Key)));
        }
    }
    private readonly Channel<Request> _queue;
    private readonly Lazy<Task> _writer;
    private int _closing;
    private bool _closed;
    private Exception? _fault;
    private readonly object _disposeSync = new();
    private Task? _disposeTask;
    private readonly record struct Entry(long Key, long Offset, int Length, uint Crc = 0, bool Verify = false);
    private sealed class Request(byte[]? frame, bool compact, TaskCompletionSource completion)
    {
        public byte[]? Frame = frame;
        public bool Compact = compact;
        public TaskCompletionSource Completion = completion;
        public Func<ISqlSnapshot, (Mutation[] Mutations, int Affected)>? Transform;
        public int Affected;
    }

    public Database(string path, DatabaseOptions? options = null)
    {
        _options = options ?? new();
        // This immutable facade reads state only under the existing database lease.
        _snapshot = new(this);
        if (_options.QueueCapacity < 1 || _options.MaxGroupRequests < 1 ||
            _options.MaxTransactionBytes < 4 || _options.MaxTransactionBytes > MaxFrame || !Enum.IsDefined(_options.Durability) || !Enum.IsDefined(_options.Access) || _options.CacheBytes < 0 || _options.CheckpointIntervalBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        _path = Path.GetFullPath(path);
        _primary.MaxHotEntries = (int)Math.Min(int.MaxValue, _options.CacheBytes / 128);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (_options.Access == DatabaseAccess.Shared)
        {
            var canonical = OperatingSystem.IsWindows() ? _path.ToUpperInvariant() : _path;
            _processMutex = new Mutex(false, "PureDb-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
        }
        // Exclusive instances retain a read handle on the coordination file; shared instances
        // retain write handles. FileShare compatibility rejects mixing access modes.
        _ownership = new FileStream(_path + ".lock", FileMode.OpenOrCreate,
            _processMutex is null ? FileAccess.Read : FileAccess.ReadWrite,
            _processMutex is null ? FileShare.None : FileShare.ReadWrite);
        if (_processMutex is not null) _coordHandle = _ownership.SafeFileHandle;
        EnterProcess();
        try { _file = OpenFile(); Recover(); PublishCoordination(); }
        catch { _primary.Dispose(); _handle?.Dispose(); _file?.Dispose(); _coordView?.Dispose(); _coordMap?.Dispose(); _ownership.Dispose(); throw; }
        finally { ExitProcess(); }
        _queue = Channel.CreateBounded<Request>(new BoundedChannelOptions(_options.QueueCapacity)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _writer = new(() => Task.Run(WriterLoop));
    }
    private FileStream OpenFile()
    {
        var file = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.RandomAccess);
        _handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        return file;
    }
    private void EnterProcess()
    {
        if (_processMutex is null) return;
        try { _processMutex.WaitOne(); _abandoned = false; } catch (AbandonedMutexException) { _abandoned = true; } // Recovery under the acquired lock follows.
    }
    private void ExitProcess() => _processMutex?.ReleaseMutex();
    // Advisory coordination stamp: never recovery truth. Constructors and writers always recover
    // the data file. Abandoned mutexes or invalid stamps force recovery before any cached read.
    private void PublishCoordination()
    {
        if (_coordHandle is null) return;
        Span<byte> stamp = stackalloc byte[24]; stamp.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(stamp, 0x32524450);
        BinaryPrimitives.WriteInt32LittleEndian(stamp[4..], _generation);
        BinaryPrimitives.WriteInt64LittleEndian(stamp[8..], _scanOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(stamp[16..], Crc32.Compute(stamp[..16]));
        if (_coordView is null)
        {
            _coordMap = MemoryMappedFile.CreateFromFile(_ownership!, null, 24, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: true);
            _coordView = _coordMap.CreateViewAccessor(0,24,MemoryMappedFileAccess.ReadWrite);
        }
        _coordView.Write(0,BinaryPrimitives.ReadInt64LittleEndian(stamp));
        _coordView.Write(8,_scanOffset); Thread.MemoryBarrier();
        _coordView.Write(16,BinaryPrimitives.ReadInt64LittleEndian(stamp[16..]));
    }
    private void ReadCoordination(Span<byte> stamp)
    {
        BinaryPrimitives.WriteInt64LittleEndian(stamp,_coordView!.ReadInt64(0));
        BinaryPrimitives.WriteInt64LittleEndian(stamp[8..],_coordView.ReadInt64(8));
        BinaryPrimitives.WriteInt64LittleEndian(stamp[16..],_coordView.ReadInt64(16));
    }
    private bool CoordinationMatches()
    {
        if (_abandoned) return false;
        Span<byte> stamp = stackalloc byte[24]; ReadCoordination(stamp);
        return BinaryPrimitives.ReadInt32LittleEndian(stamp) == 0x32524450 &&
            BinaryPrimitives.ReadUInt32LittleEndian(stamp[16..]) == Crc32.Compute(stamp[..16]) &&
            BinaryPrimitives.ReadInt32LittleEndian(stamp[4..]) == _generation &&
            BinaryPrimitives.ReadInt64LittleEndian(stamp[8..]) == _scanOffset;
    }
    private void RefreshShared(bool force = false)
    {
        if (_processMutex is null) return;
        bool sameFile = false;
        if (!_abandoned)
        {
            Span<byte> stamp = stackalloc byte[24]; ReadCoordination(stamp);
            if (BinaryPrimitives.ReadInt32LittleEndian(stamp) == 0x32524450 &&
                BinaryPrimitives.ReadUInt32LittleEndian(stamp[16..]) == Crc32.Compute(stamp[..16]) &&
                BinaryPrimitives.ReadInt32LittleEndian(stamp[4..]) == _generation)
            {
                sameFile = true;
                if (!force && BinaryPrimitives.ReadInt64LittleEndian(stamp[8..]) == _scanOffset) return;
            }
        }
        // The named mutex covers file replacement and stamp publication. A crash in
        // between abandons it, forcing a fresh path lookup. Writers always inspect
        // the real log, even when the advisory stamp says nothing changed.
        // Discard a previous read-ahead buffer before observing another process's
        // tail. Keeping the buffer during one recovery coalesces tiny frame reads.
        _file.Flush(false);
        if (!sameFile) { _handle.Dispose(); _file.Dispose(); _file = OpenFile(); }
        else if (force && _file.Length == _scanOffset) { _file.Position = _scanOffset; return; }
        _file.Position = 0; Recover(); PublishCoordination();
    }
    private SafeFileHandle ReadHandle()
    {
        if (_handle.IsClosed) _handle = File.OpenHandle(_path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        return _handle;
    }
    private readonly ref struct ReadLease
    {
        private readonly Database _db;
        private readonly bool _readLock;
        public ReadLease(Database db)
        {
            _db = db; _readLock = true; db._gate.EnterReadLock();
            try
            {
                db.CheckReadable();
                // A stable stamp linearizes a cached read before any concurrent
                // publication. Append-only values and retained delete-share handles
                // keep that snapshot readable even during another process's compact.
                if (db._processMutex is null || db.CoordinationMatches()) return;
            }
            catch { db._gate.ExitReadLock(); throw; }
            db._gate.ExitReadLock(); db.EnterProcess(); db._gate.EnterWriteLock(); _readLock = false;
            try { db.CheckReadable(); db.RefreshShared(); }
            catch { db._gate.ExitWriteLock(); db.ExitProcess(); throw; }
        }
        public void Dispose()
        {
            if (_readLock) _db._gate.ExitReadLock();
            else { _db._gate.ExitWriteLock(); _db.ExitProcess(); }
        }
    }
    T ISqlStorage.Read<T>(Func<ISqlSnapshot, T> action) => Read(action);
    internal T Read<T>(Func<ISqlSnapshot,T> action)
    { using var lease=new ReadLease(this);return action(_snapshot); }
    internal T Read<T, TState>(Func<ISqlSnapshot, TState, T> action, TState state)
    { using var lease = new ReadLease(this); return action(_snapshot, state); }
    internal sealed class Snapshot(Database db) : ISqlSnapshot
    {
        public bool HasPersistentIndexes => true;
        public string Path => db._path;
        public int Generation => db._generation;
        public long Length => db._scanOffset;
        public long SourceTime => File.GetLastWriteTimeUtc(db._path).Ticks;
        public DiskIndex.Item IndexItem(long id, long key)
        { var e = db._primary[id]; return new(key, id, e.Offset, e.Length, e.Crc); }
        public ReadOnlySpan<byte> IndexValue(DiskIndex.Item entry)
        {
            if (entry.Offset < HeaderSize || entry.Length < 0 || entry.Offset > db._scanOffset - entry.Length) throw new InvalidDataException("Invalid persistent record address.");
            return db.ReadValue(new(0, entry.Offset, entry.Length, entry.Crc, true));
        }
        public ReadOnlyMemory<byte> IndexMemory(DiskIndex.Item entry) => db.ReadMemory(new(0, entry.Offset, entry.Length, entry.Crc, true), entry.Id);
        public IEnumerable<DiskIndex.Item> TableEntries(int tableId)
        {
            long min = (long)tableId << 40, max = min + ((1L << 40) - 1);
            return TableEntriesRange(tableId, min, max);
        }
        public IEnumerable<DiskIndex.Item> TableEntriesRange(int tableId, long min, long max)
        {
            foreach (var pair in db._primary.Range(min, max))
                if (pair.Value.Key == tableId) yield return new(tableId, pair.Key, pair.Value.Offset, pair.Value.Length, pair.Value.Crc);
        }
        public long Version => db.Version;
        public long KeyVersion(long key) => db._keyVersions.GetValueOrDefault(key, db._resetVersion);
        public int CountKey(long key) => db._keyCounts.GetValueOrDefault(key);
        public IEnumerable<long> Ids(long key)
        {
            if (CountKey(key) == 0) yield break;
            if (key > 0 && key < 1 << 23)
            { foreach (var entry in TableEntries((int)key)) yield return entry.Id; yield break; }
            foreach (var pair in db.Secondary.GetViewBetween((key, long.MinValue), (key, long.MaxValue))) yield return pair.Id;
        }
        public ReadOnlySpan<byte> Value(long id) => db._primary.TryGetValue(id, out var entry) ? db.ReadValue(entry, id) : default;
        public ReadOnlyMemory<byte> ValueMemory(long id) => db._primary.TryGetValue(id, out var entry) ? db.ReadMemory(entry, id) : default;
        public long Count => db._primary.Count;
        public DbRecord? Get(long id) => db._primary.TryGetValue(id,out var e)?db.ReadRecord(id,e):null;
        public bool Contains(long id) => db._primary.TryGetValue(id, out _);
        public IEnumerable<DbRecord> Range(long min,long max)
        {
            if(min>max)yield break;
            foreach(var pair in db.Secondary.GetViewBetween((min,long.MinValue),(max,long.MaxValue)))
                yield return db.ReadRecord(pair.Id,db._primary[pair.Id]);
        }
    }
    public long Count {get{using var lease=new ReadLease(this);return _primary.Count;}}
    public DbRecord? Get(long id)
    {using var lease=new ReadLease(this);return _primary.TryGetValue(id,out var entry)?ReadRecord(id,entry):null;}
    public IReadOnlyList<DbRecord> Query(long min,long max,int limit=1000)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);using var lease=new ReadLease(this);
        var result=new List<DbRecord>(Math.Min(128,limit));if(min>max||limit==0)return result;
        foreach(var pair in Secondary.GetViewBetween((min,long.MinValue),(max,long.MaxValue)))
        {result.Add(ReadRecord(pair.Id,_primary[pair.Id]));if(result.Count==limit)break;}
        return result;
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private byte[] Page(long page,int required)
    {
        if(_pages.TryGetValue(page,out var bytes)&&bytes.Length>=required)return bytes;
        return LoadPage(page);
    }
    private byte[] LoadPage(long page)
    {
        int available=(int)Math.Min(PageSize,_scanOffset-page*PageSize);
        var bytes=new byte[available];ReadAt(ReadHandle(),bytes,page*PageSize);
        if(_pages.TryAdd(page,bytes))
        {
            Interlocked.Increment(ref _cachedPages);
            _pageOrder.Enqueue(page);
            while(Volatile.Read(ref _cachedPages)>_options.CacheBytes/PageSize&&_pageOrder.TryDequeue(out var victim))
                if (_pages.TryRemove(victim,out _)) Interlocked.Decrement(ref _cachedPages);
        }
        else if(_pages.TryGetValue(page,out var old)&&old.Length<bytes.Length)_pages.TryUpdate(page,bytes,old);
        return bytes;
    }
    private DbRecord ReadRecord(long id, Entry e)
    {
        var value = new byte[e.Length];
        long offset = e.Offset; int copied = 0;
        while (copied < value.Length)
        {
            long page = offset / PageSize;
            int within = (int)(offset % PageSize), count = Math.Min(PageSize - within, value.Length - copied);
            // Cached tail prefixes remain immutable. Appended records request a larger prefix
            // only after publication; readers never cache bytes beyond the committed offset.
            if (_options.CacheBytes >= PageSize) Page(page,within+count).AsSpan(within,count).CopyTo(value.AsSpan(copied));
            else ReadAt(ReadHandle(), value.AsSpan(copied,count), offset);
            copied += count; offset += count;
        }
        VerifyValue(e, value, id); return new(id, e.Key, value);
    }
    private ReadOnlySpan<byte> ReadValue(Entry e, long? id = null)
    {
        long page = e.Offset / PageSize; int within = (int)(e.Offset % PageSize);
        if (_options.CacheBytes >= PageSize && within + e.Length <= PageSize)
        { var value = Page(page,within+e.Length).AsSpan(within,e.Length); VerifyValue(e, value, id); return value; }
        return ReadRecord(id ?? 0, e).Value;
    }
    private ReadOnlyMemory<byte> ReadMemory(Entry e, long? id = null)
    {
        long page = e.Offset / PageSize; int within = (int)(e.Offset % PageSize);
        if (_options.CacheBytes >= PageSize && within + e.Length <= PageSize)
        { var value = Page(page,within+e.Length).AsMemory(within,e.Length); VerifyValue(e, value.Span, id); return value; }
        return ReadRecord(id ?? 0,e).Value;
    }
    public ValueTask PutAsync(long id, long indexKey, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default) =>
        WriteAsync([Mutation.Put(id, indexKey, value)], cancellationToken);
    public ValueTask DeleteAsync(long id, CancellationToken cancellationToken = default) => WriteAsync([Mutation.Delete(id)], cancellationToken);

    /// <summary>Copies input before enqueue. Cancellation applies to admission only; admitted transactions await their definitive result.</summary>
    public ValueTask WriteAsync(IReadOnlyList<Mutation> mutations, CancellationToken cancellationToken = default)
    {
        CheckWritable(); cancellationToken.ThrowIfCancellationRequested();
        if (mutations.Count == 0) return ValueTask.CompletedTask;
        return Enqueue(new(Encode(mutations), false, new(TaskCreationOptions.RunContinuationsAsynchronously)), cancellationToken);
    }
    private byte[] Encode(IReadOnlyList<Mutation> mutations)
    {
        long size = 4;
        foreach (var m in mutations) size = checked(size + OpHeader + (m.IsDelete ? 0 : m.Value.Length));
        if (size > _options.MaxTransactionBytes) throw new ArgumentException("Transaction exceeds MaxTransactionBytes.", nameof(mutations));
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
            if (!m.IsDelete) m.Value.Span.CopyTo(frame.AsSpan(p + OpHeader));
            p += OpHeader + (m.IsDelete ? 0 : m.Value.Length);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), Crc32.Compute(frame.AsSpan(FrameHeader)));
        return frame;
    }
    ValueTask<int> ISqlStorage.TransformAsync(Func<ISqlSnapshot, (Mutation[] Mutations, int Affected)> transform, CancellationToken token) => TransformAsync(transform, token);
    internal async ValueTask<int> TransformAsync(Func<ISqlSnapshot, (Mutation[] Mutations, int Affected)> transform, CancellationToken token)
    {
        CheckWritable();
        if (_options.Durability == Durability.Buffered) return TransformBuffered(transform, token);
        var request = new Request(null, false, new(TaskCreationOptions.RunContinuationsAsynchronously)) { Transform = transform };
        await Enqueue(request, token).ConfigureAwait(false); return request.Affected;
    }
    private int TransformBuffered(Func<ISqlSnapshot, (Mutation[] Mutations, int Affected)> transform, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); EnterProcess(); _gate.EnterWriteLock();
        try
        {
            CheckWritable(); token.ThrowIfCancellationRequested();
            try { RefreshShared(force: true); }
            catch (Exception error) { Volatile.Write(ref _fault, error); throw; }
            // SQL constraints and encoding fail before any bytes are appended.
            var result = transform(_snapshot);
            if (result.Mutations.Length == 0) return result.Affected;
            byte[] frame = Encode(result.Mutations);
            try
            {
                long offset = _file.Position; _file.Write(frame); _file.Flush(false);
                Apply(frame, offset); _scanOffset = offset + frame.Length; PublishCoordination(); MaybeCheckpoint();
            }
            catch (Exception error) { Volatile.Write(ref _fault, error); throw; }
            return result.Affected;
        }
        finally { _gate.ExitWriteLock(); ExitProcess(); }
    }
    public ValueTask CompactAsync(CancellationToken cancellationToken = default)
    {
        CheckWritable(); return Enqueue(new(null, true, new(TaskCreationOptions.RunContinuationsAsynchronously)), cancellationToken);
    }
    private async ValueTask Enqueue(Request request, CancellationToken token)
    {
        // Read-only opens do not create a writer task. Start before admission so
        // disposal can either drain every admitted request or reject it.
        _ = _writer.Value;
        await _queue.Writer.WriteAsync(request, token).ConfigureAwait(false);
        await request.Completion.Task.ConfigureAwait(false);
    }
    private void CheckWritable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing) != 0, this);
        if (Volatile.Read(ref _fault) is { } fault) throw new IOException("Writer failed; reopen the database.", fault);
    }
    private void CheckReadable()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_fault is { } fault) throw new IOException("Writer failed; reopen the database.", fault);
    }
    private async Task WriterLoop()
    {
        var group = new List<Request>();
        try
        {
            await foreach (var first in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                group.Clear(); group.Add(first);
                if (!first.Compact)
                    while (group.Count < _options.MaxGroupRequests && _queue.Reader.TryPeek(out var next) && !next.Compact)
                    { if (_queue.Reader.TryRead(out var r)) group.Add(r); }
                EnterProcess(); _gate.EnterWriteLock();
                try
                {
                    RefreshShared(force: true);
                    if (first.Compact) CompactCore();
                    else if (group.Any(r => r.Transform is not null))
                    {
                        bool written = false;
                        foreach (var r in group)
                        {
                            if (r.Transform is not null)
                            {
                                try
                                {
                                    var result = r.Transform(_snapshot); r.Affected = result.Affected;
                                    r.Frame = result.Mutations.Length == 0 ? null : Encode(result.Mutations);
                                }
                                catch (Exception ex) { r.Completion.SetException(ex); continue; }
                            }
                            if (r.Frame is not null)
                            {
                                long offset = _file.Position; _file.Write(r.Frame); _file.Flush(false);
                                Apply(r.Frame, offset); _scanOffset = offset + r.Frame.Length; written = true;
                            }
                        }
                        if (written) _file.Flush(_options.Durability == Durability.Full);
                    }
                    else
                    {
                        long offset = _file.Position;
                        bool unlock = _processMutex is null && _options.Durability == Durability.Full;
                        if (unlock) _gate.ExitWriteLock();
                        try { foreach (var r in group) if (r.Frame is not null) _file.Write(r.Frame); _file.Flush(_options.Durability == Durability.Full); }
                        finally { if (unlock) _gate.EnterWriteLock(); }
                        foreach (var r in group) if (r.Frame is not null) { Apply(r.Frame, offset); offset += r.Frame.Length; }
                        _scanOffset = offset;
                    }
                    PublishCoordination();
                    MaybeCheckpoint();
                }
                finally { _gate.ExitWriteLock(); ExitProcess(); }
                foreach (var r in group) r.Completion.TrySetResult();
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _fault, ex); _queue.Writer.TryComplete(ex);
            foreach (var r in group) r.Completion.TrySetException(ex);
            while (_queue.Reader.TryRead(out var r)) r.Completion.TrySetException(ex);
        }
    }
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
                _secondary?.Remove((old.Key, id)); _keyCounts[old.Key]--; _keyVersions[old.Key] = _version;
            }
            if (length >= 0)
            {
                _primary.SetKnown(id, new(key, offset + p + OpHeader, length, Crc32.Compute(frame.Slice(p + OpHeader, length))), exists);
                if (!exists || old.Key != key) { _secondary?.Add((key, id)); _keyCounts[key] = _keyCounts.GetValueOrDefault(key) + 1; }
                _keyVersions[key] = _version;
            }
            else if (exists) _primary.RemoveKnown(id);
            p += OpHeader + Math.Max(0, length);
        }
    }
    private void WriteHeader(Stream stream, int? generation = null)
    {
        Span<byte> header = stackalloc byte[HeaderSize]; header.Clear(); Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], generation ?? _generation); stream.Write(header);
    }
    private void Recover()
    {
        long fileLength = _file.Length;
        if (fileLength == 0) { _generation = Random.Shared.Next(1, int.MaxValue); WriteHeader(_file); _file.Flush(true); _scanOffset = HeaderSize; return; }
        if (fileLength < HeaderSize) throw new InvalidDataException("Incomplete database header.");
        Span<byte> header = stackalloc byte[HeaderSize]; _file.ReadExactly(header);
        if (!header[..8].SequenceEqual(Magic) || BinaryPrimitives.ReadInt32LittleEndian(header[8..]) != 1)
            throw new InvalidDataException("Unsupported database format.");
        int generation = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        if (_scanOffset == 0 || generation != _generation || fileLength < _scanOffset)
        {
            _primary.Clear(); _secondary = null; _pages.Clear(); _pageOrder.Clear(); _cachedPages = 0; _keyCounts.Clear(); _keyVersions.Clear(); _resetVersion = ++_version;
            _checkpointOffset = 0; _verifiedValues.Clear();
            _scanOffset = HeaderSize; _generation = generation; _version++;
            if (!_options.VerifyOnOpen) TryLoadCheckpoint(fileLength);
        }
        long offset = _scanOffset; _file.Position = offset;
        while (offset < fileLength)
        {
            if (fileLength - offset < FrameHeader) break;
            byte[] h = new byte[FrameHeader]; _file.ReadExactly(h);
            int length = BinaryPrimitives.ReadInt32LittleEndian(h);
            if (length < 4 || length > MaxFrame || BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(8)) != ~length)
                throw new InvalidDataException($"Invalid frame header at {offset}.");
            if (fileLength - offset - FrameHeader < length) break;
            byte[] frame = new byte[FrameHeader + length]; h.CopyTo(frame, 0); _file.ReadExactly(frame.AsSpan(FrameHeader));
            if (Crc32.Compute(frame.AsSpan(FrameHeader)) != BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4)))
                throw new InvalidDataException($"Checksum mismatch at {offset}; refusing silent data loss.");
            ValidateFrame(frame); Apply(frame, offset); offset += frame.Length;
        }
        if (offset != fileLength) { _file.SetLength(offset); _file.Flush(true); }
        _file.Position = offset; _scanOffset = offset;
    }
    private static void ValidateFrame(ReadOnlySpan<byte> frame)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(frame[FrameHeader..]);
        if (count < 0 || count > (frame.Length - FrameHeader - 4) / OpHeader) throw new InvalidDataException("Invalid operation count.");
        int p = FrameHeader + 4;
        for (int i = 0; i < count; i++)
        {
            if (frame.Length - p < OpHeader) throw new InvalidDataException("Truncated operation.");
            int length = BinaryPrimitives.ReadInt32LittleEndian(frame[(p + 16)..]);
            if (length < -1 || length > frame.Length - p - OpHeader) throw new InvalidDataException("Invalid value length.");
            p += OpHeader + Math.Max(0, length);
        }
        if (p != frame.Length) throw new InvalidDataException("Unexpected frame bytes.");
    }
    private static void ReadAt(SafeFileHandle handle, Span<byte> destination, long offset)
    {
        while (!destination.IsEmpty)
        {
            int n = RandomAccess.Read(handle, destination, offset);
            if (n == 0) throw new EndOfStreamException();
            destination = destination[n..]; offset += n;
        }
    }
    private void CompactCore()
    {
        string temp = _path + ".compact";
        try
        {
            var entries = new Dictionary<long, Entry>();
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            {
                WriteHeader(output, unchecked(_generation + 1));
                byte[] frame = new byte[1024 * 1024]; int position = FrameHeader + 4, count = 0;
                void FlushFrame()
                {
                    if (count == 0) return;
                    frame.AsSpan(0, FrameHeader).Clear();
                    int length = position - FrameHeader;
                    BinaryPrimitives.WriteInt32LittleEndian(frame, length);
                    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), ~length);
                    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(FrameHeader), count);
                    BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), Crc32.Compute(frame.AsSpan(FrameHeader, length)));
                    output.Write(frame.AsSpan(0, position)); position = FrameHeader + 4; count = 0;
                }
                foreach (var (id, entry) in _primary)
                {
                    int needed = checked(OpHeader + entry.Length);
                    if (position + needed > frame.Length) { FlushFrame(); if (position + needed > frame.Length) frame = new byte[position + needed]; }
                    var value = ReadValue(entry, id);
                    frame.AsSpan(position, OpHeader).Clear();
                    BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(position), id);
                    BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(position + 8), entry.Key);
                    BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(position + 16), entry.Length);
                    value.CopyTo(frame.AsSpan(position + OpHeader));
                    entries[id] = new(entry.Key, output.Position + position + OpHeader, entry.Length, entry.Crc);
                    position += needed; count++;
                }
                FlushFrame();
                output.Flush(true);
            }
            _handle.Dispose(); _file.Dispose();
            for (int attempt = 0; ; attempt++)
            {
                try { File.Replace(temp, _path, null); break; }
                catch (IOException error) when (OperatingSystem.IsWindows() && attempt < 5 && (error.HResult & 65535) is 5 or 32 or 1175 or 1176) { Thread.Sleep(10 << attempt); }
            }
            _generation = unchecked(_generation + 1);
            _file = OpenFile(); _file.Position = _file.Length;
            _primary.Clear(); foreach (var item in entries) _primary.SetKnown(item.Key, item.Value, false);
            _scanOffset = _file.Length; _checkpointOffset = 0; _verifiedValues.Clear(); _pages.Clear(); _pageOrder.Clear(); _cachedPages = 0; _version++;
        }
        finally { }
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public ValueTask DisposeAsync() { lock (_disposeSync) return new(_disposeTask ??= CloseAsync()); }
    private async Task CloseAsync()
    {
        await DrainForCloseAsync().ConfigureAwait(false);
        EnterProcess(); _gate.EnterWriteLock();
        try
        {
            _closed = true;
            try { if (_fault is null) { RefreshShared(force: true); TrySaveCheckpoint(); } }
            finally { _primary.Dispose(); _handle.Dispose(); _file.Dispose(); _coordView?.Dispose(); _coordMap?.Dispose(); _ownership?.Dispose(); }
        }
        finally { _gate.ExitWriteLock(); ExitProcess(); }
    }
    internal async ValueTask DrainForCloseAsync()
    { Interlocked.Exchange(ref _closing, 1); _queue.Writer.TryComplete(); await (_writer.IsValueCreated ? _writer.Value : Task.CompletedTask).ConfigureAwait(false); }
    ValueTask ISqlStorage.DrainForCloseAsync() => DrainForCloseAsync();
}
