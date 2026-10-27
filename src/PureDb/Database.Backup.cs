using System.Buffers;

namespace PureDb;

/// <remarks>本部分提供已提交原生日志的一致备份，备份文件可独立恢复且不需要复制旁路索引。</remarks>
public sealed partial class Database
{
    /// <summary>同步备份当前已提交快照；成功时以新文件发布，拒绝覆盖已有目标。</summary>
    /// <param name="destination">尚不存在的目标文件路径；父目录必须已存在。</param>
    /// <param name="cancellationToken">逐块检查的取消信号；取消或失败不发布半份备份。</param>
    /// <exception cref="IOException">目标已存在，或源读取、目标写入及刷盘失败。</exception>
    /// <exception cref="OperationCanceledException">备份发布前收到取消。</exception>
    public void Backup(string destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string target = Path.GetFullPath(destination);
        if (File.Exists(target))
            throw new IOException("Backup destination already exists.");
        using var lease = new ReadLease(this);
        long length = _scanOffset;
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan))
            {
                long offset = 0;
                while (offset < length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(buffer.Length, length - offset);
                    ReadAt(ReadHandle(), buffer.AsSpan(0, count), offset);
                    output.Write(buffer.AsSpan(0, count));
                    offset += count;
                }
                output.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
