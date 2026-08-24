using System.Collections.Concurrent;

namespace TC.Tier.Runtime.AddressSpace.Leases;

/// <summary>
/// lease 工厂——创建 + 可选池化 + 可选诊断。
/// <para>★ Default：每次 new + 无诊断（生产推荐——零诊断开销）。</para>
/// <para>★ WithDiagnostics：每次 new + 有诊断（测试/调试——GetActiveLeases/ForceRelease 可用）。</para>
/// <para>★ Pooled：对象池复用（高频 Append/Write 零分配）。</para>
/// <para>★ 使用方选择模式——不改变 lease 协议语义。</para>
/// <para>★ 类型化 lease 协议（2026-08-16 复拆）：每个操作类型独立 lease 类 + 独立池——
///   池按类型分立（Append 高频、ReclaimTail 低频，互不挤占），工厂方法返回各自类型。</para>
/// </summary>
public abstract class LeaseFactory
{
    /// <summary>
    /// ★ 是否启用诊断跟踪（RegisterLease/UnregisterLease/GetActiveLeases/ForceRelease）。
    /// <para>false（生产）：lease 创建/销毁零诊断开销，省 ~250ns + ~168B/op。</para>
    /// <para>true（测试/调试）：lease 注册到诊断表，泄漏检测可用。</para>
    /// </summary>
    internal abstract bool EnableDiagnostics { get; }

    /// <summary>
    /// 默认工厂——每次 new + 无诊断（生产推荐）。
    /// </summary>
    public static LeaseFactory Default { get; } = new NewFactory(enableDiagnostics: false);

    /// <summary>
    /// 诊断工厂——每次 new + 有诊断（测试/调试——GetActiveLeases/ForceRelease 可用）。
    /// </summary>
    public static LeaseFactory WithDiagnostics { get; } = new NewFactory(enableDiagnostics: true);

    /// <summary>
    /// 池化工厂——对象池复用（高频 Append/Write 零分配）+ 无诊断。
    /// </summary>
    public static LeaseFactory Pooled { get; } = new PooledFactory(enableDiagnostics: false);

    /// <summary>
    /// 池化工厂——对象池复用（可指定池容量上限，防内存膨胀）。
    /// </summary>
    public static LeaseFactory PooledWithLimit(int maxPoolSize = 64) => new PooledFactory(false, maxPoolSize);
    // ═══ 创建入口（类型化返回——命名方法区分协议，不用 bool 选择子）═══

    internal abstract AppendLease NewAppend(ILeaseSource source, LogicalAddress start, LogicalAddress end,
        ILogger? logger = null);

    internal abstract WriteLease NewWriteRange(ILeaseSource source, LogicalAddress start, LogicalAddress end,
        ILogger? logger = null);

    internal abstract ReclaimLease NewReclaim(ILeaseSource source, LogicalAddress start, LogicalAddress end,
        ILogger? logger = null);

    internal abstract ReclaimHeadLease NewReclaimHead(ILeaseSource source, LogicalAddress start, LogicalAddress end,
        ILogger? logger = null);

    internal abstract ReclaimTailLease NewReclaimTail(ILeaseSource source, LogicalAddress start, LogicalAddress end,
        ILogger? logger = null);

    internal abstract CompactLease NewCompact(ILeaseSource source, LogicalAddress start, LogicalAddress end,
        ILogger? logger = null);

    /// <summary>归还 lease（池化模式按类型回各自池，new 模式丢弃）。</summary>
    internal abstract void Return(LeaseBase lease);

    /// <summary>归还 CompactLease（独立协议，池化模式回池）。</summary>
    internal abstract void ReturnCompact(CompactLease lease);

    /// <summary>
    /// 默认工厂——每次 new（简单，GC 管理）。
    /// </summary>
    private sealed class NewFactory(bool enableDiagnostics) : LeaseFactory
    {
        internal override bool EnableDiagnostics => enableDiagnostics;
        // ★ RegisterLease 已移到 LeaseBase.Reset 里统一调——工厂只负责造对象，不重复注册。
        internal override AppendLease NewAppend(ILeaseSource source, LogicalAddress start, LogicalAddress end,
            ILogger? logger = null)
            => new AppendLease(source, start, end, logger);

        internal override WriteLease NewWriteRange(ILeaseSource source, LogicalAddress start, LogicalAddress end,
            ILogger? logger = null)
            => new WriteLease(source, start, end, logger);

        internal override ReclaimLease NewReclaim(ILeaseSource source, LogicalAddress start, LogicalAddress end,
            ILogger? logger = null)
            => new ReclaimLease(source, start, end, logger);

        internal override ReclaimHeadLease NewReclaimHead(ILeaseSource source, LogicalAddress start, LogicalAddress end,
            ILogger? logger = null)
            => new ReclaimHeadLease(source, start, end, logger);

        internal override ReclaimTailLease NewReclaimTail(ILeaseSource source, LogicalAddress start, LogicalAddress end,
            ILogger? logger = null)
            => new ReclaimTailLease(source, start, end, logger);

        internal override CompactLease NewCompact(ILeaseSource source, LogicalAddress start, LogicalAddress end,
            ILogger? logger = null)
            => new CompactLease(source, start, end, logger);

        internal override void Return(LeaseBase lease)
        {
            /* 丢弃，GC 回收 */
        }

        internal override void ReturnCompact(CompactLease lease)
        {
            /* 丢弃，GC 回收 */
        }
    }

    // ═══ Pooled：对象池复用（五池分立——按类型，互不挤占）═══

    /// <summary>
    /// 池化工厂——对象池复用（高频 Append/Write 零分配）。
    /// </summary>
    /// <param name="maxPoolSize">池容量上限（防内存膨胀）</param>
    private sealed class PooledFactory(bool enableDiagnostics, int maxPoolSize = 64) : LeaseFactory
    {
        internal override bool EnableDiagnostics => enableDiagnostics;

        private readonly ConcurrentQueue<AppendLease> _appendPool = new();
        private readonly ConcurrentQueue<WriteLease> _writePool = new();
        private readonly ConcurrentQueue<ReclaimLease> _reclaimPool = new();
        private readonly ConcurrentQueue<ReclaimHeadLease> _reclaimHeadPool = new();
        private readonly ConcurrentQueue<ReclaimTailLease> _reclaimTailPool = new();

        internal override AppendLease NewAppend(ILeaseSource source, LogicalAddress start, LogicalAddress end,
            ILogger? logger = null)
        {
            if (_appendPool.TryDequeue(out var leased))
            {
                leased.Reset(source, start, end, ExtentStateCode.AppendLeased, logger);
                return leased;
            }
            return new AppendLease(source, start, end, logger);
        }

        internal override WriteLease NewWriteRange(ILeaseSource source, LogicalAddress start, LogicalAddress end,
            ILogger? logger = null)
        {
            if (_writePool.TryDequeue(out var leased))
            {
                leased.Reset(source, start, end, ExtentStateCode.WriteLeased, logger);
                return leased;
            }
            return new WriteLease(source, start, end, logger);
        }

        internal override ReclaimLease NewReclaim(ILeaseSource source, LogicalAddress start, LogicalAddress end,
            ILogger? logger = null)
        {
            if (_reclaimPool.TryDequeue(out var leased))
            {
                leased.Reset(source, start, end, ExtentStateCode.ReclaimLeased, logger);
                return leased;
            }
            return new ReclaimLease(source, start, end, logger);
        }

        internal override ReclaimHeadLease NewReclaimHead(ILeaseSource source, LogicalAddress start, LogicalAddress end,
            ILogger? logger = null)
        {
            if (_reclaimHeadPool.TryDequeue(out var leased))
            {
                leased.Reset(source, start, end, ExtentStateCode.ReclaimLeased, logger);
                return leased;
            }
            return new ReclaimHeadLease(source, start, end, logger);
        }

        internal override ReclaimTailLease NewReclaimTail(ILeaseSource source, LogicalAddress start, LogicalAddress end,
            ILogger? logger = null)
        {
            if (_reclaimTailPool.TryDequeue(out var leased))
            {
                leased.Reset(source, start, end, ExtentStateCode.ReclaimLeased, logger);
                return leased;
            }
            return new ReclaimTailLease(source, start, end, logger);
        }

        internal override CompactLease NewCompact(ILeaseSource source, LogicalAddress start, LogicalAddress end,
            ILogger? logger = null)
        {
            // CompactLease 独立池化（kind 特有 CompactChunk 结构）
            if (_compactPool.TryDequeue(out var leased))
            {
                leased.Reset(source, start, end, logger);
                return leased;
            }

            return new CompactLease(source, start, end, logger);
        }

        private readonly ConcurrentQueue<CompactLease> _compactPool = new();

        internal override void Return(LeaseBase lease)
        {
            // 归还前校验——必须已完成 Commit/Rollback（_state != Active）；按类型回各自池
            if (lease.State == LeaseState.Active) return;
            switch (lease)
            {
                case AppendLease l when _appendPool.Count < maxPoolSize:
                    _appendPool.Enqueue(l);
                    break;
                case WriteLease l when _writePool.Count < maxPoolSize:
                    _writePool.Enqueue(l);
                    break;
                case ReclaimLease l when _reclaimPool.Count < maxPoolSize:
                    _reclaimPool.Enqueue(l);
                    break;
                case ReclaimHeadLease l when _reclaimHeadPool.Count < maxPoolSize:
                    _reclaimHeadPool.Enqueue(l);
                    break;
                case ReclaimTailLease l when _reclaimTailPool.Count < maxPoolSize:
                    _reclaimTailPool.Enqueue(l);
                    break;
            }
        }

        internal override void ReturnCompact(CompactLease lease)
        {
            if (lease.State == LeaseState.Active) return;
            if (_compactPool.Count < maxPoolSize)
                _compactPool.Enqueue(lease);
        }
    }
}
