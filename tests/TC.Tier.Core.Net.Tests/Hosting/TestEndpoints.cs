using System.Net;
using System.Net.Sockets;

namespace TC.Tier.Core.Net.Tests.Hosting;

/// <summary>Hosting 装配门测试共享端点辅助（loopback 端口预保留——builder 装配需显式端口）。</summary>
internal static class TestEndpoints
{
    /// <summary>预保留一个 loopback 端口（bind 后释放——立即复用竞态可忽略的测试形态）。</summary>
    internal static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>loopback 端点。</summary>
    internal static IPEndPoint Loopback(int port) => new(IPAddress.Loopback, port);
}
