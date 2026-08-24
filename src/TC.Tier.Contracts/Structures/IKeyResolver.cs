namespace TC.Tier.Contracts.Structures;

/// <summary>
/// IKeyResolver——按地址解析 key + 按范围扫 key 流的最小读契约（Ring 泛型改版设计稿 §3.4 三方法定稿）。
/// <para>★ 接口形状由消费者决定：判等（ProbingIndex tag 命中后回读真 key，bool 显式成败）+
///   扫描（两族恢复自建，异步迭代器流式，冷区真异步 IO 不阻塞）。</para>
/// <para>★ 无 value（索引不消费 value——交付是组合层点查最后一跳，直接 Ring 公开面 GetValue）；
///   无 header（有效性/半写过滤内化在 ScanAsync 实现里，消费者只见有效条目）；
///   无写入（写编排归组合层：先 ring.Write 得地址、再 index.Insert）；
///   无批量随机读（语义缺陷：平行数组+count 丢失成败信息；且无真实消费者——判等形态是单条）。</para>
/// <para>★ RingBase&lt;TKey&gt; 直接实现（无适配层）；任何实现者（测试 Mock 等）皆可喂索引。</para>
/// </summary>
public interface IKeyResolver<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>按地址读单条 key。false = 读不到（无效地址/record 无效）。</summary>
    bool TryGetKey(LogicalAddress addr, out TKey key);

    /// <summary>
    /// 查询真相源已落盘水位（组合层契约：Insert 先于落盘、失败回滚——已落盘记录必已入索引）。
    /// <para>★ 派生结构后台持久化（如 HashIndex 主存储 dump）以此为 footer 水位锚点 W：
    ///   表内容 = 流 [?, W] 的完整折叠，恢复重放 (W, End] 收敛。</para>
    /// </summary>
    LogicalAddress GetFlushedWatermark();

    /// <summary>范围扫描——吐 (Key, Address) 对（光有 key 建不了条目）；异步迭代器流式回源。</summary>
    IAsyncEnumerable<(TKey Key, LogicalAddress Address)> ScanAsync(
        LogicalAddress begin, LogicalAddress end, CancellationToken ct = default);

    /// <summary>全量扫描（重载）——从 BeginAddress 扫到当前尾。</summary>
    IAsyncEnumerable<(TKey Key, LogicalAddress Address)> ScanAsync(CancellationToken ct = default);
}
