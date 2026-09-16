using System.Collections.Concurrent;

namespace TC.Tier.Core.Net.Transport.Tcp;

/// <summary>
/// 故障注入实现（spec-12 §9.2——TCP 介质上与 InProcess 等价语义：
/// 延迟/丢包/乱序 = 发送路径拦截（协议域数据通道面，握手/管理帧不注入）；
/// 分区 = 断开既有链路 + 阻断拨号；愈合 = Reset）。
/// </summary>
public sealed partial class ClusterTransport
{
    private readonly FaultsImpl _faultsImpl;

    /// <summary>注入器实现（同 InProcess 形态——节点对定向字典 + Random 判定）。</summary>
    internal sealed class FaultsImpl(ClusterTransport owner) : ITransportFaultInjector
    {
        private readonly ConcurrentDictionary<(NodeId From, NodeId To), TimeSpan> _latency = new();
        private readonly ConcurrentDictionary<(NodeId From, NodeId To), double> _dropRate = new();
        private readonly ConcurrentDictionary<(NodeId From, NodeId To), byte> _reorder = new();
        private readonly ConcurrentDictionary<(NodeId From, NodeId To), byte> _partitioned = new();

        /// <summary>对向是否分区（拨号阻断判定）。</summary>
        /// <param name="self">本端节点。</param>
        /// <param name="peer">对端节点。</param>
        /// <returns>true = 双向任一方向已标记分区（拨号将被阻断）；false = 未分区。</returns>
        public bool IsPartitioned(NodeId self, NodeId peer)
            => _partitioned.ContainsKey((self, peer)) || _partitioned.ContainsKey((peer, self));

        /// <summary>
        /// 发送路径注入（数据通道面）：分区/丢包 = false（静默丢弃）；延迟/乱序 = 阻塞式时延（串行链路语义）。
        /// </summary>
        /// <param name="from">发送方节点。</param>
        /// <param name="to">接收方节点。</param>
        /// <param name="ct">取消令牌（延迟等待可取消）。</param>
        /// <returns>true = 放行投递（延迟/乱序已在路径上注入）；false = 被注入丢弃（分区/丢包——调用方静默丢弃）。</returns>
        public async Task<bool> ApplyDeliveryDelay(NodeId from, NodeId to, CancellationToken ct)
        {
            if (_partitioned.ContainsKey((from, to))) return false;
            if (_dropRate.TryGetValue((from, to), out var rate) && rate > 0 && Random.Shared.NextDouble() < rate) return false;
            if (_latency.TryGetValue((from, to), out var latency) && latency > TimeSpan.Zero)
                await Task.Delay(latency, ct).ConfigureAwait(false);
            if (_reorder.ContainsKey((from, to)))
            {
                var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 21));   // 0–20ms——对齐 InProcess 形态
                await Task.Delay(jitter, ct).ConfigureAwait(false);
            }
            return true;
        }

        /// <inheritdoc/>
        /// <param name="a">端点 A（方向 a→b 生效）。</param>
        /// <param name="b">端点 B。</param>
        /// <param name="latency">单向延迟（null/非正 = 清除该定向延迟）。</param>
        public void SetLatency(NodeId a, NodeId b, TimeSpan? latency)
        {
            var key = (a, b);
            if (latency is null || latency.Value <= TimeSpan.Zero) _latency.TryRemove(key, out _);
            else _latency[key] = latency.Value;
        }

        /// <inheritdoc/>
        /// <param name="groupA">分区组 A。</param>
        /// <param name="groupB">分区组 B（与组 A 之间双向断并断开既有链路）。</param>
        public void Partition(IEnumerable<NodeId> groupA, IEnumerable<NodeId> groupB)
        {
            ArgumentNullException.ThrowIfNull(groupA);
            ArgumentNullException.ThrowIfNull(groupB);
            var listA = groupA.ToArray();
            var listB = groupB.ToArray();
            foreach (var a in listA)
                foreach (var b in listB)
                {
                    _partitioned[(a, b)] = 0;   // 双向断
                    _partitioned[(b, a)] = 0;
                }

            // 既有链路立即断开（PeerGone 事件链 + 拨号阻断由 IsPartitioned 承担）
            foreach (var link in owner._links.Values)
            {
                if (listA.Contains(link.Remote) || listB.Contains(link.Remote))
                    link.Close();
            }
        }

        /// <inheritdoc/>
        /// <param name="from">发送方节点。</param>
        /// <param name="to">接收方节点。</param>
        /// <param name="rate">丢包率（0..1；0 = 清除该定向丢包）。</param>
        public void Drop(NodeId from, NodeId to, double rate)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(rate);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(rate, 1.0);
            var key = (from, to);
            if (rate <= 0) _dropRate.TryRemove(key, out _);
            else _dropRate[key] = rate;
        }

        /// <inheritdoc/>
        /// <param name="from">发送方节点。</param>
        /// <param name="to">接收方节点。</param>
        /// <param name="enable">true = 启用乱序注入（0–20ms 随机抖动）；false = 清除。</param>
        public void Reorder(NodeId from, NodeId to, bool enable)
        {
            var key = (from, to);
            if (!enable) _reorder.TryRemove(key, out _);
            else _reorder[key] = 0;
        }

        /// <inheritdoc/>
        public void Reset()
        {
            _latency.Clear();
            _dropRate.Clear();
            _reorder.Clear();
            _partitioned.Clear();
        }
    }
}
