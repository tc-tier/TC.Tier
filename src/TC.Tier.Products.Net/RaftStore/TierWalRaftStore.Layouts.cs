using System.Runtime.InteropServices;
using TC.Tier.CodeGen;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Raft;

namespace TC.Tier.Products.Net.RaftStore;

/// <summary>
/// TierWAL raft 帧头（<see cref="TierWalRaftStore"/> 载荷帧的固定前缀，9B——
/// <c>[Term 8B LE][Kind 1B]</c>，entry content 紧随；index 由 WAL"起点 + 顺序计数"推导，
/// 帧内零 index（TierWAL §8.7）。布局知识声明式单点（生成 codec 出偏移/读写/尺寸——零手写字节序）。
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 9)]
public readonly struct RaftWalFrameHeader
{
    /// <summary>条目 term。</summary>
    [FieldOffset(0)] public readonly long Term;

    /// <summary>条目种类（<see cref="RaftEntryKind"/>）。</summary>
    [FieldOffset(8)] public readonly byte Kind;

    /// <summary>构造（参数序 = 偏移序——生成整体 Read 的契约形态）。</summary>
    /// <param name="term">条目 term。</param>
    /// <param name="kind">条目种类。</param>
    public RaftWalFrameHeader(long term, byte kind)
    {
        Term = term;
        Kind = kind;
    }
}

/// <summary>
/// TierWAL raft 元数据 opaque 槽（<see cref="TierWalRaftStore"/> 原子替换写，41B——
/// <c>[ver 1B][term 8B][votedFor 16B][applied 8B][snapshotIndex 8B]</c>；votedFor 委托
/// <see cref="NodeIdCodec"/>）。布局知识声明式单点（生成 codec 出偏移/读写/尺寸——零手写字节序）。
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 41)]
public readonly struct RaftStoreMeta
{
    /// <summary>元数据格式版本（现役 1——前向兼容门归 <see cref="TierWalRaftStore"/> 消费面）。</summary>
    [FieldOffset(0)] public readonly byte Version;

    /// <summary>当前任期。</summary>
    [FieldOffset(1)] public readonly long Term;

    /// <summary>本届投票对象。</summary>
    [FieldOffset(9)] public readonly NodeId VotedFor;

    /// <summary>已应用水位。</summary>
    [FieldOffset(25)] public readonly long AppliedIndex;

    /// <summary>快照覆盖点 N₀。</summary>
    [FieldOffset(33)] public readonly long SnapshotIndex;

    /// <summary>构造（参数序 = 偏移序——生成整体 Read 的契约形态）。</summary>
    /// <param name="version">元数据格式版本。</param>
    /// <param name="term">当前任期。</param>
    /// <param name="votedFor">本届投票对象。</param>
    /// <param name="appliedIndex">已应用水位。</param>
    /// <param name="snapshotIndex">快照覆盖点 N₀。</param>
    public RaftStoreMeta(byte version, long term, NodeId votedFor, long appliedIndex, long snapshotIndex)
    {
        Version = version;
        Term = term;
        VotedFor = votedFor;
        AppliedIndex = appliedIndex;
        SnapshotIndex = snapshotIndex;
    }
}
