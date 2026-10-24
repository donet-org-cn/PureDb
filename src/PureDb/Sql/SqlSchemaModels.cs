namespace PureDb;
/// <summary>多列外键定义，使用 MATCH SIMPLE 的 NULL 语义。</summary>
/// <param name = "Columns">子表列名。</param>
/// <param name = "Parent">父表名。</param>
/// <param name = "ParentColumns">父表列名，null 表示主键。</param>
/// <param name = "OnDelete">删除父行时的动作。</param>
/// <param name = "OnUpdate">更新父键时的动作。</param>
/// <param name = "Deferred">是否在事务提交时检查。</param>
internal sealed record SqlForeignKey(string[] Columns, string Parent, string[]? ParentColumns, string OnDelete = "NO ACTION", string OnUpdate = "NO ACTION", bool Deferred = false);
/// <summary>持久化视图，保存查询文本而不是运行时委托。</summary>
/// <param name = "Name">视图名称。</param>
/// <param name = "Columns">可选显式列名。</param>
/// <param name = "Sql">视图的 SELECT 文本。</param>
internal sealed record SqlView(string Name, string[]? Columns, string Sql);
/// <summary>持久化触发器元数据，完整 SQL 在执行时经过解析缓存绑定。</summary>
/// <param name = "Name">触发器名称。</param>
/// <param name = "Table">目标表或视图。</param>
/// <param name = "Timing">BEFORE、AFTER 或 INSTEAD OF。</param>
/// <param name = "Event">INSERT、UPDATE 或 DELETE。</param>
/// <param name = "Sql">完整 CREATE TRIGGER 文本。</param>
internal sealed record SqlTrigger(string Name, string Table, string Timing, string Event, string Sql);
/// <summary>CREATE VIEW 的查询定义。</summary>
/// <param name = "View">持久化视图定义。</param>
/// <param name = "IfNotExists">存在时是否忽略。</param>
internal sealed record CreateView(SqlView View, bool IfNotExists) : SqlStatement;
/// <summary>CREATE TRIGGER 的可执行定义。</summary>
/// <param name = "Trigger">持久化触发器元数据。</param>
/// <param name = "IfNotExists">存在时是否忽略。</param>
/// <param name = "Columns">可选 UPDATE OF 列名。</param>
/// <param name = "When">可选逐行条件。</param>
/// <param name = "Body">按顺序执行的触发器语句。</param>
internal sealed record CreateTrigger(SqlTrigger Trigger, bool IfNotExists, string[]? Columns, Expr? When, List<SqlStatement> Body) : SqlStatement;
