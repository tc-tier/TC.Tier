namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 冷启动重建协调器（spec-05 §5 定案——日志即状态机：重建 = 重放）。
/// <para>★ 存储经 <see cref="IRaftStore"/> 端口（spec-12 §8.1）——★ 双源读（spec-03 §7 快照区
/// 重放归位）：跨界区间 [from..N₀] 经快照读面（快照 = 可导出的日志前缀镜像），(N₀..to] 经
/// 主数据读面——applied &lt; N₀ 的重启/快照安装场景由本协调器一次补齐，业务状态不依赖
/// 快照面之外的镜像语义。</para>
/// <para>★ 配置条目在重放中按序 apply（spec-04 §6 配置恢复——配置与成员关系全部由 apply 管道产物决定，
/// 无独立配置持久化面）；判别 = 条目结构字段 Kind（<see cref="RaftEntryKind"/>——wire v3 信封之死）。</para>
/// </summary>
public static class RebuildCoordinator
{
    /// <summary>
    /// 应用已提交区间 [from..to]（双源读：≤ N₀ 经快照读面、&gt; N₀ 经主数据读面——一次调用读完整跨界区间）。
    /// <para>调用方保证 from &gt; applied、to ≤ commit（单调推进区间——严格与日志序一致）。</para>
    /// </summary>
    /// <param name="store">存储端口（双源读：快照读面 + 主数据读面）。</param>
    /// <param name="machine">业务状态机（按序 ApplyAsync）。</param>
    /// <param name="from">区间起始 index（须 &gt; applied）。</param>
    /// <param name="to">区间终止 index（须 ≤ commit）。</param>
    /// <param name="onConfigChanged">配置条目 apply 回调（→ 状态机/管道——spec-04）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际应用到的最后 index（区间为空 = from-1）。</returns>
    public static async ValueTask<long> ApplyCommittedRangeAsync(
        IRaftStore store, IStateMachine machine, long from, long to,
        Action<ClusterConfig> onConfigChanged, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(onConfigChanged);

        var applied = from - 1;
        var n0 = store.SnapshotIndex;
        if (from <= n0)
        {
            var toSnap = Math.Min(to, n0);
            await foreach (var e in store.ReadSnapshotEntriesAsync(n0, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (e.Index < from) continue;
                if (e.Index > toSnap) break;
                await ApplyEntryAsync(machine, onConfigChanged, e.Index, e.Kind, e.Content, cancellationToken).ConfigureAwait(false);
                applied = e.Index;
            }
        }
        if (to > n0)
        {
            var start = Math.Max(from, n0 + 1);
            await foreach (var e in store.ReadEntriesAsync(start, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (e.Index > to) break;
                await ApplyEntryAsync(machine, onConfigChanged, e.Index, e.Kind, e.Content, cancellationToken).ConfigureAwait(false);
                applied = e.Index;
            }
        }
        return applied;
    }

    /// <summary>单条应用（Kind 判别分流——配置/命令/锚点空条目；kind 未知 = 日志损坏形态抛）。</summary>
    private static ValueTask ApplyEntryAsync(IStateMachine machine, Action<ClusterConfig> onConfigChanged,
        long index, byte kind, ReadOnlyMemory<byte> content, CancellationToken cancellationToken=default)
    {
        if (kind == RaftEntryKind.Config)
        {
            onConfigChanged(ClusterConfig.Deserialize(content.Span));
            return ValueTask.CompletedTask;
        }
        if (kind == RaftEntryKind.Noop)
            return ValueTask.CompletedTask;   // ★ 二期-B：leader 任期锚点——仅推进水位，不达业务状态机
        if (kind != RaftEntryKind.Command)
            throw new FormatException($"日志条目 {index} 种类未知：{kind}（日志损坏形态）。");
        return machine.ApplyAsync(index, content, cancellationToken);
    }

    /// <summary>
    /// 重建业务状态（幂等——重复调用重新重放，业务 at-least-once 契约容忍）。
    /// </summary>
    /// <param name="store">存储端口（日志读取 + appliedIndex 落盘）。</param>
    /// <param name="machine">业务状态机（ApplyAsync 重放）。</param>
    /// <param name="onConfigChanged">配置条目 apply 回调（→ 状态机/管道——spec-04）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>重建后的 appliedIndex（此后 ApplyPipeline 从此 +1 接管）。</returns>
    public static async ValueTask<long> RebuildAsync(IRaftStore store, IStateMachine machine,
        Action<ClusterConfig> onConfigChanged, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(onConfigChanged);

        var applied = store.AppliedIndex;
        var tail = store.PersistedIndex;

        // 增量重建 [applied+1 .. PersistedIndex]（双源读——applied < N₀ 的跨界区间由快照读面补齐）
        if (applied < tail)
        {
            applied = await ApplyCommittedRangeAsync(store, machine, applied + 1, tail, onConfigChanged, cancellationToken)
                .ConfigureAwait(false);
        }

        await store.UpdateAppliedIndexAsync(applied, cancellationToken).ConfigureAwait(false);
        return applied;
    }
}
