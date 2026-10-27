using PureDb;

/// <summary>验证操作系统拒绝部分写入后，不确认失败事务，并能够恢复此前已确认的数据。</summary>
internal static class IoLimitChecks
{
    /// <summary>在受文件长度限制的独立进程中写入，或在限制解除后验证恢复。</summary>
    /// <param name="mode">write 表示触发真实写入错误，read 表示验证恢复和后续提交。</param>
    /// <param name="path">本次测试专用的原生文件。</param>
    /// <returns>文件写入或恢复断言完成的任务。</returns>
    internal static async Task Run(string mode, string path)
    {
        using var db = new Database(path, new() { VerifyOnOpen = true });
        if (mode == "write")
        {
            await db.PutAsync(1, 1, Enumerable.Repeat((byte)61, 256).ToArray());
            byte[] large = new byte[16 * 1024 * 1024];
            _ = Crc32.Compute(large);
            // 运行时、写入线程和校验路径先初始化，父进程随后只限制此进程的文件增长。
            Console.WriteLine("READY");
            Console.Out.Flush();
            if (Console.ReadLine() != "GO") throw new InvalidDataException("Missing write-limit synchronization");
            bool failed = false;
            try { await db.PutAsync(2, 2, large); }
            catch (Exception error) when (error is IOException or ArgumentOutOfRangeException)
            {
                // .NET 在 Linux 上将 EFBIG 映射为 ArgumentOutOfRangeException；
                // 父进程还会核对文件确实被写到上限，排除输入校验提前拒绝的情况。
                failed = true;
                Console.WriteLine("OS_FAILURE " + error.GetType().Name);
            }
            if (!failed) throw new InvalidDataException("Expected a real OS write limit failure");
            try { await db.PutAsync(3, 3, new byte[] { 7 }); throw new InvalidDataException("Faulted writer accepted another transaction"); }
            catch (IOException) { }
        }
        else
        {
            if (db.Count != 1 || db.Get(2) is not null || db.Get(3) is not null || db.Get(1) is not { } first || first.Value.Length != 256 || first.Value.Any(value => value != 61))
                throw new InvalidDataException("Write failure lost the acknowledged prefix or committed a partial transaction");
            db.VerifyIntegrity();
            await db.PutAsync(4, 4, new byte[] { 9 });
            await db.CompactAsync();
            if (db.Count != 2 || db.Get(4)!.Value[0] != 9) throw new InvalidDataException("Recovered file could not commit again");
        }
        Console.WriteLine("PASS OS write limit " + mode);
    }
}
