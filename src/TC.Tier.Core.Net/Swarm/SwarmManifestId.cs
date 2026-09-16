using System;

namespace TC.Tier.Core.Net.Swarm;

/// <summary>
/// Swarm manifest 内容标识派生（#436 件四——语义化入口）。
/// <para>★ 不可变对象假设下 manifest 是内容的稳定函数：raft 域 = 快照覆盖点 index 承载；
///   内容域 = 16B 字节原序直拷（如 Blob ObjectId 的 16B 布局原序）。</para>
/// </summary>
public static class SwarmManifestId
{
    /// <summary>raft 快照域：覆盖点 index 承载（高 8B = index，低 8B = 0——行为逐字节不变）。</summary>
    /// <param name="snapshotIndex">快照覆盖点 index。</param>
    /// <returns>内容标识。</returns>
    public static Opaque16 ForRaftSnapshot(long snapshotIndex)
        => new Opaque16((ulong)snapshotIndex, 0);

    /// <summary>通用内容域：16B 标识字节原序直拷。</summary>
    /// <param name="id16">16 字节内容标识（不足抛 ArgumentException）。</param>
    /// <returns>内容标识。</returns>
    public static Opaque16 ForContent(ReadOnlySpan<byte> id16)
    {
        if (id16.Length != Opaque16.Size)
            throw new ArgumentException($"内容标识须为 {Opaque16.Size} 字节，实际 {id16.Length}。", nameof(id16));
        return new Opaque16(id16);
    }
}
