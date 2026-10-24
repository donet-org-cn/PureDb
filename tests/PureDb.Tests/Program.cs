using System.Buffers.Binary;
using System.Diagnostics;
using PureDb;

if (args is ["--crc-bench", var crcOutput])
{
    var compute = typeof(Database).Assembly.GetType("PureDb.Crc32")!.GetMethod("Compute")!.CreateDelegate<CrcFunction>();
    var results = new List<object>(); uint sink = 0;
    foreach (int length in new[] {16,64,256,4096,65536,1048576})
    {
        byte[] buffer = new byte[length]; new Random(42).NextBytes(buffer);
        uint expected=uint.MaxValue; foreach(byte b in buffer){expected^=b;for(int bit=0;bit<8;bit++)expected=(expected&1)!=0?(expected>>1)^0xedb88320u:expected>>1;}
        if (compute(buffer)!=~expected) throw new Exception("CRC oracle");
        for(int warm=0;warm<100;warm++)sink^=compute(buffer);
        for(int round=1;round<=3;round++)
        {
            using var p=Process.GetCurrentProcess();double cpu=p.TotalProcessorTime.TotalMilliseconds;long alloc=GC.GetTotalAllocatedBytes(true);int operations=0;var timer=Stopwatch.StartNew();
            do { sink^=compute(buffer); operations++; } while(timer.ElapsedMilliseconds<400);
            timer.Stop();results.Add(new{Length=length,Round=round,Operations=operations,Milliseconds=timer.Elapsed.TotalMilliseconds,CpuMilliseconds=p.TotalProcessorTime.TotalMilliseconds-cpu,AllocatedBytes=GC.GetTotalAllocatedBytes(true)-alloc});
        }
    }
    File.WriteAllText(crcOutput,System.Text.Json.JsonSerializer.Serialize(new{Results=results,Sink=sink,ArmCRC=System.Runtime.Intrinsics.Arm.Crc32.IsSupported,AdvSimd=System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported,Runtime=Environment.Version.ToString(),Hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(Database).Assembly.Location)))},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));return;
}
if (args is ["crash-writer", var crashPath])
{
    var db = new Database(crashPath);
    for (int i = 0; i < 100; i++) await db.PutAsync(i, i % 7, BitConverter.GetBytes(i));
    Console.WriteLine("COMMITTED"); Console.Out.Flush();
    Process.GetCurrentProcess().Kill();
    return;
}
var root = Path.GetFullPath(Path.Combine("artifacts", "tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
int passed = 0;
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task Test(string name, Func<Task> body) { await body(); Console.WriteLine($"PASS {name}"); passed++; }
async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
string PathFor(string name) => Path.Combine(root, name + ".pdb");
await Test("CRUD, duplicate keys, boundaries, index maintenance, reopen", async () =>
{
    var path = PathFor("crud");
    await using (var db = new Database(path))
    {
        Assert(db.Get(10) is null, "missing");
        await db.WriteAsync([Mutation.Put(1, 9, new byte[] { 1 }), Mutation.Put(1, -9, new byte[] { 2 }), Mutation.Put(2, -9, Array.Empty<byte>())]);
        Assert(db.Count == 2 && db.Get(1)!.Value[0] == 2, "upsert");
        Assert(db.Query(-9, -9).Select(x => x.Id).SequenceEqual(new long[] { 1, 2 }), "duplicate index ordering");
        Assert(db.Query(9, 9).Count == 0 && db.Query(5, 1).Count == 0 && db.Query(-10, 10, 0).Count == 0, "bounds");
        await db.PutAsync(long.MinValue, long.MinValue, new byte[] { 3 });
        await db.PutAsync(long.MaxValue, long.MaxValue, new byte[] { 4 });
        Assert(db.Query(long.MinValue, long.MaxValue).Count == 4, "signed extremes");
        await db.DeleteAsync(1); await db.DeleteAsync(999);
    }
    await using var reopened = new Database(path);
    Assert(reopened.Count == 3 && reopened.Get(1) is null && reopened.Get(2)!.Value.Length == 0, "reopen");
});
await Test("randomized transactions against independent dictionary oracle", async () =>
{
    var path = PathFor("oracle"); var oracle = new Dictionary<long, DbRecord>(); var rng = new Random(92);
    await using (var db = new Database(path, new() { Durability = Durability.Buffered }))
    {
        for (int b = 0; b < 200; b++)
        {
            var ops = new List<Mutation>();
            for (int j = 0; j < 30; j++)
            {
                long id = rng.Next(300), key = rng.Next(-10, 11); byte[] value = new byte[rng.Next(200)]; rng.NextBytes(value);
                if (rng.Next(4) == 0) { ops.Add(Mutation.Delete(id)); oracle.Remove(id); }
                else { ops.Add(Mutation.Put(id, key, value)); oracle[id] = new(id, key, value); }
            }
            await db.WriteAsync(ops);
            Assert(db.Count == oracle.Count, "oracle count");
            var actual = db.Query(-5, 5, 77);
            var expected = oracle.Values.Where(x => x.IndexKey >= -5 && x.IndexKey <= 5).OrderBy(x => x.IndexKey).ThenBy(x => x.Id).Take(77).ToArray();
            Assert(actual.Count == expected.Length, "range count");
            for (int k = 0; k < expected.Length; k++) Assert(actual[k].Id == expected[k].Id && actual[k].Value.SequenceEqual(expected[k].Value), "range data");
        }
    }
    await using var read = new Database(path);
    foreach (var (id, row) in oracle) Assert(read.Get(id)!.Value.SequenceEqual(row.Value), "oracle persistence");
});
await Test("concurrent writes, read isolation, compaction and queue backpressure", async () =>
{
    await using var db = new Database(PathFor("concurrent"), new() { QueueCapacity = 2 });
    await db.WriteAsync([Mutation.Put(-1, -1, BitConverter.GetBytes(0)), Mutation.Put(-2, -1, BitConverter.GetBytes(0))]);
    using var readStop = new CancellationTokenSource();
    var reader = Task.Run(async () =>
    {
        for (int i = 0; !readStop.IsCancellationRequested || i < 2000; i++)
        {
            var pair = db.Query(-1, -1);
            Assert(pair.Count == 2 && pair[0].Value.SequenceEqual(pair[1].Value), "partial transaction observed");
            await Task.Delay(1);
        }
    });
    await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
    {
        for (int j = 0; j < 200; j++)
        {
            long id = worker * 200 + j;
            await db.PutAsync(id, id % 10, BitConverter.GetBytes(id));
            if (worker == 0) await db.WriteAsync([Mutation.Put(-1, -1, BitConverter.GetBytes(j)), Mutation.Put(-2, -1, BitConverter.GetBytes(j))]);
            if (worker == 1 && j % 50 == 0) await db.CompactAsync();
        }
    })));
    readStop.Cancel();
    await reader;
    Assert(db.Count == 1602, "concurrent count");
    for (int i = 0; i < 1600; i++) Assert(BitConverter.ToInt64(db.Get(i)!.Value) == i, "concurrent bytes");
});
await Test("compaction shrink, reopen, stale temporary file", async () =>
{
    var path = PathFor("compact");
    await using (var db = new Database(path))
    {
        for (int i = 0; i < 50; i++) await db.PutAsync(1, i, new byte[2048]);
        long before = new FileInfo(path).Length;
        await db.CompactAsync(); Assert(new FileInfo(path).Length < before / 10 && db.Get(1)!.IndexKey == 49, "compact");
    }
    File.WriteAllText(path + ".compact", "interrupted compaction");
    await using var read = new Database(path); Assert(read.Get(1)!.IndexKey == 49, "stale temp");
});
await Test("every truncated transaction prefix recovers atomically", async () =>
{
    var path = PathFor("trunc-source"); long prefix;
    await using (var db = new Database(path)) { await db.PutAsync(1, 1, new byte[] { 7 }); prefix = new FileInfo(path).Length; await db.WriteAsync([Mutation.Put(2, 2, new byte[40]), Mutation.Delete(1)]); }
    var bytes = File.ReadAllBytes(path);
    for (int end = (int)prefix; end < bytes.Length; end++)
    {
        var copy = PathFor("trunc-" + end); File.WriteAllBytes(copy, bytes[..end]);
        await using var db = new Database(copy);
        Assert(db.Count == 1 && db.Get(1)!.Value[0] == 7 && db.Get(2) is null, "partial batch recovered");
        await db.PutAsync(3, 3, new byte[] { 9 });
    }
});
await Test("checksum corruption refuses open instead of deleting data", async () =>
{
    var path = PathFor("corrupt"); await using (var db = new Database(path)) await db.PutAsync(1, 1, new byte[40]);
    DateTime originalTime = File.GetLastWriteTimeUtc(path);
    byte[] bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
    // Make the external edit observable even on filesystems with coarse timestamps.
    // Same-stamp corruption and deferred checks are covered by PureDb.StorageTests.
    File.SetLastWriteTimeUtc(path, originalTime.AddSeconds(1));
    await Throws<InvalidDataException>(() => { using var db = new Database(path); return Task.CompletedTask; });
});
await Test("exclusive owner, cancellation, size limits, copied values, concurrent disposal", async () =>
{
    var path = PathFor("lifecycle"); var db = new Database(path, new() { MaxTransactionBytes = 128 });
    await Throws<IOException>(() => { using var other = new Database(path); return Task.CompletedTask; });
    var cts = new CancellationTokenSource(); cts.Cancel();
    await Throws<OperationCanceledException>(async () => await db.PutAsync(1, 1, new byte[1], cts.Token));
    await Throws<ArgumentException>(async () => await db.PutAsync(1, 1, new byte[200]));
    byte[] value = [12]; var pending = db.PutAsync(2, 2, value); value[0] = 99; await pending;
    Assert(db.Get(2)!.Value[0] == 12 && db.Get(1) is null, "copy or rejection");
    var writes = Enumerable.Range(3, 50).Select(i => db.PutAsync(i, i, new byte[1]).AsTask()).ToArray();
    await Task.WhenAll(db.DisposeAsync().AsTask(), db.DisposeAsync().AsTask()); await Task.WhenAll(writes);
    await Throws<ObjectDisposedException>(() => { db.Get(2); return Task.CompletedTask; });
    await using var read = new Database(path); Assert(read.Count == 51, "drain on disposal");
});
await Test("acknowledged Full writes survive process kill without disposal", async () =>
{
    var path = PathFor("kill");
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(typeof(Program).Assembly.Location); start.ArgumentList.Add("crash-writer"); start.ArgumentList.Add(path);
    using var process = Process.Start(start)!;
    string output = await process.StandardOutput.ReadToEndAsync(); await process.WaitForExitAsync();
    Assert(output.Contains("COMMITTED"), "child did not commit: " + await process.StandardError.ReadToEndAsync());
    await using var db = new Database(path);
    Assert(db.Count == 100, "kill count");
    for (int i = 0; i < 100; i++) Assert(BitConverter.ToInt32(db.Get(i)!.Value) == i, "kill payload");
});
await Test("bad headers, malformed checksummed frames, large values and transaction budget", async () =>
{
    var path = PathFor("invalid"); File.WriteAllBytes(path, new byte[16]);
    await Throws<InvalidDataException>(() => { using var db = new Database(path); return Task.CompletedTask; });
    var largePath = PathFor("large"); byte[] payload = new byte[1024 * 1024]; new Random(2).NextBytes(payload);
    await using (var db = new Database(largePath, new() { MaxTransactionBytes = payload.Length + 28 }))
    {
        await db.PutAsync(1, 1, payload);
        Assert(db.Get(1)!.Value.SequenceEqual(payload), "large value");
        await Throws<ArgumentException>(async () => await db.WriteAsync([Mutation.Put(2, 2, payload), Mutation.Delete(1)]));
        Assert(db.Count == 1, "oversized transaction partially applied");
        await db.CompactAsync();
    }
    await using (var db = new Database(largePath)) Assert(db.Get(1)!.Value.SequenceEqual(payload), "large reopen");
    // Valid CRC with impossible operation count exercises structure validation independently of checksum.
    var malformedPath = PathFor("malformed");
    await using (var db = new Database(malformedPath)) await db.PutAsync(1, 1, new byte[1]);
    DateTime malformedTime = File.GetLastWriteTimeUtc(malformedPath);
    byte[] data = File.ReadAllBytes(malformedPath);
    BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(32), int.MaxValue);
    uint crc = uint.MaxValue;
    foreach (byte b in data.AsSpan(32))
    {
        crc ^= b;
        for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
    }
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), ~crc);
    File.WriteAllBytes(malformedPath, data);
    await Throws<InvalidDataException>(() => { using var db = new Database(malformedPath, new() { VerifyOnOpen = true }); return Task.CompletedTask; });
    File.SetLastWriteTimeUtc(malformedPath, malformedTime.AddSeconds(1));
    await Throws<InvalidDataException>(() => { using var db = new Database(malformedPath); return Task.CompletedTask; });
});
await Test("slicing CRC frames match independent bitwise IEEE checksum", async () =>
{
    var path=PathFor("crc-slices");var random=new Random(824);
    await using(var db=new Database(path,new(){Durability=Durability.Buffered}))
    {
        foreach(int length in new[]{0,1,7,8,9,15,16,17,127,128,255,256,1024,10001,65537})
        {byte[] value=new byte[length];random.NextBytes(value);await db.PutAsync(length,length,value);}
    }
    byte[] file=File.ReadAllBytes(path);int offset=16;
    while(offset<file.Length)
    {
        int length=BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(offset));uint expected=BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset+4));
        uint actual=uint.MaxValue;foreach(byte b in file.AsSpan(offset+16,length)){actual^=b;for(int i=0;i<8;i++)actual=(actual&1)!=0?(actual>>1)^0xedb88320u:actual>>1;}
        Assert(~actual==expected,"CRC wire compatibility");offset+=16+length;
    }
});
await Test("uncached synchronous random-access handles preserve concurrent offsets", async () =>
{
    await using var db = new Database(PathFor("uncached-readers"), new() { CacheBytes = 0, Durability = Durability.Buffered });
    var values = Enumerable.Range(0,3000).Select(i => { var bytes = new byte[257]; new Random(i).NextBytes(bytes); return bytes; }).ToArray();
    await db.WriteAsync(Enumerable.Range(0,values.Length).Select(i => Mutation.Put(i,i%31,values[i])).ToArray());
    await Task.WhenAll(Enumerable.Range(0,8).Select(worker => Task.Run(() =>
    {
        for (int i=0;i<2000;i++) { int id=(i*7919+worker*31)%values.Length; Assert(db.Get(id)!.Value.SequenceEqual(values[id]),"concurrent uncached offset"); }
    })));
});
Console.WriteLine($"{passed} test groups passed. Artifacts: {root}");
internal delegate uint CrcFunction(ReadOnlySpan<byte> data);
