// Core.Net Raft 性能探针（Windows 同台对照形态——与 tests/RaftProtocolProbe（DotNext）同机同轮）
// 形态对齐 raft-perf-report.md：3 节点 · InProcessTransportHub（零网络）/ TCP loopback（RAFT_PROBE_NET=tcp）
// · memory 卷 · Noop 状态机 · entry 64B；串行 await / 流水线 800 in-flight 两形态。
// ★ 装配 = Core.Net 夹具（FsRaftStore+ApplyPipeline+RaftStateMachine——对抗套件 AdversarialNode 同源，
//   spec-12 §13.1 夹具模式）；W1 历史数字（96.6k/98.6k）出自 RaftNode+TierWAL 产品装配——产品接线
//   随 D6 重建，本探针 = 引擎+传输层同面对照（存储夹具 FsRaftStore·memory 卷同介质）。
// 零 IO 打点（时间戳数组结束统一输出）。
using System.Diagnostics;
using System.Net;
using TC.Tier.Contracts.Layout;
using TC.Tier.Contracts.Meta;
using TC.Tier.Core.IO;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Tests.Fixtures;   // FsRaftStore（链接共享——单一真源）
using TC.Tier.Core.Net.Transport;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Core.Net.Transport.Tcp;
using TC.Tier.Core.Primitives;
using TC.Tier.Products.Net.Node;
using TC.Tier.Products.Net.Host;
using TC.Tier.Products.Net.Admin;
using TC.Tier.Products.Net.RaftStore;
using TC.Tier.Products.Wal;
using TC.Tier.Runtime.Meta;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Log.Contracts;
using TC.Tier.Runtime.Structures.Metadata;

const int EntrySize = 64;
// 串行形态默认 1000 条（RAFT_PROBE_SERIAL 可缩——诊断长尾时缩短首段墙钟）
// 在途窗（RAFT_PROBE_WINDOW 可调——延迟×窗 = 吞吐上限判定）
var WindowSize = int.TryParse(Environment.GetEnvironmentVariable("RAFT_PROBE_WINDOW"), out var wv) && wv > 0 ? wv : 800;
// 流水线轮次：默认 10（对齐 raft-perf-report 基线负载）；RAFT_PROBE_ROUNDS 可拉长（长窗测量/剖析取样）
// ★ 测量窗：RAFT_PROBE_ROUNDS 显式轮数优先；缺省按整轮跑满 RAFT_PROBE_SECONDS 时间预算
//   （默认 5s，自适应介质吞吐——10 轮 8000 条 ≈ 40-80ms 窗在轮级方差下是噪声区，
//   出不了性能；中位/p10/p90 口径对尾轮方差稳健）
var pipelineRounds = int.TryParse(Environment.GetEnvironmentVariable("RAFT_PROBE_ROUNDS"), out var rounds) && rounds > 0 ? rounds : 0;
var probeSeconds = double.TryParse(Environment.GetEnvironmentVariable("RAFT_PROBE_SECONDS"), out var secs) && secs > 0 ? secs : 5;

// ★ Windows 时钟精度实验：默认 15.6ms 定时器量子 vs Linux 1-4ms——若吞吐显著变化，
//   差距主因 = 环境（定时器粒度）而非协议层（判决性对照见提交注）
if (OperatingSystem.IsWindows()) _ = Winmm.timeBeginPeriod(1);   // 返回 HRESULT 无检查必要（探针进程级时序旋钮）

// ★ 介质开关（真磁盘基准）：env RAFT_PROBE_FS=local:///<目录> → 每节点私有 local 子卷
//   （真 fsync——commit 走载体写穿档）；缺省 memory:（机制上限形态）。InProcessTransportHub 不变
//   （隔离磁盘变量——网络形态另测）。
var fsSpec = Environment.GetEnvironmentVariable("RAFT_PROBE_FS");
var diskRoot = fsSpec is null ? null : fsSpec.Replace('\\', '/').TrimEnd('/');
// virtual 形态（RAFT_PROBE_FS=virtual:///<目录>）：每节点私有 .tier 卷 + 载体写穿档（IS-03——
// JournalBarrier 免逐写 fsync，io-semantics 实测机械盘 0.179ms 档；TierWAL 契约① 要求 virtual 必挂）
var virtualMode = diskRoot is not null && diskRoot.StartsWith("virtual", StringComparison.Ordinal);
var vroot = virtualMode ? diskRoot![(diskRoot!.IndexOf("///", StringComparison.Ordinal) + 3)..] : null;
var baseSpec = diskRoot is null || virtualMode ? null
    : (diskRoot!.StartsWith("local:///", StringComparison.Ordinal) ? diskRoot : $"local:///{diskRoot}");   // 探针脚本：null 容忍面已由 virtualMode 分流
var medium = diskRoot is null ? "memory" : (virtualMode ? $"virtual+WT({vroot})" : $"local({diskRoot})");

// ★ 传输开关（TCP loopback 基准——spec-11 W1）：env RAFT_PROBE_NET=tcp → 每节点 ClusterTransport
//   （loopback 节点对单长连接）；缺省 InProcessTransportHub（机制上限形态——零编解码零 socket）。
var useTcp = string.Equals(Environment.GetEnvironmentVariable("RAFT_PROBE_NET"), "tcp", StringComparison.OrdinalIgnoreCase);
var wire = useTcp ? "TCP loopback" : "InProcess";

// ★ 存储开关（raft × TierWal 产品接线端到端）：env RAFT_STORE=tierwal → 节点 store = TierWalRaftStore
//   （TierWal 生产默认——DIO hints/Managed meta/组提交三维度；virtual 卷须载体写穿档——契约① 姿态，
//   本探针 virtualMode 已挂）；缺省 fs = FsRaftStore 夹具（对抗套件同源——历史基线可比）。
var useTierWal = string.Equals(Environment.GetEnvironmentVariable("RAFT_STORE"), "tierwal", StringComparison.OrdinalIgnoreCase);
var storeName = useTierWal ? "TierWalRaftStore" : "FsRaftStore";
// ★ meta 形态旋钮（RAFT_WAL_META=versioned）：TierWal 元数据从默认 Managed 单槽切版本链
//   （Transport + MetadataMetaTransport——meta.md §7 3a）——单槽 vs 版本链归因对照
var useVersionedMeta = string.Equals(Environment.GetEnvironmentVariable("RAFT_WAL_META"), "versioned", StringComparison.OrdinalIgnoreCase);
if (useVersionedMeta) storeName += "+versionedMeta";
// WAL 旋钮（二分诊断/组提交调优）：RAFT_WAL_COMMIT_MS=组提交时间维度（缺省 10=产品默认；-1 禁用循环）
var walCommitMs = int.TryParse(Environment.GetEnvironmentVariable("RAFT_WAL_COMMIT_MS"), out var wc) ? wc : 10;
// 串行形态条数（缺省 1000；RAFT_PROBE_SERIAL 缩短——诊断长尾时避免首段墙钟过长）
var serialCount = int.TryParse(Environment.GetEnvironmentVariable("RAFT_PROBE_SERIAL"), out var sc) && sc > 0 ? sc : 1000;

Console.WriteLine($"=== Core.Net Raft 性能探针（3 节点 {wire} · {medium} · {storeName} · Noop · {EntrySize}B）===");
Console.WriteLine($"环境：{Environment.ProcessorCount} 逻辑核，.NET {Environment.Version}，{DateTime.Now:yyyy-MM-dd HH:mm:ss}，timeBeginPeriod=1");

var members = new ClusterMember[3];
for (var i = 0; i < 3; i++) members[i] = new ClusterMember(NodeId.NewRandom(), "");
var config = new ClusterConfig(members);

// 传输装配（探针持有生命周期——节点 Dispose 后释放）：
// InProcess = 单枢纽；TCP = 每节点 ClusterTransport（引擎直连 IProtocolTransport——无桥）
InProcessTransportHub? hub = null;
ClusterTransport[]? tcpHubs = null;
if (useTcp)
{
    var ports = ReservePorts(3);
    tcpHubs = new ClusterTransport[3];
    for (var i = 0; i < 3; i++)
    {
        var peers = new Dictionary<NodeId, IPEndPoint>();
        for (var j = 0; j < 3; j++)
            if (j != i) peers[members[j].Id] = new IPEndPoint(IPAddress.Loopback, ports[j]);
        // 重连退避封顶 = ElectionTimeoutMin(150ms)/2——spec-11 §2.2 raft 消费方覆盖（选举不被重连节奏劫持）
        tcpHubs[i] = new ClusterTransport(members[i].Id,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, ports[i]), peers)
                .WithReconnect(TimeSpan.FromMilliseconds(25), 2.0, TimeSpan.FromMilliseconds(75)));
        tcpHubs[i].Start();
    }
    // 拨号归属收敛等待：全 mesh 链路建立（每枢纽 2 条对端连接）后才起节点——避免早期 RPC 静默丢
    var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var upCount = 0;
    foreach (var h in tcpHubs)
        h.PeerConnected += _ =>
        {
            if (Interlocked.Increment(ref upCount) >= 2 * 3) connected.TrySetResult();
        };
    await connected.Task.WaitAsync(TimeSpan.FromSeconds(10));
}
else
{
    hub = new InProcessTransportHub();
}

var nodes = new ProbeNode[3];
var nodeFss = new IFileSystem[3];   // 探针持有——节点 Dispose 后释放（local 卷 Dispose=递归清理）
for (var i = 0; i < 3; i++)
{
    nodeFss[i] = baseSpec is null
        ? TierFs.New("memory:")     // 每节点私有 mem 卷
        : TierFs.New($"{baseSpec}/node-{i}-{DateTime.Now:HHmmss}");
    if (virtualMode)
        nodeFss[i] = TierFs.New($"virtual:///{vroot}/node-{i}-{DateTime.Now:HHmmss}.tier",
            new TC.Tier.Core.IO.TierVolume.TierVolumeFormatOptions { CarrierWriteThrough = true });
    IProtocolTransport transport = useTcp ? tcpHubs![i] : hub!.Register(members[i].Id);
    nodes[i] = await ProbeNode.CreateAsync(members[i].Id, nodeFss[i], transport, config, useTierWal, walCommitMs, useVersionedMeta);
}

// TCP 端口预占（实际监听由 ClusterTransport 绑定——占后立放，微小竞态由拨号重连退避吸收）
static int[] ReservePorts(int count)
{
    var listeners = new System.Net.Sockets.TcpListener[count];
    var ports = new int[count];
    for (var i = 0; i < count; i++)
    {
        listeners[i] = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listeners[i].Start();
        ports[i] = ((IPEndPoint)listeners[i].LocalEndpoint).Port;
    }
    foreach (var l in listeners) l.Stop();
    return ports;
}

// leader 收敛（稳定 1s）——先等选举完成再取（启动即 First 会撞"选举未收敛"偶发崩）
ProbeNode? leader = null;
var waitLeader = Stopwatch.StartNew();
while (leader is null && waitLeader.Elapsed < TimeSpan.FromSeconds(10))
{
    leader = nodes.FirstOrDefault(n => n.Raft.IsLeader);
    if (leader is null) await Task.Delay(50);
}
if (leader is null) throw new InvalidOperationException("10s 无 leader——选举异常");
var stableSince = Stopwatch.StartNew();
while (stableSince.Elapsed < TimeSpan.FromSeconds(1))
{
    await Task.Delay(100);
    var current = nodes.FirstOrDefault(n => n.Raft.IsLeader);
    if (current is not null && current != leader) { leader = current; stableSince.Restart(); }
}

// ★ 完成档开关（env RAFT_PROBE_COMMITTED=1）：ReplicateCommittedAsync——完成=多数派提交
//   （与 DotNext 对照同口径；默认 applied 档 = read-your-writes）
var committedTier = string.Equals(Environment.GetEnvironmentVariable("RAFT_PROBE_COMMITTED"), "1", StringComparison.Ordinal);

Console.WriteLine($"leader={leader.Id}（term={leader.Raft.CurrentTerm}）完成档={(committedTier ? "committed（多数派提交即返——DotNext 同口径）" : "applied（read-your-writes 默认档）")}");

var payload = new byte[EntrySize];
payload.AsSpan().Fill(0x5A);

// ★ 重路由复制（磁盘形态换届常态——spec-08 客户端标准模式：NotLeader → 重找 leader 重试）
async Task<long> ReplicateRoutedAsync(byte[] cmd)
{
    for (var attempt = 0; ; attempt++)
    {
        var l = nodes.FirstOrDefault(n => n.Raft.IsLeader);
        if (l is null) { await Task.Delay(25); continue; }
        try
        {
            return committedTier
                ? await l.Raft.ReplicateCommittedAsync(cmd)
                : await l.Raft.ReplicateAsync(cmd);
        }
        catch (NotLeaderException) when (attempt < 500) { await Task.Delay(25); }   // 换届风暴容忍（TierWal 持久化门下换届常态）
    }
}

// ── 预热：2000 条流水线（JIT/引擎冷路径）──
{
    var warm = new Task<long>[WindowSize];
    for (var round = 0; round < 2000 / WindowSize; round++)
    {
        for (var i = 0; i < WindowSize; i++) warm[i] = ReplicateRoutedAsync(payload);
        await Task.WhenAll(warm);
    }
}

// ★ GC 对照基线（零拷贝/分配优化的量化口径——预热后记录，流水线结束输出增量）
var gc0 = GC.CollectionCount(0);
var gc1 = GC.CollectionCount(1);
var gc2 = GC.CollectionCount(2);
var allocBase = GC.GetTotalAllocatedBytes(precise: false);
var allocBasePrecise = GC.GetTotalAllocatedBytes(precise: true);
var pausePctBase = GC.GetGCMemoryInfo().PauseTimePercentage;   // 暂停占比基线（差分测量窗内暂停墙钟占比）

// ── 形态 A：串行单条 await（延迟主导）──
double[] serialUs;
var serialRaw = new double[serialCount];
// ★ 停顿现场看门狗（RAFT_PROBE_FAILFAST_MS=阈值，缺省 0 关）：serial/pipeline op 执行中
//   （未返回）超阈即 Environment.FailFast——抓停顿现场的 in-flight 栈。锚点 = Stopwatch.
//   GetTimestamp() 原始计数（GetElapsedTime 专用；Elapsed.Ticks 是 100ns 单位会算错量级）。
var ffMs = double.TryParse(Environment.GetEnvironmentVariable("RAFT_PROBE_FAILFAST_MS"), out var ffv) && ffv > 0 ? ffv : 0;
var opStartTs = -1L;
using var watchdogCts = new CancellationTokenSource();
async Task WatchdogLoopAsync()
{
    while (!watchdogCts.IsCancellationRequested)
    {
        var s = Volatile.Read(ref opStartTs);
        if (s > 0 && Stopwatch.GetElapsedTime(s).TotalMilliseconds > ffMs)
        {
            Console.WriteLine($"[failfast] op 执行中超阈 {ffMs:F0}ms——取现场");
            Console.Out.Flush();
            Environment.FailFast($"raft×TierWal 停顿取证（op in-flight > {ffMs:F0}ms）");
        }
        await Task.Delay(5, watchdogCts.Token).ConfigureAwait(false);
    }
}
var watchdogTask = ffMs > 0 ? WatchdogLoopAsync() : Task.CompletedTask;
{
    serialUs = new double[serialCount];
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < serialCount; i++)
    {
        var t0 = sw.Elapsed.TotalMicroseconds;
        Volatile.Write(ref opStartTs, Stopwatch.GetTimestamp());
        await ReplicateRoutedAsync(payload);
        Volatile.Write(ref opStartTs, -1L);
        serialRaw[i] = serialUs[i] = sw.Elapsed.TotalMicroseconds - t0;
    }
    // ★ 看门狗跨段常驻（serial → pipeline）：取消点移后到流水线结束——停顿轮现场同取
    Array.Sort(serialUs);
    var p50 = serialUs[serialCount / 2] / 1000;
    var p99 = serialUs[(int)(serialCount * 0.99)] / 1000;
    // ★ 慢操作节律诊断（RAFT_PROBE_SLOW_MS=阈值，缺省 0 关）：原始序遍历打印慢点与其
    //   在时间轴上的分布——等间隔峰 = 周期性停顿（flush/定时器竞态）；聚类 = 换届/GC 风暴
    var slowMs = double.TryParse(Environment.GetEnvironmentVariable("RAFT_PROBE_SLOW_MS"), out var sm) && sm > 0 ? sm : 0;
    if (slowMs > 0)
    {
        var slowSeq = new List<double>();
        double elapsedMs = 0;
        for (var i = 0; i < serialCount; i++)
        {
            elapsedMs += serialRaw[i] / 1000;
            if (serialRaw[i] / 1000 > slowMs)
                slowSeq.Add(elapsedMs);
        }
        if (slowSeq.Count > 0)
        {
            var msg = string.Join(" ", slowSeq.Select(t => t.ToString("F0")));
            Console.WriteLine($"[slow] >{slowMs:F0}ms 共 {slowSeq.Count} 个（时间轴 ms：{msg}）");
        }
        else Console.WriteLine($"[slow] 无 >{slowMs:F0}ms 慢点");
    }
    Console.WriteLine($"串行单条 await：{serialCount} 条，p50={p50:F2}ms p99={p99:F2}ms，" +
        $"吞吐 {serialCount / (serialUs.Sum() / 1_000_000):F0} op/s");
}

// ── 形态 B：并发流水线（800 in-flight 窗口 × N 轮）──
{
    var roundOps = new List<double>(pipelineRounds > 0 ? pipelineRounds : 256);
    var total = Stopwatch.StartNew();
    for (var round = 0; pipelineRounds > 0 ? round < pipelineRounds : total.Elapsed.TotalSeconds < probeSeconds; round++)
    {
        var rsw = Stopwatch.StartNew();
        Volatile.Write(ref opStartTs, Stopwatch.GetTimestamp());   // 轮级看门狗锚点（停顿轮现场）
        // ★ 信号量滑动窗口（恒定 in-flight）：无 Task.WhenAny——其每完成一条 O(N) 数组拷贝
        //   +N 个 continuation 注册是测量税（800 in-flight 下 ~6.4KB/条分配），污染引擎数字
        using var gate = new SemaphoreSlim(WindowSize);
        var tasks = new Task[WindowSize];
        for (var i = 0; i < WindowSize; i++)
        {
            await gate.WaitAsync();
            tasks[i] = SendOneAsync(gate, payload);
        }
        await Task.WhenAll(tasks);
        Volatile.Write(ref opStartTs, -1L);
        var ops = WindowSize / rsw.Elapsed.TotalSeconds;
        roundOps.Add(ops);
        var allocSinceStart = GC.GetTotalAllocatedBytes(precise: true) - allocBasePrecise;
        Console.WriteLine($"流水线轮 {round + 1}：{WindowSize} 条 / {rsw.Elapsed.TotalMilliseconds:F1}ms = {ops:F0} op/s · 分配累计 {allocSinceStart / 1024.0:F0} KB");
    }
    var totalOps = roundOps.Count * WindowSize;
    watchdogCts.Cancel();
    if (watchdogTask is not null) await watchdogTask;
    var sorted = roundOps.ToArray();
    Array.Sort(sorted);
    var median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : 0.5 * (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]);
    Console.WriteLine($"流水线合计：{totalOps} 条 / {total.Elapsed.TotalSeconds:F2}s = {totalOps / total.Elapsed.TotalSeconds:F0} op/s" +
        $"（{sorted.Length} 轮 · 中位 {median:F0} · p10 {sorted[(int)(sorted.Length * 0.10)]:F0} · p90 {sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.90))]:F0} op/s）");
    Console.WriteLine($"GC 增量：GC0 +{GC.CollectionCount(0) - gc0} · GC1 +{GC.CollectionCount(1) - gc1} · GC2 +{GC.CollectionCount(2) - gc2} · 分配(近似) {(GC.GetTotalAllocatedBytes(false) - allocBase) / 1024.0:F0} KB · 分配(精确) {(GC.GetTotalAllocatedBytes(true) - allocBasePrecise) / 1024.0:F0} KB");
    // ★ GC 暂停窗占比（方差归因——暂停总时长/测量窗墙钟，差分进程启动以来的累计占比）
    var gcInfo = GC.GetGCMemoryInfo();
    var pausedMs = (gcInfo.PauseTimePercentage - pausePctBase) / 100.0 * total.Elapsed.TotalMilliseconds;
    Console.WriteLine($"GC 暂停：测量窗内 {pausedMs:F0}ms（{100.0 * pausedMs / total.Elapsed.TotalMilliseconds:F1}% 墙钟）· 最近暂停 " +
        string.Join(" / ", gcInfo.PauseDurations.ToArray().Select(d => $"{d.TotalMilliseconds:F1}ms")));
}

/// <summary>单条发送（滑动窗口补位——完成即放行下一槽）。</summary>
async Task SendOneAsync(SemaphoreSlim gate, byte[] cmd)
{
    try { await ReplicateRoutedAsync(cmd); }
    finally { gate.Release(); }
}

Console.WriteLine();
Console.WriteLine("=== 探针完成 ===");

// ★ 介质锚点（env RAFT_WAL_MICRO=1）：裸 WAL append/append+commit 在同介质——设备地板 vs 协议开销分解
//   （=2：并发提交流形态——N 线程各自 append+commit 同一 wal，模拟 raft leader 双提交流；meta 无关性对照见下）
if (Environment.GetEnvironmentVariable("RAFT_WAL_MICRO") is "1" or "2")
{
    var concurrent = Environment.GetEnvironmentVariable("RAFT_WAL_MICRO") == "2";
    var microVersioned = Environment.GetEnvironmentVariable("RAFT_WAL_MICRO_VMETA") == "1";
    var microFs = baseSpec is null
        ? TierFs.New("memory:")
        : TierFs.New($"{baseSpec}/micro-{DateTime.Now:HHmmss}");
    var microWalOpts = new TC.Tier.Products.Wal.TierWalOptions()
        .WithWalName("micro").WithCommitInterval(TimeSpan.FromMilliseconds(-1))
        .WithMaxUnflushedBytes(long.MaxValue).WithMaxUnflushedCount(int.MaxValue);
    var microMetaTransport = default(TC.Tier.Runtime.Meta.MetadataMetaTransport?);
    if (microVersioned)
    {
        var settings = new VersionedMetadataSettings(new StorageEngineOptions("micro.vmeta", 1024L * 1024 * 1024,
            enableSegmentation: false, preallocateFile: false))
        {
            PayloadSize = LogMetaHeaderCodec.StructSize + LogMetaPayloadCodec.StructSize
                + TC.Tier.Products.Wal.TierWalOptions.Default.MetaOpaqueBytes + Crc32FooterCodec.StructSize,
        };
        microMetaTransport = new MetadataMetaTransport(microFs, settings);
        microWalOpts = microWalOpts.WithMetaPolicyKind(MetaPolicyKind.Transport);
    }
    var microWal = microVersioned
        ? await microWalOpts.Builder(microFs).WithMetaTransport((IMetaTransport)microMetaTransport!).StartAsync(default)
        : await microWalOpts.Builder(microFs).StartAsync(default);
    var metaLabel = microVersioned ? "+versionedMeta" : "";
    var frame = new byte[EntrySize];
    var msw = Stopwatch.StartNew();
    double MMed(double[] xs) { Array.Sort(xs); return xs[xs.Length / 2]; }
    if (!concurrent)
    {
        const int MN = 200;
        var mt = new double[MN];
        for (var i = 0; i < MN; i++) { var t0 = msw.Elapsed.TotalMicroseconds; await microWal.AppendSingleAsync(frame, default); mt[i] = msw.Elapsed.TotalMicroseconds - t0; }
        Console.WriteLine($"[walMicro] append p50={MMed(mt) / 1000:F3}ms");
        for (var i = 0; i < MN; i++) { var t0 = msw.Elapsed.TotalMicroseconds; await microWal.AppendSingleAsync(frame, default); await microWal.CommitAsync(default); mt[i] = msw.Elapsed.TotalMicroseconds - t0; }
        Console.WriteLine($"[walMicro] append+commit{metaLabel} p50={MMed(mt) / 1000:F3}ms p99={mt[(int)(MN * 0.99)] / 1000:F3}ms");
    }
    else
    {
        // ★ 并发提交流隔离（RAFT_WAL_MICRO=2）：2 线程 × 各自连续 append+commit（raft leader 形态）
        //   ——验证 TierWal 单 wal 并发提交是否自伤（meta 区段租约互踩），排除 raft/引擎归因
        var CT = int.TryParse(Environment.GetEnvironmentVariable("RAFT_WAL_MICRO_THREADS"), out var ctv) && ctv > 0 ? ctv : 2;
        const int CN = 3000;
        var ops = new double[CT][];
        var threads = Enumerable.Range(0, CT).Select(ti => Task.Run(async () =>
        {
            var mine = new double[CN];
            for (var i = 0; i < CN; i++)
            {
                var t0 = msw.Elapsed.TotalMicroseconds;
                await microWal.AppendSingleAsync(frame, default);
                await microWal.CommitAsync(default);
                mine[i] = msw.Elapsed.TotalMicroseconds - t0;
            }
            ops[ti] = mine;
        })).ToArray();
        await Task.WhenAll(threads);
        var all = ops.SelectMany(o => o).OrderBy(x => x).ToArray();
        static double Pct(double[] xs, double p) => xs[(int)(xs.Length * p)];
        var slowCount = all.Count(x => x / 1000 > 5);
        Console.WriteLine($"[walMicro2] {CT} 线程并发 append+commit{metaLabel}：p50={Pct(all, 0.5) / 1000:F3}ms p99={Pct(all, 0.99) / 1000:F3}ms p999={Pct(all, 0.999) / 1000:F3}ms · >5ms 共 {slowCount} 个（{100.0 * slowCount / all.Length:F1}%）");
    }
    await microWal.DisposeAsync();
    microMetaTransport?.Dispose();
    microFs.Dispose();
}

// ★ 剖析保持窗（env RAFT_PROBE_HOLD=秒）：测量完成后节点继续跑（心跳稳态），供 dump/trace 取样
var holdSeconds = int.TryParse(Environment.GetEnvironmentVariable("RAFT_PROBE_HOLD"), out var hs) ? hs : 0;
if (holdSeconds > 0)
{
    Console.WriteLine($"保持窗 {holdSeconds}s（剖析取样）…");
    await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
}

foreach (var n in nodes) await n.DisposeAsync();
if (tcpHubs is not null)
{
    foreach (var h in tcpHubs) await h.DisposeAsync();   // 枢纽后关（在途发送可用的端点最后关）
}
else
{
    await hub!.DisposeAsync();
}
foreach (var fs in nodeFss) fs.Dispose();   // 探针持卷释放（local 卷 Dispose=目录递归清理）

/// <summary>探针节点（ApplyPipeline+RaftStateMachine 装配；store 两形态：
/// FsRaftStore 夹具（对抗套件同源——历史基线可比）/ TierWalRaftStore（raft×TierWal 产品接线端到端））。</summary>
internal sealed class ProbeNode : IAsyncDisposable
{
    private readonly NodeId _id;
    private readonly IRaftStore _store;
    private readonly IDisposable? _storeDisposable;   // TierWalRaftStore 的排他门
    private readonly TierWal? _wal;                   // tierwal 形态的生命周期所有（await Dispose）
    private readonly IDisposable? _metaTransport;     // 版本链运输面（wal 之后释放）
    private readonly ApplyPipeline _pipeline;
    private readonly RaftStateMachine _raft;

    internal ProbeNode(NodeId id, IRaftStore store, IDisposable? storeDisposable, TierWal? wal,
        IDisposable? metaTransport, ApplyPipeline pipeline, RaftStateMachine raft)
    {
        _id = id;
        _store = store;
        _storeDisposable = storeDisposable;
        _wal = wal;
        _metaTransport = metaTransport;
        _pipeline = pipeline;
        _raft = raft;
    }

    public NodeId Id => _id;
    public RaftStateMachine Raft => _raft;

    public static async Task<ProbeNode> CreateAsync(NodeId id, IFileSystem fs, IProtocolTransport transport,
        ClusterConfig config, bool useTierWal, int walCommitMs, bool useVersionedMeta = false)
    {
        TierWal? wal = null;
        IDisposable? storeDisposable = null;
        IDisposable? metaTransport = null;
        IRaftStore store;
        if (useTierWal)
        {
            // ★ TierWal 生产默认（DIO hints/Managed meta/组提交三维度）——raft×TierWal 端到端形态；
            //   RAFT_WAL_COMMIT_MS 覆写时间维度（-1=禁用后台循环，靠 raft 显式 CommitAsync 驱动）
            //   RAFT_WAL_META=versioned → 版本链（Transport + MetadataMetaTransport——meta.md §7 3a）
            var walOpts = new TierWalOptions()
                .WithWalName("wal")
                .WithCommitInterval(TimeSpan.FromMilliseconds(walCommitMs));
            if (useVersionedMeta)
            {
                var settings = new VersionedMetadataSettings(new StorageEngineOptions("wal.vmeta", 1024L * 1024 * 1024,
                    enableSegmentation: false, preallocateFile: false))
                {
                    PayloadSize = LogMetaHeaderCodec.StructSize + LogMetaPayloadCodec.StructSize
                        + TierWalOptions.Default.MetaOpaqueBytes + Crc32FooterCodec.StructSize,
                };
                metaTransport = new MetadataMetaTransport(fs, settings);
                walOpts = walOpts.WithMetaPolicyKind(MetaPolicyKind.Transport);
            }
            wal = useVersionedMeta
                ? await walOpts.Builder(fs).WithMetaTransport((IMetaTransport)metaTransport!).StartAsync()
                : await walOpts.Builder(fs).StartAsync();
            var adapter = new TierWalRaftStore(wal);
            store = adapter;
            storeDisposable = adapter;
        }
        else
        {
            store = new FsRaftStore(fs, "node");
        }
        await store.InitializeAsync();
        var pipeline = new ApplyPipeline(store, new NoopMachine(), ApplyPipelineOptions.Default);
        // ★ 攒批窗口（RAFT_PROBE_BATCH_WINDOW=毫秒，缺省 0=立即推）——真实网络摊销 RTT 的策略旋钮
        var batchWindowMs = double.TryParse(Environment.GetEnvironmentVariable("RAFT_PROBE_BATCH_WINDOW"), out var bw) && bw > 0 ? bw : 0;
        var lingerMs = int.TryParse(Environment.GetEnvironmentVariable("RAFT_PROBE_LINGER_MS"), out var lm) && lm > 0 ? lm : 0;
        var batchSize = int.TryParse(Environment.GetEnvironmentVariable("RAFT_PROBE_BATCH"), out var bs) && bs > 0 ? bs : 1000;
        // ★ 心跳/选举旋钮（二分诊断：停顿轮若 = 选举风暴，心跳↓/选举↑ 应改变停顿形态）
        var raftOpts = RaftOptions.Default;
        if (int.TryParse(Environment.GetEnvironmentVariable("RAFT_PROBE_HEARTBEAT_MS"), out var hb) && hb > 0)
            raftOpts = raftOpts.WithHeartbeatInterval(TimeSpan.FromMilliseconds(hb));
        if (int.TryParse(Environment.GetEnvironmentVariable("RAFT_PROBE_ELECTION_MS"), out var el) && el > 0)
            raftOpts = raftOpts.WithElectionTimeout(TimeSpan.FromMilliseconds(el), TimeSpan.FromMilliseconds(el * 2));
        raftOpts = raftOpts.WithReplication(new ReplicationPolicy { BatchWindow = TimeSpan.FromMilliseconds(batchWindowMs), LingerMilliseconds = lingerMs, BatchSize = batchSize });
        var raft = new RaftStateMachine(id, store, transport, pipeline, raftOpts);
        pipeline.SetConfigCallback(raft.PostConfigChanged);
        await pipeline.StartAsync();
        await raft.StartAsync(config);
        return new ProbeNode(id, store, storeDisposable, wal, metaTransport, pipeline, raft);
    }

    public async ValueTask DisposeAsync()
    {
        await _raft.DisposeAsync();
        await _pipeline.DisposeAsync();
        _storeDisposable?.Dispose();
        if (_wal is not null) await _wal.DisposeAsync();
        _metaTransport?.Dispose();
    }
}

/// <summary>Noop 状态机（零业务成本——纯协议层数字，对齐报告 NoopMachine）。</summary>
internal sealed class NoopMachine : IStateMachine
{
    public ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}

/// <summary>提升 Windows 定时器精度到 1ms（进程级——实验开关，见 Main 顶注）。</summary>
internal static class Winmm
{
    [System.Runtime.InteropServices.DllImport("winmm.dll", ExactSpelling = true)]
    public static extern uint timeBeginPeriod(uint period);
}
