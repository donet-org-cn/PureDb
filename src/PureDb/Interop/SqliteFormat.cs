using System.Buffers.Binary;
using System.Text;

namespace PureDb;
// SQLite 格式 3 的纯托管编码和页读写实现，不调用 SQLite 动态库或外部进程。
/// <summary>SQLite 变长整数及记录存储类型的编码器和解码器。</summary>
internal static class SqliteRecord
{
    /// <summary>将无符号整数编码为 SQLite 变长整数。</summary>
    /// <param name="value">待编码的无符号整数。</param>
    /// <returns>本次编码或复制得到的字节数组。</returns>
    internal static byte[] Varint(ulong value)
    {
        if (value > 0x00ffffffffffffffUL)
        {
            var nine = new byte[9];
            nine[8] = (byte)value;
            value >>= 8;
            for (int i = 7; i >= 0; i--)
            {
                nine[i] = (byte)((value & 127) | 128);
                value >>= 7;
            }

            return nine;
        }

        Span<byte> temp = stackalloc byte[9];
        int at = 8;
        temp[at] = (byte)(value & 127);
        while ((value >>= 7) != 0)
            temp[--at] = (byte)((value & 127) | 128);
        return temp[at..].ToArray();
    }

    /// <summary>从指定位置读取 SQLite 变长整数，并前移游标。</summary>
    /// <param name="bytes">待校验或解码的只读编码字节。</param>
    /// <param name="at">页或记录内的当前游标；成功读取后前移。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    internal static ulong ReadVarint(ReadOnlySpan<byte> bytes, ref int at)
    {
        ulong value = 0;
        for (int i = 0; i < 9; i++)
        {
            if ((uint)at >= bytes.Length)
                throw new InvalidDataException("Truncated SQLite varint.");
            byte b = bytes[at++];
            if (i == 8)
                return (value << 8) | b;
            value = (value << 7) | (uint)(b & 127);
            if (b < 128)
                return value;
        }

        throw new InvalidDataException();
    }

    /// <summary>按 SQLite 记录头和存储类型编码全部列值。</summary>
    /// <param name="values">按对应模式列序或函数参数顺序排列的 SQL 标量值。</param>
    /// <returns>本次编码或复制得到的字节数组。</returns>
    internal static byte[] Encode(object?[] values)
    {
        Span<ulong> types = values.Length <= 128 ? stackalloc ulong[values.Length] : new ulong[values.Length];
        int header = 0, body = 0;
        for (int i = 0; i < values.Length; i++)
        {
            object? v = SqlJsonFunctions.Unwrap(values[i]);
            ulong type;
            int width;
            if (v is null)
                type = 0;
            else if (v is long n)
            {
                width = n is 0 or 1 ? 0 : n is >= sbyte.MinValue and <= sbyte.MaxValue ? 1 : n is >= short.MinValue and <= short.MaxValue ? 2 : n is >= -8388608 and <= 8388607 ? 3 : n is >= int.MinValue and <= int.MaxValue ? 4 : n is >= -140737488355328L and <= 140737488355327L ? 6 : 8;
                type = width == 0 ? (ulong)(8 + n) : width switch
                {
                    6 => 5UL,
                    8 => 6UL,
                    _ => (ulong)width
                };
                body = checked(body + width);
            }
            else if (v is double)
            {
                type = 7;
                body = checked(body + 8);
            }
            else
            {
                width = v is byte[] b ? b.Length : Encoding.UTF8.GetByteCount((string)v);
                type = (ulong)width * 2 + (v is byte[]? 12UL : 13UL);
                body = checked(body + width);
            }

            types[i] = type;
            header = checked(header + VarintSize(type));
        }

        int size = header + 1;
        while (size != header + VarintSize((ulong)size))
            size = header + VarintSize((ulong)size);
        var result = new byte[checked(size + body)];
        int at = WriteVarint(result, (ulong)size);
        foreach (ulong type in types)
            at += WriteVarint(result.AsSpan(at), type);
        for (int i = 0; i < values.Length; i++)
        {
            object? v = SqlJsonFunctions.Unwrap(values[i]);
            ulong type = types[i];
            if (type is 0 or 8 or 9)
                continue;
            if (v is long n)
            {
                int width = type switch
                {
                    5 => 6,
                    6 => 8,
                    _ => (int)type
                };
                for (int shift = width - 1; shift >= 0; shift--)
                    result[at++] = (byte)(n >> (shift * 8));
            }
            else if (v is double d)
            {
                BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(at), BitConverter.DoubleToInt64Bits(d));
                at += 8;
            }
            else if (v is byte[] b)
            {
                b.CopyTo(result, at);
                at += b.Length;
            }
            else
                at += Encoding.UTF8.GetBytes((string)v!, result.AsSpan(at));
        }

        return result;
    }

    /// <summary>计算 SQLite 变长整数占用的字节数。</summary>
    /// <param name="value">待编码的无符号整数。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    private static int VarintSize(ulong value)
    {
        if (value > 0x00ffffffffffffffUL)
            return 9;
        int n = 1;
        while ((value >>= 7) != 0)
            n++;
        return n;
    }

    /// <summary>将 SQLite 变长整数写入目标片段并返回写入长度。</summary>
    /// <param name="target">接收编码结果的片段或待合并的 JSON 节点。</param>
    /// <param name="value">待编码的无符号整数。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    private static int WriteVarint(Span<byte> target, ulong value)
    {
        int size = VarintSize(value), at = size - 1;
        if (size == 9)
        {
            target[at--] = (byte)value;
            value >>= 8;
        }
        else
        {
            target[at--] = (byte)(value & 127);
            value >>= 7;
        }

        while (at >= 0)
        {
            target[at--] = (byte)((value & 127) | 128);
            value >>= 7;
        }

        return size;
    }

    /// <summary>验证记录头边界后解码 SQLite 列值。</summary>
    /// <param name="bytes">待校验或解码的只读编码字节。</param>
    /// <param name="encoding">SQLite 文件头指定的文本编码。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    internal static object?[] Decode(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        int at = 0;
        ulong size = ReadVarint(bytes, ref at);
        if (size > (ulong)bytes.Length || size < (ulong)at)
            throw new InvalidDataException("Invalid SQLite record header.");
        var types = new List<ulong>();
        while (at < (int)size)
            types.Add(ReadVarint(bytes[..(int)size], ref at));
        var result = new object?[types.Count];
        at = (int)size;
        for (int i = 0; i < types.Count; i++)
        {
            ulong type = types[i];
            int n = type switch
            {
                0 or 8 or 9 => 0,
                1 => 1,
                2 => 2,
                3 => 3,
                4 => 4,
                5 => 6,
                6 or 7 => 8,
                >= 12 when (type - 12) / 2 <= int.MaxValue => (int)((type - 12) / 2),
                _ => throw new InvalidDataException("Unsupported SQLite serial type.")
            };
            if (n > bytes.Length - at)
                throw new InvalidDataException("Truncated SQLite record.");
            var data = bytes.Slice(at, n);
            at += n;
            if (type == 0)
                result[i] = null;
            else if (type is 8 or 9)
                result[i] = (long)type - 8;
            else if (type == 7)
                result[i] = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(data));
            else if (type < 7)
            {
                long v = (sbyte)data[0];
                for (int j = 1; j < n; j++)
                    v = (v << 8) | data[j];
                result[i] = v;
            }
            else
                result[i] = (type & 1) == 0 ? data.ToArray() : encoding.GetString(data);
        }

        if (at != bytes.Length)
            throw new InvalidDataException("Unexpected SQLite record bytes.");
        return result;
    }
}

/// <summary>校验 SQLite 文件头并读取 B-tree、溢出页和记录的托管读取器。</summary>
internal sealed class SqliteFileReader : IDisposable
{
    private readonly FileStream _file;
    private readonly int _size, _usable;
    private readonly long _pages;
    private readonly Encoding _encoding;
    private readonly bool _leaveOpen;
    private readonly Dictionary<long, byte[]> _cache = [];
    private readonly int _cachePages;
    /// <summary>文件头声明并已校验的页大小。</summary>
    internal int PageSize => _size;
    /// <summary>文件头声明的数据库页数。</summary>
    internal long PageCount => _pages;
    /// <summary>SQLite 文件头中的数据变化计数器。</summary>
    internal uint ChangeCounter
    {
        get;
    }
    /// <summary>SQLite 文件头中的模式版本标记。</summary>
    internal uint SchemaCookie
    {
        get;
    }
    /// <summary>应用可维护的 user_version 头部字段。</summary>
    internal uint UserVersion
    {
        get;
    }
    /// <summary>应用可维护的 application_id 头部字段。</summary>
    internal uint ApplicationId
    {
        get;
    }

    /// <summary>SQLite 表 B-tree 中读取到的 rowid 和列值。</summary>
    /// <param name="Id">记录的唯一 ID。</param>
    /// <param name="Values">按对应模式列序或函数参数顺序排列的 SQL 标量值。</param>
    internal sealed record Row(long Id, object?[] Values);
    /// <summary>打开或接管 SQLite 读取句柄，验证文件头并配置页缓存与日志协议限制。</summary>
    /// <param name="path">当前数据库或索引文件路径。</param>
    public SqliteFileReader(string path) : this(OpenOffline(path), false)
    {
    }

    /// <summary>打开仅用于离线导入的文件，检查不能忽略的 WAL 或日志状态。</summary>
    /// <param name="path">当前数据库或索引文件路径。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private static FileStream OpenOffline(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        if (new[]
        {
            "-wal",
            "-journal"
        }.Any(s => File.Exists(path + s) && new FileInfo(path + s).Length > 0))
        {
            file.Dispose();
            throw new NotSupportedException("Import requires an offline SQLite database with its WAL checkpointed and no pending journal.");
        }

        return file;
    }

    /// <summary>打开或接管 SQLite 读取句柄，验证文件头并配置页缓存与日志协议限制。</summary>
    /// <param name="file">本操作使用的文件句柄。</param>
    /// <param name="leaveOpen">释放读取器时是否保留外部传入的文件句柄。</param>
    /// <param name="cacheBytes">缓存预算，单位为字节。</param>
    /// <param name="rollbackOnly">是否只接受当前实现的回滚日志模式，拒绝 WAL。</param>
    internal SqliteFileReader(FileStream file, bool leaveOpen, long cacheBytes = 0, bool rollbackOnly = false)
    {
        _file = file;
        _leaveOpen = leaveOpen;
        try
        {
            var h = new byte[100];
            ReadAt(h, 0);
            if (!h.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
                throw new InvalidDataException("Not a SQLite format 3 file.");
            _size = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(16));
            if (_size == 1)
                _size = 65536;
            if (_size < 512 || (_size & (_size - 1)) != 0 || _size > 65536 || _file.Length % _size != 0 || h[18] is not (1 or 2) || h[19] is not (1 or 2))
                throw new InvalidDataException("Invalid SQLite header.");
            _usable = _size - h[20];
            if (_usable < 480)
                throw new InvalidDataException("Invalid SQLite usable page size.");
            _pages = _file.Length / _size;
            _encoding = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(56)) switch
            {
                1 => new UTF8Encoding(false, true),
                2 => new UnicodeEncoding(false, false, true),
                3 => new UnicodeEncoding(true, false, true),
                _ => throw new NotSupportedException("Unsupported SQLite text encoding.")
            };
            if (rollbackOnly && (_size != 4096 || h[18] != 1 || h[19] != 1 || h[20] != 0 || BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(56)) != 1))
                throw new NotSupportedException("In-place phase 1 requires 4096-byte pages, UTF-8, no reserved bytes and rollback journal mode. Checkpoint WAL and select journal_mode=DELETE in SQLite first.");
            if (rollbackOnly && (BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(52)) != 0 || BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(44)) != 4))
                throw new NotSupportedException("In-place phase 1 requires schema format 4 and auto_vacuum=NONE.");
            _cachePages = (int)Math.Min(int.MaxValue, cacheBytes / _size);
            ChangeCounter = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(24));
            SchemaCookie = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(40));
            UserVersion = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(60));
            ApplicationId = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(68));
        }
        catch
        {
            if (!leaveOpen)
                _file.Dispose();
            throw;
        }
    }

    /// <summary>从当前文件指定偏移完整读取所需字节。</summary>
    /// <param name="destination">接收文件读取结果的目标缓冲区，必须完整填满。</param>
    /// <param name="offset">文件或缓冲区中的字节偏移。</param>
    private void ReadAt(Span<byte> destination, long offset)
    {
        while (!destination.IsEmpty)
        {
            int n = RandomAccess.Read(_file.SafeFileHandle, destination, offset);
            if (n == 0)
                throw new EndOfStreamException();
            destination = destination[n..];
            offset += n;
        }
    }

    /// <summary>按页号读取并校验文件边界，按需复用受预算限制的页缓存。</summary>
    /// <param name="number">从 1 开始的 SQLite 页号。</param>
    /// <returns>本次编码或复制得到的字节数组。</returns>
    private byte[] Page(long number)
    {
        if (number < 1 || number > _pages)
            throw new InvalidDataException("SQLite page outside the file.");
        if (_cache.TryGetValue(number, out var cached))
            return cached;
        var b = new byte[_size];
        ReadAt(b, (number - 1) * _size);
        if (_cachePages > 0)
        {
            if (_cache.Count >= _cachePages)
                _cache.Clear();
            _cache.Add(number, b);
        }

        return b;
    }

    /// <summary>统计指定表 B-tree 的记录数量。</summary>
    /// <param name="root">从 1 开始的 SQLite 表 B-tree 根页号。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    internal long Count(long root)
    {
        long count = 0;
        var seen = new HashSet<long>();
        var pending = new Stack<long>();
        pending.Push(root);
        while (pending.TryPop(out long number))
        {
            if (!seen.Add(number))
                throw new InvalidDataException("Cyclic SQLite table.");
            var page = Page(number);
            int h = number == 1 ? 100 : 0;
            int cells = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 3));
            if (page[h] == 13)
                count += cells;
            else if (page[h] == 5)
            {
                for (int i = 0; i < cells; i++)
                    pending.Push(BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 12 + i * 2)))));
                pending.Push(BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(h + 8)));
            }
            else
                throw new NotSupportedException("Only ordinary rowid tables are supported.");
        }

        return count;
    }

    /// <summary>沿整数键表 B-tree 定位指定 rowid，未命中时返回 null。</summary>
    /// <param name="root">从 1 开始的 SQLite 表 B-tree 根页号。</param>
    /// <param name="id">原生记录 ID 或受支持的整数主键。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    internal Row? Seek(long root, long id)
    {
        long number = root;
        for (int depth = 0; depth < 64; depth++)
        {
            byte[] page = Page(number);
            int h = number == 1 ? 100 : 0;
            byte type = page[h];
            if (type is not (5 or 13))
                throw new NotSupportedException("Only ordinary rowid tables are supported.");
            int count = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 3)), header = type == 5 ? 12 : 8;
            int lo = 0, hi = count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2, at = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + header + mid * 2));
                if (type == 5)
                    at += 4;
                else
                    SqliteRecord.ReadVarint(page, ref at);
                long key = unchecked((long)SqliteRecord.ReadVarint(page, ref at));
                if (key < id)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            if (type == 13)
            {
                if (lo == count)
                    return null;
                int at = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 8 + lo * 2));
                var row = ReadRow(page, at);
                return row.Id == id ? row : null;
            }

            number = lo == count ? BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(h + 8)) : BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 12 + lo * 2))));
        }

        throw new InvalidDataException("Cyclic or excessively deep SQLite B-tree.");
    }

    /// <summary>从叶页单元及必要的溢出页读取一条表记录。</summary>
    /// <param name="page">当前数据页或热点页；具体类型由当前签名确定。</param>
    /// <param name="at">页或记录内的当前游标；成功读取后前移。</param>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private Row ReadRow(byte[] page, int at)
    {
        ulong size = SqliteRecord.ReadVarint(page, ref at);
        long id = unchecked((long)SqliteRecord.ReadVarint(page, ref at));
        if (size > 256 * 1024 * 1024)
            throw new NotSupportedException("SQLite record exceeds the 256 MiB limit.");
        int length = (int)size, local = Local(length, _usable, true);
        if (at + local + (local < length ? 4 : 0) > _usable)
            throw new InvalidDataException("Invalid SQLite cell payload.");
        if (local == length)
            return new(id, SqliteRecord.Decode(page.AsSpan(at, length), _encoding));
        var payload = new byte[length];
        page.AsSpan(at, local).CopyTo(payload);
        int copied = local;
        long next = local < length ? BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(at + local)) : 0;
        var overflow = new HashSet<long>();
        while (copied < length)
        {
            if (!overflow.Add(next))
                throw new InvalidDataException("Cyclic SQLite overflow chain.");
            var b = Page(next);
            int n = Math.Min(length - copied, _usable - 4);
            b.AsSpan(4, n).CopyTo(payload.AsSpan(copied));
            copied += n;
            next = BinaryPrimitives.ReadUInt32BigEndian(b);
        }

        if (next != 0)
            throw new InvalidDataException("Unexpected SQLite overflow tail.");
        return new(id, SqliteRecord.Decode(payload, _encoding));
    }

    /// <summary>从指定根页枚举全部表记录。</summary>
    /// <param name="root">从 1 开始的 SQLite 表 B-tree 根页号。</param>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    public IEnumerable<Row> Table(long root) => Walk(root, new HashSet<long>(), 0);
    /// <summary>递归遍历表 B-tree，限制深度并检测循环页引用。</summary>
    /// <param name="number">从 1 开始的 SQLite 页号。</param>
    /// <param name="seen">本次遍历已访问的页号集合，用于检测环。</param>
    /// <param name="depth">当前 B-tree 遍历深度，用于阻止恶意或损坏文件导致无限递归。</param>
    /// <returns>按方法规定的顺序和可见性组织的结果集合。</returns>
    private IEnumerable<Row> Walk(long number, HashSet<long> seen, int depth)
    {
        if (depth > 64 || !seen.Add(number))
            throw new InvalidDataException("Cyclic or excessively deep SQLite B-tree.");
        byte[] page = Page(number);
        int h = number == 1 ? 100 : 0;
        byte type = page[h];
        if (type is not (5 or 13))
            throw new NotSupportedException("Only ordinary SQLite rowid tables are supported; WITHOUT ROWID/virtual tables are not supported.");
        int count = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 3)), header = type == 5 ? 12 : 8;
        if (h + header + count * 2 > _usable)
            throw new InvalidDataException("Invalid SQLite cell pointer array.");
        for (int i = 0; i < count; i++)
        {
            int at = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + header + i * 2));
            if (at < h + header + count * 2 || at >= _usable)
                throw new InvalidDataException("Invalid SQLite cell offset.");
            if (type == 5)
            {
                long child = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(at));
                foreach (var row in Walk(child, seen, depth + 1))
                    yield return row;
            }
            else
            {
                yield return ReadRow(page, at);
            }
        }

        if (type == 5)
            foreach (var row in Walk(BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(h + 8)), seen, depth + 1))
                yield return row;
    }

    /// <summary>按 SQLite 页容量规则计算载荷在本地页保存的字节数。</summary>
    /// <param name="length">本次处理的字节长度或请求长度。</param>
    /// <param name="usable">扣除保留区后的 SQLite 页可用字节数。</param>
    /// <param name="table">是否按表 B-tree 而非索引 B-tree 的本地载荷规则计算。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    internal static int Local(int length, int usable, bool table)
    {
        int max = table ? usable - 35 : (usable - 12) * 64 / 255 - 23, min = (usable - 12) * 32 / 255 - 23;
        if (length <= max)
            return length;
        int k = min + (length - min) % (usable - 4);
        return k <= max ? k : min;
    }

    /// <summary>释放页缓存，并在拥有文件句柄时关闭它。</summary>
    public void Dispose()
    {
        _cache.Clear();
        if (!_leaveOpen)
            _file.Dispose();
    }
}

/// <summary>生成受支持的 SQLite 表和索引 B-tree，并写入文件头及模式表。</summary>
internal sealed class SqliteFileWriter : IDisposable
{
    private const int Size = 4096;
    private readonly FileStream _file;
    private uint _pages = 1;
    /// <summary>创建新的 SQLite 目标文件并预留模式根页。</summary>
    /// <param name="path">当前数据库或索引文件路径。</param>
    internal SqliteFileWriter(string path)
    {
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536);
        _file.SetLength(Size);
    }

    /// <summary>分配新页并写入完整页内容，返回页号。</summary>
    /// <param name="bytes">当前调用持有的完整页、记录或模式编码字节。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    private uint Page(byte[] bytes)
    {
        uint number = ++_pages;
        Store(number, bytes);
        return number;
    }

    /// <summary>将完整页内容写到指定页号对应的位置。</summary>
    /// <param name="number">从 1 开始的 SQLite 页号。</param>
    /// <param name="bytes">当前调用持有的完整页、记录或模式编码字节。</param>
    private void Store(uint number, byte[] bytes)
    {
        _file.Position = ((long)number - 1) * Size;
        _file.Write(bytes);
    }

    /// <summary>编码表或索引 B-tree 单元，按需生成溢出页链。</summary>
    /// <param name="payload">已编码的 SQLite 记录载荷。</param>
    /// <param name="rowid">SQLite 表 B-tree 中的整数行标识。</param>
    /// <param name="child">B-tree 子页号；无子页时使用格式约定值。</param>
    /// <returns>本次编码或复制得到的字节数组。</returns>
    private byte[] Cell(byte[] payload, long? rowid, uint? child = null)
    {
        using var cell = new MemoryStream();
        Span<byte> number = stackalloc byte[4];
        if (child is uint c)
        {
            BinaryPrimitives.WriteUInt32BigEndian(number, c);
            cell.Write(number);
        }

        cell.Write(SqliteRecord.Varint((ulong)payload.Length));
        if (rowid is long id)
            cell.Write(SqliteRecord.Varint(unchecked((ulong)id)));
        int local = SqliteFileReader.Local(payload.Length, Size, rowid is not null);
        cell.Write(payload.AsSpan(0, local));
        if (local < payload.Length)
        {
            uint next = 0;
            for (int end = payload.Length; end > local;)
            {
                int start = local + (end - local - 1) / (Size - 4) * (Size - 4);
                var b = new byte[Size];
                BinaryPrimitives.WriteUInt32BigEndian(b, next);
                payload.AsSpan(start, end - start).CopyTo(b.AsSpan(4));
                next = Page(b);
                end = start;
            }

            BinaryPrimitives.WriteUInt32BigEndian(number, next);
            cell.Write(number);
        }

        return cell.ToArray();
    }

    /// <summary>生成指定类型的 B-tree 页并布局单元指针及内容区。</summary>
    /// <param name="type">SQLite B-tree 页类型标记。</param>
    /// <param name="cells">按 B-tree 顺序排列的已编码单元。</param>
    /// <param name="right">内部 B-tree 页的最右子页号。</param>
    /// <param name="offset">文件或缓冲区中的字节偏移。</param>
    /// <returns>本次编码或复制得到的字节数组。</returns>
    private static byte[] Node(byte type, List<byte[]> cells, uint right = 0, int offset = 0)
    {
        var b = new byte[Size];
        b[offset] = type;
        int header = type is 2 or 5 ? 12 : 8;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(offset + 3), (ushort)cells.Count);
        if (header == 12)
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(offset + 8), right);
        int at = Size;
        for (int i = 0; i < cells.Count; i++)
        {
            at -= cells[i].Length;
            cells[i].CopyTo(b, at);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(offset + header + i * 2), (ushort)at);
        }

        if (at < offset + header + cells.Count * 2)
            throw new InvalidDataException("SQLite output page overflow.");
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(offset + 5), (ushort)at);
        return b;
    }

    /// <summary>将按 rowid 排列的记录构建为表 B-tree，返回根页号。</summary>
    /// <param name="rows">按当前方法要求排序或分组的输入行。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    public uint Table(IEnumerable<(long Id, object?[] Values)> rows)
    {
        var nodes = new List<(uint Page, long Max)>();
        var cells = new List<byte[]>();
        int used = 8;
        long max = 0;
        foreach (var row in rows)
        {
            byte[] cell = Cell(SqliteRecord.Encode(row.Values), row.Id);
            if (used + 2 + cell.Length > Size && cells.Count > 0)
            {
                nodes.Add((Page(Node(13, cells)), max));
                cells.Clear();
                used = 8;
            }

            cells.Add(cell);
            used += 2 + cell.Length;
            max = row.Id;
        }

        nodes.Add((Page(Node(13, cells)), max));
        while (nodes.Count > 1)
        {
            var next = new List<(uint Page, long Max)>();
            int at = 0;
            while (at < nodes.Count)
            {
                cells.Clear();
                used = 12;
                while (at + 1 < nodes.Count)
                {
                    using var m = new MemoryStream();
                    var n = new byte[4];
                    BinaryPrimitives.WriteUInt32BigEndian(n, nodes[at].Page);
                    m.Write(n);
                    m.Write(SqliteRecord.Varint(unchecked((ulong)nodes[at].Max)));
                    byte[] c = m.ToArray();
                    if (used + 2 + c.Length > Size)
                        break;
                    cells.Add(c);
                    used += 2 + c.Length;
                    at++;
                }

                next.Add((Page(Node(5, cells, nodes[at].Page)), nodes[at].Max));
                at++;
            }

            nodes = next;
        }

        return nodes[0].Page;
    }

    /// <summary>将已排序索引记录构建为索引 B-tree，返回根页号。</summary>
    /// <param name="records">已按 SQLite 索引顺序排列的记录。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    public uint Index(IReadOnlyList<object?[]> records)
    {
        var children = new List<uint>();
        var separators = new List<byte[]>();
        var cells = new List<byte[]>();
        int used = 8;
        for (int at = 0; at < records.Count; at++)
        {
            byte[] payload = SqliteRecord.Encode(records[at]);
            int local = SqliteFileReader.Local(payload.Length, Size, false);
            int size = SqliteRecord.Varint((ulong)payload.Length).Length + local + (local < payload.Length ? 4 : 0);
            if (used + 2 + size > Size && cells.Count > 0)
            {
                children.Add(Page(Node(10, cells)));
                separators.Add(payload);
                cells.Clear();
                used = 8;
            }
            else
            {
                cells.Add(Cell(payload, null));
                used += 2 + size;
            }
        }

        children.Add(Page(Node(10, cells)));
        while (children.Count > 1)
        {
            var nextChildren = new List<uint>();
            var nextSeparators = new List<byte[]>();
            int at = 0;
            while (at < children.Count)
            {
                cells.Clear();
                used = 12;
                while (at < separators.Count)
                {
                    // 分配溢出页前先检查记录大小，避免失败路径留下孤立页。
                    int local = SqliteFileReader.Local(separators[at].Length, Size, false);
                    int size = 4 + SqliteRecord.Varint((ulong)separators[at].Length).Length + local + (local < separators[at].Length ? 4 : 0);
                    if (used + 2 + size > Size)
                        break;
                    cells.Add(Cell(separators[at], null, children[at]));
                    used += 2 + size;
                    at++;
                }

                nextChildren.Add(Page(Node(2, cells, children[at])));
                at++;
                if (at < children.Count)
                    nextSeparators.Add(separators[at - 1]);
            }

            children = nextChildren;
            separators = nextSeparators;
        }

        return children[0];
    }

    /// <summary>写入 sqlite_schema 及文件头，保存变化计数器和应用版本信息后同步文件。</summary>
    /// <param name="schema">SQLite 模式表记录或 SQL 表模式，具体类型由签名确定。</param>
    /// <param name="changeCounter">SQLite 文件头的数据变化计数器。</param>
    /// <param name="schemaCookie">SQLite 文件头中的模式版本。</param>
    /// <param name="userVersion">SQLite 文件头中由应用维护的版本号。</param>
    /// <param name="applicationId">SQLite 文件头中的应用 ID。</param>
    public void Finish(List<object?[]> schema, uint changeCounter = 1, uint schemaCookie = 1, uint userVersion = 0, uint applicationId = 0)
    {
        uint root = Table(schema.Select((r, i) => ((long)i + 1, r)));
        var first = Node(5, [], root, 100);
        "SQLite format 3\0"u8.CopyTo(first);
        BinaryPrimitives.WriteUInt16BigEndian(first.AsSpan(16), Size);
        first[18] = first[19] = 1;
        first[21] = 64;
        first[22] = first[23] = 32;
        foreach (int offset in new[]
        {
            24,
            40,
            56,
            92
        }

        )
            BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(offset), 1);
        BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(24), changeCounter);
        BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(92), changeCounter);
        BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(40), schemaCookie);
        BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(60), userVersion);
        BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(68), applicationId);
        BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(28), _pages);
        BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(44), 4);
        BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(96), 3054001);
        Store(1, first);
        _file.Flush(true);
    }

    /// <summary>释放目标文件写入句柄。</summary>
    public void Dispose() => _file.Dispose();
}
