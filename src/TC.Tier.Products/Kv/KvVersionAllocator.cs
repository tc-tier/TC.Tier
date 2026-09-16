using System.Runtime.InteropServices;
using TC.Tier.CodeGen;
namespace TC.Tier.Products.Kv;

/// <summary>
/// 版本分配器（tierkv-design.md §3.2，D1=session-version）：全局单调分配——会话创建自分配器取版本
/// （会话区间 = 本版本至下一分配之间的代际标签）；分配器高水位经 Ring opaque meta 持久化
/// （随 2PC Prepare 原子落盘），恢复期续接（新分配版本恒大于历史——版本单调不回退）。
/// <para>★ 检查点粒度版本（版本图=分配器高水位+环水位+索引几何，W5 入帧）——记录级版本标签
/// 不存在（append-only Ring 记录无版本字段；快照/版本读按检查点版本定位水位重放，F3/W5）。</para>
/// <para>★ opaque 布局（24B）：[0..8) 高水位 little-endian + [8..12) 魔数 "TKV1"——代际守卫，
/// 代际演进换魔数值 + [12..16) 过期回收扫描水位 SegId + [16..24) 扫描水位 Offset
/// （A.2a——ExpirySweep 增量游标，恢复从水印续扫不重复全扫；SegId&lt;0 = 未持久化态）。
/// 编码经 [BinaryLayout] 生成 Codec（<c>KvVersionOpaqueCodec</c>）——零手写字节序；
/// 高水位读取兼容旧 12B 块（短块零扩展解读——魔数在 [8..12) 不变）。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 24)]
public struct KvVersionOpaque
{
    /// <summary>opaque 魔数（"TKV1" little-endian——布局代际守卫，代际演进换值）。</summary>
    public const uint MagicValue = 0x544B5631;

    /// <summary>高水位区占用字节数（8B 高水位 + 4B 魔数——旧 12B 块的最小兼容解读长度）。</summary>
    public const int HighWaterSize = 12;

    /// <summary>已分配最高版本。</summary>
    [FieldOffset(0)] public long HighWater;

    /// <summary>代际守卫魔数。</summary>
    [FieldOffset(8)] public uint Magic;

    /// <summary>过期回收扫描水位 SegId（&lt;0 = 未持久化态）。</summary>
    [FieldOffset(12)] public int SweepSegId;

    /// <summary>过期回收扫描水位 Offset。</summary>
    [FieldOffset(16)] public long SweepOffset;
}

/// <summary>版本号段分配器（opaque 布局 + raft 复制面——批量取段降低 raft 频次）。</summary>
public sealed class KvVersionAllocator
{
    /// <summary>opaque 魔数（"TKV1" little-endian——布局代际守卫，代际演进换值）。</summary>
    public const int OpaqueMagic = 0x544B5631;

    /// <summary>高水位区占用字节数（8B 高水位 + 4B 魔数——旧 12B 块的最小兼容解读长度）。</summary>
    public const int OpaqueHighWaterSize = 12;

    /// <summary>opaque 占用字节数（8B 高水位 + 4B 魔数 + 12B 扫描水位；Ring 缺省 opaque 容量 256B 绰绰）。</summary>
    public const int OpaqueSize = 24;

    /// <summary>已分配最高版本（0=未分配——首版从 1 起）。</summary>
    private long _highWater;

    /// <summary>当前高水位（已分配最高版本）。</summary>
    public long HighWater => Volatile.Read(ref _highWater);

    /// <summary>分配下一版本（全局单调——并发安全）。</summary>
    /// <returns>新分配的版本号（全局单调递增）。</returns>
    public long NextVersion() => Interlocked.Increment(ref _highWater);

    /// <summary>恢复高水位（仅恢复期——续接历史，单调不回退；向下值被忽略守卫）。</summary>
    /// <param name="highWater">恢复目标高水位（小于当前值被忽略——单调不回退守卫）。</param>
    public void Restore(long highWater)
    {
        if (highWater > Volatile.Read(ref _highWater))
            Volatile.Write(ref _highWater, highWater);
    }

    /// <summary>编码 opaque（写入 destination 前 <see cref="OpaqueSize"/> 字节——高水位+扫描水位随
    /// 2PC Prepare 同块原子落盘）。</summary>
    /// <param name="destination">opaque 写入目标缓冲（长度不小于 <see cref="OpaqueSize"/>）。</param>
    /// <param name="highWater">待持久化的高水位。</param>
    /// <param name="sweepWatermark">待持久化的过期回收扫描水位（Invalid = 未持久化态，落盘为 SegId=-1）。</param>
    /// <exception cref="ArgumentException">destination 长度小于 <see cref="OpaqueSize"/>。</exception>
    public static void WriteOpaque(Span<byte> destination, long highWater, LogicalAddress sweepWatermark)
    {
        if (destination.Length < OpaqueSize)
            throw new ArgumentException($"opaque 容量 {destination.Length} < 需求 {OpaqueSize}", nameof(destination));
        KvVersionOpaqueCodec.Write(destination, new KvVersionOpaque
        {
            HighWater = highWater,
            Magic = KvVersionOpaque.MagicValue,
            SweepSegId = sweepWatermark.IsValid ? sweepWatermark.SegId : -1,
            SweepOffset = sweepWatermark.Offset,
        });
    }

    /// <summary>解码 opaque 高水位（魔数不匹配/长度不足 12B → null——调用方回落未持久化态；
    /// 旧 12B 块兼容——扫描水位区缺失按未持久化解读）。</summary>
    /// <param name="opaque">Ring opaque 区字节序列。</param>
    /// <returns>高水位（魔数匹配且长度足够时）；否则 null。</returns>
    public static long? ReadOpaqueHighWater(ReadOnlySpan<byte> opaque)
    {
        if (opaque.Length < OpaqueHighWaterSize)
            return null;
        if (KvVersionOpaqueCodec.Read_Magic(opaque) != KvVersionOpaque.MagicValue)
            return null;
        return KvVersionOpaqueCodec.Read_HighWater(opaque);
    }

    /// <summary>解码过期回收扫描水位（长度不足/魔数不匹配/SegId&lt;0 → null = 未持久化态——
    /// 调用方回落 Invalid，首轮 sweep 自 BeginAddress 全扫）。</summary>
    /// <param name="opaque">Ring opaque 区字节序列。</param>
    /// <returns>扫描水位（已持久化时）；否则 null。</returns>
    public static LogicalAddress? ReadOpaqueSweepWatermark(ReadOnlySpan<byte> opaque)
    {
        if (opaque.Length < OpaqueSize)
            return null;
        if (KvVersionOpaqueCodec.Read_Magic(opaque) != KvVersionOpaque.MagicValue)
            return null;
        var segId = KvVersionOpaqueCodec.Read_SweepSegId(opaque);
        if (segId < 0)
            return null;
        return new LogicalAddress(segId, KvVersionOpaqueCodec.Read_SweepOffset(opaque));
    }
}
