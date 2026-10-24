namespace PureDb;
/// <remarks>扩展状态按需分配，普通主键查询只增加一个空引用，不分配窗口、约束或保存点对象。</remarks>
internal sealed partial class SqlSession
{
    private ExtendedState? _extendedState;
    /// <summary>自动主键的事务内最大值缓存，只在自动分配时创建。</summary>
    private Dictionary<int, long>? _automaticIds
    {
        get => _extendedState?.AutomaticIds; set => (_extendedState ??= new()).AutomaticIds = value;
    }

    /// <summary>只在扩展 SQL 路径首次写入状态时创建的状态容器。</summary>
    private sealed class ExtendedState
    {
        /// <summary>派生表名称作用域。</summary>
        internal Dictionary<string, SqlTable>? Names;
        /// <summary>临时表数据。</summary>
        internal Dictionary<int, SqlTableData>? VirtualData;
        /// <summary>当前查询的数据源缓存。</summary>
        internal Dictionary<Source, SqlTable>? Sources;
        /// <summary>相关子查询的外层行。</summary>
        internal List<(SqlBinding Binding, object?[] Row)>? OuterRows;
        /// <summary>下一个临时表 ID。</summary>
        internal int VirtualId = -1;
        /// <summary>扩展查询嵌套深度。</summary>
        internal int QueryDepth;
        /// <summary>当前窗口结果。</summary>
        internal Dictionary<Expr, Dictionary<object?[], object?>>? Windows;
        /// <summary>嵌套语句执行深度。</summary>
        internal int ExecutionDepth;
        /// <summary>触发器调用深度。</summary>
        internal int TriggerDepth;
        /// <summary>正在执行的触发器。</summary>
        internal HashSet<string>? ActiveTriggers;
        /// <summary>正在解析的视图。</summary>
        internal HashSet<string>? ActiveViews;
        /// <summary>级联删除中的记录。</summary>
        internal HashSet<long>? Deleting;
        /// <summary>本语句变更的父键表。</summary>
        internal HashSet<int>? ChangedParents;
        /// <summary>外键级联深度。</summary>
        internal int ForeignDepth;
        /// <summary>事务撤销记录。</summary>
        internal List<UndoEntry>? Undo;
        /// <summary>当前暂存数据涉及的表。</summary>
        internal HashSet<int>? MutatedTables;
        /// <summary>事务内非主键外键的父键集合。</summary>
        internal Dictionary<string, HashSet<SqlCompositeKey>>? ForeignKeys;
        /// <summary>无需逐行重新绑定的非唯一表达式索引委托。</summary>
        internal Dictionary<string, Func<object?[], object?>>? IndexGetters;
        /// <summary>自动主键分配缓存。</summary>
        internal Dictionary<int, long>? AutomaticIds;
    }

    /// <summary>派生表名称作用域，简单查询读取默认值不触发分配。</summary>
    private Dictionary<string, SqlTable>? _namedSources
    {
        get => _extendedState?.Names; set => (_extendedState ??= new()).Names = value;
    }
    /// <summary>临时表数据，简单查询读取默认值不触发分配。</summary>
    private Dictionary<int, SqlTableData>? _virtualData
    {
        get => _extendedState?.VirtualData; set => (_extendedState ??= new()).VirtualData = value;
    }
    /// <summary>当前查询的数据源缓存，简单查询读取默认值不触发分配。</summary>
    private Dictionary<Source, SqlTable>? _sourceCache
    {
        get => _extendedState?.Sources; set => (_extendedState ??= new()).Sources = value;
    }
    /// <summary>相关子查询的外层行，简单查询读取默认值不触发分配。</summary>
    private List<(SqlBinding Binding, object?[] Row)>? _outerRows
    {
        get => _extendedState?.OuterRows; set => (_extendedState ??= new()).OuterRows = value;
    }
    /// <summary>下一个临时表 ID，简单查询读取默认值不触发分配。</summary>
    private int _virtualId
    {
        get => _extendedState?.VirtualId ?? -1; set => (_extendedState ??= new()).VirtualId = value;
    }
    /// <summary>扩展查询嵌套深度，简单查询读取默认值不触发分配。</summary>
    private int _queryDepth
    {
        get => _extendedState?.QueryDepth ?? 0; set => (_extendedState ??= new()).QueryDepth = value;
    }
    /// <summary>当前窗口结果，简单查询读取默认值不触发分配。</summary>
    private Dictionary<Expr, Dictionary<object?[], object?>>? _windowValues
    {
        get => _extendedState?.Windows; set => (_extendedState ??= new()).Windows = value;
    }
    /// <summary>嵌套语句执行深度，简单查询读取默认值不触发分配。</summary>
    private int _executionDepth
    {
        get => _extendedState?.ExecutionDepth ?? 0; set => (_extendedState ??= new()).ExecutionDepth = value;
    }
    /// <summary>触发器调用深度，简单查询读取默认值不触发分配。</summary>
    private int _triggerDepth
    {
        get => _extendedState?.TriggerDepth ?? 0; set => (_extendedState ??= new()).TriggerDepth = value;
    }
    /// <summary>正在执行的触发器，简单查询读取默认值不触发分配。</summary>
    private HashSet<string>? _activeTriggers
    {
        get => _extendedState?.ActiveTriggers; set => (_extendedState ??= new()).ActiveTriggers = value;
    }
    /// <summary>正在解析的视图，简单查询读取默认值不触发分配。</summary>
    private HashSet<string>? _activeViews
    {
        get => _extendedState?.ActiveViews; set => (_extendedState ??= new()).ActiveViews = value;
    }
    /// <summary>级联删除中的记录，简单查询读取默认值不触发分配。</summary>
    private HashSet<long>? _deleting
    {
        get => _extendedState?.Deleting; set => (_extendedState ??= new()).Deleting = value;
    }
    /// <summary>本语句变更的父键表，简单查询读取默认值不触发分配。</summary>
    private HashSet<int>? _changedParents
    {
        get => _extendedState?.ChangedParents; set => (_extendedState ??= new()).ChangedParents = value;
    }
    /// <summary>外键级联深度，简单查询读取默认值不触发分配。</summary>
    private int _foreignDepth
    {
        get => _extendedState?.ForeignDepth ?? 0; set => (_extendedState ??= new()).ForeignDepth = value;
    }
    /// <summary>事务撤销记录，简单查询读取默认值不触发分配。</summary>
    private List<UndoEntry>? _undo
    {
        get => _extendedState?.Undo; set => (_extendedState ??= new()).Undo = value;
    }
}
