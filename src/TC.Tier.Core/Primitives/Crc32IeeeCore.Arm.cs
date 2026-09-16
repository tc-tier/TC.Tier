// ══ 移植自 dotnet/runtime System.IO.Hashing (对应 Crc32.*.cs, v8.0.0, MIT)——自研化收编：IEEE CRC-32 向量/查表核心原样。
﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.


using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ArmCrc = System.Runtime.Intrinsics.Arm.Crc32;

namespace TC.Tier.Core.Primitives
{
    internal static partial class Crc32IeeeCore
    {
        private static uint UpdateScalarArm64(uint crc, ReadOnlySpan<byte> source)
        {
            Debug.Assert(ArmCrc.Arm64.IsSupported, "ARM CRC support is required.");

            // Compute in 8 byte chunks
            if (source.Length >= sizeof(ulong))
            {
                ref byte ptr = ref MemoryMarshal.GetReference(source);
                int longLength = source.Length & ~0x7; // Exclude trailing bytes not a multiple of 8

                for (int i = 0; i < longLength; i += sizeof(ulong))
                {
                    crc = ArmCrc.Arm64.ComputeCrc32(crc,
                        Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref ptr, i)));
                }

                source = source.Slice(longLength);
            }

            // Compute remaining bytes
            for (int i = 0; i < source.Length; i++)
            {
                crc = ArmCrc.ComputeCrc32(crc, source[i]);
            }

            return crc;
        }

        private static uint UpdateScalarArm32(uint crc, ReadOnlySpan<byte> source)
        {
            Debug.Assert(ArmCrc.IsSupported, "ARM CRC support is required.");

            // Compute in 4 byte chunks
            if (source.Length >= sizeof(uint))
            {
                ref byte ptr = ref MemoryMarshal.GetReference(source);
                int intLength = source.Length & ~0x3; // Exclude trailing bytes not a multiple of 4

                for (int i = 0; i < intLength; i += sizeof(uint))
                {
                    crc = ArmCrc.ComputeCrc32(crc,
                        Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref ptr, i)));
                }

                source = source.Slice(intLength);
            }

            // Compute remaining bytes
            for (int i = 0; i < source.Length; i++)
            {
                crc = ArmCrc.ComputeCrc32(crc, source[i]);
            }

            return crc;
        }
    }
}
