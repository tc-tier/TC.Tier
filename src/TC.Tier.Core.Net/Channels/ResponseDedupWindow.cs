namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 应答端 CorrId 去重窗口（spec-12 §5.2 at-least-once——识别已服务请求 → 缓存响应重放，
/// 不重复执行）。键 =（来源节点, CorrId）——CorrId 唯一域在发起端。
/// <para>★ 双重有界：<b>条目数</b>（<see cref="_capacity"/>——重发窗口的机器纪律）与
/// <b>字节软预算</b>（<see cref="_maxBytes"/>——缓存应答的字节总量驱逐目标）。窗口按条目
/// 有界而单条应答大小无界（帧上限 16MB），大响应（如 SwarmSync MiB 级块）会让条目数上界
/// 失去内存意义——字节预算把驻留压回 <c>预算 + 最大单条</c>。</para>
/// <para>★ 软预算 = <b>驱逐目标非准入门槛</b>：应答一律缓存（哪怕单条超预算——独占驻留至
/// 淘汰）；超预算后按 FIFO 驱逐最老直至回落。"超预算不缓存"是错误语义——占位保留
/// "已见未回"会让对端重发永不获回复（破坏 at-least-once 的确认契约）。</para>
/// <para>★ 线性低频路径（每次请求服务一次/应答一次）——简单锁足够，无需无锁结构。</para>
/// </summary>
internal sealed class ResponseDedupWindow
{
    /// <summary>字节软预算缺省（8 MiB——窗口驻留 ≈ 预算 + 最大单条应答）。</summary>
    public const long DefaultMaxBytes = AlignmentConst.Alignment4M * 2;

    private readonly object _lock = new();
    private readonly Dictionary<(NodeId From, ulong CorrId), byte[]?> _served = new();
    private readonly Queue<(NodeId From, ulong CorrId)> _order = new();
    private readonly int _capacity;
    private readonly long _maxBytes;
    private long _bytes;   // 当前缓存应答字节总量（锁内维护）

    /// <summary>构造。</summary>
    /// <param name="capacity">窗口容量（已服务请求记忆上限——条目数）。</param>
    /// <param name="maxBytes">缓存应答字节软预算（驱逐目标——超预算驱逐最老；单条超预算独占驻留）。</param>
    public ResponseDedupWindow(int capacity, long maxBytes = DefaultMaxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _capacity = capacity;
        _maxBytes = maxBytes;
    }

    /// <summary>已记忆条目数（诊断/测试）。</summary>
    public int Count
    {
        get { lock (_lock) return _served.Count; }
    }

    /// <summary>缓存应答字节总量（诊断/测试——软预算核对面）。</summary>
    public long CachedBytes
    {
        get { lock (_lock) return _bytes; }
    }

    /// <summary>
    /// 开始服务判定：true = 首见（调用方执行 handler）；false = 已服务
    /// （<paramref name="cachedResponse"/> = 缓存响应——null = 已见但 handler 未回复过，
    /// 重放面不重复执行也不回复——对端继续超时重试）。
    /// </summary>
    /// <param name="from">请求来源节点（去重键一半）。</param>
    /// <param name="corrId">请求关联 ID（去重键一半——唯一域在发起端）。</param>
    /// <param name="cachedResponse">缓存响应（false 且已回复过 = 缓存字节；false 但未回复过/true = null）。</param>
    /// <returns>true = 首见（调用方执行 handler）；false = 已服务（据 <paramref name="cachedResponse"/> 决定重放或不回复）。</returns>
    public bool TryBeginServe(NodeId from, ulong corrId, out byte[]? cachedResponse)
    {
        lock (_lock)
        {
            if (_served.TryGetValue((from, corrId), out var cached))
            {
                cachedResponse = cached;
                return false;   // 已服务——重放缓存或不回
            }
            if (_served.Count >= _capacity) EvictOldest();
            _served[(from, corrId)] = null;   // 先占位（已见未回——零字节）
            _order.Enqueue((from, corrId));
            cachedResponse = null;
            return true;
        }
    }

    /// <summary>
    /// 登记响应（handler 回复时——重发到达即重放此响应）。字节软预算在登记点收口：
    /// 超预算按 FIFO 驱逐最老（含当前条目在内的独占形态保留——软预算语义）。
    /// </summary>
    /// <param name="from">请求来源节点（去重键一半）。</param>
    /// <param name="corrId">请求关联 ID（去重键一半）。</param>
    /// <param name="payload">应答载荷（字节；驻留窗口供重发重放）。</param>
    public void RecordResponse(NodeId from, ulong corrId, byte[] payload)
    {
        lock (_lock)
        {
            if (!_served.TryGetValue((from, corrId), out var slot)) return;   // 未在窗口（已淘汰）——不缓存
            if (slot is not null) _bytes -= slot.Length;                      // 重复应答（罕见）——旧缓存先减
            _served[(from, corrId)] = payload;
            _bytes += payload.Length;
            while (_bytes > _maxBytes && _served.Count > 1)
                EvictOne();   // 字节驱逐与条目容量无关——窗口未满也收口（软预算=驱逐目标）
        }
    }

    /// <summary>驱逐至条目容量内（TryBeginServe 占位路径）。</summary>
    private void EvictOldest()
    {
        while (_order.Count > 0 && _served.Count >= _capacity)
            EvictOne();
    }

    /// <summary>单步驱逐 FIFO 最老（字节核算一体）。</summary>
    private void EvictOne()
    {
        if (_order.Count == 0) return;
        var oldest = _order.Dequeue();
        if (_served.Remove(oldest, out var removed) && removed is not null)
            _bytes -= removed.Length;   // 已淘汰条目重发到达 = 视为首见重新执行（窗口有界语义）
    }
}
