using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
namespace PureDb;

// Immutable, rebuildable index. Read a header at open, then checksum pages on demand.
// Data files remain the recovery authority; replacing a sidecar is atomic.
internal sealed class DiskIndex : IDisposable
{
    internal readonly record struct Item(long Key, long Id, long Offset, int Length, uint Crc);
    private const int PageSize = 4096, PerPage = 127;
    private readonly SafeFileHandle _handle;
    private readonly ConcurrentDictionary<int, byte[]> _pages = new();
    private readonly ConcurrentQueue<int> _order = new();
    private int _cachedPageCount;
    private readonly long _start;
    public int Count { get; }
    public int Generation { get; }
    public long SourceLength { get; }
    public long SourceTime { get; }
    public byte[] Metadata { get; }
    public long MinKey { get; }
    public long MaxKey { get; }
    private DiskIndex(SafeFileHandle handle, byte[] header, byte[] metadata)
    {
        _handle = handle; Count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        Generation = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        SourceLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
        SourceTime = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24));
        _start = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40)); Metadata = metadata;
        MinKey = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(48)); MaxKey = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(56));
    }
    public static DiskIndex? Open(string path, int generation, long length, long? time = null)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
            var h = new byte[PageSize]; ReadExactly(handle, h, 0);
            if (!h.AsSpan(0, 8).SequenceEqual("PDBIDX03"u8) || Crc32.Compute(h.AsSpan(0, 4092)) != BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4092))) return null;
            int gen = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(8)), count = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(12));
            long source = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(16)), stamp = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(24));
            int size = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(32)); long start = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(40));
            if (gen != generation || source < 16 || source > length || time is long t && source == length && t != stamp || count < 0 || size < 0 || size > 64 * 1024 * 1024 || start != PageSize + ((long)size + 4095) / 4096 * 4096 ||
                RandomAccess.GetLength(handle) != start + ((long)count + PerPage - 1) / PerPage * PageSize) return null;
            var metadata = new byte[size]; ReadExactly(handle, metadata, PageSize);
            if (Crc32.Compute(metadata) != BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(36))) return null;
            var result = new DiskIndex(handle, h, metadata); handle = null; return result;
        }
        catch (IOException) { return null; }
        finally { handle?.Dispose(); }
    }
    public Item At(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal); if (ordinal >= Count) throw new ArgumentOutOfRangeException(nameof(ordinal));
        int page = ordinal / PerPage;
        if (!_pages.TryGetValue(page, out var bytes))
        {
            bytes = new byte[PageSize]; ReadExactly(_handle, bytes, _start + (long)page * PageSize);
            if (Crc32.Compute(bytes.AsSpan(0, 4092)) != BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4092)))
                throw new InvalidDataException("Persistent index checksum mismatch. Remove the .pidx/.sidx sidecar to rebuild it from the data log.");
            if (_pages.TryAdd(page, bytes))
            {
                Interlocked.Increment(ref _cachedPageCount);
                _order.Enqueue(page);
                while (Volatile.Read(ref _cachedPageCount) > 128 && _order.TryDequeue(out var victim))
                    if (_pages.TryRemove(victim, out _)) Interlocked.Decrement(ref _cachedPageCount);
            }
        }
        var b = bytes.AsSpan(ordinal % PerPage * 32, 32);
        return new(BinaryPrimitives.ReadInt64LittleEndian(b), BinaryPrimitives.ReadInt64LittleEndian(b[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(b[16..]), BinaryPrimitives.ReadInt32LittleEndian(b[24..]), BinaryPrimitives.ReadUInt32LittleEndian(b[28..]));
    }
    public int Bound(long key, bool upper)
    {
        if (Count == 0 || key < MinKey || !upper && key == MinKey) return 0;
        if (key > MaxKey || upper && key == MaxKey) return Count;
        int lo = 0, hi = Count;
        // A verified probe only narrows the binary-search interval. Dense keys and
        // ordinary duplicate runs usually need one or two pages; skewed keys keep
        // a bounded number of extra probes before the exact binary-search fallback.
        double span = (double)MaxKey - MinKey;
        if (Count >= PerPage * 2 && span >= Count / 1024d && span <= (double)Count * 16)
        {
            int guess = (int)Math.Clamp(((double)key - MinKey) / span * (Count - 1), 0, Count - 1);
            bool Before(long value) => value < key || upper && value == key;
            if (Before(At(guess).Key))
            {
                lo = guess + 1;
                for (int step = 1; step <= 128 && lo < Count; step <<= 1)
                {
                    int probe = (int)Math.Min(Count - 1L, (long)guess + step);
                    if (!Before(At(probe).Key)) { hi = probe; break; }
                    lo = probe + 1;
                }
            }
            else
            {
                hi = guess;
                for (int step = 1; step <= 128 && hi > 0; step <<= 1)
                {
                    int probe = Math.Max(0, guess - step);
                    if (Before(At(probe).Key)) { lo = probe + 1; break; }
                    hi = probe;
                }
            }
        }
        while (lo < hi) { int mid = lo + (hi - lo) / 2; long value = At(mid).Key; if (value < key || upper && value == key) lo = mid + 1; else hi = mid; }
        return lo;
    }
    private static void ReadExactly(SafeFileHandle handle, Span<byte> bytes, long offset)
    {
        while (!bytes.IsEmpty)
        { int n = RandomAccess.Read(handle, bytes, offset); if (n == 0) throw new EndOfStreamException(); bytes = bytes[n..]; offset += n; }
    }
    public static void Write(string path, int generation, long length, long time, byte[] metadata, IReadOnlyList<Item> entries)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            {
                var header = new byte[PageSize]; "PDBIDX03"u8.CopyTo(header);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), generation); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), entries.Count);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), length); BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), time);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), metadata.Length); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), Crc32.Compute(metadata));
                long start = PageSize + ((long)metadata.Length + 4095) / 4096 * 4096;
                if (entries.Count > 0) { BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(48), entries[0].Key); BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(56), entries[^1].Key); }
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40), start); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4092), Crc32.Compute(header.AsSpan(0, 4092)));
                file.Write(header); file.Write(metadata); file.SetLength(start); file.Position = start;
                var page = new byte[PageSize];
                for (int i = 0; i < entries.Count;)
                {
                    page.AsSpan().Clear();
                    for (int j = 0; j < PerPage && i < entries.Count; j++, i++)
                    {
                        var e = entries[i]; var b = page.AsSpan(j * 32, 32);
                        BinaryPrimitives.WriteInt64LittleEndian(b, e.Key); BinaryPrimitives.WriteInt64LittleEndian(b[8..], e.Id);
                        BinaryPrimitives.WriteInt64LittleEndian(b[16..], e.Offset); BinaryPrimitives.WriteInt32LittleEndian(b[24..], e.Length); BinaryPrimitives.WriteUInt32LittleEndian(b[28..], e.Crc);
                    }
                    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4092), Crc32.Compute(page.AsSpan(0, 4092))); file.Write(page);
                }
                file.Flush(true);
            }
            for (int attempt = 0; ; attempt++)
            {
                try { if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); break; }
                catch (IOException error) when (OperatingSystem.IsWindows() && attempt < 5 &&
                    (error.HResult & 65535) is 5 or 32 or 1175 or 1176)
                { Thread.Sleep(10 << attempt); }
            }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Dispose() => _handle.Dispose();
}

