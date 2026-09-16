// ══ 移植自 dotnet/runtime System.IO.Hashing (Crc32.cs 核心, v8.0.0, MIT)——自研化收编。
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// 说明：官方 Crc32 实例语义 = 内部寄存器 _crc（初值 0xFFFFFFFF，反射位序）+ 输出时 ~ 取反
// （GetCurrentHashAsUInt32 => ~_crc）。本核心只搬 Update 寄存器语义（含向量/ARM 分支），
// UnifiedCrc.ComputeCrc32 的 canonical（zlib）公共语义在调用侧以 ~ 段边界转换——输出逐位不变。

using System.Runtime.Intrinsics.Arm;

namespace TC.Tier.Core.Primitives;

internal static partial class Crc32IeeeCore
{
    /// <summary>官方初始寄存器（canonical 0 → 寄存器 0xFFFFFFFF）。</summary>
    public const uint InitialRegister = 0xFFFF_FFFFu;

    /// <summary>
    /// 寄存器续算（官方 Update 语义——寄存器 = canonical 值的按位取反，段间按官方实例链）。
    /// <para>★ ≥16B 且支持 PCLMULQDQ / ARM AES+AdvSimd → UpdateVectorized；ARMv8 CRC32 指令可用
    /// → UpdateScalarArm64/32（IEEE 多项式专用指令）；否则查表。</para>
    /// </summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> source)
    {
        if (VectorHelper.IsSupported && source.Length >= System.Runtime.Intrinsics.Vector128<byte>.Count)
        {
            return UpdateVectorized(crc, source);
        }

        return UpdateScalar(crc, source);
    }

    private static uint UpdateScalar(uint crc, ReadOnlySpan<byte> source)
    {
        // ARMv8 crc32* 指令即 IEEE 802.3 多项式（区别于 Castagnoli 的 crc32c*）——优先于查表。
        if (Crc32.Arm64.IsSupported)
        {
            return UpdateScalarArm64(crc, source);
        }

        if (Crc32.IsSupported)
        {
            return UpdateScalarArm32(crc, source);
        }

        ReadOnlySpan<uint> crcLookup = CrcLookup;
        for (int i = 0; i < source.Length; i++)
        {
            byte idx = (byte)crc;
            idx ^= source[i];
            crc = crcLookup[idx] ^ (crc >> 8);
        }

        return crc;
    }
}
