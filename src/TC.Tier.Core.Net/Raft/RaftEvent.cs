using TC.Tier.Core.Net.Channels;

namespace TC.Tier.Core.Net.Raft;

/// <summary>状态机循环事件（spec-01 §6.1 逻辑消息 + 循环内部事件——进程内零编码）。
/// <para>★ 全家族 class（热路径事件池化需要可变对象；事件按引用消息使用，无相等语义依赖）。</para>
/// <para>★ <see cref="Rpc.Reply"/>（spec-12 §5.2 请求回调形态）：入站请求事件携带应答上下文——
/// 循环处理完成后经其回写应答（对端 CorrId 由传输配对）；出站请求的应答回投事件 Reply = null。</para></summary>
internal abstract class RaftEvent
{
    /// <summary>入站 RPC（请求回调 handler → 状态机循环 Channel 投递；应答经 <see cref="Reply"/> 异步回写）。</summary>
    /// <param name="from">来源节点 ID。</param>
    /// <param name="message">RPC 消息（请求或应答——出站应答回投时 Reply=null）。</param>
    /// <param name="reply">应答上下文（入站请求时持有——循环处理完成后回写应答；出站请求的应答回投 = null）。</param>
    public sealed class Rpc(NodeId from, RaftRpc message, IReplyContext? reply) : RaftEvent
    {
        public readonly NodeId From = from;
        public readonly RaftRpc Message = message;
        public readonly IReplyContext? Reply = reply;   // null = 出站请求的应答回投（无需应答）
    }

    /// <summary>选举超时到期（Follower/PreCandidate/Candidate 持有；到期后重掷——spec-01 §4.1 规则 4）。</summary>
    public sealed class ElectionTimeout : RaftEvent;

    /// <summary>心跳 tick（Leader 持有——周期复制/心跳，spec-02 §6）。</summary>
    public sealed class HeartbeatTick : RaftEvent;

    /// <summary>停止请求（循环退出后完成 TCS）。</summary>
    /// <param name="done">循环退出完成信号（finally 完成清理后置位）。</param>
    public sealed class Stop(TaskCompletionSource done) : RaftEvent
    {
        public readonly TaskCompletionSource Done = done;
    }

    /// <summary>★ 周期唤醒 tick（仅唤醒循环做 deadline 检查——事件密集时丢弃无妨）。</summary>
    public sealed class Tick : RaftEvent;

    /// <summary>★ 组提交完工唤醒（W4 persist 下环——在途提交任务完工自投；仅唤醒批尾观察收尾，
    /// 事件密集时丢弃无妨——完成感知另有每事件观察兜底）。</summary>
    public sealed class PersistCompleted : RaftEvent;

    /// <summary>★ 用户 ReplicateAsync 入队（仅 Leader 处理——非 Leader 立即 NotLeaderException）。
    /// <para>★ 完成源双路径：Slot（池化完成源——热路径零分配）/ Done（TCS——取消语义/窗口打满兜底）。</para>
    /// <para>★ 完成档（<see cref="Committed"/>）：TCS 路径的档位载体（Slot 路径档位在
    /// <see cref="InFlightSlot.CommittedOnly"/>——true = committed 档）。</para>
    /// <para>★ 事件对象池化（热路径零新分配）。</para></summary>
    public sealed class Replicate : RaftEvent
    {
        private static readonly System.Collections.Concurrent.ConcurrentBag<Replicate> Pool = new();

        public ReadOnlyMemory<byte> Command;
        public InFlightSlot? Slot;
        public TaskCompletionSource<long>? Done;
        public bool Committed;    // TCS 路径完成档（true = committed——Slot 路径不读此字段）
        public bool LeaderLocal;  // TCS 路径完成档（true = 本地持久化即返——优先于 Committed 判定）

        private Replicate() { }

        /// <summary>租用（池空才分配——稳态零新分配）。</summary>
        /// <param name="command">命令内容（帧 payload——业务方定义格式）。</param>
        /// <param name="slot">池化完成源槽（热路径零分配）；null = TCS 直通路径。</param>
        /// <param name="done">TCS 完成源（取消语义/窗口打满兜底）；slot 非 null 时 = null。</param>
        /// <param name="committed">TCS 路径完成档（true = committed——多数派提交即完成，Slot 路径不读此字段）。</param>
        /// <param name="leaderLocal">TCS 路径完成档（true = 本地持久化即返——优先于 committed 判定）。</param>
        /// <returns>从对象池租用的 Replicate 实例（处理完须调 <see cref="Return"/>）。</returns>
        public static Replicate Rent(ReadOnlyMemory<byte> command, InFlightSlot? slot, TaskCompletionSource<long>? done, bool committed = false, bool leaderLocal = false)
        {
            if (!Pool.TryTake(out var r)) r = new Replicate();
            r.Command = command;
            r.Slot = slot;
            r.Done = done;
            r.Committed = committed;
            r.LeaderLocal = leaderLocal;
            return r;
        }

        /// <summary>归还（循环线程——事件处理完成后；字段清空防滞留引用）。</summary>
        public void Return()
        {
            Command = default;
            Slot = null;
            Done = null;
            Committed = false;
            LeaderLocal = false;
            Pool.Add(this);
        }
    }

    /// <summary>批复制入队（spec-08 §1——一批一 tcs，批尾 index 返回）。</summary>
    /// <param name="commands">命令批（顺序追加——批尾 index 即完成位）。</param>
    /// <param name="done">批复制完成源（批尾 committed 且 applied 后置位）。</param>
    public sealed class ReplicateBatch(IReadOnlyList<ReadOnlyMemory<byte>> commands, TaskCompletionSource<long> done)
        : RaftEvent
    {
        public readonly IReadOnlyList<ReadOnlyMemory<byte>> Commands = commands;
        public readonly TaskCompletionSource<long> Done = done;
    }

    /// <summary>线性读请求（spec-08 §4——leader 向多数派确认身份后返回 readIndex）。</summary>
    /// <param name="done">读位点完成源（多数派确认后置位 commitIndex）。</param>
    public sealed class ReadIndex(TaskCompletionSource<long> done) : RaftEvent
    {
        public readonly TaskCompletionSource<long> Done = done;
    }

    /// <summary>配置条目提案（spec-04——与 Replicate 同构：追加 isConfig 条目 + 等 committed 且 applied）。</summary>
    /// <param name="config">提案的新配置（追加为 Kind=Config 条目）。</param>
    /// <param name="done">配置条目完成源（committed 且 applied——配置已切换——置位）。</param>
    public sealed class ProposeConfig(ClusterConfig config, TaskCompletionSource<long> done) : RaftEvent
    {
        public readonly ClusterConfig Config = config;
        public readonly TaskCompletionSource<long> Done = done;
    }

    /// <summary>活动配置切换（apply 产物——spec-04：配置与状态机同构；经 apply 管道回调入队）。</summary>
    /// <param name="config">切换后的活动配置（apply 产物）。</param>
    public sealed class ConfigChanged(ClusterConfig config) : RaftEvent
    {
        public readonly ClusterConfig Config = config;
    }

    /// <summary>learner 晋级提案（JoinReq.AutoPromote 登记 + 复制应答判追平——lane 线程投递，
    /// 循环线程以新鲜配置出提案，杜绝并发提案丢更新）。</summary>
    /// <param name="member">要晋级的 learner 节点 ID。</param>
    public sealed class PromoteLearner(NodeId member) : RaftEvent
    {
        public readonly NodeId Member = member;
    }
}
