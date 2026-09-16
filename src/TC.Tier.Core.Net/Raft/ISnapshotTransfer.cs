namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 快照传输协调器（spec-03 §7——协议层实现网络版传输面注入存储：导出/导入经注入面开会话；
/// 目标绑定 = 装配层内部细节——每 follower 一次调用 = 一个新会话）。
/// <para>★ 装配（Builder/测试组合根）实现：设置当前目标（写/读会话路由）→ 调存储适配器
/// ExportSnapshotAsync/ImportSnapshotAsync。状态机只依赖本接口——传输形态零耦合。</para>
/// <para>★ 握手协调面（spec-12 §6.1）：导出返回 <see cref="SnapshotCoordination"/>（swarm 形态
/// 的 manifest+holders——由状态机装入 InstallSnapshot RPC 握手面随行）；null = 单源流式形态
/// （数据面自带一切，RPC 协调字段零值）。导入对称消费协调数据。两形态不可混装（集群装配
/// 一致——swarm 导入方收到 null 协调数据 = 装配错误快速失败）。</para>
/// </summary>
public interface ISnapshotTransfer
{
    /// <summary>导出当前快照到目标（快照创建 = 实现侧策略——快照期间 Append 继续，增量随后推）。</summary>
    /// <param name="target">目标节点（传输面路由）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>握手协调数据（swarm 形态；null = 单源流式——无协调数据）。</returns>
    ValueTask<SnapshotCoordination?> ExportSnapshotAsync(NodeId target, CancellationToken cancellationToken = default);

    /// <summary>从对端导入快照（协调数据来自 InstallSnapshot RPC 握手面——swarm 形态据此多源拉取；
    /// null = 等待注入流式会话；事务式语义失败回滚旧快照完好）。</summary>
    /// <param name="source">来源节点（传输面路由）。</param>
    /// <param name="coordination">握手协调数据（swarm 形态；null = 单源流式——缺省）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    ValueTask ImportSnapshotAsync(NodeId source, SnapshotCoordination? coordination = null, CancellationToken cancellationToken = default);
}
