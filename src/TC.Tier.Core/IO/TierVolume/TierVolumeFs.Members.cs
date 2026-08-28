using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TC.Tier.Core.IO.Image;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.TierVolume;

public sealed partial class TierVolumeFs
{
    // ═══════════════ 多载体操作族（RM-04 §3.8——扩容/缩容）═══════════════

    /// <summary>在线扩容 = 加载体（§3.8）：成员表事务（检查点原子持久）→ 新块立即可用。
    /// 新成员容量须 64 块对齐（位字不跨成员）；设备载体容量自几何，文件载体必填 capacityBytes。</summary>
    public void AddCarrier(TierVolumeCarrier carrier, long capacityBytes = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(carrier);
        if (_readOnly) ThrowIfReadOnly(nameof(AddCarrier));
        if (_sb.Members.Count >= Sb.MemberTableMax)
            throw new FileIOException(IOError.IOFailure,
                $"成员表满（{Sb.MemberTableMax}）——超上限留布局版本（§3.8）", null, nameof(AddCarrier));
        using var gate = _maintenance.BeginMutation(nameof(AddCarrier), carrier.Path);
        lock (MetadataLock)
        {
            ClaimInstance(carrier, null);
            // ★ V2 §1.1：快照存在期间拒绝成员增减（快照引用全局块号——成员表变更使引用失效；布局版本演进项）
            if (_sb.Snapshots.Count > 0)
                throw new FileIOException(IOError.Unsupported,
                    "快照存在期间不接受成员增减（快照冻结位图/镜像引用全局块号——成员表变更即失效；先删快照）",
                    carrier.Path, nameof(AddCarrier));
            // 容量解析（设备 = 几何 ioctl；文件 = 必填）+ 64 块对齐 + 扇区校验
            long capacity;
            var probeHandle = OpenProbeHandle(carrier, writable: true);
            try
            {
                if (carrier.IsDevice)
                {
                    capacity = QueryDeviceCapacityBytes(probeHandle);
                    var sector = QueryDeviceSectorSize(probeHandle);
                    if (sector > _pageSize)
                        throw new FileIOException(IOError.IOFailure,
                            $"新成员逻辑扇区 {sector} > 卷块大小 {_pageSize}——几何不兼容", carrier.Path, nameof(AddCarrier));
                }
                else
                {
                    capacity = capacityBytes;
                    if (capacity <= 0)
                        throw new ArgumentException("文件成员须声明 capacityBytes。", nameof(capacityBytes));
                }
                capacity -= capacity % (_pageSize * BitmapAlignBlocks);
                if (capacity <= 0)
                    throw new FileIOException(IOError.IOFailure,
                        $"成员容量不足（64 块对齐后 = {capacity}）", carrier.Path, nameof(AddCarrier));
                // 载体须非 TC 卷（防误并入已格式化载体）
                var probe = new byte[4];
                var got = RandomAccess.Read(probeHandle, probe, 0);
                if (got >= 4 && (probe.AsSpan().SequenceEqual("RAW1"u8) || probe.AsSpan().SequenceEqual("RAWC"u8)))
                    throw new FileIOException(IOError.AlreadyExists,
                        $"载体已是 TC 卷成员：{carrier.Path}", carrier.Path, nameof(AddCarrier));
            }
            finally
            {
                probeHandle.Dispose();
            }

            var bs = (long)_pageSize;
            var capacityBlocks = (ulong)(capacity / bs);
            var bitmapBytes = (capacityBlocks + 7) / 8;
            var bitmapBlocks = (bitmapBytes + (ulong)bs - 1) / (ulong)bs;
            var bitmapStart = (ulong)((HeaderBytes + bs - 1) / bs);
            var info = new MemberEntry(Guid.NewGuid(), capacityBlocks, bitmapStart, bitmapBlocks);

            // 打开成员（锁 + DIO）+ RAWC 身份头 + 位图区清零
            var m = OpenMemberCarrier(carrier, info, writable: true, createIfMissing: !carrier.IsDevice);
            var oldBitmap = _bitmapWords;   // D11：回滚基准（失败时位图/空闲计数/脏字索引复原）
            try
            {
                if (!carrier.IsDevice)
                {
                    RandomAccess.SetLength(m.Handle, capacity);
                    // IS-02：full 档新成员载体物理物化（与 FormatCore 同规——失败 fail-fast）
                    if (_preallocation == PreallocationMode.Full
                        && !TC.Tier.Core.NativeInterop.FileNative.EnsurePhysicalAllocation(m.Handle, capacity, _logger))
                        throw new FileIOException(IOError.IOFailure,
                            $"载体物理占位失败（Preallocation=Full）：{carrier.Path}——full 档不允许静默降级为稀疏",
                            carrier.Path, nameof(AddCarrier));
                }
                m.BaseBlock = _sb.CapacityBlocks;
                var header = new byte[512];
                EncodeMemberHeader(header, info, _sb.Uuid, _sb.Members.Count, _pageSize);
                WriteMemberLocal(m, 0, header);
                var zeros = new byte[_pageSize];
                for (var b = 0UL; b < bitmapBlocks; b++)
                    WriteMemberLocalAligned(m, (long)((bitmapStart + b) * (ulong)_pageSize), zeros);

                // 成员表事务（检查点原子——日志记录含全局块号，追加式扩容下旧记录语义稳定）
                var newWords = new ulong[oldBitmap.LongLength + (long)(capacityBlocks / 64)];
                Array.Copy(oldBitmap, newWords, oldBitmap.LongLength);
                _bitmapWords = newWords;
                _freeBlocks += capacityBlocks;   // 新成员块全部空闲（保留区随后标记扣减）
                for (var w = oldBitmap.LongLength; w < newWords.LongLength; w++) _dirtyBitmapWords.Add((ulong)w);   // 新区全脏（落位图区）
                MarkBlocks(m.BaseBlock, (uint)((long)bitmapStart + (long)bitmapBlocks), used: true);   // 新成员头部+位图保留

                _sb.Members.Add(info);
                _sb.CapacityBlocks += capacityBlocks;
                _sb.Flags |= FlagMultiCarrier;
                // 多载体卷退出自动扩容：成员 0 容量变更会使后续成员基块漂移——容量管理转显式（AddCarrier/RemoveCarrier）
                if (_autoExpand)
                {
                    _sb.Flags = (ushort)(_sb.Flags & ~FlagAutoExpand);
                    _autoExpand = false;
                    _logger?.LogInformation("卷转入多载体管理：自动扩容关闭（容量管理 = AddCarrier/RemoveCarrier）");
                }
                _members = _members.Append(m).ToArray();
                RefreshCarrierDio();
                CommitMetadata();   // 成员表 + 位图 + superblock 原子持久（§3.8 成员表事务）
            }
            catch
            {
                // 失败回滚：新成员登记撤销（载体上残留 RAWC 头——下次 AddCarrier 探测拒并入）
                _sb.Members.Remove(info);
                _sb.CapacityBlocks -= capacityBlocks;
                if (_sb.Members.Count == 1) _sb.Flags = (ushort)(_sb.Flags & ~FlagMultiCarrier);
                _members = _members.Where(x => !ReferenceEquals(x, m)).ToArray();
                RefreshCarrierDio();
                // D11：位图/空闲计数/脏字索引一并回滚（失败后卷内存态一致——半提交成员不得污染分配面）
                _bitmapWords = oldBitmap;
                _freeBlocks = 0;
                foreach (var w in oldBitmap) _freeBlocks += (ulong)(64 - System.Numerics.BitOperations.PopCount(w));
                var totalBits = _sb.CapacityBlocks;
                var usedBeyond = (ulong)oldBitmap.LongLength * 64 - totalBits;
                if (usedBeyond > 0) _freeBlocks -= usedBeyond;
                _dirtyBitmapWords.Clear();
                for (var w = 0UL; w < (ulong)oldBitmap.LongLength; w++) _dirtyBitmapWords.Add(w);   // 全量重写（回滚后基线复位）
                try
                {
                    m.Handle.Dispose();
                }
                catch
                {
                    // ignored
                }

                try { m.CrossProcLock?.Dispose(); }
                catch
                {
                    // ignored
                }

                throw;
            }
        }
    }

    /// <summary>迁移式缩容数据面（RM-04 v2a——btrfs device remove 同构）：
    /// ① 源成员数据区位全部标记占用（分配器自然绕开——迁移目标必落其他成员）；
    /// ② 逐文件逐 extent：数据搬运（读旧写新）+ ApplyExtentRelocate 重定向 + 日志记录；
    /// ③ 页缓存失效旧块。完成即成员全空 → 走摘除路径。fs 锁内（RemoveCarrier 调用）。</summary>
    private void MigrateMemberData(CarrierMember m)
    {
        var memberEnd = m.BaseBlock + m.Info.CapacityBlocks;
        // ① 屏蔽源成员数据区（位图标占——分配绕开；摘除时整体丢弃不计泄漏）
        var firstData = m.BaseBlock + m.Info.BitmapStartLocal + m.Info.BitmapBlocksLocal;
        MarkBlocks(firstData, (uint)(memberEnd - firstData), used: true);

        const int chunkBlocks = 64;   // 256KB 搬运粒度
        var buf = ArrayPool<byte>.Shared.Rent(chunkBlocks * _pageSize);
        try
        {
            foreach (var e in _entries.Values.ToList())
            {
                foreach (var x in e.Extents.Where(x => x.PhysicalBlock < memberEnd
                             && x.PhysicalBlock + (ulong)(x.Length / _pageSize) > m.BaseBlock).ToList())
                {
                    // 逐段搬迁：ExtentRelocate 粒度 = 单个旧 extent（新 run 可拆多段——分配器自由）
                    var blocks = (uint)((x.Length + _pageSize - 1) / _pageSize);
                    var newRuns = new List<(ulong Phys, long Len)>();
                    var remaining = blocks;
                    while (remaining > 0)
                    {
                        var take = Math.Min(remaining, chunkBlocks);
                        var phys = AllocateBlocks(take, "Migrate");
                        newRuns.Add((phys, take * _pageSize));
                        remaining -= take;
                    }
                    // 数据搬运（旧读新写——块粒度对齐，源/目标各自 DIO 纪律经全局路由）
                    long done = 0;
                    foreach (var (phys, len) in newRuns)
                    {
                        for (var off = 0L; off < len; off += chunkBlocks * _pageSize)
                        {
                            var take = (int)Math.Min(len - off, (long)chunkBlocks * _pageSize);
                            ReadCarrierExactly((long)(x.PhysicalBlock * (ulong)_pageSize) + done + off, buf.AsSpan(0, take));
                            WriteCarrier((long)(phys * (ulong)_pageSize) + off, buf.AsSpan(0, take));
                        }
                        done += len;
                        TrackDeltaDirtyBlocks(phys, (uint)(len / _pageSize));   // ★ V2 §1.2：迁移物化块入增量窗口
                        InvalidateCacheBlocks(phys, (uint)(len / _pageSize));   // 新块直落——缓存不驻留旧载体状态
                    }
                    ApplyExtentRelocate(e, x.LogicalStart, x.Length, newRuns);
                    JnlExtentRelocate(e.Path, x.LogicalStart, x.Length, newRuns);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>缩容 = 减载体（§3.8 v1：仅允许移除全空成员——位图全零校验）。
    /// 成员表事务（检查点原子）；被移成员后续载体作废（含 RAWC 头）。</summary>
    public void RemoveCarrier(int memberIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_readOnly) ThrowIfReadOnly(nameof(RemoveCarrier));
        using var gate = _maintenance.BeginMutation(nameof(RemoveCarrier), null);
        lock (MetadataLock)
        {
            if (memberIndex <= 0)
                throw new ArgumentException("主载体（成员 0）不可移除。", nameof(memberIndex));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(memberIndex, _members.Length, "成员索引超限。");
            // ★ V2 §1.1：快照存在期间拒绝成员增减（全局块号重排使快照引用失效；布局版本演进项）
            if (_sb.Snapshots.Count > 0)
                throw new FileIOException(IOError.Unsupported,
                    "快照存在期间不接受成员增减（成员摘除 = 全局块号重排——快照冻结位图/镜像引用失效；先删快照）",
                    null, nameof(RemoveCarrier));
            var m = _members[memberIndex];
            // 全空校验：数据区位全零（头部+位图保留位在数据区之前——共字时掩码排除）
            var firstData = m.BaseBlock + m.Info.BitmapStartLocal + m.Info.BitmapBlocksLocal;
            var firstDataWord = firstData / 64;
            var nonEmpty = false;
            for (var w = m.BaseBlock / 64; w < (m.BaseBlock + m.Info.CapacityBlocks) / 64; w++)
            {
                var word = _bitmapWords[w];
                if (w == firstDataWord)
                    word &= ulong.MaxValue << (int)(firstData % 64);   // 保留侧位掩除
                if (w >= firstDataWord && word != 0) { nonEmpty = true; break; }
            }
            if (nonEmpty)
                MigrateMemberData(m);   // RM-04 v2a：迁移式缩容（非空成员——块搬迁后摘除）
            JournalCommit();   // 在途记录先落（成员表变更后全局块号语义变化——日志尾必须清空）
            // 成员表事务：摘除（先记旧基块——位图重排拷贝用）
            var oldWords = _bitmapWords;
            var oldMembers = _members;
            _sb.Members.RemoveAt(memberIndex);
            _sb.CapacityBlocks -= m.Info.CapacityBlocks;
            if (_sb.Members.Count == 1) _sb.Flags = (ushort)(_sb.Flags & ~FlagMultiCarrier);
            _members = _members.Where((_, i) => i != memberIndex).ToArray();
            RefreshCarrierDio();
            // 位图重排：按新基块把各成员字段从旧数组搬到新数组（被移成员字全零已验证——丢弃无损）
            var newLen = (_sb.CapacityBlocks + 63) / 64;
            var newWords = new ulong[newLen];
            ulong total = 0;
            foreach (var mm in _members)
            {
                var oldBase = oldMembers[Array.IndexOf(oldMembers, mm)].BaseBlock;
                Array.Copy(oldWords, (long)(oldBase / 64), newWords, (long)(total / 64), (long)(mm.Info.CapacityBlocks / 64));
                mm.BaseBlock = total;
                total += mm.Info.CapacityBlocks;
            }
            _bitmapWords = newWords;
            _freeBlocks = 0;
            foreach (var w in newWords) _freeBlocks += (ulong)(64 - System.Numerics.BitOperations.PopCount(w));
            var totalBits = _sb.CapacityBlocks;
            var usedBeyond = newLen * 64 - totalBits;
            if (usedBeyond > 0) _freeBlocks -= usedBeyond;
            _dirtyBitmapWords.Clear();
            for (var w = 0UL; w < newLen; w++) _dirtyBitmapWords.Add(w);   // 全量重写（布局重排）
            CommitMetadata();   // 摘除原子持久
            SInstances.TryRemove(m.Carrier.IdentityKey, out _);
            try { m.Handle.Dispose(); }
            catch
            {
                // ignored
            }

            try { m.DioReadHandle?.Dispose(); }
            catch
            {
                // ignored
            }

            try { m.CrossProcLock?.Dispose(); }
            catch
            {
                // ignored
            }
        }
    }

    /// <summary>探测句柄（AddCarrier 容量/残留检查用——不复用成员锁路径）。
    /// 文件成员允许创建（新建载体探测语义）；设备成员仅打开。</summary>
    private SafeFileHandle OpenProbeHandle(TierVolumeCarrier carrier, bool writable)
        => File.OpenHandle(carrier.Path, carrier.IsDevice ? FileMode.Open : FileMode.OpenOrCreate,
            writable ? FileAccess.ReadWrite : FileAccess.Read,
            FileShare.ReadWrite, FileOptions.Asynchronous);

    private void ThrowIfReadOnly(string op)
    {
        if (_degraded)
            throw new FileIOException(IOError.ReadOnlyVolume,
                $"降级卷不接受 {op}（成员缺失只读形态——RM-04 v2b；修复 = 全量成员重开）", null, op);
        if (_readOnly)
            throw new FileIOException(IOError.ReadOnlyVolume,
                $"只读卷不接受 {op}（ReadOnlyVolume 语义——dirty 降级形态或显式只读打开，§4.1）", null, op);
    }

    private sealed class NoOpLease : IDisposable
    {
        public static readonly NoOpLease Instance = new();
        public void Dispose() { }
    }

}
