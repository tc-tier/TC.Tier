using System.Net;
using System.Net.Sockets;
using System.Text;
using TC.Tier.Core.IO;

namespace TC.Tier.Core.Net.Transport;

/// <summary>
/// 服务发现源（二期-F8 NETGAP-041——引导/发现的可插拔源）：每次 Discover 返回当前已知的
/// (NodeId, EndPoint) 集合。实现归使用方/外部系统（K8s headless DNS、Consul KV 导出文件、
/// 静态配置）——Core.Net 只定义源契约与汇聚循环。
/// <para>★ 语义：发现为 <b>add-only</b>——汇聚仅 AddPeer 新增/更新端点；移除归退役/治理编排
/// （D8/C2），发现源缺失项不触发移除（避免源抖动误删在途成员）。</para>
/// </summary>
public interface IPeerDiscoverySource
{
    /// <summary>源名称（诊断/日志）。</summary>
    string Name { get; }

    /// <summary>发现一次（实现自行容错——抛异常由汇聚循环捕获计数续跑）。</summary>
    Task<IReadOnlyDictionary<NodeId, IPEndPoint>> DiscoverAsync(CancellationToken ct = default);
}

/// <summary>
/// 文件发现源（NETGAP-041——K8s ConfigMap/Consul KV 导出文件的常见形态）：
/// 每行 <c>hexNodeId=host:port</c>，<c>#</c> 注释行跳过；每次 Discover 重读文件（热载）。
/// <para>★ 零 BCL IO 纪律（设计 §11）——文件访问经 <see cref="IFileSystem"/>，不自持 BCL File。</para>
/// </summary>
public sealed class FilePeerDiscoverySource : IPeerDiscoverySource
{
    private readonly IFileSystem _fs;
    private readonly string _path;

    /// <param name="fs">承载文件系统（发现文件所在介质——local/mem/remote 皆可）。</param>
    /// <param name="path">发现文件相对路径（文本行 <c>hexNodeId=host:port</c>）。</param>
    public FilePeerDiscoverySource(IFileSystem fs, string path) => (_fs, _path) = (fs, path);

    /// <summary>发现源名称（fs:&lt;path&gt;——日志/诊断标识）。</summary>
    public string Name => $"fs:{_path}";

    /// <summary>发现一次：重读发现文件并解析（每次调用热载——文件不存在 = 空集，畸形行跳过）。</summary>
    /// <param name="ct">取消令牌（保留签名同构——读取为同步小文件）。</param>
    /// <returns>解析出的（NodeId → 端点）快照（同 ID 后行覆盖前行）。</returns>
    public Task<IReadOnlyDictionary<NodeId, IPEndPoint>> DiscoverAsync(CancellationToken ct = default)
    {
        var result = new Dictionary<NodeId, IPEndPoint>();
        if (_fs.Exists(_path))
        {
            using var h = _fs.Open(_path, new FileOpenOptions { Mode = FileOpenMode.OpenExisting });
            var bytes = new byte[h.Length];
            var n = h.Read(0, bytes);
            foreach (var line in Encoding.UTF8.GetString(bytes, 0, n).Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                var eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;
                if (!NodeId.TryParse(trimmed[..eq].Trim(), out var id)) continue;
                if (!IPEndPoint.TryParse(trimmed[(eq + 1)..].Trim(), out var ep)) continue;
                result[id] = ep;
            }
        }
        return Task.FromResult<IReadOnlyDictionary<NodeId, IPEndPoint>>(result);
    }
}

/// <summary>
/// DNS 发现源（二期-F8/F9——well-known 引导节点的主机名 A 记录解析）：
/// NodeId 静态已知（集群引导配置），端点 = 解析地址 + 配置端口；每次 Discover 重解析
 /// （DNS 漂移/端点迁移跟随——F9 的最小实现面）。
/// </summary>
public sealed class DnsPeerDiscoverySource : IPeerDiscoverySource
{
    private readonly NodeId _nodeId;
    private readonly string _host;
    private readonly int _port;

    /// <summary>构造。</summary>
    /// <param name="nodeId">本端标识（注册表键）。</param>
    /// <param name="host">DNS 主机名。</param>
    /// <param name="port">端口。</param>
    public DnsPeerDiscoverySource(NodeId nodeId, string host, int port)
    {
        _nodeId = nodeId;
        _host = host;
        _port = port;
    }

    /// <summary>发现源名称（dns:&lt;host&gt;:&lt;port&gt;——日志/诊断标识）。</summary>
    /// <summary>发现源名称（dns:&lt;host&gt;:&lt;port&gt;——日志/诊断标识）。</summary>
    public string Name => $"dns:{_host}:{_port}";

    /// <summary>发现一次：解析主机名地址（偏好 IPv4，无则取首个解析地址——DNS 漂移/端点迁移跟随）。</summary>
    /// <param name="ct">取消令牌（传播到 DNS 解析）。</param>
    /// <returns>解析结果（NodeId 固定为构造注入值 → 端点 = 解析地址 + 配置端口；解析失败 = 抛，由汇聚循环捕获计数）。</returns>
    public async Task<IReadOnlyDictionary<NodeId, IPEndPoint>> DiscoverAsync(CancellationToken ct = default)
    {
        var addresses = await Dns.GetHostAddressesAsync(_host, ct).ConfigureAwait(false);
        var result = new Dictionary<NodeId, IPEndPoint>();
        var first = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                    ?? addresses.FirstOrDefault();
        if (first is not null)
            result[_nodeId] = new IPEndPoint(first, _port);
        return result;
    }
}

/// <summary>
/// 发现汇聚服务（二期-F8）：周期轮询全部源 → 合并 → <see cref="IPeerRegistry.AddPeer"/>
/// （新增/端点更新；**不移除**——add-only 语义见 <see cref="IPeerDiscoverySource"/>）。
/// </summary>
public sealed class PeerDiscoveryService : IAsyncDisposable
{
    private readonly IPeerRegistry _registry;
    private readonly IReadOnlyList<IPeerDiscoverySource> _sources;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private long _pollCount;
    private long _errorCount;

    /// <summary>构造。</summary>
    /// <param name="registry">对端注册表（发现目标——ClusterTransport/QUIC 实现）。</param>
    /// <param name="sources">发现源（至少一个）。</param>
    /// <param name="interval">轮询周期（缺省 30s）。</param>
    public PeerDiscoveryService(IPeerRegistry registry, IEnumerable<IPeerDiscoverySource> sources,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _sources = sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));
        _interval = interval ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>启动轮询循环。</summary>
    public void Start()
    {
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>单次轮询（测试/手动面——发现项 AddPeer 新增/更新，不移除）。</summary>
    /// <param name="ct">取消令牌（传播到各源发现调用）。</param>
    /// <returns>完成时全部源已轮询完毕（单源失败计入错误计数不阻断其余源——见 <see cref="Diagnostics"/>）。</returns>
    public async Task PollOnceAsync(CancellationToken ct = default)
    {
        foreach (var source in _sources)
        {
            try
            {
                var discovered = await source.DiscoverAsync(ct).ConfigureAwait(false);
                foreach (var (id, ep) in discovered)
                {
                    var exists = _registry.Peers.TryGetValue(id, out var current);
                    if (!exists || current is null || !current.Equals(ep))
                        _registry.AddPeer(id, ep);
                }
                Interlocked.Increment(ref _pollCount);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _errorCount);   // 单源失败不阻断其他源/后续轮询
            }
        }
    }

    /// <summary>轮询/错误计数（诊断）。</summary>
    public (long Polls, long Errors) Diagnostics =>
        (Interlocked.Read(ref _pollCount), Interlocked.Read(ref _errorCount));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, ct).ConfigureAwait(false);
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // 循环必须存活——单轮异常续跑
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) { /* 循环退出超时——进程收尾 */ }
        }
        _cts.Dispose();
    }
}
