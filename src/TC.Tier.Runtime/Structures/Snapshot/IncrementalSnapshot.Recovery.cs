using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.Snapshot;

/// <summary>恢复 partial——IncrementalRecovery 嵌套类（<see cref="RecoveryBase{THints}"/> 模板派生）。</summary>
public sealed partial class IncrementalSnapshot
{
    /// <summary>
    /// 增量快照恢复核心：join 引擎 → meta O(1) 水位 + opaque 段表 + 悬干裁决。
    /// <para>★ 会话模式一致性（底层 2PC）：段写 = Prepare → Confirm——崩溃在两者之间 = 悬干段
    ///   （meta prepared &gt; committed）→ 恢复尾截断回滚到提交点（失败即清理——未提交段物理清除，
    ///   已提交段完好）；无"自动认领未提交段"（孤儿转正作废——未提交 = 失败 = 清理）。</para>
    /// </summary>
    private sealed class IncrementalRecovery(SnapshotBase owner) : RecoveryBase<SnapshotRecoveryHints>
    {
        private readonly IncrementalSnapshot _self = (IncrementalSnapshot)owner;

        /// <summary>层间 join——主引擎 + meta 引擎（Managed 模式）双 await（OnInitializeBegin 已并行启动）。</summary>
        protected override async ValueTask WaitForDependenciesAsync(CancellationToken ct)
        {
            await _self._engine.WaitForReadyAsync(ct).ConfigureAwait(false);
            if (_self.MetaEngine is { } metaEngine)
                await metaEngine.WaitForReadyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 恢复核心：
        /// <para>1. meta 三水位（hints → meta O(1) → Backward 扫描兜底——同 DefaultSnapshotRecovery）；</para>
        /// <para>2. ★ 悬干裁决：prepared &gt; committed = 段写崩溃（Prepare 后 Confirm 前）→
        ///   尾截断回滚到提交点（未提交段物理清除——失败即清理）；</para>
        /// <para>3. opaque 段表解析（O(1) 段起点列表——恢复免全盘扫描；仅含已提交段）。</para>
        /// </summary>
        protected override async ValueTask OnRecoveryCoreAsync(SnapshotRecoveryHints hints, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            SnapshotMetaPayload? metaPayload = null;
            if (_self._settings.MetaPolicyKind != MetaPolicyKind.Disabled)
            {
                await _self.MetaPolicy.LoadAsync(ct).ConfigureAwait(false);
                metaPayload = _self.MetaPolicy.ReadMetaPayload();
            }
            ct.ThrowIfCancellationRequested();

            // ── 1. 三级回退恢复三水位（hints → meta → Backward 扫描）──
            if (hints.WriteAddress is { } hWrite)
            {
                _self._writeAddress = hWrite;
                _self._physicalWriteAddress = hints.PhysicalWriteAddress ?? _self.AlignUpToSector(hWrite);
                RaiseProgress(50, $"hints write={hWrite}");
            }
            else if (metaPayload is { } p)
            {
                _self._writeAddress = p.WriteAddress;
                _self._physicalWriteAddress = p.PhysicalWriteAddress;
                _self._truncatedAddress = p.TruncatedAddress;
                _self._committedWriteAddress = p.CommittedWriteAddress;
                _self._lastCommittedSeq = p.LastCommittedSeq;
                _self._lastPreparedSeq = p.LastPreparedSeq;
                RaiseProgress(50, "meta watermarks");
            }
            else
            {
                RaiseProgress(50, "backward scan frame end");
                if (_self.LocateLastFrameEnd() is { } frameEnd)
                {
                    _self._writeAddress = frameEnd;
                    _self._physicalWriteAddress = _self.AlignUpToSector(frameEnd);
                }
            }

            // ── 2. ★ 悬干裁决（段写崩溃——失败即清理）：尾截断回滚到提交点
            //    （未提交段物理清除；已提交段表/数据完好——raft 重试快照/安装即可）
            if (metaPayload is { } mp && mp.LastPreparedSeq > mp.LastCommittedSeq)
            {
                RaiseProgress(60, "truncating dangling segment");
                _self.TruncateSuffix(mp.CommittedWriteAddress);
                _self._lastPreparedSeq = mp.LastCommittedSeq;
            }
            ct.ThrowIfCancellationRequested();

            // ── 3. opaque 段表（O(1) 段起点——仅已提交段；恢复免全盘扫描）──
            var segments = DeserializeSegments(_self.ReadOpaqueMeta());
            lock (_self._segmentLock)
            {
                _self._segments.Clear();
                _self._segments.AddRange(segments);
                _self._latestN0 = segments.Count > 0 ? segments[^1].N0 : 0;
            }

            // ★ 2PC 事务序号续接（Confirm 须 > 已提交——段写事务单调）
            _self._nextSeq = _self._lastCommittedSeq + 1;

            _self._writeWindow = _self._engine.GetDistance(
                _self._physicalWriteAddress, _self._engine.AllocatedTail);

            RaiseProgress(90, $"segments={_self.SegmentCount} latestN0={_self.LatestN0} tail={_self._writeAddress}");
        }
    }
}
