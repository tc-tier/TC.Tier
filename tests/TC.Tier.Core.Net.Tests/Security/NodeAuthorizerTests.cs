using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Security;
using TC.Tier.Core.Net.Transport;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Security;

/// <summary>
/// 授权钩子 + 审计（二期-H3/H4——验证矩阵 H 行）：链路准入拒绝（审计 authz_link_denied）、
/// 域级请求拒绝（审计 authz_request_denied + 对端超时语义）、连接治理拒绝审计（H4+H3 协同）。
/// </summary>
public class NodeAuthorizerTests
{
    private sealed class RecordingAuditSink : IAuditSink
    {
        private readonly ConcurrentQueue<(string Event, string Actor, string Detail)> _events = new();
        public void OnAudit(string @event, string actor, string detail)
            => _events.Enqueue((@event, actor, detail));
        public long CountOf(string @event) => _events.Count(e => e.Event == @event);
        public bool Any(Func<(string Event, string Actor, string Detail), bool> predicate) => _events.Any(predicate);
    }

    /// <summary>按节点 ID 拒绝链路 + 域级请求拒绝的授权器（测试桩——策略模型归产品）。</summary>
    private sealed class DenyListAuthorizer(NodeId deniedLink, NodeId? deniedRequestDomain = null) : INodeAuthorizer
    {
        public NodeId? LastDeniedRequest { get; private set; }

        public bool AuthorizeLink(NodeId remote) => remote != deniedLink;

        public bool AuthorizeRequest(NodeId from, byte protocolId)
        {
            if (deniedRequestDomain is { } domain && from == deniedRequestDomain)
            {
                LastDeniedRequest = from;
                return false;
            }
            return true;
        }
    }

    private static int ReservePort()
    {
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        socket.Close();
        return port;
    }

    /// <summary>H4：链路准入拒绝——被拒节点 ConnectAsync 失败 + H3 审计 authz_link_denied。</summary>
    [Fact]
    public async Task Authorizer_DenyLink_RejectsAndAudits()
    {
        var port = ReservePort();
        var serverId = NodeId.NewRandom();
        var deniedId = NodeId.NewRandom();
        var audit = new RecordingAuditSink();
        var authorizer = new DenyListAuthorizer(deniedLink: deniedId);

        var server = new ClusterTransport(serverId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port), new Dictionary<NodeId, IPEndPoint>()),
            authorizer: authorizer, audit: audit);
        server.Start();
        var client = new ClusterTransport(deniedId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()));
        try
        {
            // ★ 订阅先于连接：服务端授权拒绝发生在客户端握手完成之后——断链经 FIN 传导
            var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            client.PeerGone += _ => gone.TrySetResult();

            var act = async () => await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            // 握手面：服务端 Ack 先回、随后授权拒绝断连——ConnectAsync 可能成功也可能已断
            try { await act(); } catch (NetIOException) { /* 已在连接期拒绝 */ }

            await gone.Task.WaitAsync(TimeSpan.FromSeconds(5));   // 服务端授权拒绝 → 断链传导
            await Task.Delay(100);   // 审计事件落
            audit.CountOf("authz_link_denied").Should().BeGreaterThanOrEqualTo(1, "H3 审计——授权拒绝事件");
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    /// <summary>H4：域级请求准入——被拒域请求丢弃（对端超时语义）+ 审计 authz_request_denied。</summary>
    [Fact]
    public async Task Authorizer_DenyRequestDomain_DropsAndAudits()
    {
        var port = ReservePort();
        var serverId = NodeId.NewRandom();
        var clientId = NodeId.NewRandom();
        var audit = new RecordingAuditSink();
        const byte secretDomain = 0x71;
        var authorizer = new DenyListAuthorizer(deniedLink: NodeId.Empty, deniedRequestDomain: clientId);

        var server = new ClusterTransport(serverId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port), new Dictionary<NodeId, IPEndPoint>()),
            authorizer: authorizer, audit: audit);
        server.Start();
        var client = new ClusterTransport(clientId,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, 0), new Dictionary<NodeId, IPEndPoint>()));
        client.Start();
        try
        {
            await client.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            // 被拒域（0x71）请求——服务端丢弃不应答（对端超时自愈）
            var act = async () => await client.SendRequestAsync(serverId, secretDomain, new byte[] { 0x01 },
                    new TC.Tier.Core.Net.Channels.RequestOptions { Timeout = TimeSpan.FromMilliseconds(400) })
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await act.Should().ThrowAsync<TimeoutException>("域级授权拒绝——无应答");
            audit.CountOf("authz_request_denied").Should().BeGreaterThanOrEqualTo(1, "H3 审计——域级拒绝事件");
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }
}
