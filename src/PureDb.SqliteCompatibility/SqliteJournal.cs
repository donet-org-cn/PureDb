using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PureDb.SqliteCompatibility;
// SQLite 格式 3 回滚日志，使用一个完整同步的头部和页段。
// 锁由 SqliteStorage 持有；主数据库句柄不被替换。
/// <summary>SQLite 回滚日志的生成和热日志恢复；先持久化旧页，再修改主文件。</summary>
internal static class SqliteJournal
{
    private const int PageSize = 4096, SectorSize = 4096, RecordSize = PageSize + 8;
    /// <summary>SQLite 回滚日志的有效头部魔数。</summary>
    private static ReadOnlySpan<byte> Magic => [0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7];

    /// <summary>使用日志随机数计算 SQLite 页校验和。</summary>
    /// <param name="page">当前数据页或热点页；具体类型由当前签名确定。</param>
    /// <param name="nonce">SQLite 回滚日志头中的随机校验种子。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    private static uint Checksum(ReadOnlySpan<byte> page, uint nonce)
    {
        for (int at = page.Length - 200; at >= 0; at -= 200)
            nonce = unchecked(nonce + page[at]);
        return nonce;
    }

    /// <summary>从主文件或回滚日志的固定偏移完整读取字节。</summary>
    /// <param name="file">本操作使用的文件句柄。</param>
    /// <param name="bytes">接收完整文件读取结果的目标片段。</param>
    /// <param name="offset">文件或缓冲区中的字节偏移。</param>
    private static void ReadAt(FileStream file, Span<byte> bytes, long offset)
    {
        while (!bytes.IsEmpty)
        {
            int n = RandomAccess.Read(file.SafeFileHandle, bytes, offset);
            if (n == 0)
                throw new EndOfStreamException();
            bytes = bytes[n..];
            offset += n;
        }
    }

    /// <summary>比较完整候选镜像与主文件，在持久化旧页日志后写入发生变化的页。</summary>
    /// <param name="database">持有独占锁的 SQLite 主文件句柄。</param>
    /// <param name="path">当前数据库或索引文件路径。</param>
    /// <param name="image">本次候选 SQLite 完整数据库镜像。</param>
    /// <param name="stage">持久化阶段测试钩子，用于在指定提交步骤注入进程故障。</param>
    internal static void Commit(FileStream database, string path, string image, Action<string>? stage)
    {
        using var input = File.OpenRead(image);
        var changed = new List<uint>();
        var before = new byte[PageSize];
        var after = new byte[PageSize];
        uint oldPages = checked((uint)(database.Length / PageSize)), newPages = checked((uint)(input.Length / PageSize));
        for (uint page = 1; page <= Math.Max(oldPages, newPages); page++)
        {
            if (page <= oldPages)
                ReadAt(database, before, ((long)page - 1) * PageSize);
            if (page <= newPages)
                ReadAt(input, after, ((long)page - 1) * PageSize);
            // SequenceEqual 使用运行时在 x64 和 ARM64 上的 SIMD 实现。
            if (page > oldPages || page > newPages || !before.AsSpan().SequenceEqual(after))
                changed.Add(page);
        }

        CommitPages(database, path, changed, input.Length, (page, buffer) => ReadAt(input, buffer, ((long)page - 1) * PageSize), stage, journalAll: true);
    }

    /// <summary>将事务私有脏页通过旧页回滚日志原子提交到现有主文件。</summary>
    /// <param name="database">持有独占锁的 SQLite 主文件句柄。</param>
    /// <param name="path">当前数据库或索引文件路径。</param>
    /// <param name="pages">待提交脏页的映射，或成功构造的页编辑器。</param>
    /// <param name="stage">持久化阶段测试钩子，用于在指定提交步骤注入进程故障。</param>
    internal static void CommitIncremental(FileStream database, string path, Dictionary<uint, byte[]> pages, Action<string>? stage)
    {
        if (!pages.ContainsKey(1) || pages.Any(p => p.Key == 0 || p.Key > database.Length / PageSize || p.Value.Length != PageSize))
            throw new InvalidDataException("Invalid incremental page transaction.");
        CommitPages(database, path, pages.Keys.Order().ToArray(), database.Length, (page, buffer) => pages[page].CopyTo(buffer, 0), stage, journalAll: false);
    }

    /// <summary>按日志先于主文件的顺序提交页变更，支持测试阶段注入及失败回滚。</summary>
    /// <param name="database">持有独占锁的 SQLite 主文件句柄。</param>
    /// <param name="path">当前数据库或索引文件路径。</param>
    /// <param name="changed">需要提交的页号集合。</param>
    /// <param name="newLength">事务提交后主数据库文件的目标长度。</param>
    /// <param name="readPage">按页号读取候选新页完整内容的回调。</param>
    /// <param name="stage">持久化阶段测试钩子，用于在指定提交步骤注入进程故障。</param>
    /// <param name="journalAll">是否记录全部旧页；关闭时仅记录被覆盖的旧页。</param>
    private static void CommitPages(FileStream database, string path, IReadOnlyList<uint> changed, long newLength, Action<uint, byte[]> readPage, Action<string>? stage, bool journalAll)
    {
        if (changed.Count == 0)
            return;
        string journalPath = path + "-journal";
        if (File.Exists(journalPath))
            throw new IOException("Unresolved journal before commit.");
        uint pages = checked((uint)(database.Length / PageSize));
        uint records = journalAll ? pages : checked((uint)changed.Count);
        uint nonce = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
        using (var journal = new FileStream(journalPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536))
        {
            // 所有需要恢复的旧页持久化后才写入有效日志魔数；此前崩溃时主库尚未改变，
            // 零头部的日志可以安全丢弃。
            var header = new byte[SectorSize];
            journal.Write(header);
            var record = new byte[RecordSize];
            for (uint i = 0; i < records; i++)
            {
                uint page = journalAll ? i + 1 : changed[checked((int)i)];
                BinaryPrimitives.WriteUInt32BigEndian(record, page);
                ReadAt(database, record.AsSpan(4, PageSize), ((long)page - 1) * PageSize);
                BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(PageSize + 4), Checksum(record.AsSpan(4, PageSize), nonce));
                journal.Write(record);
            }

            journal.Flush(true);
            stage?.Invoke("before-journal-header");
            Magic.CopyTo(header);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), records);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), nonce);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), pages);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), SectorSize);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), PageSize);
            journal.Position = 0;
            journal.Write(header);
            journal.Flush(true);
        }

        stage?.Invoke("after-journal-sync");
        var buffer = new byte[PageSize];
        bool first = true;
        foreach (uint page in changed)
        {
            long offset = ((long)page - 1) * PageSize;
            if (offset >= newLength)
                continue;
            readPage(page, buffer);
            RandomAccess.Write(database.SafeFileHandle, buffer, offset);
            if (first && stage is not null)
            {
                database.Flush(true);
                stage("after-first-page");
            }

            first = false;
        }

        if (database.Length != newLength)
            database.SetLength(newLength);
        database.Flush(true);
        stage?.Invoke("after-database-sync");
        File.Delete(journalPath); // SQLite DELETE-mode commit point, after database fsync.
        stage?.Invoke("after-commit");
    }

    /// <summary>检查热回滚日志，完整验证后恢复旧页并同步主文件，再删除日志。</summary>
    /// <param name="database">持有独占锁的 SQLite 主文件句柄。</param>
    /// <param name="path">当前数据库或索引文件路径。</param>
    internal static void Recover(FileStream database, string path)
    {
        string journalPath = path + "-journal";
        if (!File.Exists(journalPath))
            return;
        using (var journal = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (journal.Length == 0)
            {
            } // Truncate-mode committed journal.
            else
            {
                if (journal.Length < 8)
                    throw new InvalidDataException("Incomplete SQLite journal header; recovery requires inspection.");
                var header = new byte[28];
                journal.ReadExactly(header.AsSpan(0, 8));
                if (header.AsSpan(0, 8).IndexOfAnyExcept((byte)0) < 0)
                {
                } // Unarmed or persisted committed journal.
                else
                {
                    if (!header.AsSpan(0, 8).SequenceEqual(Magic) || journal.Length < SectorSize)
                        throw new InvalidDataException("Invalid SQLite journal magic.");
                    journal.ReadExactly(header.AsSpan(8));
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)), nonce = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12)), pages = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16));
                    int sector = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)), size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(24));
                    if (size != PageSize || sector != SectorSize || count == uint.MaxValue || count == 0 || journal.Length != SectorSize + (long)count * RecordSize)
                        throw new NotSupportedException("Phase 1 recovery supports a complete single-header 4096-byte journal without a super-journal. Recover other native journals in SQLite first.");
                    var record = new byte[RecordSize];
                    var seen = new HashSet<uint>();
                    // 先验证整个日志，再修改数据库的任何字节。
                    for (uint i = 0; i < count; i++)
                    {
                        ReadAt(journal, record, SectorSize + (long)i * RecordSize);
                        uint page = BinaryPrimitives.ReadUInt32BigEndian(record);
                        if (page == 0 || page > pages || !seen.Add(page) || Checksum(record.AsSpan(4, PageSize), nonce) != BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(PageSize + 4)))
                            throw new InvalidDataException("SQLite rollback journal page/checksum is invalid; database left untouched.");
                    }

                    for (uint i = 0; i < count; i++)
                    {
                        ReadAt(journal, record, SectorSize + (long)i * RecordSize);
                        uint page = BinaryPrimitives.ReadUInt32BigEndian(record);
                        RandomAccess.Write(database.SafeFileHandle, record.AsSpan(4, PageSize), ((long)page - 1) * PageSize);
                    }

                    database.SetLength((long)pages * PageSize);
                    database.Flush(true);
                }
            }
        }

        File.Delete(journalPath);
    }
}
