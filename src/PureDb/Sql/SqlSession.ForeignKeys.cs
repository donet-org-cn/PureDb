namespace PureDb;
/// <remarks>本部分验证外键并执行父键变化引起的原子级联。</remarks>
internal sealed partial class SqlSession
{
    /// <summary>解析父键列并验证其由主键或完整唯一索引保护。</summary>
    /// <param name = "foreign">待检查外键。</param>
    /// <param name = "parent">父表模式。</param>
    /// <returns>父表中的目标列位置。</returns>
    private static int[] ParentColumns(SqlForeignKey foreign, SqlTable parent)
    {
        int[] columns = foreign.ParentColumns is null ? [parent.Primary] : foreign.ParentColumns.Select(parent.Column).ToArray();
        if (columns.Length != foreign.Columns.Length)
            throw new SqlException("Foreign-key column count mismatch");
        bool unique = columns.Length == 1 && (columns[0] == parent.Primary || parent.Columns[columns[0]].Unique || parent.Indexes.Any(i => i.Unique && i.Expression is null && parent.Column(i.Column) == columns[0]));
        unique |= parent.Indexes.Any(i => i.Unique && i.Expression?.Op == "composite" && i.Expression.Args.Length == columns.Length && i.Expression.Args.All(e => e.Op == "column") && i.Expression.Args.Select(e => parent.Column(((string)e.Value!).Split('.')[^1])).ToHashSet().SetEquals(columns));
        if (!unique)
            throw new SqlException("Foreign key must reference a PRIMARY KEY or UNIQUE key");
        return columns;
    }

    /// <summary>验证本条语句的即时约束，或提交时的全部延迟约束。</summary>
    /// <param name = "commit">是否执行提交阶段检查。</param>
    /// <param name = "fromUndo">即时检查从哪个撤销位置收集本语句变更。</param>
    internal void ValidateForeignKeys(bool commit, int fromUndo = 0)
    {
        if (!Catalog.ManagedWrites)
            return;
        HashSet<long>? changed = commit ? _mutations?.Keys.Where(id => id >= 0).ToHashSet() : _undo?.Skip(fromUndo).Select(u => u.Id).ToHashSet();
        HashSet<int>? changedTables = commit ? changed?.Select(id => (int)(id >> 40)).ToHashSet() : null;
        foreach (var child in Catalog.Tables.Where(t => t.ForeignKeys.Count > 0))
        {
            foreach (var foreign in child.ForeignKeys)
            {
                if (foreign.Deferred && !commit)
                    continue;
                var parentTable = Catalog.Tables.FirstOrDefault(t => t.Name.Equals(foreign.Parent, StringComparison.OrdinalIgnoreCase));
                bool parentChanged = parentTable is null || (commit ? changedTables?.Contains(parentTable.Id) == true : _changedParents?.Contains(parentTable.Id) == true);
                if (commit && !parentChanged && changedTables?.Contains(child.Id) != true)
                    continue;
                IEnumerable<object?[]> childRows;
                if (parentChanged)
                    childRows = Data(child).Rows;
                else
                    childRows = changed is null ? [] : changed.Where(id => id >= 0 && (id >> 40) == child.Id).Select(id => Row(child, id & ((1L << 40) - 1))).Where(row => row is not null).Select(row => row!);
                int[] childColumns = foreign.Columns.Select(child.Column).ToArray();
                var pending = childRows.Where(row => childColumns.All(column => row[column] is not null)).ToArray();
                if (pending.Length == 0)
                    continue;
                var parent = Catalog.Table(foreign.Parent);
                int[] parentColumns = ParentColumns(foreign, parent);
                HashSet<SqlCompositeKey>? keys = null;
                foreach (var row in pending)
                {
                    var values = childColumns.Select(c => row[c]).ToArray();
                    if (values.Any(v => v is null))
                        continue;
                    bool exists;
                    if (parentColumns.Length == 1 && parentColumns[0] == parent.Primary)
                        exists = SqlPrimaryPredicate.TryId(values[0], out long id) && ForeignPrimaryExists(parent, id);
                    else
                    {
                        if (keys is null)
                        {
                            var cache = (_extendedState ??= new()).ForeignKeys ??= new(StringComparer.Ordinal);
                            string key = parent.Id + ":" + string.Join(',', parentColumns);
                            if (!cache.TryGetValue(key, out keys))
                                cache[key] = keys = Data(parent).Rows.Select(r => new SqlCompositeKey(parentColumns.Select(c => r[c]).ToArray())).ToHashSet();
                        }

                        exists = keys.Contains(new(values));
                    }

                    if (!exists)
                        throw new SqlException("FOREIGN KEY constraint failed");
                }
            }
        }

        _changedParents?.Clear();
    }

    /// <summary>外键引用主键时只验证索引存在性，避免解码父行和复制 BLOB。</summary>
    /// <param name = "parent">父表模式。</param>
    /// <param name = "id">经过主键范围验证的整数。</param>
    /// <returns>当前事务视图中存在父行时为 true。</returns>
    private bool ForeignPrimaryExists(SqlTable parent, long id)
    {
        long key = StorageId(parent, id);
        if (_mutations?.TryGetValue(key, out var mutation) == true)
            return !mutation.IsDelete;
        return snapshot.Contains(key);
    }

    /// <summary>父键更新或删除时执行 RESTRICT、CASCADE、SET NULL 和 SET DEFAULT。</summary>
    /// <param name = "parent">父表模式。</param>
    /// <param name = "oldRow">变更前父行。</param>
    /// <param name = "newRow">更新后父行；删除为 null。</param>
    private void ParentActions(SqlTable parent, object?[] oldRow, object?[]? newRow)
    {
        if (++_foreignDepth > 64)
        {
            _foreignDepth--;
            throw new SqlException("Foreign-key cascade exceeds 64 levels");
        }

        try
        {
            foreach (var child in Catalog.Tables)
                foreach (var foreign in child.ForeignKeys.Where(f => f.Parent.Equals(parent.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    int[] parentColumns = ParentColumns(foreign, parent), childColumns = foreign.Columns.Select(child.Column).ToArray();
                    if (newRow is not null && parentColumns.All(c => SqlValueComparer.Instance.Equals(oldRow[c], newRow[c])))
                        continue;
                    (_changedParents ??= []).Add(parent.Id);
                    var matches = Data(child).Rows.Where(row => childColumns.Select((c, i) => row[c] is not null && SqlValueComparer.Instance.Equals(row[c], oldRow[parentColumns[i]])).All(equal => equal)).ToArray();
                    string action = newRow is null ? foreign.OnDelete : foreign.OnUpdate;
                    if (action == "RESTRICT" && matches.Length > 0)
                        throw new SqlException("FOREIGN KEY RESTRICT constraint failed");
                    if (action is "NO ACTION" or "RESTRICT")
                        continue;
                    foreach (var row in matches)
                    {
                        if (newRow is null && action == "CASCADE")
                        {
                            DeleteRow(child, row);
                            continue;
                        }

                        var next = row.ToArray();
                        for (int i = 0; i < childColumns.Length; i++)
                            next[childColumns[i]] = action == "CASCADE" ? newRow![parentColumns[i]] : action == "SET NULL" ? null : child.Columns[childColumns[i]].Default is { } value ? new SqlBinding(owner, this).Bind(value, SqlDatabase.EmptyParameters)([]) : null;
                        if (child.Id == parent.Id && (long)row[child.Primary]! == (long)oldRow[parent.Primary]!)
                        {
                            if (newRow is not null)
                                for (int i = 0; i < childColumns.Length; i++)
                                    newRow[childColumns[i]] = next[childColumns[i]];
                            continue;
                        }

                        WriteRow(child, next, row, foreign.Columns);
                    }
                }
        }
        finally
        {
            _foreignDepth--;
        }
    }
}
