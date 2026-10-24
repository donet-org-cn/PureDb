using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
namespace PureDb;

public sealed partial class Database
{
    private long _checkpointOffset;
    private readonly ConcurrentDictionary<long, byte> _verifiedValues = new();
    private int _verifiedCount;
    private void VerifyValue(Entry entry, ReadOnlySpan<byte> value, long? id = null)
    {
        if (!entry.Verify) return;
        bool known = _verifiedValues.ContainsKey(entry.Offset);
        if (!known && Crc32.Compute(value) != entry.Crc) throw new InvalidDataException($"Value checksum mismatch at {entry.Offset}.");
        // A hot primary page already has one atomic verification flag per record;
        // do not also allocate a dictionary node for the same immutable value.
        if (id is long key && _primary.RememberVerified(key, entry)) return;
        if (!known)
        {
            if (Volatile.Read(ref _verifiedCount) > 16384) { _verifiedValues.Clear(); Interlocked.Exchange(ref _verifiedCount, 0); }
            if (_verifiedValues.TryAdd(entry.Offset, 0)) Interlocked.Increment(ref _verifiedCount);
        }
    }
    private void TryLoadCheckpoint(long length)
    {
        var index = DiskIndex.Open(_path + ".pidx", _generation, length, File.GetLastWriteTimeUtc(_path).Ticks);
        if (index is null) return;
        var bytes = index.Metadata.AsSpan();
        if (bytes.Length % 16 != 0) { index.Dispose(); return; }
        for (int p = 0; p < bytes.Length; p += 16)
        {
            long key = BinaryPrimitives.ReadInt64LittleEndian(bytes[p..]); int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[(p + 8)..]);
            if (count <= 0 || _keyCounts.ContainsKey(key)) { _keyCounts.Clear(); index.Dispose(); return; }
            _keyCounts[key] = count; _keyVersions[key] = _version;
        }
        _primary.Load(index); _scanOffset = _checkpointOffset = index.SourceLength;
    }
    private void SaveCheckpoint()
    {
        if (_scanOffset == _checkpointOffset && File.Exists(_path + ".pidx")) return;
        _file.Flush(true); // The index may only reference a durable data prefix.
        var metadata = new byte[_keyCounts.Count(p => p.Value > 0) * 16]; int p = 0;
        foreach (var pair in _keyCounts.Where(p => p.Value > 0))
        { BinaryPrimitives.WriteInt64LittleEndian(metadata.AsSpan(p), pair.Key); BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(p + 8), pair.Value); p += 16; }
        var entries = _primary.OrderBy(p => p.Key).Select(p => new DiskIndex.Item(p.Key, p.Value.Key, p.Value.Offset, p.Value.Length, p.Value.Crc)).ToArray();
        DiskIndex.Write(_path + ".pidx", _generation, _scanOffset, File.GetLastWriteTimeUtc(_path).Ticks, metadata, entries);
        _checkpointOffset = _scanOffset;
    }
    public Exception? CheckpointError { get; private set; }
    private void TrySaveCheckpoint()
    {
        try { SaveCheckpoint(); CheckpointError = null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { CheckpointError = error; }
    }
    private void MaybeCheckpoint()
    { if (_options.CheckpointIntervalBytes > 0 && _scanOffset - _checkpointOffset >= _options.CheckpointIntervalBytes) TrySaveCheckpoint(); }
    /// <summary>Persist the current primary index. Commits remain recoverable if the sidecar is missing.</summary>
    public ValueTask CheckpointAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); CheckWritable(); EnterProcess(); _gate.EnterWriteLock();
        try { RefreshShared(force: true); SaveCheckpoint(); return ValueTask.CompletedTask; }
        finally { _gate.ExitWriteLock(); ExitProcess(); }
    }
    /// <summary>Verify all committed frame checksums, including overwritten/deleted historical records.</summary>
    public void VerifyIntegrity(CancellationToken cancellationToken = default)
    {
        using var lease = new ReadLease(this); long offset = HeaderSize;
        while (offset < _scanOffset)
        {
            cancellationToken.ThrowIfCancellationRequested(); var h = new byte[FrameHeader]; ReadAt(_handle, h, offset);
            int size = BinaryPrimitives.ReadInt32LittleEndian(h);
            if (size < 4 || size > MaxFrame || BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(8)) != ~size || size > _scanOffset - offset - FrameHeader) throw new InvalidDataException("Invalid transaction frame.");
            var frame = new byte[FrameHeader + size]; h.CopyTo(frame, 0); ReadAt(_handle, frame.AsSpan(FrameHeader), offset + FrameHeader);
            if (Crc32.Compute(frame.AsSpan(FrameHeader)) != BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4))) throw new InvalidDataException($"Checksum mismatch at {offset}.");
            ValidateFrame(frame); offset += frame.Length;
        }
    }
    private sealed class PrimaryIndex : IEnumerable<KeyValuePair<long, Entry>>, IDisposable
    {
        private DiskIndex? _base;
        private readonly Dictionary<long, Entry?> _changes = [];
        // A page stores immutable entries inline; its flags publish them only after
        // initialization. Readers never observe a partially written Entry struct.
        private sealed class HotPage
        {
            internal readonly Entry[] Entries = new Entry[256];
            internal readonly int[] Flags = new int[256];
        }
        private readonly ConcurrentDictionary<long, HotPage> _hotPages = new();
        private readonly object _hotGate = new();
        private int _hotPageCount;
        internal int MaxHotEntries = 524288;
        public bool RememberVerified(long id, Entry entry)
        {
            if (_hotPages.TryGetValue(id >> 8, out var page))
            {
                int slot = (int)(id & 255);
                if (Volatile.Read(ref page.Flags[slot]) != 0 && page.Entries[slot] == entry)
                { Interlocked.CompareExchange(ref page.Flags[slot], 2, 1); return true; }
            }
            return false;
        }
        private int _count;
        public int Count => _count;
        public void Load(DiskIndex index) { Clear(); _base = index; _count = index.Count; }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        public bool TryGetValue(long id, out Entry value)
        {
            if (_changes.Count > 0 && _changes.TryGetValue(id, out var change)) { value = change.GetValueOrDefault(); return change.HasValue; }
            if (_hotPages.TryGetValue(id >> 8, out var hot))
            {
                int slot = (int)(id & 255), flag = Volatile.Read(ref hot.Flags[slot]);
                if (flag != 0)
                {
                    var entry = hot.Entries[slot];
                    value = new(entry.Key, entry.Offset, entry.Length, entry.Crc, flag == 1); return true;
                }
            }
            return FindBase(id, hot, out value);
        }
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
            value = default; return false;
        }
        private void Remember(long id, Entry entry, HotPage? page)
        {
            if (page is null)
            {
                if (Volatile.Read(ref _hotPageCount) >= MaxHotEntries / 256) return;
                lock (_hotGate)
                {
                    if (!_hotPages.TryGetValue(id >> 8, out page))
                    {
                        if (_hotPageCount >= MaxHotEntries / 256) return;
                        page = new(); _hotPages[id >> 8] = page; _hotPageCount++;
                    }
                }
            }
            int slot = (int)(id & 255);
            lock (page)
                if (page.Flags[slot] == 0) { page.Entries[slot] = entry; Volatile.Write(ref page.Flags[slot], entry.Verify ? 1 : 2); }
        }
        public Entry this[long id]
        {
            get => TryGetValue(id, out var entry) ? entry : throw new KeyNotFoundException();
            set { if (!TryGetValue(id, out _)) _count++; _changes[id] = value; }
        }
        public void Add(long id, Entry entry) => this[id] = entry;
        public void SetKnown(long id, Entry entry, bool existed)
        { if (!existed) _count++; _changes[id] = entry; }
        public void RemoveKnown(long id) { _changes[id] = null; _count--; }
        public bool Remove(long id) { if (!TryGetValue(id, out _)) return false; _changes[id] = null; _count--; return true; }
        public IEnumerable<KeyValuePair<long, Entry>> Range(long min, long max)
        {
            var changes = _changes.Where(p => p.Key >= min && p.Key <= max && p.Value.HasValue).OrderBy(p => p.Key).ToArray(); int at = 0;
            if (_base is not null)
            {
                int begin = _base.Bound(min, false), end = _base.Bound(max, true);
                for (int i = begin; i < end; i++)
                {
                    var e = _base.At(i);
                    while (at < changes.Length && changes[at].Key < e.Key) { var c = changes[at++]; yield return new(c.Key, c.Value!.Value); }
                    if (!_changes.ContainsKey(e.Key)) yield return new(e.Key, new(e.Id, e.Offset, e.Length, e.Crc, true));
                }
            }
            while (at < changes.Length) { var c = changes[at++]; yield return new(c.Key, c.Value!.Value); }
        }
        public void Clear() { _base?.Dispose(); _base = null; _changes.Clear(); _hotPages.Clear(); _hotPageCount = 0; _count = 0; }
        public IEnumerator<KeyValuePair<long, Entry>> GetEnumerator()
        {
            if (_base is not null) for (int i = 0; i < _base.Count; i++)
            { var e = _base.At(i); if (!_changes.ContainsKey(e.Key)) yield return new(e.Key, new(e.Id, e.Offset, e.Length, e.Crc, true)); }
            foreach (var pair in _changes) if (pair.Value is Entry e) yield return new(pair.Key, e);
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() => Clear();
    }
}
