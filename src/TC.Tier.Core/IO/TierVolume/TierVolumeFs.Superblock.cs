using System.Buffers.Binary;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Core.IO.TierVolume;

/// <summary>
/// TierVolume 根空间——第四介质（raw-medium-and-conversion-design 全篇）。
/// 自维护布局的连续后端根空间：既是活卷又是存档，本地持久化推荐位（§1.4）。
/// </summary>
public sealed partial class TierVolumeFs
{
    /// <summary>superblock 主份字节偏移（固定 0——不依赖块大小，读侧无鸡生蛋）。</summary>
    private const long SuperblockPrimaryOffset = 0;

    /// <summary>superblock 备份字节偏移（固定 4096）。</summary>
    private const long SuperblockBackupOffset = 4096;

    /// <summary>头部区字节（两份 superblock 槽——位图区在块对齐后开始）。</summary>
    private const long HeaderBytes = 8192;

    /// <summary>superblock 内偏移（字段表——v1 布局定稿，§3.9 演进只加不改）。</summary>
    private static class Sb
    {
        public const int ImageRuns = 40;         // 8 × (u64 start + u32 count) = 96B
        public const int Uuid = 156;             // 16B
        public const int Label = 206;            // 32B UTF8 零填充
        public const int MemberTable = 256;      // 成员表（RM-04 §3.8——8 × 40B：UUID16 + cap8 + bitmapStart8 + bitmapBlocks8）
        public const int MemberTableMax = 8;     // v1 上限（加载体 = 纯加法；超 8 载体留布局版本）
        public const int SnapshotTable = 576;    // 快照表（V2 §1.1——16 × 180B：名字/时刻/捕获 LSN + 镜像 runs/CRC + 冻结位图引用）
        public const int SnapshotEntrySize = 180;
        public const int SnapshotMax = 16;       // 上限（初始参数——内存预算反推，判定门 2 实测裁决）
        public const int SnapshotTableEnd = SnapshotTable + SnapshotEntrySize * SnapshotMax;   // = 3456
        public const int LabelBytes = 32;        // Label UTF8 零填充域宽
        public const int TotalSize = 4096;
    }

    private const ushort TierVolumeLayoutVersion = 1;
    private const ushort FlagClean = 0x0001;
    private const ushort FlagJournaled = 0x0002;   // raw-journal-design §3.1——日志启用位
    private const ushort FlagMultiCarrier = 0x0004;   // RM-04 §3.8——多载体卷（老二进制拒开）
    private const ushort FlagAutoExpand = 0x0008;   // medium-protocol §5.3——自动扩容卷（quota=-1 New；老二进制拒开）
    private const ushort FlagSnapshots = 0x0010;   // V2 §1.1——快照表非空（老二进制经未知 flags 拒开——fail-closed）
    private const ushort FlagsKnownMask = 0x001F;   // 其余位未知 → 拒开（§3.9 未知保留值拒开）

    /// <summary>成员表条目（§3.8——Day-1 字段激活；成员 0 = 主载体）。</summary>
    internal sealed record MemberEntry(Guid Uuid, ulong CapacityBlocks, ulong BitmapStartLocal, ulong BitmapBlocksLocal);

    /// <summary>快照表条目（V2 §1.1——superblock 内联，180B/条；捕获时由检查点原子绑定）。
    /// <para>★ 不可变盘上字段（捕获时填充、删除时移除）；<see cref="FrozenWords"/> = 运行态冻结位图
    /// （捕获时自活位图克隆 / 打开时自冻结区载入——释放过滤的判据源）。</para>
    /// <para>★ 快照 = 第 N 代元数据镜像（CoW 天然保留）+ 冻结分配位图——写路径钉块、读路径可挂载。</para></summary>
    internal sealed class SnapshotEntry
    {
        public string Name = "";
        public long CaptureTicks;                       // DateTimeOffset.UtcTicks
        public ulong CaptureLsn;                        // 捕获检查点的 CkptLsn（增量导出基点）
        public List<(ulong Start, uint Count)> ImageRuns = [];   // 快照元数据镜像（捕获检查点的新镜像——CoW 不释放）
        public ulong ImageLength;
        public uint ImageCrc;
        public ulong BitmapStart;                       // 冻结位图区（全局块号——连续 run）
        public ulong BitmapBlocks;
        public ulong[]? FrozenWords;                    // 运行态：捕获时刻位图（字形态；null = 未载入）
    }

    /// <summary>superblock 内存形态（不可变快照——提交时序列化轮写）。</summary>
    private sealed class SuperblockData
    {
        public ushort Flags;
        public uint BlockSize;
        public ulong CapacityBlocks;
        public ulong BitmapStart;
        public ulong BitmapBlocks;
        public List<(ulong Start, uint Count)> ImageRuns = [];
        public ulong ImageLength;
        public uint ImageCrc;
        public ulong Generation;
        public Guid Uuid;
        public string Label = "";
        // 日志（raw-journal-design §3.1——Journaled 置位时有效）
        public ulong JournalStart;
        public ulong JournalBlocks;
        public ulong JournalGeneration;
        public uint JournalState;
        public ulong JournalCkptLsn;
        public ulong JournalHeadLsn;
        // 成员表（RM-04 §3.8——MultiCarrier 置位时 >1 条）
        public List<MemberEntry> Members = [new(Guid.Empty, 0, 0, 0)];
        // 快照表（V2 §1.1——Snapshots 置位时非空；捕获序）
        public List<SnapshotEntry> Snapshots = [];
    }

    private static void EncodeSuperblock(Span<byte> buffer, SuperblockData sb)
    {
        buffer.Clear();
        // 快照旗标与表空态对称维护（V2 §1.1——Snapshots 置位 ⇔ 表非空；老二进制经未知 flags 拒开）
        sb.Flags = (ushort)(sb.Flags & ~FlagSnapshots);
        if (sb.Snapshots.Count > 0) sb.Flags |= FlagSnapshots;
        var journaled = (sb.Flags & FlagJournaled) != 0;
        var multi = (sb.Flags & FlagMultiCarrier) != 0 && sb.Members.Count > 1;
        var memberCount = multi ? sb.Members.Count : 1;
        var member0 = multi ? sb.Members[0]
            : new MemberEntry(sb.Uuid, sb.CapacityBlocks, sb.BitmapStart, sb.BitmapBlocks);

        // ★ 域裸区先行（uuid/label/表区——codec 无字段间隙；CRC 计算须在全部字段就位后）
        sb.Uuid.TryWriteBytes(buffer[Sb.Uuid..]);
        var p = Sb.ImageRuns;
        foreach (var (start, count) in sb.ImageRuns)
        {
            var run = new SuperblockImageRun { Start = start, Count = count };
            SuperblockImageRunCodec.Write(buffer[p..], in run);
            p += SuperblockImageRunCodec.StructSize;
        }
        for (var i = 0; i < memberCount; i++)
        {
            var mp = Sb.MemberTable + i * SuperblockMemberCodec.StructSize;
            var m = i == 0 ? member0 : sb.Members[i];
            m.Uuid.TryWriteBytes(buffer.Slice(mp, SuperblockMember.UuidBytes));   // uuid 裸区（条目首部）
            var mem = new SuperblockMember
            {
                CapacityBlocks = m.CapacityBlocks,
                BitmapStartLocal = m.BitmapStartLocal,
                BitmapBlocksLocal = m.BitmapBlocksLocal,
            };
            SuperblockMemberCodec.Write(buffer.Slice(mp, SuperblockMemberCodec.StructSize), in mem);
        }
        var labelBytes = System.Text.Encoding.UTF8.GetBytes(sb.Label);
        labelBytes.AsSpan(0, Math.Min(labelBytes.Length, SuperblockSnapshot.NameBytes)).CopyTo(buffer[Sb.Label..]);
        EncodeSnapshotTable(buffer, sb);

        // ★ 标量面（G1 0..40 + G2 136..254 + Crc@4088——codec 单结构，间隙由域代码承载）：
        //   两段式 = 全字段写（Crc=0）→ 算 [0..Offset_Crc) → 生成单值 Write_Crc 覆写
        var meta = new SuperblockMeta
        {
            Magic = SuperblockMeta.MagicValue,
            Version = TierVolumeLayoutVersion,
            Flags = sb.Flags,
            BlockSize = sb.BlockSize,
            CapacityBlocks = sb.CapacityBlocks,
            BitmapStart = sb.BitmapStart,
            BitmapBlocks = sb.BitmapBlocks,
            ImageRunCount = (uint)sb.ImageRuns.Count,
            ImageLength = sb.ImageLength,
            ImageCrc = sb.ImageCrc,
            Generation = sb.Generation,
            MemberCount = (uint)memberCount,
            JournalStart = journaled ? sb.JournalStart : 0,
            JournalBlocks = journaled ? sb.JournalBlocks : 0,
            JournalGeneration = journaled ? sb.JournalGeneration : 0,
            JournalState = journaled ? sb.JournalState : 0,
            JournalCkptLsn = journaled ? sb.JournalCkptLsn : 0,
            JournalHeadLsn = journaled ? sb.JournalHeadLsn : 0,
        };
        SuperblockMetaCodec.Write(buffer, in meta);
        meta.Crc = UnifiedCrc.ComputeCrc32C(buffer[..SuperblockMetaCodec.Offset_Crc]);
        SuperblockMetaCodec.Write_Crc(buffer, meta.Crc);
    }

    /// <summary>快照表序列化（V2 §1.1——superblock 内联区；条目序 = 捕获序；在册位 = 每条目 flags bit0）。
    /// Snapshots 旗标 ⇔ 表非空（删除至空即清旗标——双门对称）。
    /// ★ 条目 = SuperblockSnapshot 结构 codec（name 裸区 + runs 间隙由域代码承载）。</summary>
    private static void EncodeSnapshotTable(Span<byte> buffer, SuperblockData sb)
    {
        if (sb.Snapshots.Count == 0) return;   // buffer.Clear() 已零化——旗标由 EncodeSuperblock 按表空态维护
        for (var i = 0; i < sb.Snapshots.Count; i++)
        {
            var s = sb.Snapshots[i];
            var p = Sb.SnapshotTable + i * Sb.SnapshotEntrySize;
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(s.Name);
            nameBytes.AsSpan(0, Math.Min(nameBytes.Length, SuperblockSnapshot.NameBytes)).CopyTo(buffer[p..]);
            var snap = new SuperblockSnapshot
            {
                CaptureTicks = s.CaptureTicks,
                CaptureLsn = s.CaptureLsn,
                RunsCount = (uint)s.ImageRuns.Count,
                ImageLength = s.ImageLength,
                ImageCrc = s.ImageCrc,
                BitmapStart = s.BitmapStart,
                BitmapBlocks = s.BitmapBlocks,
                InUse = 1,   // flags bit0 = 在册（其余位零——保留）
            };
            SuperblockSnapshotCodec.Write(buffer.Slice(p, Sb.SnapshotEntrySize), in snap);
            var rp = p + SuperblockSnapshot.RunGapOffset;
            foreach (var (start, count) in s.ImageRuns)
            {
                var run = new SuperblockImageRun { Start = start, Count = count };
                SuperblockImageRunCodec.Write(buffer.Slice(rp, SuperblockImageRunCodec.StructSize), in run);
                rp += SuperblockImageRunCodec.StructSize;
            }
        }
    }

    /// <summary>快照表解码（V2 §1.1——前向兼容双门与 journal 字段同族：
    /// Snapshots 置位 → 解析（条目 ≥1）；未置位 + 区域非零 → 拒开（更高版本写入的卷——绝不静默忽略）。</summary>
    private static void DecodeSnapshotTable(ReadOnlySpan<byte> buffer, ushort flags, SuperblockData sb)
    {
        var flagSet = (flags & FlagSnapshots) != 0;
        var hasData = false;
        for (var i = 0; i < Sb.SnapshotMax; i++)
        {
            var p = Sb.SnapshotTable + i * Sb.SnapshotEntrySize;
            var inUse = SuperblockSnapshotCodec.Read(buffer.Slice(p, Sb.SnapshotEntrySize)).InUse;
            if (inUse == 0)
            {
                var allZero = true;
                var entry = buffer[p..(p + Sb.SnapshotEntrySize)];
                foreach (var b in entry)
                    if (b != 0) { allZero = false; break; }
                if (!allZero)
                    throw new FileIOException(IOError.IOFailure,
                        $"快照表条目 {i} 未在册但字段非零——数据不一致", null, "Open");
                continue;
            }
            if (inUse != 1)
                throw new FileIOException(IOError.Unsupported,
                    $"快照表条目 {i} 含未知 flags：0x{inUse:X4}（未知保留值拒开，§3.9）", null, "Open");
            hasData = true;
            var snapFields = SuperblockSnapshotCodec.Read(buffer.Slice(p, Sb.SnapshotEntrySize));
            var runCount = (int)snapFields.RunsCount;
            if (runCount is < 1 or > 8)
                throw new FileIOException(IOError.IOFailure, $"快照表条目 {i} 镜像区间数非法：{runCount}", null, "Open");
            var nameLen = buffer[p..(p + SuperblockSnapshot.NameBytes)].IndexOf((byte)0);
            var snap = new SnapshotEntry
            {
                Name = System.Text.Encoding.UTF8.GetString(buffer[p..(p + (nameLen < 0 ? SuperblockSnapshot.NameBytes : nameLen))]),
                CaptureTicks = snapFields.CaptureTicks,
                CaptureLsn = snapFields.CaptureLsn,
                ImageLength = snapFields.ImageLength,
                ImageCrc = snapFields.ImageCrc,
                BitmapStart = snapFields.BitmapStart,
                BitmapBlocks = snapFields.BitmapBlocks,
            };
            var rp = p + SuperblockSnapshot.RunGapOffset;
            for (var k = 0; k < runCount; k++)
            {
                var run = SuperblockImageRunCodec.Read(buffer[rp..]);
                snap.ImageRuns.Add((run.Start, run.Count));
                rp += SuperblockImageRunCodec.StructSize;
            }
            sb.Snapshots.Add(snap);
        }
        if (flagSet != hasData)
            throw new FileIOException(IOError.IOFailure,
                $"Snapshots 旗标与快照表内容不一致（flag={flagSet}, entries={hasData}）", null, "Open");
    }

    /// <summary>解码并校验——magic/版本/未知 flags/未知保留值/CRC 任一违约即 <see cref="IOError.IOFailure"/> 拒读。
    /// ★ 标量面 = SuperblockMeta 结构 codec 单解码；表区/uuid/label = 域（条目 codec + 裸区）。</summary>
    private static SuperblockData DecodeSuperblock(ReadOnlySpan<byte> buffer)
    {
        var meta = SuperblockMetaCodec.Read(buffer[..SuperblockMetaCodec.StructSize]);
        if (meta.Magic != SuperblockMeta.MagicValue)
            throw new FileIOException(IOError.IOFailure, "superblock magic 不符（非 TierVolume 卷）", null, "Open");
        var version = meta.Version;
        if (version != TierVolumeLayoutVersion)
            throw new FileIOException(IOError.Unsupported,
                $"布局版本不支持：{version}（本实现 {TierVolumeLayoutVersion}——版本高于支持上限拒开，§3.9）", null, "Open");
        if (meta.Crc != UnifiedCrc.ComputeCrc32C(buffer[..SuperblockMetaCodec.Offset_Crc]))
            throw new FileIOException(IOError.IOFailure, "superblock CRC 校验失败", null, "Open");

        var flags = meta.Flags;
        if ((flags & ~FlagsKnownMask) != 0)
            throw new FileIOException(IOError.Unsupported,
                $"superblock 含未知 flags：0x{flags:X4}（未知保留值拒开——绝不静默忽略，§3.9）", null, "Open");

        var sb = new SuperblockData
        {
            Flags = flags,
            BlockSize = meta.BlockSize,
            CapacityBlocks = meta.CapacityBlocks,
            BitmapStart = meta.BitmapStart,
            BitmapBlocks = meta.BitmapBlocks,
            ImageLength = meta.ImageLength,
            ImageCrc = meta.ImageCrc,
            Generation = meta.Generation,
            Uuid = new Guid(buffer[Sb.Uuid..(Sb.Uuid + 16)].ToArray()),
        };
        var runCount = (int)meta.ImageRunCount;
        if (runCount is < 1 or > 8)
            throw new FileIOException(IOError.IOFailure, $"镜像区间数非法：{runCount}", null, "Open");
        var p = Sb.ImageRuns;
        for (var i = 0; i < runCount; i++)
        {
            var run = SuperblockImageRunCodec.Read(buffer[p..]);
            sb.ImageRuns.Add((run.Start, run.Count));
            p += SuperblockImageRunCodec.StructSize;
        }
        // 日志字段（§3.9 前向兼容双门 / raw-journal-design §3.1）：
        // flag 未置 + 字段非零 = 更高版本写入的卷 → 拒开（老语义保持）；flag 置位 = 本特性，解析
        var jStart = meta.JournalStart;
        var jBlocks = meta.JournalBlocks;
        var jGen = meta.JournalGeneration;
        var jState = meta.JournalState;
        var jCkpt = meta.JournalCkptLsn;
        var jHead = meta.JournalHeadLsn;
        if ((flags & FlagJournaled) != 0)
        {
            if (jStart == 0 || jBlocks == 0)
                throw new FileIOException(IOError.IOFailure,
                    "Journaled 卷日志区字段非法（start/blocks 为零）", null, "Open");
            sb.JournalStart = jStart;
            sb.JournalBlocks = jBlocks;
            sb.JournalGeneration = jGen;
            sb.JournalState = jState;
            sb.JournalCkptLsn = jCkpt;
            sb.JournalHeadLsn = jHead;
        }
        else if (jStart != 0 || jBlocks != 0 || jGen != 0 || jState != 0 || jCkpt != 0 || jHead != 0)
            throw new FileIOException(IOError.Unsupported,
                "日志字段非零（v2+ 特性写入的卷）——本卷未置 Journaled 旗标，数据不一致", null, "Open");

        // 成员表（RM-04 §3.8）：MultiCarrier 置位 = 多载体卷（老二进制经未知 flags 门拒开）
        var memberCount = (int)meta.MemberCount;
        var multi = (flags & FlagMultiCarrier) != 0;
        if (multi)
        {
            if (memberCount is < 2 or > Sb.MemberTableMax)
                throw new FileIOException(IOError.IOFailure,
                    $"多载体卷成员数非法：{memberCount}（2..{Sb.MemberTableMax}）", null, "Open");
            sb.Members.Clear();
            for (var i = 0; i < memberCount; i++)
            {
                var dp = Sb.MemberTable + i * SuperblockMemberCodec.StructSize;
                var mem = SuperblockMemberCodec.Read(buffer.Slice(dp, SuperblockMemberCodec.StructSize));
                sb.Members.Add(new MemberEntry(
                    new Guid(buffer.Slice(dp, SuperblockMember.UuidBytes).ToArray()),
                    mem.CapacityBlocks,
                    mem.BitmapStartLocal,
                    mem.BitmapBlocksLocal));
            }
        }
        else
        {
            if (memberCount != 1)
                throw new FileIOException(IOError.Unsupported,
                    $"成员数 {memberCount} 但未置 MultiCarrier 旗标——数据不一致", null, "Open");
            sb.Members = [new MemberEntry(sb.Uuid, sb.CapacityBlocks, sb.BitmapStart, sb.BitmapBlocks)];
        }
        var labelLen = buffer[Sb.Label..(Sb.Label + Sb.LabelBytes)].IndexOf((byte)0);
        sb.Label = System.Text.Encoding.UTF8.GetString(buffer[Sb.Label..(Sb.Label + (labelLen < 0 ? Sb.LabelBytes : labelLen))]);
        DecodeSnapshotTable(buffer, flags, sb);   // V2 §1.1（Snapshots 置位 ⇔ 表非空——双门与 journal 字段同族）
        return sb;
    }

}
