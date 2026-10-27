using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PureDb;

/// <summary>长序列独立模型检查，交错并发读取、增改删、检查点、压缩和重开。</summary>
internal static class SoakChecks
{
    /// <summary>在本次生成的独立文件上运行指定数量的变更，并保存可审计的结果。</summary>
    /// <param name="folder">只包含本次测试文件的目录。</param>
    /// <param name="operations">请求的随机变更数量，按完整批次向上取整。</param>
    /// <returns>所有模型与持久化断言完成的任务。</returns>
    internal static async Task Run(string folder, int operations)
    {
        if (operations < 1000 || operations > 10000000)
            throw new ArgumentOutOfRangeException(nameof(operations));
        string path = Path.Combine(folder, "soak.pdb");
        var oracle = new Dictionary<long, byte[]>();
        var random = new Random(20261027);
        int completed = 0, phases = 0;
        long reads = 0;
        var timer = Stopwatch.StartNew();
        while (completed < operations)
        {
            using var db = new Database(path, new()
            {
                Durability = Durability.Buffered,
                CacheBytes = 2 * 1024 * 1024,
                CheckpointIntervalBytes = 0
            });
            using var cancellation = new CancellationTokenSource();
            var readers = Enumerable.Range(0, 4).Select(index => Task.Run(() =>
            {
                var source = new Random(99 + index);
                long count = 0;
                while (!cancellation.IsCancellationRequested)
                {
                    if (db.Get(source.Next(100000)) is { } row)
                        Validate(row.Id, row.Value);
                    count++;
                }
                Interlocked.Add(ref reads, count);
            })).ToArray();
            try
            {
                for (int batch = 0; batch < 100 && completed < operations; batch++)
                {
                    var mutations = new Mutation[1000];
                    for (int i = 0; i < mutations.Length; i++)
                    {
                        int sequence = completed + i;
                        long id = sequence % 997 == 0 ? long.MinValue : sequence % 991 == 0 ? long.MaxValue : random.Next(100000);
                        if (random.Next(5) == 0)
                        {
                            mutations[i] = Mutation.Delete(id);
                            oracle.Remove(id);
                        }
                        else
                        {
                            var bytes = new byte[128];
                            BinaryPrimitives.WriteInt64LittleEndian(bytes, id);
                            BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), sequence);
                            bytes.AsSpan(16).Fill((byte)(id ^ sequence));
                            oracle[id] = bytes;
                            mutations[i] = Mutation.Put(id, id % 31, bytes);
                        }
                    }
                    await db.WriteAsync(mutations);
                    completed += mutations.Length;
                    if (batch == 49)
                        await db.CheckpointAsync();
                }
            }
            finally
            {
                cancellation.Cancel();
                await Task.WhenAll(readers);
            }
            await db.CheckpointAsync();
            if (phases % 2 == 1)
                await db.CompactAsync();
            db.VerifyIntegrity();
            if (db.Count != oracle.Count)
                throw new InvalidDataException("Soak count differs from independent model");
            foreach (var pair in oracle)
                if (db.Get(pair.Key) is not { } row || !row.Value.AsSpan().SequenceEqual(pair.Value) || row.IndexKey != pair.Key % 31)
                    throw new InvalidDataException("Soak row differs from independent model");
            var expected = oracle.Keys.OrderBy(id => id % 31).ThenBy(id => id).Take(128);
            if (!db.Query(-31, 31, 128).Select(row => row.Id).SequenceEqual(expected))
                throw new InvalidDataException("Soak secondary order differs from independent model");
            phases++;
            Console.WriteLine($"SOAK {completed}/{operations}, rows={oracle.Count}, concurrent reads={reads}");
        }
        timer.Stop();
        var result = new
        {
            Operations = completed,
            ConcurrentReads = reads,
            ReopenPhases = phases,
            Rows = oracle.Count,
            Milliseconds = timer.Elapsed.TotalMilliseconds,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            OS = RuntimeInformation.OSDescription,
            CoreSHA256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Database).Assembly.Location)))
        };
        File.WriteAllText(Path.Combine(folder, "soak-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS SOAK " + Path.Combine(folder, "soak-result.json"));
    }

    /// <summary>验证任一并发读取只观察到一条完整记录，不观察到混合版本字节。</summary>
    /// <param name="id">记录 ID。</param>
    /// <param name="value">当前读取到的独立值副本。</param>
    private static void Validate(long id, byte[] value)
    {
        if (value.Length != 128 || BinaryPrimitives.ReadInt64LittleEndian(value) != id)
            throw new InvalidDataException("Soak read observed an invalid record header");
        byte expected = (byte)(id ^ BinaryPrimitives.ReadInt64LittleEndian(value.AsSpan(8)));
        foreach (byte item in value.AsSpan(16))
            if (item != expected)
                throw new InvalidDataException("Soak read observed mixed record versions");
    }
}
