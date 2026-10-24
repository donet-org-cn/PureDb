namespace PureDb;
/// <remarks>本部分在统一写入入口执行持久化 CHECK，批量事务内复用绑定结果。</remarks>
internal sealed partial class SqlSession
{
    /// <summary>检查规范化新行，SQL NULL 通过约束，数值假值导致当前语句失败。</summary>
    /// <param name = "table">含检查约束的目标表模式。</param>
    /// <param name = "row">已转换为表存储类型的新行。</param>
    private void ValidateChecks(SqlTable table, object?[] row)
    {
        if (table.Checks.Count == 0)
            return;
        var cache = (_extendedState ??= new()).CheckGetters ??= [];
        _extendedState.CheckEpoch = owner.FunctionEpoch;
        if (!cache.TryGetValue(table.Id, out var checks))
        {
            var binding = new SqlBinding(owner, this);
            binding.Add(table, table.Name);
            cache[table.Id] = checks = table.Checks.Select(check => binding.Bind(check, SqlDatabase.EmptyParameters)).ToArray();
        }

        foreach (var check in checks)
            if (SqlValueComparer.Truth(check(row)) == false)
                throw new SqlException("CHECK constraint failed: " + table.Name);
    }
}
