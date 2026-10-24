namespace PureDb;
/// <summary>Full confirms after a disk flush; Buffered can lose acknowledged writes on power loss.</summary>
public enum Durability { Full, Buffered }
public enum DatabaseAccess { Exclusive, Shared }
public sealed class DatabaseOptions
{
    public DatabaseAccess Access { get; init; } = DatabaseAccess.Exclusive;
    public long CacheBytes { get; init; } = 64 * 1024 * 1024;
    public Durability Durability { get; init; } = Durability.Full;
    public int QueueCapacity { get; init; } = 4096;
    public int MaxGroupRequests { get; init; } = 128;
    public int MaxTransactionBytes { get; init; } = 64 * 1024 * 1024;
    /// <summary>Replay and verify the entire data log at open instead of using the lazy persistent index.</summary>
    public bool VerifyOnOpen { get; init; }
    /// <summary>Automatically checkpoint after this many appended bytes; zero disables periodic checkpoints.</summary>
    public long CheckpointIntervalBytes { get; init; } = 64 * 1024 * 1024;
}
public sealed record DbRecord(long Id, long IndexKey, byte[] Value);
public readonly record struct Mutation(long Id, long IndexKey, ReadOnlyMemory<byte> Value, bool IsDelete)
{
    public static Mutation Put(long id, long indexKey, ReadOnlyMemory<byte> value) => new(id, indexKey, value, false);
    public static Mutation Delete(long id) => new(id, 0, ReadOnlyMemory<byte>.Empty, true);
}
