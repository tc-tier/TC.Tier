using System.Runtime.InteropServices;
using TC.Tier.Contracts.Storage;

namespace TC.Tier.Products.Queue;

/// <summary>重试计数条目（>0 者持久——DLQ 判据，spec §5.2）。</summary>
/// <param name="Address">重试中的消息地址。</param>
/// <param name="Count">已重投次数（>0 才持久）。</param>
public readonly record struct RetryEntry(LogicalAddress Address, int Count);

/// <summary>组状态头部的固定布局，由 <c>BinaryLayoutGenerator</c> 生成 codec。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct QueueGroupStateHeaderLayout
{
    [FieldOffset(0)] internal ulong MagicValue;
    [FieldOffset(8)] internal int CursorSegId;
    [FieldOffset(12)] internal int CursorExtension;
    [FieldOffset(16)] internal long CursorOffset;
    [FieldOffset(24)] internal int SkipCount;
    [FieldOffset(28)] internal int RetryCount;
}

/// <summary>组状态地址的固定布局，由 <c>BinaryLayoutGenerator</c> 生成 codec。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct QueueGroupStateAddressLayout
{
    [FieldOffset(0)] internal int SegId;
    [FieldOffset(4)] internal int Extension;
    [FieldOffset(8)] internal long Offset;
}

/// <summary>组状态重试项的固定布局，由 <c>BinaryLayoutGenerator</c> 生成 codec。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 20)]
internal struct QueueGroupStateRetryLayout
{
    [FieldOffset(0)] internal int AddressSegId;
    [FieldOffset(4)] internal int AddressExtension;
    [FieldOffset(8)] internal long AddressOffset;
    [FieldOffset(16)] internal int Count;
}

/// <summary>组状态尾部的固定布局，由 <c>BinaryLayoutGenerator</c> 生成 codec。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 12)]
internal struct QueueGroupStateTailLayout
{
    [FieldOffset(0)] internal long Epoch;
    [FieldOffset(8)] internal int Flags;
}

/// <summary>
/// 组状态（tc-tier-queue-spec §5.2 P2 面）定长 codec。
/// <para>布局（小端，"TQGRP03\0"）：
/// <c>[Magic 8B][Cursor 16B][SkipCount 4B][RetryCount 4B]
/// [Skip[i] 16B × MaxInFlight][Retry[i] 20B（addr 16B + count 4B）× MaxInFlight]
/// [Epoch 8B][Flags 4B]</c>。
/// Cursor = 连续已确认前缀的下一地址（Invalid = 从未确认）；Skip = 乱序 ack 空洞（升序）；
/// Retry = 活跃重试计数（>0 者——死信判据 redeliveryCount &gt; MaxRedeliveries）；
/// Epoch = fencing 组代次；Flags bit0 = Fenced（EvictOldest 淘汰）。</para>
/// <para>LogicalAddress 序列化 16B（SegId+Ext+Offset）——地址是事实，不解析结构。
/// P0/P1 魔数无部署存量——v3 不做兼容（beta 格式迭代期）。</para>
/// </summary>
public static class QueueGroupState
{
    private const ulong MagicValue = 0x0033305052475154UL;

    /// <summary>布局魔数（版本化——P2 "TQGRP03\0"：+RetryCounts）。</summary>
    public static ReadOnlySpan<byte> Magic => "TQGRP03\0"u8;

    /// <summary>定长头（由源生成器从 <c>QueueGroupStateHeaderLayout</c> 生成）。</summary>
    public const int HeaderSize = QueueGroupStateHeaderLayoutCodec.StructSize;

    /// <summary>尾部长（由源生成器从 <c>QueueGroupStateTailLayout</c> 生成）。</summary>
    public const int TailSize = QueueGroupStateTailLayoutCodec.StructSize;

    /// <summary>Fenced 标志位（Flags bit0——EvictOldest 淘汰后置位）。</summary>
    public const int FlagsFenced = 0x1;

    /// <summary>MaxInFlight 对应的 payload 定长。</summary>
    /// <param name="maxInFlight">组在途窗口上限（Skip + Retry 表容量）。</param>
    /// <returns>该 maxInFlight 对应的定长 payload 字节数。</returns>
    public static int PayloadSize(int maxInFlight)
        => HeaderSize + maxInFlight * QueueGroupStateAddressLayoutCodec.StructSize
            + maxInFlight * QueueGroupStateRetryLayoutCodec.StructSize + TailSize;

    /// <summary>解码产物：组持久状态。</summary>
    /// <param name="Cursor">连续已确认前缀的下一地址（Invalid = 从未确认）。</param>
    /// <param name="Skip">乱序 ack 空洞（升序地址）。</param>
    /// <param name="Retry">活跃重试计数（>0 者）。</param>
    /// <param name="Epoch">fencing 组代次。</param>
    /// <param name="Fenced">EvictOldest 淘汰标记。</param>
    public readonly record struct State(
        LogicalAddress Cursor, IReadOnlyList<LogicalAddress> Skip,
        IReadOnlyList<RetryEntry> Retry, long Epoch, bool Fenced);

    private static readonly RetryEntry[] EmptyRetry = [];

    /// <summary>编码到 span（PayloadSize(maxInFlight) 定长——尾部零填充）。</summary>
    /// <param name="dst">目标缓冲（长度 ≥ <see cref="PayloadSize"/>(maxInFlight)）。</param>
    /// <param name="state">待编码的组状态。</param>
    /// <param name="maxInFlight">组在途窗口上限（Skip + Retry 表容量）。</param>
    /// <exception cref="ArgumentException">dst 过小 / Skip 或 Retry 表超 maxInFlight。</exception>
    public static void Write(Span<byte> dst, in State state, int maxInFlight)
    {
        ArgumentNullException.ThrowIfNull(state.Skip);
        ArgumentNullException.ThrowIfNull(state.Retry);
        if (dst.Length < PayloadSize(maxInFlight))
            throw new ArgumentException($"目标 span 过小：{dst.Length} < {PayloadSize(maxInFlight)}");
        if (state.Skip.Count > maxInFlight)
            throw new ArgumentException($"Skip 表超限：{state.Skip.Count} > MaxInFlight={maxInFlight}");
        if (state.Retry.Count > maxInFlight)
            throw new ArgumentException($"Retry 表超限：{state.Retry.Count} > MaxInFlight={maxInFlight}");

        var header = new QueueGroupStateHeaderLayout
        {
            MagicValue = MagicValue,
            CursorSegId = state.Cursor.SegId,
            CursorExtension = state.Cursor.Extension,
            CursorOffset = state.Cursor.Offset,
            SkipCount = state.Skip.Count,
            RetryCount = state.Retry.Count,
        };
        QueueGroupStateHeaderLayoutCodec.Write(dst, in header);
        for (int i = 0; i < state.Skip.Count; i++)
        {
            var address = state.Skip[i];
            var layout = new QueueGroupStateAddressLayout
            {
                SegId = address.SegId,
                Extension = address.Extension,
                Offset = address.Offset,
            };
            QueueGroupStateAddressLayoutCodec.Write(
                dst.Slice(HeaderSize + i * QueueGroupStateAddressLayoutCodec.StructSize), in layout);
        }
        int retryAt = HeaderSize + maxInFlight * QueueGroupStateAddressLayoutCodec.StructSize;
        for (int i = 0; i < state.Retry.Count; i++)
        {
            var entry = state.Retry[i];
            var layout = new QueueGroupStateRetryLayout
            {
                AddressSegId = entry.Address.SegId,
                AddressExtension = entry.Address.Extension,
                AddressOffset = entry.Address.Offset,
                Count = entry.Count,
            };
            QueueGroupStateRetryLayoutCodec.Write(
                dst.Slice(retryAt + i * QueueGroupStateRetryLayoutCodec.StructSize), in layout);
        }
        int tail = retryAt + maxInFlight * QueueGroupStateRetryLayoutCodec.StructSize;
        var tailLayout = new QueueGroupStateTailLayout
        {
            Epoch = state.Epoch,
            Flags = state.Fenced ? FlagsFenced : 0,
        };
        QueueGroupStateTailLayoutCodec.Write(dst.Slice(tail), in tailLayout);
    }

    /// <summary>解码（魔数/计数校验失败 = 从未持久化或损坏 → false，调用方取空状态）。</summary>
    /// <param name="src">组状态字节。</param>
    /// <param name="maxInFlight">组在途窗口上限（Skip + Retry 表容量）。</param>
    /// <param name="state">输出：解码后的组状态（失败 = default）。</param>
    /// <returns>true = 解码成功；false = 魔数/几何/计数非法。</returns>
    public static bool TryRead(ReadOnlySpan<byte> src, int maxInFlight, out State state)
    {
        state = default;
        if (src.Length < PayloadSize(maxInFlight)) return false;
        var header = QueueGroupStateHeaderLayoutCodec.Read(src);
        if (header.MagicValue != MagicValue) return false;

        var cursor = new LogicalAddress(header.CursorSegId, header.CursorExtension, header.CursorOffset);
        int skipCount = header.SkipCount;
        int retryCount = header.RetryCount;
        if (skipCount < 0 || skipCount > maxInFlight) return false;
        if (retryCount < 0 || retryCount > maxInFlight) return false;

        var skip = new List<LogicalAddress>(skipCount);
        for (int i = 0; i < skipCount; i++)
        {
            var layout = QueueGroupStateAddressLayoutCodec.Read(
                src.Slice(HeaderSize + i * QueueGroupStateAddressLayoutCodec.StructSize));
            skip.Add(new LogicalAddress(layout.SegId, layout.Extension, layout.Offset));
        }

        int retryAt = HeaderSize + maxInFlight * QueueGroupStateAddressLayoutCodec.StructSize;
        var retry = new List<RetryEntry>(retryCount);
        for (int i = 0; i < retryCount; i++)
        {
            var layout = QueueGroupStateRetryLayoutCodec.Read(
                src.Slice(retryAt + i * QueueGroupStateRetryLayoutCodec.StructSize));
            var addr = new LogicalAddress(layout.AddressSegId, layout.AddressExtension, layout.AddressOffset);
            int count = layout.Count;
            if (count > 0) retry.Add(new RetryEntry(addr, count));
        }

        int tail = retryAt + maxInFlight * QueueGroupStateRetryLayoutCodec.StructSize;
        var tailLayout = QueueGroupStateTailLayoutCodec.Read(src.Slice(tail));
        state = new State(cursor, skip, retry, tailLayout.Epoch, (tailLayout.Flags & FlagsFenced) != 0);
        return true;
    }
}
