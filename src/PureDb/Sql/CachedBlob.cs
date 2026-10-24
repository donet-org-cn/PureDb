namespace PureDb;

// Immutable view into an owned committed page. Kept internal: all public query
// boundaries materialize an independent byte[] so callers cannot mutate pages.
internal sealed class CachedBlob(ReadOnlyMemory<byte> value)
{
    public ReadOnlySpan<byte> Span => value.Span;
    public int Length => value.Length;
    public byte[] Copy() => value.ToArray();
}
