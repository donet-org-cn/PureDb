using System.Buffers.Binary;

namespace PureDb.SqliteCompatibility;
// 事务私有页：写好回滚日志之前不触碰原始文件内容。
// 该增量路径保持 B-tree 拓扑和溢出页链接。
/// <summary>在事务私有缓存中编辑 SQLite 页，仅实现无需改变 B-tree 拓扑的增量写入子集。</summary>
/// <param name="file">当前 SQLite 主数据库文件句柄。</param>
/// <param name="pageBudget">干净页缓存预算，单位为字节。</param>
internal sealed class SqlitePageEditor(FileStream file, long pageBudget)
{
    private const int PageSize = 4096;
    private readonly uint _pageCount = checked((uint)(file.Length / PageSize));
    private readonly Dictionary<uint, byte[]> _read = [];
    /// <summary>本事务修改的页号到完整页副本映射。</summary>
    internal Dictionary<uint, byte[]> Dirty { get; } = [];

    /// <summary>读取事务私有页或受预算限制的干净页缓存。</summary>
    /// <param name="number">从 1 开始的 SQLite 页号。</param>
    /// <returns>本次编码或复制得到的字节数组。</returns>
    private byte[] Page(uint number)
    {
        if (number == 0 || number > _pageCount)
            throw new InvalidDataException("SQLite page outside the database.");
        if (Dirty.TryGetValue(number, out var changed))
            return changed;
        if (_read.TryGetValue(number, out var cached))
            return cached;
        var page = new byte[PageSize];
        int copied = 0;
        while (copied < page.Length)
        {
            int n = RandomAccess.Read(file.SafeFileHandle, page.AsSpan(copied), ((long)number - 1) * PageSize + copied);
            if (n == 0)
                throw new EndOfStreamException();
            copied += n;
        }

        // 干净页缓存单独受预算限制；清空它不会丢失待写的脏页。
        if ((long)_read.Count * PageSize >= pageBudget)
            _read.Clear();
        _read[number] = page;
        return page;
    }

    /// <summary>首次修改时复制原页并加入脏页集合，保持提交前原文件不变。</summary>
    /// <param name="number">从 1 开始的 SQLite 页号。</param>
    /// <returns>本次编码或复制得到的字节数组。</returns>
    private byte[] Writable(uint number)
    {
        if (!Dirty.TryGetValue(number, out var page))
            Dirty.Add(number, page = Page(number).ToArray());
        _read.Remove(number);
        return page;
    }

    /// <summary>在不分裂或合并 B-tree 的前提下尝试替换记录；不适用时返回 false。</summary>
    /// <param name="root">从 1 开始的 SQLite B-tree 根页号。</param>
    /// <param name="rowid">SQLite 表 B-tree 中的整数行标识。</param>
    /// <param name="payload">已编码的 SQLite 记录载荷。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    internal bool TryReplace(uint root, long rowid, byte[] payload)
    {
        uint number = root;
        for (int depth = 0; depth < 64; depth++)
        {
            byte[] page = Page(number);
            int h = number == 1 ? 100 : 0;
            byte type = page[h];
            if (type is not (5 or 13))
                throw new InvalidDataException("Expected an ordinary rowid B-tree page.");
            int count = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + 3)), header = type == 5 ? 12 : 8;
            if (h + header + count * 2 > PageSize)
                throw new InvalidDataException("Invalid cell pointer array.");
            int Cell(int index)
            {
                int at = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(h + header + index * 2));
                if (at < h + header + count * 2 || at >= PageSize)
                    throw new InvalidDataException("Invalid cell offset.");
                return at;
            }

            int lo = 0, hi = count;
            while (lo < hi)
            {
                int mid = lo + (hi - lo) / 2, at = Cell(mid);
                if (type == 5)
                    at += 4;
                else
                    SqliteRecord.ReadVarint(page, ref at);
                long key = unchecked((long)SqliteRecord.ReadVarint(page, ref at));
                if (key < rowid)
                    lo = mid + 1;
                else
                    hi = mid;
            }

            if (type == 5)
            {
                number = lo == count ? BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(h + 8)) : BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(Cell(lo)));
                continue;
            }

            if (lo == count)
                return false;
            int offset = Cell(lo);
            ulong length = SqliteRecord.ReadVarint(page, ref offset);
            long id = unchecked((long)SqliteRecord.ReadVarint(page, ref offset));
            if (id != rowid)
                return false;
            if (length != (ulong)payload.Length)
            {
                // 仅当全部现有单元仍可放入同一叶页时，才允许调整完全本地记录的大小；
                // 此路径不分配新页。
                if (length > PageSize - 35 || payload.Length > PageSize - 35)
                    return false;
                var cells = new List<byte[]>(count);
                for (int i = 0; i < count; i++)
                {
                    int begin = Cell(i), at = begin;
                    ulong size = SqliteRecord.ReadVarint(page, ref at);
                    SqliteRecord.ReadVarint(page, ref at);
                    if (size > int.MaxValue)
                        return false;
                    int bytes = SqliteFileReader.Local((int)size, PageSize, true);
                    int cellLength = checked(at - begin + bytes + (bytes < (int)size ? 4 : 0));
                    if (begin + (long)cellLength > PageSize)
                        throw new InvalidDataException("Invalid leaf cell extent.");
                    if (i == lo)
                    {
                        byte[] sizePrefix = SqliteRecord.Varint((ulong)payload.Length), idPrefix = SqliteRecord.Varint(unchecked((ulong)rowid));
                        var replacement = new byte[sizePrefix.Length + idPrefix.Length + payload.Length];
                        sizePrefix.CopyTo(replacement, 0);
                        idPrefix.CopyTo(replacement, sizePrefix.Length);
                        payload.CopyTo(replacement, sizePrefix.Length + idPrefix.Length);
                        cells.Add(replacement);
                    }
                    else
                        cells.Add(page.AsSpan(begin, cellLength).ToArray());
                }

                if (h + 8 + count * 2 + cells.Sum(c => c.Length) > PageSize)
                    return false;
                var rebuilt = new byte[PageSize];
                if (h != 0)
                    page.AsSpan(0, h).CopyTo(rebuilt);
                rebuilt[h] = 13;
                BinaryPrimitives.WriteUInt16BigEndian(rebuilt.AsSpan(h + 3), checked((ushort)count));
                int content = PageSize;
                for (int i = 0; i < count; i++)
                {
                    content -= cells[i].Length;
                    cells[i].CopyTo(rebuilt, content);
                    BinaryPrimitives.WriteUInt16BigEndian(rebuilt.AsSpan(h + 8 + i * 2), checked((ushort)content));
                }

                BinaryPrimitives.WriteUInt16BigEndian(rebuilt.AsSpan(h + 5), checked((ushort)content));
                Dirty[number] = rebuilt;
                _read.Remove(number);
                return (long)Dirty.Count * PageSize <= pageBudget;
            }

            int local = SqliteFileReader.Local(payload.Length, PageSize, true);
            if (offset + local + (local < payload.Length ? 4 : 0) > PageSize)
                throw new InvalidDataException("Invalid cell payload.");
            uint next = local < payload.Length ? BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(offset + local)) : 0;
            payload.AsSpan(0, local).CopyTo(Writable(number).AsSpan(offset));
            int copied = local;
            var seen = new HashSet<uint>();
            while (copied < payload.Length)
            {
                if (!seen.Add(next))
                    throw new InvalidDataException("Cyclic overflow chain.");
                byte[] overflow = Page(next);
                uint following = BinaryPrimitives.ReadUInt32BigEndian(overflow);
                int n = Math.Min(payload.Length - copied, PageSize - 4);
                payload.AsSpan(copied, n).CopyTo(Writable(next).AsSpan(4));
                copied += n;
                next = following;
            }

            if (next != 0)
                throw new InvalidDataException("Unexpected overflow tail.");
            return (long)Dirty.Count * PageSize <= pageBudget;
        }

        throw new InvalidDataException("Cyclic or excessively deep B-tree.");
    }

    /// <summary>更新文件头变化计数器及相关校验版本标记。</summary>
    internal void IncrementChangeCounter()
    {
        var header = Writable(1);
        uint counter = unchecked(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(24)) + 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), counter);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(92), counter);
    }

    // 受限索引维护路径：仅在同一叶页内移动 (INTEGER key, rowid) 索引项。
    // 父分隔键保持有效，不执行分裂、合并或空闲链表修改。
    /// <summary>仅在同一叶页内尝试维护非唯一整数索引，父分隔键必须保持有效。</summary>
    /// <param name="root">从 1 开始的 SQLite B-tree 根页号。</param>
    /// <param name="oldKey">索引项修改前的整数键。</param>
    /// <param name="newKey">索引项修改后的整数键。</param>
    /// <param name="rowid">SQLite 表 B-tree 中的整数行标识。</param>
    /// <returns>匹配、校验或操作成功时为 true，否则为 false；具体条件见方法说明。</returns>
    internal bool TryReplaceIntegerIndex(uint root, long oldKey, long newKey, long rowid)
    {
        (uint Page, int Slot, bool Found, bool Leaf) Find(long key)
        {
            uint number = root;
            for (int depth = 0; depth < 64; depth++)
            {
                byte[] page = Page(number);
                byte type = page[0];
                if (type is not (2 or 10))
                    throw new InvalidDataException("Invalid SQLite index page.");
                int count = BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(3)), header = type == 2 ? 12 : 8;
                if (header + count * 2 > PageSize)
                    throw new InvalidDataException("Invalid index cell array.");
                int At(int i) => BinaryPrimitives.ReadUInt16BigEndian(page.AsSpan(header + i * 2));
                int Compare(int slot)
                {
                    int at = At(slot);
                    if (at < header + count * 2 || at >= PageSize)
                        throw new InvalidDataException("Invalid index cell offset.");
                    if (type == 2)
                        at += 4;
                    ulong size = SqliteRecord.ReadVarint(page, ref at);
                    if (size > 64 || at + (long)size > PageSize)
                        throw new NotSupportedException("Integer index record exceeds the incremental subset.");
                    var values = SqliteRecord.Decode(page.AsSpan(at, (int)size), System.Text.Encoding.UTF8);
                    if (values is not [long storedKey, long storedId])
                        throw new NotSupportedException("Incremental index requires two integer fields.");
                    int n = storedKey.CompareTo(key);
                    return n == 0 ? storedId.CompareTo(rowid) : n;
                }

                int lo = 0, hi = count;
                while (lo < hi)
                {
                    int mid = lo + (hi - lo) / 2;
                    if (Compare(mid) < 0)
                        lo = mid + 1;
                    else
                        hi = mid;
                }

                bool found = lo < count && Compare(lo) == 0;
                if (type == 10 || found)
                    return (number, lo, found, type == 10);
                number = lo == count ? BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(8)) : BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(At(lo)));
            }

            throw new InvalidDataException("Cyclic or excessively deep index.");
        }

        try
        {
            var old = Find(oldKey);
            var target = Find(newKey);
            if (!old.Found || !old.Leaf || !target.Leaf || target.Found || old.Page != target.Page)
                return false;
            byte[] original = Page(old.Page);
            int count = BinaryPrimitives.ReadUInt16BigEndian(original.AsSpan(3));
            var cells = new List<byte[]>(count);
            for (int i = 0; i < count; i++)
            {
                int begin = BinaryPrimitives.ReadUInt16BigEndian(original.AsSpan(8 + i * 2)), at = begin;
                ulong length = SqliteRecord.ReadVarint(original, ref at);
                if (length > 64 || begin < 8 + count * 2 || at + (long)length > PageSize)
                    return false;
                if (i != old.Slot)
                    cells.Add(original.AsSpan(begin, checked(at - begin + (int)length)).ToArray());
            }

            byte[] payload = SqliteRecord.Encode([newKey, rowid]), prefix = SqliteRecord.Varint((ulong)payload.Length);
            var replacement = new byte[prefix.Length + payload.Length];
            prefix.CopyTo(replacement, 0);
            payload.CopyTo(replacement, prefix.Length);
            cells.Insert(target.Slot - (old.Slot < target.Slot ? 1 : 0), replacement);
            if (8 + count * 2 + cells.Sum(c => c.Length) > PageSize)
                return false;
            var rebuilt = new byte[PageSize];
            rebuilt[0] = 10;
            BinaryPrimitives.WriteUInt16BigEndian(rebuilt.AsSpan(3), checked((ushort)count));
            int content = PageSize;
            for (int i = 0; i < count; i++)
            {
                content -= cells[i].Length;
                cells[i].CopyTo(rebuilt, content);
                BinaryPrimitives.WriteUInt16BigEndian(rebuilt.AsSpan(8 + i * 2), checked((ushort)content));
            }

            BinaryPrimitives.WriteUInt16BigEndian(rebuilt.AsSpan(5), checked((ushort)content));
            Dirty[old.Page] = rebuilt;
            _read.Remove(old.Page);
            return (long)Dirty.Count * PageSize <= pageBudget;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
