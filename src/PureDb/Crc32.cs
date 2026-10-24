using System.Buffers.Binary;

namespace PureDb;

/// <summary>IEEE CRC-32 校验器；按硬件能力使用多项式折叠或标量查表，结果保持文件格式一致。</summary>
internal static partial class Crc32
{
    private static readonly uint[] Table = Build();
    /// <summary>生成 IEEE CRC-32 的标量查表。</summary>
    /// <returns>本次绑定、解析或查询得到的结果对象；生命周期见方法说明。</returns>
    private static uint[] Build()
    {
        var table = new uint[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++)
                c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }

        for (int slice = 1; slice < 8; slice++)
            for (int i = 0; i < 256; i++)
            {
                uint previous = table[(slice - 1) * 256 + i];
                table[slice * 256 + i] = table[previous & 255] ^ (previous >> 8);
            }

        return table;
    }

    /// <summary>计算指定字节序列的 IEEE CRC-32，自动选择可用的向量化路径。</summary>
    /// <param name="bytes">待校验或解码的只读编码字节。</param>
    /// <returns>按方法规定计算的数量、位置、版本或比较结果。</returns>
    public static uint Compute(ReadOnlySpan<byte> bytes)
    {
        if (System.Runtime.Intrinsics.Arm.Crc32.IsSupported && BitConverter.IsLittleEndian)
        {
            uint arm = uint.MaxValue;
            if (System.Runtime.Intrinsics.Arm.Crc32.Arm64.IsSupported)
                while (bytes.Length >= 8)
                {
                    arm = System.Runtime.Intrinsics.Arm.Crc32.Arm64.ComputeCrc32(arm, BinaryPrimitives.ReadUInt64LittleEndian(bytes));
                    bytes = bytes[8..];
                }

            while (bytes.Length >= 4)
            {
                arm = System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32(arm, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
                bytes = bytes[4..];
            }

            foreach (byte b in bytes)
                arm = System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32(arm, b);
            return ~arm;
        }

        if (System.Runtime.Intrinsics.X86.Pclmulqdq.IsSupported && System.Runtime.Intrinsics.X86.Sse2.IsSupported && BitConverter.IsLittleEndian && bytes.Length >= 64)
            return ~Vectorized(uint.MaxValue, bytes);
        uint c = uint.MaxValue;
        while (bytes.Length >= 8)
        {
            uint a = c ^ BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            uint b = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
            c = Table[1792 + (a & 255)] ^ Table[1536 + ((a >> 8) & 255)] ^ Table[1280 + ((a >> 16) & 255)] ^ Table[1024 + (a >> 24)] ^ Table[768 + (b & 255)] ^ Table[512 + ((b >> 8) & 255)] ^ Table[256 + ((b >> 16) & 255)] ^ Table[b >> 24];
            bytes = bytes[8..];
        }

        foreach (byte b in bytes)
            c = Table[(c ^ b) & 255] ^ (c >> 8);
        return ~c;
    }
}
