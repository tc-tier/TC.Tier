using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using TC.Tier.Core.Net.NameResolution;
using Xunit;

namespace TC.Tier.Core.Net.Tests.NameResolution;

/// <summary>
/// DNS stub resolver 回环验收（#498 验收判据）——本地 UDP+TCP 同端口测试服务器喂 canned 应答：
/// SRV 多记录＋weight、TC→TCP 回退、NXDOMAIN 负缓存、ndots/search 展开顺序、
/// EDNS0 OPT＋DO 请求置位＋AD 位透传、多 server 容错、CNAME 跟随聚合、确定性。
/// </summary>
public sealed class DnsStubResolverTests
{
    // ═══════════════ SRV 多记录＋weight（验收 3）═══════════════

    [Fact]
    public async Task ResolveSrv_MultiRecord_WeightsInOrder()
    {
        using var server = new DnsTestServer();
        server.RespondWith(q => DnsTestServer.SrvResponse(q,
            (10, 100, 8080, "i-1.test."),
            (10, 50, 8081, "i-2.test.")));
        using var resolver = NewResolver(server);

        var answer = await resolver.ResolveSrvAsync("app.prod.svc.");
        server.LoopFault.Should().BeNull("服务器循环故障会静默吞响应：{0}", server.LoopFault);
        answer.Security.ResponseCode.Should().Be(DnsResponseCode.NoError);
        answer.SrvRecords.Should().HaveCount(2);
        answer.SrvRecords[0].Should().Be(new DnsSrvRecord("i-1.test", 10, 100, 8080), "wire 解析名无尾点（根 label 不物化）");
        answer.SrvRecords[1].Should().Be(new DnsSrvRecord("i-2.test", 10, 50, 8081), "weight 保持应答节序");
        answer.TimeToLive.Should().Be(TimeSpan.FromSeconds(60));
    }

    // ═══════════════ TC→TCP 回退（验收 2）═══════════════

    [Fact]
    public async Task Resolve_TcTruncated_FallsBackToTcp()
    {
        using var server = new DnsTestServer();
        server.UdpHandler = q => DnsTestServer.RawResponse(q, flags: 0x8200);   // TC 置位 + 空应答节
        server.TcpHandler = q => DnsTestServer.AResponse(q, IPAddress.Parse("10.1.2.3"));
        using var resolver = NewResolver(server);

        var answer = await resolver.ResolveAddressesAsync("tc.test.");
        answer.Addresses.Should().ContainSingle().Which.Should().Be(IPAddress.Parse("10.1.2.3"),
            "TC 置位应经 TCP 重取完整应答");
        TcpHits(server).Should().BeGreaterThanOrEqualTo(1, "TCP 腿被走到");
    }

    // ═══════════════ NXDOMAIN 负缓存（验收 4）═══════════════

    [Fact]
    public async Task Resolve_NxDomain_NegativeCached_SecondCallNoServerHit()
    {
        using var server = new DnsTestServer();
        server.RespondWith(q => DnsTestServer.RawResponse(q, flags: 0x8183));   // NXDOMAIN
        using var resolver = NewResolver(server);

        var first = await resolver.ResolveAddressesAsync("missing.test.");
        first.Security.ResponseCode.Should().Be(DnsResponseCode.NxDomain, "负向结论以应答码外显（非异常）");
        first.Addresses.Should().BeEmpty();

        var second = await resolver.ResolveAddressesAsync("missing.test.");
        second.Security.ResponseCode.Should().Be(DnsResponseCode.NxDomain);
        UdpHits(server).Should().Be(1, "第二次命中负缓存——服务器零请求");
    }

    // ═══════════════ ndots/search 展开顺序（验收 5）═══════════════

    [Fact]
    public async Task Resolve_RelativeName_LowDots_SearchFirst()
    {
        using var server = new DnsTestServer();
        server.RespondWith(q => DnsTestServer.AResponse(q, IPAddress.Parse("10.0.0.9")));
        using var resolver = NewResolver(server, search: ["svc.cluster.local"], ndots: 1);

        await resolver.ResolveAddressesAsync("web");   // 0 点 < 1 → 先 search 后绝对
        QueryNames(server).Should().Contain("web.svc.cluster.local");
        QueryNames(server)[^1].Should().Be("web.svc.cluster.local");
    }

    [Fact]
    public async Task Resolve_RelativeName_HighDots_AbsoluteFirst()
    {
        using var server = new DnsTestServer();
        server.RespondWith(q => DnsTestServer.AResponse(q, IPAddress.Parse("10.0.0.9")));
        using var resolver = NewResolver(server, search: ["svc.cluster.local"], ndots: 1);

        await resolver.ResolveAddressesAsync("a.b");   // 1 点 ≥ 1 → 先绝对
        QueryNames(server).Should().Contain("a.b");
        QueryNames(server)[^1].Should().Be("a.b");
    }

    // ═══════════════ EDNS0 OPT + DO + AD 透传（验收 6/7）═══════════════

    [Fact]
    public async Task Resolve_Edns0AndDoInQuery_AdBitSurfaced()
    {
        using var server = new DnsTestServer();
        server.RespondWith(q => DnsTestServer.AResponse(q, IPAddress.Parse("10.9.9.9"), authenticatedData: true));
        using var resolver = NewResolver(server, dnsSecOk: true);

        var answer = await resolver.ResolveAddressesAsync("sec.test.");

        var lastQuery = server.LastQuery!;
        var arCount = BinaryPrimitives.ReadUInt16BigEndian(lastQuery.AsSpan(10));
        arCount.Should().Be(1, "EDNS0 OPT 伪记录在 additional 节");
        var nameEnd = 12;
        while (lastQuery[nameEnd] != 0) nameEnd += 1 + lastQuery[nameEnd];
        nameEnd += 1 + 4 + 1;   // 问题根 + QTYPE(2) + QCLASS(2) + OPT 根名
        BinaryPrimitives.ReadUInt16BigEndian(lastQuery.AsSpan(nameEnd)).Should().Be(41, "OPT TYPE");
        var optTtl = BinaryPrimitives.ReadUInt32BigEndian(lastQuery.AsSpan(nameEnd + 4));
        (optTtl & 0x8000u).Should().Be(0x8000u, "DO 位置位");

        answer.Security.AuthenticatedData.Should().BeTrue("AD 位随记录集外显");
    }

    // ═══════════════ 多 server 容错 ═══════════════

    [Fact]
    public async Task Resolve_FirstServerDead_FailsOverToSecond()
    {
        using var server = new DnsTestServer();
        server.RespondWith(q => DnsTestServer.AResponse(q, IPAddress.Parse("10.2.2.2")));
        var dead = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1);   // 保留端口——必拒
        using var resolver = NewResolver(dead, server.EndPoint);

        var answer = await resolver.ResolveAddressesAsync("failover.test.");
        answer.Addresses.Should().ContainSingle().Which.Should().Be(IPAddress.Parse("10.2.2.2"), "首 server 失联跟随下一");
    }

    // ═══════════════ CNAME 跟随（聚合 MinTtl + 链外显）═══════════════

    [Fact]
    public async Task ResolveAddresses_CnameChain_FollowsAndAggregates()
    {
        using var server = new DnsTestServer();
        server.RespondWith(q =>
        {
            var name = DnsTestServer.QueryName(q);
            return name == "alias.test"
                ? DnsTestServer.CnameResponse(q, "real.test.")
                : DnsTestServer.AResponse(q, IPAddress.Parse("10.3.3.3"));
        });
        using var resolver = NewResolver(server);

        var answer = await resolver.ResolveAddressesAsync("alias.test.");
        answer.Aliases.Should().Equal(["real.test"], "wire 解析名无尾点");
        answer.Addresses.Should().ContainSingle().Which.Should().Be(IPAddress.Parse("10.3.3.3"));
        answer.TimeToLive.Should().Be(TimeSpan.FromSeconds(10), "聚合 MinTtl = 链上各跳最小");
    }

    // ═══════════════ 确定性（验收 7）═══════════════

    [Fact]
    public async Task Resolve_Deterministic_SameAnswerEquivalentResult()
    {
        using var server = new DnsTestServer();
        server.RespondWith(q => DnsTestServer.SrvResponse(q, (5, 10, 9042, "db.test.")));
        using var first = NewResolver(server);
        using var second = new DnsStubResolver(NewOptions(server));

        var left = await first.ResolveSrvAsync("cass.test.");
        var right = await second.ResolveSrvAsync("cass.test.");
        right.Security.Should().Be(left.Security);
        right.SrvRecords.Should().Equal(left.SrvRecords);
        right.TimeToLive.Should().Be(left.TimeToLive);
    }

    // ═══════════════ fixture ═══════════════

    private static DnsStubResolverOptions NewOptions(DnsTestServer server,
        IReadOnlyList<string>? search = null, int? ndots = null, bool dnsSecOk = false)
        => new()
        {
            Servers = [server.EndPoint],
            QueryTimeout = TimeSpan.FromSeconds(2),
            Retries = 1,
            SearchDomains = search ?? [],
            NdotsOverride = ndots ?? 1,
            RequestDnsSecOk = dnsSecOk,
        };

    private static DnsStubResolver NewResolver(DnsTestServer server,
        IReadOnlyList<string>? search = null, int? ndots = null, bool dnsSecOk = false)
        => new(NewOptions(server, search, ndots, dnsSecOk));

    private static DnsStubResolver NewResolver(params IPEndPoint[] servers)
        => new(new DnsStubResolverOptions { Servers = servers, Retries = 1, SearchDomains = [], NdotsOverride = 1 });

    private static int UdpHits(DnsTestServer server) => server.GetUdpHits();
    private static int TcpHits(DnsTestServer server) => server.GetTcpHits();
    private static IReadOnlyList<string> QueryNames(DnsTestServer server) => server.GetQueryNames();

    /// <summary>
    /// 本地 DNS 测试服务器（UDP+TCP 同端口；canned 应答 + 查询记录面——仅测试内自建，零外部依赖）。
    /// </summary>
    private sealed class DnsTestServer : IDisposable
    {
        private readonly UdpClient _udp;
        private readonly TcpListener _tcp;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _workers = [];
        private readonly List<string> _queryNames = [];
        private int _udpHits;
        private int _tcpHits;

        /// <summary>UDP 应答器（null = 静默吞）。</summary>
        public Func<byte[], byte[]>? UdpHandler { get; set; }

        /// <summary>TCP 应答器（TC 回退腿）。</summary>
        public Func<byte[], byte[]>? TcpHandler { get; set; }

        /// <summary>最后一条查询原文（EDNS0/DO 断言面）。</summary>
        public byte[]? LastQuery { get; private set; }

        public IPEndPoint EndPoint { get; }

        public int GetUdpHits() => Volatile.Read(ref _udpHits);
        public int GetTcpHits() => Volatile.Read(ref _tcpHits);
        public IReadOnlyList<string> GetQueryNames() { lock (_queryNames) return [.. _queryNames]; }

        public void RespondWith(Func<byte[], byte[]> handler) => UdpHandler = handler;

        public DnsTestServer()
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            EndPoint = (IPEndPoint)_udp.Client.LocalEndPoint!;
            _tcp = new TcpListener(IPAddress.Loopback, EndPoint.Port);
            _tcp.Start();
            _workers.Add(Task.Run(() => UdpLoopAsync(_cts.Token)));
            _workers.Add(Task.Run(() => TcpLoopAsync(_cts.Token)));
        }

        public Exception? LoopFault { get; private set; }

        private async Task UdpLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var result = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _udpHits);
                    try
                    {
                        RecordQuery(result.Buffer);
                        var handler = UdpHandler;
                        if (handler is null) continue;
                        var response = handler(result.Buffer);
                        if (response is not null)
                            await _udp.SendAsync(response, result.RemoteEndPoint, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LoopFault = ex;   // handler 故障可见（单查询失败不杀循环）
                    }
                }
            }
            catch (Exception ex) { LoopFault = ex; }
        }

        private async Task TcpLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _tcp.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                Interlocked.Increment(ref _tcpHits);
                var worker = Task.Run(async () =>
                {
                    try
                    {
                        await using var stream = client.GetStream();
                        var header = new byte[2];
                        await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false);
                        var length = BinaryPrimitives.ReadUInt16BigEndian(header);
                        var query = new byte[length];
                        await ReadExactlyAsync(stream, query, ct).ConfigureAwait(false);
                        RecordQuery(query);
                        var response = TcpHandler?.Invoke(query);
                        if (response is not null)
                        {
                            var framed = new byte[response.Length + 2];
                            BinaryPrimitives.WriteUInt16BigEndian(framed, (ushort)response.Length);
                            response.CopyTo(framed.AsSpan(2));
                            await stream.WriteAsync(framed, ct).ConfigureAwait(false);
                        }
                    }
                    catch (Exception) when (ct.IsCancellationRequested) { /* 收尾竞态 */ }
                    catch (IOException) { /* 客户端断开 */ }
                    finally { client.Dispose(); }
                }, ct);
                _workers.Add(worker);
            }
        }

        /// <summary>记录查询（名字断言面 + 原文留存）。</summary>
        private void RecordQuery(byte[] query)
        {
            LastQuery = query;
            var name = QueryName(query);
            lock (_queryNames) _queryNames.Add(name);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _udp.Dispose(); } catch (Exception) { }
            try { _tcp.Stop(); } catch (Exception) { }
            try { Task.WaitAll([.. _workers], TimeSpan.FromSeconds(2)); } catch (Exception) { }
            _cts.Dispose();
        }

        // ═══ canned 应答构造（raw wire——真字节口径）═══

        /// <summary>裸应答（自定义 flags——TC/NXDOMAIN 形态；AN/AR=0，回显问题节）。</summary>
        public static byte[] RawResponse(byte[] query, ushort flags)
        {
            var response = new byte[query.Length];
            query.CopyTo(response, 0);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), flags);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6), 0);   // ANCOUNT=0
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(10), 0);  // ARCOUNT=0
            return response;
        }

        /// <summary>A 应答（AD 可置）。</summary>
        public static byte[] AResponse(byte[] query, IPAddress address, bool authenticatedData = false)
        {
            var flags = (ushort)(0x8180 | (authenticatedData ? 0x0020 : 0));
            return Assemble(query, flags, [MakeAnswer((ushort)DnsRecordType.A, 30, address.GetAddressBytes())]);
        }

        /// <summary>CNAME 应答。</summary>
        public static byte[] CnameResponse(byte[] query, string target)
            => Assemble(query, 0x8180, [MakeAnswer((ushort)DnsRecordType.CNAME, 10, EncodeName(target))]);

        /// <summary>SRV 应答（多记录；target 为绝对名）。</summary>
        public static byte[] SrvResponse(byte[] query,
            params (ushort Priority, ushort Weight, ushort Port, string Target)[] records)
        {
            var answers = new List<byte[]>();
            foreach (var (priority, weight, port, target) in records)
            {
                var name = EncodeName(target);
                var rdata = new byte[6 + name.Length];
                rdata[0] = (byte)(priority >> 8);
                rdata[1] = (byte)priority;
                rdata[2] = (byte)(weight >> 8);
                rdata[3] = (byte)weight;
                rdata[4] = (byte)(port >> 8);
                rdata[5] = (byte)port;
                name.CopyTo(rdata, 6);
                answers.Add(MakeAnswer((ushort)DnsRecordType.SRV, 60, rdata));
            }
            return Assemble(query, 0x8180, answers);
        }

        /// <summary>单条应答记录字节（属主 = 压缩指针 0xC00C 指回问题名）。</summary>
        private static byte[] MakeAnswer(ushort type, uint ttl, byte[] rdata)
        {
            // 固定段 12 = 属主指针(2) + TYPE(2) + CLASS(2) + TTL(4) + RDLENGTH(2)
            var answer = new byte[12 + rdata.Length];
            answer[0] = 0xC0; answer[1] = 0x0C;
            BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(2), type);
            BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(4), 1);   // IN
            BinaryPrimitives.WriteUInt32BigEndian(answer.AsSpan(6), ttl);
            BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(10), (ushort)rdata.Length);
            rdata.CopyTo(answer.AsSpan(12));
            return answer;
        }

        /// <summary>组装应答：头（flags/AN/AR 覆写）+ 问题节 + 应答节——应答节先于 additional
        /// （真实服务器节序；查询自带的 EDNS OPT 丢弃——stub 不需要回显）。</summary>
        private static byte[] Assemble(byte[] query, ushort flags, List<byte[]> answers)
        {
            var qdEnd = 12;
            while (query[qdEnd] != 0) qdEnd += 1 + query[qdEnd];
            qdEnd += 5;   // 根 + QTYPE(2) + QCLASS(2)

            var total = answers.Sum(a => a.Length);
            var response = new byte[qdEnd + total];
            query.AsSpan(0, qdEnd).CopyTo(response);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), flags);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), 1);   // QDCOUNT
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6), (ushort)answers.Count);
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(10), 0);  // ARCOUNT=0
            var pos = qdEnd;
            foreach (var answer in answers)
            {
                answer.CopyTo(response, pos);
                pos += answer.Length;
            }
            return response;
        }

        /// <summary>名字节编码（labels + 根）。</summary>
        private static byte[] EncodeName(string name)
        {
            using var ms = new MemoryStream();
            foreach (var label in name.TrimEnd('.').Split('.'))
            {
                ms.WriteByte((byte)label.Length);
                foreach (var c in label) ms.WriteByte((byte)c);
            }
            ms.WriteByte(0);
            return ms.ToArray();
        }

        /// <summary>从查询提问题名（断言/路由用）。</summary>
        public static string QueryName(byte[] query)
        {
            var sb = new StringBuilder();
            var pos = 12;
            while (pos < query.Length && query[pos] != 0)
            {
                var labelLen = query[pos];
                if (sb.Length > 0) sb.Append('.');
                for (var i = 1; i <= labelLen; i++) sb.Append((char)query[pos + i]);
                pos += 1 + labelLen;
            }
            return sb.ToString();
        }

        private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
        {
            var got = 0;
            while (got < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(got), ct).ConfigureAwait(false);
                if (n <= 0) throw new IOException("测试 TCP 流提前结束。");
                got += n;
            }
        }
    }
}
