using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("PureDb.SqliteCompatibility")]
[assembly: InternalsVisibleTo("PureDb.CompatibilityTests")]
namespace PureDb;

internal interface ISqlStorage : IDisposable, IAsyncDisposable
{
    T Read<T>(Func<ISqlSnapshot, T> action);
    ValueTask<int> TransformAsync(Func<ISqlSnapshot, (Mutation[] Mutations, int Affected)> transform, CancellationToken token);
    ValueTask CompactAsync(CancellationToken token = default);
    ValueTask CheckpointAsync(CancellationToken token = default);
    ValueTask DrainForCloseAsync();
}
internal interface ISqlSnapshot
{
    bool HasPersistentIndexes { get; }
    string Path { get; }
    int Generation { get; }
    long Length { get; }
    long SourceTime { get; }
    long Version { get; }
    long KeyVersion(long key);
    int CountKey(long key);
    IEnumerable<long> Ids(long key);
    long Count { get; }
    DbRecord? Get(long id);
    bool Contains(long id) => Get(id) is not null;
    ReadOnlySpan<byte> Value(long id);
    ReadOnlyMemory<byte> ValueMemory(long id);
    IEnumerable<DbRecord> Range(long min, long max);
    DiskIndex.Item IndexItem(long id, long key);
    ReadOnlySpan<byte> IndexValue(DiskIndex.Item entry);
    ReadOnlyMemory<byte> IndexMemory(DiskIndex.Item entry);
    IEnumerable<DiskIndex.Item> TableEntries(int tableId);
    IEnumerable<DiskIndex.Item> TableEntriesRange(int tableId, long min, long max) =>
        TableEntries(tableId).Where(e => e.Id >= min && e.Id <= max);
}
