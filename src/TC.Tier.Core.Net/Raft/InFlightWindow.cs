using System.Threading.Tasks.Sources;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// In-flight 完成源窗口（复制热路径零分配：预分配 N 个完成源槽池化复用，每 op 零新分配、
/// 零线程池入队——ValueTask 直连 IValueTaskSource，完成时执行续体）。
/// <para>★ 分配 = 入口线程原子取槽（Interlocked 序号 + CAS 抢槽）；注册/完成 = 状态机循环线程
/// 顺序结构（registered 列表 = index 序，前缀 consume O(1) 摊还）。绕回撞在途（窗口打满）→
/// TryAlloc 失败，调用方回退 TCS 直通路径（安全网）。</para>
/// </summary>
internal sealed class InFlightWindow
{
    private readonly InFlightSlot[] _slots;
    private readonly List<InFlightSlot> _registered = [];   // 循环线程：注册序（= index 序）
    private long _seq;

    internal InFlightWindow(int capacity)
    {
        _slots = new InFlightSlot[capacity];
        for (var i = 0; i < capacity; i++) _slots[i] = new InFlightSlot();
    }

    /// <summary>原子分配（多写者入口线程安全——Interlocked 序号 + CAS 抢槽）。
    /// 返回 null = 窗口打满/绕回撞在途——调用方回退直通路径。</summary>
    internal InFlightSlot? TryAlloc()
    {
        long seq = Interlocked.Increment(ref _seq);
        var slot = _slots[seq % _slots.Length];
        // ★ CAS 抢槽（直排多写者修复）：检查 State=0 到置 State=1 之间必须原子——
        //   旧形态两步读改写下两个 caller 可同时持同一槽（两个 ValueTask 共享一个
        //   Core——完成互相覆盖/版本 token 错乱）。CAS 成功才 Reset/写 Version。
        if (Interlocked.CompareExchange(ref slot.State, 1, 0) != 0) return null;   // 绕回未完成/被抢——回退直通
        slot.CommittedOnly = false;              // 档位归默认（上代残留清除——committed 档由入口覆写）
        slot.Core.Reset();                       // Reset 递增版本 token（防旧 await 误接下一代）
        slot.Core.RunContinuationsAsynchronously = true;   // ★ 完成线程=apply worker——同步续体会把调用方代码内联进 apply 管线（实测内联=61-64k vs 异步 88-96k：调用方 drain 串行进 apply 管线，数据否决内联形态）
        short version = slot.Core.Version;
        slot.Version = version;
        return slot;
    }

    /// <summary>注册（循环线程——append 后填 index，入注册序列表）。</summary>
    /// <param name="slot">已分配的完成源槽。</param>
    /// <param name="index">条目日志 index。</param>
    internal void Register(InFlightSlot slot, long index)
    {
        slot.Index = index;
        _registered.Add(slot);
    }

    /// <summary>完成 ≤ 水位的前缀（★ 双水位档位：<see cref="InFlightSlot.CommittedOnly"/> 槽吃
    /// commit 水位（多数派提交即完成——不等 apply），其余吃 applied 水位（read-your-writes）；
    /// commit ≥ applied 恒成立（apply 只处理已提交区间）。registered 按 index 序，摊还 O(1)：
    /// 中段完成的槽（applied 档保留挡住其后 committed 档的消费连续性）留表为死项，
    /// 下轮遍历按 State=2 消费——每项至多滞留两轮。</summary>
    /// <param name="commit">提交水位（多数派提交即完成 committed 档槽）。</param>
    /// <param name="applied">应用水位（applied 档槽须 ≤ applied 才完成）。</param>
    internal void CompleteUpTo(long commit, long applied)
    {
        int consume = 0;
        bool contiguous = true;   // 头部消费连续性（保留项打断后，其后完成项不计入前缀）
        foreach (var slot in _registered)
        {
            if (slot.Index > commit) break;
            if (Volatile.Read(ref slot.State) == 1)
            {
                if (!slot.CommittedOnly && slot.Index > applied)
                {
                    contiguous = false;   // applied 档未到水位——保留（更后的 committed 档仍可被 commit 水位完成）
                    continue;
                }
                slot.SetResult(slot.Index);
                Volatile.Write(ref slot.State, 2);
            }
            if (contiguous) consume++;
        }
        if (consume > 0) _registered.RemoveRange(0, consume);
    }

    /// <summary>停止时取消全部在途（循环退出 finally）。</summary>
    internal void CancelAll()
        => FailAll(new OperationCanceledException());

    /// <summary>以指定异常失败全部在途（★ 换届取消用 <see cref="NotLeaderException"/>——调用方
    /// 契约语义 = 重路由新 leader 重试，与快速失败路径同型；区别于停止路径的取消）。
    /// <para>★ 只遍历已注册槽（事件路径自洽）：未注册槽由循环 finally 的事件归还路径唤醒。</para></summary>
    /// <param name="ex">失败异常（换届用 NotLeaderException——调用方重路由新 leader 重试；停止路径用 OperationCanceledException）。</param>
    internal void FailAll(Exception ex)
    {
        foreach (var slot in _registered)
        {
            if (Volatile.Read(ref slot.State) == 1)
                slot.SetException(ex);
            Volatile.Write(ref slot.State, 2);
        }
        _registered.Clear();
    }

    /// <summary>
    /// 换届时失败未提交前缀的槽（★ 已提交区保留判例：已提交条目是 raft 不变量——新 leader
    /// 不可能截断（AE 处理有已提交区保护），本地 apply 管线最终必消费。若换届把"已提交
    /// 未 apply"的槽也 Failed，调用方收到 NotLeader 重试 → 同命令重复入日志（实锤判例：
    /// 快照安装测试 cold 节点追平 61/60——重复条目进快照）。保留槽由后续
    /// <see cref="CompleteUpTo"/>（apply 推进）完成。
    /// </summary>
    /// <param name="ex">失败异常（NotLeaderException）。</param>
    /// <param name="commitIndex">提交水位——≤ 此水位 = 已提交（保留）；&gt; = 未提交（失败）。</param>
    internal void FailUncommitted(Exception ex, long commitIndex)
    {
        int firstBeyond = _registered.Count;
        for (var i = 0; i < _registered.Count; i++)
        {
            var slot = _registered[i];
            if (slot.Index <= commitIndex) continue;
            if (firstBeyond > i) firstBeyond = i;   // 注册序 = index 序——首个超水位下标
            if (Volatile.Read(ref slot.State) == 1)
                slot.SetException(ex);
            Volatile.Write(ref slot.State, 2);
        }
        if (firstBeyond < _registered.Count)
            _registered.RemoveRange(firstBeyond, _registered.Count - firstBeyond);
    }
}

/// <summary>池化完成源槽（class——ValueTask 构造经接口引用零装箱；ManualResetValueTaskSourceCore
/// 内嵌管理版本 token/状态机/续体）。</summary>
internal sealed class InFlightSlot : IValueTaskSource<long>
{
    internal ManualResetValueTaskSourceCore<long> Core;   // Reset 后即用
    internal long Index;
    internal int State;      // 0=Free 1=Registered 2=Done（Volatile 跨线程）
    internal short Version;
    internal bool CommittedOnly;   // 完成档：true = committed（多数派提交即完成——不等 apply）；false = applied（默认档）

    internal void SetResult(long index) => Core.SetResult(index);
    internal void SetException(Exception ex) => Core.SetException(ex);

    /// <summary>取完成状态（IValueTaskSource 实现——版本失效 = CannotBeCanceled 语义随 Core）。</summary>
    /// <param name="token">构造 ValueTask 时下发的版本 token。</param>
    /// <returns>槽位当前完成状态（Pending/Succeeded/Faulted）。</returns>
    public ValueTaskSourceStatus GetStatus(short token) => Core.GetStatus(token);
    /// <summary>注册续体（await 挂起——IValueTaskSource 实现，转发内嵌 Core）。</summary>
    /// <param name="continuation">完成时回调的续体。</param>
    /// <param name="state">续体状态对象。</param>
    /// <param name="token">版本 token（不匹配 = 忽略）。</param>
    /// <param name="flags">续体调度标志（执行上下文/调度器捕获）。</param>
    public void OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags) => Core.OnCompleted(continuation, state, token, flags);
    /// <summary>取结果（完成位 = 复制完成 index；失败位 = 抛注册时异常）。</summary>
    /// <param name="token">版本 token。</param>
    /// <returns>复制完成 index（批尾/单条 committed 或 applied 水位）。</returns>
    public long GetResult(short token) => Core.GetResult(token);
}
