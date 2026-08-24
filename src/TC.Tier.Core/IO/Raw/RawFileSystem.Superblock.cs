using System.Buffers.Binary;
using System.IO.Hashing;

namespace TC.Tier.Core.IO.Raw;

/// <summary>
/// Raw 根空间——第四介质（raw-medium-and-conversion-design 全篇）。
/// 自维护布局的连续后端根空间：既是活卷又是存档，本地持久化推荐位（§1.4）。
/// </summary>
public sealed partial class RawFileSystem
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
        public const int Magic = 0;              // 4B "RAW1"
        public const int Version = 4;            // u16
        public const int Flags = 6;              // u16（bit0=clean；其余保留→非零拒开）
        public const int BlockSize = 8;          // u32
        public const int CapacityBlocks = 12;    // u64
        public const int BitmapStart = 20;       // u64
        public const int BitmapBlocks = 28;      // u64
        public const int ImageRunCount = 36;     // u32（1..8）
        public const int ImageRuns = 40;         // 8 × (u64 start + u32 count) = 96B
        public const int ImageLength = 136;      // u64（元数据镜像字节数）
        public const int ImageCrc = 144;         // u32
        public const int Generation = 148;       // u64（双份轮写代数——恢复取高者）
        public const int Uuid = 156;             // 16B
        public const int CarrierIndex = 172;     // u16（多载体 Day-1 预留，§3.8——v1 恒 0）
        public const int MemberCount = 174;      // u32（v1 恒 1）
        public const int JournalStart = 178;     // u64（日志两级预留字段级，§3.9——禁用时恒 0）
        public const int JournalBlocks = 186;    // u64
        public const int JournalGeneration = 194;// u64
        public const int JournalState = 202;     // u32
        public const int Label = 206;            // 32B UTF8 零填充
        public const int JournalCkptLsn = 238;   // u64（raw-journal-design §3.1 零空间扩展——重放下界）
        public const int JournalHeadLsn = 246;   // u64（最后已提交 LSN——诊断/快速定位）
        public const int MemberTable = 256;      // 成员表（RM-04 §3.8——8 × 40B：UUID16 + cap8 + bitmapStart8 + bitmapBlocks8）
        public const int MemberTableMax = 8;     // v1 上限（加载体 = 纯加法；超 8 载体留布局版本）
        public const int Crc = 4088;             // u32（覆盖 0..4087）
        public const int TotalSize = 4096;
    }

    private const ushort RawLayoutVersion = 1;
    private const ushort FlagClean = 0x0001;
    private const ushort FlagJournaled = 0x0002;   // raw-journal-design §3.1——日志启用位
    private const ushort FlagMultiCarrier = 0x0004;   // RM-04 §3.8——多载体卷（老二进制拒开）
    private const ushort FlagAutoExpand = 0x0008;   // medium-protocol §5.3——自动扩容卷（quota=-1 New；老二进制拒开）
    private const ushort FlagsKnownMask = 0x000F;   // 其余位未知 → 拒开（§3.9 未知保留值拒开）

    /// <summary>成员表条目（§3.8——Day-1 字段激活；成员 0 = 主载体）。</summary>
    internal sealed record MemberEntry(Guid Uuid, ulong CapacityBlocks, ulong BitmapStartLocal, ulong BitmapBlocksLocal);

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
    }

    private static void EncodeSuperblock(Span<byte> buffer, SuperblockData sb)
    {
        buffer.Clear();
        "RAW1"u8.CopyTo(buffer[Sb.Magic..]);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[Sb.Version..], RawLayoutVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[Sb.Flags..], sb.Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[Sb.BlockSize..], sb.BlockSize);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[Sb.CapacityBlocks..], sb.CapacityBlocks);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[Sb.BitmapStart..], sb.BitmapStart);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[Sb.BitmapBlocks..], sb.BitmapBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[Sb.ImageRunCount..], (uint)sb.ImageRuns.Count);
        var p = Sb.ImageRuns;
        foreach (var (start, count) in sb.ImageRuns)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(buffer[p..], start);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer[(p + 8)..], count);
            p += 12;
        }
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[Sb.ImageLength..], sb.ImageLength);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[Sb.ImageCrc..], sb.ImageCrc);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[Sb.Generation..], sb.Generation);
        sb.Uuid.TryWriteBytes(buffer[Sb.Uuid..]);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer[Sb.CarrierIndex..], 0);   // 成员表见 @256（MemberCount 随成员表段写入）
        // 日志字段（raw-journal-design §3.1）：Journaled 置位时写实值；否则恒零（老二进制可开）
        var journaled = (sb.Flags & FlagJournaled) != 0;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[Sb.JournalStart..], journaled ? sb.JournalStart : 0);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[Sb.JournalBlocks..], journaled ? sb.JournalBlocks : 0);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[Sb.JournalGeneration..], journaled ? sb.JournalGeneration : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[Sb.JournalState..], journaled ? sb.JournalState : 0);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[Sb.JournalCkptLsn..], journaled ? sb.JournalCkptLsn : 0);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[Sb.JournalHeadLsn..], journaled ? sb.JournalHeadLsn : 0);
        // 成员表（RM-04 §3.8）：MultiCarrier 置位时实值；单载体卷恒 [self]（老语义）
        var multi = (sb.Flags & FlagMultiCarrier) != 0 && sb.Members.Count > 1;
        var memberCount = multi ? sb.Members.Count : 1;
        var member0 = multi ? sb.Members[0]
            : new MemberEntry(sb.Uuid, sb.CapacityBlocks, sb.BitmapStart, sb.BitmapBlocks);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[Sb.MemberCount..], (uint)memberCount);
        for (var i = 0; i < memberCount; i++)
        {
            var mp = Sb.MemberTable + i * 40;
            var m = i == 0 ? member0 : sb.Members[i];
            m.Uuid.TryWriteBytes(buffer[mp..]);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer[(mp + 16)..], m.CapacityBlocks);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer[(mp + 24)..], m.BitmapStartLocal);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer[(mp + 32)..], m.BitmapBlocksLocal);
        }
        var labelBytes = System.Text.Encoding.UTF8.GetBytes(sb.Label);
        labelBytes.AsSpan(0, Math.Min(labelBytes.Length, 32)).CopyTo(buffer[Sb.Label..]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[Sb.Crc..],
            Crc32.HashToUInt32(buffer[..Sb.Crc]));
    }

    /// <summary>解码并校验——magic/版本/未知 flags/未知保留值/CRC 任一违约即 <see cref="IOError.IOFailure"/> 拒读。</summary>
    private static SuperblockData DecodeSuperblock(ReadOnlySpan<byte> buffer)
    {
        if (!buffer[Sb.Magic..(Sb.Magic + 4)].SequenceEqual("RAW1"u8))
            throw new FileIOException(IOError.IOFailure, "superblock magic 不符（非 Raw 卷）", null, "Open");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(buffer[Sb.Version..]);
        if (version != RawLayoutVersion)
            throw new FileIOException(IOError.Unsupported,
                $"布局版本不支持：{version}（本实现 {RawLayoutVersion}——版本高于支持上限拒开，§3.9）", null, "Open");
        if (BinaryPrimitives.ReadUInt32LittleEndian(buffer[Sb.Crc..]) != Crc32.HashToUInt32(buffer[..Sb.Crc]))
            throw new FileIOException(IOError.IOFailure, "superblock CRC 校验失败", null, "Open");

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(buffer[Sb.Flags..]);
        if ((flags & ~FlagsKnownMask) != 0)
            throw new FileIOException(IOError.Unsupported,
                $"superblock 含未知 flags：0x{flags:X4}（未知保留值拒开——绝不静默忽略，§3.9）", null, "Open");

        var sb = new SuperblockData
        {
            Flags = flags,
            BlockSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer[Sb.BlockSize..]),
            CapacityBlocks = BinaryPrimitives.ReadUInt64LittleEndian(buffer[Sb.CapacityBlocks..]),
            BitmapStart = BinaryPrimitives.ReadUInt64LittleEndian(buffer[Sb.BitmapStart..]),
            BitmapBlocks = BinaryPrimitives.ReadUInt64LittleEndian(buffer[Sb.BitmapBlocks..]),
            ImageLength = BinaryPrimitives.ReadUInt64LittleEndian(buffer[Sb.ImageLength..]),
            ImageCrc = BinaryPrimitives.ReadUInt32LittleEndian(buffer[Sb.ImageCrc..]),
            Generation = BinaryPrimitives.ReadUInt64LittleEndian(buffer[Sb.Generation..]),
            Uuid = new Guid(buffer[Sb.Uuid..(Sb.Uuid + 16)].ToArray()),
        };
        var runCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[Sb.ImageRunCount..]);
        if (runCount is < 1 or > 8)
            throw new FileIOException(IOError.IOFailure, $"镜像区间数非法：{runCount}", null, "Open");
        var p = Sb.ImageRuns;
        for (var i = 0; i < runCount; i++)
        {
            sb.ImageRuns.Add((BinaryPrimitives.ReadUInt64LittleEndian(buffer[p..]),
                BinaryPrimitives.ReadUInt32LittleEndian(buffer[(p + 8)..])));
            p += 12;
        }
        // 日志字段（§3.9 前向兼容双门 / raw-journal-design §3.1）：
        // flag 未置 + 字段非零 = 更高版本写入的卷 → 拒开（老语义保持）；flag 置位 = 本特性，解析
        var jStart = BinaryPrimitives.ReadUInt64LittleEndian(buffer[Sb.JournalStart..]);
        var jBlocks = BinaryPrimitives.ReadUInt64LittleEndian(buffer[Sb.JournalBlocks..]);
        var jGen = BinaryPrimitives.ReadUInt64LittleEndian(buffer[Sb.JournalGeneration..]);
        var jState = BinaryPrimitives.ReadUInt32LittleEndian(buffer[Sb.JournalState..]);
        var jCkpt = BinaryPrimitives.ReadUInt64LittleEndian(buffer[Sb.JournalCkptLsn..]);
        var jHead = BinaryPrimitives.ReadUInt64LittleEndian(buffer[Sb.JournalHeadLsn..]);
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
        var memberCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[Sb.MemberCount..]);
        var multi = (flags & FlagMultiCarrier) != 0;
        if (multi)
        {
            if (memberCount is < 2 or > Sb.MemberTableMax)
                throw new FileIOException(IOError.IOFailure,
                    $"多载体卷成员数非法：{memberCount}（2..{Sb.MemberTableMax}）", null, "Open");
            sb.Members.Clear();
            for (var i = 0; i < memberCount; i++)
            {
                var dp = Sb.MemberTable + i * 40;
                sb.Members.Add(new MemberEntry(
                    new Guid(buffer[dp..(dp + 16)].ToArray()),
                    BinaryPrimitives.ReadUInt64LittleEndian(buffer[(dp + 16)..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(buffer[(dp + 24)..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(buffer[(dp + 32)..])));
            }
        }
        else
        {
            if (memberCount != 1)
                throw new FileIOException(IOError.Unsupported,
                    $"成员数 {memberCount} 但未置 MultiCarrier 旗标——数据不一致", null, "Open");
            sb.Members = [new MemberEntry(sb.Uuid, sb.CapacityBlocks, sb.BitmapStart, sb.BitmapBlocks)];
        }
        var labelLen = buffer[Sb.Label..(Sb.Label + 32)].IndexOf((byte)0);
        sb.Label = System.Text.Encoding.UTF8.GetString(buffer[Sb.Label..(Sb.Label + (labelLen < 0 ? 32 : labelLen))]);
        return sb;
    }
}
