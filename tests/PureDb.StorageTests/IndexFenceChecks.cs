using System.Buffers.Binary;
using PureDb;

/// <summary>验证索引头稀疏定位点、重复键、跨表大间隔和旧版无定位点索引的边界。</summary>
internal static class IndexFenceChecks
{
    /// <summary>用线性扫描参考结果检查两种方向的二分边界，检查点头异常必须安全回退。</summary>
    /// <param name="folder">本次自动生成文件的目录。</param>
    internal static void Run(string folder)
    {
        long prefix = 1L << 40;
        var distributions = new[]
        {
            new long[] { -1 }.Concat(Enumerable.Range(0, 20000).Select(i => prefix + i / 3)).Concat(Enumerable.Range(0, 10000).Select(i => prefix * 2 + i)).ToArray(),
            new[] { long.MinValue }.Concat(Enumerable.Range(0, 20000).Select(i => i < 18000 ? 7L : i * 12345678901L)).Append(long.MaxValue).Order().ToArray(),
            Enumerable.Range(0, 10000).Select(i => long.MaxValue - 10000 + i).ToArray()
        };
        for (int distribution = 0; distribution < distributions.Length; distribution++)
        {
            long[] keys = distributions[distribution];
            string path = Path.Combine(folder, "fence-" + distribution + ".pidx");
            var entries = keys.Select((key, ordinal) => new DiskIndex.Item(key, ordinal, 16, 0, 0)).ToArray();
            DiskIndex.Write(path, 1, 100, 1, [], entries);
            for (int legacy = 0; legacy < 2; legacy++)
            {
                using var index = DiskIndex.Open(path, 1, 100) ?? throw new InvalidDataException("fence index was rejected");
                var random = new Random(901 + distribution);
                for (int i = 0; i < 500; i++)
                {
                    long key = i % 7 == 0 ? random.NextInt64() : keys[random.Next(keys.Length)];
                    if (i % 3 == 0 && key < long.MaxValue) key++;
                    int lower = Array.FindIndex(keys, value => value >= key);
                    if (lower < 0) lower = keys.Length;
                    int upper = Array.FindIndex(keys, value => value > key);
                    if (upper < 0) upper = keys.Length;
                    if (index.Bound(key, false) != lower || index.Bound(key, true) != upper)
                        throw new InvalidDataException("Sparse fence bound differs from linear oracle");
                }
                index.Dispose();
                byte[] bytes = File.ReadAllBytes(path);
                bytes.AsSpan(64, 4092 - 64).Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4092), Crc32.Compute(bytes.AsSpan(0, 4092)));
                File.WriteAllBytes(path, bytes);
            }
            DiskIndex.Write(path, 1, 100, 1, [], entries);
            byte[] malformed = File.ReadAllBytes(path);
            BinaryPrimitives.WriteInt32LittleEndian(malformed.AsSpan(88), -1);
            BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(4092), Crc32.Compute(malformed.AsSpan(0, 4092)));
            File.WriteAllBytes(path, malformed);
            using var rejected = DiskIndex.Open(path, 1, 100);
            if (rejected is not null) throw new InvalidDataException("Malformed fence ordinal was accepted");
        }
    }
}
