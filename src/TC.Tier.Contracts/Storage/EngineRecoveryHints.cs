namespace TC.Tier.Contracts.Storage;

/// <summary>
/// 引擎恢复提示——恢复期对段表双尾水位的修正值（扫描/上层知识）。
/// <para>★ 只携带水位：生命周期参数（段生长上限/分段开关）<b>构造期传入引擎</b>，不经此类型
///   （构造 = 配置，启动 = 双尾）。</para>
/// <para>★ 两值可空：null = 该尾不修正（维持持久化 footer / 构造默认）。引擎恢复流程负责
///   翻译为段表的 <c>SetStartupTails(StartupParameters)</c>。</para>
/// </summary>
public readonly struct EngineRecoveryHints(
    LogicalAddress? committedTailHint = null,
    LogicalAddress? allocatedTailHint = null)
{
    /// <summary>提交尾修正（null = 不修正）。小值触发段级截断联动。</summary>
    public LogicalAddress? CommittedTailHint { get; init; } = committedTailHint;

    /// <summary>分配尾修正（null = 不修正）。</summary>
    public LogicalAddress? AllocatedTailHint { get; init; } = allocatedTailHint;
}
