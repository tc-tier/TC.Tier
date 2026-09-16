using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace TC.Tier.Runtime.Structures;

/// <summary>
/// CRC 算/验统一工具——所有类型共用（unified-binary-layout.md §3）。
/// <para>★ 规范字段（magic/version/flags/payloadLength/paddingLength）的读写已由源生成器生成的
/// XxxHeaderCodec.Write/Read 承担（偏移源自 [FieldOffset]，端序强制 LE）——本类不再管规范字段。</para>
/// <para>★ 本类只管 CRC：算法（CRC32C/CRC64）和位置（Header 末尾 / Footer 末尾）由 flags 决定。
/// 调用方负责算 crcCoverEnd（= headerLen + payloadLen + paddingLen [+ footerMagicLen]）和 crcOffset。</para>
/// </summary>
public static class RecordCodec
{
    /// <summary>
    /// 计算并写入 CRC。
    /// <para>★ 调用前 record 须已填好所有字段（规范+独有+payload+padding+footerMagic），
    /// CRC 字段区域任意值即可（本方法先置零再计算）。</para>
    /// <para>★ CRC 算法（CRC32C/CRC64）和位置（Header 末尾 / Footer 末尾）由 flags 决定。</para>
    /// </summary>
    /// <param name="record">整条记录缓冲区</param>
    /// <param name="flags">记录 flags（决定 CRC 算法 + 字段长度）</param>
    /// <param name="crcCoverEnd">CRC 覆盖范围末尾（exclusive）= headerLen + payloadLen + paddingLen + footerMagicLen</param>
    /// <param name="crcOffset">CRC 字段在 record 中的起始偏移</param>
    public static void FillCrc(Span<byte> record, ushort flags, int crcCoverEnd, int crcOffset)
    {
        var crcLen = RecordFlags.GetCrcLen(flags);
        if (crcLen == 0) return;

        // CRC 字段置零（覆盖范围内含此字段时，置零保证计算正确）
        record.Slice(crcOffset, crcLen).Clear();

        var crcAlgo = flags & RecordFlags.FLAG_CRC_MASK;
        switch (crcAlgo)
        {
            case RecordFlags.FLAG_CRC32C:
            {
                var crc = UnifiedCrc.ComputeCrc32C(record.Slice(0, crcCoverEnd));
                BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(crcOffset, crcLen), crc);
                break;
            }
            case RecordFlags.FLAG_CRC64:
            {
                var crc = UnifiedCrc.ComputeCrc64(record.Slice(0, crcCoverEnd));
                BinaryPrimitives.WriteUInt64LittleEndian(record.Slice(crcOffset, crcLen), crc);
                break;
            }
        }
    }

    /// <summary>
    /// 校验整条记录的 CRC（零拷贝主体：仅 CRC 字段位置喂入等长零字节，无整记录拷贝/无堆分配）。
    /// <para>★ CRC 字段位置在覆盖范围内时，须按 0 参与计算（与 <see cref="FillCrc"/> 清零后算一致）。
    ///   分三段：[0,crcOffset) + crcLen 个 0 字节 + [crcOffset+crcLen, crcCoverEnd)。</para>
    /// <para>★ CRC 在末尾（footer）时 crcOffset==crcCoverEnd，单段算 [0,crcCoverEnd)，CRC 不在范围里。</para>
    /// <para>★ 比"拷贝整条记录到临时缓冲再清零"省去 O(crcCoverEnd) 拷贝 + 堆分配；CRC32C 走硬件指令原位算。</para>
    /// </summary>
    /// <param name="record">整条记录缓冲区（只读，须包含 CRC 字段与全部覆盖范围）。</param>
    /// <param name="flags">记录 flags（决定 CRC 算法 + 字段长度；无 CRC 位时直接返回 true）。</param>
    /// <param name="crcCoverEnd">CRC 覆盖范围末尾（exclusive）= headerLen + payloadLen + paddingLen + footerMagicLen，单位字节。</param>
    /// <param name="crcOffset">CRC 字段在 record 中的起始偏移（单位字节；CRC 在 footer 时等于 crcCoverEnd）。</param>
    /// <returns>true = CRC 匹配；false = CRC 不匹配或记录损坏。</returns>
    public static bool VerifyCrc(ReadOnlySpan<byte> record, ushort flags, int crcCoverEnd, int crcOffset)
    {
        var crcLen = RecordFlags.GetCrcLen(flags);
        if (crcLen == 0) return true;

        var crcAlgo = flags & RecordFlags.FLAG_CRC_MASK;
        var afterCrcStart = crcOffset + crcLen;
        var afterCrcLen = crcCoverEnd - afterCrcStart;
        // CRC 字段是否在覆盖范围内（header 内时 crcOffset < crcCoverEnd；footer 末尾时 crcOffset==crcCoverEnd）。
        // 在范围内时，CRC 字段位置须按 0 参与计算（与 FillCrc 清零后算一致）。
        var crcInCover = crcOffset < crcCoverEnd;
        Span<byte> zeroCrc = stackalloc byte[8];  // crcLen 恒为 4 或 8，足够
        zeroCrc[..crcLen].Clear();

        switch (crcAlgo)
        {
            case RecordFlags.FLAG_CRC32C:
            {
                var stored = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(crcOffset, crcLen));
                // 累加：header 前 [0,crcOffset) + （若 CRC 在范围内）crcLen 个 0 + 剩余 [afterCrcStart, crcCoverEnd)。
                // ★ x64 热路径：段循环内联在本方法内（零跨模块调用/零 span 切片链）——读自愈每次
                //   校验都走这里，调用/建立开销曾占 CRC 校验成本的 ~1/2（112B 实测 29.7ns 中
                //   crc32q 依赖链仅 ~10ns）。寄存器续算语义与 UnifiedCrc 逐位一致
                //   （UnifiedCrcTests 差分验证），非 x64 回退 UnifiedCrc（含 ARM/软件路径）。
                if (Sse42.X64.IsSupported)
                {
                    uint crc = 0xFFFF_FFFF;   // 裸寄存器初值（canonical 的 ~0）
                    crc = Crc32CSegment(crc, record.Slice(0, crcOffset));
                    if (crcInCover)
                        crc = Sse42.Crc32(crc, 0u);   // CRC32C 的 crcLen 恒 4——零字节段 = 单条 crc32d
                    if (afterCrcLen > 0)
                        crc = Crc32CSegment(crc, record.Slice(afterCrcStart, afterCrcLen));
                    return stored == ~crc;
                }

                var computed = UnifiedCrc.ComputeCrc32C(record[..crcOffset]);
                if (crcInCover)
                    computed = UnifiedCrc.ComputeCrc32C(computed, zeroCrc[..crcLen]);
                if (afterCrcLen > 0)
                    computed = UnifiedCrc.ComputeCrc32C(computed, record.Slice(afterCrcStart, afterCrcLen));
                return stored == computed;
            }
            case RecordFlags.FLAG_CRC64:
            {
                var stored = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(crcOffset, crcLen));
                var crc = UnifiedCrc.CreateCrc64();
                crc.Append(record[..crcOffset]);
                if (crcInCover)
                    crc.Append(zeroCrc[..crcLen]);
                if (afterCrcLen > 0)
                    crc.Append(record.Slice(afterCrcStart, afterCrcLen));
                Span<byte> hash = stackalloc byte[UnifiedCrc.Crc64Len];
                crc.GetCurrentHash(hash);
                var computed = BinaryPrimitives.ReadUInt64LittleEndian(hash);
                return stored == computed;
            }
            default:
                return true;
        }
    }

    /// <summary>★ CRC32C 段循环（crc32q/crc32d 硬件指令，寄存器续算）——VerifyCrc 热路径专用内联形态。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Crc32CSegment(uint crc, ReadOnlySpan<byte> data)
    {
        ref byte p = ref MemoryMarshal.GetReference(data);
        nint len = data.Length;
        while (len >= 8)
        {
            crc = (uint)Sse42.X64.Crc32(crc, Unsafe.ReadUnaligned<ulong>(ref p));
            p = ref Unsafe.Add(ref p, 8);
            len -= 8;
        }
        if (len >= 4)
        {
            crc = Sse42.Crc32(crc, Unsafe.ReadUnaligned<uint>(ref p));
            p = ref Unsafe.Add(ref p, 4);
            len -= 4;
        }
        while (len > 0)
        {
            crc = Sse42.Crc32(crc, p);
            p = ref Unsafe.Add(ref p, 1);
            len--;
        }
        return crc;
    }
}
