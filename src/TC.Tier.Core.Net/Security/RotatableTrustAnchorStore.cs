using System;

namespace TC.Tier.Core.Net.Security;

/// <summary>
/// 可轮换信任锚（二期-H1——多锚并存过渡期）：
/// 轮换 = <see cref="BeginRotation"/> 把旧钥降级为带过期窗的 previous、新钥立即成为
/// current——过渡期内新旧两把公钥都被接受（轮换非全网原子：先升级节点已用新钥、
/// 后升级节点仍持旧钥），窗过旧钥移除，收敛回单锚。
/// <para>★ 兼容：作为 <see cref="ITrustAnchorStore"/> 使用时只见 current（TryGet 单钥
/// 契约不变）；存量为 <see cref="ISupportsKeyRotation"/> 的比对方（PeerLink）走
/// <see cref="IsTrusted"/> 并存判定。</para>
/// <para>★ 纪律保持：Save 不覆盖既有绑定（TOFU 首连语义）；钉扎强度不降——轮换是
/// 显式编排动作（由密钥管理面经本口驱动），不是握手旁路。</para>
/// </summary>
public sealed class RotatableTrustAnchorStore : ITrustAnchorStore, ISupportsKeyRotation
{
    private readonly object _lock = new();
    private readonly Dictionary<NodeId, AnchorEntry> _anchors = [];
    private Func<DateTime> _utcNow;

    /// <param name="utcNow">时钟注入口（测试确定性——缺省 UTC now）。</param>
    public RotatableTrustAnchorStore(Func<DateTime>? utcNow = null)
        => _utcNow = utcNow ?? (static () => DateTime.UtcNow);

    /// <summary>当前生效公钥（兼容单钥契约——轮换过渡期只暴露新钥）。</summary>
    /// <param name="node">节点 ID。</param>
    /// <param name="publicKey">当前生效公钥（无绑定时为 default）。</param>
    /// <returns>true = 该节点已有绑定（<paramref name="publicKey"/> = current 新钥）；false = 无绑定。</returns>
    public bool TryGet(NodeId node, out ReadOnlyMemory<byte> publicKey)
    {
        lock (_lock)
        {
            if (_anchors.TryGetValue(node, out var entry))
            {
                publicKey = entry.Current;
                return true;
            }
        }
        publicKey = default;
        return false;
    }

    /// <summary>TOFU 首绑（已有绑定不覆盖——覆盖面走 <see cref="BeginRotation"/>）。</summary>
    /// <param name="node">节点 ID。</param>
    /// <param name="publicKey">首绑公钥（长度须 = <see cref="NodeKeyPair.PublicKeySize"/>）。</param>
    /// <exception cref="ArgumentException">公钥长度不符。</exception>
    /// <exception cref="InvalidOperationException">该节点已有绑定（拒绝静默覆盖）。</exception>
    public void Save(NodeId node, ReadOnlyMemory<byte> publicKey)
    {
        ValidateKey(publicKey);
        lock (_lock)
        {
            if (_anchors.ContainsKey(node))
                throw new InvalidOperationException($"节点 {node} 已有绑定（换钥走 BeginRotation——拒绝静默覆盖）。");
            _anchors[node] = new AnchorEntry(publicKey.ToArray(), Previous: null, PreviousExpiresUtc: null);
        }
    }

    /// <inheritdoc/>
    public bool IsTrusted(NodeId node, ReadOnlyMemory<byte> presentedKey)
    {
        lock (_lock)
        {
            if (!_anchors.TryGetValue(node, out var entry)) return false;
            if (entry.Current.AsSpan().SequenceEqual(presentedKey.Span)) return true;
            // 过渡窗：旧钥仍被接受（惰性过期——到点即拒）
            if (entry.Previous is { } prev
                && (entry.PreviousExpiresUtc is not { } expiry || _utcNow() < expiry))
                return prev.AsSpan().SequenceEqual(presentedKey.Span);
            return false;
        }
    }

    /// <inheritdoc/>
    public void BeginRotation(NodeId node, ReadOnlyMemory<byte> newKey, TimeSpan graceWindow)
    {
        ValidateKey(newKey);
        if (graceWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(graceWindow), "过渡窗须为正（零 = 无并存，直换无回退余地）。");
        lock (_lock)
        {
            if (!_anchors.TryGetValue(node, out var entry))
                throw new InvalidOperationException($"节点 {node} 无既有绑定——首绑走 Save（TOFU/钉扎），不走轮换。");
            if (entry.Current.AsSpan().SequenceEqual(newKey.Span))
                return;   // 同钥重入幂等
            _anchors[node] = new AnchorEntry(
                newKey.ToArray(),
                Previous: entry.Current,
                PreviousExpiresUtc: _utcNow() + graceWindow);
        }
    }

    /// <summary>清除已过期的 previous（收敛回单锚——可选主动清理，不调也惰性拒）。</summary>
    /// <returns>清除的过期 previous 条数。</returns>
    public int PruneExpired()
    {
        var now = _utcNow();
        lock (_lock)
        {
            var removed = 0;
            foreach (var key in _anchors.Keys)
            {
                var entry = _anchors[key];
                if (entry.Previous is not null && entry.PreviousExpiresUtc is { } expiry && now >= expiry)
                {
                    _anchors[key] = entry with { Previous = null, PreviousExpiresUtc = null };
                    removed++;
                }
            }
            return removed;
        }
    }

    private static void ValidateKey(ReadOnlyMemory<byte> key)
    {
        if (key.Length != NodeKeyPair.PublicKeySize)
            throw new ArgumentException($"公钥须 {NodeKeyPair.PublicKeySize}B compressed，实际 {key.Length}。");
    }

    private sealed record AnchorEntry(byte[] Current, byte[]? Previous, DateTime? PreviousExpiresUtc);
}

/// <summary>
/// 多锚并存判定端口（二期-H1——比对方探测实现即可用并存语义，否则回退单钥比对）。
/// </summary>
public interface ISupportsKeyRotation
{
    /// <summary>呈现公钥是否受信（current 或过渡窗内 previous）。</summary>
    bool IsTrusted(NodeId node, ReadOnlyMemory<byte> presentedKey);

    /// <summary>登记轮换：新钥立即生效、旧钥保留 <paramref name="graceWindow"/> 过渡窗。</summary>
    void BeginRotation(NodeId node, ReadOnlyMemory<byte> newKey, TimeSpan graceWindow);
}
