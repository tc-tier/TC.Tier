// ══ 移植自 dotnet/runtime System.IO.Hashing (Crc64.cs, v8.0.0, MIT)——
//    自研化收编：算法/字节序/性能与官方逐位一致。下行为原版权头（MIT 要求保留）。
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// 说明：官方 Crc64 为 ECMA-182 Annex B 多项式 0x42F0E1EBA9EA3693（非 ISO 3309）。
//    仅移植实例型增量 API（本仓库经 UnifiedCrc 使用的 Reset/Append/GetCurrentHash）；
//    byte[]/一次性静态 API 未移植（仓库统一走 UnifiedCrc.ComputeCrc64）。
//    GetCurrentHash 字节序与官方一致（Big Endian）——UnifiedCrc.FinalizeCrc64 的
//    ReadUInt64LittleEndian(GetCurrentHash()) 数值约定不受影响（迁移前逐位相同）。

using System.Buffers.Binary;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// CRC-64（ECMA-182）增量计算器——System.IO.Hashing.Crc64 的自研化替代（算法/输出逐位一致）。
/// <para>★ 配套 <see cref="UnifiedCrc"/>：一次性计算走 <c>UnifiedCrc.ComputeCrc64</c>（ThreadStatic 复用本类实例）；
/// 跨 chunk 累积走 <c>UnifiedCrc.CreateCrc64()</c> + Append + <c>UnifiedCrc.FinalizeCrc64()</c>。</para>
/// <para>★ ≥16B 输入走 PCLMULQDQ/ARM AES 向量折叠（性能与官方同）；短输入/无指令平台走 256 项查表。</para>
/// </summary>
public sealed partial class UnifiedCrc64
{
    private const ulong InitialState = 0UL;
    private const int Size = sizeof(ulong);

    private ulong _crc = InitialState;

    /// <summary>创建增量计算器（初始状态 = 0——官方 Crc64 语义）。</summary>
    public UnifiedCrc64()
    {
    }

    /// <summary>追加数据到已处理内容。</summary>
    /// <param name="source">本段待累积数据（可空 span——空为 no-op）。</param>
    public void Append(ReadOnlySpan<byte> source)
    {
        _crc = Update(_crc, source);
    }

    /// <summary>重置到初始状态。</summary>
    public void Reset()
    {
        _crc = InitialState;
    }

    /// <summary>当前 CRC 值（不改状态）。</summary>
    /// <returns>当前累积内容的 CRC-64 值（官方 _crc 数值口径）。</returns>
    public ulong GetCurrentHashAsUInt64() => _crc;

    /// <summary>写当前 CRC 到 <paramref name="destination"/>（Big Endian，不改状态——官方字节序语义）。</summary>
    /// <param name="destination">输出缓冲区，长度至少 8 字节；前 8 字节被覆盖为当前 CRC 的 Big Endian 字节。</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> 长度 &lt; 8。</exception>
    public void GetCurrentHash(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException("destination 过短（CRC-64 输出 8 字节）。", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, _crc);
    }

    /// <summary>
    /// 一次性计算（与官方静态 <c>Crc64.HashToUInt64</c> 同型——无实例/TLS 脚手架，热路径首选）。
    /// <para>★ 返回官方语义的 _crc 数值（Big Endian 字节序列 = 官方 Hash() 输出字节）。
    /// UnifiedCrc.ComputeCrc64 的 LE 数值约定由此值字节翻转得到（迁移语义不变）。</para>
    /// </summary>
    internal static ulong ComputeOneShot(ReadOnlySpan<byte> source) => Update(InitialState, source);

    private static ulong Update(ulong crc, ReadOnlySpan<byte> source)
    {
        if (CanBeVectorized(source))
        {
            return UpdateVectorized(crc, source);
        }

        return UpdateScalar(crc, source);
    }

    private static ulong UpdateScalar(ulong crc, ReadOnlySpan<byte> source)
    {
        ReadOnlySpan<ulong> crcLookup = CrcLookup;
        for (int i = 0; i < source.Length; i++)
        {
            ulong idx = (crc >> 56);
            idx ^= source[i];
            crc = crcLookup[(int)idx] ^ (crc << 8);
        }

        return crc;
    }
}
