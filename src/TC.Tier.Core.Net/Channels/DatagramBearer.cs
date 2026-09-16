namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 数据报通道承载（spec-12 §5.1/§7——通道语义（尽力送达）与介质（TCP/UDP）正交；
/// bearer 是协议域注册时的发送面声明，接收侧介质无差别）。
/// </summary>
public enum DatagramBearer
{
    /// <summary>TCP 帧流承载（默认——可靠有序传输介质上的尽力送达语义）。</summary>
    Tcp = 0,

    /// <summary>
    /// UDP 数据报承载（对端 UDP 端点经握手通告已知且载荷在单报预算内时走 UDP；
    /// 端点未知/超预算/本端未开 UDP 时回落 TCP 承载，尽力送达语义不变）。
    /// </summary>
    Udp = 1,
}
