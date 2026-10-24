namespace PureDb;
// 已提交缓存页内的不可变 BLOB 视图，仅供内部使用；
// 所有公开查询边界均复制为独立 byte[]，避免调用方修改缓存页。
/// <summary>已提交缓存页内的只读 BLOB 视图；必须在公开边界复制后交给调用方。</summary>
/// <param name="value">要写入的数据；写入接口在准入前复制，构造 Mutation 本身不复制。</param>
internal sealed class CachedBlob(ReadOnlyMemory<byte> value)
{
    /// <summary>内部只读字节视图，不得跨读取保护期暴露给调用方。</summary>
    public ReadOnlySpan<byte> Span => value.Span;
    /// <summary>BLOB 的字节长度，无需复制内容。</summary>
    public int Length => value.Length;

    /// <summary>创建可交给调用方独立修改的字节数组。</summary>
    /// <returns>本次编码或复制得到的字节数组。</returns>
    public byte[] Copy() => value.ToArray();
}
