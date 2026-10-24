using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PureDb.SqliteCompatibility;

// SQLite format 3 rollback journal, one fully synced header and page segment.
// Lock ownership is supplied by SqliteStorage. The database handle is never replaced.
internal static class SqliteJournal
{
    private const int PageSize = 4096, SectorSize = 4096, RecordSize = PageSize + 8;
    private static ReadOnlySpan<byte> Magic => [0xd9, 0xd5, 0x05, 0xf9, 0x20, 0xa1, 0x63, 0xd7];
    private static uint Checksum(ReadOnlySpan<byte> page, uint nonce)
    { for (int at = page.Length - 200; at >= 0; at -= 200) nonce = unchecked(nonce + page[at]); return nonce; }
    private static void ReadAt(FileStream file, Span<byte> bytes, long offset)
    { while (!bytes.IsEmpty) { int n = RandomAccess.Read(file.SafeFileHandle, bytes, offset); if (n == 0) throw new EndOfStreamException(); bytes = bytes[n..]; offset += n; } }
    internal static void Commit(FileStream database, string path, string image, Action<string>? stage)
    {
        using var input = File.OpenRead(image);
        var changed = new List<uint>(); var before = new byte[PageSize]; var after = new byte[PageSize];
        uint oldPages = checked((uint)(database.Length / PageSize)), newPages = checked((uint)(input.Length / PageSize));
        for (uint page = 1; page <= Math.Max(oldPages, newPages); page++)
        {
            if (page <= oldPages) ReadAt(database, before, ((long)page - 1) * PageSize);
            if (page <= newPages) ReadAt(input, after, ((long)page - 1) * PageSize);
            // SequenceEqual uses the runtime's SIMD implementation on x64 and ARM64.
            if (page > oldPages || page > newPages || !before.AsSpan().SequenceEqual(after)) changed.Add(page);
        }
        CommitPages(database, path, changed, input.Length,
            (page, buffer) => ReadAt(input, buffer, ((long)page - 1) * PageSize), stage, journalAll: true);
    }
    internal static void CommitIncremental(FileStream database, string path, Dictionary<uint, byte[]> pages, Action<string>? stage)
    {
        if (!pages.ContainsKey(1) || pages.Any(p => p.Key == 0 || p.Key > database.Length / PageSize || p.Value.Length != PageSize))
            throw new InvalidDataException("Invalid incremental page transaction.");
        CommitPages(database, path, pages.Keys.Order().ToArray(), database.Length,
            (page, buffer) => pages[page].CopyTo(buffer, 0), stage, journalAll: false);
    }
    private static void CommitPages(FileStream database, string path, IReadOnlyList<uint> changed, long newLength, Action<uint, byte[]> readPage, Action<string>? stage, bool journalAll)
    {
        if (changed.Count == 0) return;
        string journalPath = path + "-journal";
        if (File.Exists(journalPath)) throw new IOException("Unresolved journal before commit.");
        uint pages = checked((uint)(database.Length / PageSize));
        uint records = journalAll ? pages : checked((uint)changed.Count);
        uint nonce = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
        using (var journal = new FileStream(journalPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536))
        {
            // No valid magic until every required original page is durable; a crash before that
            // point leaves the main database unchanged and the zero header is disposable.
            var header = new byte[SectorSize]; journal.Write(header);
            var record = new byte[RecordSize];
            for (uint i = 0; i < records; i++)
            {
                uint page = journalAll ? i + 1 : changed[checked((int)i)];
                BinaryPrimitives.WriteUInt32BigEndian(record, page); ReadAt(database, record.AsSpan(4, PageSize), ((long)page - 1) * PageSize);
                BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(PageSize + 4), Checksum(record.AsSpan(4, PageSize), nonce)); journal.Write(record);
            }
            journal.Flush(true); stage?.Invoke("before-journal-header");
            Magic.CopyTo(header); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), records);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), nonce); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), pages);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(20), SectorSize); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(24), PageSize);
            journal.Position = 0; journal.Write(header); journal.Flush(true);
        }
        stage?.Invoke("after-journal-sync");
        var buffer = new byte[PageSize]; bool first = true;
        foreach (uint page in changed)
        {
            long offset = ((long)page - 1) * PageSize;
            if (offset >= newLength) continue;
            readPage(page, buffer); RandomAccess.Write(database.SafeFileHandle, buffer, offset);
            if (first && stage is not null) { database.Flush(true); stage("after-first-page"); }
            first = false;
        }
        if (database.Length != newLength) database.SetLength(newLength);
        database.Flush(true);
        stage?.Invoke("after-database-sync");
        File.Delete(journalPath); // SQLite DELETE-mode commit point, after database fsync.
        stage?.Invoke("after-commit");
    }
    internal static void Recover(FileStream database, string path)
    {
        string journalPath = path + "-journal"; if (!File.Exists(journalPath)) return;
        using (var journal = new FileStream(journalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (journal.Length == 0) { } // Truncate-mode committed journal.
            else
            {
                if (journal.Length < 8) throw new InvalidDataException("Incomplete SQLite journal header; recovery requires inspection.");
                var header = new byte[28]; journal.ReadExactly(header.AsSpan(0, 8));
                if (header.AsSpan(0, 8).IndexOfAnyExcept((byte)0) < 0) { } // Unarmed or persisted committed journal.
                else
                {
                    if (!header.AsSpan(0, 8).SequenceEqual(Magic) || journal.Length < SectorSize) throw new InvalidDataException("Invalid SQLite journal magic.");
                    journal.ReadExactly(header.AsSpan(8));
                    uint count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)), nonce = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12)), pages = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16));
                    int sector = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)), size = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(24));
                    if (size != PageSize || sector != SectorSize || count == uint.MaxValue || count == 0 || journal.Length != SectorSize + (long)count * RecordSize)
                        throw new NotSupportedException("Phase 1 recovery supports a complete single-header 4096-byte journal without a super-journal. Recover other native journals in SQLite first.");
                    var record = new byte[RecordSize]; var seen = new HashSet<uint>();
                    // Validate the entire journal before modifying any database byte.
                    for (uint i = 0; i < count; i++)
                    {
                        ReadAt(journal, record, SectorSize + (long)i * RecordSize); uint page = BinaryPrimitives.ReadUInt32BigEndian(record);
                        if (page == 0 || page > pages || !seen.Add(page) || Checksum(record.AsSpan(4, PageSize), nonce) != BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(PageSize + 4)))
                            throw new InvalidDataException("SQLite rollback journal page/checksum is invalid; database left untouched.");
                    }
                    for (uint i = 0; i < count; i++)
                    {
                        ReadAt(journal, record, SectorSize + (long)i * RecordSize); uint page = BinaryPrimitives.ReadUInt32BigEndian(record);
                        RandomAccess.Write(database.SafeFileHandle, record.AsSpan(4, PageSize), ((long)page - 1) * PageSize);
                    }
                    database.SetLength((long)pages * PageSize); database.Flush(true);
                }
            }
        }
        File.Delete(journalPath);
    }
}
