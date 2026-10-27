using PureDb;

/// <summary>验证维护操作与主键范围合并在变更、并发读取和重开之后仍保持一致。</summary>
internal static class MaintenanceChecks
{
    /// <summary>使用确定随机种子比较主键投影与独立内存模型。</summary>
    /// <param name="folder">本轮独立的自动生成目录。</param>
    /// <returns>全部随机序列及重开检查完成的任务。</returns>
    internal static async Task Run(string folder)
    {
        string path = Path.Combine(folder, "maintenance-model.pdb");
        var oracle = new SortedDictionary<long, long>();
        var random = new Random(71027);
        for (int cycle = 0; cycle < 5; cycle++)
        {
            using var db = new SqlDatabase(path, new()
            {
                CacheBytes = 0,
                Storage = new()
                {
                    Durability = Durability.Buffered,
                    CacheBytes = 4096,
                    CheckpointIntervalBytes = cycle % 2 == 0 ? 4096 : 0
                }
            });
            await db.ExecuteAsync("CREATE TABLE IF NOT EXISTS t(id INTEGER PRIMARY KEY,n INTEGER,b BLOB)");
            for (int batch = 0; batch < 15; batch++)
            {
                var commands = new List<SqlCommand>();
                for (int i = 0; i < 100; i++)
                {
                    long id = i == 0 ? (1L << 40) - 1 : random.Next(2500);
                    if (random.Next(4) == 0)
                    {
                        commands.Add(new("DELETE FROM t WHERE id=@id", new Dictionary<string, object?> { ["id"] = id }));
                        oracle.Remove(id);
                    }
                    else
                    {
                        long value = random.NextInt64();
                        commands.Add(new("INSERT INTO t VALUES(@id,@n,X'010203') ON CONFLICT(id) DO UPDATE SET n=excluded.n", new Dictionary<string, object?> { ["id"] = id, ["n"] = value }));
                        oracle[id] = value;
                    }
                }
                await db.ExecuteTransactionAsync(commands);
                if (batch % 3 == 0)
                    await db.CheckpointAsync();
                if (batch == 7)
                    await db.CompactAsync();
                Parallel.For(0, 12, index =>
                {
                    bool desc = index % 2 == 0;
                    int skip = index * 3;
                    string direction = desc ? "DESC" : "ASC";
                    var actual = db.Query($"SELECT id,n,b,b FROM t WHERE id BETWEEN 0 AND 1099511627775 ORDER BY id {direction} LIMIT 20 OFFSET {skip}").Rows;
                    IEnumerable<KeyValuePair<long, long>> expected = desc ? oracle.Reverse() : oracle;
                    var selected = expected.Skip(skip).Take(20).ToArray();
                    Check(actual.Count == selected.Length, "primary range count");
                    for (int j = 0; j < selected.Length; j++)
                    {
                        Check(Equals(actual[j][0], selected[j].Key) && Equals(actual[j][1], selected[j].Value), "primary range order/value");
                        var first = (byte[])actual[j][2]!;
                        var second = (byte[])actual[j][3]!;
                        first[0] = 99;
                        Check(second[0] == 1, "duplicate BLOB projection aliases writable arrays");
                    }
                });
                var top = db.Query("SELECT id FROM t ORDER BY id DESC LIMIT 20");
                Check(top.Rows.Select(row => (long)row[0]!).SequenceEqual(oracle.Keys.Reverse().Take(20)), "descending primary top without WHERE");
                Check(!File.Exists(path + ".sidx-1-0"), "redundant primary sidecar was created");
            }
            Check(Equals(db.Query("SELECT COUNT(*) FROM t").Rows[0][0], (long)oracle.Count), "model final count");
        }
    }

    /// <summary>在独立模型与实际数据不一致时停止测试。</summary>
    /// <param name="condition">需要成立的断言。</param>
    /// <param name="message">失败的模型条件。</param>
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}
