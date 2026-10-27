namespace PureDb;

/// <remarks>本部分实现专用数值聚合循环和按表版本复用的分组计数。</remarks>
internal sealed partial class SqlSession
{
    /// <summary>对受支持的简单数值聚合使用专用循环，避免通用分组物化。</summary>
    /// <param name="select">当前已解析的 SELECT 查询结构。</param>
    /// <param name="table">当前操作的不可变表模式。</param>
    /// <param name="parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private SqlResult? AggregateFast(Select select, SqlTable table, IReadOnlyDictionary<string, object?> parameters)
    {
        if (table.Id == 0 || select.Joins.Count != 0 || select.Distinct || select.Having is not null || select.GroupBy.Count != 0 || select.Columns.Count != 1 || select.Order.Count != 0)
            return null;
        Expr expression = select.Columns[0].Expr;
        if (expression.Op is not ("COUNT" or "SUM" or "AVG" or "MIN" or "MAX") || expression.Args.Length != 1)
            return null;
        var arg = expression.Args[0];
        if (arg.Op != "column" && !(arg.Op == "star" && expression.Op == "COUNT"))
            return null;
        var bind = new SqlBinding();
        bind.Add(table, select.From.Alias);
        int col = arg.Op == "column" ? bind.Resolve((string)arg.Value!) : -1;
        var filter = select.Where is null ? null : bind.Bind(select.Where, parameters);
        long count = 0, sum = 0;
        double real = 0;
        bool isReal = false;
        object? best = null;
        long? derived = expression.Op == "COUNT" && col < 0 && select.Where is not null ? DerivedCount(table, select.Where, parameters) : null;
        if (derived is not null)
            count = derived.Value;
        else if (filter is null && col >= 0 && expression.Op == "SUM")
        {
            // 在行循环外选择一次聚合算法，避免逐行进行操作符分派。
            foreach (var row in Data(table).Rows)
            {
                object? value = row[col];
                if (value is null)
                    continue;
                count++;
                if (!isReal && value is long n)
                {
                    sum = SqlNumeric.AddSum(sum, n);
                    continue;
                }

                if (value is not (long or double))
                    throw new SqlException("SUM requires numeric values");
                if (!isReal)
                {
                    real = sum;
                    isReal = true;
                }

                real += Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        else
            foreach (var row in Data(table).Rows)
            {
                if (filter is not null && SqlValueComparer.Truth(filter(row)) != true)
                    continue;
                object? value = col < 0 ? 1L : row[col];
                if (value is null)
                    continue;
                count++;
                if (expression.Op == "COUNT")
                    continue;
                if (expression.Op is "MIN" or "MAX")
                {
                    if (best is null || (expression.Op == "MIN" ? SqlValueComparer.Instance.Compare(value, best) < 0 : SqlValueComparer.Instance.Compare(value, best) > 0))
                        best = value;
                    continue;
                }

                if (value is not (long or double))
                    throw new SqlException("SUM/AVG requires numeric values");
                if (expression.Op == "AVG")
                {
                    real += Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                    continue;
                }

                if (value is long n && !isReal)
                    sum = SqlNumeric.AddSum(sum, n);
                else
                {
                    if (!isReal)
                    {
                        real = sum;
                        isReal = true;
                    }

                    real += Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                }
            }

        object? result = expression.Op switch
        {
            "COUNT" => count,
            "MIN" or "MAX" => best,
            _ when count == 0 => null,
            "AVG" => real / count,
            "SUM" when isReal => real,
            _ => sum
        };
        int Limit(Expr? e, int fallback)
        {
            if (e is null)
                return fallback;
            object? value = new SqlBinding().Bind(e, parameters)([]);
            return value is long n && n >= 0 && n <= int.MaxValue ? (int)n : throw new SqlException("Invalid LIMIT/OFFSET");
        }

        bool visible = Limit(select.Limit, int.MaxValue) > 0 && Limit(select.Offset, 0) == 0;
        return new([select.Columns[0].Alias ?? expression.Op.ToLowerInvariant()], visible ? [new object?[] { result is byte[] b ? b.ToArray() : SqlJsonFunctions.Unwrap(result) }] : [], "StreamingAggregate");
    }

    /// <summary>对简单 GROUP BY 计数复用按表版本失效的有序分组缓存。</summary>
    /// <param name="select">当前已解析的 SELECT 查询结构。</param>
    /// <param name="table">当前操作的不可变表模式。</param>
    /// <param name="parameters">本次调用的参数值或编译期间收集的参数名称。</param>
    /// <returns>计算或查找得到的结果；未找到或 SQL NULL 情况按方法说明返回 null。</returns>
    private SqlResult? GroupCountFast(Select select, SqlTable table, IReadOnlyDictionary<string, object?> parameters)
    {
        if (table.Id == 0 || select.Joins.Count > 0 || select.Distinct || select.Where is not null || select.Having is not null || select.GroupBy.Count != 1 || select.Columns.Count != 2 || select.GroupBy[0].Op != "column")
            return null;
        if (select.Columns[0].Expr.Op != "column" || !SqlAst.Equal(select.Columns[0].Expr, select.GroupBy[0]))
            return null;
        var count = select.Columns[1].Expr;
        if (count.Op != "COUNT" || count.Args.Length != 1 || count.Args[0].Op != "star")
            return null;
        if (select.Order.Count > 1)
            return null;
        var bind = new SqlBinding();
        bind.Add(table, select.From.Alias);
        int col = bind.Resolve((string)select.GroupBy[0].Value!);
        if (bind.Resolve((string)select.Columns[0].Expr.Value!) != col)
            return null;
        string name = select.Columns[0].Alias ?? table.Columns[col].Name;
        if (select.Order.Count == 1)
        {
            Expr order = select.Order[0].Expr;
            if (order.Op == "column" && !((string)order.Value!).Equals(name, StringComparison.OrdinalIgnoreCase) && bind.Resolve((string)order.Value!) != col)
                return null;
            if (!(order.Op == "column" && (SqlAst.Equal(order, select.GroupBy[0]) || ((string)order.Value!).Equals(name, StringComparison.OrdinalIgnoreCase))) && !(order.Op == "literal" && Equals(order.Value, 1L)))
                return null;
        }

        var groups = Data(table).Groups(col);
        int Number(Expr? e, int fallback)
        {
            if (e is null)
                return fallback;
            object? value = new SqlBinding().Bind(e, parameters)([]);
            return value is long n && n >= 0 && n <= int.MaxValue ? (int)n : throw new SqlException("Invalid LIMIT/OFFSET");
        }

        int skip = Number(select.Offset, 0), take = Math.Min(Number(select.Limit, int.MaxValue), Math.Max(0, groups.Length - skip));
        bool descending = select.Order.Count == 1 && select.Order[0].Descending;
        var result = new object?[take][];
        for (int i = 0; i < take; i++)
        {
            var group = groups[descending ? groups.Length - 1 - skip - i : skip + i];
            result[i] = [group.Key is byte[] b ? b.ToArray() : SqlJsonFunctions.Unwrap(group.Key), group.Count];
        }

        return new([name, select.Columns[1].Alias ?? "count"], result, "HashCountAggregate");
    }
}
