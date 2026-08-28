using System.IO.Compression;
using System.IO.Hashing;

namespace TC.Tier.Core.IO.Image;


/// <summary>
/// TCA1 流式传送格式（raw-medium-and-conversion-design §5.1）——采集器与还原器共用的二进制编解码。
/// <para>★ 布局（顺序流式友好——先清单位后数据帧，接收端先建命名空间再收数据）：</para>
/// <code>
/// [流头]  "TCA1" | 版本 u16 | flags u16(bit0=zlib) | 条目数 u32 | 头CRC32
/// [清单]  ×N：路径 u16len+UTF8 | 类型 u8 | 逻辑长度 u64 | FileExtra u16len+bytes
///         | 修改刻 i64(审计) | 区间数 u32 | 区间 ×M：start u64 | end u64 | flags u8(bit0=unwritten 预留)
/// [数据帧] ×K：entryIdx u32 | offset u64 | rawLen u32 | storedLen u32 | codec u8 | CRC32(raw) u32 | payload
/// [流尾]  "TCE1" | 帧数 u64 | 原始字节 u64 | 帧CRC聚合 u32 | 尾CRC32
/// </code>
/// <para>★ 稀疏保真：清单区间 = <see cref="IFileHandle.EnumerateAllocatedRanges"/>——洞不占数据帧，
///   还原端按区间写 + <see cref="IFileHandle.SetLength"/> 重建逻辑长度（洞读零语义三介质天然成立）。</para>
/// <para>★ 校验：每帧 CRC32（原始字节）+ 帧序列 CRC 聚合（流尾）——坏帧定位到帧、整体对账在尾。</para>
/// </summary>
internal static class ImageFormat
{
    private static ReadOnlySpan<byte> Magic => "TCA1"u8;
    /// <summary>
    /// 流尾魔数（尾 CRC 聚合的对账基准——帧序列 CRC 聚合，设计 §5.1/§5.3）。
    /// </summary>
    internal static ReadOnlySpan<byte> FooterMagic => "TCE1"u8;
    private const ushort Version = 1;

    // 区间 flags
    internal const byte RangeUnwritten = 0x01;   // §5.2 unwritten 保真（TierVolume 载体启用——virtual 介质 unwritten extent 保真；local/memory 恒 0）

    /// <summary>写流头 + 清单（采集第一阶段——枚举 + Stat + FileExtra + 区间表）。</summary>
    internal static void WriteManifest(BinaryWriter w, ImageOptions options,
        IReadOnlyList<ManifestEntry> entries)
    {
        w.Write(Magic);
        w.Write(Version);
        w.Write((ushort)options.Compression);
        w.Write((uint)entries.Count);
        w.Flush();
        foreach (var e in entries)
        {
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(e.Path);
            w.Write((ushort)pathBytes.Length);
            w.Write(pathBytes);
            w.Write((byte)e.Type);
            w.Write(e.LogicalLength);
            w.Write((ushort)e.Extra.Length);
            if (e.Extra.Length > 0) w.Write(e.Extra);
            w.Write(e.ModifiedTicks);
            w.Write((uint)e.Ranges.Count);
            foreach (var (start, end, flags) in e.Ranges)
            {
                w.Write(start);
                w.Write(end);
                w.Write(flags);
            }
        }
    }

    /// <summary>读流头 + 清单（还原第一阶段；magic/版本/CRC 违约即拒——未知保留值拒开同族，§3.9）。</summary>
    /// <exception cref="FileIOException">流头/清单格式违约（magic/版本/flags/长度/CRC）</exception>
    /// <param name="r">流头 + 清单的二进制读取器</param>
    /// <returns>流头压缩编码 + 清单条目列表</returns>
    internal static (ImageCompression Compression, List<ManifestEntry> Entries) ReadManifest(BinaryReader r)
    {
        if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic))
            throw NewFormatError(null, "流头 magic 不符（非 TCA1 采集流）。");
        var version = r.ReadUInt16();
        if (version != Version)
            throw NewFormatError(null, $"流版本不支持：{version}（本实现 {Version}——版本高于支持上限拒开）");
        var flags = r.ReadUInt16();
        if ((flags & ~0x03) != 0)
            throw NewFormatError(null, $"流头含未知 flags：0x{flags:X4}（未知保留值拒读——绝不静默忽略）");
        var compression = (ImageCompression)(flags & 0x03);
        if (!Enum.IsDefined(compression))
            throw NewFormatError(null, $"未知压缩编码：{flags & 0x03}");
        var count = r.ReadUInt32();
        var entries = new List<ManifestEntry>((int)Math.Min(count, 1 << 20));
        for (var i = 0; i < count; i++)
        {
            var pathLen = r.ReadUInt16();
            var path = System.Text.Encoding.UTF8.GetString(r.ReadBytes(pathLen));
            var type = (FsEntryType)r.ReadByte();
            var logicalLength = r.ReadInt64();
            var extraLen = r.ReadUInt16();
            var extra = extraLen > 0 ? r.ReadBytes(extraLen) : [];
            var modifiedTicks = r.ReadInt64();
            var rangeCount = r.ReadUInt32();
            var ranges = new List<(long Start, long End, byte Flags)>((int)Math.Min(rangeCount, 1 << 20));
            for (var k = 0; k < rangeCount; k++)
            {
                var start = r.ReadInt64();
                var end = r.ReadInt64();
                var rflags = r.ReadByte();
                if ((rflags & ~RangeUnwritten) != 0)
                    throw NewFormatError(path, $"区间含未知 flags：0x{rflags:X2}（未知保留值拒读）");
                ranges.Add((start, end, rflags));
            }
            entries.Add(new ManifestEntry(path, type, logicalLength, extra, modifiedTicks, ranges));
        }
        return (compression, entries);
    }

    /// <summary>写单个数据帧（压缩 + CRC；返回本帧原始字节数与帧 CRC 供聚合）。</summary>
    /// <param name="w">二进制写入器</param>
    /// <param name="entryIdx">清单条目索引（帧所属条目）</param>
    /// <param name="offset">帧在条目中的偏移量</param>
    /// <param name="raw">原始数据</param>
    /// <param name="codec">压缩编码</param>
    /// <returns>(Offset, RawLen, FrameCrc)</returns>
    internal static (long Offset, int RawLen, uint FrameCrc) WriteFrame(BinaryWriter w, uint entryIdx,
        long offset, ReadOnlySpan<byte> raw, ImageCompression codec)
    {
        uint crc = Crc32.HashToUInt32(raw);
        byte[] stored;
        var storedCodec = (byte)codec;
        if (codec == ImageCompression.ZLib)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                z.Write(raw);
            stored = ms.ToArray();
            if (stored.Length >= raw.Length)
            {
                stored = raw.ToArray();   // 膨胀回退原始（小随机块不可压——帧级自适应）
                storedCodec = (byte)ImageCompression.None;
            }
        }
        else if (codec == ImageCompression.Zstd)
        {
            stored = NativeInterop.ZstdCodec.CompressFrame(raw);
            if (stored.Length >= raw.Length)
            {
                stored = raw.ToArray();   // 膨胀回退（与 ZLib 同策略）
                storedCodec = (byte)ImageCompression.None;
            }
        }
        else
        {
            stored = raw.ToArray();
        }

        w.Write(entryIdx);
        w.Write(offset);
        w.Write(raw.Length);
        w.Write(stored.Length);
        w.Write(storedCodec);
        w.Write(crc);
        w.Write(stored);
        return (offset, raw.Length, crc);
    }

    /// <summary>读单个数据帧（解压 + 校验）；返回 (entryIdx, offset, rawPayload)。</summary>
    /// <param name="r">二进制读取器</param>
    /// <param name="verify">是否校验 CRC（还原端可选关闭 吞吐）</param>
    /// <returns>(EntryIdx, Offset, RawPayload)</returns>
    internal static (uint EntryIdx, long Offset, byte[] Raw) ReadFrame(BinaryReader r, bool verify)
    {
        var entryIdx = r.ReadUInt32();
        var (offset, raw) = ReadFrameCore(r, verify);
        return (entryIdx, offset, raw);
    }

    /// <summary>
    /// 帧体读取（entryIdx 已由调用方读走——纯流式探测路径复用，D5：
    /// 非可寻址流先探 4 字节判帧/尾，尾 magic 不匹配时按帧体续读）。
    /// </summary>
    /// <param name="r">二进制读取器</param>
    /// <param name="verify">是否校验 CRC（还原端可选关闭 吞吐）</param>
    /// <returns>(Offset, RawPayload)</returns>
    internal static (long Offset, byte[] Raw) ReadFrameCore(BinaryReader r, bool verify)
    {
        var offset = r.ReadInt64();
        var rawLen = r.ReadInt32();
        var storedLen = r.ReadInt32();
        var codec = r.ReadByte();
        if (codec is > (byte)ImageCompression.Zstd)
            throw NewFormatError(null, $"未知帧编码：{codec}（未知保留值拒读）");
        var crc = r.ReadUInt32();
        var stored = r.ReadBytes(storedLen);

        byte[] raw;
        if (codec == (byte)ImageCompression.ZLib)
        {
            using var input = new MemoryStream(stored);
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            raw = output.ToArray();
        }
        else if (codec == (byte)ImageCompression.Zstd)
        {
            if (!NativeInterop.ZstdCodec.IsAvailable)
                throw NewFormatError(null, "zstd 帧但本机运行库不可用——无法还原（诚实失败）");
            raw = NativeInterop.ZstdCodec.DecompressFrame(stored, rawLen);
        }
        else
        {
            raw = stored;
        }

        if (raw.Length != rawLen)
            throw NewFormatError(null, $"帧长度不符：解压 {raw.Length} ≠ 头声明 {rawLen}");
        if (verify && Crc32.HashToUInt32(raw) != crc)
            throw NewFormatError(null, $"帧 CRC 校验失败（offset={offset}）——数据损坏");
        return (offset, raw);
    }

    /// <summary>写流尾（帧数/原始字节/帧 CRC 聚合）。</summary>
    /// <param name="w">二进制写入器</param>
    /// <param name="frameCount">帧数</param>
    /// <param name="rawBytes">原始字节数</param>
    /// <param name="framesCrc">帧 CRC 聚合</param>
    internal static void WriteFooter(BinaryWriter w, long frameCount, long rawBytes, uint framesCrc)
    {
        w.Write(FooterMagic);
        w.Write(frameCount);
        w.Write(rawBytes);
        w.Write(framesCrc);
    }

    /// <summary>读流尾并全量对账（帧数/字节数/聚合 CRC——不符即拒）。</summary>
    /// <param name="r">二进制读取器</param>
    /// <param name="frameCount">帧数</param>
    /// <param name="rawBytes">原始字节数</param>
    /// <param name="framesCrc">帧 CRC 聚合</param>
    internal static void ReadFooterAndVerify(BinaryReader r, long frameCount, long rawBytes, uint framesCrc)
    {
        if (!r.ReadBytes(4).AsSpan().SequenceEqual(FooterMagic))
            throw NewFormatError(null, "流尾 magic 不符（流不完整）");
        ReadFooterFieldsAndVerify(r, frameCount, rawBytes, framesCrc);
    }

    /// <summary>流尾字段对账（magic 已由调用方读走——纯流式探测路径复用，D5）。</summary>
    /// <param name="r">二进制读取器</param>
    /// <param name="frameCount">帧数</param>
    /// <param name="rawBytes">原始字节数</param>
    /// <param name="framesCrc">帧 CRC 聚合</param>
    internal static void ReadFooterFieldsAndVerify(BinaryReader r, long frameCount, long rawBytes, uint framesCrc)
    {
        var fc = r.ReadInt64();
        var rb = r.ReadInt64();
        var fcr = r.ReadUInt32();
        if (fc != frameCount || rb != rawBytes || fcr != framesCrc)
            throw NewFormatError(null,
                $"流尾对账失败：帧 {fc}=={frameCount}? 字节 {rb}=={rawBytes}? 聚合CRC {fcr:X8}=={framesCrc:X8}?");
    }

    /// <summary>帧 CRC 聚合（帧 CRC 序列的滚动 CRC-32——尾对账基准；增量实现，与库的一次散列独立）。</summary>
    /// <param name="current">当前聚合 CRC（初始 0）</param>
    /// <param name="frameCrc">单帧 CRC</param>
    internal static uint AggregateCrc(uint current, uint frameCrc)
    {
        Span<byte> b = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, frameCrc);
        return Crc32Incremental(current, b);
    }

    /// <summary>
    /// CRC-32 查表（标准反射多项式 0xEDB88320——System.IO.Hashing 8.x 无静态 Update，自持增量）。
    /// </summary>
    private static readonly uint[] SCrcTable = BuildCrcTable();

    /// <summary>增量 CRC-32（标准反射多项式 0xEDB88320——System.IO.Hashing 8.x 无静态 Update，自持增量）。</summary>
    /// <param name="crc">当前 CRC（初始 0）</param>
    /// <param name="data">增量数据</param>
    /// <returns>增量后的 CRC</returns>
    private static uint Crc32Incremental(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
        foreach (var b in data)
            crc = SCrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var i = 0u; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    /// <summary>
    /// 格式错误异常（流头/清单/帧/尾违约——magic/版本/flags/长度/CRC）。
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <param name="message">错误信息</param>
    /// <returns>文件 I/O 异常</returns>
    internal static FileIOException NewFormatError(string? path, string message)
        => new(IOError.IOFailure, $"TCA1 格式错误：{message}", path, "Image");

    /// <summary>清单条目（采集端从接口平面物化；还原端按此重建）。</summary>
    /// <param name="Path">条目路径（相对根目录，UTF-8）</param>
    /// <param name="Type">条目类型（文件/目录/符号链接/特殊文件）</param>
    /// <param name="LogicalLength">条目逻辑长度（文件/符号链接/特殊文件）</param>
    /// <param name="Extra">条目额外信息（文件/符号链接/特殊文件）</param>
    /// <param name="ModifiedTicks">条目修改时间（审计，文件/符号链接/特殊文件）</param>
    /// <param name="Ranges">条目稀疏区间（文件）</param>
    internal sealed record ManifestEntry(
        string Path,
        FsEntryType Type,
        long LogicalLength,
        byte[] Extra,
        long ModifiedTicks,
        List<(long Start, long End, byte Flags)> Ranges);
}
