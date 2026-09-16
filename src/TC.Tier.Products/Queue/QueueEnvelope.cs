using TC.Tier.Contracts.Storage;
using TC.Tier.Contracts.Structures;
using TC.Tier.Runtime.Structures.Ring;
using System.Runtime.InteropServices;

namespace TC.Tier.Products.Queue;

[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 29)]
internal struct QueueEnvelopeHeaderLayout
{
    [FieldOffset(0)] internal uint MagicValue;
    [FieldOffset(4)] internal byte Flags;
    [FieldOffset(5)] internal long ProducerId;
    [FieldOffset(13)] internal long Seq;
    [FieldOffset(21)] internal long DueTime;
}

/// <summary>
/// TierQueue 消息 envelope（tc-tier-queue-spec §2 产品层协议——版本化变长头）。
/// <para>布局（"TQE1"+flags 共 5B 头 + 三槽 24B）：
/// <c>[Magic 4B "TQE1"][Flags 1B][ProducerId 8B?][Seq 8B?][DueTime 8B] + 原 payload</c>。</para>
/// <para>★ flags：bit0=DELAYED（延迟消息）/ bit1=IDEMPOTENT（幂等生产——pid/seq 槽有效）。</para>
/// <para>★ record key 二态保持（(due,0) 延迟 / (0,0) 即时）——扫描侧零读 value 判延迟；
///   envelope 携带完整语义槽（幂等判等/恢复重放经 <see cref="IdemKeyResolver"/> 读回）。</para>
/// </summary>
public static class QueueEnvelope
{
    private const uint MagicValue = 0x31455154U;

    /// <summary>布局魔数（版本化）。</summary>
    public static ReadOnlySpan<byte> Magic => "TQE1"u8;

    /// <summary>flags：延迟消息。</summary>
    public const byte FlagDelayed = 0x1;

    /// <summary>flags：幂等生产（pid/seq 槽有效）。</summary>
    public const byte FlagIdempotent = 0x2;

    /// <summary>固定头长（Magic 4 + Flags 1 + pid 8 + seq 8 + due 8）。</summary>
    public const int HeaderSize = QueueEnvelopeHeaderLayoutCodec.StructSize;

    /// <summary>包裹：原 payload → envelope 化消息字节。</summary>
    /// <param name="delayed">是否延迟消息（置 FlagDelayed）。</param>
    /// <param name="idempotent">是否幂等生产（置 FlagIdempotent——pid/seq 槽有效）。</param>
    /// <param name="producerId">幂等生产者 ID（idempotent=false 时忽略）。</param>
    /// <param name="seq">幂等序号（生产者内单调）。</param>
    /// <param name="dueTime">延迟投递时刻（UTC Ticks；非延迟时忽略）。</param>
    /// <param name="payload">原消息字节。</param>
    /// <returns>固定头 + 原 payload 拼接后的完整 envelope 消息字节。</returns>
    public static byte[] Wrap(bool delayed, bool idempotent, long producerId, long seq, long dueTime,
        ReadOnlySpan<byte> payload)
    {
        var buf = new byte[HeaderSize + payload.Length];
        WriteHeader(buf, delayed, idempotent, producerId, seq, dueTime);
        payload.CopyTo(buf.AsSpan(HeaderSize));
        return buf;
    }

    /// <summary>仅编码固定 envelope 头，供 Ring scatter 写入避免临时拼接数组。</summary>
    /// <param name="destination">目标缓冲（长度 ≥ <see cref="HeaderSize"/>）。</param>
    /// <param name="delayed">是否延迟消息（置 FlagDelayed）。</param>
    /// <param name="idempotent">是否幂等生产（置 FlagIdempotent）。</param>
    /// <param name="producerId">幂等生产者 ID。</param>
    /// <param name="seq">幂等序号。</param>
    /// <param name="dueTime">延迟投递时刻（UTC Ticks）。</param>
    internal static void WriteHeader(Span<byte> destination, bool delayed, bool idempotent,
        long producerId, long seq, long dueTime)
    {
        byte flags = (byte)((delayed ? FlagDelayed : 0) | (idempotent ? FlagIdempotent : 0));
        var header = new QueueEnvelopeHeaderLayout
        {
            MagicValue = MagicValue,
            Flags = flags,
            ProducerId = producerId,
            Seq = seq,
            DueTime = dueTime,
        };
        QueueEnvelopeHeaderLayoutCodec.Write(destination, in header);
    }

    /// <summary>解包（magic 校验失败 = 非本产品格式 → false）。</summary>
    /// <param name="src">envelope 化消息字节。</param>
    /// <param name="flags">输出：flags（bit0=DELAYED/bit1=IDEMPOTENT）。</param>
    /// <param name="producerId">输出：幂等生产者 ID。</param>
    /// <param name="seq">输出：幂等序号。</param>
    /// <param name="dueTime">输出：延迟投递时刻（UTC Ticks）。</param>
    /// <param name="payload">输出：原 payload（拷贝交付）。</param>
    /// <returns>true = 解包成功；false = 非本产品格式（魔数/长度非法）。</returns>
    public static bool TryUnwrap(ReadOnlySpan<byte> src, out byte flags, out long producerId, out long seq,
        out long dueTime, out byte[] payload)
    {
        flags = 0; producerId = 0; seq = 0; dueTime = 0;
        payload = Array.Empty<byte>();
        if (src.Length < HeaderSize) return false;
        var header = QueueEnvelopeHeaderLayoutCodec.Read(src);
        if (header.MagicValue != MagicValue) return false;
        flags = header.Flags;
        producerId = header.ProducerId;
        seq = header.Seq;
        dueTime = header.DueTime;
        payload = src.Slice(HeaderSize).ToArray();
        return true;
    }
}

/// <summary>
/// 幂等索引专用 keyResolver（spec §6.4——HashIndex 判等闭环/恢复重放的数据面）：
/// TryGetKey 读 record 的 envelope → 幂等消息返回 (pid, seq)；非幂等 record 返回 false
/// （天然不参与幂等索引的重放）。扫描 = Ring 流逐条解 envelope。
/// </summary>
/// <param name="ring">数据 Ring（读 record envelope 反解 pid/seq）。</param>
internal sealed class IdemKeyResolver(RingOfQueueKey ring) : IKeyResolver<QueueKey>
{
    /// <inheritdoc/>
    public bool TryGetKey(LogicalAddress addr, out QueueKey key)
    {
        key = default;
        var recordKey = ring.GetKey(addr);
        if (recordKey.ValueLength < QueueEnvelope.HeaderSize) return false;
        var buf = new byte[recordKey.ValueLength];   // GetValue 需完整 value 长度（头只是前缀）
        if (ring.GetValue(addr, buf) < QueueEnvelope.HeaderSize) return false;
        if (!QueueEnvelope.TryUnwrap(buf, out var flags, out var pid, out var seq, out _, out _)) return false;
        if ((flags & QueueEnvelope.FlagIdempotent) == 0) return false;   // 非幂等 record 不入索引
        key = new QueueKey(pid, seq);
        return true;
    }

    /// <inheritdoc/>
    public LogicalAddress GetFlushedWatermark() => ring.GetFlushedWatermark();

    /// <inheritdoc/>
    public async IAsyncEnumerable<(QueueKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(
        LogicalAddress begin, LogicalAddress end,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var (_, addr, _) in ring.ScanAsync(begin, end, ct).ConfigureAwait(false))
        {
            if (TryGetKey(addr, out var key))
                yield return (key, addr, false);
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<(QueueKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(CancellationToken ct = default)
        => ScanAsync(ring.BeginAddress, ring.TailAddress, ct);
}
