using System.Diagnostics;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Mem;
using TC.Tier.Runtime.Structures.Log;
using TC.Tier.Runtime.Transactions;

namespace TC.Tier.Runtime.Benchmarks.Kv;

/// <summary>
/// ★ Session 提交管线吞吐探针（v2 形态——session-manager-design.md §8.2 回合/s 入档口径）。
/// <para>v1（方案验证期）双变体（手搓管线 serial/group + commit record 直写）已随 v2 定稿退役：
/// record 已删（session 零持久化决策）、批合并已内建。本探针=真 SessionManager 驱动：</para>
/// <para>- 并发档：N 会话并发 CommitAsync（管线排空批合并生效——同批共享 seq、整批一次 2PC）；</para>
/// <para>- 单会话档：1 会话串行（每回合独占批=批合并上界对照）；</para>
/// <para>时延口径=完整回程（入队→物化→Prepare→Confirm→回执）。参与者=真 EntryLog（自动提交禁用，
/// 2PC 由管线驱动）。阈值口径：mem ≥30k 回合/s（8 会话）——管线永不回退。</para>
/// <para>运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks --
/// --session-pipeline-probe [mem|local] [sessions] [commits/session]</para>
/// </summary>
public static class SessionPipelineProbe
{
    public static void Run(string medium = "mem", int sessions = 8, int commitsPerSession = 2_000)
    {
        string spec = medium == "local"
            ? $"local:///F:/CodeSource/mytzz/dotnet/TC.Tier/test_out/session-probe-{Guid.NewGuid():N}"
            : "memory:";
        using var fs = TierFs.New(spec);

        using var log = NewEntryLog(fs);
        var payload = new byte[64];
        new Random(7).NextBytes(payload);

        Console.WriteLine($"[probe] medium={medium} sessions={sessions} commits/session={commitsPerSession} entry=64B");

        RunVariant("single", fs, log, payload, 1, commitsPerSession);
        if (sessions > 1)
            RunVariant("concurrent", fs, log, payload, sessions, commitsPerSession);
    }

    private static void RunVariant(string name, IFileSystem fs, EntryLog log, byte[] payload,
        int sessions, int commits)
    {
        using var mgr = SessionManager.Create(fs, $"probe-{name}", participants: ("log", log));
        mgr.Initialize();
        mgr.WaitForReady();

        var latencies = new long[sessions][];
        for (int i = 0; i < sessions; i++)
            latencies[i] = new long[commits];

        var workers = Enumerable.Range(0, sessions).Select(i => Task.Run(async () =>
        {
            using var session = mgr.OpenSession($"s{i}");
            var sw = Stopwatch.StartNew();
            for (int c = 0; c < commits; c++)
            {
                long start = sw.ElapsedTicks;
                session.Stage(() => log.Append(payload));
                await session.CommitAsync();
                latencies[i][c] = (sw.ElapsedTicks - start) * 1_000_000 / Stopwatch.Frequency;
            }
        })).ToArray();

        var swAll = Stopwatch.StartNew();
        Task.WaitAll(workers);
        var wall = swAll.ElapsedMilliseconds;
        mgr.DisposeAsync().AsTask().GetAwaiter().GetResult();

        double roundsPerSec = sessions * (double)commits / wall * 1000;
        var all = latencies.SelectMany(l => l).OrderBy(v => v).ToArray();
        double p(double q) => all[(int)(q * (all.Length - 1))];
        Console.WriteLine($"[{name,-10}] {sessions * commits} rounds in {wall} ms = {roundsPerSec:N0} rounds/s | " +
                          $"回程 µs p50={p(0.50):N0} p99={p(0.99):N0} p999={p(0.999):N0} max={all[^1]:N0} | " +
                          $"水位={mgr.LastCommittedSeq}");
    }

    private static EntryLog NewEntryLog(IFileSystem fs)
    {
        var settings = new EntryLogSettings(
            new StorageEngineOptions("probe-entry", 32L << 20, enableSegmentation: true,
                preallocateFile: false, deleteOnClose: false))
        {
            MetaPolicyKind = TC.Tier.Contracts.Meta.MetaPolicyKind.Managed,
            CommitInterval = TimeSpan.FromMilliseconds(-1),   // 2PC 由管线驱动
            MaxUnflushedBytes = long.MaxValue,
            MaxUnflushedCount = int.MaxValue,
        };
        var log = new EntryLog(fs, settings);
        log.Initialize();
        log.WaitForReady();
        return log;
    }
}
