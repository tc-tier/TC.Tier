using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// AppendEntries 条目区（线格式 v3——信封之死后的单拷贝直写区）。
/// <para>★ 区布局（全部小端）：<c>[Count 4B][×N: <see cref="RaftAppendEntryHeader"/> 13B + Content]</c>。
/// 嵌入 <see cref="AppendEntriesReq.EntriesRegion"/> blob 随生成 codec 上线（blob 外壳
/// [Len 4B][bytes] 归 WireMessageGenerator 管辖）；区内布局在本文件单点声明。</para>
/// <para>★ 写面 = <see cref="IRaftStore.WriteEntriesToAsync"/>（存储直写帧缓冲——单拷贝：
/// 存储页 → 帧，消灭 ReadEntriesAsync 逐条枚举 + Tuple + 逐条 EncodeInto 拷贝）；
/// 读面 = <see cref="TryReadCount"/>/<see cref="TryReadEntry"/> 游标式零拷贝切片。</para>
/// </summary>
public static class RaftEntriesRegion
{
    /// <summary>单条目固定头宽（<see cref="RaftAppendEntryHeaderCodec.StructSize"/>）。</summary>
    public static int HeaderSize => RaftAppendEntryHeaderCodec.StructSize;

    /// <summary>区计数前缀宽。</summary>
    public const int CountPrefixSize = 4;

    /// <summary>写区计数（条目写入前先落——读面据此确定条目数）。</summary>
    /// <param name="writer">区写器（帧缓冲视图）。</param>
    /// <param name="count">条目数。</param>
    public static void WriteCount(IBufferWriter<byte> writer, int count)
    {
        var span = writer.GetSpan(CountPrefixSize);
        BinaryPrimitives.WriteInt32LittleEndian(span, count);
        writer.Advance(CountPrefixSize);
    }

    /// <summary>写单条目（固定头 + 内容各一次直写——内容单拷贝落帧）。</summary>
    /// <param name="writer">区写器（帧缓冲视图）。</param>
    /// <param name="term">条目 term。</param>
    /// <param name="kind">条目种类（<see cref="RaftEntryKind"/>）。</param>
    /// <param name="content">条目内容（去信封原字节）。</param>
    public static void WriteEntry(IBufferWriter<byte> writer, long term, byte kind, ReadOnlyMemory<byte> content)
    {
        var header = writer.GetSpan(RaftAppendEntryHeaderCodec.StructSize);
        RaftAppendEntryHeaderCodec.Write(header, new RaftAppendEntryHeader(term, kind, content.Length));
        writer.Advance(RaftAppendEntryHeaderCodec.StructSize);
        content.Span.CopyTo(writer.GetSpan(content.Length));
        writer.Advance(content.Length);
    }

    /// <summary>读区计数（畸形 = false）。</summary>
    /// <param name="region">条目区。</param>
    /// <param name="count">条目数。</param>
    /// <param name="cursor">首条目偏移（= 计数前缀之后）。</param>
    /// <returns>true = 计数读取成功；false = 区不足以容纳计数前缀/计数为负（畸形）。</returns>
    public static bool TryReadCount(ReadOnlyMemory<byte> region, out int count, out int cursor)
    {
        count = 0;
        cursor = 0;
        if (region.Length < CountPrefixSize) return false;
        count = BinaryPrimitives.ReadInt32LittleEndian(region.Span);
        if (count < 0) return false;
        cursor = CountPrefixSize;
        return true;
    }

    /// <summary>游标读单条目（内容 = 区内切片——零拷贝；截断/负长 = false）。
    /// ★ 入参取 ReadOnlyMemory——消费面（follower 解码）在 async 方法内调用，C#12 禁
    /// ref-struct 局部跨 await（判例：async 禁 stackalloc 同源），零拷贝语义不受影响。</summary>
    /// <param name="region">条目区。</param>
    /// <param name="cursor">读游标（成功后推进到下一条目）。</param>
    /// <param name="term">条目 term。</param>
    /// <param name="kind">条目种类。</param>
    /// <param name="content">条目内容（区内切片视图）。</param>
    /// <returns>true = 条目读取成功（游标已推进）；false = 头/内容截断或负长（畸形）。</returns>
    public static bool TryReadEntry(ReadOnlyMemory<byte> region, ref int cursor,
        out long term, out byte kind, out ReadOnlyMemory<byte> content)
    {
        term = 0;
        kind = 0;
        content = default;
        if (region.Length < cursor + RaftAppendEntryHeaderCodec.StructSize) return false;
        var header = RaftAppendEntryHeaderCodec.Read(region.Span.Slice(cursor, RaftAppendEntryHeaderCodec.StructSize));
        if (header.ContentLength < 0
            || region.Length < cursor + RaftAppendEntryHeaderCodec.StructSize + header.ContentLength) return false;
        cursor += RaftAppendEntryHeaderCodec.StructSize;
        term = header.Term;
        kind = header.Kind;
        content = region.Slice(cursor, header.ContentLength);
        cursor += header.ContentLength;
        return true;
    }
}

/// <summary>
/// AppendEntries 条目固定头（13B——[Term 8B][Kind 1B][ContentLength 4B]，内容紧随）。
/// 线格式 v3：kind 升格为条目结构字段（信封格式消灭——引擎内/wire/存储帧均不再有
/// [Kind 1B][内容] 前缀字节）。
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 13)]
public readonly struct RaftAppendEntryHeader
{
    /// <summary>条目 term。</summary>
    [FieldOffset(0)] public readonly long Term;

    /// <summary>条目种类（<see cref="RaftEntryKind"/>）。</summary>
    [FieldOffset(8)] public readonly byte Kind;

    /// <summary>内容字节数（紧随头之后）。</summary>
    [FieldOffset(9)] public readonly int ContentLength;

    /// <summary>构造。</summary>
    /// <param name="term">条目 term。</param>
    /// <param name="kind">条目种类（<see cref="RaftEntryKind"/>）。</param>
    /// <param name="contentLength">内容字节数（紧随头之后）。</param>
    public RaftAppendEntryHeader(long term, byte kind, int contentLength)
    {
        Term = term;
        Kind = kind;
        ContentLength = contentLength;
    }
}
