using TC.Tier.Core.Shared;
using TC.Tier.Contracts.Structures;

namespace TC.Tier.Runtime.Structures.ProbingIndex;

/// <summary>
/// ProbingIndexBase 恢复 partial（DefaultProbingIndexBaseRecovery——RecoveryBase 模板派生，不裸写 IRecovery：
/// 底层生命周期骨架是压测背书的信任边界，结构层只填钩子）。
/// <para>★ 自建恢复（设计稿 §4）：索引=派生数据，重建数据面 = 真相源 record 流——
///   建空结构后拉 <see cref="IKeyResolver{TKey}"/>.ScanAsync 窗口流逐条 Insert 自填桶
///   （复用公开 Insert：判等闭环/tag 冲突/同 key 覆盖语义原样生效，流序折叠 = 最新写胜出）。</para>
/// <para>★ 三级回退（统一生命周期——主存储载入归本 partial 中间级）：hints（调用方最强知识）
///   → 主存储最新完整帧（载入物化 + ring 裁决清理 + 重放 (W, End]）→ 建空结构 + Ring 全量重放兜底。</para>
/// </summary>
public abstract partial class ProbingIndexBase<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>建空索引结构（建空表/head 节点）——恢复核心第一步（主存储不可用时）。</summary>
    protected virtual void InitializeIndex() { }

    /// <summary>
    /// 默认恢复实现（模板派生——只填恢复算法）。
    /// </summary>
    private protected class DefaultProbingIndexBaseRecovery(ProbingIndexBase<TKey> owner) : RecoveryBase<ProbingIndexRecoveryHints>
    {
        /// <summary>层间 join——主引擎异步就绪（OnInitializeBegin 已启动；主存储帧读经主引擎）。</summary>
        protected override async ValueTask WaitForDependenciesAsync(CancellationToken ct)
        {
            await owner._engine.WaitForReadyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>★ 恢复核心（模板唯一必 override）——先试主存储（帧有效=增量重放），否则建空结构+全量重放。</summary>
        protected override async ValueTask OnRecoveryCoreAsync(ProbingIndexRecoveryHints hints, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // ★ 主存储加速：最新完整帧有效且 W∈[Begin,End] → 载帧物化 + 只重放 (W, End)；
            //   无帧/损坏/W 越界 → fail-safe 建空结构 + 全量重放（W=Begin 同一条路）
            LogicalAddress replayFrom = hints.HasReplayWindow ? hints.Begin : LogicalAddress.Invalid;
            bool applied = false;
            if (hints.HasReplayWindow && owner.TryApplyMainStorage(hints.Begin, hints.End, out var effectiveW))
            {
                applied = true;
                replayFrom = effectiveW;   // ★ 仅成功才取 out 值——失败保留 Begin（fail-safe 全量窗口）
            }
            if (!applied)
            {
                RaiseProgress(30, "InitializeIndex");
                owner.InitializeIndex();
            }
            owner.MainStorageAppliedLastRecovery = applied;

            if (hints.HasReplayWindow)
            {
                RaiseProgress(50, applied
                    ? $"Main-storage replay [{replayFrom}, {hints.End})"
                    : $"Replay entries [{hints.Begin}, {hints.End})");
                // begin 传 Empty（最小地址）：重放不做旧条目抑制——同 key 多版本靠流序覆盖（最新写胜出）
                await foreach (var (key, addr) in owner.KeyResolver.ScanAsync(replayFrom, hints.End, ct)
                                   .ConfigureAwait(false))
                {
                    owner.Insert(key, addr, LogicalAddress.Empty);
                }
                ct.ThrowIfCancellationRequested();
            }
        }
    }
}
