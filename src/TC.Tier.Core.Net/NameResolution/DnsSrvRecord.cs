namespace TC.Tier.Core.Net.NameResolution;

/// <summary>
/// SRV 服务定位记录（RFC 2782——#498 服务发现主形态）。
/// </summary>
/// <param name="Target">目标主机名（绝对名——消费方另经地址解析获 IP；本客户端不做跟随）。</param>
/// <param name="Priority">优先级（值小优先——同 Target 组内先试）。</param>
/// <param name="Weight">权重（同优先级内的负载分布权重；0 = 不参与加权）。</param>
/// <param name="Port">服务端口。</param>
public readonly record struct DnsSrvRecord(string Target, ushort Priority, ushort Weight, ushort Port);
