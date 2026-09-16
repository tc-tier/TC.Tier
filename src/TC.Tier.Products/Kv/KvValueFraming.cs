using System.Buffers.Binary;
using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Products.Kv;

/// <summary>Kv 值帧头（9B：[Tag 1B][ExpiryTicks 8B]——[BinaryLayout] 声明式生成，零手写字节序）。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 9)]
internal struct KvValueFrameHeader
{
    /// <summary>帧 tag（TierKv 值帧标识）。</summary>
    [FieldOffset(0)] public byte Tag;

    /// <summary>过期 ticks（UTC，0 = 无过期）。</summary>
    [FieldOffset(1)] public long ExpiryTicks;
}

/// <summary>
/// TierKv 值帧（W8 TTL/过期）：TierKv 写入 Ring 的统一值封装 [tag 0xC8][8B 过期 ticks LE][payload]。
/// <para>★ 全量封装不变式：经 TierKv 写入的一切值都带帧（无 TTL = ticks 0）——读侧嗅探 tag
///   无歧义（TierKv 管辖的值恒有帧，非 TierKv 写入不适用）。过期判定惰性：读命中帧内过期戳
///   ≤ 当前 UtcNow 即视为不存在（W8 惰性读删），物理回收由 ReclaimAsync 强删。</para>
/// <para>★ 帧是 TierKv 内部线格式：payload = 用户经 IValueFormatter 翻译的字节（字节面为原值）。</para>
/// </summary>
internal static class KvValueFraming
{
    /// <summary>帧头字节数（1B tag + 8B 过期 ticks）。</summary>
    public const int HeaderSize = 9;

    /// <summary>帧 tag（TierKv 值帧标识）。</summary>
    public const byte Tag = 0xC8;

    /// <summary>封装（expiryTicks = 0 表示无过期）。</summary>
    /// <param name="payload">用户经 IValueFormatter 翻译后的 payload 字节。</param>
    /// <param name="expiryTicks">过期 ticks（UTC，0 表示无过期）。</param>
    /// <returns>带帧头的完整值帧字节数组。</returns>
    public static byte[] Frame(ReadOnlySpan<byte> payload, long expiryTicks)
    {
        var framed = new byte[HeaderSize + payload.Length];
        KvValueFrameHeaderCodec.Write(framed, new KvValueFrameHeader { Tag = Tag, ExpiryTicks = expiryTicks });
        payload.CopyTo(framed.AsSpan(HeaderSize));
        return framed;
    }

    /// <summary>
    /// 单分配封装缓冲（写热路径——帧头预写 + payload 区待 formatter 直填）：
    /// 与 <see cref="Frame"/> 的差异 = 不拷 payload——调用方把值格式化进返回缓冲的
    /// <c>AsSpan(HeaderSize)</c>，消除「formatter 缓冲 + Frame 再拷」的双分配双拷贝。
    /// </summary>
    /// <param name="payloadLength">payload 字节数（决定帧缓冲 payload 区容量）。</param>
    /// <param name="expiryTicks">过期 ticks（UTC，0 表示无过期）。</param>
    /// <returns>已写帧头、payload 区待填的帧缓冲（调用方经 AsSpan(HeaderSize) 直填）。</returns>
    public static byte[] AllocateFrame(int payloadLength, long expiryTicks)
    {
        var framed = new byte[HeaderSize + payloadLength];
        WriteHeader(framed, expiryTicks);
        return framed;
    }

    /// <summary>写帧头（tag + 过期 ticks）到已分配帧缓冲。</summary>
    /// <param name="framed">已分配的帧缓冲（长度不小于 HeaderSize）。</param>
    /// <param name="expiryTicks">过期 ticks（UTC，0 表示无过期）。</param>
    public static void WriteHeader(byte[] framed, long expiryTicks)
    {
        KvValueFrameHeaderCodec.Write(framed, new KvValueFrameHeader { Tag = Tag, ExpiryTicks = expiryTicks });
    }

    /// <summary>是否为 TierKv 值帧（长度/tag 嗅探——TierKv 管辖值恒带帧）。</summary>
    /// <param name="stored">存储侧的字节序列。</param>
    /// <returns>长度不小于 HeaderSize 且首字节为帧 tag 时为 true。</returns>
    public static bool IsFramed(ReadOnlySpan<byte> stored)
        => stored.Length >= HeaderSize && stored[0] == Tag;

    /// <summary>帧内过期 ticks（0 = 无过期；仅对 IsFramed 的存储字节调用）。</summary>
    /// <param name="stored">已帧封装的存储字节（长度不小于 HeaderSize）。</param>
    /// <returns>帧内过期 ticks（0 表示无过期）。</returns>
    public static long ExpiryTicks(ReadOnlySpan<byte> stored)
        => KvValueFrameHeaderCodec.Read_ExpiryTicks(stored);

    /// <summary>TTL → 过期 ticks（UTC 绝对值；null = 无过期；Zero = 立即过期——边界语义显式）。
    /// <para>★ 过期语义计算单点：Put/Rmw/CAS 三写路径共用——调用方（TierKv/KvSession 同 assembly）
    /// 在各自原子域内、比较通过后调用（CAS 的「比较+写入+挂过期」单一原子单元口径）。</para></summary>
    /// <param name="timeToLive">TTL（null = 无过期）。</param>
    /// <returns>过期 ticks（UTC 绝对值，0 表示无过期）。</returns>
    internal static long ExpiryTicks(TimeSpan? timeToLive)
        => timeToLive is { } ttl ? DateTime.UtcNow.Ticks + ttl.Ticks : 0;

    /// <summary>帧内 payload 拷贝。</summary>
    /// <param name="stored">已帧封装的存储字节（长度不小于 HeaderSize）。</param>
    /// <returns>帧内 payload 的拷贝字节数组。</returns>
    public static byte[] Payload(ReadOnlySpan<byte> stored)
        => stored[HeaderSize..].ToArray();

    /// <summary>过期判定（ticks 0 = 无过期；惰性读删的判定点）。</summary>
    /// <param name="expiryTicks">帧内过期 ticks（0 表示无过期）。</param>
    /// <param name="nowUtcTicks">当前 UTC ticks。</param>
    /// <returns>expiryTicks 非 0 且 ≤ nowUtcTicks 时为 true（已过期）。</returns>
    public static bool IsExpired(long expiryTicks, long nowUtcTicks)
        => expiryTicks != 0 && expiryTicks <= nowUtcTicks;
}
