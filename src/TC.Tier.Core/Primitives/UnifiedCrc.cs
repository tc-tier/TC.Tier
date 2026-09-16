using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// 统一 CRC 计算工具——仓库持久/传输格式校验和的唯一出口（unified-binary-layout.md §2 的
/// FLAG_CRC32 / FLAG_CRC32C / FLAG_CRC64 三算法均由本族（UnifiedCrc + UnifiedCrc64）自研覆盖，
/// 产品代码零 System.IO.Hashing 依赖；结构侧按格式规范选算法）。
/// <para><b>CRC32C</b>（Castagnoli 反射多项式 0x82F63B78）：硬件加速。x86 走 <c>Sse42.Crc32</c>（每 cycle 8B），
/// ARM 走 <c>Crc32.ComputeCrc32C</c>（每 cycle 4B）；无硬件平台回退自建软件表。格式族（示例）：
/// TierVolume 帧/Delta/Journal/Carrier/Superblock、RecordCodec（FLAG_CRC32C）、Ring overflow、Meta、Wire/Swarm、WAL 快照。</para>
/// <para><b>CRC32</b>（IEEE 802.3，反射多项式 0xEDB88320，zlib 语义）：软件查表零分配，与
/// System.IO.Hashing.Crc32 逐位一致（迁移调用点不改动已落盘/在途校验值）。用于格式规范指定 IEEE 的场合：
/// TCA1/TCE1 image 帧与流尾聚合、TransactionLog 提交记录、Compact 标记、NetworkImageTransfer 回执。</para>
/// <para><b>CRC64</b>（ECMA-182 多项式；实例型增量经 <see cref="UnifiedCrc64"/>，见 <see cref="CreateCrc64"/>）：
/// 常驻归档抗位衰减（RecordCodec FLAG_CRC64、Snapshot、SortedIndex/ProbingIndex、Mirror FLAG_CRC64 帧等）。</para>
/// <para>★ 一次性路径零分配零 TLS（CRC64 静态直算；CRC32C 内建指令、CRC32 查表）——均无对象分配。</para>
/// </summary>
public static class UnifiedCrc
{
    /// <summary>CRC32C 校验值字节数（4 字节）。</summary>
    public const int Crc32CLen = 4;
    /// <summary>CRC64 校验值字节数（8 字节）。</summary>
    public const int Crc64Len  = 8;

    // ══ CRC32C 硬件加速（零分配）══

    /// <summary>
    /// CRC32C 一次性计算（initialCrc=0）——硬件加速。
    /// <para>★ x86-64: Sse42.X64.Crc32 每 cycle 处理 8B（~1GB/s）。</para>
    /// <para>★ ARM: Crc32.ComputeCrc32C 每 cycle 处理 4B。</para>
    /// <para>★ 其他平台: 软件表（Castagnoli 反射多项式 0x82F63B78）。</para>
    /// <para>★ 标准语义：CRC-32C（Castagnoli，init/xorout = 0xFFFFFFFF，iSCSI 系）——
    /// 校验向量 "123456789" → 0xE3069283（2026-09-02 修正：原实现为 0 初值无补码变体，非标准）。</para>
    /// </summary>
    /// <param name="data">待校验数据（可空 span——空数据返回标准空输入 CRC 值）。</param>
    /// <returns>32 位 CRC32C 校验值（canonical 语义，init/xorout = 0xFFFFFFFF）。</returns>
    public static uint ComputeCrc32C(ReadOnlySpan<byte> data) => ComputeCrc32C(0, data);

    /// <summary>
    /// CRC32C 增量计算——支持分段累加（VerifyCrc 跳过 CRC 字段时分两段算）。
    /// <para>★ initialCrc = 上一段算出的 crc（标准 canonical 续算语义：整段一次性 == 各段累加）。</para>
    /// <para>★ 零拷贝：直接对传入 span 算，硬件指令原位执行。</para>
    /// </summary>
    /// <param name="initialCrc">上一段返回的 CRC 值（首次/单独一段传 0）。</param>
    /// <param name="data">本段待校验数据。</param>
    /// <returns>32 位 CRC32C 校验值（canonical 续算语义：整段一次性 == 各段累加）。</returns>
    public static uint ComputeCrc32C(uint initialCrc, ReadOnlySpan<byte> data)
    {
        // 标准 CRC-32C 的 canonical 值在段边界做 ~ 往返（同 IEEE 的 zlib 约定）——
        // SSE/ARM 指令寄存器按"裸余数"续算，故补码必须在方法边界完成。
        uint crc = ~initialCrc;
        crc = ComputeCrc32CRegister(crc, data);
        return ~crc;
    }

    private static uint ComputeCrc32CRegister(uint crc, ReadOnlySpan<byte> data)
    {
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

    // ══ CRC32（IEEE 802.3——zlib 语义；核心 = 官方同源 Crc32IeeeCore：PCLMULQDQ/ARM 向量折叠 + 查表回退）══

    /// <summary>
    /// CRC-32（IEEE 802.3，反射多项式 0xEDB88320）一次性计算。
    /// <para>★ canonical（zlib）语义——与 System.IO.Hashing.Crc32.HashToUInt32 逐位一致
    /// （迁移调用点不改动已落盘/在途校验值）；≥16B 走官方同源向量路径。</para>
    /// </summary>
    /// <param name="data">待校验数据（可空 span）。</param>
    /// <returns>32 位 CRC-32（IEEE 802.3）校验值（zlib canonical 语义）。</returns>
    public static uint ComputeCrc32(ReadOnlySpan<byte> data) => ComputeCrc32(0, data);

    /// <summary>
    /// CRC-32（IEEE 802.3）增量计算——支持分段累加（同 ComputeCrc32C 的增量重载形态）。
    /// <para>★ initialCrc = 上一段返回的 CRC（zlib 续算语义：整段一次性 == 各段累加）。
    /// canonical 段边界做 ~ 转换，寄存器核心（官方语义，初值 0xFFFFFFFF）原样续算。</para>
    /// </summary>
    /// <param name="initialCrc">上一段返回的 CRC 值（首次/单独一段传 0）。</param>
    /// <param name="data">本段待校验数据。</param>
    /// <returns>32 位 CRC-32（IEEE 802.3）校验值（zlib 续算语义：整段一次性 == 各段累加）。</returns>
    public static uint ComputeCrc32(uint initialCrc, ReadOnlySpan<byte> data)
    {
        uint register = ~initialCrc;   // canonical → 官方寄存器（canonical 0 → 0xFFFFFFFF）
        register = Crc32IeeeCore.Update(register, data);
        return ~register;
    }

    // ══ CRC64（软件查表 + PCLMULQDQ/ARM AES 向量折叠——见 UnifiedCrc64）══

    /// <summary>
    /// CRC64（ECMA-182）一次性计算。静态直算（内部 UnifiedCrc64.ComputeOneShot）。
    /// <para>★ 数值约定：官方 _crc 大端字节按 LE 解释（ReverseEndianness）——历史口径，
    /// 落盘字节 = 官方 Hash() 输出，勿改。</para>
    /// </summary>
    /// <param name="data">待校验数据（可空 span）。</param>
    /// <returns>64 位 CRC64（ECMA-182）校验值（按 LE 字节序解释的 ulong）。</returns>
    public static ulong ComputeCrc64(ReadOnlySpan<byte> data) =>
        BinaryPrimitives.ReverseEndianness(UnifiedCrc64.ComputeOneShot(data));

    // ══ 增量 CRC（多 chunk 场景：IndexMirror / StreamBlock）══

    /// <summary>创建 CRC64 增量计算器（跨多 chunk 追加后 <see cref="FinalizeCrc64"/>）。</summary>
    /// <returns>初始态（_crc = 初值）的 CRC64 增量计算器实例。</returns>
    public static UnifiedCrc64 CreateCrc64() => new();

    /// <summary>
    /// 终结增量 CRC64 计算并返回 ulong 值（取出 GetCurrentHash 的 8B 并按 LE 解释）。
    /// <para>★ 多 chunk 累积后调此方法拿最终 CRC64，避免调用方手写 <c>BinaryPrimitives.ReadUInt64LittleEndian</c>。</para>
    /// </summary>
    /// <param name="crc">经多次 <see cref="UnifiedCrc64.Append"/> 累积的增量计算器。</param>
    /// <returns>64 位 CRC64 校验值（当前累积内容的最终值，按 LE 字节序解释的 ulong）。</returns>
    public static ulong FinalizeCrc64(UnifiedCrc64 crc)
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
