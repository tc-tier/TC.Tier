using System.Threading.Channels;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Products.Kv;

/// <summary>Watch 事件种类（F5——etcd Watch 对齐：写=Put，墓碑=Delete）。</summary>
public enum KvWatchEventKind : byte
{
    /// <summary>写入/覆写（含 RMW 产物）。</summary>
    Put = 0,

    /// <summary>删除（墓碑提交）。</summary>
    Delete = 1,
}

/// <summary>Watch 变更事件（F5）：key + 种类 + 提交地址（<see cref="Address"/> 即续传游标——
/// 全局单调追加序，跨恢复稳定；续传语义 = <c>(from, ...]</c> 开区间）。</summary>
/// <param name="Key">变更涉及的键。</param>
/// <param name="Kind">事件种类（Put/Delete）。</param>
/// <param name="Address">提交地址（续传游标——全局单调追加序）。</param>
public readonly record struct KvWatchEvent<TKey>(TKey Key, KvWatchEventKind Kind, LogicalAddress Address);

/// <summary>
/// Watch 变更流中枢（F5——apply/换绑点回调注册 + 订阅面）。
/// <para>★ 发布协议：全部可见性点经 <see cref="Publish"/> 发布（锁内串行）——锁内水位去重
/// （地址 ≤ 已发布水位 = 已被某订阅者的历史补扫覆盖，跳过）+ 锁内分发（订阅者少且 TryWrite
/// 纯内存，临界区微秒级）。发布点契约 = 可见性动作完成之后（批路径在 ConfirmCommitted 后），
/// 保证「事件 = 已提交事实」。</para>
/// <para>★ 断连语义（etcd 对齐）：订阅者通道有界，慢订阅者写满即断连（通道完结 + 注销）——
/// 写路径永不被慢订阅者反压；枚举流干净结束 = 断连信号，订阅者须凭已见最大地址重订阅续传。</para>
/// <para>★ 无界通道禁用：丢弃事件违反不丢断言，反压等待违反写路径自治——断连是唯一出路。</para>
/// </summary>
internal sealed class KvWatchHub<TKey> where TKey : unmanaged
{
    /// <summary>单订阅：前缀匹配器 + 有界事件通道。</summary>
    private sealed class Subscription
    {
        public required TKey Prefix;
        public required int PrefixByteLength;   // 0 = 全 key（无过滤）
        public required Channel<KvWatchEvent<TKey>> Events;
    }

    private readonly object _lock = new();
    private readonly List<Subscription> _subs = [];
    private readonly int _capacity;

    /// <summary>已发布事件最大地址（单调；锁内推进）。续传历史补扫的上界锚——
    /// 恢复后初始化为 Ring 提交尾（历史全量在 Ring，扫描可及）。</summary>
    private LogicalAddress _published;

    /// <summary>构造（容量 + 发布水位起点）。</summary>
    /// <param name="capacity">单订阅事件通道容量（&lt;=0 抛 ArgumentOutOfRangeException）。</param>
    /// <param name="publishedStart">发布水位起点（恢复期 = Ring 提交尾）。</param>
    public KvWatchHub(int capacity, LogicalAddress publishedStart)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _published = publishedStart;
    }

    /// <summary>
    /// 注册订阅（锁内原子捕获发布水位——历史补扫 [.., watermark) 与通道事件 (watermark, ..]
    /// 按地址严格不相交，无重无漏）。
    /// </summary>
    /// <returns>订阅句柄（通道读端 + 前缀；枚举终结时须 <see cref="Unsubscribe"/>）。</returns>
    /// <param name="prefix">订阅前缀（键字节前缀匹配）。</param>
    /// <param name="prefixByteLength">前缀字节数（0 = 全 key 无过滤）。</param>
    public (ChannelReader<KvWatchEvent<TKey>> Reader, LogicalAddress Watermark) Subscribe(
        TKey prefix, int prefixByteLength)
    {
        var sub = new Subscription
        {
            Prefix = prefix,
            PrefixByteLength = prefixByteLength,
            Events = Channel.CreateBounded<KvWatchEvent<TKey>>(new BoundedChannelOptions(_capacity)
            {
                SingleReader = true,
                SingleWriter = false,
            }),
        };
        lock (_lock)
        {
            _subs.Add(sub);
            return (sub.Events.Reader, _published);
        }
    }

    /// <summary>注销订阅（枚举终结——取消/完成/断连一律走此收口；仅摘除，通道完结由断连侧或读端终结自理）。</summary>
    /// <param name="reader">订阅时返回的通道读端（匹配摘除）。</param>
    public void Unsubscribe(ChannelReader<KvWatchEvent<TKey>> reader)
    {
        lock (_lock)
        {
            for (var i = _subs.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_subs[i].Events.Reader, reader))
                    _subs.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// 发布事件（可见性点回调——锁内水位去重 + 分发）。
    /// 地址 &lt; 已发布水位 = 已被订阅者历史补扫覆盖 → 跳过（续传无重）。
    /// <para>★ 严格小于：水位可能是「下一写入位」（恢复重置 = Ring 提交尾）——恢复后首条记录地址
    /// 恰等于水位，它未被补扫覆盖（补扫 [.., watermark) 排他），必须进通道。</para>
    /// </summary>
    /// <param name="key">变更涉及的键。</param>
    /// <param name="kind">事件种类（Put/Delete）。</param>
    /// <param name="addr">提交地址（&lt; 已发布水位则跳过去重）。</param>
    public void Publish(TKey key, KvWatchEventKind kind, LogicalAddress addr)
    {
        if (!addr.IsValid) return;   // 防御：Invalid 非事实地址
        lock (_lock)
        {
            if (addr < _published) return;
            _published = addr;

            for (var i = _subs.Count - 1; i >= 0; i--)
            {
                var sub = _subs[i];
                if (!Matches(sub, key)) continue;
                if (sub.Events.Writer.TryWrite(new KvWatchEvent<TKey>(key, kind, addr))) continue;

                // 慢订阅者：断连（通道完结 + 注销）——写路径不被反压，订阅者凭游标重订阅续传
                sub.Events.Writer.TryComplete();
                _subs.RemoveAt(i);
            }
        }
    }

    /// <summary>当前发布水位（IVT 观测面）。</summary>
    internal LogicalAddress PublishedWatermark { get { lock (_lock) return _published; } }

    /// <summary>恢复期水位重置（TierKv 恢复核心调用——Ring Ready 后、任何订阅/发布前；
    /// 上界锚 = 悬干裁决后的干净提交尾，历史续传扫描 [.., 尾) 可及）。</summary>
    /// <param name="watermark">恢复后的发布水位起点（Ring 提交尾）。</param>
    internal void ResetPublished(LogicalAddress watermark)
    {
        lock (_lock) _published = watermark;
    }

    /// <summary>断连全部订阅（KV 释放收口——枚举端干净收束，不再等待）。</summary>
    internal void CompleteAll()
    {
        lock (_lock)
        {
            foreach (var sub in _subs)
                sub.Events.Writer.TryComplete();
            _subs.Clear();
        }
    }

    private static bool Matches(Subscription sub, TKey key)
        => sub.PrefixByteLength == 0
            || KeyByteOrderComparer<TKey>.IsBytePrefix(key, sub.Prefix, sub.PrefixByteLength);
}
