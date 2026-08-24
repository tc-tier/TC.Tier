using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Storage;

/// <summary>
/// 句柄池 partial——<see cref="FileHandlePool"/> 薄封装：Acquire=借（Dispose=还，池缓存保留），
/// 删段/Compact = RemoveAll 收口（关闭必与出缓存同发——无僵尸句柄）。
/// <para>★ 打开语义（与旧 Runtime DiskFileHandle 逐位对应）：写 = ReadWrite × OpenOrCreate ×
///   ShareReadWrite × (NoBuffering|WriteThrough 提示)；读 = Read × OpenExisting。</para>
/// <para>★ DIO 决策：请求（Hints.NoBuffering）× 探测结果（<see cref="UnbufferedSupport"/>，
///   建段首开回写）——Ignored 时回落缓冲（等价旧 FromSupport 映射）。</para>
/// <para>★ 借还纪律：每次 Acquire 恰好配对一次 Dispose（using）——池 DEBUG 计数绊线兜底。</para>
/// </summary>
internal sealed partial class StorageEngine
{
    /// <summary>DIO 句柄激活判定——请求 NoBuffering 且探测未被判 Ignored（NotRequested=未探测，跟随请求意图）。</summary>
    private bool DioActive
        => Hints.HasFlag(FileOpenHints.NoBuffering)
           && (UnbufferedIoSupport)Volatile.Read(ref _unbufferedSupportRaw) != UnbufferedIoSupport.Ignored;

    /// <summary>★ M1 修复：写句柄打开语义固化两形态（构造期初始化——对齐读路径记忆化；
    /// 原每借 new FileOpenOptions（record 类 56B——写主路径每 chunk 一次堆分配）。
    /// DIO 意图（请求 NoBuffering + WriteThrough）/ 缓冲（仅 WriteThrough）——DioActive
    /// 动态性由调用点按当前激活态选形态（探测前跟随请求意图）。</summary>
    private readonly FileOpenOptions _writeOptionsDio;
    private readonly FileOpenOptions _writeOptionsBuffer;

    /// <summary>写句柄打开语义（池 key 组成部分）——按当前 DIO 激活态选固化形态，零分配。</summary>
    private FileOpenOptions WriteOpenOptions() => DioActive ? _writeOptionsDio : _writeOptionsBuffer;

    /// <summary>
    /// 拿写句柄（借——调用方 using 归还；同 key 命中池内实例）。
    /// <para>★ 首借顺带回写 DIO 探测结果（请求意图 → 真实探测，IStorageInfo.UnbufferedSupport 报告）。</para>
    /// <para>★ 调用方：跨段 Append/Write 的 CopyChunks、Reclaim PunchHole、Flush。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IFileHandle GetWriteHandle(int segId)
    {
        // ★ L21 修复（）：ReclaimHead 过的段（表内 Invalid 墓碑）——OpenOrCreate 会在
        //   已删路径上复活幽灵空段文件（重开扫盘装配幽灵段）。表内 Invalid = 地址已死，
        //   快速失败（与"读已删段抛 PartitionInvalidException"同语义）。未注册段（前沿预建窗口）
        //   与在表非 Invalid 段正常放行。
        if (_segmentTable.TryGetSegment(segId, out var seg) && seg is { IsValid: true }
            && seg.Value.StableState == StableState.Invalid)
            throw new PartitionInvalidException("Segment not found.", new LogicalAddress(segId, 0));

        var handle = _pool.Acquire(SegmentFileName(segId), WriteOpenOptions());
        if (Hints.HasFlag(FileOpenHints.NoBuffering) && Volatile.Read(ref _supportProbed) == 0)
        {
            Volatile.Write(ref _unbufferedSupportRaw, (int)handle.UnbufferedSupport);
            Volatile.Write(ref _supportProbed, 1);
        }
        return handle;
    }

    /// <summary>DIO 探测完成标志（0=未探测——首借回写）。</summary>
    private int _supportProbed;

    /// <summary>
    /// ★ 按本次写几何选句柄——DIO 激活且 (offset,length) 满足对齐地板时走 DIO 写句柄；
    /// 否则走缓冲写句柄（Core Win DIO 地板 = max(扇区, 4096)（㉙），引擎 chunk 是 512 粒度 +
    /// 任意 payload 尾——非对齐尾走页缓存是 Core io.md 指定的消费纪律，性能主体（对齐大块）仍 DIO）。
    /// </summary>
    private IFileHandle GetWriteHandleForChunk(int segId, long offset, int length)
    {
        if (!DioActive) return GetWriteHandle(segId);
        var align = Math.Max((int)SectorSize, 4096);
        return offset % align == 0 && length % align == 0
            ? GetWriteHandle(segId)
            : _pool.Acquire(SegmentFileName(segId), _writeOptionsBuffer);   // ★ M1：固化缓冲形态（零分配）
    }

    /// <summary>
    /// 拿读句柄（借——调用方 using 归还）。usePageCache=false 时 NoBuffering（DIO 读，须对齐）。
    /// <para>★ 调用方：跨段 Read、SequentialReader、Recovery 扫描读。</para>
    /// <para>★ 读选项实例记忆化：FileOpenOptions 是 record 类——每读 new 即热路径堆分配；
    ///   两形态（页缓存/DIO）构造期固化，init-only 不可变可安全共享。</para>
    /// </summary>
    private readonly FileOpenOptions _readOptionsPageCache;
    private readonly FileOpenOptions _readOptionsDio;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private IFileHandle GetReadHandle(int segId, bool usePageCache)
        => _pool.Acquire(SegmentFileName(segId),
            usePageCache ? _readOptionsPageCache : _readOptionsDio);

    /// <summary>
    /// 建段文件（创建/句柄解耦，D-4）——<c>fs.CreateFile</c> 一次原子完成：空文件 + 幂等预分配 +
    /// 初始元组 FileExtra（Ready, maxOffset=0）。★ 句柄完全不参与建段——首个写句柄由
    /// <see cref="GetWriteHandle"/> 按需池化打开。
    /// <para>★ 已存在（single-flight 下不应发生/恢复重放）→ 幂等跳过。</para>
    /// <para>★ 调用方：建段（<c>CreateSegmentPhysical</c>）/ 恢复合成 seg0。</para>
    /// </summary>
    private void CreateSegmentFile(int segId, long growthLimit)
    {
        var path = SegmentFileName(segId);
        if (_fs.Exists(path)) return;
        _fs.CreateFile(path, PreallocateFile ? growthLimit : 0,
            SegmentTupleCodec.Encode(StableState.Ready, maxOffset: 0, growthLimit: growthLimit,
                realSize: growthLimit, ReadOnlySpan<byte>.Empty));
    }

    /// <summary>
    /// 释放某段全部池内句柄（删段/Compact rename 前收口——关闭必与出缓存同发，防 Windows 共享违例）。
    /// </summary>
    private void ReleaseSegmentHandles(int segId)
        => _pool.RemoveAll(p => p == SegmentFileName(segId));

    /// <summary>Dispose 时全量收口（池 Dispose 幂等——强制关闭残留借用并告警）。</summary>
    private void ReleaseAllHandles() => _pool.Dispose();

    /// <summary>
    /// 枚举介质上已存在的段（统一扫盘——全介质同构）：按引擎子目录 + 文件名模式匹配，解析 segId + 文件大小。
    /// <para>★ 多段：文件名 <c>{lastComponent}.{segId}</c>；单段：<c>{lastComponent}</c>（seg0）。
    ///   引擎名可多级（<c>"a/b"</c>）——模式用最后组件。</para>
    /// </summary>
    private IEnumerable<(int segId, long size)> EnumerateSegments()
    {
        var last = LastPathComponent(EngineName);
        if (!EnableSegmentation)
        {
            var single = SegmentFileName(0);
            if (_fs.Exists(single))
                yield return (0, _fs.Stat(single).Length);
            yield break;
        }

        if (!_fs.DirectoryExists(EngineName))
            yield break;   // 引擎目录不存在 = 空卷（扫盘⇒合成 seg0）
        foreach (var entry in _fs.EnumerateFiles(EngineName, $"{last}.*"))
        {
            var name = LastPathComponent(entry.Name);
            var dot = name.LastIndexOf('.');
            if (dot < 0 || !int.TryParse(name.AsSpan(dot + 1), out var segId)) continue;
            yield return (segId, entry.Length);
        }
    }

    /// <summary>取路径最后组件（'/' 唯一分隔符）。</summary>
    private static string LastPathComponent(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx < 0 ? path : path[(idx + 1)..];
    }

    // ═══════════════════════════════════════════════════════════════
    //  扫描面（internal——Checkpoint 子系统扫盘消费，全经 fs）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>扫盘枚举（<see cref="EnumerateSegments"/> 的 internal 出口）。</summary>
    internal IEnumerable<(int segId, long size)> ScanSegments() => EnumerateSegments();

    /// <summary>段文件存在性（fs.Stat 面）。</summary>
    internal bool SegmentFileExists(int segId) => _fs.Exists(SegmentFileName(segId));

    /// <summary>段文件长度（fileSize 物理权威）。</summary>
    internal long SegmentFileLength(int segId) => _fs.Stat(SegmentFileName(segId)).Length;
}
