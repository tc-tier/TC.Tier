using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.IO.TierVolume;

// ═══════════════════════════════════════════════════════════════════
// TierVolume 盘上帧头布局（[BinaryLayout] 生成 codec——单一真源：偏移/大小/字节序编译期校验，
// TCSG001 交叉验证 [StructLayout].Size vs 字段偏移和）。
//
// ★ Features = All（FieldAccessors | Constants）：每字段生成单值 Read_*/Write_* 与
//   Offset_*/Size_* 常量——域代码零魔数（uuid 裸区范围与 CRC 覆盖区一律自生成常量推导）。
// ★ 盘上格式锁定：帧字段迁移前后字节逐位一致（uuid 区/CRC 覆盖区为域代码裸操作——codec 不触碰无字段区）。
// ★ CRC 统一（2026-09-01）：全部帧/块校验用 UnifiedCrc.ComputeCrc32C（仓库统一工具，SSE4.2 硬件加速）
//   ——取代旧 IEEE CRC-32（System.IO.Hashing）——格式字节相对旧构建的卷有算法级变化（自洽，旧卷互不认 CRC）。
// ★ Magic 均以 u32 小端字段承载（值 = 4 字节 ASCII 逆序，如 "RJRN" = 0x4E524A52）。
// ═══════════════════════════════════════════════════════════════════

/// <summary>日志帧头（32B："RJRN" magic | type(1B) | pad/rsvd(3B 恒零) | lsn@8 | gen@16 | bodyLen@24 | bodyCrc@28）。
/// pad/rsvd 区无字段——调用方以零化缓冲承载（盘上恒零，与旧实现逐字节一致）。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct JournalFrameHeader
{
    /// <summary>"RJRN" 小端。</summary>
    internal const uint MagicValue = 0x4E524A52;

    [FieldOffset(0)] public uint Magic;
    [FieldOffset(4)] public byte Type;          // JournalRecordType（域代码 cast）
    [FieldOffset(8)] public ulong Lsn;
    [FieldOffset(16)] public ulong Generation;
    [FieldOffset(24)] public uint BodyLength;
    [FieldOffset(28)] public uint BodyCrc;
}

/// <summary>delta 流文件头（44B："TCD1" | ver@4 | flags@6 | blockSize@8 | uuid@12..28（裸区）| baseLsn@28 |
/// baseCrc@36 | headerCrc@40 覆盖 [0..Offset_HeaderCrc)）。uuid 区由域代码裸拷——范围自相邻字段常量推导。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 44)]
internal struct DeltaFileHeader
{
    /// <summary>"TCD1" 小端。</summary>
    internal const uint MagicValue = 0x31444354;
    internal const ushort FormatVersion = 1;

    /// <summary>uuid 字节长（Guid 线形——盘上固定 16B；域语义常量）。</summary>
    internal const int UuidBytes = 16;

    /// <summary>uuid 裸区起点 = [blockSize 字段末, baseLsn 字段首)——生成常量推导，零手写偏移。</summary>
    internal static int UuidOffset => DeltaFileHeaderCodec.Offset_BaseLsn - UuidBytes;

    [FieldOffset(0)] public uint Magic;
    [FieldOffset(4)] public ushort Version;
    [FieldOffset(6)] public ushort Flags;       // 保留——未知拒读（域代码校验）
    [FieldOffset(8)] public uint BlockSize;
    [FieldOffset(28)] public ulong BaseLsn;
    [FieldOffset(36)] public uint BaseCrc;
    [FieldOffset(40)] public uint HeaderCrc;    // Crc32([0..Offset_HeaderCrc))——域代码计算后单值写
}

/// <summary>delta 数据段头（12B："TCD3" | count@4）。流 CRC 覆盖整段字节（域代码 append）。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 12)]
internal struct DeltaSectionHeader
{
    /// <summary>"TCD3" 小端。</summary>
    internal const uint MagicValue = 0x33444354;

    [FieldOffset(0)] public uint Magic;
    [FieldOffset(4)] public ulong Count;
}

/// <summary>delta 数据块条目（12B：块号@0 + 块 CRC@8）。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 12)]
internal struct DeltaEntryHeader
{
    [FieldOffset(0)] public ulong Block;
    [FieldOffset(8)] public uint BlockCrc;
}

/// <summary>delta 流尾（16B："TCD2" | count@4 | payloadCrc@12）。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct DeltaFooterHeader
{
    /// <summary>"TCD2" 小端。</summary>
    internal const uint MagicValue = 0x32444354;

    [FieldOffset(0)] public uint Magic;
    [FieldOffset(4)] public ulong Count;
    [FieldOffset(12)] public uint PayloadCrc;
}

/// <summary>成员载体身份头（512B："RAWC" | ver@4 | uuid@8..24（裸区）| carrierIndex@24 | bitmapStart@28 |
/// bitmapBlocks@36 | capacity@44 | crc@508 覆盖 [0..Offset_Crc)）。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 512)]
internal struct CarrierMemberHeader
{
    /// <summary>"RAWC" 小端。</summary>
    internal const uint MagicValue = 0x43574152;

    /// <summary>uuid 字节长（Guid 线形——盘上固定 16B；域语义常量）。</summary>
    internal const int UuidBytes = 16;

    /// <summary>uuid 裸区起点 = [carrierIndex 字段首 − uuid 长)——生成常量推导，零手写偏移。</summary>
    internal static int UuidOffset => CarrierMemberHeaderCodec.Offset_CarrierIndex - UuidBytes;

    [FieldOffset(0)] public uint Magic;
    [FieldOffset(4)] public ushort Version;
    [FieldOffset(24)] public uint CarrierIndex;
    [FieldOffset(28)] public ulong BitmapStartLocal;
    [FieldOffset(36)] public ulong BitmapBlocksLocal;
    [FieldOffset(44)] public ulong Capacity;
    [FieldOffset(508)] public uint Crc;         // Crc32([0..Offset_Crc))——域代码计算后单值写
}


// ═══════════════════════════════════════════════════════════════════
// Superblock 4096B 布局（半生成化——标量组 + 条目 codec）：
//   可生成面 = 连续标量区（0..40 与 136..254，中间夹 runs 表 40..136）+ 尾部 Crc@4088；
//   域语义留手写 = uuid/label 裸区、定长容量表区循环（count 驱动）、条件零写、CRC 覆盖计算。
//   表区几何 = 固定容量槽（runs 8×12 / members 8×40 / snapshots 16×180），与 v1 冻结格式逐字节一致。
// ═══════════════════════════════════════════════════════════════════

/// <summary>superblock 标量面（显式布局——字段散布于 0..254 与 4088，中间为表区/uuid/label 间隙，
/// codec 只写声明的 FieldOffset 区——间隙由域代码/零化缓冲承载）。Size = 末字段 extent（4092），
/// Crc 覆盖 [0..Offset_Crc)。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 4092)]
internal struct SuperblockMeta
{
    /// <summary>"RAW1" 小端。</summary>
    internal const uint MagicValue = 0x31574152;

    [FieldOffset(0)] public uint Magic;
    [FieldOffset(4)] public ushort Version;
    [FieldOffset(6)] public ushort Flags;
    [FieldOffset(8)] public uint BlockSize;
    [FieldOffset(12)] public ulong CapacityBlocks;
    [FieldOffset(20)] public ulong BitmapStart;
    [FieldOffset(28)] public ulong BitmapBlocks;
    [FieldOffset(36)] public uint ImageRunCount;
    [FieldOffset(136)] public ulong ImageLength;
    [FieldOffset(144)] public uint ImageCrc;
    [FieldOffset(148)] public ulong Generation;
    [FieldOffset(172)] public ushort CarrierIndex;
    [FieldOffset(174)] public uint MemberCount;
    [FieldOffset(178)] public ulong JournalStart;
    [FieldOffset(186)] public ulong JournalBlocks;
    [FieldOffset(194)] public ulong JournalGeneration;
    [FieldOffset(202)] public uint JournalState;
    [FieldOffset(238)] public ulong JournalCkptLsn;
    [FieldOffset(246)] public ulong JournalHeadLsn;
    [FieldOffset(4088)] public uint Crc;         // Crc32C([0..Offset_Crc))——域代码计算后单值写
}

/// <summary>镜像区间条目（12B：块起点 u64 + 块数 u32——runs 表槽内循环条目）。
/// 域代码调用 <c>Write/Read</c> 全字段方法 + <c>StructSize</c> 常量，故取 All（同 v1 全家福）。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 12)]
internal struct SuperblockImageRun
{
    [FieldOffset(0)] public ulong Start;
    [FieldOffset(8)] public uint Count;
}

/// <summary>成员表条目（40B：uuid@0..16 裸区 + capacity/bitmap 标量）。
/// 域代码调用 <c>Write/Read</c> 全字段方法 + <c>StructSize</c> 常量，故取 All。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 40)]
internal struct SuperblockMember
{
    /// <summary>uuid 字节长（Guid 线形——域语义常量）。</summary>
    internal const int UuidBytes = 16;

    /// <summary>uuid 裸区起点（0——条目首部）。</summary>
    internal const int UuidOffset = 0;

    [FieldOffset(16)] public ulong CapacityBlocks;
    [FieldOffset(24)] public ulong BitmapStartLocal;
    [FieldOffset(32)] public ulong BitmapBlocksLocal;
}

/// <summary>快照表条目（180B：name@0..32 裸区 | ticks@32 | lsn@40 | runsCount@48 | runs@52..148（≤8×12）|
/// imageLength@148 | imageCrc@156 | bitmapStart@160 | bitmapBlocks@168 | inUse@176）。
/// runs 间隙区由域代码按 runsCount 逐条 SuperblockImageRun 写入。
/// 域代码调用 <c>Write/Read</c> 全字段方法 + <c>StructSize</c> 常量，故取 All。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 178)]
internal struct SuperblockSnapshot
{
    internal const int NameBytes = 32;
    internal const int RunGapOffset = 52;       // runs 间隙起点（= runs 表满 8 槽 96B 前的偏移）
    internal const int MaxRuns = 8;

    [FieldOffset(32)] public long CaptureTicks;
    [FieldOffset(40)] public ulong CaptureLsn;
    [FieldOffset(48)] public uint RunsCount;
    [FieldOffset(148)] public ulong ImageLength;
    [FieldOffset(156)] public uint ImageCrc;
    [FieldOffset(160)] public ulong BitmapStart;
    [FieldOffset(168)] public ulong BitmapBlocks;
    [FieldOffset(176)] public ushort InUse;     // flags bit0 = 在册
}
