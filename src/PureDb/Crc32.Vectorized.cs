// Polynomial folding adapted from .NET runtime v10.0.0 Crc32.Vectorized.cs.
// Copyright (c) .NET Foundation and Contributors. Licensed under the MIT license.
// See THIRD-PARTY-NOTICES.md. Scalar fallback and the on-disk CRC remain IEEE CRC-32.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
namespace PureDb;

internal static partial class Crc32
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> Fold(Vector128<ulong> data, Vector128<ulong> state, Vector128<ulong> constants) =>
        data ^ Pclmulqdq.CarrylessMultiply(state, constants, 0x00) ^ Pclmulqdq.CarrylessMultiply(state, constants, 0x11);
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static uint Vectorized(uint crc, ReadOnlySpan<byte> source)
    {
        ref byte src = ref MemoryMarshal.GetReference(source); int length = source.Length;
        Vector128<ulong> x1, constants;
        if (length >= 128)
        {
            x1 = Vector128.LoadUnsafe(ref src).AsUInt64() ^ Vector128.CreateScalar(crc).AsUInt64();
            var x2 = Vector128.LoadUnsafe(ref src, 16).AsUInt64(); var x3 = Vector128.LoadUnsafe(ref src, 32).AsUInt64(); var x4 = Vector128.LoadUnsafe(ref src, 48).AsUInt64();
            src = ref Unsafe.Add(ref src, 64); length -= 64;
            constants = Vector128.Create(0x0154442bd4UL, 0x01c6e41596UL);
            do
            {
                x1 = Fold(Vector128.LoadUnsafe(ref src).AsUInt64(), x1, constants);
                x2 = Fold(Vector128.LoadUnsafe(ref src, 16).AsUInt64(), x2, constants);
                x3 = Fold(Vector128.LoadUnsafe(ref src, 32).AsUInt64(), x3, constants);
                x4 = Fold(Vector128.LoadUnsafe(ref src, 48).AsUInt64(), x4, constants);
                src = ref Unsafe.Add(ref src, 64); length -= 64;
            } while (length >= 64);
            constants = Vector128.Create(0x01751997d0UL, 0x00ccaa009eUL);
            x1 = Fold(x2, x1, constants); x1 = Fold(x3, x1, constants); x1 = Fold(x4, x1, constants);
        }
        else
        {
            x1 = Vector128.LoadUnsafe(ref src).AsUInt64() ^ Vector128.CreateScalar(crc).AsUInt64();
            src = ref Unsafe.Add(ref src, 16); length -= 16;
        }
        while (length >= 16)
        {
            x1 = Fold(Vector128.LoadUnsafe(ref src).AsUInt64(), x1, Vector128.Create(0x01751997d0UL, 0x00ccaa009eUL));
            src = ref Unsafe.Add(ref src, 16); length -= 16;
        }
        var mask = Vector128.Create(~0, 0, ~0, 0).AsUInt64();
        x1 = Sse2.ShiftRightLogical128BitLane(x1.AsByte(), 8).AsUInt64() ^ Pclmulqdq.CarrylessMultiply(x1, Vector128.CreateScalar(0x00ccaa009eUL), 0x00);
        x1 = Pclmulqdq.CarrylessMultiply(x1 & mask, Vector128.CreateScalar(0x0163cd6124UL), 0x00) ^ Sse2.ShiftRightLogical128BitLane(x1.AsByte(), 4).AsUInt64();
        constants = Vector128.Create(0x01db710641UL, 0x01f7011641UL);
        var reduced = Pclmulqdq.CarrylessMultiply(x1 & mask, constants, 0x10) & mask;
        x1 ^= Pclmulqdq.CarrylessMultiply(reduced, constants, 0x00);
        uint result = x1.AsUInt32().GetElement(1);
        for (int i = 0; i < length; i++) result = Table[(result ^ Unsafe.Add(ref src, i)) & 255] ^ (result >> 8);
        return result;
    }
}
