using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Observability;

public sealed partial class ObservabilityHub
{
    /// <summary>
    /// Raft 共识维度视图（二期-I2——第七维度，对齐 NetView 形态）：
    /// 选举/换届/任期/提交与应用水位/快照安装/转让的共识面指标。
    /// <para>★ 语义：水位 <b>gauge 直写</b>（低频状态变化时上报）；事件 <b>counter</b>；
    ///   错误/转让<b>全采</b>。Core.Net 只调本视图，绝不直调 sink。</para>
    /// </summary>
    public sealed partial class RaftView
    {
        private readonly IMetricsSink _sink;
        private readonly bool _enabled;

        internal RaftView(IMetricsSink sink, bool enabled)
        {
            _sink = sink;
            _enabled = enabled;
        }

        /// <summary>Raft 维度指标是否启用（总开关 &amp;&amp; EnableRaftMetrics 短路后的终值）。</summary>
        public bool IsEnabled => _enabled;

        /// <summary>上报发起真选举（<c>raft.elections_started</c>——预票通过后的真选举计数）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnElectionStarted()
        {
            if (!_enabled) return;
            _sink.Counter("raft.elections_started", []);
        }

        /// <summary>上报换届（<c>raft.leader_changes</c>，tag: became_leader——当选/让位全采）。</summary>
        /// <param name="becameLeader">true = 本节点当选 leader；false = 失去领导权（让位/落选）。</param>
        /// <param name="term">换届所在任期号。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnLeaderChanged(bool becameLeader, long term)
        {
            if (!_enabled) return;
            _sink.Counter("raft.leader_changes",
                [Kv("became_leader", becameLeader ? "true" : "false"), Kv("term", term.ToString())]);
        }

        /// <summary>上报提交水位（<c>raft.commit_index</c> gauge——AdvanceCommit 单调推进时）。</summary>
        /// <param name="index">新提交水位（raft log index——单调推进）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnCommitAdvanced(long index)
        {
            if (!_enabled) return;
            _sink.Gauge("raft.commit_index", index, []);
        }

        /// <summary>上报应用水位（<c>raft.applied_index</c> gauge——CompleteApplied 推进时）。</summary>
        /// <param name="index">新应用水位（raft log index——状态机已应用，单调推进）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnAppliedAdvanced(long index)
        {
            if (!_enabled) return;
            _sink.Gauge("raft.applied_index", index, []);
        }

        /// <summary>上报快照安装（<c>raft.snapshot_installs</c>，tag: n0——追平追赶面可观测）。</summary>
        /// <param name="n0">快照覆盖点（N₀——安装后本端日志基点）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnSnapshotInstalled(long n0)
        {
            if (!_enabled) return;
            _sink.Counter("raft.snapshot_installs", [Kv("n0", n0.ToString())]);
        }

        /// <summary>上报领导权转让（<c>raft.transfers</c>，tag: phase=timeout_now_sent/completed）。</summary>
        /// <param name="phase">转让阶段（timeout_now_sent = 已发送 TimeoutNow；completed = 转让完成）。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnTransfer(string phase)
        {
            if (!_enabled) return;
            _sink.Counter("raft.transfers", [Kv("phase", phase)]);
        }
    }
}
