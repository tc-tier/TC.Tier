namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 日志条目种类（wire/storage 稳定值——kind 升格为条目结构字段后在此单点声明；
/// 旧 <c>[Kind 1B][内容]</c> 引擎内信封格式已消灭——条目三元组
/// <c>(Term, Kind, Content)</c> 的 Kind 即本常量，Content 恒为去信封原字节）。
/// </summary>
public static class RaftEntryKind
{
    /// <summary>用户命令条目（Content = 原命令字节——<see cref="IStateMachine.ApplyAsync"/> 收到的恒为原样命令）。</summary>
    public const byte Command = 0;

    /// <summary>配置条目（Content = <see cref="ClusterConfig"/>.Serialize() 稳定格式）。</summary>
    public const byte Config = 1;

    /// <summary>leader 上任空条目（二期-B——任期提交锚点：commit 计数只认本任期条目，新 leader
    /// 不追加锚点则 commitIndex 停在前任期水位，ReadIndex 返回过期位点 = 线性读破约；
    /// Content 恒空——apply 跳过不达业务状态机，仅推进水位）。</summary>
    public const byte Noop = 2;
}
