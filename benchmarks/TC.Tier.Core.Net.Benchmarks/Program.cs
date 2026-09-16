using System.Diagnostics;
using System.Net;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Transport;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Core.Net.Transport.Tcp;

namespace TC.Tier.Core.Net.Benchmarks;

/// <summary>
/// Core.Net 形态面 loopback 基准（spec-12 §12 性能门——同机同轮净测量）：
/// <para> - 三形态 × 双介质同轮对照：数据报（单向尽力送达）/ 请求回调（echo RTT + 滑动窗口
///   流水线）/ 流式（bulk 帧流）——TCP loopback ∥ InProcess（传输税分离）。</para>
/// <para> - 全部走公开消费面（IProtocolTransport 三形态 API——基准即消费方视角）。</para>
/// <para> - 测量纪律：AutoFlush 输出不进测量区间 / Stopwatch 净计时 / GC 计数对照 /
///   收端到齐确认才停表（吞吐含末帧达递、不含装配与预热）。</para>
/// <para>用法：<c>dotnet run -c Release --project benchmarks/TC.Tier.Core.Net.Benchmarks</c></para>
/// </summary>
public class Program
{
    private const byte DatagramSmall = 0x64;   // 数据报 64B 档
    private const byte DatagramLarge = 0x67;   // 数据报 64KiB 档
    private const byte RequestEcho = 0x65;     // 请求回调（RTT 与流水线共用）
    private const byte StreamLarge = 0x66;     // 流式 64KiB 帧档
    private const byte StreamSmall = 0x68;     // 流式 1KiB 帧档

    public static async Task<int> Main()
    {
        var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(writer);
        Console.WriteLine("=== Core.Net 形态面 loopback 基准（2 节点 · TCP ∥ InProcess 同轮对照）===");

        await using var tcp = await CreateTcpRigAsync();
        await using var ipc = CreateInProcessRig();
        var rigs = new (string Medium, Rig Rig)[] { ("TCP", tcp), ("InProcess", ipc) };

        foreach (var (_, rig) in rigs)
            rig.Server.RegisterRequestHandler(RequestEcho, new EchoHandler());   // RTT 与流水线共用

        await DatagramAsync(rigs, DatagramSmall, 64, count: 100_000);
        await DatagramAsync(rigs, DatagramLarge, 64 * 1024, count: 2_000);
        await RequestRttAsync(rigs, iterations: 2_000);
        await RequestPipelineAsync(rigs, window: 256, total: 50_000);
        await StreamBulkAsync(rigs, StreamLarge, 64 * 1024, frames: 2_000);
        await StreamBulkAsync(rigs, StreamSmall, 1024, frames: 20_000);
        return 0;
    }

    // ══ 数据报：单向吞吐（尽力送达——收端到齐确认）══

    private static async Task DatagramAsync((string Medium, Rig Rig)[] rigs, byte protocol, int size, int count)
    {
        foreach (var (medium, rig) in rigs)
        {
            var payload = new byte[size];
            var handler = new CountingHandler(count + 1);   // +1 探测帧
            rig.Server.RegisterProtocol(protocol, handler);
            await rig.Client.SendDatagramAsync(rig.ServerId, protocol, payload);   // 探测——链路就绪
            await handler.Ready.WaitAsync(TimeSpan.FromSeconds(10));

            var gc0 = GC.CollectionCount(0);
            var gc1 = GC.CollectionCount(1);
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < count; i++)
                await rig.Client.SendDatagramAsync(rig.ServerId, protocol, payload);
            await handler.Done.WaitAsync(TimeSpan.FromSeconds(60));
            sw.Stop();

            Console.WriteLine($"[{medium}] 数据报 {SizeLabel(size)} × {count}: {sw.Elapsed.TotalMilliseconds:F2}ms = " +
                $"{count / sw.Elapsed.TotalSeconds:F0} msg/s | GC0 +{GC.CollectionCount(0) - gc0} GC1 +{GC.CollectionCount(1) - gc1}");
        }
    }

    // ══ 请求回调：串行 RTT（echo 往返延迟分布）══

    private static async Task RequestRttAsync((string Medium, Rig Rig)[] rigs, int iterations)
    {
        foreach (var (medium, rig) in rigs)
        {
            var payload = new byte[64];
            for (var i = 0; i < 50; i++)   // 预热（JIT/关联表）
                await rig.Client.SendRequestAsync(rig.ServerId, RequestEcho, payload);

            var samples = new double[iterations];
            var gc0 = GC.CollectionCount(0);
            var gc1 = GC.CollectionCount(1);
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
            {
                var t0 = Stopwatch.GetTimestamp();
                var reply = await rig.Client.SendRequestAsync(rig.ServerId, RequestEcho, payload);
                samples[i] = Stopwatch.GetElapsedTime(t0).TotalMicroseconds / 1000.0;
                if (reply.Length != payload.Length)
                    throw new InvalidOperationException($"回显长度 {reply.Length} ≠ {payload.Length}");
            }
            sw.Stop();
            Array.Sort(samples);

            Console.WriteLine($"[{medium}] 请求 RTT 64B × {iterations}: 均值 {samples.Average():F3}ms p50 {Percentile(samples, 0.50):F3}ms " +
                $"p99 {Percentile(samples, 0.99):F3}ms | GC0 +{GC.CollectionCount(0) - gc0} GC1 +{GC.CollectionCount(1) - gc1}");
        }
    }

    // ══ 请求回调：滑动窗口流水线（在途窗口 = 真实并发形态）══

    private static async Task RequestPipelineAsync((string Medium, Rig Rig)[] rigs, int window, int total)
    {
        foreach (var (medium, rig) in rigs)
        {
            var payload = new byte[64];
            for (var i = 0; i < window; i++)   // 预热
                await rig.Client.SendRequestAsync(rig.ServerId, RequestEcho, payload);

            using var gate = new SemaphoreSlim(window);
            var gc0 = GC.CollectionCount(0);
            var gc1 = GC.CollectionCount(1);
            var tasks = new Task[total];
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < total; i++)
            {
                await gate.WaitAsync();
                tasks[i] = SendOneAsync(rig, gate, payload);
            }
            await Task.WhenAll(tasks);
            sw.Stop();

            Console.WriteLine($"[{medium}] 请求流水线 64B 窗口{window} × {total}: {sw.Elapsed.TotalMilliseconds:F2}ms = " +
                $"{total / sw.Elapsed.TotalSeconds:F0} op/s | GC0 +{GC.CollectionCount(0) - gc0} GC1 +{GC.CollectionCount(1) - gc1}");
        }
    }

    private static async Task SendOneAsync(Rig rig, SemaphoreSlim gate, byte[] payload)
    {
        try
        {
            var reply = await rig.Client.SendRequestAsync(rig.ServerId, RequestEcho, payload);
            if (reply.Length != payload.Length)
                throw new InvalidOperationException($"回显长度 {reply.Length} ≠ {payload.Length}");
        }
        finally
        {
            gate.Release();
        }
    }

    // ══ 流式：bulk 帧流（单会话顺序写 → 对端逐帧消费到齐）══

    private static async Task StreamBulkAsync((string Medium, Rig Rig)[] rigs, byte protocol, int frameSize, int frames)
    {
        foreach (var (medium, rig) in rigs)
        {
            var frame = new byte[frameSize];
            var acceptor = new CollectingStreamAcceptor();
            rig.Server.RegisterStreamAcceptor(protocol, acceptor);

            // 预热会话（StreamOpen/Write/ReadAll 的 JIT 一次性成本）
            acceptor.Arm();
            await using (var warm = await rig.Client.OpenStreamAsync(rig.ServerId, protocol))
            {
                for (var i = 0; i < 8; i++) await warm.WriteAsync(frame);
                await warm.CompleteAsync();
            }
            await acceptor.Done.WaitAsync(TimeSpan.FromSeconds(10));

            acceptor.Arm();
            var gc0 = GC.CollectionCount(0);
            var gc1 = GC.CollectionCount(1);
            var sw = Stopwatch.StartNew();
            await using (var stream = await rig.Client.OpenStreamAsync(rig.ServerId, protocol))
            {
                for (var i = 0; i < frames; i++)
                    await stream.WriteAsync(frame);
                await stream.CompleteAsync();
            }
            var (gotFrames, gotBytes) = await acceptor.Done.WaitAsync(TimeSpan.FromSeconds(120));
            sw.Stop();
            if (gotFrames != frames || gotBytes != (long)frames * frameSize)
                throw new InvalidOperationException($"流校验失败：帧 {gotFrames}/{frames} 字节 {gotBytes}");

            var totalMb = frames * frameSize / 1024.0 / 1024.0;
            Console.WriteLine($"[{medium}] 流式 {SizeLabel(frameSize)}帧 × {frames}（{totalMb:F0}MB）: {sw.Elapsed.TotalMilliseconds:F2}ms = " +
                $"{totalMb / sw.Elapsed.TotalSeconds:F0} MB/s（{frames / sw.Elapsed.TotalSeconds:F0} 帧/s）| " +
                $"GC0 +{GC.CollectionCount(0) - gc0} GC1 +{GC.CollectionCount(1) - gc1}");
        }
    }

    private static double Percentile(double[] sorted, double q) =>
        sorted[Math.Clamp((int)(q * sorted.Length), 0, sorted.Length - 1)];

    private static string SizeLabel(int size) => size >= 1024 ? $"{size / 1024.0:F0}KiB " : $"{size}B ";

    // ══ 装配 ══

    /// <summary>两节点装配（client=发端，server=收端；Dispose 经委托收口）。</summary>
    private sealed class Rig : IAsyncDisposable
    {
        public IProtocolTransport Client { get; }
        public IProtocolTransport Server { get; }
        public NodeId ClientId { get; }
        public NodeId ServerId { get; }
        private readonly Func<ValueTask> _dispose;

        public Rig(IProtocolTransport client, IProtocolTransport server, NodeId clientId, NodeId serverId, Func<ValueTask> dispose)
        {
            Client = client;
            Server = server;
            ClientId = clientId;
            ServerId = serverId;
            _dispose = dispose;
        }

        public ValueTask DisposeAsync() => _dispose();
    }

    /// <summary>TCP loopback 装配（成员制——较大方监听、较小方拨号，双向 PeerConnected 就绪）。</summary>
    private static async Task<Rig> CreateTcpRigAsync()
    {
        var a = NodeId.NewRandom();
        var b = NodeId.NewRandom();
        var (clientId, serverId) = a.CompareTo(b) < 0 ? (a, b) : (b, a);   // 较小方拨号

        var server = new ClusterTransport(serverId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0),
                    new Dictionary<NodeId, IPEndPoint> { [clientId] = new(IPAddress.Loopback, 0) })
                .WithRequest(timeout: TimeSpan.FromSeconds(60)));
        server.Start();
        var listen = server.LocalEndPoint!;

        var client = new ClusterTransport(clientId,
            TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint> { [serverId] = listen })
                .WithRequest(timeout: TimeSpan.FromSeconds(60)));
        client.Start();

        var clientUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serverUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.PeerConnected += _ => clientUp.TrySetResult();
        server.PeerConnected += _ => serverUp.TrySetResult();
        await Task.WhenAll(clientUp.Task.WaitAsync(TimeSpan.FromSeconds(10)), serverUp.Task.WaitAsync(TimeSpan.FromSeconds(10)));

        return new Rig(client, server, clientId, serverId, async () =>
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        });
    }

    /// <summary>InProcess 装配（同构对照——hub 直通，零网络栈）。</summary>
    private static Rig CreateInProcessRig()
    {
        var hub = new InProcessTransportHub(TransportOptions.Default(null, new Dictionary<NodeId, IPEndPoint>())
            .WithRequest(timeout: TimeSpan.FromSeconds(60)));
        var clientId = NodeId.NewRandom();
        var serverId = NodeId.NewRandom();
        var client = hub.Register(clientId);
        var server = hub.Register(serverId);
        return new Rig(client, server, clientId, serverId, () => hub.DisposeAsync());
    }

    // ══ handlers ══

    private sealed class CountingHandler(int expected) : IDatagramHandler
    {
        private int _seen;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Ready => _ready.Task;
        public Task Done => _done.Task;

        public void OnDatagram(NodeId from, ReadOnlyMemory<byte> payload)
        {
            var n = Interlocked.Increment(ref _seen);
            if (n == 1) _ready.TrySetResult();
            if (n == expected) _done.TrySetResult();
        }
    }

    private sealed class EchoHandler : IRequestHandler
    {
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
        {
            var send = reply.ReplyAsync(payload);
            if (!send.IsCompletedSuccessfully) _ = ObserveAsync(send);   // 异步写路径——观测异常防静默吞
        }

        private static async Task ObserveAsync(ValueTask send) => await send;
    }

    private sealed class CollectingStreamAcceptor : IStreamAcceptor
    {
        private TaskCompletionSource<(int Frames, long Bytes)>? _done;

        public Task<(int Frames, long Bytes)> Done => (_done ?? throw new InvalidOperationException("先 Arm 再开流")).Task;

        /// <summary>武装一次会话采集（每会话一臂——预热与正式会话独立确认）。</summary>
        public void Arm() => _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void OnStream(NodeId from, IWireStream stream, ReadOnlyMemory<byte> openPayload)
        {
            var done = _done;
            if (done is null)
            {
                _ = ObserveDisposeAsync(stream);   // 未武装——直接拒绝
                return;
            }
            _ = ConsumeAsync(stream, done);   // 受控消费循环（终态经 Done 观测——IStreamAcceptor 契约形态）
        }

        private static async Task ConsumeAsync(IWireStream stream, TaskCompletionSource<(int, long)> done)
        {
            try
            {
                var frames = 0;
                long bytes = 0;
                await foreach (var frame in stream.ReadAllAsync())
                {
                    frames++;
                    bytes += frame.Length;
                }
                done.TrySetResult((frames, bytes));
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        }

        private static async Task ObserveDisposeAsync(IWireStream stream) => await stream.DisposeAsync();
    }
}
