using System.Buffers.Binary;
using System.Text;
namespace PureDb;

// SQLite format 3. Pure managed code; no SQLite DLL or process is used here.
internal static class SqliteRecord
{
    internal static byte[] Varint(ulong value)
    {
        if (value > 0x00ffffffffffffffUL)
        {
            var nine = new byte[9]; nine[8] = (byte)value; value >>= 8;
            for (int i = 7; i >= 0; i--) { nine[i] = (byte)((value & 127) | 128); value >>= 7; } return nine;
        }
        Span<byte> temp = stackalloc byte[9]; int at = 8; temp[at] = (byte)(value & 127);
        while ((value >>= 7) != 0) temp[--at] = (byte)((value & 127) | 128);
        return temp[at..].ToArray();
    }
    internal static ulong ReadVarint(ReadOnlySpan<byte> bytes, ref int at)
    {
        ulong value = 0;
        for (int i = 0; i < 9; i++)
        {
            if ((uint)at >= bytes.Length) throw new InvalidDataException("Truncated SQLite varint.");
            byte b = bytes[at++]; if (i == 8) return (value << 8) | b;
            value = (value << 7) | (uint)(b & 127); if (b < 128) return value;
        }
        throw new InvalidDataException();
    }
    internal static byte[] Encode(object?[] values)
    {
        Span<ulong> types = values.Length <= 128 ? stackalloc ulong[values.Length] : new ulong[values.Length];
        int header = 0, body = 0;
        for (int i = 0; i < values.Length; i++)
        {
            object? v = SqlJsonFunctions.Unwrap(values[i]); ulong type; int width;
            if (v is null) type = 0;
            else if (v is long n)
            {
                width = n is 0 or 1 ? 0 : n is >= sbyte.MinValue and <= sbyte.MaxValue ? 1 : n is >= short.MinValue and <= short.MaxValue ? 2 : n is >= -8388608 and <= 8388607 ? 3 : n is >= int.MinValue and <= int.MaxValue ? 4 : n is >= -140737488355328L and <= 140737488355327L ? 6 : 8;
                type = width == 0 ? (ulong)(8 + n) : width switch { 6 => 5UL, 8 => 6UL, _ => (ulong)width };
                body = checked(body + width);
            }
            else if (v is double) { type = 7; body = checked(body + 8); }
            else
            {
                width = v is byte[] b ? b.Length : Encoding.UTF8.GetByteCount((string)v);
                type = (ulong)width * 2 + (v is byte[] ? 12UL : 13UL); body = checked(body + width);
            }
            types[i] = type; header = checked(header + VarintSize(type));
        }
        int size = header + 1;
        while (size != header + VarintSize((ulong)size)) size = header + VarintSize((ulong)size);
        var result = new byte[checked(size + body)]; int at = WriteVarint(result, (ulong)size);
        foreach (ulong type in types) at += WriteVarint(result.AsSpan(at), type);
        for (int i = 0; i < values.Length; i++)
        {
            object? v = SqlJsonFunctions.Unwrap(values[i]); ulong type = types[i];
            if (type is 0 or 8 or 9) continue;
            if (v is long n)
            {
                int width = type switch { 5 => 6, 6 => 8, _ => (int)type };
                for (int shift = width - 1; shift >= 0; shift--) result[at++] = (byte)(n >> (shift * 8));
            }
            else if (v is double d) { BinaryPrimitives.WriteInt64BigEndian(result.AsSpan(at), BitConverter.DoubleToInt64Bits(d)); at += 8; }
            else if (v is byte[] b) { b.CopyTo(result, at); at += b.Length; }
            else at += Encoding.UTF8.GetBytes((string)v!, result.AsSpan(at));
        }
        return result;
    }
    private static int VarintSize(ulong value) { if (value > 0x00ffffffffffffffUL) return 9; int n = 1; while ((value >>= 7) != 0) n++; return n; }
    private static int WriteVarint(Span<byte> target, ulong value)
    {
        int size = VarintSize(value), at = size - 1;
        if (size == 9) { target[at--] = (byte)value; value >>= 8; }
        else { target[at--] = (byte)(value & 127); value >>= 7; }
        while (at >= 0) { target[at--] = (byte)((value & 127) | 128); value >>= 7; }
        return size;
    }
    internal static object?[] Decode(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        int at = 0; ulong size = ReadVarint(bytes, ref at);
        if (size > (ulong)bytes.Length || size < (ulong)at) throw new InvalidDataException("Invalid SQLite record header.");
        var types = new List<ulong>(); while (at < (int)size) types.Add(ReadVarint(bytes[..(int)size], ref at));
        var result = new object?[types.Count]; at = (int)size;
        for (int i = 0; i < types.Count; i++)
        {
            ulong type = types[i]; int n = type switch { 0 or 8 or 9 => 0, 1 => 1, 2 => 2, 3 => 3, 4 => 4, 5 => 6, 6 or 7 => 8, >= 12 when (type - 12) / 2 <= int.MaxValue => (int)((type - 12) / 2), _ => throw new InvalidDataException("Unsupported SQLite serial type.") };
            if (n > bytes.Length - at) throw new InvalidDataException("Truncated SQLite record."); var data = bytes.Slice(at, n); at += n;
            if (type == 0) result[i] = null;
            else if (type is 8 or 9) result[i] = (long)type - 8;
            else if (type == 7) result[i] = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(data));
            else if (type < 7) { long v = (sbyte)data[0]; for (int j = 1; j < n; j++) v = (v << 8) | data[j]; result[i] = v; }
            else result[i] = (type & 1) == 0 ? data.ToArray() : encoding.GetString(data);
        }
        if (at != bytes.Length) throw new InvalidDataException("Unexpected SQLite record bytes."); return result;
    }
}

internal sealed class SqliteFileReader : IDisposable
{
    private readonly FileStream _file;
    private readonly int _size, _usable;
    private readonly long _pages;
    private readonly Encoding _encoding;
    private readonly bool _leaveOpen;
    private readonly Dictionary<long, byte[]> _cache = [];
    private readonly int _cachePages;
    internal int PageSize => _size;
    internal long PageCount => _pages;
    internal uint ChangeCounter { get; }
    internal uint SchemaCookie { get; }
    internal uint UserVersion { get; }
    internal uint ApplicationId { get; }
    internal sealed record Row(long Id, object?[] Values);
    public SqliteFileReader(string path) : this(OpenOffline(path), false) { }
    private static FileStream OpenOffline(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        if (new[] { "-wal", "-journal" }.Any(s => File.Exists(path + s) && new FileInfo(path + s).Length > 0))
        { file.Dispose(); throw new NotSupportedException("Import requires an offline SQLite database with its WAL checkpointed and no pending journal."); }
        return file;
    }
    internal SqliteFileReader(FileStream file, bool leaveOpen, long cacheBytes = 0, bool rollbackOnly = false)
    {
        _file = file; _leaveOpen = leaveOpen;
        try
        {
            var h = new byte[100]; ReadAt(h, 0);
            if (!h.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8)) throw new InvalidDataException("Not a SQLite format 3 file.");
            _size = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(16)); if (_size == 1) _size = 65536;
            if (_size < 512 || (_size & (_size - 1)) != 0 || _size > 65536 || _file.Length % _size != 0 || h[18] is not (1 or 2) || h[19] is not (1 or 2)) throw new InvalidDataException("Invalid SQLite header.");
            _usable = _size - h[20]; if (_usable < 480) throw new InvalidDataException("Invalid SQLite usable page size.");
            _pages = _file.Length / _size;
            _encoding = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(56)) switch { 1 => new UTF8Encoding(false, true), 2 => new UnicodeEncoding(false, false, true), 3 => new UnicodeEncoding(true, false, true), _ => throw new NotSupportedException("Unsupported SQLite text encoding.") };
            if (rollbackOnly && (_size != 4096 || h[18] != 1 || h[19] != 1 || h[20] != 0 || BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(56)) != 1))
                throw new NotSupportedException("In-place phase 1 requires 4096-byte pages, UTF-8, no reserved bytes and rollback journal mode. Checkpoint WAL and select journal_mode=DELETE in SQLite first.");
            if (rollbackOnly && (BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(52)) != 0 || BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(44)) != 4))
                throw new NotSupportedException("In-place phase 1 requires schema format 4 and auto_vacuum=NONE.");
            _cachePages = (int)Math.Min(int.MaxValue, cacheBytes / _size);
            ChangeCounter = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(24)); SchemaCookie = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(40));
            UserVersion = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(60)); ApplicationId = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(68));
        }
        catch { if (!leaveOpen) _file.Dispose(); throw; }
    }
    private void ReadAt(Span<byte> destination, long offset)
    {
        while (!destination.IsEmpty) { int n = RandomAccess.Read(_file.SafeFileHandle, destination, offset); if (n == 0) throw new EndOfStreamException(); destination = destination[n..]; offset += n; }
    }
    private byte[] Page(long number)
    {
        if (number < 1 || number > _pages) throw new InvalidDataException("SQLite page outside the file.");
        if (_cache.TryGetValue(number, out var cached)) return cached;
        var b = new byte[_size]; ReadAt(b, (number - 1) * _size);
        if (_cachePages > 0) { if (_cache.Count >= _cachePages) _cache.Clear(); _cache.Add(number, b); } return b;
    }
    internal long Count(long root)
    {
        long count = 0; var seen = new HashSet<long>(); var pending = new Stack<long>(); pending.Push(root);
        while (pending.TryPop(out long number))
        {
            if (!seen.Add(number)) throw new InvalidDataException("Cyclic SQLite table.");
            var page = Page(number); int h = number == 1 ? 100 : 0; int cells = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 3));
            if (page[h] == 13) count += cells;
            else if (page[h] == 5) { for (int i = 0; i < cells; i++) pending.Push(BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 12 + i * 2))))); pending.Push(BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(h + 8))); }
            else throw new NotSupportedException("Only ordinary rowid tables are supported.");
        }
        return count;
    }
    internal Row? Seek(long root, long id)
    {
        long number = root;
        for (int depth = 0; depth < 64; depth++)
        {
            byte[] page = Page(number); int h = number == 1 ? 100 : 0; byte type = page[h];
            if (type is not (5 or 13)) throw new NotSupportedException("Only ordinary rowid tables are supported.");
            int count = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 3)), header = type == 5 ? 12 : 8;
            int lo = 0, hi = count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2, at = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + header + mid * 2));
                if (type == 5) at += 4; else SqliteRecord.ReadVarint(page, ref at);
                long key = unchecked((long)SqliteRecord.ReadVarint(page, ref at)); if (key < id) lo = mid + 1; else hi = mid;
            }
            if (type == 13)
            {
                if (lo == count) return null;
                int at = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 8 + lo * 2)); var row = ReadRow(page, at); return row.Id == id ? row : null;
            }
            number = lo == count ? BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(h + 8)) : BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 12 + lo * 2))));
        }
        throw new InvalidDataException("Cyclic or excessively deep SQLite B-tree.");
    }
    private Row ReadRow(byte[] page, int at)
    {
        ulong size = SqliteRecord.ReadVarint(page, ref at); long id = unchecked((long)SqliteRecord.ReadVarint(page, ref at));
        if (size > 256 * 1024 * 1024) throw new NotSupportedException("SQLite record exceeds the 256 MiB limit.");
        int length = (int)size, local = Local(length, _usable, true);
        if (at + local + (local < length ? 4 : 0) > _usable) throw new InvalidDataException("Invalid SQLite cell payload.");
        if (local == length) return new(id, SqliteRecord.Decode(page.AsSpan(at, length), _encoding));
        var payload = new byte[length]; page.AsSpan(at, local).CopyTo(payload); int copied = local;
        long next = local < length ? BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(at + local)) : 0; var overflow = new HashSet<long>();
        while (copied < length)
        {
            if (!overflow.Add(next)) throw new InvalidDataException("Cyclic SQLite overflow chain.");
            var b = Page(next); int n = Math.Min(length - copied, _usable - 4); b.AsSpan(4, n).CopyTo(payload.AsSpan(copied)); copied += n; next = BinaryPrimitives.ReadUInt32BigEndian(b);
        }
        if (next != 0) throw new InvalidDataException("Unexpected SQLite overflow tail.");
        return new(id, SqliteRecord.Decode(payload, _encoding));
    }
    public IEnumerable<Row> Table(long root) => Walk(root, new HashSet<long>(), 0);
    private IEnumerable<Row> Walk(long number, HashSet<long> seen, int depth)
    {
        if (depth > 64 || !seen.Add(number)) throw new InvalidDataException("Cyclic or excessively deep SQLite B-tree.");
        byte[] page = Page(number); int h = number == 1 ? 100 : 0; byte type = page[h];
        if (type is not (5 or 13)) throw new NotSupportedException("Only ordinary SQLite rowid tables are supported; WITHOUT ROWID/virtual tables are not supported.");
        int count = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 3)), header = type == 5 ? 12 : 8;
        if (h + header + count * 2 > _usable) throw new InvalidDataException("Invalid SQLite cell pointer array.");
        for (int i = 0; i < count; i++)
        {
            int at = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + header + i * 2));
            if (at < h + header + count * 2 || at >= _usable) throw new InvalidDataException("Invalid SQLite cell offset.");
            if (type == 5)
            { long child = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(at)); foreach (var row in Walk(child, seen, depth + 1)) yield return row; }
            else
            {
                yield return ReadRow(page, at);
            }
        }
        if (type == 5) foreach (var row in Walk(BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(h + 8)), seen, depth + 1)) yield return row;
    }
    internal static int Local(int length, int usable, bool table)
    {
        int max = table ? usable - 35 : (usable - 12) * 64 / 255 - 23, min = (usable - 12) * 32 / 255 - 23;
        if (length <= max) return length; int k = min + (length - min) % (usable - 4); return k <= max ? k : min;
    }
    public void Dispose() { _cache.Clear(); if (!_leaveOpen) _file.Dispose(); }
}

internal sealed class SqliteFileWriter : IDisposable
{
    private const int Size = 4096;
    private readonly FileStream _file;
    private uint _pages = 1;
    internal SqliteFileWriter(string path) { _file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536); _file.SetLength(Size); }
    private uint Page(byte[] bytes) { uint number = ++_pages; Store(number, bytes); return number; }
    private void Store(uint number, byte[] bytes) { _file.Position = ((long)number - 1) * Size; _file.Write(bytes); }
    private byte[] Cell(byte[] payload, long? rowid, uint? child = null)
    {
        using var cell = new MemoryStream(); Span<byte> number = stackalloc byte[4];
        if (child is uint c) { BinaryPrimitives.WriteUInt32BigEndian(number, c); cell.Write(number); }
        cell.Write(SqliteRecord.Varint((ulong)payload.Length)); if (rowid is long id) cell.Write(SqliteRecord.Varint(unchecked((ulong)id)));
        int local = SqliteFileReader.Local(payload.Length, Size, rowid is not null); cell.Write(payload.AsSpan(0, local));
        if (local < payload.Length)
        {
            uint next = 0;
            for (int end = payload.Length; end > local;)
            {
                int start = local + (end - local - 1) / (Size - 4) * (Size - 4); var b = new byte[Size];
                BinaryPrimitives.WriteUInt32BigEndian(b, next); payload.AsSpan(start, end - start).CopyTo(b.AsSpan(4)); next = Page(b); end = start;
            }
            BinaryPrimitives.WriteUInt32BigEndian(number, next); cell.Write(number);
        }
        return cell.ToArray();
    }
    private static byte[] Node(byte type, List<byte[]> cells, uint right = 0, int offset = 0)
    {
        var b = new byte[Size]; b[offset] = type; int header = type is 2 or 5 ? 12 : 8;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(offset + 3), (ushort)cells.Count);
        if (header == 12) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(offset + 8), right);
        int at = Size;
        for (int i = 0; i < cells.Count; i++) { at -= cells[i].Length; cells[i].CopyTo(b, at); BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(offset + header + i * 2), (ushort)at); }
        if (at < offset + header + cells.Count * 2) throw new InvalidDataException("SQLite output page overflow.");
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(offset + 5), (ushort)at); return b;
    }
    public uint Table(IEnumerable<(long Id, object?[] Values)> rows)
    {
        var nodes = new List<(uint Page, long Max)>(); var cells = new List<byte[]>(); int used = 8; long max = 0;
        foreach (var row in rows)
        {
            byte[] cell = Cell(SqliteRecord.Encode(row.Values), row.Id);
            if (used + 2 + cell.Length > Size && cells.Count > 0) { nodes.Add((Page(Node(13, cells)), max)); cells.Clear(); used = 8; }
            cells.Add(cell); used += 2 + cell.Length; max = row.Id;
        }
        nodes.Add((Page(Node(13, cells)), max));
        while (nodes.Count > 1)
        {
            var next = new List<(uint Page, long Max)>(); int at = 0;
            while (at < nodes.Count)
            {
                cells.Clear(); used = 12;
                while (at + 1 < nodes.Count)
                {
                    using var m = new MemoryStream(); var n = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(n, nodes[at].Page); m.Write(n); m.Write(SqliteRecord.Varint(unchecked((ulong)nodes[at].Max))); byte[] c = m.ToArray();
                    if (used + 2 + c.Length > Size) break; cells.Add(c); used += 2 + c.Length; at++;
                }
                next.Add((Page(Node(5, cells, nodes[at].Page)), nodes[at].Max)); at++;
            }
            nodes = next;
        }
        return nodes[0].Page;
    }
    public uint Index(IReadOnlyList<object?[]> records)
    {
        var children = new List<uint>(); var separators = new List<byte[]>(); var cells = new List<byte[]>(); int used = 8;
        for (int at = 0; at < records.Count; at++)
        {
            byte[] payload = SqliteRecord.Encode(records[at]); int local = SqliteFileReader.Local(payload.Length, Size, false);
            int size = SqliteRecord.Varint((ulong)payload.Length).Length + local + (local < payload.Length ? 4 : 0);
            if (used + 2 + size > Size && cells.Count > 0)
            { children.Add(Page(Node(10, cells))); separators.Add(payload); cells.Clear(); used = 8; }
            else { cells.Add(Cell(payload, null)); used += 2 + size; }
        }
        children.Add(Page(Node(10, cells)));
        while (children.Count > 1)
        {
            var nextChildren = new List<uint>(); var nextSeparators = new List<byte[]>(); int at = 0;
            while (at < children.Count)
            {
                cells.Clear(); used = 12;
                while (at < separators.Count)
                {
                    // Inspect size before allocating overflow pages, to avoid orphan pages.
                    int local = SqliteFileReader.Local(separators[at].Length, Size, false);
                    int size = 4 + SqliteRecord.Varint((ulong)separators[at].Length).Length + local + (local < separators[at].Length ? 4 : 0);
                    if (used + 2 + size > Size) break;
                    cells.Add(Cell(separators[at], null, children[at])); used += 2 + size; at++;
                }
                nextChildren.Add(Page(Node(2, cells, children[at]))); at++;
                if (at < children.Count) nextSeparators.Add(separators[at - 1]);
            }
            children = nextChildren; separators = nextSeparators;
        }
        return children[0];
    }
    public void Finish(List<object?[]> schema, uint changeCounter = 1, uint schemaCookie = 1, uint userVersion = 0, uint applicationId = 0)
    {
        uint root = Table(schema.Select((r, i) => ((long)i + 1, r))); var first = Node(5, [], root, 100);
        "SQLite format 3\0"u8.CopyTo(first); BinaryPrimitives.WriteUInt16BigEndian(first.AsSpan(16), Size);
        first[18] = first[19] = 1; first[21] = 64; first[22] = first[23] = 32;
        foreach (int offset in new[] { 24, 40, 56, 92 }) BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(offset), 1);
        BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(24), changeCounter); BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(92), changeCounter);
        BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(40), schemaCookie); BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(60), userVersion); BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(68), applicationId);
        BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(28), _pages); BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(44), 4);
        BinaryPrimitives.WriteUInt32BigEndian(first.AsSpan(96), 3054001); Store(1, first); _file.Flush(true);
    }
    public void Dispose() => _file.Dispose();
}
