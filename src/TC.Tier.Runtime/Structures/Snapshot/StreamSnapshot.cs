namespace TC.Tier.Runtime.Structures.Snapshot;

/// <summary>
/// StreamSnapshot——GB/TB 大数据流的流式帧实现（原 StreamBlockBlob 的 LifecycleBase 化继任者）。
/// <para>帧格式：[Header 14B][Data][Footer 28B（Magic+TotalLength+EntryCount+Crc64）]；
/// 多帧支持（Complete 后 ResetFrame 可再开新帧）。帧写入器 CRC64 增量流式累积（边写边算，不驻内存整块）。</para>
/// <para>★ 生命周期：new + Initialize()（后台恢复）+ WaitForReady()。详见 src/TC.Tier.Core/docs/lifecycle.md。</para>
/// </summary>
public sealed partial class StreamSnapshot : SnapshotBase
{
    private readonly StreamSnapshotSettings _settings;

    // === 帧 checkpoint（逻辑↔物理映射；逻辑地址不连续预留时靠它换算）===
    private readonly List<(LogicalAddress Logical, LogicalAddress Physical)> _frameCheckpoints = new();
    private readonly object _checkpointLock = new();

    /// <summary>
    /// 初始化一个新的<see cref="StreamSnapshot"/>实例。
    /// </summary>
    /// <param name="fileSystem">组合根文件系统（TierFs 构造的 IFileSystem）。</param>
    /// <param name="settings">流式快照设置。</param>
    /// <param name="recovery">可选的恢复算法实例。</param>
    /// <param name="metaPolicyFactory">可选的 meta 策略工厂。</param>
    /// <param name="metaTransport">可选的 meta 传输（Transport 模式用）。</param>
    public StreamSnapshot(
        IFileSystem fileSystem,
        StreamSnapshotSettings settings,
        IRecovery<SnapshotRecoveryHints>? recovery = null,
        MetaPolicyFactory<SnapshotMetaHeader, SnapshotMetaPayload>? metaPolicyFactory = null,
        IMetaTransport? metaTransport = null)
        : base(new StreamFrameCodec(), fileSystem, settings, recovery, metaPolicyFactory, metaTransport)
    {
        _settings = settings;
    }


    // ════════════════════════════════════════════════════════════
    // === 帧写入器（主用法：OpenWrite → WriteAsync × N → CompleteAsync）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 打开帧写入器：WriteAsync(data) × N（自动 EntryCount+1）→ CompleteAsync 写帧尾。
    /// 首次写自动加帧头；Complete 写帧尾 + flush；只 using 不 Complete 也会自动闭环。
    /// </summary>
    public StreamFrameWriter OpenWrite()
    {
        var physStart = _physicalWriteAddress;
        var logicalStart = _writeAddress;
        RecordCheckpoint(logicalStart, physStart);
        var session = OpenWriteSession(physStart);
        // ★ flush 完成回调：双水位分别推进——逻辑尾按 logicalBytes（非对齐），物理尾按 alignedBytes
        session.OnFlushed += (_, logicalBytes, alignedBytes) =>
        {
            _writeAddress = _engine.CalculationAddress(_writeAddress, logicalBytes);
            _physicalWriteAddress = _engine.CalculationAddress(_physicalWriteAddress, alignedBytes);
        };
        return new StreamFrameWriter(session);
    }

    /// <summary>指定逻辑区间打开帧写入器（物理地址续接，logical 不推进）。</summary>
    public StreamFrameWriter OpenWriteRange(LogicalAddress start, LogicalAddress end)
    {
        if (end.CompareTo(start) <= 0) throw new ArgumentException("end must be > start");
        var physStart = _physicalWriteAddress;
        RecordCheckpoint(start, physStart);
        var session = OpenWriteSession(physStart);
        session.OnFlushed += (_, _, alignedBytes) =>
        {
            _physicalWriteAddress = _engine.CalculationAddress(_physicalWriteAddress, alignedBytes);
        };
        return new StreamFrameWriter(session);
    }

    // ════════════════════════════════════════════════════════════
    // === 帧读取器 ===
    // ════════════════════════════════════════════════════════════

    /// <summary>打开帧读取器（从截断点读到写尾）：ReadDataAsync 读 data 至 EOF（footer 校验 CRC64）。</summary>
    public StreamFrameReader OpenRead()
    {
        var start = _truncatedAddress;
        var end = _writeAddress;
        var physStart = AlignDownAddress(LogicalToPhysical(start));
        var physEnd = _physicalWriteAddress;
        var session = OpenReadSession(start, end, physStart, physEnd);
        return new StreamFrameReader(session, _engine.GetDistance(start, end));
    }

    /// <summary>指定逻辑区间打开帧读取器。</summary>
    public StreamFrameReader OpenReadRange(LogicalAddress start, LogicalAddress end)
    {
        var physStart = AlignDownAddress(LogicalToPhysical(start));
        var physEnd = AlignUpAddress(LogicalToPhysical(end));
        var session = OpenReadSession(start, end, physStart, physEnd);
        return new StreamFrameReader(session, _engine.GetDistance(start, end));
    }

    // ════════════════════════════════════════════════════════════
    // === 辅助（checkpoint 映射 + 对齐）===
    // ════════════════════════════════════════════════════════════

    private void RecordCheckpoint(LogicalAddress logical, LogicalAddress physical)
    {
        lock (_checkpointLock) { _frameCheckpoints.Add((logical, physical)); }
    }

    /// <summary>
    /// 逻辑地址 → 物理地址（帧 checkpoint 二分 + 偏移叠加；全部正向推算）。
    /// </summary>
    private LogicalAddress LogicalToPhysical(LogicalAddress logical)
    {
        lock (_checkpointLock)
        {
            var cps = _frameCheckpoints;
            int n = cps.Count;
            if (n == 0) return logical;

            // 二分：最后一个 logical <= 目标的 checkpoint（列表按 logical 单调追加）
            int lo = 0, hi = n - 1;
            LogicalAddress bestLogical = LogicalAddress.Empty, bestPhysical = LogicalAddress.Empty;
            bool found = false;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (cps[mid].Logical.CompareTo(logical) <= 0)
                {
                    bestLogical = cps[mid].Logical;
                    bestPhysical = cps[mid].Physical;
                    found = true;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            if (!found) return logical;
            long delta = _engine.GetDistance(bestLogical, logical);
            return _engine.CalculationAddress(bestPhysical, delta);
        }
    }

    /// <summary>地址向扇区下对齐（RetreatFrom 正向等价）。</summary>
    private LogicalAddress AlignDownAddress(LogicalAddress addr)
    {
        long off = addr.Offset;
        long aligned = SectorAlignment.AlignDown(off, SectorSize);
        return RetreatFrom(addr, off - aligned);
    }

    /// <summary>地址向扇区上对齐。</summary>
    private LogicalAddress AlignUpAddress(LogicalAddress addr)
        => _engine.CalculationAddress(addr, addr.Offset.AlignUp(SectorSize) - addr.Offset);
}
