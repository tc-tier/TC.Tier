using System.Net;
using System.Net.NetworkInformation;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;

namespace TC.Tier.Core.Net.NameResolution;

/// <summary>
/// 解析器操作系统自举配置（internal——#498 诉求 5）：servers / search 域 / ndots 阈值。
/// <para>★ Unix：resolv.conf（nameserver/search/domain/options ndots——K8s CoreDNS 形态兼容），
/// 文件访问经 TierFs（零 BCL IO 纪律）；Windows：网卡 DNS 配置（suffix + 服务器，ndots 缺省 1）。
/// 探测失败回退 loopback:53 + 无 search——显式 <see cref="DnsStubResolverOptions.Servers"/> 永远优先。</para>
/// <para>★ 进程级缓存（Lazy 一次探测）——F9 漂移语义针对记录/端点，resolv.conf 变更随进程重启生效。</para>
/// </summary>
/// <param name="Servers">DNS 服务器列表。</param>
/// <param name="SearchDomains">search 域（无尾点形态——展开侧补点）。</param>
/// <param name="Ndots">ndots 阈值（相对名点数 ≥ 阈值先绝对后 search）。</param>
internal sealed record DnsResolverConfig(IReadOnlyList<IPEndPoint> Servers, IReadOnlyList<string> SearchDomains, int Ndots)
{
    /// <summary>回退配置（探测失败——loopback:53，无 search）。</summary>
    public static DnsResolverConfig Fallback { get; } =
        new([new IPEndPoint(IPAddress.Loopback, 53)], [], 1);

    private static readonly Lazy<DnsResolverConfig> Cached =
        new(ProbeOnce, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>探测（进程级一次）。</summary>
    public static DnsResolverConfig Probe() => Cached.Value;

    private static DnsResolverConfig ProbeOnce()
    {
        try
        {
            return OperatingSystem.IsWindows() ? ProbeWindows() : ProbeUnix();
        }
        catch
        {
            return Fallback;   // 自举失败不致命——显式 Servers 或回退仍可解析
        }
    }

    /// <summary>Windows：Up 态网卡的 DNS 服务器并集 + 主 DNS 后缀（ndots 缺省 1——Windows 无公开 search 列表面）。</summary>
    private static DnsResolverConfig ProbeWindows()
    {
        var servers = new List<IPEndPoint>();
        var suffixes = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            var props = nic.GetIPProperties();
            foreach (var address in props.DnsAddresses)
                if (IPAddress.TryParse(address.ToString(), out var ip) && !servers.Contains(new IPEndPoint(ip, 53)))
                    servers.Add(new IPEndPoint(ip, 53));
            var suffix = props.DnsSuffix;
            if (!string.IsNullOrEmpty(suffix) && !suffixes.Contains(suffix)) suffixes.Add(suffix);
        }

        return servers.Count > 0
            ? new DnsResolverConfig(servers, suffixes, 1)
            : Fallback;
    }

    /// <summary>Unix：resolv.conf（nameserver/search/domain/options ndots）——经 TierFs 读（零 BCL IO 纪律）。</summary>
    private static DnsResolverConfig ProbeUnix()
    {
        using var fs = DiskFileSystem.Open("/");
        if (!fs.Exists("etc/resolv.conf")) return Fallback;

        var servers = new List<IPEndPoint>();
        var search = new List<string>();
        var ndots = 1;   // glibc 缺省
        using (var h = fs.Open("etc/resolv.conf", new FileOpenOptions { Mode = FileOpenMode.OpenExisting }))
        {
            var bytes = new byte[h.Length];
            var n = h.Read(0, bytes);
            foreach (var rawLine in System.Text.Encoding.UTF8.GetString(bytes, 0, n).Split('\n'))
            {
                var line = rawLine.Trim();
                var hash = line.IndexOf('#');
                if (hash >= 0) line = line[..hash].Trim();   // 行内注释
                if (line.Length == 0) continue;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                switch (parts[0])
                {
                    case "nameserver" when parts.Length >= 2 && IPAddress.TryParse(parts[1], out var ip):
                        if (!servers.Contains(new IPEndPoint(ip, 53))) servers.Add(new IPEndPoint(ip, 53));
                        break;
                    case "search":
                        for (var i = 1; i < parts.Length; i++)
                            if (!search.Contains(parts[i])) search.Add(parts[i]);
                        break;
                    case "domain" when parts.Length >= 2:
                        search.Clear();
                        search.Add(parts[1]);
                        break;
                    case "options" when parts.Length >= 2:
                        foreach (var option in parts.Skip(1))
                            if (option.StartsWith("ndots:", StringComparison.Ordinal)
                                && int.TryParse(option["ndots:".Length..], out var parsed))
                                ndots = parsed;
                        break;
                }
            }
        }

        return servers.Count > 0
            ? new DnsResolverConfig(servers, search, ndots)
            : Fallback;
    }
}
