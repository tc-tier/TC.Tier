using System.Runtime.InteropServices;

namespace TC.Tier.Core.IO.TierVolume;

/// <summary>
/// 位图 partial——空间事实的真相源（§2.3 铁律 / §3.1 统一块空间）。
/// <para>★ 容量成比例（1 bit/块）、格式化时静态定长——是几何不是台阶（§3.1）。</para>
/// <para>★ v1 分配 = 起点提示 + 线性扫描 first-fit（分配是元数据路径非数据热路径——§3.4 单锁足够）。</para>
/// </summary>
public sealed partial class TierVolumeFs
{
    /// <summary>成员容量对齐粒度（块）——64 块 = 一个位图字：保证位字不跨成员（RM-04 §3.8）。</summary>
    private const long BitmapAlignBlocks = 64;

    private ulong[] _bitmapWords = null!;      // 位图机器形态（字 64 位——小端位序：位 i 在字 i/64 的位 i%64）
    private ulong _allocHint;                  // first-fit 起点提示（分配成功后推进——摊销扫描）
    private readonly HashSet<ulong> _dirtyBitmapWords = new();   // 脏字索引（增量落盘判据——RM-17）

    /// <summary>空闲块数（缓存——提交/统计用）。</summary>
    private ulong _freeBlocks;

    private bool IsBlockUsed(ulong block)
        => (_bitmapWords[block >> 6] & (1UL << (int)(block & 63))) != 0;

    /// <summary>批量标记（字级掩码——O(相交字数) 而非 O(块数)；脏字入增量索引）。</summary>
    private void MarkBlocks(ulong start, uint count, bool used)
    {
        if (count == 0) return;
        var end = start + count;
        var firstWord = start >> 6;
        var lastWord = (end - 1) >> 6;
        for (var w = firstWord; w <= lastWord; w++)
        {
            var wordStart = w << 6;
            var from = Math.Max(start, wordStart);
            var to = Math.Min(end, wordStart + 64);
            var mask = to - from == 64 ? ~0UL : ((1UL << (int)(to - from)) - 1) << (int)(from - wordStart);
            ref var word = ref _bitmapWords[w];
            if (used)
            {
                _freeBlocks -= (ulong)System.Numerics.BitOperations.PopCount(mask & ~word);   // 新置位位数（已置位不重复扣）
                word |= mask;
            }
            else
            {
                _freeBlocks += (ulong)System.Numerics.BitOperations.PopCount(word & mask);
                word &= ~mask;
            }
            _dirtyBitmapWords.Add(w);
        }
    }

    /// <summary>
    /// 分配 count 个连续块——返回起始块号；空间不足抛 <see cref="IOError.DiskFull"/>
    /// （★ 无台阶契约：唯一上限 = 块数，失败类型只允许空间耗尽，§3.1）。
    /// 自动扩容卷（quota=-1 New 的文件载体）：空间耗尽/碎片化时先尝试 <see cref="TryExpandCapacity"/>
    /// 再重扫一次（成功即返回；不可扩 = DiskFull 原语义）。
    /// <paramref name="maxBlock"/>：分配上限（排除范围）——元数据镜像锚定主载体（降级运行前提，v2b）。
    /// <paramref name="minBlock"/>：分配下限（含）——成员内分配（Defrag 单成员落点，D8）。
    /// </summary>
    private ulong AllocateBlocks(uint count, string purpose, ulong? maxBlock = null, ulong? minBlock = null)
    {
        if (count == 0) return 0;
        TryFreeRetiredLocked();   // D1b：分配前推进安全批次的回收——空间回收及时可见，避免虚假 DiskFull
        var total = maxBlock ?? _sb.CapacityBlocks;
        var min = minBlock ?? 0;
        if (_quotaCapBlocks is { } cap && _sb.CapacityBlocks - _freeBlocks + count > cap)
            throw new FileIOException(IOError.DiskFull,
                $"配额收紧：已用 {_sb.CapacityBlocks - _freeBlocks} 块 + 请求 {count} > 上限 {cap} 块" +
                "（Open 收紧 min(quota, 供给)，medium-protocol-and-conversion §5.3）", null, purpose);
        if (_freeBlocks < count)
        {
            if (!TryExpandCapacity(count, purpose))
                throw new FileIOException(IOError.DiskFull,
                    $"空间耗尽：请求 {count} 块（{purpose}），空闲 {_freeBlocks} 块——容量物理判据（§3.2）", null, purpose);
            return AllocateBlocks(count, purpose, RefreshPrimaryLimit(maxBlock), minBlock);
        }
        var start = Math.Max(_allocHint, min);
        ulong? found = null;
        var scanFrom = start;
        while (found is null)
        {
            found = scanFrom + count <= total
                ? ScanFree(scanFrom, count, total)   // 提示点起扫
                : null;
            // ★ V2 §1.1：冻结块跳过（belt-and-suspenders——钉块纪律下冻结块恒 used，本判据防过滤遗漏）
            if (found is { } s && IsRangeFrozen(s, count))
            {
                scanFrom = s + 1;
                found = null;
                continue;
            }
            if (found is not null) break;
            if (scanFrom == min) break;   // 已全量扫过——落扩缩/报错路径
            scanFrom = min;               // 界内全量重扫（碎片化兜底）
        }
        if (found is { } s0)
        {
            MarkBlocks(s0, count, used: true);
            _allocHint = (s0 + count) % _sb.CapacityBlocks;
            return s0;
        }
        // 碎片化（限界内无连续 run）——自动扩容卷新界是整段连续空闲，扩后重扫
        if (TryExpandCapacity(count, purpose))
            return AllocateBlocks(count, purpose, RefreshPrimaryLimit(maxBlock), minBlock);
        throw new FileIOException(IOError.DiskFull,
            minBlock is not null
                ? $"成员内无 {count} 连续块（[{minBlock}, {total})——单成员分配失败（D8：Map 物化前提）"
                : maxBlock is null
                    ? $"空间碎片化：无 {count} 连续块（总空闲 {_freeBlocks}）——v1 连续分配的已知边界"
                    : $"主载体空间不足：元数据镜像须锚定成员 0（请求 {count} 块，界内空闲不足——降级运行前提，RM-04 v2b）",
            null, purpose);
    }

    /// <summary>扩容后重扫时的主载体限界刷新（maxBlock 唯一来源 = 成员 0 末端——扩容后末端已变）。</summary>
    private ulong? RefreshPrimaryLimit(ulong? maxBlock)
        => maxBlock is null ? null : _members[0].BaseBlock + _members[0].Info.CapacityBlocks;

    // ═══════════════ 自动扩容（medium-protocol §5.3：quota=-1 文件载体按需增长）═══════════════

    /// <summary>自动扩容初始界（64 MiB——"初始小界"；日志预留默认 8 MiB 恰在 1/8 封顶内自适）。</summary>
    private const long AutoExpandInitialBytes = 64L << 20;

    /// <summary>容量上限护栏（块）：2^44 块 = 64 TiB @4KiB——几何倍增循环的终止界。</summary>
    private const ulong MaxVolumeBlocks = 1UL << 44;

    /// <summary>capacity 块所需位图块数（1 bit/块 → 字节 → 块向上取整）。</summary>
    private ulong BitmapBlocksFor(ulong capacityBlocks)
        => (capacityBlocks / 8 + (ulong)_pageSize - 1) / (ulong)_pageSize;

    /// <summary>
    /// 自动扩容（fs 锁内——触发点在 AllocateBlocks 的两处 DiskFull 失败位）。不可扩（非自动卷/多载体/
    /// 配额已界/重入）返回 false 由调用方抛 DiskFull。机制 = 位图重定位 + superblock 原子翻转：
    /// <para>★ 新位图区落在增长区间头部 [旧容量, 旧容量+新位图块)，与既有数据无重叠；</para>
    /// <para>★ 提交序与 <see cref="CommitMetadata"/> 同构：新位图整区先落盘 → 双侧翻转（原子提交点）→
    ///   旧位图块在新位图中即释放。翻转前崩溃 = 旧界+旧位图完好（增长尾部不可达，等价崩溃残留）；
    ///   翻转后崩溃 = 新位图已完整（旧位图块若未及重写，dirty 对账回收兜底）；</para>
    /// <para>★ 日志语义不切割：翻转保留日志五字段原值（代数/状态/CkptLsn/HeadLsn），追加式扩容下
    ///   既有记录的全局块号语义稳定（在途两段式提交只写日志区、sb 变更全在锁内——无竞态）；</para>
    /// <para>★ 物理盘满（ENOSPC 等 IO 失败）：内存态复原后按 DiskFull 语义上报（-1 的边界 = 磁盘物理满）。</para>
    /// </summary>
    private bool TryExpandCapacity(uint neededBlocks, string purpose)
    {
        if (!_autoExpand || _expanding || _sb.Members.Count > 1) return false;
        _expanding = true;
        try
        {
            var bs = (ulong)_pageSize;
            var oldCapacity = _sb.CapacityBlocks;
            var used = oldCapacity - _freeBlocks;
            var target = oldCapacity * 2;
            while (used + neededBlocks + BitmapBlocksFor(target) > target)
            {
                if (target >= MaxVolumeBlocks) return false;   // 需求超上限界——DiskFull 原语义
                target = Math.Min(target * 2, MaxVolumeBlocks);
            }
            if (_quotaCapBlocks is { } quotaCap)
            {
                if (quotaCap <= oldCapacity) return false;   // 配额不高于现供给——扩容无意义
                var aligned = Math.Min(target, quotaCap / (ulong)BitmapAlignBlocks * (ulong)BitmapAlignBlocks);
                if (aligned <= oldCapacity || used + neededBlocks + BitmapBlocksFor(aligned) > aligned)
                    return false;   // 配额界内容纳不下请求——DiskFull 原语义
                target = aligned;
            }

            var oldWords = _bitmapWords;
            var (oldCap, oldBitmapStart, oldBitmapBlocks) = (_sb.CapacityBlocks, _sb.BitmapStart, _sb.BitmapBlocks);
            var oldFree = _freeBlocks;
            var oldHint = _allocHint;
            var oldInfo = _members[0].Info;
            var newBitmapStart = oldCapacity;   // 增长区间头部（旧容量已 64 块对齐）
            var newBitmapBlocks = BitmapBlocksFor(target);
            try
            {
                // 内存位图重定位：旧字拷贝（新区零=空闲）；旧位图块释放 + 新位图块保留（均计入新位图）
                var newWords = new ulong[target / 64];
                Array.Copy(oldWords, newWords, oldWords.LongLength);
                _bitmapWords = newWords;
                _freeBlocks += target - oldCapacity;   // 增长区整段空闲（新位图保留随下述标记扣减）
                // ★ V2 §1.1：旧位图区冻结感知释放（快照冻结位图在捕获时含旧位图区——钉块保持）
                ReleaseBlocksFrozenAware(oldBitmapStart, (uint)oldBitmapBlocks);
                MarkBlocks(newBitmapStart, (uint)newBitmapBlocks, used: true);

                _sb.CapacityBlocks = target;
                _sb.BitmapStart = newBitmapStart;
                _sb.BitmapBlocks = newBitmapBlocks;
                _members[0].Info = new MemberEntry(_sb.Uuid, target, newBitmapStart, newBitmapBlocks);

                RandomAccess.SetLength(_members[0].Handle, (long)(target * bs));   // 稀疏延伸（字节惰性分配）

                // 新位图整区先落（全字置脏）——翻转前完成是原子性前提
                _dirtyBitmapWords.Clear();
                for (var w = 0UL; w < target / 64; w++) _dirtyBitmapWords.Add(w);
                WriteBitmapToCarrier();

                // 原子翻转（代数+1；日志五字段原值；clean 位在写打开时已清）
                _sb.Generation++;
                var buffer = new byte[Sb.TotalSize];
                EncodeSuperblock(buffer, _sb);
                WriteCarrier(SuperblockBackupOffset, buffer);
                FlushCarrier();
                WriteCarrier(SuperblockPrimaryOffset, buffer);
                FlushCarrier();

                _allocHint = newBitmapStart + newBitmapBlocks;   // 直指新界空闲区
                _logger?.LogInformation("虚拟卷自动扩容：{Old} → {New} 块（{Bytes} 字节，{Purpose}）",
                    oldCapacity, target, (long)(target * bs), purpose);
                return true;
            }
            catch (Exception ex)
            {
                // 物理失败（典型 = 盘满）——内存态复原（载体长度不回缩：过长文件+旧 sb = 尾部不可达，崩溃等价形态）
                _bitmapWords = oldWords;
                _sb.CapacityBlocks = oldCap;
                _sb.BitmapStart = oldBitmapStart;
                _sb.BitmapBlocks = oldBitmapBlocks;
                _members[0].Info = oldInfo;
                _freeBlocks = oldFree;
                _allocHint = oldHint;
                _dirtyBitmapWords.Clear();
                for (var w = 0UL; w < (ulong)oldWords.LongLength; w++) _dirtyBitmapWords.Add(w);
                if (ex is not IOException or FileIOException) throw;   // FileIOException 原样上抛
                var diskFull = ex.HResult is unchecked((int)0x80070070)   // ERROR_DISK_FULL
                    or unchecked((int)0x80070027)                          // ERROR_HANDLE_DISK_FULL
                    or unchecked((int)0x8007001C);                         // ENOSPC (28)
                throw new FileIOException(diskFull ? IOError.DiskFull : IOError.IOFailure,
                    $"自动扩容失败（磁盘物理满？）：{ex.Message}", _carrier.Path, purpose, ex);
            }
        }
        finally { _expanding = false; }
    }

    /// <summary>
    /// 追加快道用（RM-01/D1）：[start, start+count) 恰好全空闲则占用并返回 true——
    /// 尾区间物理邻接延伸的判据（不推进 _allocHint——延伸不是常规分配路径）。
    /// ★ V2 §1.1：冻结范围拒绝（belt-and-suspenders——钉块纪律下恒 used，本判据防过滤遗漏复用）。
    /// </summary>
    private bool TryMarkContiguous(ulong start, uint count)
    {
        if (count == 0) return true;
        if (start + count > _sb.CapacityBlocks) return false;
        if (IsRangeFrozen(start, count)) return false;
        if (!IsRangeFree(start, count)) return false;
        MarkBlocks(start, count, used: true);
        return true;
    }

    private bool IsRangeFree(ulong start, uint count)
    {
        // 字级判检（RM-33）：相交字构造掩码一次比——O(range/64) 而非逐块 O(range)
        var end = start + count;
        var lastWord = (end - 1) >> 6;
        for (var w = start >> 6; w <= lastWord; w++)
        {
            var wordStart = w << 6;
            var from = Math.Max(start, wordStart);
            var to = Math.Min(end, wordStart + 64);
            var mask = to - from == 64 ? ~0UL : ((1UL << (int)(to - from)) - 1) << (int)(from - wordStart);
            if ((_bitmapWords[w] & mask) != 0) return false;
        }
        return true;
    }

    /// <summary>字级 first-fit 扫描（RM-33）：整字满 O(1) 跳 64 块（先查整字空/满）；
    /// 部分占用字从首个空闲位起候选（候选失败继续字内下一空闲位——覆盖面与逐块扫描等价）。</summary>
    private ulong? ScanFree(ulong from, uint count, ulong total)
    {
        if (count > total) return null;
        var firstWord = from >> 6;
        var wordEnd = (total + 63) >> 6;   // 含尾 partial 字（maxBlock 限界时非整字）
        for (var w = firstWord; w < wordEnd; w++)
        {
            var word = _bitmapWords[w];
            if (word == ulong.MaxValue) continue;   // 整字满——跳 64 块
            var firstBit = w == firstWord ? from & 63 : 0UL;
            for (var b = firstBit; b < 64; b++)
            {
                if ((word & (1UL << (int)b)) != 0) continue;
                var candidate = (w << 6) + b;
                if (candidate + count > total) return null;   // 候选只增——后续必越界
                if (IsRangeFree(candidate, count)) return candidate;
            }
        }
        return null;
    }

    /// <summary>位图增量落盘（RM-17）：只写含脏字的块——O(脏块) 而非 O(容量)。
    /// RM-31：同页脏字合并——按目标位图块（成员,块）去重后每块一次写（此前每脏字一次——检查点写放大）。
    /// RM-04：物理位图按成员分段——全局字 w 属成员 m（基块 ≤ w*64 小于 基块+容量），写 m 的本地位图区。
    /// 不刷盘（持久化屏障由提交序统一持有）。格式化时全字置脏 → 首次提交全量写。
    /// </summary>
    private void WriteBitmapToCarrier()
    {
        if (_dirtyBitmapWords.Count == 0) return;
        var wordsPerBlock = (ulong)(_pageSize / 8);
        var targets = new HashSet<(CarrierMember M, ulong Block)>();
        foreach (var w in _dirtyBitmapWords)
        {
            var m = MemberForBlock(w * 64);
            var localWord = (w * 64 - m.BaseBlock) / 64;
            targets.Add((m, localWord / wordsPerBlock));
        }
        var blockBuf = new byte[_pageSize];
        foreach (var (m, localBitmapBlock) in targets)
        {
            var baseWord = localBitmapBlock * wordsPerBlock;
            for (var i = 0UL; i < wordsPerBlock; i++)
            {
                // 全局字 = 成员基字 + 本地字（本地越界 = padding 零）
                var globalIdx = m.BaseBlock / 64 + baseWord + i;
                var word = globalIdx < (ulong)_bitmapWords.LongLength ? _bitmapWords[globalIdx] : 0UL;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(blockBuf.AsSpan((int)(i * 8)), word);
            }
            var localOffset = (long)((m.Info.BitmapStartLocal + localBitmapBlock) * (ulong)_pageSize);
            WriteMemberLocalAligned(m, localOffset, blockBuf);
        }
        _dirtyBitmapWords.Clear();
    }

    /// <summary>载体空间归还（RM-05 + V2 §1.3）：块真正回收时对载体下发空间释放——
    /// 设备 = BLKDISCARD（TRIM）；文件载体 = fallocate(PUNCH_HOLE) / FSCTL_SET_ZERO_DATA（打洞——
    /// 载体已稀疏，RM-41 协同：`.tier` 物理尺寸跟踪活数据，存档紧凑；qcow2 discard=unmap 平价）。
    /// ★ V2 §1.1：冻结块跳过打洞（钉块数据是快照读面的物源——打洞即毁存档）。
    /// 优化非正确性（B1 零基纪律独立成立——失败仅损失空间优化）；advisory 失败静默。
    /// 设备 TRIM 仅 Linux；文件打洞全平台（FileNative.PunchHole 平台分发）。字节区间按成员内本地偏移。</summary>
    private unsafe void TrimCarrierBlocks(ulong block, uint count)
    {
        if (count == 0) return;
        var range = stackalloc long[2];   // CA2014：stackalloc 不入循环
        foreach (var m in _members)
        {
            if (m.IsMissing) continue;
            var memberEnd = m.BaseBlock + m.Info.CapacityBlocks;
            var s = Math.Max(block, m.BaseBlock);
            var e = Math.Min(block + count, memberEnd);
            if (e <= s) continue;
            // ★ V2 §1.1：按冻结位拆分——冻结段不动（快照数据），非冻结段打洞
            for (var b = s; b < e;)
            {
                if (IsBlockFrozen(b)) { b++; continue; }
                var runEnd = b;
                while (runEnd < e && !IsBlockFrozen(runEnd)) runEnd++;
                PunchRange(m, b, runEnd);
                b = runEnd;
            }
        }
        return;

        unsafe void PunchRange(CarrierMember m, ulong fromBlock, ulong toBlock)
        {
            var localOffset = (long)((fromBlock - m.BaseBlock) * (ulong)_pageSize);
            var byteLen = (long)(toBlock - fromBlock) * _pageSize;
            if (m.Carrier.IsDevice)
            {
                if (!OperatingSystem.IsLinux()) return;
                try
                {
                    var borrowed = false;
                    m.Handle.DangerousAddRef(ref borrowed);
                    try
                    {
                        range[0] = localOffset;
                        range[1] = byteLen;
                        _ = LibC.Ioctl(m.Handle.DangerousGetHandle().ToInt32(), LibC.BlkDiscard, range);
                    }
                    finally
                    {
                        if (borrowed) m.Handle.DangerousRelease();
                    }
                }
                catch
                {
                    // advisory 尽力（EINVAL = 不支持丢弃——失败记忆不值得，逐次探测成本更低）
                }
            }
            else
            {
                // V2 §1.3：文件载体打洞（qcow2 unmap 平价——PunchHole 平台分发：
                // Linux fallocate(PUNCH_HOLE|KEEP_SIZE) / Windows SetSparse+SetZeroData /
                // 不支持退零写 ZeroFilled）——advisory，结果不参与正确性
                try
                {
                    FileNative.PunchHole(m.Handle, localOffset, byteLen, _logger);
                }
                catch
                {
                    // advisory 尽力（失败仅损失空间优化——B1 零基纪律独立成立）
                }
            }
        }
    }

    /// <summary>全局块号 → 所属成员。</summary>
    private CarrierMember MemberForBlock(ulong block)
    {
        foreach (var m in _members)
            if (block < m.BaseBlock + m.Info.CapacityBlocks)
                return m;
        throw new FileIOException(IOError.IOFailure, $"块号越界：{block}", _carrier.Path, "Bitmap");
    }

    /// <summary>成员本地位图块写（页对齐整页——DIO 成员经全局路由亦可，此处避免双跳）。</summary>
    private unsafe void WriteMemberLocalAligned(CarrierMember m, long localOffset, byte[] page)
    {
        if (!m.Direct)
        {
            RandomAccess.Write(m.Handle, page, localOffset);
            return;
        }
        var buf = (byte*)NativeMemory.AlignedAlloc((nuint)_pageSize, 4096);
        try
        {
            page.AsSpan(0, _pageSize).CopyTo(new Span<byte>(buf, _pageSize));
            RandomAccess.Write(m.Handle, new Span<byte>(buf, _pageSize), localOffset);
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
        }
    }

    private void ReadBitmapFromCarrier()
    {
        // RM-04：逐成员载入本地位图 → 全局字拼接（成员基字 = BaseBlock/64）
        var totalWords = (_sb.CapacityBlocks + 63) / 64;
        _bitmapWords = new ulong[totalWords];
        foreach (var m in _members)
        {
            var memberWords = m.Info.CapacityBlocks / 64;   // 容量 64 块对齐（Format/AddCarrier 保证）
            var baseWord = m.BaseBlock / 64;
            if (m.IsMissing)
            {
                // 降级运行（v2b）：幽灵成员位图不可读——全 used 填充（保守；可达性对账以元数据区间表为准）
                for (var w = 0UL; w < memberWords; w++)
                    _bitmapWords[baseWord + w] = ulong.MaxValue;
                continue;
            }
            var memberBytes = (long)(m.Info.BitmapBlocksLocal * (ulong)_pageSize);
            var bytes = new byte[memberBytes];
            ReadMemberLocal(m, (long)(m.Info.BitmapStartLocal * (ulong)_pageSize), bytes);
            for (var w = 0UL; w < memberWords; w++)
            {
                var off = (long)(w * 8);
                if (off + 8 > bytes.LongLength) break;
                _bitmapWords[baseWord + w] = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan((int)off, 8));
            }
        }
        _freeBlocks = 0;
        foreach (var word in _bitmapWords)
            _freeBlocks += (ulong)(64 - System.Numerics.BitOperations.PopCount(word));
        var totalBits = _sb.CapacityBlocks;
        var usedBeyond = totalWords * 64 - totalBits;
        if (usedBeyond > 0) _freeBlocks -= usedBeyond;   // 尾部 padding 位不计空闲
        _dirtyBitmapWords.Clear();   // 盘上版本重新成为权威——增量基线复位
    }
}
