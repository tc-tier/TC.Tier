namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 集群成员角色（spec-12 §6 增量——复制家族设计件 B：raft learner）。
/// </summary>
public enum ClusterMemberRole
{
    /// <summary>投票成员（缺省）：参与选举授权、计入多数派。</summary>
    Voter,

    /// <summary>学习者（raft 论文 §7）：日志照常复制/快照安装全路径，但不参与选举投票、
    /// 不计入多数派——读扩容/容灾异步副本/观察者（加副本不动写入可用性）。</summary>
    Learner,

    /// <summary>见证者（二期-F2——DDR-F2）：投票成员（计入选主与提交多数派）但不存全量
    /// 日志体（只持久化高水位对 LastIndex/LastTerm——索引断言流）、无状态机、不自荐。
    /// 第三票防脑裂省全量副本（MongoDB arbitrator / PolarDB witness 同位）。</summary>
    Witness,
}
