namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 段生命周期稳态（byte 持久化，按值兼容——Ready 承袭原 Written 的值 1）。
/// <para>★ 两层协议映射（设计文档 lease-protocol-unified-design.md §1.3）：每层恰好一个准入态——</para>
/// <para>  Empty = 第一阶段（逻辑）准入：地址空间有效，可分配、可占区间；物理门关。</para>
/// <para>  Ready = 第二阶段（物理）准入：建段完成/恢复在案，chunk IO 合法（WaitSegmentReady 守的就是这道门）。</para>
/// <para>★ "有已提交数据"是导出属性（Ready &amp;&amp; MaxOffset &gt; 0），不是稳态——
///   池预建段/内存引擎段/恢复段出生即 Ready 且零数据。</para>
/// </summary>
public enum StableState : byte
{
    /// <summary>
    /// 第一阶段准入——段对象在表、地址空间有效（占位/建段中）。可分配、可占区间；
    /// 物理门关：不接受 chunk IO。唯一入口 <c>AppendSegmentRaw</c>；
    /// 唯一出口 <c>CreateSegmentCallback(success) → MarkReady</c>。
    /// </summary>
    Empty,

    /// <summary>第二阶段准入——物理就绪（建段完成/恢复在案），可接受 chunk IO 与段内水位推进。最常见稳态。</summary>
    Ready,

    /// <summary>写满（MaxOffset ≥ GrowthLimit），不可扩容但仍可覆写。AdvanceOffset 到顶自动从 Ready 流转。</summary>
    Full,

    /// <summary>碎片整理中（Compact 期间，秒级到分钟级）。持 _lockWord 排他，不接受新写入。</summary>
    Compacting,

    /// <summary>建段失败，永久拒绝后续操作（不重试）。物理门永久关。</summary>
    Broken,

    /// <summary>已删除（Compact/ReclaimHead 回收，文件已不存在）。第一阶段准入吊销。</summary>
    Invalid,
}
