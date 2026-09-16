using System.Buffers.Binary;
using TC.Tier.Core.Net.Raft;

namespace TC.Tier.Core.Net.Coordination;

/// <summary>
/// 跨组事务决策注入器（二期-G3——NETGAP-026）：把 raft 组共识接到复制事务的
/// 决策注入位（<c>TierSession.CommitReplicatedAsync</c> 的 <c>awaitDecision</c>——Phase 4 对接）。
/// <para>★ 决策 = 协调组上的一条共识命令（<c>[Kind 1B][TxSeq 8B][CtxLen 4B][Ctx]</c>）：
/// 经 <see cref="RaftStateMachine.ReplicateAsync"/>（applied 档——多数派落盘且本机已应用）
/// 返回即决策定案，decider 返回 true，Confirm-all（不可回退点）随之推进；换届/取消
/// 原样上抛——回滚路径归事务管线（Abort 已 Prepare 者）。</para>
/// <para>★ 原子性锚点：参与效果在协调组 <see cref="IStateMachine.ApplyAsync"/> 回调内
/// 随决策可见性落（决策 applied 即效果可复制）——决策未见，效果不存在。</para>
/// <para>★ 零依赖装配：decider 为纯 BCL 委托形态，本类型不引用事务运行时——由装配层
/// 把两者接上（TierSession 所在工程不反向引用 Core.Net）。</para>
/// </summary>
public sealed class RaftDecisionInjector
{
    /// <summary>决策记录种类：提交决定（当前唯一——回滚走异常路径，不落记录）。</summary>
    public const byte RecordKindCommit = 0x01;

    /// <summary>决策记录定长头：<c>[Kind 1B][TxSeq 8B][CtxLen 4B]</c>。</summary>
    public const int HeaderBytes = 13;

    private readonly RaftStateMachine _coordinator;
    private readonly int _leadershipRetryCount;
    private readonly TimeSpan _leadershipRetryDelay;

    /// <param name="coordinator">协调组引擎（任意成员——非 leader 时注入器内部有界重试）。</param>
    /// <param name="leadershipRetryCount">换届重试上限（超过上抛 NotLeaderException——决策超时）。</param>
    /// <param name="leadershipRetryDelay">换届重试间隔。</param>
    public RaftDecisionInjector(RaftStateMachine coordinator, int leadershipRetryCount = 60,
        TimeSpan? leadershipRetryDelay = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _leadershipRetryCount = leadershipRetryCount;
        _leadershipRetryDelay = leadershipRetryDelay ?? TimeSpan.FromMilliseconds(50);
    }

    /// <summary>编码决策记录（应用侧按 <see cref="DecodeDecision"/> 对称解码）。</summary>
    /// <param name="txSeq">事务序号（决策归属的复制事务）。</param>
    /// <param name="context">随决策定案的上下文（整段拷入记录——应用侧解码后解释）。</param>
    /// <returns>决策记录字节（<c>[Kind 1B][TxSeq 8B][CtxLen 4B][Ctx]</c>——小端编码）。</returns>
    public static byte[] EncodeDecision(long txSeq, ReadOnlySpan<byte> context)
    {
        var record = new byte[HeaderBytes + context.Length];
        record[0] = RecordKindCommit;
        BinaryPrimitives.WriteInt64LittleEndian(record.AsSpan(1, 8), txSeq);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(9, 4), context.Length);
        context.CopyTo(record.AsSpan(HeaderBytes));
        return record;
    }

    /// <summary>解码决策记录（格式非法抛 <see cref="FormatException"/>——静默容错会吞掉决策）。</summary>
    public static (long TxSeq, byte[] Context) DecodeDecision(ReadOnlySpan<byte> record)
    {
        if (record.Length < HeaderBytes)
            throw new FormatException($"决策记录截断：len={record.Length} < 头 {HeaderBytes}。");
        if (record[0] != RecordKindCommit)
            throw new FormatException($"决策记录种类未知：kind=0x{record[0]:X2}。");
        var txSeq = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(1, 8));
        var ctxLen = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(9, 4));
        if (ctxLen < 0 || HeaderBytes + ctxLen != record.Length)
            throw new FormatException($"决策记录长度失配：ctxLen={ctxLen} 总长={record.Length}。");
        return (txSeq, record.Slice(HeaderBytes, ctxLen).ToArray());
    }

    /// <summary>
    /// 构造决策函数（<c>awaitDecision(candidate, ct)</c> 形态——直接传给复制回合入口）。
    /// </summary>
    /// <param name="context">随决策定案的上下文（应用侧解码后解释——如参与组清单/效果载荷）。</param>
    /// <returns>决策委托 <c>(txSeq, ct) → ValueTask&lt;bool&gt;</c>：正常完成即决策定案（恒 true——applied 档多数派落盘且已应用）；
    /// 换届重试耗尽上抛 <see cref="NotLeaderException"/>、取消上抛 <see cref="OperationCanceledException"/>。</returns>
    public Func<long, CancellationToken, ValueTask<bool>> CreateDecider(ReadOnlyMemory<byte> context)
        => (txSeq, ct) => DecideAsync(txSeq, context.ToArray(), ct);

    private async ValueTask<bool> DecideAsync(long txSeq, byte[] context, CancellationToken ct)
    {
        var record = EncodeDecision(txSeq, context);
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // applied 档：返回 = 多数派落盘且协调组状态机已应用——决策可见
                await _coordinator.ReplicateAsync(record, ct).ConfigureAwait(false);
                return true;
            }
            catch (NotLeaderException) when (attempt < _leadershipRetryCount)
            {
                await Task.Delay(_leadershipRetryDelay, ct).ConfigureAwait(false);
            }
        }
    }
}
