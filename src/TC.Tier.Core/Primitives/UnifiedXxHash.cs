namespace TC.Tier.Core.Primitives;

/// <summary>
/// 统一 XxHash 计算工具——仓库内哈希的对外出口（与 <see cref="UnifiedCrc"/> 同族的校验/哈希原语）。
/// <para>★ 实现自研化收编自 dotnet/runtime System.IO.Hashing（MIT）：算法/字节序/性能与官方逐位一致，
/// 替换 System.IO.Hashing 调用点不改动任何结果（XxHash64 与 XxHash128 均为公开确定性规范）。</para>
/// <para>★ 用途：KeyComparer 默认哈希（HashIndex tag/桶位）、QueueKeyComparer、对象存储替身 ETag 等。
/// 64 位为内存内哈希；128 位输出 16B Big Endian 字节（与官方 XxHash128.Hash 同序）。</para>
/// <para>★ 当前仅一次性形态（无流式消费者）；XxHash64 短 key 热路径零分配。</para>
/// </summary>
public static class UnifiedXxHash
{
    /// <summary>XxHash64 输出字节数。</summary>
    public const int Hash64Len = sizeof(ulong);

    /// <summary>XxHash128 输出字节数。</summary>
    public const int Hash128Len = 16;

    /// <summary>XXH64（seed 0）一次性计算——与官方 <c>XxHash64.HashToUInt64(source)</c> 逐位一致。</summary>
    /// <param name="data">待哈希数据（可空 span）。</param>
    /// <returns>64 位 XxHash64 哈希值（seed 0）。</returns>
    public static ulong Hash64(ReadOnlySpan<byte> data) => XxHash64.HashToUInt64(data);

    /// <summary>
    /// XXH128（seed 0）一次性计算，写 16B 至 <paramref name="destination"/>（Big Endian，与官方
    /// <c>XxHash128.Hash(source, destination)</c> 输出逐位一致）。
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="destination"/> 长度 &lt; 16。</exception>
    /// <param name="data">待哈希数据（可空 span）。</param>
    /// <param name="destination">输出缓冲区，长度至少 16 字节；前 16 字节被覆盖为哈希值（Big Endian 字节序）。</param>
    public static void Hash128(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        XxHash128.Hash(data, destination);
    }
}
