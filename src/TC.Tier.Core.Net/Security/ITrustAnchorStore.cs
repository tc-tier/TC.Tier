namespace TC.Tier.Core.Net.Security;

/// <summary>
/// 信任锚存储端口（spec-12 §3.4 信任锚子形态——NodeId↔公钥绑定关系的持久化面）。
/// <para>★ Core.Net 零 IO——实现归组装层（内存/文件/密管）：钉扎 = 只读表（装配期一次给全）；
///   TOFU = 可读写（首连学习写回——SSH known_hosts 式，首连有中间人窗口，半可信内网形态）。</para>
/// </summary>
public interface ITrustAnchorStore
{
    /// <summary>查询节点绑定的公钥。</summary>
    /// <param name="node">节点 ID。</param>
    /// <param name="publicKey">绑定公钥（<see cref="NodeKeyPair.PublicKeySize"/>B compressed；33B 全零 = 未绑定）。</param>
    /// <returns>true = 已绑定（钉扎/已学习）。</returns>
    bool TryGet(NodeId node, out ReadOnlyMemory<byte> publicKey);

    /// <summary>学习绑定（TOFU 首连——已有绑定不覆盖：公钥变化 = 拒绝并断连，提示人工介入）。</summary>
    /// <param name="node">节点 ID。</param>
    /// <param name="publicKey">对端公钥（33B compressed）。</param>
    void Save(NodeId node, ReadOnlyMemory<byte> publicKey);
}

/// <summary>只读钉扎表（信任强度 = mTLS——地址表/成员表条目在 KeyPair 档携带对端公钥）。</summary>
public sealed class PinnedTrustStore(NodeId[] nodes, byte[][] publicKeys) : ITrustAnchorStore
{
    private readonly Dictionary<NodeId, byte[]> _pinned = Build(nodes, publicKeys);

    private static Dictionary<NodeId, byte[]> Build(NodeId[] nodes, byte[][] keys)
    {
        if (nodes.Length != keys.Length)
            throw new ArgumentException("节点表与公钥表等长。");
        var map = new Dictionary<NodeId, byte[]>(nodes.Length);
        for (var i = 0; i < nodes.Length; i++)
        {
            if (keys[i].Length != NodeKeyPair.PublicKeySize)
                throw new ArgumentException($"公钥须 {NodeKeyPair.PublicKeySize}B compressed（节点 {nodes[i]}）。");
            map[nodes[i]] = keys[i];
        }
        return map;
    }

    /// <inheritdoc/>
    /// <param name="node">节点 ID。</param>
    /// <param name="publicKey">绑定公钥（33B compressed；未绑定 = 空切片）。</param>
    /// <returns>true = 已钉扎；false = 未绑定。</returns>
    public bool TryGet(NodeId node, out ReadOnlyMemory<byte> publicKey)
    {
        if (_pinned.TryGetValue(node, out var key))
        {
            publicKey = key;
            return true;
        }
        publicKey = default;
        return false;
    }

    /// <inheritdoc/>
    /// <param name="node">节点 ID。</param>
    /// <param name="publicKey">对端公钥（被忽略——钉扎表不可写）。</param>
    /// <remarks>钉扎表不可写——TOFU 形态用可写实现。</remarks>
    public void Save(NodeId node, ReadOnlyMemory<byte> publicKey)
        => throw new NotSupportedException("钉扎表不可写（信任变更 = 改配置重新装配——spec-12 §3.4）。");
}

/// <summary>内存 TOFU 存储（测试/半可信内网缺省；持久化形态组装层实现端口）。</summary>
public sealed class InMemoryTrustStore : ITrustAnchorStore
{
    private readonly object _lock = new();
    private readonly Dictionary<NodeId, byte[]> _learned = [];

    /// <inheritdoc/>
    /// <param name="node">节点 ID。</param>
    /// <param name="publicKey">绑定公钥（33B compressed；未学习 = 空切片）。</param>
    /// <returns>true = 已学习（TOFU 首连写回过）；false = 未见。</returns>
    public bool TryGet(NodeId node, out ReadOnlyMemory<byte> publicKey)
    {
        lock (_lock)
        {
            if (_learned.TryGetValue(node, out var key))
            {
                publicKey = key;
                return true;
            }
        }
        publicKey = default;
        return false;
    }

    /// <inheritdoc/>
    /// <param name="node">节点 ID。</param>
    /// <param name="publicKey">对端公钥（33B compressed——首连学习写回）。</param>
    public void Save(NodeId node, ReadOnlyMemory<byte> publicKey)
    {
        lock (_lock) _learned[node] = publicKey.ToArray();
    }
}
