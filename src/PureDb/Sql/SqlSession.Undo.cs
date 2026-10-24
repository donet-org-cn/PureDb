namespace PureDb;
/// <remarks>本部分使用变更撤销记录实现语句原子性和保存点，避免复制整个事务字典。</remarks>
internal sealed partial class SqlSession
{
    /// <summary>一个模式引用和撤销日志位置组成的保存点。</summary>
    /// <param name = "Position">撤销日志中的位置。</param>
    /// <param name = "Catalog">保存点时的不可变私有模式引用。</param>
    internal sealed record SaveState(int Position, SqlCatalog? Catalog);
    /// <summary>一次暂存替换之前的记录值。</summary>
    /// <param name = "Id">记录 ID。</param>
    /// <param name = "Existed">此前是否已有暂存值。</param>
    /// <param name = "Previous">此前的暂存变更。</param>
    private readonly record struct UndoEntry(long Id, bool Existed, Mutation Previous);
    /// <summary>暂存一个最终变更；启用保存点后同时保留此前状态。</summary>
    /// <param name = "mutation">本次需要暂存的变更。</param>
    private void Stage(Mutation mutation)
    {
        if (_extendedState is { } extended)
        {
            extended.MutatedTables ??= _mutations is null ? [] : _mutations.Values.Where(m => m.Id >= 0).Select(m => (int)(m.Id >> 40)).ToHashSet();
            if (mutation.Id >= 0)
            {
                int table = (int)(mutation.Id >> 40);
                extended.MutatedTables.Add(table);
                if (extended.ForeignKeys is not null)
                    foreach (string key in extended.ForeignKeys.Keys.Where(k => k.StartsWith(table + ":", StringComparison.Ordinal)).ToArray())
                        extended.ForeignKeys.Remove(key);
            }
        }

        if (_automaticIds is not null && mutation.Id >= 0)
        {
            int table = (int)(mutation.Id >> 40);
            long id = mutation.Id & ((1L << 40) - 1);
            if (_automaticIds.TryGetValue(table, out long maximum))
            {
                if (mutation.IsDelete && id == maximum)
                    _automaticIds.Remove(table);
                else if (!mutation.IsDelete && id > maximum)
                    _automaticIds[table] = id;
            }
        }

        if (_undo is not null)
        {
            bool existed = Mutations.TryGetValue(mutation.Id, out var previous);
            _undo.Add(new(mutation.Id, existed, previous));
        }

        Mutations[mutation.Id] = mutation;
    }

    /// <summary>以常数时间记录当前事务位置。</summary>
    /// <returns>可用于回滚私有状态的保存点。</returns>
    internal SaveState SaveStateAtCurrent()
    {
        _undo ??= [];
        return new(_undo.Count, _catalog);
    }

    /// <summary>按相反顺序撤销保存点之后的变更，并丢弃派生约束缓存。</summary>
    /// <param name = "state">当前事务中仍有效的保存点。</param>
    internal void RestoreState(SaveState state)
    {
        if (_undo is null || state.Position > _undo.Count)
            throw new InvalidOperationException("Invalid savepoint state");
        for (int i = _undo.Count - 1; i >= state.Position; i--)
        {
            var item = _undo[i];
            if (item.Existed)
                Mutations[item.Id] = item.Previous;
            else
                Mutations.Remove(item.Id);
        }

        _undo.RemoveRange(state.Position, _undo.Count - state.Position);
        if (!ReferenceEquals(_catalog, state.Catalog))
            owner.InvalidateStructurePlans();
        _catalog = state.Catalog;
        _uniqueMaps = null;
        _changedParents?.Clear();
        _automaticIds?.Clear();
        if (_extendedState is { } extended)
        {
            extended.MutatedTables = null;
            extended.ForeignKeys?.Clear();
            extended.IndexGetters?.Clear();
        }
    }

    /// <summary>没有命名保存点时释放已完成语句的历史值，最终暂存字典继续保留。</summary>
    internal void ForgetUndo() => _undo?.Clear();
    /// <summary>回滚整个会话，并使已回滚模式生成的结构计划失效。</summary>
    internal void ResetState()
    {
        if (_mutations?.ContainsKey(-1) == true)
            owner.InvalidateStructurePlans();
        _mutations?.Clear();
        _catalog = null;
        _uniqueMaps = null;
        _undo?.Clear();
        _automaticIds?.Clear();
        if (_extendedState is { } extended)
        {
            extended.MutatedTables = null;
            extended.ForeignKeys?.Clear();
            extended.IndexGetters?.Clear();
        }
    }
}
