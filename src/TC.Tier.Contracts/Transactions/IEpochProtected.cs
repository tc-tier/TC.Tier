namespace TC.Tier.Contracts.Transactions;

/// <summary>
/// ★ epoch 读保护协议（Session 读 scope 的聚合入口）——结构把"epoch 进出对"提升为可发现协议，
/// 供 <c>SessionManager</c> 会话读 scope（SessionReadScope）统一进/出域内可读结构，
/// 防组合层漏进单结构 scope（零拷贝 span 生命周期护栏由各结构 epoch 保障）。
/// <para>与各结构自带 <c>EnterReadScope()</c>/<c>EnterScope()</c>（ref struct 栈护栏）语义等价、
/// 单一真源（ref struct 版转发本协议）；差异仅在形态：本接口可被非泛型侧多态聚合。</para>
/// <para>★ 保护区纪律（结构形态决定）：</para>
/// <para>① 区内只做<b>无自保护的零拷贝读</b>（如 Ring <c>GetValueSpan</c> 族——不内部持 epoch，
/// 正是聚合协议的目标消费者）；</para>
/// <para>② 区内<b>禁止</b>调用自带 epoch 进出的 API（Ring 写路径、Index/Hash 的 Find/Insert 逐次形态、
/// 逐次 <c>EnterScope</c>）——同实例重入 = LightEpoch 协议违反（Debug 绊线立即抛）；
/// 此类操作要么在保护区外做，要么用各自"scope 内单查"形态（IndexScope.Find 等）；</para>
/// <para>③ 成对调用（Enter→Exit，同线程）；同实例不可重入；跨实例可并发持有
/// （同线程对多结构各自 Enter——Session 聚合域形态）；</para>
/// <para>④ 保护区严禁跨 await/跨线程（thread-static 语义）。</para>
/// </summary>
public interface IEpochProtected
{
    /// <summary>进入 epoch 读保护（本线程）——持保护期间本结构页驱逐/回收被排水阻塞。</summary>
    void EnterEpoch();

    /// <summary>退出 epoch 读保护（与 <see cref="EnterEpoch"/> 同线程配对）。</summary>
    void ExitEpoch();
}
