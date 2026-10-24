using System.Buffers.Binary;
using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;

namespace PureDb;
// 可重建的不可变索引：打开时只读取头部，访问页时才校验。
// 数据文件始终是恢复依据，旁路索引通过原子替换更新。
/// <summary>带页校验的不可变持久化索引；可丢弃并从事务日志重建。</summary>
internal sealed class DiskIndex : IDisposable
{
    /// <summary>持久化索引中的一个键、记录 ID、日志位置、长度及校验和。</summary>
    /// <param name="Key">索引键或查询主键表达式。</param>
    /// <param name="Id">记录的唯一 ID。</param>
    /// <param name="Offset">数据在文件中的字节偏移，或查询的 OFFSET 表达式。</param>
    /// <param name="Length">编码值的字节长度。</param>
    /// <param name="Crc">IEEE CRC-32 校验和。</param>
    internal readonly record struct Item(long Key, long Id, long Offset, int Length, uint Crc);
    private const int PageSize = 4096, PerPage = 127;
    private readonly SafeFileHandle _handle;
    private readonly ConcurrentDictionary<int, byte[]> _pages = new();
    private readonly ConcurrentQueue<int> _order = new();
    private int _cachedPageCount;
    private readonly long _start;
    /// <summary>不可变索引中的条目数量。</summary>
    public int Count
    {
        get;
    }
    /// <summary>索引所属数据文件的代次。</summary>
    public int Generation
    {
        get;
    }
    /// <summary>创建索引时对应的数据文件提交长度。</summary>
    public long SourceLength
    {
        get;
    }
    /// <summary>创建索引时对应的数据文件修改时间戳。</summary>
    public long SourceTime
    {
        get;
    }
    /// <summary>带索引头校验保护的模式或表计数元数据。</summary>
    public byte[] Metadata
    {
        get;
    }
    /// <summary>首个索引键，空索引时使用格式约定值。</summary>
    public long MinKey
    {
        get;
    }
    /// <summary>最后一个索引键，空索引时使用格式约定值。</summary>
    public long MaxKey
    {
        get;
    }

    /// <summary>接管已校验的索引文件句柄和头部，初始化按需读取信息。</summary>
    /// <param name="handle">本操作使用的安全文件句柄。</param>
    /// <param name="header">已经读取的固定格式头部字节。</param>
    /// <param name="metadata">随持久化索引头保存的辅助元数据。</param>
    private DiskIndex(SafeFileHandle handle, byte[] header, byte[] metadata)
    {
        _handle = handle;
        Count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        Generation = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        SourceLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16));
        SourceTime = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24));
        _start = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40));
        Metadata = metadata;
        MinKey = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(48));
        MaxKey = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(56));
    }

    /// <summary>校验索引头及其与数据文件的对应关系；无法信任时返回 null，让调用方回退恢复。</summary>
    /// <param name="path">当前数据库或索引文件路径。</param>
    /// <param name="generation">数据文件代次，用于识别原子替换。</param>
    /// <param name="length">当前数据文件的已提交长度，单位为字节。</param>
    /// <param name="time">源数据文件的修改时间戳。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    public static DiskIndex? Open(string path, int generation, long length, long? time = null)
    {
        SafeFileHandle? handle = null;
        try
        {
            handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
            var h = new byte[PageSize];
            ReadExactly(handle, h, 0);
            if (!h.AsSpan(0, 8).SequenceEqual("PDBIDX03"u8) || Crc32.Compute(h.AsSpan(0, 4092)) != BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4092)))
                return null;
            int gen = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(8)), count = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(12));
            long source = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(16)), stamp = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(24));
            int size = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(32));
            long start = BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(40));
            if (gen != generation || source < 16 || source > length || time is long t && source == length && t != stamp || count < 0 || size < 0 || size > 64 * 1024 * 1024 || start != PageSize + ((long)size + 4095) / 4096 * 4096 || RandomAccess.GetLength(handle) != start + ((long)count + PerPage - 1) / PerPage * PageSize)
                return null;
            var metadata = new byte[size];
            ReadExactly(handle, metadata, PageSize);
            if (Crc32.Compute(metadata) != BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(36)))
                return null;
            var result = new DiskIndex(handle, h, metadata);
            handle = null;
            return result;
        }
        catch (IOException)
        {
            return null;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    /// <summary>按零基条目序号读取并校验所属索引页。</summary>
    /// <param name="ordinal">从 0 开始的条目或列序号。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    public Item At(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        if (ordinal >= Count)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        int page = ordinal / PerPage;
        if (!_pages.TryGetValue(page, out var bytes))
        {
            bytes = new byte[PageSize];
            ReadExactly(_handle, bytes, _start + (long)page * PageSize);
            if (Crc32.Compute(bytes.AsSpan(0, 4092)) != BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4092)))
                throw new InvalidDataException("Persistent index checksum mismatch. Remove the .pidx/.sidx sidecar to rebuild it from the data log.");
            if (_pages.TryAdd(page, bytes))
            {
                Interlocked.Increment(ref _cachedPageCount);
                _order.Enqueue(page);
                while (Volatile.Read(ref _cachedPageCount) > 128 && _order.TryDequeue(out var victim))
                    if (_pages.TryRemove(victim, out _))
                        Interlocked.Decrement(ref _cachedPageCount);
            }
        }

        var b = bytes.AsSpan(ordinal % PerPage * 32, 32);
        return new(BinaryPrimitives.ReadInt64LittleEndian(b), BinaryPrimitives.ReadInt64LittleEndian(b[8..]), BinaryPrimitives.ReadInt64LittleEndian(b[16..]), BinaryPrimitives.ReadInt32LittleEndian(b[24..]), BinaryPrimitives.ReadUInt32LittleEndian(b[28..]));
    }

    /// <summary>通过有限探测和二分查找取得键的下界或上界位置。</summary>
    /// <param name="key">用于查询或统计的内部表键或索引键。</param>
    /// <param name="upper">是否返回严格大于指定键的上界；否则返回大于或等于的下界。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    public int Bound(long key, bool upper)
    {
        if (Count == 0 || key < MinKey || !upper && key == MinKey)
            return 0;
        if (key > MaxKey || upper && key == MaxKey)
            return Count;
        int lo = 0, hi = Count;
        // 已校验的探测只缩小二分查找区间。密集键和常见重复键通常只访问一至两页；
        // 倾斜分布只允许有限次额外探测，然后回退到精确二分查找。
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
                    if (!Before(At(probe).Key))
                    {
                        hi = probe;
                        break;
                    }

                    lo = probe + 1;
                }
            }
            else
            {
                hi = guess;
                for (int step = 1; step <= 128 && hi > 0; step <<= 1)
                {
                    int probe = Math.Max(0, guess - step);
                    if (Before(At(probe).Key))
                    {
                        lo = probe + 1;
                        break;
                    }

                    hi = probe;
                }
            }
        }

        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            long value = At(mid).Key;
            if (value < key || upper && value == key)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    /// <summary>从文件指定位置读取全部请求字节，遇到截断文件立即失败。</summary>
    /// <param name="handle">本操作使用的安全文件句柄。</param>
    /// <param name="bytes">接收完整文件读取结果的目标片段。</param>
    /// <param name="offset">文件或缓冲区中的字节偏移。</param>
    private static void ReadExactly(SafeFileHandle handle, Span<byte> bytes, long offset)
    {
        while (!bytes.IsEmpty)
        {
            int n = RandomAccess.Read(handle, bytes, offset);
            if (n == 0)
                throw new EndOfStreamException();
            bytes = bytes[n..];
            offset += n;
        }
    }

    /// <summary>写入带校验的临时索引文件并原子替换目标索引。</summary>
    /// <param name="path">当前数据库或索引文件路径。</param>
    /// <param name="generation">数据文件代次，用于识别原子替换。</param>
    /// <param name="length">当前数据文件的已提交长度，单位为字节。</param>
    /// <param name="time">源数据文件的修改时间戳。</param>
    /// <param name="metadata">随持久化索引头保存的辅助元数据。</param>
    /// <param name="entries">待持久化的有序索引项。</param>
    public static void Write(string path, int generation, long length, long time, byte[] metadata, IReadOnlyList<Item> entries)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            {
                var header = new byte[PageSize];
                "PDBIDX03"u8.CopyTo(header);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), generation);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), entries.Count);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), length);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), time);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), metadata.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), Crc32.Compute(metadata));
                long start = PageSize + ((long)metadata.Length + 4095) / 4096 * 4096;
                if (entries.Count > 0)
                {
                    BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(48), entries[0].Key);
                    BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(56), entries[^1].Key);
                }

                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40), start);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4092), Crc32.Compute(header.AsSpan(0, 4092)));
                file.Write(header);
                file.Write(metadata);
                file.SetLength(start);
                file.Position = start;
                var page = new byte[PageSize];
                for (int i = 0; i < entries.Count;)
                {
                    page.AsSpan().Clear();
                    for (int j = 0; j < PerPage && i < entries.Count; j++, i++)
                    {
                        var e = entries[i];
                        var b = page.AsSpan(j * 32, 32);
                        BinaryPrimitives.WriteInt64LittleEndian(b, e.Key);
                        BinaryPrimitives.WriteInt64LittleEndian(b[8..], e.Id);
                        BinaryPrimitives.WriteInt64LittleEndian(b[16..], e.Offset);
                        BinaryPrimitives.WriteInt32LittleEndian(b[24..], e.Length);
                        BinaryPrimitives.WriteUInt32LittleEndian(b[28..], e.Crc);
                    }

                    BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4092), Crc32.Compute(page.AsSpan(0, 4092)));
                    file.Write(page);
                }

                file.Flush(true);
            }

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                        File.Replace(temp, path, null);
                    else
                        File.Move(temp, path);
                    break;
                }
                catch (IOException error) when (OperatingSystem.IsWindows() && attempt < 5 && (error.HResult & 65535) is 5 or 32 or 1175 or 1176)
                {
                    Thread.Sleep(10 << attempt);
                }
            }
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>关闭索引文件句柄并释放页缓存。</summary>
    public void Dispose() => _handle.Dispose();
}
