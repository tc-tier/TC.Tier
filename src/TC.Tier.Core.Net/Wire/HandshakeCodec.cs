namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// 握手帧载荷编解码与协商规则（spec-12 §3.3 三步握手）。
/// <para>★ 载荷布局 [BinaryLayout] 声明（<see cref="HandshakeInit"/>/<see cref="HandshakeAck"/>，
///   32B/31B 定长——NodeId@0 为 §2 一等结构嵌套件 16B 原序直拷；ClusterTag 4B 集群归属在
///   握手期交换（身份不变、归属可变——错集群 fail-fast，§3.3））；读写由生成的
///   <c>HandshakeInitCodec</c>/<c>HandshakeAckCodec</c> 产出，本层只持协商语义
///   （版本区间/保留位/安全档校验、交集规则）。</para>
/// <para>★ 三步时序/超时/防降级判定归连接层（spec-12 §3.3/§3.4）。</para>
/// </summary>
public static class HandshakeCodec
{
    /// <summary>HandshakeInit 载荷定长（= 载荷布局 [StructLayout].Size——同源生成）。</summary>
    public static int InitPayloadSize => HandshakeInitCodec.StructSize;

    /// <summary>HandshakeAck 载荷定长（= 载荷布局 [StructLayout].Size——同源生成）。</summary>
    public static int AckPayloadSize => HandshakeAckCodec.StructSize;

    /// <summary>编码 HandshakeInit 载荷（写入 <paramref name="payload"/>，定长 <see cref="InitPayloadSize"/>）。</summary>
    /// <param name="payload">目标缓冲（≥ <see cref="InitPayloadSize"/> 字节）。</param>
    /// <param name="self">发起方节点 ID。</param>
    /// <param name="minVersion">本端支持头版本下界。</param>
    /// <param name="maxVersion">本端支持头版本上界（不得小于 <paramref name="minVersion"/>）。</param>
    /// <param name="features">本端特性位（<see cref="HandshakeFeatures"/>——保留位必须清零）。</param>
    /// <param name="clusterTag">本端集群归属标签。</param>
    /// <param name="nonce">8B 发起方随机数（Ack 原样回带——错配/重放甄别）。</param>
    /// <param name="security">安全形态（<see cref="HandshakeSecurity"/> 常量）。</param>
    /// <exception cref="ArgumentException">版本区间倒挂 / 保留特性位置位 / 安全形态未定义 / 缓冲不足。</exception>
    public static void WriteInit(Span<byte> payload, NodeId self, byte minVersion, byte maxVersion, byte features, uint clusterTag, ulong nonce, byte security)
    {
        ValidateVersionRange(minVersion, maxVersion);
        ValidateAdvertisedFeatures(features);
        ValidateSecurity(security);
        if (payload.Length < InitPayloadSize)
            throw new ArgumentException($"Init 载荷缓冲 {payload.Length} 不足（需 {InitPayloadSize}）。", nameof(payload));

        HandshakeInitCodec.Write(payload, new HandshakeInit(self, minVersion, maxVersion, features, clusterTag, nonce, security));
    }

    /// <summary>解码 HandshakeInit 载荷。</summary>
    /// <param name="payload">线载荷（定长 <see cref="InitPayloadSize"/> 字节）。</param>
    /// <returns>解码出的 Init 载荷（发起方 ID/版本区间/特性位/集群标签/nonce/安全形态）。</returns>
    /// <exception cref="FormatException">载荷定长不符 / 版本区间倒挂（对端协议违规——传输层 Error + 断连）。</exception>
    public static HandshakeInit ReadInit(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != InitPayloadSize)
            throw new FormatException($"Init 载荷长度 {payload.Length} ≠ {InitPayloadSize}。");
        var init = HandshakeInitCodec.Read(payload);
        ValidateSecurity(init.Security);
        if (init.MinVersion > init.MaxVersion) throw new FormatException($"对端版本区间倒挂：{init.MinVersion}..{init.MaxVersion}。");
        return init;
    }

    /// <summary>编码 HandshakeAck 载荷（定长 <see cref="AckPayloadSize"/>）。</summary>
    /// <param name="payload">目标缓冲（≥ <see cref="AckPayloadSize"/> 字节）。</param>
    /// <param name="self">应答方节点 ID。</param>
    /// <param name="selectedVersion">选定的头版本（双方版本区间的最高交集）。</param>
    /// <param name="intersectedFeatures">双方特性位按位与的交集。</param>
    /// <param name="clusterTagEcho">回显的发起方集群归属标签（发起方据此核对）。</param>
    /// <param name="initiatorNonce">原样回带的发起方随机数。</param>
    /// <param name="security">安全形态（<see cref="HandshakeSecurity"/> 常量）。</param>
    /// <exception cref="ArgumentException">保留特性位置位 / 安全形态未定义 / 缓冲不足。</exception>
    public static void WriteAck(Span<byte> payload, NodeId self, byte selectedVersion, byte intersectedFeatures, uint clusterTagEcho, ulong initiatorNonce, byte security)
    {
        ValidateAdvertisedFeatures(intersectedFeatures);
        ValidateSecurity(security);
        if (payload.Length < AckPayloadSize)
            throw new ArgumentException($"Ack 载荷缓冲 {payload.Length} 不足（需 {AckPayloadSize}）。", nameof(payload));

        HandshakeAckCodec.Write(payload, new HandshakeAck(self, selectedVersion, intersectedFeatures, clusterTagEcho, initiatorNonce, security));
    }

    /// <summary>解码 HandshakeAck 载荷。</summary>
    /// <param name="payload">线载荷（定长 <see cref="AckPayloadSize"/> 字节）。</param>
    /// <returns>解码出的 Ack 载荷（应答方 ID/选定版本/特性交集/标签回显/nonce 回带/安全形态）。</returns>
    /// <exception cref="FormatException">载荷定长不符 / 安全形态未定义。</exception>
    public static HandshakeAck ReadAck(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != AckPayloadSize)
            throw new FormatException($"Ack 载荷长度 {payload.Length} ≠ {AckPayloadSize}。");
        var ack = HandshakeAckCodec.Read(payload);
        ValidateSecurity(ack.Security);
        return ack;
    }

    /// <summary>
    /// 头版本协商（取双方区间交集的最高版；无交集 = false——Error 帧拒绝 + 断连）。
    /// </summary>
    /// <param name="minLocal">本端支持版本下界。</param>
    /// <param name="maxLocal">本端支持版本上界。</param>
    /// <param name="minRemote">对端支持版本下界。</param>
    /// <param name="maxRemote">对端支持版本上界。</param>
    /// <param name="selected">选定的版本（双方区间的最高交集；false 时为 0）。</param>
    /// <returns>true = 存在交集，<paramref name="selected"/> 为可选的最高版；false = 无交集（版本协商失败）。</returns>
    public static bool TryNegotiateVersion(byte minLocal, byte maxLocal, byte minRemote, byte maxRemote, out byte selected)
    {
        selected = 0;
        byte lower = Math.Max(minLocal, minRemote);
        byte upper = Math.Min(maxLocal, maxRemote);
        if (lower > upper) return false;
        selected = upper;
        return true;
    }

    /// <summary>特性位交集（双方都开才生效——按位与天然清除单侧/保留位）。</summary>
    /// <param name="local">本端特性位。</param>
    /// <param name="remote">对端特性位。</param>
    /// <returns>双方特性位的按位与（任一侧未开的特性被清除）。</returns>
    public static byte IntersectFeatures(byte local, byte remote) => (byte)(local & remote);

    /// <summary>集群归属核对（发起方核对 Ack 回显；应答方核对 Init——错集群 = Error + 断连 + 计数，§3.3）。</summary>
    /// <param name="local">本端配置的集群标签。</param>
    /// <param name="remote">对端到达帧携带/回显的标签。</param>
    /// <returns>true = 同集群。</returns>
    public static bool ClusterTagMatches(uint local, uint remote) => local == remote;

    private static void ValidateVersionRange(byte minVersion, byte maxVersion)
    {
        if (minVersion > maxVersion)
            throw new ArgumentException($"版本区间倒挂：{minVersion}..{maxVersion}。", nameof(maxVersion));
    }

    private static void ValidateAdvertisedFeatures(byte features)
    {
        if ((features & HandshakeFeatures.ReservedMask) != 0)
            throw new ArgumentException($"保留特性位（bit3-7）必须清零：0x{features:X2}。", nameof(features));
    }

    private static void ValidateSecurity(byte security)
    {
        if (security is not (HandshakeSecurity.Plaintext or HandshakeSecurity.KeyPair or HandshakeSecurity.MutualTls))
            throw new FormatException($"未定义安全形态：{security}。");
    }
}