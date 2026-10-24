using System.Buffers.Binary;
using PureDb;

/// <summary>在临时生成的数据库上验证共享日志增量恢复的边界及故障行为。</summary>
internal static class SharedReplayChecks
{
    /// <summary>验证跨缓冲区帧、大帧、截断和完整帧损坏，不接触业务数据库。</summary>
    /// <param name = "folder">本次测试独占的临时目录。</param>
    /// <returns>全部恢复断言完成的任务。</returns>
    public static async Task Run(string folder)
    {
        string path = Path.Combine(folder, "shared-boundaries.pdb");
        using (var reader = Open(path))
        using (var writer = Open(path))
        {
            for (int i = 0; i < 180; i++)
                await writer.PutAsync(i, i, Enumerable.Repeat((byte)i, 800 + i % 17).ToArray());
            await writer.PutAsync(500, 9, new byte[180000]);
            await writer.PutAsync(501, 10, new byte[] { 42 });
            Check(reader.Count == 182, "Shared tail count across buffer refills");
            for (int i = 0; i < 180; i++)
                Check(reader.Get(i)!.Value.SequenceEqual(Enumerable.Repeat((byte)i, 800 + i % 17)), "Shared small frame boundary");
            Check(reader.Get(500)!.Value.Length == 180000 && reader.Get(501)!.Value[0] == 42, "Shared large frame boundary");
            await writer.CompactAsync();
            Check(reader.Count == 182 && reader.Get(501)!.Value[0] == 42, "Shared generation fallback");
            await reader.PutAsync(502, 11, new byte[] { 43 });
            Check(writer.Get(502)!.Value[0] == 43, "Shared write after generation fallback");
            reader.VerifyIntegrity();
        }

        string donor = Path.Combine(folder, "shared-frame-source.pdb");
        using (var db = Open(donor))
            await db.PutAsync(2, 2, new byte[] { 1, 2, 3, 4, 5 });
        byte[] frame = File.ReadAllBytes(donor)[16..];
        // 对每一个字节位置生成独立副本，完整帧才能生效，半帧只能回退到原有提交。
        for (int cut = 0; cut <= frame.Length; cut++)
        {
            string truncated = Path.Combine(folder, $"shared-cut-{cut}.pdb");
            using (var db = Open(truncated))
            {
                await db.PutAsync(1, 1, new byte[] { 9 });
                Append(truncated, frame.AsSpan(0, cut));
                await db.PutAsync(3, 3, new byte[] { 7 });
                Check(db.Get(1)!.Value[0] == 9 && db.Get(3)!.Value[0] == 7, "Shared truncation lost acknowledged prefix");
                Check((db.Get(2)is not null) == (cut == frame.Length), "Shared partial frame became visible");
                db.VerifyIntegrity();
            }

            using var reopened = Open(truncated);
            Check(reopened.Count == (cut == frame.Length ? 3 : 2), "Shared truncation reopen");
        }

        for (int kind = 0; kind < 6; kind++)
        {
            string damaged = Path.Combine(folder, $"shared-bad-{kind}.pdb");
            using (var db = Open(damaged))
            {
                await db.PutAsync(1, 1, new byte[] { 9 });
                byte[] bad = frame.ToArray();
                switch (kind)
                {
                    case 0:
                        bad[0] ^= 1;
                        break;
                    case 1:
                        bad[8] ^= 1;
                        break;
                    case 2:
                        bad[^1] ^= 1;
                        break;
                    case 3:
                        BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(16), int.MaxValue);
                        UpdateCrc(bad);
                        break;
                    case 4:
                        BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(36), int.MaxValue);
                        UpdateCrc(bad);
                        break;
                    case 5:
                        BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(16), 0);
                        UpdateCrc(bad);
                        break;
                }

                Append(damaged, bad);
                byte[] original = ReadShared(damaged);
                bool rejected = false;
                try
                {
                    await db.PutAsync(3, 3, new byte[] { 7 });
                }
                catch (InvalidDataException)
                {
                    rejected = true;
                }

                Check(rejected && ReadShared(damaged).SequenceEqual(original), "Shared corrupt complete frame was accepted or truncated");
            }

            bool reopenRejected = false;
            try
            {
                using var reopened = Open(damaged);
            }
            catch (InvalidDataException)
            {
                reopenRejected = true;
            }

            Check(reopenRejected, "Shared corrupt frame passed full recovery");
        }

        string shortPrefix = Path.Combine(folder, "shared-short-prefix.pdb");
        using (var db = Open(shortPrefix))
        {
            await db.PutAsync(1, 1, new byte[100]);
            using (var file = new FileStream(shortPrefix, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                file.SetLength(16);
            bool rejected = false;
            try
            {
                await db.PutAsync(2, 2, new byte[] { 1 });
            }
            catch (InvalidDataException)
            {
                rejected = true;
            }

            Check(rejected && new FileInfo(shortPrefix).Length == 16, "Shared committed prefix silently expanded");
        }
    }

    /// <summary>打开当前测试使用的共享缓冲数据库。</summary>
    /// <param name = "path">测试文件的绝对路径。</param>
    /// <returns>由调用方负责释放的数据库。</returns>
    private static Database Open(string path) => new(path, new() { Access = DatabaseAccess.Shared, Durability = Durability.Buffered });
    /// <summary>在没有并发写入时追加故障注入字节，模拟未发布或中断的日志尾部。</summary>
    /// <param name = "path">仅供本测试使用的文件。</param>
    /// <param name = "bytes">需要追加的帧或半帧。</param>
    private static void Append(string path, ReadOnlySpan<byte> bytes)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        file.Position = file.Length;
        file.Write(bytes);
        file.Flush(true);
    }

    /// <summary>在测试数据库仍打开时读取快照，保持 Windows 文件共享标志兼容。</summary>
    /// <param name = "path">没有并发写入的测试文件。</param>
    /// <returns>文件的完整字节副本。</returns>
    private static byte[] ReadShared(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>按公开文件格式重新计算 CRC，以单独检验操作边界验证。</summary>
    /// <param name = "frame">已更改操作内容的完整测试帧。</param>
    private static void UpdateCrc(byte[] frame)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in frame.AsSpan(16))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320U);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), ~crc);
    }

    /// <summary>断言恢复结果，不满足条件时终止测试。</summary>
    /// <param name = "condition">必须成立的条件。</param>
    /// <param name = "message">失败时的诊断信息。</param>
    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
