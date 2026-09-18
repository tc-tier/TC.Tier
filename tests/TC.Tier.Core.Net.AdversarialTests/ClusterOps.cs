namespace TC.Tier.Core.Net.AdversarialTests;

/// <summary>
/// 对抗场景共享操作（收敛语义等待/leader 定位/复制容错/取证状态转储）。
/// </summary>
internal static class ClusterOps
{
    /// <summary>命令载荷（确定性内容）。</summary>
    internal static byte[] Cmd(int i) => System.Text.Encoding.UTF8.GetBytes($"cmd-{i:D4}");

    /// <summary>业务域恰好一次断言：applied 按键升序恰为 cmd-0001..cmd-{count:D4}，无重复无空洞。
    /// ★ 日志 index ≠ 业务序列——leader 任期锚点条目不达状态机（二期-B，Noop apply 跳过），
    /// 业务条目自锚点起右移；断言只认业务载荷序列，不认日志 index 连续性。</summary>
    internal static void AssertAppliedExactlyOnce(AdversarialNode node, int count)
    {
        var pairs = node.Machine.Applied.OrderBy(kv => kv.Key).ToArray();
        pairs.Select(p => p.Key).Should().BeInAscendingOrder("apply 按日志序——键必升序");
        pairs.Should().HaveCount(count);
        for (var i = 0; i < count; i++)
            pairs[i].Value.Should().Equal(Cmd(i + 1));
    }

    /// <summary>业务域严格递增断言（soak 形态——命令号跨重启段有洞、锚点在键序列留洞）：
    /// applied 键升序 + 载荷 cmd 号严格递增无重复——命令域的"无洞无重复"真语义。</summary>
    internal static void AssertAppliedStrictlyAscending(AdversarialNode node)
    {
        var pairs = node.Machine.Applied.OrderBy(kv => kv.Key).ToArray();
        pairs.Should().NotBeEmpty("节点应至少应用一段");
        pairs.Select(p => p.Key).Should().BeInAscendingOrder("apply 按日志序——键必升序");
        var last = 0;
        foreach (var pair in pairs)
        {
            var n = CmdNumber(pair.Value);
            n.Should().BeGreaterThan(last, "业务命令号严格递增（重启续传段/锚点洞两侧均不许重复与回退）");
            last = n;
        }
    }

    /// <summary>解析命令载荷 cmd-N 的业务命令号（非命令形态 = 格式损坏抛）。</summary>
    internal static int CmdNumber(byte[] payload)
    {
        var text = System.Text.Encoding.UTF8.GetString(payload);
        return text.StartsWith("cmd-", System.StringComparison.Ordinal) && int.TryParse(text[4..], out var n)
            ? n
            : throw new FormatException($"载荷非 cmd-N 形态：'{text}'。");
    }

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
