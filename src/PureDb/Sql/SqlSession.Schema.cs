namespace PureDb;
/// <remarks>本部分执行视图和触发器，所有触发器写入都进入同一个原子变更集合。</remarks>
internal sealed partial class SqlSession
{
    /// <summary>执行一条语句，含约束和触发器时记录撤销位置并验证即时外键。</summary>
    /// <param name = "statement">已解析语句。</param>
    /// <param name = "parameters">本次参数。</param>
    /// <returns>直接受影响行数，不包含级联及触发器写入。</returns>
    internal int Execute(SqlStatement statement, IReadOnlyDictionary<string, object?> parameters)
    {
        if (statement is SqlWithStatement with)
            return ExecuteWith(with, parameters);
        if (!Catalog.ManagedWrites)
            return ExecuteCore(statement, parameters);
        var state = SaveStateAtCurrent();
        _executionDepth++;
        try
        {
            int affected = ExecuteCore(statement, parameters);
            if (_executionDepth == 1)
                ValidateForeignKeys(false, state.Position);
            return affected;
        }
        catch
        {
            RestoreState(state);
            throw;
        }
        finally
        {
            _executionDepth--;
        }
    }

    /// <summary>为增删改建立独立 WITH 作用域，结束时释放临时结果。</summary>
    /// <param name = "statement">带公共表表达式的增删改语句。</param>
    /// <param name = "parameters">调用参数。</param>
    /// <returns>直接受影响行数。</returns>
    private int ExecuteWith(SqlWithStatement statement, IReadOnlyDictionary<string, object?> parameters)
    {
        var names = _namedSources;
        var sources = _sourceCache;
        int scope = _virtualId;
        _namedSources = names is null ? new(StringComparer.OrdinalIgnoreCase) : new(names, StringComparer.OrdinalIgnoreCase);
        _sourceCache = [];
        try
        {
            foreach (var cte in statement.Ctes)
                _namedSources[cte.Name] = MaterializeCte(cte, statement.Recursive, parameters);
            return Execute(statement.Statement, parameters);
        }
        finally
        {
            _namedSources = names;
            _sourceCache = sources;
            ReleaseVirtualTables(scope);
        }
    }

    /// <summary>创建视图或触发器，并检查目标对象和名称边界。</summary>
    /// <param name = "statement">CREATE VIEW 或 CREATE TRIGGER。</param>
    /// <returns>DDL 固定返回零。</returns>
    private int CreateRelation(SqlStatement statement)
    {
        if (statement is CreateView view)
        {
            bool exists = Catalog.Views.Any(v => v.Name.Equals(view.View.Name, StringComparison.OrdinalIgnoreCase));
            if (exists && view.IfNotExists)
                return 0;
            if (exists || Catalog.Tables.Any(t => t.Name.Equals(view.View.Name, StringComparison.OrdinalIgnoreCase)))
                throw new SqlException("Schema object already exists");
            ChangeCatalog(c => c.Views.Add(view.View));
        }
        else
        {
            var trigger = (CreateTrigger)statement;
            bool exists = Catalog.Triggers.Any(t => t.Name.Equals(trigger.Trigger.Name, StringComparison.OrdinalIgnoreCase));
            if (exists && trigger.IfNotExists)
                return 0;
            if (exists)
                throw new SqlException("Trigger already exists");
            bool isView = Catalog.Views.Any(v => v.Name.Equals(trigger.Trigger.Table, StringComparison.OrdinalIgnoreCase));
            if (!isView)
                Catalog.Table(trigger.Trigger.Table);
            if (isView != (trigger.Trigger.Timing == "INSTEAD OF"))
                throw new SqlException("INSTEAD OF requires a view; BEFORE/AFTER require a table");
            ChangeCatalog(c => c.Triggers.Add(trigger.Trigger));
        }

        return 0;
    }

    /// <summary>删除视图或触发器，删除视图时同时删除其触发器。</summary>
    /// <param name = "drop">删除定义。</param>
    /// <returns>DDL 固定返回零。</returns>
    private int DropRelation(Drop drop)
    {
        bool view = drop.Kind == "VIEW";
        bool exists = view ? Catalog.Views.Any(v => v.Name.Equals(drop.Name, StringComparison.OrdinalIgnoreCase)) : Catalog.Triggers.Any(t => t.Name.Equals(drop.Name, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            if (drop.IfExists)
                return 0;
            throw new SqlException("Unknown " + drop.Kind);
        }

        ChangeCatalog(c =>
        {
            if (view)
            {
                c.Views.RemoveAll(v => v.Name.Equals(drop.Name, StringComparison.OrdinalIgnoreCase));
                c.Triggers.RemoveAll(t => t.Table.Equals(drop.Name, StringComparison.OrdinalIgnoreCase));
            }
            else
                c.Triggers.RemoveAll(t => t.Name.Equals(drop.Name, StringComparison.OrdinalIgnoreCase));
        });
        return 0;
    }

    /// <summary>在作用域和循环保护下物化视图，模式本身不含运行时结果。</summary>
    /// <param name = "source">视图引用及别名。</param>
    /// <param name = "view">持久化视图定义。</param>
    /// <param name = "parameters">调用参数，不允许视图定义包含参数。</param>
    /// <returns>只在当前查询中可见的派生表。</returns>
    private SqlTable ViewTable(Source source, SqlView view, IReadOnlyDictionary<string, object?> parameters)
    {
        _sourceCache ??= [];
        if (_sourceCache.TryGetValue(source, out var cached))
            return cached;
        _activeViews ??= new(StringComparer.OrdinalIgnoreCase);
        if (_activeViews.Count >= 64 || !_activeViews.Add(view.Name))
            throw new SqlException("Cyclic or excessively nested view");
        try
        {
            if (owner.ParseSchema(view.Sql) is not [Select query])
                throw new SqlException("Invalid persisted view");
            var result = Query(query, parameters);
            if (result.FieldTypes.Count != result.Columns.Count)
                result.FieldTypes = Describe(query, parameters);
            return _sourceCache[source] = VirtualTable(source.Alias, result, view.Columns);
        }
        finally
        {
            _activeViews.Remove(view.Name);
        }
    }

    /// <summary>依次执行匹配的行级触发器；OLD/NEW 绑定在独立外层作用域中。</summary>
    /// <param name = "timing">执行时机。</param>
    /// <param name = "action">INSERT、UPDATE 或 DELETE。</param>
    /// <param name = "table">目标模式。</param>
    /// <param name = "oldRow">旧行，插入时为 null。</param>
    /// <param name = "newRow">新行，删除时为 null。</param>
    /// <param name = "columns">UPDATE 语句明确赋值的列。</param>
    private void FireTriggers(string timing, string action, SqlTable table, object?[]? oldRow, object?[]? newRow, string[] columns)
    {
        if (Catalog.Triggers.Count == 0)
            return;
        foreach (var metadata in Catalog.Triggers.Where(t => t.Timing == timing && t.Event == action && t.Table.Equals(table.Name, StringComparison.OrdinalIgnoreCase)).Reverse().ToArray())
        {
            _activeTriggers ??= new(StringComparer.OrdinalIgnoreCase);
            if (_activeTriggers.Contains(metadata.Name))
                continue;
            if (_triggerDepth >= 64)
                throw new SqlException("Trigger nesting exceeds 64 levels");
            var trigger = (CreateTrigger)owner.ParseSchema(metadata.Sql)[0];
            if (trigger.Columns is not null && !trigger.Columns.Any(c => columns.Contains(c, StringComparer.OrdinalIgnoreCase)))
                continue;
            var binding = new SqlBinding(owner, this);
            object?[] row = oldRow is null ? newRow! : newRow is null ? oldRow : Combine(oldRow, newRow);
            if (oldRow is not null)
                binding.Add(table, "OLD");
            if (newRow is not null)
                binding.Add(table, "NEW");
            (_outerRows ??= []).Add((binding, row));
            _activeTriggers.Add(metadata.Name);
            _triggerDepth++;
            try
            {
                if (trigger.When is not null && SqlValueComparer.Truth(binding.Bind(trigger.When, SqlDatabase.EmptyParameters)(row)) != true)
                    continue;
                foreach (var statement in trigger.Body)
                    if (statement is Select query)
                        _ = Query(query, SqlDatabase.EmptyParameters);
                    else
                        Execute(statement, SqlDatabase.EmptyParameters);
            }
            finally
            {
                _triggerDepth--;
                _activeTriggers.Remove(metadata.Name);
                _outerRows.RemoveAt(_outerRows.Count - 1);
            }
        }
    }

    /// <summary>实现触发器中的 RAISE(ABORT)，禁止在普通表达式中使用。</summary>
    /// <param name = "message">触发器给出的错误消息。</param>
    /// <returns>此方法始终抛出 SQL 异常。</returns>
    internal object? Raise(object? message) => throw new SqlException(_triggerDepth > 0 ? Convert.ToString(message, System.Globalization.CultureInfo.InvariantCulture) ?? "Trigger aborted" : "RAISE is only allowed in a trigger");
    /// <summary>写入普通行并执行 BEFORE、外键动作和 AFTER。</summary>
    /// <param name = "table">持久化目标表。</param>
    /// <param name = "row">准备写入的新行。</param>
    /// <param name = "oldRow">更新前行；插入为 null。</param>
    /// <param name = "columns">显式更新列名。</param>
    private void WriteRow(SqlTable table, object?[] row, object?[]? oldRow, string[] columns)
    {
        if (!Catalog.ManagedWrites)
        {
            Store(table, row, oldRow is null ? null : (long)oldRow[table.Primary]!);
            return;
        }

        for (int i = 0; i < row.Length; i++)
            row[i] = Coerce(table.Columns[i], row[i]);
        string action = oldRow is null ? "INSERT" : "UPDATE";
        FireTriggers("BEFORE", action, table, oldRow, row, columns);
        if (oldRow is not null)
            ParentActions(table, oldRow, row);
        Store(table, row, oldRow is null ? null : (long)oldRow[table.Primary]!);
        FireTriggers("AFTER", action, table, oldRow, row, columns);
    }

    /// <summary>删除一行，先处理限制及级联，再更新唯一约束映射和触发器。</summary>
    /// <param name = "table">持久化目标表。</param>
    /// <param name = "row">删除前的当前行。</param>
    /// <param name = "triggers">DROP TABLE 的隐式删除不触发目标表触发器。</param>
    private void DeleteRow(SqlTable table, object?[] row, bool triggers = true)
    {
        long key = StorageId(table, (long)row[table.Primary]!);
        if (!Catalog.ManagedWrites)
        {
            if (table.HasUniqueConstraints)
                foreach (var state in UniqueConstraints(table))
                    if (state.Get(row) is { } value)
                        state.Values.Remove(value);
            Stage(Mutation.Delete(key));
            return;
        }

        _deleting ??= [];
        if (!_deleting.Add(key))
            return;
        try
        {
            if (triggers)
                FireTriggers("BEFORE", "DELETE", table, row, null, []);
            if (Catalog.ManagedWrites)
                ParentActions(table, row, null);
            if (table.HasUniqueConstraints)
                foreach (var state in UniqueConstraints(table))
                    if (state.Get(row) is { } value)
                        state.Values.Remove(value);
            Stage(Mutation.Delete(key));
            if (triggers)
                FireTriggers("AFTER", "DELETE", table, row, null, []);
        }
        finally
        {
            _deleting.Remove(key);
        }
    }

    /// <summary>只允许存在匹配 INSTEAD OF 触发器的视图参与写入。</summary>
    /// <param name = "statement">针对视图的增删改语句。</param>
    /// <param name = "parameters">调用参数。</param>
    /// <returns>视图写入按 SQLite 的直接变更计数规则返回零。</returns>
    private int WriteView(SqlStatement statement, IReadOnlyDictionary<string, object?> parameters)
    {
        var cache = _sourceCache;
        int scope = _virtualId;
        _sourceCache = [];
        try
        {
            return WriteViewCore(statement, parameters);
        }
        finally
        {
            _sourceCache = cache;
            ReleaseVirtualTables(scope);
        }
    }

    /// <summary>在独立的派生表作用域内执行视图增删改。</summary>
    /// <param name = "statement">针对视图的语句。</param>
    /// <param name = "parameters">本次参数。</param>
    /// <returns>视图直接变更计数为零。</returns>
    private int WriteViewCore(SqlStatement statement, IReadOnlyDictionary<string, object?> parameters)
    {
        string name = statement switch
        {
            Insert i => i.Table,
            Update u => u.Table,
            Delete d => d.Table,
            _ => throw new SqlException("Invalid view write")
        };
        string action = statement is Insert ? "INSERT" : statement is Update ? "UPDATE" : "DELETE";
        if (!Catalog.Triggers.Any(t => t.Table.Equals(name, StringComparison.OrdinalIgnoreCase) && t.Timing == "INSTEAD OF" && t.Event == action))
            throw new SqlException("View is read-only without an INSTEAD OF trigger");
        var table = SourceTable(new(name, name), parameters);
        var binding = new SqlBinding(owner, this);
        binding.Add(table, name);
        if (statement is Insert insert)
        {
            int[] columns = insert.Columns is null ? Enumerable.Range(0, table.Columns.Count).ToArray() : insert.Columns.Select(table.Column).ToArray();
            var values = insert.Query is null ? insert.Values.Select(v => v.Select(e => binding.Bind(e, parameters)([])).ToArray()).ToArray() : Query(insert.Query, parameters).Rows;
            foreach (var input in values)
            {
                if (input.Length != columns.Length)
                    throw new SqlException("INSERT column/value mismatch");
                var row = new object?[table.Columns.Count];
                for (int i = 0; i < columns.Length; i++)
                    row[columns[i]] = input[i];
                FireTriggers("INSTEAD OF", action, table, null, row, []);
            }
        }
        else
        {
            Expr? where = statement is Update update ? update.Where : ((Delete)statement).Where;
            var filter = where is null ? null : binding.Bind(where, parameters);
            foreach (var row in Data(table).Rows)
            {
                if (filter is not null && SqlValueComparer.Truth(filter(row)) != true)
                    continue;
                object?[]? next = statement is Update ? row.ToArray() : null;
                string[] columns = [];
                if (statement is Update change)
                {
                    columns = change.Assignments.Select(a => a.Column).ToArray();
                    foreach (var assignment in change.Assignments)
                        next![table.Column(assignment.Column)] = binding.Bind(assignment.Value, parameters)(row);
                }

                FireTriggers("INSTEAD OF", action, table, row, next, columns);
            }
        }

        return 0;
    }
}
