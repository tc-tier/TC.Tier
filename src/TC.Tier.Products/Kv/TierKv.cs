using System.Buffers;
using System.Runtime.InteropServices;
using TC.Tier.Core.Epochs;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Lifecycle;
using TC.Tier.Core.Resources;
using TC.Tier.Runtime.Structures.ProbingIndex;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.SortedIndex;

namespace TC.Tier.Products.Kv;

/// <summary>
/// TierKv&lt;TKey,TValue&gt;——本地持久 KV 存储（tierkv-design.md：单一泛型产品类，TierWal 同构）。
/// <para>★ 组合配方：Ring（数据唯一真源——append-only 结构化记录+变长本体+自动溢出）+
///   主索引（缺省 HashIndex 点查 O(1)；BTree/SkipList 经 <see cref="KvIndexKind"/> 装配）。
///   索引是可重建派生结构（派生重放教义）——任何索引切换/重建都不丢数据。</para>
/// <para>★ 写语义：append-only 换绑——Put 恒为「Ring 追加新记录 → 索引 CAS 换绑地址」，
///   旧值留 Ring 由回收（W6）治理；Delete = 墓碑写入 + 索引清槽（恢复重放跳过已删 key）。</para>
/// <para>★ 装配闸门：开放泛型不落消费面（同 BlittableRing 律）——ctor 收 <c>protected internal</c>、
///   类不封闭，供 <c>[KvStore]</c> 生成的封闭形态派生（formatter 零代码）；显式路径 =
///   <see cref="TierKvBuilder{TKey,TValue}"/>（工厂注入显式装配）；测试经 IVT 直构。</para>
/// </summary>
/// <typeparam name="TKey">键类型（unmanaged + IEquatable——判等经注入的 IKeyComparer/IKeyResolver 闭环）。</typeparam>
/// <typeparam name="TValue">值类型（无约束——经 <see cref="IValueFormatter{TValue}"/> 纯转 byte 存储）。</typeparam>
public class TierKv<TKey, TValue> : LifecycleBase<KvRecoveryHints>, ITierKv<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    private readonly RingBase<TKey> _ring;
    private readonly TierKvOptions _options;
    private readonly TimeProvider _clock;   // 时钟供给源（时钟缝 件一——TTL 墙钟/清扫周期统一；缺省 System）
    private readonly IValueFormatter<TValue> _formatter;

    // ═══ 主索引（W-Hot 热切换——单引用发布；访问一律 Volatile.Read 局部化）═══
    private IIndex<TKey> _index;
    /// <summary>切换进行中门闩（并发切换拒绝）。</summary>
    private int _switching;
    /// <summary>范围扫描预扫批粒度（P1 扫描模式选择器）。</summary>
    private readonly int _scanBatchSize;
    /// <summary>顺序模式地址跨度阈值（Ring 页数）。</summary>
    private readonly int _scanSpanPages;

    // ═══ Session 域（W2——EPVS 版本保护区 + 原子批提交编排）═══
    /// <summary>会话域 epoch（独立于 Ring 内部 epoch——会话保护区管 TierKv 层相过渡，如 W-Hot 索引热切换）。</summary>
    private readonly LightEpoch _sessionEpoch = new();
    private readonly EpochProtectedVersionScheme _sessionEpvs;
    /// <summary>原子批提交门（per-Kv 串行提交——Ring 2PC seq 单槽，并发提交轮会碰撞）。</summary>
    private readonly SemaphoreSlim _commitGate = new(1, 1);

    // ═══ 组提交（W6 尾巴——Committed 档接管：并发写共享一轮 flush，fsync 摊薄）═══
    private sealed class FlushRound
    {
        public LogicalAddress Target;   // 可被并发写提升（本轮覆盖到提升值）
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _flushLock = new();
    private FlushRound? _flushRound;   // 进行中轮（null=空闲）
    private LogicalAddress _flushedFloor;   // 已确定被刷覆盖的最大目标（单调）
    private int _batchSeq;   // 原子批 2PC 序号分配（协调者域）
    private int _activeSessions;   // 存活会话数（Dispose 契约观测——Debug 断言）
    /// <summary>版本分配器（D1 session-version——高水位随 2PC Prepare 入 Ring opaque 持久化）。</summary>
    private readonly KvVersionAllocator _versions = new();
    /// <summary>范围索引（W7 F1——key 字节序 BTree 辅助索引；EnableRangeIndex=false 恒 null）。</summary>
    private readonly SortedIndexBase<TKey>? _rangeIndex;
    /// <summary>Watch 变更流中枢（W9 F5——可见性点发布 + 订阅面；水位恢复期重置为 Ring 提交尾）。</summary>
    private readonly KvWatchHub<TKey> _watchHub;

    // ═══ checkpoint 周期泵（FASTER 形态编排面——持久化=checkpoint，RPO ≈ CheckpointInterval）═══
    private CancellationTokenSource? _checkpointPumpCts;
    private Task? _checkpointPumpTask;

    // ═══ CAS 串行门（A.2b——compare→append→换绑窗口串行化：CAS×CAS 恰一成功；
    //     CAS×Put 不受此门约束——last-writer-wins 换绑，CAS 非全局写锁）═══
    private readonly SemaphoreSlim _casGate = new(1, 1);

    // ═══ 过期回收扫描（A.2a——水位推进式）：水位在提交门内读写（与 sweep/批/检查点互斥），
    //     恢复期自 opaque 续接；持久化随 2PC Prepare 原子落盘 ═══
    /// <summary>累计过期强删计数（sweep 驱动的回收结果聚合——Interlocked 读）。</summary>
    private long _expiredSupersededCount;
    /// <summary>过期扫描水位（增量游标——其下记录必为过期/墓碑/已删；提交门内读写）。</summary>
    private LogicalAddress _sweepWatermark;

    // ═══ TTL 时钟缓存（ms 粒度——单调戳变化才查墙钟，点查过期判定的时间查询 ~25ns→~5ns；
    //     TTL 语义秒/分级，精度无损）——双钟均经时钟供给源 _clock（时钟缝 件一）═══
    private long _nowUtcTicks;
    private long _nowStamp = -1;   // 缓存戳哨兵（-1 永不由单调戳产生——假钟 0 起点下首读必须刷新）
    private long _nowFloorTicks;   // 墙钟单调水位（时钟缝 §1.3——倒流/后跳不回退已判过期项）

    /// <summary>缓存的 UTC ticks（ms 粒度刷新；多线程并发刷幂等）。
    /// <para>★ 倒流守卫：观测墙钟经单调水位钳制（读未回退）——墙钟后跳/倒流时已判过期项不复活、
    /// 倒流期新写项的过期点不早于水位（NTP 校时跳变下的 TTL 语义防线）。</para></summary>
    private long NowUtcTicks
    {        get
        {
            var stamp = _clock.GetMsTimestamp();
            var ticks = Volatile.Read(ref _nowUtcTicks);
            if (Volatile.Read(ref _nowStamp) != stamp)
            {
                ticks = _clock.GetUtcNow().Ticks;
                var floor = Volatile.Read(ref _nowFloorTicks);
                if (ticks < floor)
                {
                    ticks = floor;                          // 倒流——钳到水位（观测不回退）
                }
                else
                {
                    Volatile.Write(ref _nowFloorTicks, ticks);   // 正向——抬水位
                }
                Volatile.Write(ref _nowUtcTicks, ticks);
                Volatile.Write(ref _nowStamp, stamp);
            }
            return ticks;
        }
    }

    /// <summary>值格式化器（类型化值面直取——[KvStore] 封闭形态的承载点）。</summary>
    public IValueFormatter<TValue> Formatter => _formatter;

    /// <summary>
    /// ctor（protected internal——开放泛型不落消费面闸门，同 BlittableRing：<see cref="HashIndex{TKey}"/>
    /// ctor 同款注释）。索引收 <see cref="IIndex{TKey}"/>（两族公共协议），构造期闸门限两族基类实例
    /// （生命周期编排按族分发——恢复 hints 族各自独立，无公共基类是设计语义：选族即选消费形态）。
    /// <para>★ 消费面两条路：① <see cref="TierKvBuilder{TKey,TValue}"/> 工厂注入（显式层——
    /// Settings 构建经 <see cref="TierKvAssembly"/> 公共翻译面）；② <c>[KvStore]</c> 生成的封闭形态
    /// （派生本类 + 内嵌派生 Ring/索引实现——构造走派生肢，开放泛型零构造）。</para>
    /// </summary>
    /// <param name="ring">Ring 数据引擎（数据唯一真源）。</param>
    /// <param name="index">主索引（须为 ProbingIndexBase/SortedIndexBase 两族实例）。</param>
    /// <param name="options">TierKv 选项。</param>
    /// <param name="formatter">值格式化器。</param>
    /// <param name="logger">日志（缺省 null）。</param>
    /// <param name="rangeIndex">范围索引（W7 F1，缺省 null）。</param>
    /// <exception cref="ArgumentException">index 非两族基类实例。</exception>
    protected internal TierKv(RingBase<TKey> ring, IIndex<TKey> index,
        TierKvOptions options, IValueFormatter<TValue> formatter, ILogger? logger,
        SortedIndexBase<TKey>? rangeIndex = null)
        : base(recovery: null, logger)
    {
        _index = index is ProbingIndexBase<TKey> or SortedIndexBase<TKey>
            ? index
            : throw new ArgumentException(
                "索引须为 ProbingIndexBase<TKey>/SortedIndexBase<TKey> 两族实例（生命周期编排按族分发）", nameof(index));
        _ring = ring;
        _options = options;
        _clock = options.Clock;
        _formatter = formatter;
        _sessionEpvs = new EpochProtectedVersionScheme(_sessionEpoch);
        _watchHub = new KvWatchHub<TKey>(options.WatchChannelCapacity, LogicalAddress.Empty);
        _scanBatchSize = options.ScanBatchSize > 0 ? options.ScanBatchSize : 512;
        _scanSpanPages = options.ScanSpanPageThreshold > 0 ? options.ScanSpanPageThreshold : 8;
        if (rangeIndex is not null)
        {
            _rangeIndex = rangeIndex;   // 范围索引（W7 F1——key 字节序 BTree，装配方经 TierKvAssembly.CreateRangeIndex）
            Resources.Add(_rangeIndex, ownership: ResourceOwnership.Owned);
        }
        Resources.Add(ring, ownership: ResourceOwnership.Owned);
        Resources.Add(index, ownership: ResourceOwnership.Owned);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 生命周期（TierWal 同构：Ring 先行，索引在恢复核心 Ring Ready 后启动——
    // 索引重放窗口依赖 Ring 的 BeginAddress/TailAddress；hints 按族分发）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Initialize 第一阶段：仅启动 Ring（索引重放窗口此时未就绪——同 TierQueue 幂等索引模式）。</summary>
    protected override void OnInitializeBegin() => _ring.Initialize();

    /// <summary>启动完成钩子——checkpoint 周期泵 + 过期回收扫描循环装配（宿主调度：
    /// LifecycleBase 内建 worker——恢复完成后启动，Dispose 统一收口）。</summary>
    protected override void OnInitializeComplete()
    {
        base.OnInitializeComplete();
        if (_options.CheckpointInterval != Timeout.InfiniteTimeSpan)
            StartCheckpointPump();
        if (_options.ExpiryScanInterval > TimeSpan.Zero)
            ConfigureBackgroundWorker(new ExpirySweepLoop(this));
    }

    /// <summary>
    /// 过期回收扫描循环（A.2a——时间驱动 worker，EntryLog/Compactor 同族形态）：按
    /// <see cref="TierKvOptions.ExpiryScanInterval"/> 周期触发一轮水位推进 sweep。首扫延后一个周期
    /// （启动零突发）；单轮失败经 OnCycleError 记录下轮重试（sweep 幂等——水位/回收线单调）。
    /// </summary>
    private sealed class ExpirySweepLoop(TierKv<TKey, TValue> owner)
        : BackgroundWorkerLoop(name: "kv-expiry-sweep-" + owner._options.KvName, logger: owner.Logger)
    {
        /// <summary>一个周期 = 周期等待 + 一轮 sweep（等待在前——启动后首个周期点才扫）。</summary>
        /// <param name="ct">循环取消令牌（Stop/Dispose 触发——等待与 sweep 全程响应）。</param>
        /// <returns>true = 继续循环；取消时 false 退出。</returns>
        protected override async ValueTask<bool> RunOneCycleAsync(CancellationToken ct)
        {
            try
            {
                await owner._clock.Delay(owner._options.ExpiryScanInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;   // Dispose 收口——正常退出
            }
            await owner.SweepExpiredCoreAsync(ct).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>
    /// checkpoint 周期泵（PeriodicTimer 循环——引擎 meta 回扫泵同构）：按 CheckpointInterval 周期
    /// 自动 checkpoint（提交门内串行——与原子批/检查点/热切换互斥）。单轮失败 best-effort 记警告
    /// 下轮重试（checkpoint 幂等——失败窗口内 RPO 变大，不影响正确性：恢复正确性走派生重放）。
    /// </summary>
    private void StartCheckpointPump()
    {
        var cts = new CancellationTokenSource();
        _checkpointPumpCts = cts;
        var ct = cts.Token;
        _checkpointPumpTask = Task.Run(async () =>
        {
            // ★ 时钟缝 件一：周期等待经时钟供给源（假钟下由快进确定性触发；System 下行为不变）
            try
            {
                while (true)
                {
                    await _clock.Delay(_options.CheckpointInterval, ct).ConfigureAwait(false);
                    try
                    {
                        await CheckpointAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning(ex, "checkpoint 泵单轮失败（best-effort，下轮重试）name={Name}", _options.KvName);
                    }
                }
            }
            catch (OperationCanceledException) { /* Dispose 取消——正常退出 */ }
        });
    }

    /// <summary>恢复核心：Ring Ready 后启动索引（重放窗口由 Ring 地址注入）。</summary>
    /// <returns>恢复算法实例（由基类持有并在 Initialize 中执行）。</returns>
    protected override IRecovery<KvRecoveryHints> CreateRecovery() => new TierKvRecovery(this);

    private sealed class TierKvRecovery(TierKv<TKey, TValue> owner) : RecoveryBase<KvRecoveryHints>
    {
        /// <summary>层间 join——等 Ring Ready。</summary>
        /// <param name="ct">取消令牌（等待期间取消则抛 OperationCanceledException，并入 Failed 语义）。</param>
        /// <returns>任务在 Ring Ready 后完成。</returns>
        protected override async ValueTask WaitForDependenciesAsync(CancellationToken ct)
            => await owner._ring.WaitForReadyAsync(ct).ConfigureAwait(false);

        /// <summary>Ring Ready 后启动索引——重放窗口由 Ring 地址注入；版本高水位自 opaque 恢复
        /// （先于 Ready——CreateSession 取版本必须晚于恢复，保证分配版本恒大于历史）。</summary>
        /// <param name="hints">恢复 hints（本实现未消费——重放窗口直接取自 Ring Begin/Tail 地址；参数保留扩展位）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>恢复完成（异常时抛——RecoveryBase 转为 Failed）。</returns>
        protected override async ValueTask OnRecoveryCoreAsync(KvRecoveryHints hints, CancellationToken ct)
        {
            var begin = owner._ring.BeginAddress;
            var end = owner._ring.TailAddress;

            // 版本高水位恢复（W3——分配器续接历史，版本单调不回退；无有效 opaque=未持久化态，从 0 续）。
            // 过期扫描水位同块恢复（A.2a——续扫不重复全扫；未持久化/旧 12B 块 → Invalid，首轮自 Begin 全扫）。
            // async 方法禁 ref-struct 局部——堆快照（判例：ReadOpaqueMeta 返回 ReadOnlySpan）
            var opaque = owner._ring.ReadOpaqueMeta().ToArray();
            var restored = KvVersionAllocator.ReadOpaqueHighWater(opaque);
            if (restored is { } highWater)
                owner._versions.Restore(highWater);
            owner._sweepWatermark = KvVersionAllocator.ReadOpaqueSweepWatermark(opaque) ?? LogicalAddress.Invalid;

            switch (owner._index)
            {
                case ProbingIndexBase<TKey> probing:
                    probing.Initialize(new ProbingIndexRecoveryHints(begin, end));
                    await probing.WaitForReadyAsync(ct).ConfigureAwait(false);
                    break;
                case SortedIndexBase<TKey> sorted:
                    sorted.Initialize(new SortedIndexRecoveryHints(begin, end));
                    await sorted.WaitForReadyAsync(ct).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException(
                        "索引须为 ProbingIndexBase<TKey>/SortedIndexBase<TKey> 两族实例（ctor 已闸——防御性兜底）");
            }

            // 范围索引二级启动（W7 F1——重放窗口同主索引；Delete-on-Tombstone 重建已删态）
            if (owner._rangeIndex is { } range)
            {
                range.Initialize(new SortedIndexRecoveryHints(begin, end));
                await range.WaitForReadyAsync(ct).ConfigureAwait(false);
            }

            // Watch 水位重置（W9 F5）：恢复后 Ring 尾 = 悬干裁决后的干净提交边界（Prepare 未 Confirm
            // 记录已被 Abort 截断）——历史续传扫描以此为上界，发布水位自此单调推进。
            owner._watchHub.ResetPublished(owner._ring.TailAddress);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Session 域（W2——会话全集：EPVS 保护区 + 原子批 2PC 编排 + pending 刷盘）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 创建会话（tierkv-design.md §3.1——FASTER ClientSession 对齐；非线程安全，每会话一实例）。
    /// <para>★ 档位缺省 ReadMyWrites（D3）；Serializable 会话存活期持有 EPVS 版本保护区
    /// （须创建线程 Dispose——epoch Leave 线程亲和）。</para>
    /// <para>★ 释放契约：会话须先于 KV 释放（Dispose/DisposeAsync Debug 断言零存活会话）。</para>
    /// </summary>
    /// <param name="conditions">会话一致性档位（缺省 ReadMyWrites）。</param>
    /// <returns>新建会话实例（单线程对象——每会话一实例）。</returns>
    public KvSession<TKey, TValue> CreateSession(KvSessionConditions conditions = KvSessionConditions.ReadMyWrites)
    {
        EnsureReady();
        Interlocked.Increment(ref _activeSessions);
        try
        {
            return new KvSession<TKey, TValue>(this, conditions);
        }
        catch
        {
            Interlocked.Decrement(ref _activeSessions);
            throw;
        }
    }

    /// <summary>存活会话数（Dispose 契约观测面——应为 0）。</summary>
    public int ActiveSessionCount => Volatile.Read(ref _activeSessions);

    /// <summary>会话域 EPVS（KvSession 构造用——版本保护区）。</summary>
    internal EpochProtectedVersionScheme SessionEpvs => _sessionEpvs;

    /// <summary>版本分配器（会话版本域——IVT 测试观测面）。</summary>
    internal KvVersionAllocator Versions => _versions;

    /// <summary>数据 Ring（IVT 测试观测面——FlushedUntil 水位断言；内部编排直用字段）。</summary>
    internal RingBase<TKey> Ring => _ring;

    /// <summary>主索引点查（IVT 测试观测面——版本历史断言：RMW 前后地址不同+旧地址仍可解析）。</summary>
    /// <param name="key">键。</param>
    /// <returns>主索引中该 key 当前绑定的逻辑地址。</returns>
    internal LogicalAddress FindAddress(TKey key) => Volatile.Read(ref _index).Find(key);

    /// <summary>主索引探测族视图（IVT 诊断面——桶级原始槽位转储；比较族为 null）。</summary>
    internal ProbingIndexBase<TKey>? ProbingIndex => Volatile.Read(ref _index) as ProbingIndexBase<TKey>;

    /// <summary>主索引当前实例（IVT 测试观测面——热切换发布断言：切换前后引用不同/族不同）。</summary>
    internal IIndex<TKey> IndexCurrent => Volatile.Read(ref _index);

    /// <summary>范围索引（IVT 观测面——扫描剖析）。</summary>
    internal SortedIndexBase<TKey>? RangeIndex => _rangeIndex;

    /// <summary>原子批提交门（per-Kv 串行——Ring 2PC seq 单槽约束）。</summary>
    internal SemaphoreSlim CommitGate => _commitGate;

    // ═══ 索引换绑（W-Hot——唯一写入口：EPVS 版本保护区包络）═══
    //
    // 写者必须持版本保护区（Enter/Leave）：热切换的发布相经版本推进排水——保证「已 Enter 的
    // 写者」全部完成换绑后才发布新引用；「已分配地址但未换绑」的写者由发布临界区内的增量
    // 补扫兜底（CatchUpIncremental 按 Ring 真源扫 (重放终点, 排水后尾]，与换绑写入同地址幂等）。
    // 正常路径 = 一次 Enter/Leave + 单引用读，零切换感知。

    /// <summary>主索引换绑（Put/RMW/批路径统一入口；范围索引双写不在此——它不参与热切换）。</summary>
    private void BindIndex(TKey key, LogicalAddress addr, LogicalAddress begin)
    {
        _sessionEpvs.Enter();
        try
        {
            Volatile.Read(ref _index).Insert(key, addr, begin);
        }
        finally
        {
            _sessionEpvs.Leave();
        }
    }

    /// <summary>主索引清槽（Delete 路径统一入口）。</summary>
    private void UnbindIndex(TKey key)
    {
        _sessionEpvs.Enter();
        try
        {
            Volatile.Read(ref _index).Delete(key);
        }
        finally
        {
            _sessionEpvs.Leave();
        }
    }

    /// <summary>
    /// 增量追平轮（W-Hot 步②——临界区外异步执行）：把 (from, to] 的 Ring 记录逐条换绑进新索引。
    /// 墓碑 → Delete（重放需墓碑语义，同恢复）；meta record 由 <c>RingBase&lt;TKey&gt;.ScanAsync</c>
    /// 过滤不产出。多轮循环调用直至追平（每轮终点=启动时尾，写入速率低于扫描速率时收敛）。
    /// </summary>
    private async ValueTask CatchUpRangeAsync(IIndex<TKey> fresh, LogicalAddress from, LogicalAddress to,
        CancellationToken ct)
    {
        var begin = _ring.BeginAddress;
        await foreach (var (key, addr, tombstone) in _ring.ScanAsync(from, to, ct).ConfigureAwait(false))
        {
            if (tombstone) fresh.Delete(key);
            else fresh.Insert(key, addr, begin);
        }
    }

    /// <summary>
    /// 增量尾巴（W-Hot 步③——EPVS 排水临界区内同步执行）：同步扫描游标扫 (from, to] 残余增量——
    /// 热区同步快路径、冷区同步读盘（全功能，无 sync-over-async 包装）；追平循环已把该区间收敛到
    /// 残余微量。meta record 过滤（<c>RecordKey&lt;TKey&gt;.IsMeta</c>），墓碑 → Delete。
    /// </summary>
    private void CatchUpTail(IIndex<TKey> fresh, LogicalAddress from, LogicalAddress to)
    {
        using var cursor = _ring.OpenScanCursor(from, to);
        var begin = _ring.BeginAddress;
        while (cursor.MoveNext())
        {
            var record = _ring.GetKey(cursor.CurrentAddress);
            if (record.IsMeta) continue;
            if (record.IsTombstone) fresh.Delete(record.Key);
            else fresh.Insert(record.Key, cursor.CurrentAddress, begin);
        }
    }

    /// <summary>索引 Ready 等待（两族基类分派——<see cref="IIndex{TKey}"/> 接口面不含生命周期）。</summary>
    private static Task ReadyIndexAsync(IIndex<TKey> index, CancellationToken ct) => index switch
    {
        ProbingIndexBase<TKey> probing => probing.WaitForReadyAsync(ct),
        SortedIndexBase<TKey> sorted => sorted.WaitForReadyAsync(ct),
        _ => Task.CompletedTask,
    };

    /// <summary>索引异步释放（两族为 LifecycleBase/IAsyncDisposable）。</summary>
    private static async ValueTask DisposeIndexAsync(IIndex<TKey> index)
    {
        switch (index)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    /// <summary>原子批 2PC 序号（协调者域单调分配）。</summary>
    /// <returns>新分配的 2PC 序号（单调递增）。</returns>
    internal long NextBatchSeq() => Interlocked.Increment(ref _batchSeq);

    /// <summary>刷盘至地址（pending 收口 / Committed 策略——Ring FlushedUntil 前缀刷）。</summary>
    /// <param name="until">刷盘目标地址（Ring 前缀刷至此）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>刷盘任务。</returns>
    internal ValueTask FlushAsync(LogicalAddress until, CancellationToken ct)
        => _ring.FlushUntilAsync(until, ct);

    /// <summary>同步刷盘至地址（快路径）。</summary>
    /// <param name="until">刷盘目标地址（Ring 前缀刷至此）。</param>
    internal void Flush(LogicalAddress until) => _ring.FlushUntil(until);

    /// <summary>会话释放回调（存活计数）。</summary>
    internal void OnSessionDisposed() => Interlocked.Decrement(ref _activeSessions);

    /// <summary>
    /// 原子批提交编排（全或无，KvSession 调用——per-Kv 提交门内串行）：
    /// Ring 追加全部暂存 → 2PC Prepare（整体落盘悬空）→ ConfirmCommitted（提交点）→
    /// 索引换绑（可见性）。崩溃任一窗口全或无：Prepare 前/悬空期崩溃=恢复丢弃（索引零污染——
    /// 可见性未开始）；Confirm 后崩溃=记录已提交、索引由派生重放自建（派生重放教义）。
    /// </summary>
    /// <param name="batch">暂存的批条目（key + payload/expiry；payload null=墓碑）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>提交任务（提交点后索引可见性生效）。</returns>
    internal async ValueTask CommitBatchAsync(
        IReadOnlyList<KeyValuePair<TKey, (byte[]? Payload, long Expiry)>> batch,
        CancellationToken ct)
    {
        if (batch.Count == 0) return;

        await _commitGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var seq = NextBatchSeq();

            // 1. Ring 追加（未刷——真源记录先行），捕获逐条地址（墓碑=墓碑记录地址——Watch 事件游标）
            var addresses = new LogicalAddress[batch.Count];
            for (var i = 0; i < batch.Count; i++)
            {
                var (key, value) = batch[i];
                if (value.Payload is { } bytes)
                {
                    var framed = KvValueFraming.Frame(bytes, value.Expiry);
                    addresses[i] = await _ring.WriteAsync(key, framed, ct).ConfigureAwait(false);
                }
                else
                {
                    addresses[i] = await _ring.WriteTombstoneAsync(key, ct).ConfigureAwait(false);
                }
            }

            // 2. 2PC 提交点（Prepare 落盘悬空 → Confirm 推进 LastCommittedSeq）。
            //    版本高水位搭 Ring opaque 同块原子落盘（SetOpaqueMeta 暂存 → Prepare WriteMeta 落盘）；
            //    meta Disabled 时 opaque 通道不存在——版本持久化随档位（Disabled=不持久化，重启续接 0）。
            if (_options.MetaPolicyKind != MetaPolicyKind.Disabled)
            {
                var opaque = new byte[KvVersionAllocator.OpaqueSize];
                KvVersionAllocator.WriteOpaque(opaque, _versions.HighWater, _sweepWatermark);
                _ring.SetOpaqueMeta(opaque);
            }
            await _ring.PrepareAsync(seq, ct).ConfigureAwait(false);

            // 3. 索引换绑（可见性——纯内存无失败路径；崩溃后由恢复重放自建）。
            //    批内序 = 提交序：同 key 多写按序换绑/清槽——批内最后一条胜出（串行语义）。
            //    墓碑判定按载荷空（地址恒有效——游标语义，见发布步）。
            var begin = _ring.BeginAddress;
            for (var i = 0; i < batch.Count; i++)
            {
                if (batch[i].Value.Payload is not null)
                    BindIndex(batch[i].Key, addresses[i], begin);
                else
                    UnbindIndex(batch[i].Key);
            }

            _ring.ConfirmCommitted(seq);

            // 4. Watch 发布（W9 F5——提交点后逐条发布：事件 = 已提交事实，批内序 = 发布序）。
            for (var i = 0; i < batch.Count; i++)
            {
                var (key, value) = batch[i];
                _watchHub.Publish(key, value.Payload is null ? KvWatchEventKind.Delete : KvWatchEventKind.Put,
                    addresses[i]);
            }
        }
        finally
        {
            _commitGate.Release();
        }
    }

    /// <summary>释放钩子（同步轨）：会话域契约（会话须先于 KV 释放——Debug 断言，epoch 排水依赖）
    /// + 提交门 / 会话 epoch 释放。核心清理经 <see cref="DisposeKvCore"/> 与异步轨共用。</summary>
    /// <param name="disposing">true=用户调 Dispose（执行本钩子释放）；false=终结器路径（直接返回，不释放）。</param>
    protected override void DisposeOverride(bool disposing)
    {
        if (!disposing) return;
        DisposeKvCore();
    }

    /// <summary>释放钩子（异步轨）：DisposeAsync 走本钩子——只实现同步钩子的子类在 await using
    /// 下全部清理失效（watch 订阅永不断连/闸门不释放），双轨必须对齐。</summary>
    /// <param name="disposing">true=用户经 DisposeAsync 调用。</param>
    protected override ValueTask DisposeOverrideAsync(bool disposing)
    {
        if (!disposing) return ValueTask.CompletedTask;
        DisposeKvCore();
        return ValueTask.CompletedTask;
    }

    /// <summary>KV 核心清理（同步/异步 Dispose 双轨共用——checkpoint 泵停 → watch 断连收口 → 闸门/epoch 释放）。</summary>
    private void DisposeKvCore()
    {
        System.Diagnostics.Debug.Assert(Volatile.Read(ref _activeSessions) == 0,
            $"TierKv 释放时仍有 {Volatile.Read(ref _activeSessions)} 个存活会话——会话须先于 KV 释放");
        // ★ checkpoint 泵先停（Cancel+有界等待泵退出——进行中 checkpoint 完成并释放提交门）
        //   再释放 _commitGate：gate Dispose 与持门 checkpoint 并发 = ObjectDisposedException。
        if (_checkpointPumpCts is { } pumpCts)
        {
            pumpCts.Cancel();
            _checkpointPumpTask?.Wait(TimeSpan.FromSeconds(5));
            pumpCts.Dispose();
        }
        _watchHub.CompleteAll();   // Watch 订阅断连收口（枚举端干净收束，不再等待）
        _commitGate.Dispose();
        _casGate.Dispose();
        _sessionEpoch.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Functions 完整模型（W4——RMW 三流 + Read/Upsert/Delete 钩子面）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// RMW（读-改-写——§3.3 三流统一「读折叠 → Format → 追加 → 索引 CAS 换绑」）：
    /// 未命中 → <see cref="IKvFunctions{TKey, TInput, TValue, TOutput, TContext}.InitialUpdater"/>（造值）；
    /// 命中 → <see cref="IKvFunctions{TKey, TInput, TValue, TOutput, TContext}.InPlaceUpdater"/> 先行
    /// （追加前最后折叠，false 回落 CopyUpdater——FASTER mutable→immutable 派生序）；折叠 false →
    /// Error 零写入。旧记录恒保留（版本历史——append-only 真源多出能力）。
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="input">RMW 操作输入（增量/参数）。</param>
    /// <param name="functions">KV Functions 完整模型（三流折叠 + 完成回调）。</param>
    /// <param name="context">用户上下文（完成回调回传）。</param>
    /// <param name="policy">完成语义（Committed=组提交等待；其余不刷）。</param>
    /// <param name="timeToLive">TTL（过期惰性读删；null=无过期）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>操作状态（Ok=折叠写入；Error=折叠失败零写入）。</returns>
    /// <exception cref="ArgumentNullException">functions 为 null。</exception>
    public async ValueTask<KvStatus> RmwAsync<TInput, TOutput, TContext>(
        TKey key, TInput input,
        IKvFunctions<TKey, TInput, TValue, TOutput, TContext> functions,
        TContext context,
        KvCommitPolicy policy = KvCommitPolicy.FireAndForget,
        TimeSpan? timeToLive = null,
        CancellationToken ct = default)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(functions);

        // 1. 读折叠源（索引命中 → Ring 取值 → Parse；墓碑/未命中 = 无值——走 Initial 流）
        var current = await TryGetBytesAsync(key, ct).ConfigureAwait(false);
        var output = default(TOutput)!;   // functions 契约：output ref 非空；NotFound 语义由 status 承载
        TValue oldValue = default!;
        if (current is not null)
            oldValue = _formatter.Parse(current);

        // 2. 三流折叠
        TValue newValue;
        if (current is null)
        {
            newValue = default!;
            if (!functions.InitialUpdater(ref key, ref input, ref newValue, ref output, ref context))
            {
                functions.RmwCompletionCallback(ref output, KvStatus.Error, context);
                return KvStatus.Error;
            }
        }
        else if (functions.InPlaceUpdater(ref key, ref input, ref oldValue, ref output, ref context))
        {
            newValue = oldValue;   // InPlace 等价流：原地改写语义——折叠产物即旧值形态
        }
        else
        {
            newValue = default!;
            if (!functions.CopyUpdater(ref key, ref input, ref oldValue, ref newValue, ref output, ref context))
            {
                functions.RmwCompletionCallback(ref output, KvStatus.Error, context);
                return KvStatus.Error;
            }
        }

        // 3. Format → 帧化 → 追加 → 索引换绑（append-only 统一路径；RMW 产物默认无过期）
        var size = _formatter.GetSize(newValue);
        var buffer = new byte[size];
        _formatter.Format(newValue, buffer);
        var addr = await PutFramedAsync(key, KvValueFraming.Frame(buffer, ExpiryTicks(timeToLive)), ct).ConfigureAwait(false);
        await ApplyCommitPolicyAsync(addr, policy, ct).ConfigureAwait(false);

        var status = KvStatus.Ok;
        functions.RmwCompletionCallback(ref output, status, context);
        return status;
    }

    /// <summary>
    /// Functions 驱动读（value→output 翻译走 <see cref="IKvFunctions{TKey, TInput, TValue, TOutput, TContext}.ConcurrentReader"/>
    /// ——kv 直连为共享记录并发读上下文；NotFound 时读钩子不征询，直接 NotFound 收口）。
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="input">读操作输入。</param>
    /// <param name="functions">KV Functions 完整模型（ConcurrentReader 翻译 value→output）。</param>
    /// <param name="context">用户上下文（完成回调回传）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>操作状态与翻译产物（NotFound/Ok）。</returns>
    /// <exception cref="ArgumentNullException">functions 为 null。</exception>
    public async ValueTask<(KvStatus Status, TOutput Output)> ReadAsync<TInput, TOutput, TContext>(
        TKey key, TInput input,
        IKvFunctions<TKey, TInput, TValue, TOutput, TContext> functions,
        TContext context,
        CancellationToken ct = default)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(functions);

        var bytes = await TryGetBytesAsync(key, ct).ConfigureAwait(false);
        var output = default(TOutput)!;   // functions 契约：output ref 非空；NotFound 语义由 status 承载
        KvStatus status;
        if (bytes is null)
        {
            status = KvStatus.NotFound;
        }
        else
        {
            var value = _formatter.Parse(bytes);
            functions.ConcurrentReader(ref key, ref input, ref value, ref output, ref context);
            status = KvStatus.Ok;
        }
        functions.ReadCompletionCallback(ref key, ref input, ref output, status, context);
        return (status, output);
    }

    /// <summary>
    /// Functions 驱动 Upsert（<see cref="IKvFunctions{TKey, TInput, TValue, TOutput, TContext}.SingleWriter"/>
    /// 校验钩子——false 拒绝写入以 Error 收口零写入；接受 → 追加 + Ok 收口）。
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="value">待写入的值。</param>
    /// <param name="input">操作输入。</param>
    /// <param name="functions">KV Functions 完整模型（SingleWriter 校验钩子）。</param>
    /// <param name="context">用户上下文（完成回调回传）。</param>
    /// <param name="policy">完成语义（Committed=组提交等待；其余不刷）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>操作状态（Ok=接受写入；Error=SingleWriter 拒绝零写入）。</returns>
    /// <exception cref="ArgumentNullException">functions 为 null。</exception>
    public async ValueTask<KvStatus> UpsertAsync<TInput, TOutput, TContext>(
        TKey key, TValue value, TInput input,
        IKvFunctions<TKey, TInput, TValue, TOutput, TContext> functions,
        TContext context,
        KvCommitPolicy policy = KvCommitPolicy.FireAndForget,
        CancellationToken ct = default)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(functions);

        if (!functions.SingleWriter(ref key, ref value))
        {
            functions.UpsertCompletionCallback(ref key, ref value, KvStatus.Error, context);
            return KvStatus.Error;
        }

        var size = _formatter.GetSize(value);
        var buffer = new byte[size];
        _formatter.Format(value, buffer);
        var addr = await PutAsync(key, buffer, ct).ConfigureAwait(false);
        await ApplyCommitPolicyAsync(addr, policy, ct).ConfigureAwait(false);

        functions.UpsertCompletionCallback(ref key, ref value, KvStatus.Ok, context);
        return KvStatus.Ok;
    }

    /// <summary>
    /// Functions 驱动删除（<see cref="IKvFunctions{TKey, TInput, TValue, TOutput, TContext}.DeleteCompletionCallback"/>
    /// 回传 NotFound/Ok——墓碑语义与裸 <see cref="DeleteAsync(TKey, CancellationToken)"/> 一致）。
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="input">操作输入。</param>
    /// <param name="functions">KV Functions 完整模型（DeleteCompletionCallback 回调）。</param>
    /// <param name="context">用户上下文（完成回调回传）。</param>
    /// <param name="policy">完成语义（Committed=组提交等待；其余不刷）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>操作状态（Ok=删除；NotFound=key 不存在）。</returns>
    /// <exception cref="ArgumentNullException">functions 为 null。</exception>
    public async ValueTask<KvStatus> DeleteAsync<TInput, TOutput, TContext>(
        TKey key, TInput input,
        IKvFunctions<TKey, TInput, TValue, TOutput, TContext> functions,
        TContext context,
        KvCommitPolicy policy = KvCommitPolicy.FireAndForget,
        CancellationToken ct = default)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(functions);

        var existed = await DeleteAsync(key, ct).ConfigureAwait(false);
        var status = existed ? KvStatus.Ok : KvStatus.NotFound;
        if (existed)
            await ApplyCommitPolicyAsync(_ring.TailAddress, policy, ct).ConfigureAwait(false);
        functions.DeleteCompletionCallback(ref key, status, context);
        return status;
    }

    /// <summary>完成语义档（Committed = 组提交等待覆盖；其余登记 pending 由会话收口——kv 直连无 pending 簿记，
    /// FireAndForget/WaitForPending 同义为不刷）。</summary>
    private async ValueTask ApplyCommitPolicyAsync(LogicalAddress addr, KvCommitPolicy policy, CancellationToken ct)
    {
        if (policy != KvCommitPolicy.Committed || !addr.IsValid || addr == LogicalAddress.Empty)
            return;
        await FlushGroupedAsync(addr, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 组提交（W6 尾巴接管）：首个到达者成为 leader 执行 FlushUntil，后续写加入本轮
    /// （提升 Target）并等轮次完成——fsync 摊薄给整轮并发写。★ leader 只对本轮启动时
    /// 快照的 flushTarget 负责；Target 被提升的部分由被提升者在轮后自行开新轮补刷
    /// （不饿死）。Committed 写返回 = 覆盖自己地址的轮已实刷完成（持久化契约不变）。
    /// </summary>
    private async ValueTask FlushGroupedAsync(LogicalAddress addr, CancellationToken ct)
    {
        while (true)
        {
            FlushRound round;
            var leader = false;
            lock (_flushLock)
            {
                if (addr <= _flushedFloor)
                    return;   // 已被更早轮覆盖（锁内读——单调仅 leader 推进）
                if (_flushRound is null)
                {
                    _flushRound = new FlushRound { Target = addr };
                    round = _flushRound;
                    leader = true;
                }
                else
                {
                    round = _flushRound;
                    if (addr > round.Target) round.Target = addr;   // 加入本轮（提升目标）
                    leader = false;
                }
            }

            if (leader)
            {
                // ★ 快照本 leader 实际落盘目标：round.Target 可被并发 follower 提升，而
                //   FlushUntilAsync 参数在启动时已定格——若 finally 用提升值推 _flushedFloor，
                //   被提升者复核 addr <= floor 误判"已覆盖"直接返回（未落盘即 Committed——
                //   持久化契约破坏）。floor 只推实刷值；被提升者醒来复核不满足，自行开新轮。
                var flushTarget = round.Target;
                try
                {
                    await _ring.FlushUntilAsync(flushTarget, ct).ConfigureAwait(false);
                }
                finally
                {
                    lock (_flushLock)
                    {
                        if (flushTarget > _flushedFloor) _flushedFloor = flushTarget;
                        if (_flushRound == round) _flushRound = null;
                    }
                    round.Completion.TrySetResult();
                }
                // loop 复核：flush 期间到达的更大目标开新轮
            }
            else
            {
                await round.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
                // loop 复核：本轮若未覆盖自己（被并发提升者占位），下一轮再等
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 检查点（W5——快速恢复：索引帧物化 + 尾部增量重放，非全量重放）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>检查点结果（版本图代际标签 + 检查点尾地址 + Watch 续传游标）。</summary>
    /// <param name="Version">检查点版本（分配器新消费高水位——恢复后代际续接）。</param>
    /// <param name="TailAddress">检查点后 Ring 尾（下一写入位——落盘锚点）。</param>
    /// <param name="WatchCursor">Watch 续传游标 = 检查点时点最后已发布事件地址（提交门内与
    /// 尾地址同拍捕获）——<c>TierKv&lt;TKey, TValue&gt;.WatchAsync</c> 的规范 from 形态
    /// （裸 TailAddress 是「下一写入位」，恢复后首条记录地址与之重合，非事件游标）。</param>
    public readonly record struct KvCheckpointResult(long Version, LogicalAddress TailAddress,
        LogicalAddress WatchCursor);

    /// <summary>
    /// 检查点（tierkv-design.md §2.3——编排三拍，提交门内串行）：
    /// ① Ring 全量落盘（FlushUntil(Tail)——索引帧 W 锚定当前尾）→
    /// ② 主索引帧落盘（<see cref="KvIndexKind"/> 按族触发帧写——恢复物化锚点）→
    /// ③ 版本图入 opaque（分配器高水位=检查点版本，消费一个新版本作代际标签）随 2PC Prepare
    /// 原子落盘。崩溃后恢复 = 索引帧物化（O(索引)）+ 增量重放 (W, Tail]（非全量重放；
    /// 帧缺失/损坏 fail-safe 回退全量——派生重放教义）。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>检查点结果（版本代际标签 + 检查点尾地址 + Watch 续传游标）。</returns>
    public async ValueTask<KvCheckpointResult> CheckpointAsync(CancellationToken ct = default)
    {
        EnsureReady();
        await _commitGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // ① Ring 全量落盘
            await _ring.FlushUntilAsync(_ring.TailAddress, ct).ConfigureAwait(false);

            // ② 主索引帧落盘（按族触发——索引引用快照；与热切换发布在提交门内互斥）
            var currentIndex = Volatile.Read(ref _index);
            var frameWritten = currentIndex switch
            {
                ProbingIndexBase<TKey> probing => probing.CheckpointFrame(),
                SortedIndexBase<TKey> sorted => sorted.CheckpointFrame(),
                _ => false,
            };

            // ③ 版本图（检查点版本=新分配高水位）随 Prepare 原子落盘
            var checkpointVersion = _versions.NextVersion();
            var seq = NextBatchSeq();
            if (_options.MetaPolicyKind != MetaPolicyKind.Disabled)
            {
                var opaque = new byte[KvVersionAllocator.OpaqueSize];
                KvVersionAllocator.WriteOpaque(opaque, _versions.HighWater, _sweepWatermark);
                _ring.SetOpaqueMeta(opaque);
            }
            await _ring.PrepareAsync(seq, ct).ConfigureAwait(false);
            _ring.ConfirmCommitted(seq);

            return new KvCheckpointResult(checkpointVersion, _ring.TailAddress,
                _watchHub.PublishedWatermark);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 索引热切换（W-Hot D9——四步版本推进：开窗→追平→发布→释放）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>热切换结果（EPVS 发布版本 + 切换起点地址=双写窗开启时 Ring 尾）。</summary>
    /// <param name="Version">EPVS 发布版本（版本推进序）。</param>
    /// <param name="SwitchStartAddress">切换起点地址（双写窗开启时 Ring 安全快照尾）。</param>
    public readonly record struct KvIndexSwitchResult(long Version, LogicalAddress SwitchStartAddress);

    /// <summary>
    /// 运行中主索引热切换（D9——数据不丢：Ring 唯一真源，索引是可重建派生结构）。
    /// <para>★ 三步协议（写者/读者经 EPVS 版本保护区，版本推进自动排水——
    /// <see cref="EpochProtectedVersionScheme.AdvanceVersionWithCriticalSection"/> 单步积木组合）：</para>
    /// <para>① <b>构建</b>（提交门内，可抛——EPVS 未动）：工厂构建新索引 + 启动其自带窗口重放
    /// [Begin, T0)（T0=当前 Ring 尾）。失败即中止，原索引服务不受影响。</para>
    /// <para>② <b>追平</b>：await 新索引 Ready——不持门不持 epoch，写者/读者全程正常。</para>
    /// <para>③ <b>补扫+发布</b>（版本推进，排水临界区）：追平循环已把增量收敛到残余微量，
    /// 排水保证「已 Enter 写者」全部完成换绑后，同步游标补扫残余 (追平点, T1]（换绑同地址幂等）
    /// → 新索引=全量快照 → 单引用原子发布（<see cref="IndexCurrent"/> 换绑瞬间完成，读者零感知）。</para>
    /// <para>★ 释放：旧索引发布后 Dispose（排水已等在途写者；读者持单引用快照纯读，GC 保活安全）；
    /// 新索引进 <see cref="LifecycleBase{THints}.Resources"/> 释放清单。写路径零切换感知（无双写窗）。</para>
    /// <para>★ 阻塞面：③ 持提交门+排水（与原子批/检查点/回收互斥，补扫量=重放期间的写入增量）；
    /// 点查/单写/Watch/范围扫描全程不受阻。</para>
    /// <para>★ 重启语义：热切换是运行时视图操作——重启恢复 Options.IndexKind 装配的原族
    /// （跨索引重启走 Ring 重放全量重建，数据不丢）；需保持切换后族请同步调整装配配置。
    /// 切换后检查点落新索引帧——重启按 Options 装配旧族时帧族不匹配走 fail-safe 全量重放（数据等价）。</para>
    /// </summary>
    /// <param name="newIndexFactory">新索引工厂（两族实例——闭包捕获 fs 与 Settings，如
    /// <c>(o, ring) =&gt; new ByteOrderBTreeIndex&lt;TKey&gt;(fs, TierKvAssembly.BTreeSettings(fs, o), keyResolver: ring)</c>）。</param>
    /// <param name="ct">取消令牌（追平途中取消=切换中止：排水关窗+释放半成品，原索引服务不中断）。</param>
    /// <returns>热切换结果（EPVS 发布版本 + 切换起点地址）。</returns>
    /// <exception cref="ArgumentNullException">newIndexFactory 为 null。</exception>
    /// <exception cref="InvalidOperationException">索引热切换进行中（不可并发发起）或新索引启动/追平失败。</exception>
    /// <exception cref="ArgumentException">新索引非两族基类实例或与当前索引同实例。</exception>
    public async ValueTask<KvIndexSwitchResult> SwitchIndexAsync(
        Func<TierKvOptions, RingBase<TKey>, IIndex<TKey>> newIndexFactory,
        CancellationToken ct = default)
    {
        EnsureReady();
        ArgumentNullException.ThrowIfNull(newIndexFactory);
        if (Interlocked.CompareExchange(ref _switching, 1, 0) != 0)
            throw new InvalidOperationException("索引热切换进行中——不可并发发起（等待当前切换完成）");
        try
        {
            // ★ EPVS 临界区禁抛：临界区抛异常=状态机卡中间态=全部后续 Enter 永久阻塞（实测挂死）。
            //   一切可抛操作（工厂/校验/Initialize）先于版本推进；临界区内只做纯内存状态写入。

            // ① 构建（可抛——EPVS 未动；重放窗口终点 T0 = 当前尾）
            var fresh = newIndexFactory(_options, _ring);
            if (fresh is not ProbingIndexBase<TKey> and not SortedIndexBase<TKey>)
                throw new ArgumentException(
                    "新索引须为 ProbingIndexBase<TKey>/SortedIndexBase<TKey> 两族实例（同 ctor 闸门）",
                    nameof(newIndexFactory));
            if (ReferenceEquals(fresh, Volatile.Read(ref _index)))
                throw new ArgumentException("新索引与当前索引同实例——切换无意义", nameof(newIndexFactory));

            // ★ 安全快照尾（lock 内随 header 写完单调推进——之前全部完整可见）：T0 若取分配尾，
            //   在途槽（已分配 header 未写）会被重放扫描跳过，而 caughtUpTo 起点即 T0——这些记录
            //   永远不再被扫（压强回归实测散点/整批丢失）。
            var replayStart = _ring.TakeSafeSnapshotTail();   // T0：重放窗口终点（开区间）
            try
            {
                switch (fresh)
                {
                    case ProbingIndexBase<TKey> probing:
                        probing.Initialize(new ProbingIndexRecoveryHints(_ring.BeginAddress, replayStart));
                        break;
                    case SortedIndexBase<TKey> sorted:
                        sorted.Initialize(new SortedIndexRecoveryHints(_ring.BeginAddress, replayStart));
                        break;
                }
            }
            catch (Exception ex)
            {
                await DisposeIndexAsync(fresh).ConfigureAwait(false);
                throw new InvalidOperationException("新索引启动失败——切换中止，原索引服务不受影响", ex);
            }

            // ② 等待重放 [Begin, T0) 完成（fresh 自带窗口重放——Ready 后 fresh 已含 T0 前全量）。
            //    ★ 原追平循环（(T0,尾] 多轮增量）删除——多轮窗口与并发写的推进交错存在缝隙
            //      （分配尾≠可见尾/游标越过在途槽，压强回归散点丢失实锤）；改由 ③发布临界区内
            //      (T0, T1] 单窗口连续补扫承接全部增量——窗口少、无缝隙可言。
            try
            {
                await ReadyIndexAsync(fresh, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                await DisposeIndexAsync(fresh).ConfigureAwait(false);   // 未发布——fresh 不承载服务
                throw;
            }

            // ③ 补扫 + 发布（版本推进 #1——排水临界区内原子完成）：排水保证「已 Enter 的写者」
            //    全部完成换绑；临界区内 (T0, T1] 单窗口连续补扫（T0=重放终点，T1=排水后安全快照尾）
            //    ——换绑写入同地址幂等，无重无乱序 → 新索引=全量快照 → 单引用发布。
            //    ★ 补扫上界取安全快照尾（非分配尾）：分配尾含「已分配 header 未写」的在途槽。
            await _commitGate.WaitAsync(ct).ConfigureAwait(false);
            var switchStart = LogicalAddress.Empty;
            long publishedVersion;
            IIndex<TKey> retired;
            Exception? catchUpFailure = null;
            try
            {
                publishedVersion = 0;
                retired = null!;
                _sessionEpvs.AdvanceVersionWithCriticalSection((_, toVersion) =>
                {
                    try
                    {
                        switchStart = _ring.TakeSafeSnapshotTail();   // T1：排水后安全尾——补扫上界（开区间）
                        CatchUpTail(fresh, replayStart, switchStart);   // 同步游标——(T0, T1) 单窗口连续
                        retired = Volatile.Read(ref _index);
                        Volatile.Write(ref _index, fresh);          // 单引用发布（原子换绑）
                        publishedVersion = toVersion;
                        Resources.TryAdd(fresh, "switched-index-" + Guid.NewGuid().ToString("N"),
                            ResourceOwnership.Owned);
                    }
                    catch (Exception ex)
                    {
                        catchUpFailure = ex;   // 不发布——原索引服务不中断，异常临界区外重抛
                    }
                });
            }
            finally
            {
                _commitGate.Release();
            }

            if (catchUpFailure is not null)
            {
                await DisposeIndexAsync(fresh).ConfigureAwait(false);   // 未发布——fresh 只含追平数据
                throw new InvalidOperationException("增量追平失败——切换中止，原索引服务不受影响", catchUpFailure);
            }

            // ④ 释放（发布已排水在途写者；旧索引进 Resources 清单，TierKv 释放时幂等二次 Dispose）
            await DisposeIndexAsync(retired).ConfigureAwait(false);
            return new KvIndexSwitchResult(publishedVersion, switchStart);
        }
        finally
        {
            Volatile.Write(ref _switching, 0);
        }
    }

    /// <summary>上次恢复主存储帧是否物化成功（IVT 观测面——快速恢复 vs 全量重放断言；false=帧缺失/损坏 fail-safe）。</summary>
    internal bool IndexMainStorageAppliedLastRecovery => Volatile.Read(ref _index) switch
    {
        ProbingIndexBase<TKey> probing => probing.MainStorageAppliedLastRecovery,
        SortedIndexBase<TKey> sorted => sorted.MainStorageAppliedLastRecovery,
        _ => false,
    };

    // ═══════════════════════════════════════════════════════════════════
    // 范围扫描（W7 F1——key 字节序 BTree 范围索引 + KV 级游标 API）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>扫描结果（key + 格式化值）。</summary>
    /// <param name="Key">键。</param>
    /// <param name="Value">经 formatter 逆翻译的值。</param>
    public readonly record struct KvScanEntry(TKey Key, TValue Value);

    /// <summary>扫描结果（含过期维度——#442 状态机快照导出/导出面）。</summary>
    /// <param name="Key">键。</param>
    /// <param name="Value">经 formatter 逆翻译的值。</param>
    /// <param name="ExpiryTicksUtc">过期点（UTC Ticks）；0 = 无过期（永久条目）。</param>
    public readonly record struct KvScanEntryWithExpiry(TKey Key, TValue Value, long ExpiryTicksUtc);

    /// <summary>
    /// 前缀扫描（F1——key 字节序）：按字节序 SeekLowerBound 起，产出前
    /// <paramref name="prefixByteLength"/> 字节与 <paramref name="prefix"/> 一致的全部存活条目
    /// （最新值；已删 key 不产出）。需 <see cref="TierKvOptions.EnableRangeIndex"/>。
    /// <para>★ 定长 key 的"前缀"必须显式给字节长度（prefix 本身是完整 TKey——低字节在前，
    ///   未涉及的高字节请传 0，如 1 字节前缀 0x10 = <c>ScanByPrefixAsync(0x10, 1)</c>）。</para>
    /// </summary>
    /// <param name="prefix">前缀（完整 TKey——低字节在前，未涉及高字节传 0）。</param>
    /// <param name="prefixByteLength">前缀字节数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>前缀匹配的存活条目流（key 字节序）。</returns>
    /// <exception cref="InvalidOperationException">范围索引未启用（需 WithRangeIndex(true)）。</exception>
    public async IAsyncEnumerable<KvScanEntry> ScanByPrefixAsync(TKey prefix, int prefixByteLength,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var range = RangeIndexOrThrow();
        await using var cursor = range.CreateScanCursor(ReadDirection.Forward);
        // ★ 游标协议：Seek 后 Current* 即首个条目——先读后推进（先 MoveNext 会跳过首条）
        if (!cursor.SeekLowerBound(prefix))
            yield break;

        var batch = new List<(TKey Key, LogicalAddress Addr)>(_scanBatchSize);
        while (true)
        {
            // ① 预扫一批（key 序产出）
            batch.Clear();
            var exhausted = false;
            while (batch.Count < _scanBatchSize)
            {
                var key = cursor.CurrentKey;
                if (!KeyByteOrderComparer<TKey>.IsBytePrefix(key, prefix, prefixByteLength))
                {
                    exhausted = true;   // 字节序越出前缀区间——后续必越
                    break;
                }
                batch.Add((key, cursor.CurrentValue));
                if (!await cursor.MoveNextAsync(ct).ConfigureAwait(false))
                {
                    exhausted = true;
                    break;
                }
            }
            if (batch.Count == 0) yield break;

            // ② 模式选择执行（按批内 key 序产出）
            await foreach (var entry in ExecuteScanBatchAsync(batch, ct).ConfigureAwait(false))
                yield return new KvScanEntry(entry.Key, entry.Value);

            if (exhausted) yield break;
        }
    }

    /// <summary>前缀扫描（含过期维度——#442 状态机快照导出面）：语义同
    /// <see cref="ScanByPrefixAsync(TKey, int, System.Threading.CancellationToken)"/>，额外回传
    /// 每条目的过期点。</summary>
    /// <param name="prefix">前缀（完整 TKey——低字节在前，未涉及高字节传 0）。</param>
    /// <param name="prefixByteLength">前缀字节长度。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>前缀区间存活条目流（key 字节序；ExpiryTicksUtc = UTC Ticks，0 = 无过期）。</returns>
    /// <exception cref="InvalidOperationException">范围索引未启用（需 WithRangeIndex(true)）。</exception>
    public async IAsyncEnumerable<KvScanEntryWithExpiry> ScanByPrefixWithExpiryAsync(TKey prefix, int prefixByteLength,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var range = RangeIndexOrThrow();
        await using var cursor = range.CreateScanCursor(ReadDirection.Forward);
        if (!cursor.SeekLowerBound(prefix))
            yield break;

        var batch = new List<(TKey Key, LogicalAddress Addr)>(_scanBatchSize);
        while (true)
        {
            batch.Clear();
            var exhausted = false;
            while (batch.Count < _scanBatchSize)
            {
                var key = cursor.CurrentKey;
                if (!KeyByteOrderComparer<TKey>.IsBytePrefix(key, prefix, prefixByteLength))
                {
                    exhausted = true;
                    break;
                }
                batch.Add((key, cursor.CurrentValue));
                if (!await cursor.MoveNextAsync(ct).ConfigureAwait(false))
                {
                    exhausted = true;
                    break;
                }
            }
            if (batch.Count == 0) yield break;

            await foreach (var entry in ExecuteScanBatchAsync(batch, ct).ConfigureAwait(false))
                yield return entry;

            if (exhausted) yield break;
        }
    }

    /// <summary>
    /// 范围扫描（F1——key 字节序）：产出 [start, end) 字节序区间内全部存活条目（最新值）。
    /// 需 <see cref="TierKvOptions.EnableRangeIndex"/>。
    /// </summary>
    /// <param name="start">区间下界（含）。</param>
    /// <param name="end">区间上界（不含）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>[start, end) 区间存活条目流（key 字节序）。</returns>
    /// <exception cref="InvalidOperationException">范围索引未启用（需 WithRangeIndex(true)）。</exception>
    public async IAsyncEnumerable<KvScanEntry> ScanByRangeAsync(TKey start, TKey end,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {

        var cmp = new KeyByteOrderComparer<TKey>();
        if (cmp.Compare(start, end) >= 0)
            yield break;   // 空区间

        var range = RangeIndexOrThrow();
        await using var cursor = range.CreateScanCursor(ReadDirection.Forward);
        // ★ 游标协议：Seek 后 Current* 即首个条目——先读后推进（先 MoveNext 会跳过首条）
        if (!cursor.SeekLowerBound(start))
            yield break;

        var batch = new List<(TKey Key, LogicalAddress Addr)>(_scanBatchSize);
        while (true)
        {
            // ① 预扫一批（key 序产出）
            batch.Clear();
            var exhausted = false;
            while (batch.Count < _scanBatchSize)
            {
                var key = cursor.CurrentKey;
                if (cmp.Compare(key, end) >= 0)
                {
                    exhausted = true;   // 越上界（[start, end)）
                    break;
                }
                batch.Add((key, cursor.CurrentValue));
                if (!await cursor.MoveNextAsync(ct).ConfigureAwait(false))
                {
                    exhausted = true;
                    break;
                }
            }
            if (batch.Count == 0) yield break;

            // ② 模式选择执行（按批内 key 序产出）
            await foreach (var entry in ExecuteScanBatchAsync(batch, ct).ConfigureAwait(false))
                yield return new KvScanEntry(entry.Key, entry.Value);

            if (exhausted) yield break;
        }
    }

    /// <summary>范围扫描（含过期维度——#442 状态机快照导出面）：语义同
    /// <see cref="ScanByRangeAsync(TKey, TKey, System.Threading.CancellationToken)"/>，额外回传
    /// 每条目的过期点。</summary>
    /// <param name="start">区间下界（含）。</param>
    /// <param name="end">区间上界（不含）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>[start, end) 区间存活条目流（key 字节序；ExpiryTicksUtc = UTC Ticks，0 = 无过期）。</returns>
    /// <exception cref="InvalidOperationException">范围索引未启用（需 WithRangeIndex(true)）。</exception>
    public async IAsyncEnumerable<KvScanEntryWithExpiry> ScanByRangeWithExpiryAsync(TKey start, TKey end,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var cmp = new KeyByteOrderComparer<TKey>();
        if (cmp.Compare(start, end) >= 0)
            yield break;

        var range = RangeIndexOrThrow();
        await using var cursor = range.CreateScanCursor(ReadDirection.Forward);
        if (!cursor.SeekLowerBound(start))
            yield break;

        var batch = new List<(TKey Key, LogicalAddress Addr)>(_scanBatchSize);
        while (true)
        {
            batch.Clear();
            var exhausted = false;
            while (batch.Count < _scanBatchSize)
            {
                var key = cursor.CurrentKey;
                if (cmp.Compare(key, end) >= 0)
                {
                    exhausted = true;
                    break;
                }
                batch.Add((key, cursor.CurrentValue));
                if (!await cursor.MoveNextAsync(ct).ConfigureAwait(false))
                {
                    exhausted = true;
                    break;
                }
            }
            if (batch.Count == 0) yield break;

            await foreach (var entry in ExecuteScanBatchAsync(batch, ct).ConfigureAwait(false))
                yield return entry;

            if (exhausted) yield break;
        }
    }

    /// <summary>
    /// 批执行器（P1 扫描模式选择器核心）：批内 addr 跨度（页数）≤ 阈值走
    /// <b>顺序区间扫</b>（Ring 区间游标 + addr 集合匹配——聚集负载最优，恢复顺序局部性）；
    /// 否则走 <b>MRR 排序批量回表</b>（addr 排序 + GetValueSpan 顺序回表——部分恢复局部性）。
    /// 两模式均按批内 key 序产出（调用方语义不变）；meta/墓碑/过期照常过滤
    /// （addr 集合匹配天然滤 meta，帧校验滤墓碑/过期）。
    /// </summary>
    private async IAsyncEnumerable<KvScanEntryWithExpiry> ExecuteScanBatchAsync(
        List<(TKey Key, LogicalAddress Addr)> batch,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();   // 批执行本体刻意全同步（span 不进 async）——首笔让出满足异步迭代器契约
        var minAddr = batch[0].Addr;
        var maxAddr = batch[0].Addr;
        foreach (var (_, a) in batch)
        {
            if (a < minAddr) minAddr = a;
            if (a > maxAddr) maxAddr = a;
        }

        // addr→batch key 映射（回表后按 batch key 序产出；addr 匹配天然滤 meta record）
        var keyByAddr = new Dictionary<LogicalAddress, TKey>(batch.Count);
        foreach (var (key, addr) in batch)
            keyByAddr[addr] = key;

        var results = new List<KvScanEntryWithExpiry>(batch.Count);
        ExecuteBatchCore(batch, minAddr, maxAddr, keyByAddr, NowUtcTicks, results);

        // ★ 按 batch 内 key 序产出（模式 A 的 Ring 区间扫按物理序收集，覆写后 addr 序≠key 序——重排）
        var byKey = new Dictionary<TKey, KvScanEntryWithExpiry>(results.Count);
        foreach (var entry in results)
            byKey[entry.Key] = entry;
        foreach (var (key, _) in batch)
        {
            if (byKey.TryGetValue(key, out var withExpiry))
                yield return withExpiry;
        }
    }

    /// <summary>批执行本体（同步——scope/span 均不进 async 方法，C#12 约束）。</summary>
    private void ExecuteBatchCore(
        List<(TKey Key, LogicalAddress Addr)> batch,
        LogicalAddress minAddr, LogicalAddress maxAddr,
        Dictionary<LogicalAddress, TKey> keyByAddr, long now,
        List<KvScanEntryWithExpiry> results)
    {
        if (_ring.SpanPages(minAddr, maxAddr) <= _scanSpanPages)
        {
            // ── 模式 A：顺序区间扫（addr 聚集——恢复顺序局部性）──
            // ★ 游标零拷贝：MoveNext 已解析 header，CurrentValue 即 TierKv 帧 span（下次 MoveNext 前有效）——
            //   严禁此处再走 GetValueSpan（重复页定位+header 解析，实测退化 8×）
            using var ringCursor = _ring.OpenScanCursor(minAddr, default);
            while (ringCursor.MoveNext())
            {
                if (ringCursor.CurrentAddress > maxAddr) break;   // 越过批次上界——提前退出
                if (results.Count >= batch.Count) break;          // 全部命中——提前退出
                if (!keyByAddr.TryGetValue(ringCursor.CurrentAddress, out var batchKey)) continue;

                var record = _ring.GetKey(ringCursor.CurrentAddress);
                if (record.IsTombstone) continue;
                var buf = ArrayPool<byte>.Shared.Rent(record.ValueLength);
                try
                {
                    if (!_ring.TryGetValue(ringCursor.CurrentAddress, buf.AsSpan(0, record.ValueLength), out var got))
                        continue;   // 无有效记录（自愈读收敛 NotFound）——跳过
                    if (got != record.ValueLength) continue;
                    if (!KvValueFraming.IsFramed(buf)) continue;
                    var expiryTicks = KvValueFraming.ExpiryTicks(buf);
                    if (KvValueFraming.IsExpired(expiryTicks, now)) continue;
                    results.Add(new KvScanEntryWithExpiry(batchKey,
                        _formatter.Parse(buf.AsSpan(KvValueFraming.HeaderSize,
                            record.ValueLength - KvValueFraming.HeaderSize)), expiryTicks));
                }
                finally { ArrayPool<byte>.Shared.Return(buf); }
            }
        }
        else
        {
            // ── 模式 B：MRR 排序批量回表（addr 分散——排序恢复页局部性）──
            foreach (var (_, addr) in batch.OrderBy(b => b.Addr))
            {
                var batchKey = keyByAddr[addr];
                if (TryParseFrameWithExpiryAt(addr, now, out var entry, out var expiry))
                    results.Add(new KvScanEntryWithExpiry(batchKey, entry, expiry));
            }
        }
    }

    /// <summary>按地址解析值帧（span 消费同步助手——span 仅同步栈内合法，Ring 内容自愈读零保护）：
    /// 帧无效/过期返回 false。</summary>
    private bool TryParseFrameAt(LogicalAddress addr, long now, out TValue value)
        => TryParseFrameWithExpiryAt(addr, now, out value, out _);

    /// <summary>按地址解析值帧并回传过期点（#442 导出面——UTC Ticks，0 = 无过期）：
    /// 帧无效/过期返回 false。</summary>
    private bool TryParseFrameWithExpiryAt(LogicalAddress addr, long now, out TValue value, out long expiryTicksUtc)
    {
        var frame = _ring.GetValueSpan(addr);
        expiryTicksUtc = KvValueFraming.ExpiryTicks(frame);
        if (!KvValueFraming.IsFramed(frame) ||
            KvValueFraming.IsExpired(expiryTicksUtc, now))
        {
            value = default!;
            return false;
        }
        value = _formatter.Parse(frame.Slice(KvValueFraming.HeaderSize));
        return true;
    }

    /// <summary>范围索引（未启用即抛——配置期缺失，运行期 fail-fast）。</summary>
    private SortedIndexBase<TKey> RangeIndexOrThrow()
        => _rangeIndex ?? throw new InvalidOperationException(
            "范围索引未启用——TierKvOptions.WithRangeIndex(true) 装配后 Scan API 可用");
    // ═══════════════════════════════════════════════════════════════════
    // Watch 变更流（W9 F5——按 key/前缀订阅，地址游标续传防丢）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 订阅变更流（F5——etcd Watch 对齐）：产出 <paramref name="from"/> 之后的全部变更
    /// （历史补扫 + 实时直通无缝衔接），可带 key 字节前缀过滤。
    /// <para>★ 游标语义：续传点 = 已见最大事件地址，产出开区间 <c>(from, ...]</c>；
    /// <see cref="LogicalAddress.Empty"/> = 从头订阅（自回收线起全史）。</para>
    /// <para>★ 续传防丢（etcd compaction 同义）：历史 = Ring 真源扫描（墓碑 → Delete 事件），
    /// 只要续传点未被回收（≥ <see cref="BeginAddress"/>）即无丢无重——订阅注册与发布水位
    /// 锁内原子捕获，补扫区间与实时通道按地址严格不相交。</para>
    /// <para>★ 断连契约：每订阅者有界通道（<see cref="TierKvOptions.WatchChannelCapacity"/>），
    /// 慢订阅者写满即断连——枚举抛 <see cref="KvWatchDisconnectedException"/> = 断连信号，
    /// 须凭游标重订阅续传；流干净结束（枚举正常收束）= 存储关闭收口，不得重订；
    /// 写路径永不被反压。取消令牌触发时枚举抛 <see cref="OperationCanceledException"/>。</para>
    /// <para>★ 持久性：事件 = 已提交事实（批路径发布于 ConfirmCommitted 之后）；但单写
    /// FireAndForget 档未刷盘记录崩溃后消失——其事件持久性跟随写入的提交档。</para>
    /// <para>★ TTL：过期不发事件（惰性读删无确定时刻，读侧表现为未命中）；回收强删不发事件
    /// （物理治理非数据变更）。</para>
    /// </summary>
    /// <param name="from">续传游标（开区间起点；Empty = 从头订阅）。</param>
    /// <param name="prefix">key 前缀（定长 TKey——低字节在前，未涉及高字节传 0）。</param>
    /// <param name="prefixByteLength">前缀字节长度（0 = 不过滤全 key）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="InvalidOperationException">续传点已越出回收线（历史事件已被回收）。</exception>
    /// <returns>变更事件流（历史补扫 + 实时直通；开区间 (from, ...]）。</returns>
    /// <exception cref="ArgumentException">from 既非合法地址也非 Empty。</exception>
    public async IAsyncEnumerable<KvWatchEvent<TKey>> WatchAsync(
        LogicalAddress from, TKey prefix, int prefixByteLength,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureReady();
        if (!from.IsValid && from != LogicalAddress.Empty)
            throw new ArgumentException($"续传游标须为合法地址或 Empty（从头订阅），实际 {from}", nameof(from));
        if (from != LogicalAddress.Empty && from < _ring.BeginAddress)
            throw new InvalidOperationException(
                $"续传游标 {from} 已越出回收线（BeginAddress={_ring.BeginAddress}）——历史事件已被回收，" +
                "须从更近游标续传（记录检查点尾地址作 Watch 游标）");

        var (reader, watermark) = _watchHub.Subscribe(prefix, prefixByteLength);
        try
        {
            // 1. 历史补扫 [scanFrom, watermark)：真源扫描产出全部已提交变更（水位 = 锁内捕获，
            //    与实时通道事件地址严格不相交——无重无漏）。watermark=Empty（尚无发布）跳过——
            //    Ring 扫描 end 语义「default 取当前尾」，空水位不能进扫描区间。
            var scanFrom = from == LogicalAddress.Empty ? _ring.BeginAddress : from;
            if (watermark != LogicalAddress.Empty && watermark > scanFrom)
            {
                await foreach (var (key, addr, tombstone) in _ring.ScanAsync(scanFrom, watermark, ct).ConfigureAwait(false))
                {
                    if (from != LogicalAddress.Empty && addr <= from) continue;   // 开区间 (from, ...]
                    if (prefixByteLength != 0 && !KeyByteOrderComparer<TKey>.IsBytePrefix(key, prefix, prefixByteLength))
                        continue;
                    yield return new KvWatchEvent<TKey>(key,
                        tombstone ? KvWatchEventKind.Delete : KvWatchEventKind.Put, addr);
                }
            }

            // 2. 水位地址自身的事件补产出：它已发布（订阅前），通道不会有；补扫区间 [.., watermark)
            //    开区间不含它——单读该地址记录补齐（Ring 扫描无「含端点」形态，地址算术需记录长度不可用）。
            //    watermark 为下一写入位（如恢复重置=Tail）时无记录，越回收线同理——守卫跳过。
            if (watermark.IsValid && watermark < _ring.TailAddress && watermark >= _ring.BeginAddress
                && (from == LogicalAddress.Empty || watermark > from))
            {
                var record = await _ring.GetKeyAsync(watermark, ct).ConfigureAwait(false);
                if (prefixByteLength == 0 || KeyByteOrderComparer<TKey>.IsBytePrefix(record.Key, prefix, prefixByteLength))
                    yield return new KvWatchEvent<TKey>(record.Key,
                        record.IsTombstone ? KvWatchEventKind.Delete : KvWatchEventKind.Put, watermark);
            }

            // 2. 实时直通（补扫期间到达的发布已在通道排队；流结束 = 断连）
            await foreach (var ev in reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return ev;
        }
        finally
        {
            _watchHub.Unsubscribe(reader);
        }
    }

    /// <summary>订阅变更流——全 key（无前缀过滤）。</summary>
    /// <param name="from">续传游标（Empty = 从头订阅）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>全 key 变更事件流。</returns>
    public IAsyncEnumerable<KvWatchEvent<TKey>> WatchAsync(LogicalAddress from, CancellationToken ct = default)
        => WatchAsync(from, default!, 0, ct);

    // ═══════════════════════════════════════════════════════════════════
    // 日志回收（W6——per-key 下界 + Compact 语义，§3.5）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>记录过期判定（W8 回收强删——过期记录不视为存活，下界可越过其物理回收）。</summary>
    private async ValueTask<bool> IsExpiredRecordAsync(LogicalAddress addr, CancellationToken ct)
    {
        var recordKey = await _ring.GetKeyAsync(addr, ct).ConfigureAwait(false);
        if (recordKey.IsTombstone) return false;
        var buf = new byte[recordKey.ValueLength];
        await _ring.GetValueAsync(addr, buf, ct).ConfigureAwait(false);
        return KvValueFraming.IsFramed(buf)
            && KvValueFraming.IsExpired(KvValueFraming.ExpiryTicks(buf), NowUtcTicks);
    }

    // ═══ 过期回收扫描（A.2a——水位推进式；tierkv-ttl-cas-addressread-design.md §1）═══
    //
    // 不做逐条墓碑化（写放大 + 破坏"过期不发事件"裁定自洽）：过期水位推进——扫描自上次水位起
    // 找最老的未过期绑定地址（waterline），其下前缀按地址序必为过期/墓碑/已删 → 复用回收线路径
    // 截断推进（物理强删，不发事件）。与手动 ReclaimAsync 同路互不冲突（提交门内串行 + 回收线单调）。

    /// <summary>累计过期强删计数（sweep 驱动的回收结果聚合——被物理回收的过期/墓碑/已删记录总数）。</summary>
    public long ExpiredSupersededCount => Interlocked.Read(ref _expiredSupersededCount);

    /// <summary>过期扫描水位（IVT 测试观测面——持久化恢复续接断言；提交门内字段，观测读无锁）。</summary>
    internal LogicalAddress SweepWatermark => _sweepWatermark;

    /// <summary>上一轮 sweep 扫过的记录数（IVT 诊断面——增量续扫不重复全扫的扫描计数断言）。</summary>
    internal long LastSweepScannedCount { get; private set; }

    /// <summary>手动触发一轮过期回收扫描（测试/运维入口——与后台循环同路：提交门内串行互斥）。</summary>
    /// <param name="ct">取消令牌（扫描/截断/落盘途中响应取消——取消窗口无部分提交：截断与水位持久化同门内完成）。</param>
    /// <returns>任务在整轮 sweep（扫描→截断→水位持久化）完成后完成。</returns>
    public async ValueTask ForceExpirySweepAsync(CancellationToken ct = default)
    {
        EnsureReady();
        await SweepExpiredCoreAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// sweep 核心（后台循环与 ForceExpirySweepAsync 统一路径——提交门内串行）：
    /// ① 增量扫描 [max(水位, Begin), 安全快照尾) 找 waterline = 最老未过期绑定地址；
    /// ② [Begin, min(waterline, 已刷水位)) 前缀全为过期/墓碑/已删 → TruncatePrefix 物理强删；
    /// ③ 扫描水位推进至截断边界，随 2PC Prepare 原子落盘（恢复续扫不重复全扫）。
    /// ★ 先截断后持久化：崩溃窗口内水位落后于截断点 = 下一轮重扫已回收前缀（幂等安全）——
    ///   绝不出现水位超前于截断点（跳过未回收记录）。alive 判定与 <see cref="ReclaimAsync"/> 同一语义
    ///   （索引绑定 + 惰性过期判）；死记录不可复活（换绑恒向新地址）——水位单调安全。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>任务在整轮完成后完成。</returns>
    private async ValueTask SweepExpiredCoreAsync(CancellationToken ct)
    {
        await _commitGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var oldBegin = _ring.BeginAddress;
            var flushed = _ring.FlushedUntilAddress;
            var index = Volatile.Read(ref _index);   // 回收期间索引引用快照（切换发布在提交门内互斥）
            var start = _sweepWatermark.IsValid && _sweepWatermark > oldBegin ? _sweepWatermark : oldBegin;

            // ① 增量扫描（安全快照尾——在途槽不含；水位自身=上轮最老存活，须重判故含起点）
            var waterline = LogicalAddress.Invalid;
            var reclaimed = 0L;
            var scanned = 0L;
            await foreach (var (key, addr, tombstone) in _ring.ScanAsync(start, _ring.TakeSafeSnapshotTail(), ct)
                .ConfigureAwait(false))
            {
                scanned++;
                var alive = !tombstone
                    && index.Find(key) == addr
                    && !await IsExpiredRecordAsync(addr, ct).ConfigureAwait(false);
                if (alive)
                {
                    if (waterline == LogicalAddress.Invalid) waterline = addr;   // 地址序——首个存活即最老
                }
                else if (waterline == LogicalAddress.Invalid && addr < flushed)
                {
                    reclaimed++;   // 截断点之前（地址序在 waterline 之前）且已刷——本轮将被强删
                }
            }
            LastSweepScannedCount = scanned;

            // ② 截断前缀（零可回收=不动；下界 clamp 已刷水位——未刷尾不回收，同 ReclaimAsync）
            var safeBound = waterline == LogicalAddress.Invalid ? flushed
                : waterline < flushed ? waterline : flushed;
            if (safeBound <= oldBegin)
                return;

            _ring.TruncatePrefix(safeBound);
            _sweepWatermark = safeBound;
            Interlocked.Add(ref _expiredSupersededCount, reclaimed);

            // ③ 水位随 2PC Prepare 原子落盘（meta Disabled = 不持久化，水位仅内存——重启从 Begin 重扫）
            if (_options.MetaPolicyKind != MetaPolicyKind.Disabled)
            {
                var opaque = new byte[KvVersionAllocator.OpaqueSize];
                KvVersionAllocator.WriteOpaque(opaque, _versions.HighWater, _sweepWatermark);
                _ring.SetOpaqueMeta(opaque);
                var seq = NextBatchSeq();
                await _ring.PrepareAsync(seq, ct).ConfigureAwait(false);
                _ring.ConfirmCommitted(seq);
            }
        }
        finally
        {
            _commitGate.Release();
        }
    }

    /// <summary>回收结果（新头边界 + 判定为被取代的记录数）。</summary>
    /// <param name="NewBeginAddress">回收后新头地址（截断下界）。</param>
    /// <param name="SupersededCount">判定为被同 key 更新记录/墓碑取代的记录数。</param>
    public readonly record struct KvReclaimResult(LogicalAddress NewBeginAddress, long SupersededCount);

    /// <summary>
    /// 日志回收（§3.5——版本链未开启的「低频全表扫」钦定路径）：扫 Ring [Begin, Tail) 逐记录判定
    /// <c>index.Find(key) == 本记录地址</c>（存活=某 key 当前最新记录）→ 存活最小地址为安全下界
    /// ——其下记录必被同 key 更新记录/墓碑取代 → <see cref="RingBase{TKey}.TruncatePrefix"/> 逻辑回收。
    /// <para>★ 读安全：下界 ≤ 一切存活记录地址，截断只回收严格低于下界的前缀（下界处记录存活）；
    /// 并发读经索引命中的地址恒 ≥ 下界。</para>
    /// <para>★ 索引帧互溶：帧条目指向旧存活记录，若已落回收区则必被同 key 更新记录取代——
    /// 恢复重放 (W, Tail] 重绑定最新地址（帧加速语义不受回收影响）。</para>
    /// <para>★ 会话钳制项（§3.5 min(活跃会话最早可见地址)）：本模型会话写集持值副本不持地址、
    /// 读恒走索引最新——会话不钉历史地址，钳制项空缺（Serializable 快照读深化时补位）。</para>
    /// <para>★ 物理段保留（truncateDevice=false 逻辑截断）——读安全优先，物理回收策略后置。
    /// 提交门内串行（与检查点/原子批互斥）；未落盘尾不回收（clamp 至 FlushedUntil）。</para>
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>回收结果（新头边界 + 被取代记录数）。</returns>
    public async ValueTask<KvReclaimResult> ReclaimAsync(CancellationToken ct = default)
    {
        EnsureReady();
        await _commitGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var oldBegin = _ring.BeginAddress;
            var flushed = _ring.FlushedUntilAddress;
            var index = Volatile.Read(ref _index);   // 回收期间索引引用快照（切换发布在提交门内互斥）

            // 1. 低频全表扫——存活最小地址（存活判定：索引最新指针即本记录）
            var safeBound = LogicalAddress.Invalid;   // 无存活记录=全表可回收
            var superseded = 0L;
            await foreach (var (key, addr, _) in _ring.ScanAsync(ct).ConfigureAwait(false))
            {
                if (index.Find(key) == addr && !await IsExpiredRecordAsync(addr, ct).ConfigureAwait(false))
                {
                    if (safeBound == LogicalAddress.Invalid || addr < safeBound)
                        safeBound = addr;
                }
                else
                {
                    superseded++;
                }
            }

            // 2. 零取代 = 无可回收（不做无意义截断）；下界 clamp：无存活→已刷全可回收；
            //    存活越界→clamp 至已刷水位（未刷尾不回收，TruncatePrefix 越已刷水位 fail-fast——双保险同义）
            if (superseded == 0)
                return new KvReclaimResult(oldBegin, 0);
            if (safeBound == LogicalAddress.Invalid || safeBound > flushed)
                safeBound = flushed;
            if (safeBound <= oldBegin)
                return new KvReclaimResult(oldBegin, superseded);

            // 3. 逻辑头截断（BeginAddress 单调推进——物理段保留）
            _ring.TruncatePrefix(safeBound);
            return new KvReclaimResult(safeBound, superseded);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 公开观测面
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>存活条目数（索引 O(1) 计数）。</summary>
    public long Count => Volatile.Read(ref _index).EntryCount;

    /// <summary>Ring 尾地址（数据写入末尾）。</summary>
    public LogicalAddress TailAddress => _ring.TailAddress;

    /// <summary>Ring 头地址（截断下界）。</summary>
    public LogicalAddress BeginAddress => _ring.BeginAddress;

    /// <summary>Ring 参与者（2PC——Session 域注册用，W2）。</summary>
    public ITransactionParticipant RingParticipant => _ring;

    /// <summary>索引参与者（2PC——Session 域注册用，W2；两族基类均实现——ctor 闸门保证转型成立）。</summary>
    public ITransactionParticipant IndexParticipant => (ITransactionParticipant)_index;

    // ═══════════════════════════════════════════════════════════════════
    // 写（append-only 换绑）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>写入 key→值字节（同 key 覆写——索引 CAS 换绑新地址，旧值留 Ring 由回收治理）。无过期。</summary>
    /// <param name="key">键。</param>
    /// <param name="value">值字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>写入记录的逻辑地址。</returns>
    public ValueTask<LogicalAddress> PutAsync(TKey key, ReadOnlyMemory<byte> value,
        CancellationToken ct = default)
        => PutAsync(key, value, KvCommitPolicy.Committed, timeToLive: null, ct);

    /// <summary>写入 key→值字节 + 可选 TTL（过期即惰性读删；回收期强删）+ 完成语义档。</summary>
    /// <param name="key">键。</param>
    /// <param name="value">值字节。</param>
    /// <param name="policy">完成语义（Committed=组提交等待；其余不刷）。</param>
    /// <param name="timeToLive">TTL（过期惰性读删；null=无过期）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>写入记录的逻辑地址。</returns>
    public async ValueTask<LogicalAddress> PutAsync(TKey key, ReadOnlyMemory<byte> value,
        KvCommitPolicy policy = KvCommitPolicy.FireAndForget, TimeSpan? timeToLive = null,
        CancellationToken ct = default)
    {
        EnsureReady();
        var framed = KvValueFraming.Frame(value.Span, ExpiryTicks(timeToLive));
        var addr = await PutFramedAsync(key, framed, ct).ConfigureAwait(false);
        await ApplyCommitPolicyAsync(addr, policy, ct).ConfigureAwait(false);
        return addr;
    }

    /// <summary>同步写入（快路径——无溢出时纯内存操作）。无过期。</summary>
    /// <param name="key">键。</param>
    /// <param name="value">值字节。</param>
    /// <returns>写入记录的逻辑地址。</returns>
    public LogicalAddress Put(TKey key, ReadOnlySpan<byte> value)
    {
        EnsureReady();
        return PutFramed(key, KvValueFraming.Frame(value, ExpiryTicks((TimeSpan?)null)));
    }

    /// <summary>
    /// 同步写入 key→格式化值（写热路径——<b>单分配</b>帧化一次成型 + 同步链零 async 状态机，
    /// 与读侧 <see cref="TryGetFormatted(TKey, out TValue)"/> 同构）。
    /// <para>★ 快路径语义：内存档（索引可见即返回，零 fsync）；无 TTL 参数走异步版。</para>
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="value">待格式化写入的值。</param>
    /// <returns>写入记录的逻辑地址。</returns>
    public LogicalAddress PutFormatted(TKey key, TValue value)
    {
        EnsureReady();
        var framed = KvValueFraming.AllocateFrame(_formatter.GetSize(value), expiryTicks: 0);
        _formatter.Format(value, framed.AsSpan(KvValueFraming.HeaderSize));
        return PutFramed(key, framed);
    }

    /// <summary>内部同步落环入口（已帧化字节——主索引+范围索引双写换绑）。</summary>
    private LogicalAddress PutFramed(TKey key, byte[] framed)
    {
        var addr = _ring.Write(key, framed);
        BindIndex(key, addr, _ring.BeginAddress);
        _rangeIndex?.Insert(key, addr, _ring.BeginAddress);
        _watchHub.Publish(key, KvWatchEventKind.Put, addr);
        return addr;
    }

    /// <summary>内部统一落环入口（已帧化字节——主索引+范围索引双写换绑）。
    /// internal：会话层单分配封装路径直用（formatter 直填帧缓冲后落环）。</summary>
    /// <param name="key">键。</param>
    /// <param name="framed">已帧化的值字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>写入记录的逻辑地址。</returns>
    internal async ValueTask<LogicalAddress> PutFramedAsync(TKey key, byte[] framed, CancellationToken ct)
    {
        var addr = await _ring.WriteAsync(key, framed, ct).ConfigureAwait(false);
        BindIndex(key, addr, _ring.BeginAddress);
        _rangeIndex?.Insert(key, addr, _ring.BeginAddress);
        _watchHub.Publish(key, KvWatchEventKind.Put, addr);
        return addr;
    }

    /// <summary>TTL → 过期 ticks（null/Zero 处理：null=无过期；Zero=立即过期——边界语义显式）。
    /// ★ 经倒流守卫后的观测墙钟锚定——倒流期新写项的过期点不早于水位。CAS 产物同走本实例
    /// 助手（时钟缝口径优先于 KvValueFraming 静态助手——静态版仅无时钟上下文的会话面使用）。</summary>
    private long ExpiryTicks(TimeSpan? timeToLive)
        => timeToLive is { } ttl ? NowUtcTicks + ttl.Ticks : 0;

    // ═══════════════════════════════════════════════════════════════════
    // 读（点查——索引 O(1) → Ring 取值）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>点查 key → 值字节（未命中返回 false；命中时 destination 须 ≥ 值长度。TTL 过期 = 未命中——惰性读删）。</summary>
    /// <param name="key">键。</param>
    /// <param name="destination">读出目标缓冲（命中时须不小于值长度）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中且写入 destination 为 true；未命中/墓碑/过期为 false。</returns>
    /// <exception cref="ArgumentException">destination 长度小于值长度。</exception>
    public async ValueTask<bool> TryGetAsync(TKey key, Memory<byte> destination, CancellationToken ct = default)
    {
        var stored = await TryGetStoredAsync(key, ct).ConfigureAwait(false);
        if (stored is null) return false;
        var payload = KvValueFraming.Payload(stored);
        if (payload.Length > destination.Length)
            throw new ArgumentException($"destination 长度 {destination.Length} < 值长度 {payload.Length}");
        payload.CopyTo(destination);
        return true;
    }

    /// <summary>点查 key → 值字节副本（未命中返回 null。TTL 过期 = 未命中——惰性读删）。</summary>
    /// <param name="key">键。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中的值字节副本；未命中/墓碑/过期为 null。</returns>
    public async ValueTask<byte[]?> TryGetBytesAsync(TKey key, CancellationToken ct = default)
    {
        var stored = await TryGetStoredAsync(key, ct).ConfigureAwait(false);
        if (stored is null) return null;
        return KvValueFraming.Payload(stored);
    }

    /// <summary>点查存储态（已帧化字节）——过期（惰性读删）/墓碑/未命中统一返回 null。</summary>
    private async ValueTask<byte[]?> TryGetStoredAsync(TKey key, CancellationToken ct)
    {
        EnsureReady();
        var addr = Volatile.Read(ref _index).Find(key);
        if (addr == LogicalAddress.Empty) return null;

        var recordKey = await _ring.GetKeyAsync(addr, ct).ConfigureAwait(false);
        if (recordKey.IsTombstone) return null;

        var buf = new byte[recordKey.ValueLength];
        await _ring.GetValueAsync(addr, buf, ct).ConfigureAwait(false);
        if (!KvValueFraming.IsFramed(buf)) return null;   // 非 TierKv 帧 = 无效
        var framed = KvValueFraming.ExpiryTicks(buf);
        if (KvValueFraming.IsExpired(framed, NowUtcTicks)) return null;   // 惰性读删
        return buf;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 删除（墓碑）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>删除 key（墓碑写入 + 索引清槽；恢复重放跳过已删 key）。返回 true=删除成功。</summary>
    /// <param name="key">键。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>key 存在并删除为 true；key 不存在为 false。</returns>
    public async ValueTask<bool> DeleteAsync(TKey key, CancellationToken ct = default)
    {
        EnsureReady();
        var addr = Volatile.Read(ref _index).Find(key);
        if (addr == LogicalAddress.Empty) return false;

        var tombstoneAddr = await _ring.WriteTombstoneAsync(key, ct).ConfigureAwait(false);
        UnbindIndex(key);
        _rangeIndex?.Delete(key);
        _watchHub.Publish(key, KvWatchEventKind.Delete, tombstoneAddr);
        return true;
    }

    // ═════════════════════════════════════════════════════════════════
    // 条件写（A.2b CompareAndSwap——地址即版本的乐观锁直接体现）
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// 地址版 CAS（乐观锁——「地址即版本」的直接体现）：当前绑定地址 == <paramref name="expectedAddress"/>
    /// 才追加 <paramref name="value"/> 并换绑；否则返回 false + 当前绑定（零写入零事件）。
    /// <para>★ 语义锚点：<paramref name="expectedAddress"/> = <see cref="LogicalAddress.Invalid"/> 表示
    /// 「当前不存在」（防并发插入——与值版 expectedValue=null 同位）；绑定记录已过期（惰性判）视为不存在
    /// （返回 false + CurrentAddress=Invalid）。</para>
    /// <para>★ 原子性（文档明示）：换绑点 CAS×CAS 经 <see cref="_casGate"/> 串行——并发 CAS 恰一成功；
    /// CAS 与普通 Put/Delete 竞争仍是 last-writer-wins 换绑（CAS 不是全局写锁，防丢失保证仅对
    /// 其他 CAS 调用方成立——etcd txn 同语义）；raft 形态命令经 apply 单 worker 串行——天然全局原子。
    /// 成功 = Put 事件（写路径既有发布）。</para>
    /// <para>★ TTL（获取即租约形态——锁配方语义源）：<paramref name="timeToLive"/> 非 null 时产物带过期，
    /// 「比较 + 写入 + 挂过期」单一原子单元——expiryTicks 在 <see cref="_casGate"/> 临界区内、比较通过后
    /// 计算（锁获取 = <c>expectedAddress=Invalid</c> 的 NX + 租约；fencing token = NewAddress，地址即版本）。
    /// null = 无过期（与无 TTL 形态逐字节同）。</para>
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="expectedAddress">预期的当前绑定地址（Invalid = 预期不存在）。</param>
    /// <param name="value">新值字节。</param>
    /// <param name="policy">完成语义（Committed=组提交等待；其余不刷）。</param>
    /// <param name="timeToLive">TTL（过期惰性读删；null = 无过期）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>CAS 回执（换绑结果 + 当前/新绑定地址）。</returns>
    public async ValueTask<KvCasResult> CompareAndSwapAsync(TKey key, LogicalAddress expectedAddress,
        ReadOnlyMemory<byte> value, KvCommitPolicy policy = KvCommitPolicy.FireAndForget,
        TimeSpan? timeToLive = null, CancellationToken ct = default)
    {
        EnsureReady();
        await _casGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await ResolveAliveBindingAsync(key, ct).ConfigureAwait(false);
            var matches = expectedAddress.IsValid
                ? current.IsValid && current == expectedAddress
                : !current.IsValid;
            if (!matches)
                return new KvCasResult(false, current, LogicalAddress.Invalid);

            var newAddr = await PutFramedAsync(key,
                    KvValueFraming.Frame(value.Span, ExpiryTicks(timeToLive)), ct)
                .ConfigureAwait(false);
            await ApplyCommitPolicyAsync(newAddr, policy, ct).ConfigureAwait(false);
            return new KvCasResult(true, newAddr, newAddr);
        }
        finally
        {
            _casGate.Release();
        }
    }

    /// <summary>
    /// 值版便捷 CAS（读折叠比较字节）：当前值字节 == <paramref name="expectedValue"/> 才追加并换绑；
    /// <paramref name="expectedValue"/> = null 语义 = 「当前不存在」（防并发插入——恰一成功语义同地址版）。
    /// <para>★ TTL 语义同地址版：<paramref name="timeToLive"/> 非 null 时「比较 + 写入 + 挂过期」
    /// 单一原子单元（临界区内比较通过后计算 expiry）；null = 无过期。</para>
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="expectedValue">预期的当前值字节（null = 预期不存在）。</param>
    /// <param name="value">新值字节。</param>
    /// <param name="policy">完成语义（Committed=组提交等待；其余不刷）。</param>
    /// <param name="timeToLive">TTL（过期惰性读删；null = 无过期）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>CAS 回执（换绑结果 + 当前/新绑定地址）。</returns>
    public async ValueTask<KvCasResult> CompareAndSwapAsync(TKey key, byte[]? expectedValue,
        ReadOnlyMemory<byte> value, KvCommitPolicy policy = KvCommitPolicy.FireAndForget,
        TimeSpan? timeToLive = null, CancellationToken ct = default)
    {
        EnsureReady();
        await _casGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (current, payload) = await ResolveAliveValueAsync(key, ct).ConfigureAwait(false);
            var matches = expectedValue is null
                ? !current.IsValid
                : current.IsValid && payload is { } bytes && bytes.AsSpan().SequenceEqual(expectedValue);
            if (!matches)
                return new KvCasResult(false, current, LogicalAddress.Invalid);

            var newAddr = await PutFramedAsync(key,
                    KvValueFraming.Frame(value.Span, ExpiryTicks(timeToLive)), ct)
                .ConfigureAwait(false);
            await ApplyCommitPolicyAsync(newAddr, policy, ct).ConfigureAwait(false);
            return new KvCasResult(true, newAddr, newAddr);
        }
        finally
        {
            _casGate.Release();
        }
    }

    /// <summary>解析 key 的存活绑定（CAS 预期比较源）：未绑定/墓碑/过期（惰性判）统一 Invalid——
    /// 「不存在」语义与点查一致。调用方须持 <see cref="_casGate"/>（compare→换绑窗口串行化）。</summary>
    /// <param name="key">键。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>存活绑定地址（Invalid = 不存在）。</returns>
    private async ValueTask<LogicalAddress> ResolveAliveBindingAsync(TKey key, CancellationToken ct)
    {
        var bound = Volatile.Read(ref _index).Find(key);
        if (bound == LogicalAddress.Empty) return LogicalAddress.Invalid;

        var record = await _ring.GetKeyAsync(bound, ct).ConfigureAwait(false);
        if (record.IsTombstone || record.IsMeta) return LogicalAddress.Invalid;   // 防御（Delete 即清槽）
        var buf = new byte[record.ValueLength];
        await _ring.GetValueAsync(bound, buf, ct).ConfigureAwait(false);
        if (!KvValueFraming.IsFramed(buf)
            || KvValueFraming.IsExpired(KvValueFraming.ExpiryTicks(buf), NowUtcTicks))
        {
            return LogicalAddress.Invalid;   // 过期惰性读删——绑定视为不存在
        }
        return bound;
    }

    /// <summary>解析 key 的存活绑定 + 值字节（值版 CAS 的比较源——同一存活判定一次读出）。</summary>
    /// <param name="key">键。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>(存活绑定地址, 值字节副本)（Invalid, null = 不存在/过期）。</returns>
    private async ValueTask<(LogicalAddress Address, byte[]? Payload)> ResolveAliveValueAsync(
        TKey key, CancellationToken ct)
    {
        var bound = Volatile.Read(ref _index).Find(key);
        if (bound == LogicalAddress.Empty) return (LogicalAddress.Invalid, null);

        var record = await _ring.GetKeyAsync(bound, ct).ConfigureAwait(false);
        if (record.IsTombstone || record.IsMeta) return (LogicalAddress.Invalid, null);
        var buf = new byte[record.ValueLength];
        await _ring.GetValueAsync(bound, buf, ct).ConfigureAwait(false);
        if (!KvValueFraming.IsFramed(buf)
            || KvValueFraming.IsExpired(KvValueFraming.ExpiryTicks(buf), NowUtcTicks))
        {
            return (LogicalAddress.Invalid, null);
        }
        return (bound, KvValueFraming.Payload(buf));
    }

    // ═══════════════════════════════════════════════════════════════════
    // 类型化值面（[KvStore] 封闭形态承载——formatter 翻译直取直存）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>写入 key→格式化值 + 可选 TTL + 完成语义档。
    /// ★ 单分配帧化：formatter 直填帧缓冲 payload 区（消除「formatter 缓冲 + Frame 再拷」双分配双拷贝）。</summary>
    /// <param name="key">键。</param>
    /// <param name="value">待格式化写入的值。</param>
    /// <param name="policy">完成语义（Committed=组提交等待；其余不刷）。</param>
    /// <param name="timeToLive">TTL（过期惰性读删；null=无过期）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>写入记录的逻辑地址。</returns>
    public async ValueTask<LogicalAddress> PutFormattedAsync(TKey key, TValue value,
        KvCommitPolicy policy = KvCommitPolicy.FireAndForget, TimeSpan? timeToLive = null,
        CancellationToken ct = default)
    {
        EnsureReady();
        var framed = KvValueFraming.AllocateFrame(_formatter.GetSize(value), ExpiryTicks(timeToLive));
        _formatter.Format(value, framed.AsSpan(KvValueFraming.HeaderSize));
        var addr = await PutFramedAsync(key, framed, ct).ConfigureAwait(false);
        await ApplyCommitPolicyAsync(addr, policy, ct).ConfigureAwait(false);
        return addr;
    }


    /// <summary>点查 key → 格式化值（Parse 逆翻译；未命中返回 false）。</summary>
    /// <param name="key">键。</param>
    /// <returns>命中时 Found=true 且 Value 为逆翻译值；未命中 Found=false。</returns>
    public async ValueTask<(bool Found, TValue Value)> TryGetFormattedAsync(TKey key)
    {
        var bytes = await TryGetBytesAsync(key).ConfigureAwait(false);
        if (bytes is null)
            return (false, default!);
        return (true, _formatter.Parse(bytes));
    }

    // ═══════════════════════════════════════════════════════════════════
    // 同步点查热路径（页池直读零 async——读放大敏感场景；冷区由调用方退化异步版）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 同步点查（热路径——addr 物理事实一跳直读）：索引 Find → <see cref="RingBase{TKey}.GetValueSpan"/>
    /// 单次页定位+header 解析+value span（零拷贝直读）→ 帧校验/过期判定 span 直做 → payload 拷入
    /// <paramref name="destination"/>。
    /// <para>★ 一跳零分配零 async：地址是物理事实稳定有效——省去 GetKey/GetValue 两跳的重复
    /// header 解析；数据在页池热区时为纯指针读，冷区同步回源（引擎同步读）。</para>
    /// <para>★ 未命中/墓碑/过期/帧无效返回 false；<paramref name="destination"/> 须 ≥ payload 长度。</para>
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="destination">读出目标缓冲（命中时须不小于 payload 长度）。</param>
    /// <returns>命中且拷入 destination 为 true；未命中/墓碑/过期/帧无效为 false。</returns>
    /// <exception cref="ArgumentException">destination 长度小于 payload 长度。</exception>
    public bool TryGet(TKey key, Span<byte> destination)
    {
        var addr = Volatile.Read(ref _index).Find(key);
        if (addr == LogicalAddress.Empty) return false;

        var frame = _ring.GetValueSpan(addr);   // 内容自愈读内聚（判据+校验+设备回退）——零保护零感知
        if (!KvValueFraming.IsFramed(frame)) return false;   // 帧无效/墓碑（空 payload）
        if (KvValueFraming.IsExpired(KvValueFraming.ExpiryTicks(frame), NowUtcTicks)) return false;

        var payload = frame.Slice(KvValueFraming.HeaderSize);
        if (payload.Length > destination.Length)
            throw new ArgumentException($"destination 长度 {destination.Length} < payload 长度 {payload.Length}");
        payload.CopyTo(destination);
        return true;
    }

    /// <summary>同步点查（类型化面——formatter.Parse 逆翻译）。未命中/墓碑/过期返回 false。</summary>
    /// <param name="key">键。</param>
    /// <param name="value">命中时为逆翻译值；未命中为 default。</param>
    /// <returns>命中为 true；未命中/墓碑/过期为 false。</returns>
    public bool TryGetFormatted(TKey key, out TValue value)
    {
        var addr = Volatile.Read(ref _index).Find(key);
        if (addr == LogicalAddress.Empty) { value = default!; return false; }

        var frame = _ring.GetValueSpan(addr);   // 内容自愈读内聚（判据+校验+设备回退）——零保护零感知
        if (!KvValueFraming.IsFramed(frame) ||
            KvValueFraming.IsExpired(KvValueFraming.ExpiryTicks(frame), NowUtcTicks))
        {
            value = default!;
            return false;
        }
        value = _formatter.Parse(frame.Slice(KvValueFraming.HeaderSize));
        return true;
    }

    // ═════════════════════════════════════════════════════════════════
    // 地址直读（A.2c TryGetAt——地址一等公民的公开读口，免索引）
    // ═════════════════════════════════════════════════════════════════

    /// <summary>
    /// 凭地址直读（同步快路径——写返地址后免索引点查；热区纯内存零分配）。
    /// <para>★ 语义：读该地址记录当前帧；无效地址/已回收（&lt; BeginAddress）/越尾/墓碑/过期/帧无效
    /// 统一 false（数据事实语义——不抛，与 WatchAsync 续传越线的契约错区分）。
    /// 溢出跟随（记录带溢出指针则经溢出引擎读回）；不走索引、不发事件、冷热透明。
    /// 会话写集对它不可见（全局视图——raft 形态全组同址同值）。</para>
    /// </summary>
    /// <param name="address">记录的逻辑地址（Put 返回值）。</param>
    /// <param name="destination">读出目标缓冲（命中时须不小于值长度）。</param>
    /// <returns>命中且拷入 destination 为 true；守卫未命中为 false。</returns>
    /// <exception cref="ArgumentException">destination 长度小于值长度。</exception>
    public bool TryGetAt(LogicalAddress address, Span<byte> destination)
    {
        EnsureReady();
        if (!IsReadableAddress(address)) return false;

        var frame = _ring.GetValueSpan(address);   // 内容自愈读——垃圾地址（页复用/撕裂）空 span = 无效
        if (frame.IsEmpty || !KvValueFraming.IsFramed(frame)) return false;   // 墓碑（空 payload）/非本产品帧
        if (KvValueFraming.IsExpired(KvValueFraming.ExpiryTicks(frame), NowUtcTicks)) return false;

        var payload = frame.Slice(KvValueFraming.HeaderSize);
        if (payload.Length > destination.Length)
            throw new ArgumentException($"destination 长度 {destination.Length} < 值长度 {payload.Length}");
        payload.CopyTo(destination);
        return true;
    }

    /// <summary>凭地址直读值字节副本（语义守卫同 <see cref="TryGetAt(LogicalAddress, Span{byte})"/>）。</summary>
    /// <param name="address">记录的逻辑地址（Put 返回值）。</param>
    /// <param name="ct">取消令牌（冷区回源/溢出读途中响应取消）。</param>
    /// <returns>(Found, Value)：命中时 Value 为值字节副本；守卫未命中 Found=false、Value=null。</returns>
    public async ValueTask<(bool Found, byte[]? Value)> TryGetBytesAtAsync(LogicalAddress address,
        CancellationToken ct = default)
    {
        EnsureReady();
        var stored = await TryGetStoredAtAsync(address, ct).ConfigureAwait(false);
        return stored is null ? (false, null) : (true, KvValueFraming.Payload(stored));
    }

    /// <summary>凭地址直读到调用方缓冲（语义守卫同 <see cref="TryGetAt(LogicalAddress, Span{byte})"/>）。</summary>
    /// <param name="address">记录的逻辑地址（Put 返回值）。</param>
    /// <param name="destination">读出目标缓冲（命中时须不小于值长度）。</param>
    /// <param name="ct">取消令牌（冷区回源/溢出读途中响应取消）。</param>
    /// <returns>命中且写入 destination 为 true；守卫未命中为 false。</returns>
    /// <exception cref="ArgumentException">destination 长度小于值长度。</exception>
    public async ValueTask<bool> TryGetAt(LogicalAddress address, Memory<byte> destination,
        CancellationToken ct = default)
    {
        EnsureReady();
        var stored = await TryGetStoredAtAsync(address, ct).ConfigureAwait(false);
        if (stored is null) return false;
        var payload = KvValueFraming.Payload(stored);
        if (payload.Length > destination.Length)
            throw new ArgumentException($"destination 长度 {destination.Length} < 值长度 {payload.Length}");
        payload.CopyTo(destination);
        return true;
    }

    /// <summary>地址可读性守卫（数据事实语义——全部 false 不抛）：无效哨兵/已回收（&lt; BeginAddress，
    /// 查询语义返回不存在）/越尾（≥ TailAddress = 尚未写入的位）。</summary>
    /// <param name="address">待校验地址。</param>
    /// <returns>落入 [BeginAddress, TailAddress) 且形式有效为 true。</returns>
    private bool IsReadableAddress(LogicalAddress address)
        => address.IsValid && address >= _ring.BeginAddress && address < _ring.TailAddress;

    /// <summary>按地址读存储帧（已帧化字节——async 路径共享核心）：守卫 + 墓碑/meta/帧校验/惰性过期
    /// 统一 null。伪造地址（CRC 无效内容）按数据事实读不出 = false 不抛（GetKeyAsync 的 fail-fast
    /// InvalidOperationException 归一为 null——地址直读面是非信任入口，与索引路径的损坏 fail-fast 分野）。</summary>
    /// <param name="address">待读地址。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完整值帧字节；不可读为 null。</returns>
    private async ValueTask<byte[]?> TryGetStoredAtAsync(LogicalAddress address, CancellationToken ct)
    {
        if (!IsReadableAddress(address)) return null;
        try
        {
            var record = await _ring.GetKeyAsync(address, ct).ConfigureAwait(false);
            if (record.IsTombstone || record.IsMeta) return null;
            var buf = new byte[record.ValueLength];
            await _ring.GetValueAsync(address, buf, ct).ConfigureAwait(false);
            if (!KvValueFraming.IsFramed(buf)) return null;   // 非 TierKv 帧 = 无效
            if (KvValueFraming.IsExpired(KvValueFraming.ExpiryTicks(buf), NowUtcTicks)) return null;
            return buf;
        }
        catch (InvalidOperationException)
        {
            return null;   // 伪造/竞争失效地址（校验失败无有效内容）——读不出 = 数据事实 false
        }
    }
}
