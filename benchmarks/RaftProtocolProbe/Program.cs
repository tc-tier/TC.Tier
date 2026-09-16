using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using DotNext.IO;
using DotNext.IO.Log;
using DotNext.Net.Cluster;
using DotNext.Net.Cluster.Consensus.Raft;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

Console.WriteLine("=== Raft Protocol Probe（DotNext 官方 ConsensusOnlyState 内存模式）===");
Console.WriteLine();

// ★ 与 RaftPerfProbe 同条件：Windows 时钟精度 1ms（公平对照——机器级定时器状态一致）
if (OperatingSystem.IsWindows()) _ = Winmm.timeBeginPeriod(1);   // HRESULT 无检查必要（探针进程级时序旋钮）

var endpoints = new IPEndPoint[]
{
    new(IPAddress.Loopback, 17031),
    new(IPAddress.Loopback, 17032),
    new(IPAddress.Loopback, 17033),
};

var loggerFactory = NullLoggerFactory.Instance;

// ★ 磁盘模式（env RAFT_DN_DISK=<目录>）：DotNext 自带 PersistentState（文件后端 WAL）——
//   WriteThrough = 逐写绕缓冲（强耐久档，与我们 virtual+载体写穿档同旨）；缺省内存形态不变。
var dnDisk = Environment.GetEnvironmentVariable("RAFT_DN_DISK");
var states = new ConsensusOnlyState[3];
var persistentStates = default(FilePersistentState[]);
if (dnDisk is not null)
{
    Directory.CreateDirectory(dnDisk);
    persistentStates = new FilePersistentState[3];
    for (var i = 0; i < 3; i++)
        persistentStates[i] = new FilePersistentState(Path.Combine(dnDisk, $"node{i}"));
    Console.WriteLine($"[disk] DotNext PersistentState（WriteThrough）at {dnDisk}");
}
for (var i = 0; i < 3; i++)
    states[i] = new ConsensusOnlyState();

var clusters = new RaftCluster[3];
for (var i = 0; i < 3; i++)
{
    var config = new RaftCluster.TcpConfiguration(endpoints[i])
    {
        ColdStart = i == 0,
        LowerElectionTimeout = 300,
        UpperElectionTimeout = 600,
        HeartbeatThreshold = 0.5,
        LoggerFactory = loggerFactory,
    };
    config.UseInMemoryConfigurationStorage();
    clusters[i] = new RaftCluster(config) { AuditTrail = persistentStates is null ? states[i] : persistentStates[i] };
}

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

Console.WriteLine("[1] Starting 3 nodes...");
for (var i = 0; i < 3; i++)
    await clusters[i].StartAsync(cts.Token);

await Task.Delay(5000);

var leaderIdx = -1;
for (var i = 0; i < 3; i++)
{
    if (clusters[i].Leader is not null && EndPoint.Equals(clusters[i].Leader, endpoints[i]))
    { leaderIdx = i; break; }
}
if (leaderIdx < 0)
{
    for (var i = 0; i < 3; i++)
    {
        if (clusters[i].Leader is not null)
        {
            for (var j = 0; j < 3; j++)
                if (EndPoint.Equals(endpoints[j], clusters[i].Leader)) { leaderIdx = j; break; }
            break;
        }
    }
}

Console.WriteLine("[2] Checking leaders...");
for (var i = 0; i < 3; i++)
{
    var leader = clusters[i].Leader;
    Console.WriteLine($"  N{i} Leader={leader}, Term={clusters[i].Term}");
    if (leader is not null)
    {
        for (var j = 0; j < 3; j++)
        {
            if (leaderIdx < 0 && EndPoint.Equals(endpoints[j], leader))
                leaderIdx = j;
        }
    }
}
if (leaderIdx < 0)
{
    Console.WriteLine("  Using first node with Leader as leaderIdx");
    for (var i = 0; i < 3; i++)
    {
        if (clusters[i].Leader is not null) { leaderIdx = i; break; }
    }
}
Console.WriteLine($"  -> Detected leaderIdx = {leaderIdx}");

if (leaderIdx < 0) goto Shutdown;

Console.WriteLine("[3] Adding peers...");
for (var i = 0; i < 3; i++)
{
    if (i == leaderIdx) continue;
    try { await clusters[leaderIdx].AddMemberAsync(endpoints[i], cts.Token); }
    catch (Exception ex) { Console.WriteLine($"  Node {i} failed: {ex.Message}"); }
}
await Task.Delay(3000);

Console.WriteLine();
Console.WriteLine("=== Phase 2: Replicate ===");
for (var i = 0; i < 5; i++)
{
    var entry = new ProbeLogEntry(Encoding.UTF8.GetBytes($"cmd-{i}"), clusters[leaderIdx].Term);
    var index = await clusters[leaderIdx].ReplicateAsync(entry, cts.Token);
    Console.WriteLine($"  Replicate entry {i} -> index={index}");
    await Task.Delay(200);
}
await Task.Delay(2000);

await PerfPhaseAsync(clusters[leaderIdx]);

Console.WriteLine();
Console.WriteLine("=== Phase 3: Summary ===");
for (var i = 0; i < 3; i++)
{
    Console.WriteLine($"  N{i}: committed={states[i].LastCommittedEntryIndex} last={states[i].LastEntryIndex}");
}

Shutdown:
Console.WriteLine();
Console.WriteLine("[Shutdown]");
for (var i = 0; i < 3; i++) await clusters[i].StopAsync(CancellationToken.None);
for (var i = 0; i < 3; i++) clusters[i].Dispose();
if (persistentStates is not null)
    foreach (var ps in persistentStates) ps.Dispose();   // 文件句柄释放
Console.WriteLine("Done.");

// ═══════════════════════════════════════════════════════════════
// 性能阶段（同台对照——与 benchmarks/RaftPerfProbe 同 entry/形态/轮次）
// ═══════════════════════════════════════════════════════════════

static async Task PerfPhaseAsync(RaftCluster leader)
{
    const int entrySize = 64, serialCount = 300, windowSize = 800;
    // ★ 长窗旋钮（同 RaftPerfProbe 的 RAFT_PROBE_ROUNDS——8000 条在 ~180k op/s 下仅 ~45ms
    //   测量窗，GC/调度噪声主导；长窗 = 秒级测量，数字稳定可复现）
    var pipelineRounds = int.TryParse(Environment.GetEnvironmentVariable("RAFT_DN_ROUNDS"), out var r) && r > 0 ? r : 10;

    Console.WriteLine();
    Console.WriteLine($"=== Phase 2.5: Perf（entry {entrySize}B · 串行 {serialCount} · 流水线 {windowSize}×{pipelineRounds}）===");
    var payload = new byte[entrySize];
    Array.Fill(payload, (byte)0x5A);
    var term = leader.Term;

    // 预热 2000 条（JIT/连接冷路径）
    for (var round = 0; round < 2000 / windowSize; round++)
    {
        var warm = new Task<bool>[windowSize];
        for (var i = 0; i < windowSize; i++)
            warm[i] = leader.ReplicateAsync(new ProbeLogEntry(payload, term), CancellationToken.None).AsTask();
        await Task.WhenAll(warm);
    }

    // 串行单条 await
    var serial = new double[serialCount];
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < serialCount; i++)
    {
        var t0 = sw.Elapsed.TotalMicroseconds;
        await leader.ReplicateAsync(new ProbeLogEntry(payload, term), CancellationToken.None);
        serial[i] = sw.Elapsed.TotalMicroseconds - t0;
    }
    Array.Sort(serial);
    Console.WriteLine($"串行单条 await：{serialCount} 条，p50={serial[serialCount / 2] / 1000:F2}ms " +
        $"p99={serial[(int)(serialCount * 0.99)] / 1000:F2}ms，吞吐 {serialCount / (serial.Sum() / 1_000_000):F0} op/s");

    // 流水线 800 in-flight × 10 轮
    // ★ 信号量滑动窗口（恒定 in-flight）：无 Task.WhenAny——其每完成一条 O(N) 数组拷贝
    //   +N 个 continuation 注册是测量税（800 in-flight 下 ~6.4KB/条分配），污染引擎数字
    var total = Stopwatch.StartNew();
    for (var round = 0; round < pipelineRounds; round++)
    {
        var rsw = Stopwatch.StartNew();
        using var gate = new SemaphoreSlim(windowSize);
        var tasks = new Task[windowSize];
        for (var i = 0; i < windowSize; i++)
        {
            await gate.WaitAsync();
            tasks[i] = SendOneAsync(leader, payload, term, gate);
        }
        await Task.WhenAll(tasks);
        Console.WriteLine($"流水线轮 {round + 1}：{windowSize} 条 / {rsw.Elapsed.TotalMilliseconds:F1}ms = {windowSize / rsw.Elapsed.TotalSeconds:F0} op/s");
    }
    var totalOps = pipelineRounds * windowSize;
    Console.WriteLine($"流水线合计：{totalOps} 条 / {total.Elapsed.TotalSeconds:F2}s = {totalOps / total.Elapsed.TotalSeconds:F0} op/s");
}

/// <summary>单条发送（滑动窗口补位——完成即放行下一槽）。</summary>
static async Task SendOneAsync(DotNext.Net.Cluster.Consensus.Raft.IRaftCluster leader, byte[] payload, long term, SemaphoreSlim gate)
{
    try { await leader.ReplicateAsync(new ProbeLogEntry(payload, term), CancellationToken.None); }
    finally { gate.Release(); }
}


/// <summary>Windows 时钟精度（与 RaftPerfProbe 同条件公平对照）。</summary>
internal static class Winmm
{
    [System.Runtime.InteropServices.DllImport("winmm.dll", ExactSpelling = true)]
    public static extern uint timeBeginPeriod(uint period);
}

// ═══ ProbeLogEntry ═══

// ★ 磁盘模式 WAL：DotNext 自带持久化形态——WriteAheadLog（文件后端 WAL）+ Noop IStateMachine
// （对齐我们探针的 NoopMachine；FlushInterval 默认 Zero=每 commit 刷盘（强耐久档））
// DOTNEXT001：WriteAheadLog 标 [Experimental]——探针测量用途豁免（基准形态随包演进跟进）
#pragma warning disable DOTNEXT001
internal sealed class FilePersistentState(string path)
    : DotNext.Net.Cluster.Consensus.Raft.StateMachine.WriteAheadLog(
        new DotNext.Net.Cluster.Consensus.Raft.StateMachine.WriteAheadLog.Options { Location = path },
        new NoopWalStateMachine());

internal sealed class NoopWalStateMachine
    : DotNext.Net.Cluster.Consensus.Raft.StateMachine.IStateMachine
{
    public ValueTask<long> ApplyAsync(DotNext.Net.Cluster.Consensus.Raft.StateMachine.LogEntry entry, CancellationToken token)
        => ValueTask.FromResult(entry.Index);

    public ValueTask ReclaimGarbageAsync(long watermark, CancellationToken token)
        => ValueTask.CompletedTask;

    public DotNext.Net.Cluster.Consensus.Raft.StateMachine.ISnapshot? Snapshot
        => null;   // Noop 探针无快照
}
#pragma warning restore DOTNEXT001

internal sealed class ProbeLogEntry(byte[] data, long entryTerm) : IRaftLogEntry
{
    long IRaftLogEntry.Term => entryTerm;
    int? IRaftLogEntry.CommandId => null;
    bool ILogEntry.IsSnapshot => false;
    DateTimeOffset ILogEntry.Timestamp => DateTimeOffset.UtcNow;
    long? IDataTransferObject.Length => data.Length;
    bool IDataTransferObject.IsReusable => true;

    bool IDataTransferObject.TryGetMemory(out ReadOnlyMemory<byte> memory)
    {
        memory = data;
        return true;
    }

    ValueTask IDataTransferObject.WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        => writer.WriteAsync(data, null, token);

    ValueTask<TResult> IDataTransferObject.TransformAsync<TResult, TTransformation>(TTransformation transformation, CancellationToken token)
        => throw new NotSupportedException();
}
