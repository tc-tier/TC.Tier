namespace TC.Tier.Runtime.Structures.Ring.Contracts;

/// <summary>
/// Ring 扫描游标接口——继承 Shared <see cref="IStructureScanCursor"/>，扩展 Ring 专属地址/header 成员。
/// <para>★ 全 LogicalAddress（base.md §2.2）。</para>
/// </summary>
public interface IRingScanCursor : IStructureScanCursor
{
    /// <summary>当前 record 的起始地址（MoveNext 推进成功后即刚交出的那条）。</summary>
    LogicalAddress CurrentAddress { get; }
    /// <summary>下一待解析地址（解析循环工作游标——跳过 meta/无效 header/跨页时推进）。</summary>
    LogicalAddress NextAddress { get; }
    /// <summary>扫描起点（已夹取到 BeginAddress）。</summary>
    LogicalAddress BeginAddress { get; }
    /// <summary>扫描终点（开区间，不含）。</summary>
    LogicalAddress EndAddress { get; }
    /// <summary>读当前 record 的 header 字段（热区直读 native 页，冷区从读帧读）。</summary>
    /// <returns>当前 record 的 header 字段（Flags/PayloadLength/PaddingLength/PreviousAddress）。</returns>
    RingRecordFields GetFields();
    /// <summary>当前 record 对齐后的占用字节数（header + payload + padding 向上取整到 codec 对齐粒度）。</summary>
    int CurrentRecordSize { get; }
}
