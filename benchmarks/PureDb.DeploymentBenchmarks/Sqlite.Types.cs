namespace PureDb.Benchmarks;
/// <summary>独立 SQLite 宿主的刷盘选项，避免为基准适配层引用 PureDb 程序集。</summary>
internal enum SqliteDurability
{
    Full, Buffered
}
/// <summary>共用 C API 适配器所需的记录形状；本次 SQL 测量不调用 KV Record 方法。</summary>
/// <param name="Id">整数主键。</param>
/// <param name="IndexKey">索引键。</param>
/// <param name="Value">二进制负载。</param>
internal sealed record SqliteRecord(long Id, long IndexKey, byte[] Value);
