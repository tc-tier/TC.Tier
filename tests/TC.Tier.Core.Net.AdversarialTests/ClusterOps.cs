namespace TC.Tier.Core.Net.AdversarialTests;

/// <summary>
/// 对抗场景共享操作（收敛语义等待/leader 定位/复制容错/取证状态转储）。
/// </summary>
internal static class ClusterOps
{
    /// <summary>命令载荷（确定性内容）。</summary>
    internal static byte[] Cmd(int i) => System.Text.Encoding.UTF8.GetBytes($"cmd-{i:D4}");

    /// <summary>条件等待（超时抛——取证信息由调用方补充）。</summary>
    internal static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("条件未满足（超时）。");
            await Task.Delay(50);
        }
    }

    /// <summary>等恰一个 leader（超时抛）。</summary>
    internal static async Task<AdversarialNode> WaitLeaderAsync(IEnumerable<AdversarialNode> nodes, TimeSpan? timeout = null)
    {
        var list = nodes.ToArray();
        await WaitForAsync(() => list.Count(n => n.Raft.IsLeader) == 1, timeout);
        return list.Single(n => n.Raft.IsLeader);
    }

    /// <summary>复制（超时 = 失 quorum 挂起——调用方按场景语义处理；非 leader 快速失败）。</summary>
    internal static async Task<long> ReplicateAsync(AdversarialNode node, byte[] command, TimeSpan timeout)
        => await node.Raft.ReplicateAsync(command).AsTask().WaitAsync(timeout);

    /// <summary>取证状态转储（失败信息拼接——任期/leader 认知/applied 计数/日志尾全览）。</summary>
    internal static string Dump(IEnumerable<AdversarialNode> nodes)
    {
        var list = nodes.ToArray();
        return $"terms=[{string.Join(",", list.Select(n => n.Raft.CurrentTerm))}] "
            + $"leaders={list.Count(n => n.Raft.IsLeader)} "
            + $"leaderIds=[{string.Join(",", list.Select(n => n.Raft.LeaderId?.ToString()[..6] ?? "null"))}] "
            + $"appliedCounts=[{string.Join(",", list.Select(n => n.Machine.Applied.Count))}] "
            + $"commits=[{string.Join(",", list.Select(n => n.Raft.CommitIndex))}] "
            + $"tails=[{string.Join(",", list.Select(n => n.Store.LastLogIndex))}]";
    }

    /// <summary>取证轨迹转储（每节点内存日志）。</summary>
    internal static string Traces(IEnumerable<AdversarialNode> nodes)
        => string.Join("\n", nodes.Select(n => n.Logger?.Dump() ?? ""));
}
