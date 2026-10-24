namespace PureDb;
/// <remarks>本部分复用事务内唯一键映射处理 UPSERT，并保持触发器、CHECK 和外键检查。</remarks>
internal sealed partial class SqlSession
{
    /// <summary>本次语句已绑定的冲突更新动作，跨多行 VALUES 复用。</summary>
    /// <param name = "Assignments">更新列与逐行赋值委托。</param>
    /// <param name = "Condition">可选更新过滤条件。</param>
    private sealed record SqlUpsertActions((int Column, Func<object?[], object?> Value)[] Assignments, Func<object?[], object?>? Condition);
    /// <summary>在读取输入前验证所有冲突目标、更新列和表达式，空输入也不能隐藏绑定错误。</summary>
    /// <param name = "table">目标表。</param>
    /// <param name = "upsert">冲突分支。</param>
    /// <param name = "parameters">本次参数。</param>
    /// <returns>在本次语句内复用的绑定动作。</returns>
    private SqlUpsertActions PrepareUpsert(SqlTable table, SqlUpsert upsert, IReadOnlyDictionary<string, object?> parameters)
    {
        ValidateUpsertTarget(table, upsert);
        var binding = new SqlBinding(owner, this, preferFirst: true);
        binding.Add(table, table.Name);
        binding.Add(table, "excluded");
        var assignments = upsert.Assignments.Select(a => (Column: table.Column(a.Column), Value: binding.Bind(a.Value, parameters))).ToArray();
        if (assignments.Select(a => a.Column).Distinct().Count() != assignments.Length)
            throw new SqlException("Duplicate UPSERT assignment");
        return new(assignments, upsert.Where is null ? null : binding.Bind(upsert.Where, parameters));
    }

    /// <summary>验证冲突目标对应主键或完整的列唯一约束，不接受普通非唯一索引。</summary>
    /// <param name = "table">持久化目标表。</param>
    /// <param name = "upsert">当前冲突分支。</param>
    private static void ValidateUpsertTarget(SqlTable table, SqlUpsert upsert)
    {
        if (upsert.Columns is null)
            return;
        int[] columns = upsert.Columns.Select(table.Column).ToArray();
        if (columns.Length == 0 || columns.Distinct().Count() != columns.Length)
            throw new SqlException("Invalid ON CONFLICT target");
        bool matched = columns.Length == 1 && (columns[0] == table.Primary || table.Columns[columns[0]].Unique);
        matched |= table.Indexes.Any(index => index.Unique && IndexColumns(table, index) is { } indexed && indexed.Length == columns.Length && indexed.ToHashSet().SetEquals(columns));
        if (!matched)
            throw new SqlException("ON CONFLICT target must match a PRIMARY KEY or UNIQUE constraint");
    }

    /// <summary>提取由普通列组成的完整唯一索引目标，表达式索引仅由省略目标的分支处理。</summary>
    /// <param name = "table">所属表模式。</param>
    /// <param name = "index">待解析唯一索引。</param>
    /// <returns>目标列序号；包含计算表达式时返回 null。</returns>
    private static int[]? IndexColumns(SqlTable table, SqlIndex index)
    {
        if (index.Expression is null)
            return [table.Column(index.Column)];
        return index.Expression.Op == "composite" && index.Expression.Args.All(e => e.Op == "column") ? index.Expression.Args.Select(e => table.Column(((string)e.Value!).Split('.')[^1])).ToArray() : null;
    }

    /// <summary>检测当前可见唯一键冲突，只处理匹配目标的约束，其他冲突由统一存储入口拒绝。</summary>
    /// <param name = "table">目标表。</param>
    /// <param name = "row">已经规范化的待插入行。</param>
    /// <param name = "upsert">冲突目标和操作。</param>
    /// <returns>发生目标冲突的旧行；无匹配冲突为 null。</returns>
    private object?[]? UpsertConflict(SqlTable table, object?[] row, SqlUpsert upsert)
    {
        int[]? columns = upsert.Columns?.Select(table.Column).ToArray();
        if (columns is null || columns.Length == 1 && columns[0] == table.Primary)
            if (Row(table, (long)row[table.Primary]!) is { } primary)
                return primary;
        // 唯一映射在同一个会话内随 Store/DeleteRow 同步更新，不重复扫描整表。
        foreach (var constraint in UniqueConstraints(table))
        {
            if (columns is not null && (constraint.Columns is not { } indexed || indexed.Length != columns.Length || !indexed.ToHashSet().SetEquals(columns)))
                continue;
            if (constraint.Get(row) is { } value && constraint.Values.TryGetValue(value, out long id))
                return Row(table, id);
        }

        return null;
    }

    /// <summary>执行一行 UPSERT，先触发 BEFORE INSERT，冲突更新转入标准 UPDATE 流水线。</summary>
    /// <param name = "table">目标表模式。</param>
    /// <param name = "row">待插入的新行。</param>
    /// <param name = "columns">显式插入列名。</param>
    /// <param name = "upsert">冲突处理分支。</param>
    /// <param name = "actions">本次语句复用的已绑定更新动作。</param>
    /// <param name = "automatic">主键是否由本次插入自动分配。</param>
    /// <returns>插入或更新为 1，DO NOTHING 或过滤不通过为 0。</returns>
    private int UpsertRow(SqlTable table, object?[] row, string[] columns, SqlUpsert upsert, SqlUpsertActions actions, bool automatic)
    {
        for (int i = 0; i < row.Length; i++)
            row[i] = Coerce(table.Columns[i], row[i]);
        FireTriggers("BEFORE", "INSERT", table, null, row, columns);
        ValidateChecks(table, row);
        var original = UpsertConflict(table, row, upsert);
        if (original is null)
        {
            Store(table, row);
            FireTriggers("AFTER", "INSERT", table, null, row, columns);
            return 1;
        }

        long attemptedId = (long)row[table.Primary]!;
        if (automatic && _automaticIds?.TryGetValue(table.Id, out long maximum) == true && maximum == attemptedId && !ForeignPrimaryExists(table, attemptedId))
            _automaticIds[table.Id] = attemptedId - 1;
        if (upsert.Assignments.Count == 0)
            return 0;
        var input = Combine(original, row);
        if (actions.Condition is not null && SqlValueComparer.Truth(actions.Condition(input)) != true)
            return 0;
        var updated = original.ToArray();
        foreach (var assignment in actions.Assignments)
            updated[assignment.Column] = assignment.Value(input);
        WriteRow(table, updated, original, upsert.Assignments.Select(a => a.Column).ToArray());
        return 1;
    }
}
