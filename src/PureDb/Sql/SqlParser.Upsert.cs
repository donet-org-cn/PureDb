namespace PureDb;
/// <remarks>本部分解析普通 INSERT 的单个冲突处理分支。</remarks>
internal sealed partial class SqlParser
{
    /// <summary>解析 ON CONFLICT 目标、DO NOTHING 或 DO UPDATE SET 及其过滤条件。</summary>
    /// <param name = "insert">已解析的插入主体。</param>
    /// <returns>包含可选冲突分支的插入节点。</returns>
    private Insert ReadInsertTail(Insert insert)
    {
        if (!Eat("ON"))
            return insert;
        Need("CONFLICT");
        string[]? columns = Is("(") ? ReadNames() : null;
        Need("DO");
        var assignments = new List<(string, Expr)>();
        Expr? where = null;
        if (!Eat("NOTHING"))
        {
            Need("UPDATE");
            Need("SET");
            if (columns is null)
                throw Error("DO UPDATE requires an explicit conflict target");
            do
            {
                string name = Name();
                Need("=");
                assignments.Add((name, Expression()));
            }
            while (Eat(","));
            if (Eat("WHERE"))
                where = Expression();
        }

        return insert with
        {
            Upsert = new(columns, assignments, where)
        };
    }
}
