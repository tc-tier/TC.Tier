using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;

namespace TC.Tier.Core.Net.Security;

/// <summary>
/// 证书 NodeId 绑定校验（spec-12 §3.4 mTLS 档——SAN <c>nid:&lt;hex32&gt;</c> URI 条目绑定）。
/// <para>★ 证书全生命周期归装配方（推荐短期证书 + 自动轮换——吊销被"等它短命"取代）；
///   本类只做"到达证书 ↔ 节点身份"的绑定判定：SAN 提取 + 链校验决策由调用方组合。</para>
/// </summary>
public static class CertificateNodeId
{
    /// <summary>SAN 绑定条目前缀（<c>nid:</c> + NodeId hex32 文本形式）。</summary>
    public const string UriPrefix = "nid:";

    /// <summary>证书 SAN 生成（装配期自签/CA 签发用——NodeId → URI 条目）。</summary>
    /// <param name="node">节点 ID。</param>
    /// <returns>SAN URI 条目文本（<c>nid:</c> + NodeId hex32）。</returns>
    public static string BuildSanUri(NodeId node) => UriPrefix + node.ToString();

    /// <summary>提取证书 SAN 里的全部 <c>nid:</c> NodeId（无 SAN/无 nid 条目 = 空数组）。</summary>
    /// <param name="certificate">对端证书。</param>
    /// <returns>证书声明的全部 NodeId（畸形 SAN 按无绑定处理——返回空表）。</returns>
    public static List<NodeId> ExtractNodeIds(X509Certificate certificate)
    {
        var result = new List<NodeId>();
        foreach (var ext in ((X509Certificate2)certificate).Extensions)
        {
            if (!string.Equals(ext.Oid?.Value, "2.5.29.17", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                // SAN = SEQUENCE OF GeneralName；URI = context tag [6]（0x86 primitive）
                var reader = new AsnReader(ext.RawData, AsnEncodingRules.DER);
                var sequence = reader.ReadSequence();
                while (sequence.HasData)
                {
                    if (sequence.PeekTag().TagValue == 6 && sequence.PeekTag().TagClass == TagClass.ContextSpecific)
                    {
                        var uri = sequence.ReadCharacterString(UniversalTagNumber.IA5String, new Asn1Tag(TagClass.ContextSpecific, 6));
                        if (uri.StartsWith(UriPrefix, StringComparison.Ordinal)
                            && NodeId.TryParse(uri.AsSpan(UriPrefix.Length), out var nodeId))
                            result.Add(nodeId);
                    }
                    else
                    {
                        sequence.ReadEncodedValue();   // 跳过非 URI 条目（DNS/IP/email 等）
                    }
                }
            }
            catch (AsnContentException)
            {
                // 畸形 SAN——按无绑定处理（调用方判定拒绝）
            }
        }
        return result;
    }

    /// <summary>绑定判定：证书 SAN 是否恰好包含目标节点（缺失/多绑定 = 拒绝——证书身份唯一性）。</summary>
    /// <param name="certificate">对端证书。</param>
    /// <param name="expected">预期节点 ID（成员制 = 静态已知；null = 地址制——返回证书声明的唯一 NodeId 或 null）。</param>
    /// <param name="declared">证书声明身份（地址制时输出——调用方用于后续绑定校验）。</param>
    /// <returns>true = 绑定成立（地址制时 <paramref name="declared"/> 携带证书声明身份）。</returns>
    public static bool IsBoundTo(X509Certificate certificate, NodeId? expected, out NodeId declared)
    {
        var ids = ExtractNodeIds(certificate);
        if (ids.Count == 1)
        {
            declared = ids[0];
            return expected is null || ids[0] == expected;
        }
        declared = NodeId.Empty;
        return false;   // 无 nid 条目/多绑定——证书未按规范签发
    }
}
