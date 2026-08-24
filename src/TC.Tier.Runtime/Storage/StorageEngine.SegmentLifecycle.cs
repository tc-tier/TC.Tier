namespace TC.Tier.Runtime.Storage;


internal sealed partial class StorageEngine
{
    /// <summary>
    /// 物理建段诊断日志（internal——N≥2 压测断言同一 segId 恰好一次 <see cref="CreateSegmentPhysical"/>）。
    /// <para>★ 随段创建低频追加，无淘汰（进程内诊断专用，不参与生产逻辑）。</para>
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<int> _physicalBuildLog = new();

    /// <summary>物理建段 segId 日志快照（诊断/测试用——重复项 = 双建窗口未关死）。</summary>
    internal IReadOnlyCollection<int> PhysicalBuildLog => _physicalBuildLog;
    /// <summary>
    /// 物理建段（IO 层资产操作）——造 IFileHandle + (按 PreallocateFile) 预分配 + 缓存 + 容量计数 + meta。
    /// <para>★ 两个调用方共享（幂等基元）：① worker 正式建段（<see cref="CreateSegmentPhysical"/>）；
    ///   ② IO 层预备池预建（<c>StorageEngine.SegmentPool.cs</c> 的 lookahead）——
    ///   物理建段是 IO 层的事，段表只发通知（OnSegmentCreate/OnSegmentFull），池攒 N 个现成段、
    ///   随取随补、Dispose 毁余量（架构约定 2026-08-14）。</para>
    /// <para>★ 调用契约（2026-08-16）：两调用方经 build-gate single-flight 互斥进入
    ///   （<see cref="EnsureSegmentPhysicalAsync"/> / <c>PreCreateSegmentPhysical</c>，均在
    ///   StorageEngine.SegmentPool.cs）——本方法自身不再防重，直接调用者之外勿绕过 gate。</para>
    /// </summary>
    /// <param name="segId">段号。</param>
    /// <param name="growthLimit">段生长上限（逻辑段大小，恒为真实值）。</param>
    private void CreateSegmentPhysical(int segId, long growthLimit)
    {
        // ★ single-flight 诊断日志（internal——N≥2 压测断言同一 segId 恰好一次物理构建）
        _physicalBuildLog.Enqueue(segId);
        // ★ 建段 = fs.CreateFile（预分配 + 初始元组 FileExtra 一次原子落位——创建/句柄解耦，D-4；
        //   预分配按 PreallocateFile：true=段大小立即分配满，false=稀疏按需增长）。
        //   realSize/元组记段大小（growthLimit 恒为真实值——元组/段表都依赖它）。
        CreateSegmentFile(segId, growthLimit);
    }

    /// <summary>
    /// 建段失败清理（IO 层物理侧，A7）——段表失败回调（Broken 终态）之外，IO 层负责物理资产清尸：
    /// 句柄释放 + meta 墓碑排空 + best-effort 删半建文件。
    /// <para>★ 残留后果：半建文件留在盘上，重开扫盘会把它当存在段装配——与 Broken 判定矛盾
    ///   （且占用物理空间）。清理失败不抛（失败路径再抛 = worker 毒化）——残留由重开扫盘按物理
    ///   真相自愈（文件在则按文件装配，语义安全）。</para>
    /// <para>★ 顺序对齐 <see cref="DeleteSegment"/> 契约：先 ReleaseSegmentHandles（物理删前释放占用），
    ///   再 meta Remove+Flush（排空在途 flush 的段文件句柄，防 Windows 共享违例），最后物理删。</para>
    /// <para>★ 不做容量计数回退——失败点可能在计数 Add（<see cref="CreateSegmentPhysical"/> 末尾）之前，
    ///   误退会把计数扣负。保守方向：子类在 base 后失败时逻辑大小多计一份 growthLimit；
    ///   Capacity 检查用的 <c>_totalAllocatedSize</c> 仅 Preallocate 模式受影响且方向保守（偏小可用）。</para>
    /// </summary>
    private void CleanupFailedSegmentBuild(int segId)
    {
        try
        {
            ReleaseSegmentHandles(segId);
            // ★ FileExtra 随宿主文件消亡（§3.6 删除契约）——删文件即删元组，无墓碑写。
            DeleteSegmentPhysical(segId);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "建段失败清理 seg#{SegId} 未完全成功（半建文件可能残留，重开扫盘自愈）", segId);
        }
    }

    /// <summary>
    /// 编码段区间表摘要（VII-3 reopen extent 级保真）——终态记录 RLE，按 FileExtra 预算条目递减收缩
    /// （D-13：装到预算为止；全装不下 → 空 = reopen 降级粗粒度，与旧行为等价）。
    /// <para>★ 调用点：段满元组写 / Dispose 尾段补写。失败/无终态 → 空（摘要绝不致命）。</para>
    /// </summary>
    private byte[] EncodeExtentSummary(int segId)
    {
        const int summaryBudget = IFileSystem.MaxFileExtraBytes - 64;   // 头+CRC 余量
        try
        {
            var records = new List<AddressSpace.ExtentRecord>(8);
            // ★ ExtentReader 枚举期间持 extent SpinLock，Dispose 才释放——必须 using
            //   （漏掉 = 锁泄漏，下一个区间操作永久自旋，实测楔死）。
            using var reader = _segmentTable.SnapshotSegmentExtents(segId);
            while (reader.MoveNext())
            {
                var (start, end, state, sparse) = reader.Current;
                records.Add(new AddressSpace.ExtentRecord(start, end, state, sparse));
            }
            if (records.Count == 0) return Array.Empty<byte>();
            // 全量装得下直接编；超预算按条目二分收缩（前 N 条保真）
            if (AddressSpace.ExtentSummaryCodec.Encode(records) is { } full && full.Length <= summaryBudget)
                return full;
            int lo = 1, hi = records.Count - 1, best = 0;
            while (lo <= hi)
            {
                var mid = (lo + hi) / 2;
                if (AddressSpace.ExtentSummaryCodec.Encode(records.GetRange(0, mid)) is { } enc
                    && enc.Length <= summaryBudget)
                {
                    best = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return best > 0 && AddressSpace.ExtentSummaryCodec.Encode(records.GetRange(0, best)) is { } trimmed
                ? trimmed
                : Array.Empty<byte>();
        }
        catch
        {
            return Array.Empty<byte>();   // 摘要是保真优化——绝不致命
        }
    }

    /// <summary>段区间记录数 + 是否含 sparse 位（诊断/测试用——VII-3 保真往返断言）。</summary>
    internal (int Count, bool AnySparse) GetExtentSummaryDiagnostic(int segId)
    {
        using var reader = _segmentTable.SnapshotSegmentExtents(segId);
        var count = 0;
        var anySparse = false;
        while (reader.MoveNext())
        {
            count++;
            if (reader.Current.Sparse) anySparse = true;
        }
        return (count, anySparse);
    }

    /// <summary>段区间记录明细 dump（诊断用——每条 "start-end:state:sp"）。</summary>
    internal string GetExtentDump(int segId)
    {
        using var reader = _segmentTable.SnapshotSegmentExtents(segId);
        var sb = new System.Text.StringBuilder();
        while (reader.MoveNext())
        {
            var (s, e, st, sp) = reader.Current;
            sb.Append($"[{s},{e}):{st}:sp={sp}; ");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 刷新一个段的 meta（携带最新区间摘要）——回收/截断改了区间布局后调（VII-3 保真）。
    /// <para>★ 段满时写的摘要在后续打洞前（stale）——本方法用当前标量 + 当前摘要重写。
    ///   段不在表 / meta 未启用 → 静默跳过；任何异常吞掉（保真优化绝不致命）。</para>
    /// </summary>
    private void RefreshSegmentMetaExtents(int segId)
    {
        try
        {
            if (!_segmentTable.TryGetSegment(segId, out var view) || view is not { IsValid: true } v) return;
            WriteSegmentTuple(segId, v.StableState, maxOffset: v.MaxOffset, growthLimit: v.GrowthLimit,
                realSize: v.RealSize, EncodeExtentSummary(segId));
        }
        catch
        {
            /* 保真优化绝不致命 */
        }
    }
}
