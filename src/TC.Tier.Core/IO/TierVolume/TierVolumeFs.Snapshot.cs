using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace TC.Tier.Core.IO.TierVolume;

/// <summary>
/// 卷级快照 partial（V2 §1.1——快照 = 冻结检查点）。
/// <para>★ 机制（初始推荐——冻结位图模型）：检查点已天然 CoW（新镜像块 + 旧镜像随引用保留）。
///   快照 = 保留第 N 代元数据镜像 + 冻结分配位图：
///   - 捕获 = 一次检查点（<see cref="CommitMetadata"/> 挂接）+ 位图副本（1 bit/块）落冻结区；
///   - 写路径：释放块被快照引用 → 不还位图（钉块——<see cref="ReleaseBlocksFrozenAware"/> 全释放路径必经）；
///   - 读路径：<c>Open(SnapshotName)</c> 只读挂载——载入快照镜像 + 冻结位图，变异全拒（ReadOnlyVolume 语义）；
///   - 删除 = 位图差集对账（复用 ReconcileBitmapToReachable 同族机制）+ 检查点原子。</para>
/// <para>★ 崩溃论证：冻结位图与镜像引用随检查点翻转原子生效——崩溃窗口内快照 = 完整旧态或不存在
///   （同检查点 CoW 语义，逐点同构）；快照表变更入 journal（SnapshotCreate/SnapshotDelete 记录——
///   翻转后发射、重放幂等）。</para>
/// <para>★ 判定门（不钉死）：① 冻结钉块写放大 vs qcow2 refcount（写放大失控 → 换 refcount 模型，
///   双机制保留对照实验）；② 快照位图载入/落盘 IO（全量 vs 脏字增量——上限 16 由内存预算反推）；
///   ③ 捕获成本 O(容量/64 字) 在 1TB 卷的绝对数。验收探针 <c>--tier-volume-snapshot-probe</c>。</para>
/// </summary>
public sealed partial class TierVolumeFs
{
    /// <summary>全体快照冻结位图并集（字形态——<see cref="IsBlockFrozen"/> O(1)；null = 无快照）。</summary>
    private ulong[]? _frozenUnion;

    /// <summary>快照挂载形态（只读实例——冻结态视图；零写、无重放、无载体直视）。</summary>
    private bool _snapshotMount;

    /// <summary>挂载的快照名（登记键 + 释放键）。</summary>
    private string? _snapshotName;

    /// <summary>是否有活跃快照（释放过滤快路径）。</summary>
    private bool HasSnapshots => _frozenUnion is not null;

    // ═══════════════ 冻结集合（钉块判据）═══════════════

    /// <summary>块是否被任一快照冻结（并集字 O(1)；越界 = 快照之后扩容的块——恒未冻结）。</summary>
    private bool IsBlockFrozen(ulong block)
    {
        var union = _frozenUnion;
        if (union is null) return false;
        var w = block >> 6;
        if (w >= (ulong)union.LongLength) return false;
        return (union[w] & (1UL << (int)(block & 63))) != 0;
    }

    /// <summary>范围是否全部未冻结（分配 belt-and-suspenders——过滤遗漏防御；全未冻结 = 可安全分配）。</summary>
    private bool IsRangeFrozen(ulong firstBlock, uint count)
    {
        var union = _frozenUnion;
        if (union is null) return false;
        for (var w = firstBlock >> 6; w <= (firstBlock + count - 1) >> 6; w++)
        {
            if (w >= (ulong)union.LongLength) return false;
            var wordStart = w << 6;
            var from = Math.Max(firstBlock, wordStart);
            var to = Math.Min(firstBlock + count, wordStart + 64);
            var mask = to - from == 64 ? ~0UL : ((1UL << (int)(to - from)) - 1) << (int)(from - wordStart);
            if ((union[w] & mask) != 0) return true;
        }
        return false;
    }

    /// <summary>写命中区间 [pos, pos+take) 的物理块是否触及冻结集（V2 §1.1 CoW 判据——
    /// 原地覆写冻结块会毁快照读面：冻结块是快照数据的物源）。</summary>
    private bool ExtentHitFrozen(Extent x, long pos, int take)
    {
        if (!HasSnapshots) return false;
        var bs = (long)_pageSize;
        var firstBlock = x.PhysicalBlock + (ulong)((pos - x.LogicalStart) / bs);
        var lastBlock = x.PhysicalBlock + (ulong)((pos + take - 1 - x.LogicalStart) / bs);
        return IsRangeFrozen(firstBlock, (uint)(lastBlock - firstBlock + 1));
    }

    /// <summary>并集追加（捕获成功后）——快照冻结字 OR 入并集（容量增长时并集随扩）。</summary>
    private void UnionAdd(ulong[] frozenWords)
    {
        var words = _frozenUnion ?? new ulong[(long)((_sb.CapacityBlocks + 63) / 64)];
        if (words.LongLength < frozenWords.LongLength)
        {
            var resized = new ulong[frozenWords.LongLength];
            Array.Copy(words, resized, words.LongLength);
            words = resized;
        }
        var n = Math.Min(words.LongLength, frozenWords.LongLength);
        for (var w = 0; w < n; w++) words[w] |= frozenWords[w];
        _frozenUnion = words;
    }

    /// <summary>并集重建（删除快照后）——其余快照冻结字重新 OR（含惰性载入）。</summary>
    private void RebuildFrozenUnion()
    {
        _frozenUnion = null;
        if (_sb.Snapshots.Count == 0) return;
        var words = new ulong[(long)((_sb.CapacityBlocks + 63) / 64)];
        foreach (var s in _sb.Snapshots)
        {
            s.FrozenWords ??= ReadFrozenWords(s);
            var n = Math.Min(s.FrozenWords.LongLength, words.LongLength);
            for (var w = 0; w < n; w++) words[w] |= s.FrozenWords[w];
        }
        _frozenUnion = words;
    }

    /// <summary>打开时载入全体快照冻结位图并集（清洁/脏恢复路径共用——释放过滤随时需要判据）。</summary>
    private void LoadSnapshotFrozenSets()
    {
        _frozenUnion = null;
        if (_sb.Snapshots.Count == 0) return;
        RebuildFrozenUnion();
    }

    /// <summary>冻结区字载入（捕获容量字 + 页 padding 零）。</summary>
    private ulong[] ReadFrozenWords(SnapshotEntry snap)
    {
        var bytes = new byte[(long)snap.BitmapBlocks * _pageSize];
        ReadCarrierExactly((long)(snap.BitmapStart * (ulong)_pageSize), bytes);
        var words = new ulong[bytes.Length / 8];
        for (var i = 0; i < words.Length; i++)
            words[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i * 8, 8));
        return words;
    }

    /// <summary>冻结位图落区（捕获时——内容 = 当前位图字；提交序屏障 #1 覆盖持久化）。</summary>
    private void WriteFrozenBitmapRegion(ulong regionStart, ulong bitmapBlocks, ulong[] words)
    {
        var total = (long)bitmapBlocks * _pageSize;
        var buf = new byte[total];   // 捕获低频路径——整区单缓冲（1TB 卷 = 32MB 一次性，判定门 2 实测）
        var wordBytes = Math.Min(total, words.LongLength * 8);
        for (var i = 0; i < wordBytes / 8; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(buf.AsSpan(i * 8, 8), words[i]);
        WriteCarrier((long)(regionStart * (ulong)_pageSize), buf);
    }

    /// <summary>
    /// 冻结感知释放（V2 §1.1 钉块过滤——全部"还位图"路径的必经点）：
    /// 快照引用块保持 used（快照删除前不可复用——分配器自然绕开）；非冻结块照常释放。
    /// </summary>
    private void ReleaseBlocksFrozenAware(ulong firstBlock, uint count)
    {
        if (count == 0) return;
        if (!HasSnapshots)
        {
            MarkBlocks(firstBlock, count, used: false);
            return;
        }
        ulong runStart = firstBlock;
        uint runLen = 0;
        for (ulong b = firstBlock; b < firstBlock + count; b++)
        {
            if (IsBlockFrozen(b))
            {
                if (runLen > 0)
                {
                    MarkBlocks(runStart, runLen, used: false);
                    runLen = 0;
                }
            }
            else
            {
                if (runLen == 0) runStart = b;
                runLen++;
            }
        }
        if (runLen > 0) MarkBlocks(runStart, runLen, used: false);
    }

    /// <summary>快照名校验（非空、UTF8 ≤ 32B、不含 '/'）。</summary>
    private static void ValidateSnapshotName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('/'))
            throw new ArgumentException("快照名不得含 '/'（路径分隔语义）。", nameof(name));
        if (Encoding.UTF8.GetByteCount(name) > 32)
            throw new ArgumentException("快照名超过 32 字节 UTF-8。", nameof(name));
    }

    // ═══════════════ 快照操作（捕获/删除/枚举）═══════════════

    /// <summary>创建快照（V2 §1.1）：捕获 = 检查点（CommitMetadata 挂接）+ 冻结位图副本——检查点原子
    /// （翻转前崩溃 = 快照不存在、冻结区孤儿回收；翻转后 = 完整态）。上限 <see cref="Sb.SnapshotMax"/>；
    /// 名字唯一；须日志卷（捕获 LSN = 增量导出基点，§1.2）。</summary>
    public SnapshotInfo CreateSnapshot(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ValidateSnapshotName(name);
        using var gate = _maintenance.BeginMutation(nameof(CreateSnapshot), null);
        lock (MetadataLock)
        {
            ThrowIfReadOnly(nameof(CreateSnapshot));
            if (!_journalOn)
                throw new FileIOException(IOError.Unsupported,
                    "快照须日志卷（捕获 LSN = 增量导出基点——raw-journal §3.1）", null, nameof(CreateSnapshot));
            if (_sb.Snapshots.Count >= Sb.SnapshotMax)
                throw new FileIOException(IOError.IOFailure,
                    $"快照表满（{Sb.SnapshotMax} 条——上限由内存预算反推，判定门 2 实测裁决）", null, nameof(CreateSnapshot));
            if (_sb.Snapshots.Any(s => s.Name == name))
                throw new FileIOException(IOError.AlreadyExists, $"快照已存在：{name}", null, nameof(CreateSnapshot));

            var snap = new SnapshotEntry { Name = name, CaptureTicks = DateTimeOffset.UtcNow.UtcTicks };
            try
            {
                CommitMetadata(snap);   // 检查点原子：冻结区分配 + 冻结位图落区 + 条目绑定随翻转生效
            }
            catch
            {
                // 失败回滚：冻结区释放（条目未入表——若已入表则移除）；冻结区内容撕裂无害（区已释放）
                if (snap.BitmapBlocks > 0)
                    MarkBlocks(snap.BitmapStart, (uint)snap.BitmapBlocks, used: false);
                _sb.Snapshots.Remove(snap);
                throw;
            }
            UnionAdd(snap.FrozenWords!);
            JnlSnapshotCreate(name, snap.CaptureTicks, snap.CaptureLsn);   // 翻转后发射——重放幂等（表已含则跳）
            return new SnapshotInfo(name, snap.CaptureTicks, snap.CaptureLsn, snap.ImageCrc);
        }
    }

    /// <summary>删除快照（V2 §1.1）：位图差集对账（可达集之外的冻结块 → 释放）+ 镜像/冻结区释放 +
    /// 表条目移除——检查点原子。活跃挂载在档 → <see cref="IOError.SharingViolation"/>（钉块解除会毁其读面）。</summary>
    public void DeleteSnapshot(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        using var gate = _maintenance.BeginMutation(nameof(DeleteSnapshot), null);
        lock (MetadataLock)
        {
            ThrowIfReadOnly(nameof(DeleteSnapshot));
            var snap = _sb.Snapshots.FirstOrDefault(s => s.Name == name)
                ?? throw new FileIOException(IOError.NotFound, $"快照不存在：{name}", null, nameof(DeleteSnapshot));
            if (SInstances.ContainsKey(SnapshotMountKey(_carrier.IdentityKey, name)))
                throw new FileIOException(IOError.SharingViolation,
                    $"快照有活跃挂载实例，拒绝删除：{name}（关闭挂载后重试——钉块解除会毁其读面）",
                    null, nameof(DeleteSnapshot));

            // ★ 位图差集对账（复用 ReconcileBitmapToReachable 同族机制）：
            // 可达集（本卷现状 + 其余快照的镜像/冻结区/冻结块）之外的本快照冻结块 → 释放
            var reachable = BuildReachableSet(includeSnapFrozen: true, excluded: snap);
            var words = snap.FrozenWords ?? ReadFrozenWords(snap);
            for (var wi = 0L; wi < words.LongLength; wi++)
            {
                var w = words[wi];
                if (w == 0) continue;
                var baseBlock = (ulong)wi * 64;
                while (w != 0)
                {
                    var tz = System.Numerics.BitOperations.TrailingZeroCount(w);
                    var block = baseBlock + (ulong)tz;
                    if (!reachable.Contains(block))
                        MarkBlocks(block, 1, used: false);   // 本快照独占冻结且已不可达——还位图
                    w &= w - 1;
                }
            }
            // 镜像块释放（其余快照不引用时才可——捕获镜像按检查点 CoW 唯一）
            foreach (var (start, count) in snap.ImageRuns)
                if (!_sb.Snapshots.Any(other => !ReferenceEquals(other, snap)
                        && other.ImageRuns.Any(r => RunsOverlap(r, start, count))))
                    ReleaseBlocksFrozenAware(start, count);   // 冻结感知（其余快照冻结位图可能标记——belt-and-suspenders）
            // 冻结位图区释放（本快照私有）
            MarkBlocks(snap.BitmapStart, (uint)snap.BitmapBlocks, used: false);
            _sb.Snapshots.Remove(snap);
            CommitMetadata();
            JnlSnapshotDelete(name, snap.CaptureLsn);   // 翻转后发射——重放幂等（表已不含则跳）
            RebuildFrozenUnion();
        }
    }

    /// <summary>快照清单（捕获序）。</summary>
    public IReadOnlyList<SnapshotInfo> ListSnapshots()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _maintenance.ThrowIfReadsRejected(nameof(ListSnapshots), null);
        lock (MetadataLock)
            return _sb.Snapshots
                .Select(s => new SnapshotInfo(s.Name, s.CaptureTicks, s.CaptureLsn, s.ImageCrc))
                .ToList();
    }

    private static bool RunsOverlap((ulong Start, uint Count) a, ulong start, uint count)
        => a.Start < start + count && start < a.Start + a.Count;

    /// <summary>可达集构建（对账/差集共用）：成员保留区 + 日志保留 + 条目区间 + 活镜像 +
    /// 快照引用（镜像 + 冻结区 + 可选冻结块）。<paramref name="excluded"/> = 删除对账时排除的条目。</summary>
    private HashSet<ulong> BuildReachableSet(bool includeSnapFrozen, SnapshotEntry? excluded = null)
    {
        var reachable = new HashSet<ulong>();
        // RM-04：每成员头部+位图保留区（全局 [基块, 基块+bitmapStart+bitmapBlocks)）
        foreach (var m in _members)
        {
            var end = m.BaseBlock + m.Info.BitmapStartLocal + m.Info.BitmapBlocksLocal;
            for (var b = m.BaseBlock; b < end; b++) reachable.Add(b);
        }
        // 日志物理保留（§3.9——格式化时标记占用的保留区在可达集中）
        foreach (var b in _journalReserveBlocks) reachable.Add(b);
        foreach (var e in _entries.Values)
            foreach (var x in e.Extents)
            {
                var blocks = (x.Length + _pageSize - 1) / _pageSize;
                for (var i = 0UL; i < (ulong)blocks; i++) reachable.Add(x.PhysicalBlock + i);
            }
        // 活镜像块（已提交的）
        foreach (var (start, count) in _sb.ImageRuns)
            for (var i = 0UL; i < count; i++) reachable.Add(start + i);
        // 快照引用（V2 §1.1——镜像 + 冻结位图区 + 冻结块）
        foreach (var s in _sb.Snapshots)
        {
            if (ReferenceEquals(s, excluded)) continue;
            foreach (var (start, count) in s.ImageRuns)
                for (var i = 0UL; i < count; i++) reachable.Add(start + i);
            for (var i = 0UL; i < s.BitmapBlocks; i++) reachable.Add(s.BitmapStart + i);
            if (!includeSnapFrozen) continue;
            var words = s.FrozenWords ?? ReadFrozenWords(s);
            for (var wi = 0L; wi < words.LongLength; wi++)
            {
                var w = words[wi];
                if (w == 0) continue;
                var baseBlock = (ulong)wi * 64;
                while (w != 0)
                {
                    var tz = System.Numerics.BitOperations.TrailingZeroCount(w);
                    reachable.Add(baseBlock + (ulong)tz);
                    w &= w - 1;
                }
            }
        }
        return reachable;
    }

    // ═══════════════ 快照挂载（只读——「存档 = 活卷」闭环）═══════════════

    private static string SnapshotMountKey(string carrierIdentity, string snapshotName)
        => $"snapshot:{carrierIdentity}:{snapshotName}";

    /// <summary>快照挂载打开：同载体只读开口（无跨进程锁、无实例登记——冻结纪律使快照读面
    /// 在活卷并发写入下稳定：冻结块永不被复用/打洞）。载入快照镜像 + 冻结位图 → 冻结态视图。</summary>
    private static TierVolumeFs OpenSnapshotMount(TierVolumeCarrier carrier, string snapName,
        TierVolumeOpenOptions options, ILogger? logger)
    {
        if (options.Access != AccessMode.Read)
            throw new ArgumentException("快照挂载恒只读（SnapshotName 与 Access=Read 组合）", nameof(options));
        var fs = new TierVolumeFs(carrier, options, logger)
        {
            _snapshotMount = true,
            _snapshotName = snapName,
        };
        try
        {
            fs.OpenCarrierHandle(writable: false, createIfMissing: false, readOnlyNoLock: true);
            fs._pageSize = 0;   // DecodeWinner 自 superblock 探知
            var (winner, side) = fs.DecodeWinner();
            if (winner.Members.Count > 1)
                throw new FileIOException(IOError.Unsupported,
                    "多载体卷快照挂载未支持（快照镜像/冻结位图引用全局块号——成员布局演进项，V2 §1.1）",
                    carrier.Path, "Open");
            var snap = winner.Snapshots.FirstOrDefault(s => s.Name == snapName)
                ?? throw new FileIOException(IOError.NotFound, $"快照不存在：{snapName}", carrier.Path, "Open");
            winner.ImageRuns = snap.ImageRuns;
            winner.ImageLength = snap.ImageLength;
            winner.ImageCrc = snap.ImageCrc;
            fs.AdoptWinner(winner);
            fs._journalOn = false;   // 冻结态：无日志、无重放
            fs.ReadSnapshotBitmap(snap);
            fs.ContinueLoadSnapshot(winner, side);
            if (options.Label is not null && options.Label != fs._sb.Label)
                throw new FileIOException(IOError.NotFound,
                    $"label 校验不符：期望 '{options.Label}'，卷上实际 '{fs._sb.Label}'（spec label 在 Open = 断言）。",
                    carrier.Path, "open-label-check");
            var key = SnapshotMountKey(carrier.IdentityKey, snapName);
            if (SInstances.ContainsKey(key))
                throw new FileIOException(IOError.SharingViolation,
                    $"快照挂载已有活跃实例：{snapName}（每快照一挂载——登记键互斥）", carrier.Path, "Open");
            SInstances[key] = fs;
            return fs;
        }
        catch
        {
            fs.ReleaseResources();
            throw;
        }
    }

    /// <summary>快照冻结位图载入（替代活位图——空间记账即捕获时刻事实；全成员无关：单载体挂载）。</summary>
    private void ReadSnapshotBitmap(SnapshotEntry snap)
    {
        var totalWords = (long)((_sb.CapacityBlocks + 63) / 64);
        var bytes = new byte[(long)snap.BitmapBlocks * _pageSize];
        ReadCarrierExactly((long)(snap.BitmapStart * (ulong)_pageSize), bytes);
        _bitmapWords = new ulong[totalWords];
        var wordCount = Math.Min(totalWords, bytes.LongLength / 8);
        for (var i = 0L; i < wordCount; i++)
            _bitmapWords[i] = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((int)(i * 8), 8));
        _freeBlocks = 0;
        foreach (var word in _bitmapWords)
            _freeBlocks += (ulong)(64 - System.Numerics.BitOperations.PopCount(word));
        var totalBits = _sb.CapacityBlocks;
        var usedBeyond = (ulong)totalWords * 64 - totalBits;
        if (usedBeyond > 0) _freeBlocks -= usedBeyond;   // 尾部 padding 位不计空闲
        _dirtyBitmapWords.Clear();
    }

    /// <summary>快照挂载续载：镜像 → 元数据。冻结态——无对账（冻结位图即权威）、无重放（冻结点之后
    /// 的日志尾属活卷）、无翻转（只读）。</summary>
    private void ContinueLoadSnapshot(SuperblockData winner, string winnerSide)
    {
        var image = new byte[(long)winner.ImageLength];
        var pos = 0L;
        foreach (var (start, count) in winner.ImageRuns)
        {
            var take = (int)Math.Min(image.Length - pos, (long)count * _pageSize);
            if (take <= 0) break;
            ReadCarrierExactly((long)(start * (ulong)_pageSize), image.AsSpan((int)pos, take));
            pos += take;
        }
        if (Crc32.HashToUInt32(image) != winner.ImageCrc)
            throw new FileIOException(IOError.IOFailure,
                $"快照元数据镜像 CRC 校验失败（{winnerSide} 侧，快照 {_snapshotName}）", _carrier.Path, "Open");
        LoadMetadata(image);
    }
}

/// <summary>快照信息（V2 §1.1——CreateSnapshot/ListSnapshots 返回）。</summary>
public readonly record struct SnapshotInfo(string Name, long CaptureTicks, ulong CaptureLsn, uint ImageCrc)
{
    /// <summary>捕获时刻（UTC）。</summary>
    public DateTimeOffset CaptureTime => new(CaptureTicks, TimeSpan.Zero);
}
