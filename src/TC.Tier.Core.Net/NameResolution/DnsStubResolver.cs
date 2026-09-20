using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using TC.Tier.Core.Logging;

namespace TC.Tier.Core.Net.NameResolution;

/// <summary>
/// security-aware DNS stub resolver（#498——RFC 1035/2782/6891 客户端面全契约，零外部依赖叶子位）：
/// 查询（SRV/A/AAAA）＋解析＋TTL 缓存＋EDNS0/安全位。
/// <para>★ 传输：UDP:53 首选；TC 置位 → TCP 重试；超时 + <see cref="DnsStubResolverOptions.Retries"/>
/// 次（指数退避）；多 server 轮询容错（每次尝试轮换下一个——ServFail/超时跟随）。</para>
/// <para>★ 名展开（K8s CoreDNS 形态）：绝对名（尾点）直查；相对名按 ndots 阈值——点数 ≥ 阈值
/// 先绝对后 search，否则先 search 后绝对（resolv.conf / Windows 网卡配置自举）。</para>
/// <para>★ CNAME 跟随：深度上限 8，聚合 MinTtl（链上各跳取最小）；SRV 不跟随（RFC 2782：SRV 属主
/// 不得为别名）。记录集保持应答节顺序——同应答字节 → 同结果。</para>
/// <para>★ Non-goal（#498 永久排除）：DNSSEC 签名验证链与递归迭代解析（stub 以 AD 位消费递归层
/// 验证结论——<see cref="DnsSecurityStatus"/>）；DNS server 面；mDNS/LLMNR/DNS-SD 组播族。</para>
/// </summary>
public sealed class DnsStubResolver : IDisposable
{
    /// <summary>CNAME 跟随深度上限（#498 诉求 2）。</summary>
    private const int MaxCnameDepth = 8;

    private readonly DnsStubResolverOptions _options;
    private readonly ILogger? _logger;
    private readonly IReadOnlyList<IPEndPoint> _servers;
    private readonly List<string> _searchDomains;
    private readonly int _ndots;
    private readonly DnsCache? _cache;
    private int _roundRobin;
    private int _disposed;

    /// <summary>构造。</summary>
    /// <param name="options">选项（null = 全缺省——OS 自举 servers/search/ndots + CoreDNS 兼容缺省）。</param>
    /// <param name="logger">可选日志器。</param>
    public DnsStubResolver(DnsStubResolverOptions? options = null, ILogger? logger = null)
    {
        _options = options ?? new DnsStubResolverOptions();
        _options.Validate();
        _logger = logger;

        var probed = _options.Servers is null || _options.SearchDomains is null || _options.NdotsOverride is null
            ? DnsResolverConfig.Probe()
            : null;
        _servers = _options.Servers ?? probed!.Servers;
        _searchDomains = NormalizeSearchDomains(_options.SearchDomains ?? probed!.SearchDomains);
        _ndots = _options.NdotsOverride ?? probed!.Ndots;
        _cache = _options.CacheCapacity > 0 ? new DnsCache(_options.CacheCapacity) : null;
    }

    /// <summary>SRV 查询（服务发现主形态——RFC 2782）。</summary>
    /// <param name="service">服务名（绝对名带尾点；相对名走 search/ndots 展开）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>SRV 记录集 + 聚合 MinTtl + 安全位结论。</returns>
    public Task<DnsAnswer> ResolveSrvAsync(string service, CancellationToken ct = default)
        => ResolveAsync(service, DnsRecordType.SRV, ct);

    /// <summary>A 地址查询（IPv6 用 <see cref="ResolveAsync"/> + <see cref="DnsRecordType.AAAA"/>）。</summary>
    /// <param name="host">主机名。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>地址记录集 + 聚合 MinTtl + 安全位结论。</returns>
    public Task<DnsAnswer> ResolveAddressesAsync(string host, CancellationToken ct = default)
        => ResolveAsync(host, DnsRecordType.A, ct);

    /// <summary>标准查询入口（缓存 → 名展开 → 网络查询 → CNAME 跟随链）。</summary>
    /// <param name="name">查询名（尾点 = 绝对名；相对名走 search/ndots 展开）。</param>
    /// <param name="type">记录类型（A/AAAA/SRV 主形态；其余类型走原语但不保证 RDATA 解码）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>记录集 + 聚合 MinTtl + 安全位结论（NxDomain = 记录集空 + 负缓存 TTL，非异常）。</returns>
    /// <exception cref="IOException">全部尝试失败（超时/服务器失败——轮询耗尽）。</exception>
    public async Task<DnsAnswer> ResolveAsync(string name, DnsRecordType type, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Length > 253) throw new ArgumentException($"域名超长（{name.Length} > 253）。", nameof(name));

        var candidates = BuildCandidates(name);
        foreach (var candidate in candidates)
        {
            var aliases = ImmutableArray.CreateBuilder<string>();
            var aggregateTtl = TimeSpan.MaxValue;
            var current = candidate;
            var depth = 0;
            while (true)
            {
                DnsAnswer hop;
                if (_cache?.TryGet(current, type, out var cached) == true)
                    hop = cached;
                else
                    hop = await QueryNetworkAsync(current, type, ct).ConfigureAwait(false);
                aggregateTtl = Min(aggregateTtl, hop.TimeToLive);

                // 终点：终态记录已得 / 负向结论 / 无链可跟 / 深度到顶 / SRV 不跟随
                if (hop.Addresses.Length > 0 || hop.SrvRecords.Length > 0
                    || hop.Security.ResponseCode != DnsResponseCode.NoError
                    || hop.Aliases.Length == 0 || depth >= MaxCnameDepth || type == DnsRecordType.SRV)
                {
                    var ttl = aggregateTtl == TimeSpan.MaxValue ? hop.TimeToLive : aggregateTtl;
                    return hop with { Name = name, Aliases = aliases.ToImmutable(), TimeToLive = ttl };
                }

                aliases.Add(hop.Aliases[^1]);
                current = hop.Aliases[^1];
                depth++;
            }
        }

        throw new InvalidOperationException("unreachable：候选列表恒非空。");
    }

    /// <summary>释放（无持有资源——句柄级关闭语义；释放后查询抛 <see cref="ObjectDisposedException"/>）。</summary>
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    // ═══════════════ 网络查询 ═══════════════

    /// <summary>单名网络查询：编码 → 逐尝试（server 轮换）交换 → 语义分轨（NxDomain 负缓存 / ServFail 跟随下一轮）→ 缓存。</summary>
    /// <exception cref="IOException">全部尝试耗尽。</exception>
    private async Task<DnsAnswer> QueryNetworkAsync(string name, DnsRecordType type, CancellationToken ct)
    {
        var query = ArrayPool<byte>.Shared.Rent(DnsWire.MaxQueryBytes);
        try
        {
            var id = NextId();
            var queryLen = DnsWire.EncodeQuery(query, id, name, type,
                _options.EdnsPayloadSize, _options.RequestDnsSecOk);

            var attempts = _options.Retries + 1;
            IOException? lastFailure = null;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var server = _servers[(int)((uint)Interlocked.Increment(ref _roundRobin) % (uint)_servers.Count)];
                try
                {
                    var message = await ExchangeAsync(server, query.AsMemory(0, queryLen), id, ct).ConfigureAwait(false);
                    if (message.ResponseCode == DnsResponseCode.NxDomain)
                        return CacheNegative(name, type, message);
                    if (message.ResponseCode != DnsResponseCode.NoError)
                    {
                        // ServFail/Refused = 本 server 健康/策略问题——计失败跟随下一 server/重试
                        lastFailure = new IOException($"DNS 服务器 {server} 应答码 {message.ResponseCode}（{name}）。");
                    }
                    else
                    {
                        return CachePositive(name, type, message);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;   // 调用方取消传播；超时取消（token 已还）按失败计
                }
                catch (Exception ex)
                {
                    lastFailure = ex as IOException
                        ?? new IOException($"DNS 交换失败（server={server}，name={name}）。", ex);
                    _logger?.LogWarning("DNS 尝试失败（attempt={Attempt}/{Attempts}，server={Server}，name={Name}）：{Error}",
                        attempt + 1, attempts, server, name, lastFailure.Message);
                }

                if (attempt < attempts - 1)
                {
                    var backoff = TimeSpan.FromMilliseconds(
                        Math.Min(100d * (1 << attempt), _options.QueryTimeout.TotalMilliseconds));
                    await Task.Delay(backoff, ct).ConfigureAwait(false);
                }
            }

            throw lastFailure ?? new IOException($"DNS 查询失败（{attempts} 次尝试耗尽，{name}）。");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(query);
        }
    }

    /// <summary>单次交换：UDP → TC 置位走 TCP；ID 对账（错包丢弃续收）+ QR 位守卫。</summary>
    /// <exception cref="IOException">传输/协议失败。</exception>
    private async Task<DnsWire.Message> ExchangeAsync(IPEndPoint server, Memory<byte> query, ushort expectedId,
        CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.QueryTimeout);
        var timeoutToken = timeoutCts.Token;

        var response = ArrayPool<byte>.Shared.Rent(Math.Max(_options.EdnsPayloadSize, 512));
        try
        {
            // UDP 首选
            using (var socket = new Socket(server.AddressFamily, SocketType.Dgram, ProtocolType.Udp))
            {
                await socket.ConnectAsync(server, timeoutToken).ConfigureAwait(false);
                await socket.SendAsync(query, timeoutToken).ConfigureAwait(false);
                for (var receives = 0; receives < 4; receives++)   // 错 ID 包丢弃续收（共享端口段混淆防护面）
                {
                    var n = await socket.ReceiveAsync(response.AsMemory(), timeoutToken).ConfigureAwait(false);
                    if (n < DnsWire.HeaderBytes) throw new IOException($"DNS 应答过短（server={server}）。");
                    var flags = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan().Slice(2, 2));
                    if ((flags & DnsHeaderFlags.QueryResponse) == 0)
                        throw new IOException($"DNS 应答 QR 位未置（server={server}）。");
                    if (BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan()) != expectedId) continue;   // 错包——续收
                    if ((flags & DnsHeaderFlags.Truncated) != 0)
                    {
                        _logger?.LogDebug("DNS TC 截断——TCP 回退（server={Server}）", server);
                        break;   // TC 截断——TCP 重试
                    }
                    return ParseValidated(response.AsSpan()[..n], expectedId, server);
                }
            }

            // TCP 回退（TC 置位 / UDP 轮空）
            var tcp = await ExchangeTcpAsync(server, query, expectedId, timeoutToken).ConfigureAwait(false);
            return ParseValidated(tcp, expectedId, server);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(response);
        }
    }

    /// <summary>TCP 交换（2 字节大端长度前缀——RFC 1035 §4.2.2）。</summary>
    private static async Task<byte[]> ExchangeTcpAsync(IPEndPoint server, Memory<byte> query, ushort expectedId,
        CancellationToken timeoutToken)
    {
        using var client = new TcpClient(server.AddressFamily);
        await client.ConnectAsync(server, timeoutToken).ConfigureAwait(false);
        await using var stream = client.GetStream();

        var framed = new byte[query.Length + 2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)query.Length);
        query.Span.CopyTo(framed.AsSpan(2));
        await stream.WriteAsync(framed, timeoutToken).ConfigureAwait(false);

        var header = new byte[2];
        await ReadExactlyAsync(stream, header, timeoutToken).ConfigureAwait(false);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(header);
        var response = new byte[length];
        await ReadExactlyAsync(stream, response, timeoutToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt16BigEndian(response) != expectedId)
            throw new IOException($"DNS TCP 应答 ID 不符（server={server}）。");
        return response;
    }

    /// <summary>精确读满（不足即连接中断）。</summary>
    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var got = 0;
        while (got < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(got), ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("DNS TCP 连接中断（应答不完整）。");
            got += n;
        }
    }

    /// <summary>解析 + 语义对账（QR 位已在收包侧校验；ID 双验——TCP 路径已验、UDP 此处兜底）。</summary>
    private static DnsWire.Message ParseValidated(ReadOnlySpan<byte> response, ushort expectedId, IPEndPoint server)
    {
        var message = DnsWire.Decode(response);
        if (message.Id != expectedId)
            throw new IOException($"DNS 应答 ID 不符（server={server}）。");
        return message;
    }

    // ═══════════════ 缓存与构建 ═══════════════

    /// <summary>正向应答入库（NODATA = 空记录集按负缓存 TTL 收口——RFC 2308 无 SOA 参考的固定短窗）。</summary>
    private DnsAnswer CachePositive(string name, DnsRecordType type, DnsWire.Message message)
    {
        var security = new DnsSecurityStatus(message.ResponseCode, message.AuthenticatedData, message.CheckingDisabled);
        DnsAnswer answer;
        TimeSpan ttl;
        if (message.MinTtl == uint.MaxValue)
        {
            ttl = _options.NegativeCacheTtl;
            answer = DnsAnswer.Empty(name, type, ttl, security);
        }
        else
        {
            ttl = TimeSpan.FromSeconds(message.MinTtl);
            answer = new DnsAnswer(name, type,
                message.Srv?.ToImmutableArray() ?? ImmutableArray<DnsSrvRecord>.Empty,
                message.Addresses?.ToImmutableArray() ?? ImmutableArray<IPAddress>.Empty,
                message.Cnames?.ToImmutableArray() ?? ImmutableArray<string>.Empty,
                ttl, security);
        }
        _cache?.Put(name, type, answer, ttl);
        return answer;
    }

    /// <summary>负向应答入库（NxDomain——负缓存 TTL）。</summary>
    private DnsAnswer CacheNegative(string name, DnsRecordType type, DnsWire.Message message)
    {
        var answer = DnsAnswer.Empty(name, type, _options.NegativeCacheTtl,
            new DnsSecurityStatus(DnsResponseCode.NxDomain, message.AuthenticatedData, message.CheckingDisabled));
        _cache?.Put(name, type, answer, _options.NegativeCacheTtl);
        return answer;
    }

    // ═══════════════ 名展开与工具 ═══════════════

    /// <summary>候选名列表（K8s CoreDNS 形态——绝对名直查；相对名按 ndots：点数 ≥ 阈值先绝对后 search）。</summary>
    private List<string> BuildCandidates(string name)
    {
        if (name.EndsWith('.')) return [name];
        if (_searchDomains.Count == 0) return [name + "."];

        var dots = 0;
        foreach (var c in name)
            if (c == '.') dots++;

        var list = new List<string>(1 + _searchDomains.Count);
        if (dots >= _ndots) list.Add(name + ".");
        foreach (var domain in _searchDomains) list.Add($"{name}.{domain}");
        if (dots < _ndots) list.Add(name + ".");
        return list;
    }

    /// <summary>search 域归一（去尾点、去空项——展开时统一加点）。</summary>
    private static List<string> NormalizeSearchDomains(IReadOnlyList<string> domains)
    {
        var result = new List<string>(domains.Count);
        foreach (var domain in domains)
        {
            var trimmed = domain.Trim().TrimEnd('.');
            if (trimmed.Length > 0 && !result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                result.Add(trimmed);
        }
        return result;
    }

    /// <summary>TTL 聚合最小值（MaxValue 哨兵 = 未初始化）。</summary>
    private static TimeSpan Min(TimeSpan left, TimeSpan right)
    {
        if (left == TimeSpan.MaxValue) return right;
        if (right == TimeSpan.MaxValue) return left;
        return left <= right ? left : right;
    }

    /// <summary>事务 ID（Random.Shared 线程安全）。</summary>
    private static ushort NextId() => (ushort)Random.Shared.Next(0, ushort.MaxValue + 1);
}
