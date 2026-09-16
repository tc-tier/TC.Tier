namespace TC.Tier.Runtime.Structures.Log.Contracts;

/// <summary>
/// Log 扫描游标接口——继承 Shared <see cref="IStructureScanCursor"/> 统一骨架，扩展 Log 专属 entry 几何成员。
/// <para>★ 单接口，同步/异步共存（<see cref="IStructureScanCursor.MoveNext"/> 同步 + <see cref="IStructureScanCursor.MoveNextAsync"/> 异步）。</para>
/// <para>★ 去泛型：CurrentEntryType(TEntryType) → CurrentIsMeta(bool)。Log 只区分"数据 vs meta"两种。</para>
/// </summary>
public interface ILogCursor : IStructureScanCursor
{
    /// <summary>当前 entry 起始地址（LogicalAddress）。MoveNext 成功后有效。</summary>
    LogicalAddress CurrentAddress { get; }

    /// <summary>扫描终点（TailAddress 或 CommittedOffset 快照）。</summary>
    LogicalAddress EndAddress { get; }

    /// <summary>当前 entry payload 起始页内偏移（纯内存缓冲概念，已跨过 header）。MoveNext 成功后有效。</summary>
    long CurrentPayloadStart { get; }

    /// <summary>
    /// ★ 当前 entry payload（指向读帧内的 Span，已跨过 header，长度 = <see cref="CurrentEntryLength"/>）。
    /// <para>MoveNext 成功后有效。零拷贝读取——禁止跨 MoveNext 持有。</para>
    /// </summary>
    ReadOnlySpan<byte> CurrentPayload { get; }

    /// <summary>
    /// ★ 当前 entry payload 的 Memory 视图（零拷贝——指向游标页缓冲，同 <see cref="CurrentPayload"/>）。
    /// <para>生命周期契约：<b>非 lease 游标</b>禁跨 MoveNext/Dispose 持有（换页即还池）；
    /// <b>lease 游标</b>（<see cref="LogBase.OpenCursor"/> leasePages=true）有效至游标 Dispose
    /// （换页不还池——游标持有全部已读页）。</para>
    /// </summary>
    ReadOnlyMemory<byte> CurrentPayloadMemory { get; }

    /// <summary>当前 entry payload 字节数。MoveNext 成功后有效。</summary>
    int CurrentEntryLength { get; }

    /// <summary>★ 当前 entry 是否为 meta/commit 标记。取代旧的 CurrentEntryType。</summary>
    bool CurrentIsMeta { get; }
}
