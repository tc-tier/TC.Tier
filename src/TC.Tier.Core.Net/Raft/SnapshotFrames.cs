using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 快照传输帧（spec-03/T5 快照流式承载——0x04 协议域会话内的帧格式，Core.Net 自有声明式布局）。
/// <para>★ 流结构：首帧 <see cref="SnapshotPreamble"/>（8B 快照覆盖点 N₀——流内自描述，
/// <see cref="ISnapshotTransfer"/> 契约无 index 参数）→ 后续每帧一个快照条目
/// （<see cref="SnapshotEntryHeader"/> v2 + payload——(index, term, kind, content) 四元组上线）。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 8)]
public readonly struct SnapshotPreamble
{
    /// <summary>快照覆盖点 N₀（导入后 SnapshotIndex = 此值）。</summary>
    [FieldOffset(0)] public readonly long SnapshotIndex;

    /// <summary>构造。</summary>
    /// <param name="snapshotIndex">快照覆盖点 N₀（导入后 SnapshotIndex = 此值）。</param>
    public SnapshotPreamble(long snapshotIndex)
    {
        SnapshotIndex = snapshotIndex;
    }
}

/// <summary>快照条目帧头（v2——21B：[Index 8B][Term 8B][Kind 1B][PayloadLength 4B]，payload 紧随；
/// v1 20B 无 Kind——信封之死后 kind 升格条目结构字段，快照流同构升格；内部协议未发布无共存）。</summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 21)]
public readonly struct SnapshotEntryHeader
{
    /// <summary>条目 index。</summary>
    [FieldOffset(0)] public readonly long Index;

    /// <summary>条目 term。</summary>
    [FieldOffset(8)] public readonly long Term;

    /// <summary>条目种类（<see cref="RaftEntryKind"/>——v2 新增）。</summary>
    [FieldOffset(16)] public readonly byte Kind;

    /// <summary>payload 字节数（紧随头之后）。</summary>
    [FieldOffset(17)] public readonly int PayloadLength;

    /// <summary>构造。</summary>
    /// <param name="index">条目 index。</param>
    /// <param name="term">条目 term。</param>
    /// <param name="kind">条目种类（<see cref="RaftEntryKind"/>——v2 新增）。</param>
    /// <param name="payloadLength">payload 字节数（紧随头之后）。</param>
    public SnapshotEntryHeader(long index, long term, byte kind, int payloadLength)
    {
        Index = index;
        Term = term;
        Kind = kind;
        PayloadLength = payloadLength;
    }
}
