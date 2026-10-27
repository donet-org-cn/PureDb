using System.Diagnostics;
using System.Runtime.InteropServices;

// 此文件仅属于测试宿主，Windows 计数器调用不进入两个纯托管产品包。
internal readonly record struct ScaleIo(long ReadBytes, long WriteBytes, long ReadOperations, long WriteOperations, long? StorageReadBytes, long? StorageWriteBytes)
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Counters { public ulong Reads, Writes, Others, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(nint process, out Counters counters);
    internal static ScaleIo Read()
    {
        if (OperatingSystem.IsLinux() && File.Exists("/proc/self/io"))
        {
            var values = File.ReadAllLines("/proc/self/io").Select(line => line.Split(':')).ToDictionary(parts => parts[0], parts => long.Parse(parts[1].Trim()));
            return new(values["rchar"], values["wchar"], values["syscr"], values["syscw"], values["read_bytes"], values["write_bytes"]);
        }
        if (!OperatingSystem.IsWindows()) return new(-1, -1, -1, -1, null, null);
        using var process = Process.GetCurrentProcess();
        if (!GetProcessIoCounters(process.Handle, out var counters)) throw new System.ComponentModel.Win32Exception();
        return new((long)counters.ReadBytes, (long)counters.WriteBytes, (long)counters.Reads, (long)counters.Writes, null, null);
    }
    public static ScaleIo operator -(ScaleIo a, ScaleIo b) => new(a.ReadBytes - b.ReadBytes, a.WriteBytes - b.WriteBytes, a.ReadOperations - b.ReadOperations, a.WriteOperations - b.WriteOperations, a.StorageReadBytes - b.StorageReadBytes, a.StorageWriteBytes - b.StorageWriteBytes);
}
