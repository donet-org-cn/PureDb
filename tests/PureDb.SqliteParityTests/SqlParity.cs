using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PureDb;
using PureDb.Benchmarks;

/// <summary>公开固定语料的 SQLite SQL 差分检查，失败项保留在 JSON 报告中。</summary>
internal static class SqlParity
{
    /// <summary>在同一进程中对相同初始数据比较两种引擎，不重复累计旧回归测试。</summary>
    /// <param name="root">本次运行生成的隔离测试目录。</param>
    /// <param name="baseline">是否只记录旧版差距而不强制通过率门槛。</param>
    /// <returns>所有差分比较完成的任务。</returns>
    internal static async Task Run(string root, bool baseline)
    {
        using var managed = new SqlDatabase(Path.Combine(root, "sql-corpus.pdb"), new() { Storage = new() { Durability = Durability.Buffered } });
        using var native = new Sqlite(Path.Combine(root, "sql-corpus.sqlite"), Durability.Buffered);
        const string setup = "CREATE TABLE t(id INTEGER PRIMARY KEY,n INTEGER,s TEXT,b BLOB,j JSON);INSERT INTO t VALUES(1,10,'alpha',X'010203','{\"x\":1}'),(2,10,'中文😀',NULL,'{\"x\":2}'),(3,NULL,'beta',X'',NULL),(4,-3,NULL,X'FF','{\"x\":3}');CREATE INDEX ix ON t(n);CREATE TABLE u(id INTEGER PRIMARY KEY,n INTEGER);INSERT INTO u VALUES(1,10),(2,30)";
        await managed.ExecuteAsync(setup); native.Exec(setup);
        var cases = new List<(string Family, string Sql)>();
        void Add(string family, params string[] expressions) { foreach (var e in expressions) cases.Add((family, "SELECT " + e)); }
        Add("numeric", "abs(-5)", "abs(-1.25)", "abs('5')", "abs('bad')", "abs(NULL)", "sign(-5)", "sign(0)", "sign(3.25)", "sign('bad')", "sign(NULL)", "round(1.25,1)", "round(-1.25,1)", "round(12.7)", "round(1.2345,20)", "round(NULL,2)", "1+2*3", "7/2", "7.0/2", "9%4", "4/0", "4%0");
        Add("null-and-lazy", "ifnull(NULL,5)", "ifnull(4,1/0)", "nullif(1,1)", "nullif(1,1.0)", "nullif(1,2)", "nullif(NULL,1)", "iif(1,5,1/0)", "iif(0,1/0,6)", "iif(NULL,1,7)", "if(1,8,9)", "coalesce(NULL,NULL,7)", "NULL IS NULL", "1 IS NOT NULL", "NULL IN (1,NULL)", "2 IN (1,NULL)", "2 NOT IN (1,3)", "CASE WHEN 1 THEN 'a' ELSE 'b' END", "CASE 2 WHEN 1 THEN 4 WHEN 2 THEN 5 ELSE 6 END");
        foreach (string value in new[] { "NULL", "123", "-123", "1.75", "'123.5tail'", "'123e2'", "'  -45.2'", "'bad'", "X'3132'", "'9223372036854775806'", "'99999999999999999999999'" })
            foreach (string type in new[] { "INTEGER", "REAL", "NUMERIC", "TEXT", "BLOB" })
                Add("cast", $"CAST({value} AS {type}),typeof(CAST({value} AS {type}))");
        Add("text", "hex(X'00FF')", "hex(NULL)", "hex('中文')", "hex(123)", "hex(zeroblob(4))", "length(zeroblob(-2))", "unicode('😀')", "unicode('')", "unicode(NULL)", "char(65,20013,128512)", "substr('a😀bc',2,2)", "substr('abcdef',-3,2)", "substr('abcdef',4,-2)", "substr('abcdef',0,3)", "substr('abcdef',-20,3)", "substr('abcdef',20,3)", "substr('abcdef',2)", "hex(substr(X'01020304',2,2))", "instr('a😀中','中')", "instr('abc','z')", "instr(X'010203',X'0203')", "replace('abcabc','ab','X')", "replace('abc','','X')", "trim('  x  ')", "trim('😀x😀','😀')", "ltrim('aaXaa','a')", "rtrim('aaXaa','a')", "concat('a',NULL,'b')", "concat_ws('-',1,NULL,2)", "concat_ws(NULL,1,2)", "octet_length('中文😀')", "typeof(NULL),typeof(1),typeof(1.0),typeof('a'),typeof(X'01')", "lower('ABC'),upper('abc')", "length('a😀中')");
        Add("json", "json_valid('{a:1}',2)", "json_extract('{a:1,b:[2,3]}','$.b[1]')", "json_type('{a:null}','$.a')", "json_array_length('[1,2,3]')", "json_set('{}','$.a',1)", "json_insert('{a:1}','$.a',2)", "json_replace('{a:1}','$.a',2)", "json_remove('{a:1,b:2}','$.a')", "json_patch('{a:1}','{b:2}')", "json_quote('a')", "json_array(1,'a',NULL)", "json_object('a',1)", "'{\"a\":2}'->>'$.a'");
        foreach (string filter in new[] { "1", "n=10", "n IS NULL", "id BETWEEN 2 AND 4", "id IN (1,3)", "n<0", "s LIKE 'a%'", "id>100" })
        {
            cases.Add(("select", "SELECT id,n,s,b FROM t WHERE " + filter + " ORDER BY id"));
            cases.Add(("aggregate", "SELECT COUNT(*),SUM(n),AVG(n),MIN(n),MAX(n) FROM t WHERE " + filter));
        }
        foreach (string kind in new[] { "INNER", "LEFT", "RIGHT", "FULL" })
            cases.Add(("join", "SELECT t.id,u.id FROM t " + kind + " JOIN u ON t.n=u.n ORDER BY t.id,u.id"));
        cases.AddRange(new[]
        {
            ("unicode-order", "SELECT s FROM (SELECT char(57344) AS s UNION ALL SELECT char(65536) UNION ALL SELECT char(1114111) UNION ALL SELECT char(97) UNION ALL SELECT char(20013)) ORDER BY s"),
            ("group", "SELECT n,COUNT(*) FROM t GROUP BY n HAVING COUNT(*)>0 ORDER BY n"),
            ("compound", "SELECT n FROM t UNION SELECT n FROM u ORDER BY n"),
            ("compound", "SELECT n FROM t INTERSECT SELECT n FROM u ORDER BY n"),
            ("compound", "SELECT n FROM t EXCEPT SELECT n FROM u ORDER BY n"),
            ("cte", "WITH q AS(SELECT id FROM t WHERE n=10) SELECT * FROM q ORDER BY id"),
            ("cte", "WITH RECURSIVE q(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM q WHERE x<5) SELECT SUM(x) FROM q"),
            ("subquery", "SELECT a.id,(SELECT n FROM u WHERE id=a.id) FROM t a ORDER BY a.id"),
            ("subquery", "SELECT id FROM t WHERE EXISTS(SELECT 1 FROM u WHERE u.n=t.n) ORDER BY id"),
            ("window", "SELECT id,row_number() OVER(ORDER BY n,id),rank() OVER(ORDER BY n) FROM t ORDER BY id"),
            ("window", "SELECT id,sum(n) OVER(ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM t ORDER BY id"),
            ("window", "SELECT id,lag(s,1,'missing') OVER(ORDER BY id) FROM t ORDER BY id"),
            ("json-table", "SELECT key,value,type,fullkey FROM json_each('[1,null,true,{\"x\":2}]') ORDER BY key"),
            ("json-table", "SELECT fullkey,type,atom FROM json_tree('{a:[1,2],b:null}') ORDER BY fullkey")
        });
        var observations = new List<object>(); int passed = 0;
        foreach (var item in cases)
        {
            string? failure = null;
            try
            {
                using var statement = native.Prepare(item.Sql);
                var expected = new List<object?[]>(); while (statement.Step()) expected.Add(statement.Values());
                var actual = managed.Query(item.Sql).Rows;
                if (actual.Count != expected.Count || actual.Where((r, i) => r.Length != expected[i].Length || !r.Zip(expected[i]).All(p => Equal(p.First, p.Second))).Any())
                    failure = "result mismatch; actual=" + Format(actual) + "; native=" + Format(expected);
            }
            catch (Exception error) { failure = error.GetType().Name + ": " + error.Message; }
            if (failure is null) passed++; else Console.WriteLine("FAIL " + item.Family + ": " + item.Sql + ": " + failure);
            observations.Add(new { item.Family, item.Sql, Passed = failure is null, Failure = failure });
        }
        string corpus = setup + "\n" + string.Join('\n', cases.Select(c => c.Family + ":" + c.Sql));
        var report = new { Scope = "Public common SQL query corpus; not the full SQLite test suite or all SQLite features", SQLite = Sqlite.Version, CorpusSHA256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(corpus))), Total = cases.Count, Passed = passed, Rate = (double)passed / cases.Count, Cases = observations };
        File.WriteAllText(Path.Combine(root, "sql-parity.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"SQL PARITY {passed}/{cases.Count} = {100.0 * passed / cases.Count:F2}%; corpus {report.CorpusSHA256}");
        if (!baseline && passed != cases.Count) throw new Exception("SQL differential corpus regression: " + root);
    }

    /// <summary>同时验证 SQL 存储类别、数值、NULL 与独立 BLOB 字节。</summary>
    /// <param name="a">托管引擎返回值。</param>
    /// <param name="b">原生 SQLite 返回值。</param>
    /// <returns>结果是否具有相同语义及 CLR 存储类别。</returns>
    private static bool Equal(object? a, object? b) => a is byte[] x && b is byte[] y ? x.AsSpan().SequenceEqual(y) : a is double d && b is double e ? Math.Abs(d - e) <= Math.Max(1, Math.Abs(d)) * 1e-13 : Equals(a, b);

    /// <summary>输出有界差分诊断，避免 BLOB 产生大量日志。</summary>
    /// <param name="rows">待描述的结果行。</param>
    /// <returns>带存储类型的少量行摘要。</returns>
    private static string Format(IReadOnlyList<object?[]> rows) => string.Join(';', rows.Take(4).Select(r => string.Join(',', r.Select(v => v is byte[] b ? "blob:" + Convert.ToHexString(b.AsSpan(0, Math.Min(16, b.Length))) : v?.GetType().Name + ":" + Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)))));
}
