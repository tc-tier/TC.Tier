using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// 统一 CRC 计算工具——全部类型共用（unified-binary-layout.md §2 FLAG_CRC32C / FLAG_CRC64）。
/// <para><b>CRC32C</b>（Castagnoli 0x1EDC6F41）：硬件加速。x86 走 <c>Sse42.Crc32</c>（每 cycle 8B），
/// ARM 走 <c>Crc32.ComputeCrc32C</c>。用于 DeltaLog / FixedBlock / PageMirror / Meta。</para>
/// <para><b>CRC64</b>（ECMA-92）：软件实现。用于 EntryLog / StreamBlock / IndexMirror 常驻文件抗位衰减。</para>
/// <para>★ 线程局部复用 Crc64 实例，避免热路径 GC 压力。CRC32C 走内建指令无对象分配。</para>
/// </summary>
public static class UnifiedCrc
{
    public const int Crc32CLen = 4;
    public const int Crc64Len  = 8;

    // ══ CRC32C 硬件加速（零分配）══

    /// <summary>
    /// CRC32C 一次性计算（initialCrc=0）——硬件加速。
    /// <para>★ x86-64: Sse42.X64.Crc32 每 cycle 处理 8B（~1GB/s）。</para>
    /// <para>★ ARM: Crc32.ComputeCrc32C 每 cycle 处理 4B。</para>
    /// <para>★ 其他平台: 软件表（Castagnoli 反射多项式 0x82F63B78）。</para>
    /// </summary>
    public static uint ComputeCrc32C(ReadOnlySpan<byte> data) => ComputeCrc32C(0, data);

    /// <summary>
    /// CRC32C 增量计算——支持分段累加（VerifyCrc 跳过 CRC 字段时分两段算）。
    /// <para>★ initialCrc = 上一段算出的 crc，本段结果 = CRC32C(initialCrc, data)。</para>
    /// <para>★ 零拷贝：直接对传入 span 算，硬件指令原位执行。</para>
    /// </summary>
    public static uint ComputeCrc32C(uint initialCrc, ReadOnlySpan<byte> data)
    {
        uint crc = initialCrc;

        if (Sse42.X64.IsSupported)
        {
            // 8B 粒度（最快）
            while (data.Length >= 8)
            {
                crc = (uint)Sse42.X64.Crc32(crc, MemoryMarshal.Read<ulong>(data));
                data = data[8..];
            }
            // 4B 残余
            if (data.Length >= 4)
            {
                crc = Sse42.Crc32(crc, MemoryMarshal.Read<uint>(data));
                data = data[4..];
            }
            // 1B 残余
            while (data.Length > 0)
            {
                crc = Sse42.Crc32(crc, data[0]);
                data = data[1..];
            }
            return crc;
        }

        if (Sse42.IsSupported) // 32-bit x86
        {
            while (data.Length >= 4)
            {
                crc = Sse42.Crc32(crc, MemoryMarshal.Read<uint>(data));
                data = data[4..];
            }
            while (data.Length > 0)
            {
                crc = Sse42.Crc32(crc, data[0]);
                data = data.Slice(1);
            }
            return crc;
        }

        if (!System.Runtime.Intrinsics.Arm.Crc32.IsSupported) return ComputeCrc32C_Software(crc, data); // ARM
        while (data.Length >= 4)
        {
            crc = System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32C(crc, MemoryMarshal.Read<uint>(data));
            data = data[4..];
        }
        while (data.Length > 0)
        {
            crc = System.Runtime.Intrinsics.Arm.Crc32.ComputeCrc32C(crc, data[0]);
            data = data[1..];
        }
        return crc;
    }

    // ══ CRC64（软件）══

    [ThreadStatic]
    private static Crc64? _tCrc64;

    /// <summary>
    /// CRC64（ECMA-92）一次性计算。线程局部复用实例。
    /// </summary>
    public static ulong ComputeCrc64(ReadOnlySpan<byte> data)
    {
        var crc = _tCrc64 ??= new Crc64();
        crc.Reset();
        crc.Append(data);
        Span<byte> hash = stackalloc byte[Crc64Len];
        crc.GetCurrentHash(hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    // ══ 增量 CRC（多 chunk 场景：IndexMirror / StreamBlock）══

    /// <summary>创建 CRC64 增量计算器（跨多 chunk 追加后 <see cref="FinalizeCrc64"/>）。</summary>
    public static Crc64 CreateCrc64() => new();

    /// <summary>
    /// 终结增量 CRC64 计算并返回 ulong 值（取出 GetCurrentHash 的 8B 并按 LE 解释）。
    /// <para>★ 多 chunk 累积后调此方法拿最终 CRC64，避免调用方手写 <c>BinaryPrimitives.ReadUInt64LittleEndian</c>。</para>
    /// </summary>
    public static ulong FinalizeCrc64(Crc64 crc)
    {
        Span<byte> hash = stackalloc byte[Crc64Len];
        crc.GetCurrentHash(hash);
        return BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    // ══ 软件回退 CRC32C ══

    private static readonly uint[] SCrc32CTable = BuildCrc32CTable();

    private static uint[] BuildCrc32CTable()
    {
        const uint poly = 0x82F63B78; // Castagnoli 反射多项式
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
                crc = (crc >> 1) ^ (poly & (uint)-(int)(crc & 1));
            table[i] = crc;
        }
        return table;
    }

    private static uint ComputeCrc32C_Software(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            crc = (crc >> 8) ^ SCrc32CTable[(crc ^ b) & 0xFF];
        return crc;
    }
}
