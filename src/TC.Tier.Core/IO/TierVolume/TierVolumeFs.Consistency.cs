using System.IO.Hashing;

namespace TC.Tier.Core.IO.TierVolume;

/// <summary>
/// 自持一致性 partial（§4.1）——CoW 元数据提交 + superblock 原子翻转 = 断电恢复底线。
/// <para>★ 全量镜像检查点形态：元数据序列化为镜像 → 新块落盘（旧块不动=CoW）→ 位图落盘 →
///   备份侧翻转（代数+1）→ 主侧翻转。单一原子提交点 = superblock 代数。</para>
/// <para>★ 提交序（★不变量）：数据先于元数据（写直通天然满足）、元数据先于翻转。</para>
/// <para>★ 恢复：取 CRC 有效且代数最高的一份 superblock → 载入其镜像 → dirty 则可达性对账重写位图
///   （孤儿回收——未提交的分配消失，★位图=可达集恢复）→ 可写继续。</para>
/// <para>★ v1 实现注记：镜像整段连续分配（≤8 run 兜底未用）；旧镜像块翻转后释放（位图惰性重写）。</para>
/// </summary>
public sealed partial class TierVolumeFs
{
    /// <summary>元数据提交（元数据脏时）——镜像写入 + 双侧翻转。fs 锁内调用。
    /// 日志模式（raw-journal §5）：兼作检查点——CkptLsn 前进 + 区代数复位 + head=0 随翻转原子生效。
    /// <paramref name="snapshotAttach"/>：V2 §1.1 快照捕获挂接——翻转前分配冻结位图区 + 落区 +
    /// 条目绑定（镜像 = 本次新镜像，捕获 LSN = 本次 CkptLsn）——快照随检查点原子生效。</summary>
    internal void CommitMetadata(SnapshotEntry? snapshotAttach = null)
    {
        if (_readOnly)
            throw new FileIOException(IOError.ReadOnlyVolume,
                "只读卷不接受提交（ReadOnlyVolume 语义——dirty 降级形态或显式只读打开，§4.1）", null, "Flush");

        if (_journalOn && _pendingRecords.Count > 0)
            JournalCommit();   // ★不变量 W3：检查点切割点之前的记录必须已提交（CkptLsn 覆盖完整性）

        // 提交序（★不变量）不变：数据 → 元数据 → 翻转。屏障合并（RM-17）：脏页/镜像/位图/备份侧
        // 连续写入后一次 fsync（屏障 #1——"数据先于元数据"在同一屏障内成立），主侧翻转后一次
        // fsync（屏障 #2——原子提交点）。5 次设备屏障 → 2 次，崩溃语义不变：
        // 翻转前崩溃 = 旧已提交版本完整（dirty 恢复对账兜底）；翻转后崩溃 = 新版本完整。
        FlushDirtyPages(sync: false);   // 脏页排干入 OS 缓存——持久化由屏障 #1 统一兜底

        // 日志检查点字段（随本次翻转原子生效——翻转前崩溃 = 老代数日志可重放；后 = 新代数 head=0）
        if (_journalOn)
        {
            _journalGen++;
            _journalHead = 0;
            _sb.JournalGeneration = _journalGen;
            _sb.JournalState = 1;
            _sb.JournalCkptLsn = _lsn;   // 发射上界（W2：吸收在途快照——内存效果已在镜像，重放跳过、无双放）
            _sb.JournalHeadLsn = _lsn;
        }

        var image = SerializeMetadata();
        var imageSpan = image.Buffer.AsSpan(0, image.LengthBytes);
        var imageCrc = Crc32.HashToUInt32(imageSpan);
        var blocks = (uint)((imageSpan.Length + _pageSize - 1) / _pageSize);
        var oldRuns = _sb.ImageRuns;

        // ① 新镜像块分配（CoW：旧镜像块不动）+ 落盘（不刷——屏障 #1 兜底）
        var primaryLimit = _members.Length > 0
            ? _members[0].BaseBlock + _members[0].Info.CapacityBlocks
            : (ulong?)null;
        var phys = AllocateBlocks(blocks, "MetadataImage", primaryLimit);   // 锚定主载体（降级运行前提——v2b）
        var padded = new byte[(long)blocks * _pageSize];
        imageSpan.CopyTo(padded);
        WriteCarrier((long)(phys * (ulong)_pageSize), padded);

        _sb.ImageRuns = [(phys, blocks)];
        _sb.ImageLength = (ulong)imageSpan.Length;
        _sb.ImageCrc = imageCrc;
        _sb.Generation++;
        _sb.Flags = (ushort)(_sb.Flags & ~FlagClean);   // 提交中即 dirty（clean 仅在关闭协议置位）

        // ② 位图增量落盘（新镜像已占用、旧镜像仍占用——翻转前不释放；只写脏字所在块，不刷）
        TryFreeRetiredLocked();   // D1b：提交前推进安全批次回收（位图落盘即最新可达事实）

        // ★ V2 §1.1 快照捕获挂接（在 TryFreeRetiredLocked 之后——冻结最小集：已回收安全批次
        //   不在冻结内，避免过度钉块；在 WriteBitmapToCarrier 之前——冻结区占用随本次位图落盘）
        if (snapshotAttach is { } snap)
        {
            while (true)
            {
                var cap0 = _sb.CapacityBlocks;
                var bitmapBytes = (cap0 + 7) / 8;
                var bitmapBlocks = (bitmapBytes + (ulong)_pageSize - 1) / (ulong)_pageSize;
                var regionStart = AllocateBlocks((uint)bitmapBlocks, "SnapshotBitmap");
                if (_sb.CapacityBlocks == cap0)
                {
                    WriteFrozenBitmapRegion(regionStart, bitmapBlocks, _bitmapWords);   // 冻结位图落区（屏障 #1 覆盖）
                    snap.BitmapStart = regionStart;
                    snap.BitmapBlocks = bitmapBlocks;
                    break;
                }
                // 自动扩容发生在分配内（位图重排 + 容量变更）——本区按旧容量尺寸作废，按新容量重来
                MarkBlocks(regionStart, (uint)bitmapBlocks, used: false);
            }
            snap.CaptureLsn = _lsn;   // 捕获 = 本次检查点（CkptLsn）
            snap.ImageRuns = [.. _sb.ImageRuns];
            snap.ImageLength = _sb.ImageLength;
            snap.ImageCrc = _sb.ImageCrc;
            snap.FrozenWords = (ulong[])_bitmapWords.Clone();
            _sb.Snapshots.Add(snap);
        }
        WriteBitmapToCarrier();

        // ③ 备份侧翻转 → 屏障 #1（以上全部落盘）→ 主侧翻转 → 屏障 #2（原子提交点 = 代数）
        var buffer = new byte[Sb.TotalSize];
        EncodeSuperblock(buffer, _sb);
        WriteCarrier(SuperblockBackupOffset, buffer);
        FlushCarrier();
        WriteCarrier(SuperblockPrimaryOffset, buffer);
        FlushCarrier();

        // ④ 旧镜像块释放（内存位图；盘上位图下次提交重写——恢复对账兜底）。
        // ★ V2 §1.1：快照引用的旧镜像不释放（CoW 保留——快照读面的元数据源）；其余照常。
        foreach (var (start, count) in oldRuns)
            if (start != phys && !_sb.Snapshots.Any(s => s.ImageRuns.Any(r => RunsOverlap(r, start, count))))
                MarkBlocks(start, count, used: false);
        MetadataDirty = false;
        _timestampsDirty = false;   // 镜像含当前时间戳——随本提交顺带收口（lazytime）
        // ★ V2 §1.2：检查点吸收窗口内全部写入——增量窗口复位（脏块集清零、完整性恢复）
        if (_deltaDirtyBlocks is { } deltaSet)
            lock (_deltaDirtyGate)
                deltaSet.Clear();
        _deltaDirtyComplete = true;
    }

    /// <summary>双侧 superblock 轮写（先备后主，各带 CRC 与代数——固定字节偏移 0/4096）。</summary>
    private void RotateSuperblocks()
    {
        var buffer = new byte[Sb.TotalSize];
        EncodeSuperblock(buffer, _sb);
        WriteCarrier(SuperblockBackupOffset, buffer);
        FlushCarrier();
        WriteCarrier(SuperblockPrimaryOffset, buffer);
        FlushCarrier();
    }

    /// <summary>
    /// 载入与恢复：双 superblock 取 CRC 有效且代数最高者 → 读镜像（CRC 校验）→ 载入元数据 →
    /// dirty 则可达性对账（★位图=可达集：superblock 块 + 位图块 + 日志保留 + 全部区间物理块）重写位图。
    /// </summary>
    /// <summary>superblock 采纳（成员装配前——仅读主载体 8K 头，路由安全）。</summary>
    private (SuperblockData Winner, string Side) DecodeWinner()
    {
        var blockBuffer = new byte[Sb.TotalSize];
        var primary = TryDecodeSuperblock(SuperblockPrimaryOffset, blockBuffer);
        var backup = TryDecodeSuperblock(SuperblockBackupOffset, blockBuffer);
        return (primary, backup) switch
        {
            (null, null) => throw new FileIOException(IOError.IOFailure,
                "双 superblock 皆 CRC 失效——卷不可开（显式只读请求也无效，§4.1 唯一不可修复场景）",
                _carrier.Path, "Open"),
            (null, var b) => (b!, "backup"),
            (var p, null) => (p!, "primary"),
            var (p, b) => p!.Generation >= b!.Generation ? (p, "primary") : (b, "backup"),
        };
    }

    private void LoadAndRecover()
    {
        var (winner, winnerSide) = DecodeWinner();
        AdoptWinner(winner);
        ContinueLoad(winner, winnerSide);
    }

    /// <summary>采纳胜者 + 成员装配（多载体卷在 Open(carriers[]) 路径于装配后直接 ContinueLoad）。</summary>
    private void AdoptWinner(SuperblockData winner)
    {
        _sb = winner;
        _pageSize = (int)winner.BlockSize;
        _autoExpand = (winner.Flags & FlagAutoExpand) != 0;   // 自动扩容是卷属性——Open 继承（quota 收紧另计）
        if (_members.Length == 1 && winner.Members.Count == 1)
            _members[0].Info = winner.Members[0];   // 单载体：成员 0 信息补全
        JournalInitFromSuperblock();   // 日志运行态就位（重放需要区参数——clean 卷只初始化）
    }

    private void ContinueLoad(SuperblockData winner, string winnerSide)
    {
        if (!_snapshotMount)
            LoadSnapshotFrozenSets();   // ★ V2 §1.1：冻结并集载入（成员装配后——冻结区可跨成员；清洁/脏恢复路径共用）
        ReadBitmapFromCarrier();

        // 镜像载入（CRC 违约 = 卷损坏——拒开）
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
                $"元数据镜像 CRC 校验失败（{winnerSide} 侧，gen={winner.Generation}）", _carrier.Path, "Open");
        LoadMetadata(image);

        var wasClean = (winner.Flags & FlagClean) != 0;
        if (wasClean)
            return;   // clean 快路径——位图信任（跳过对账）

        // dirty → 孤儿回收：可达性对账 + 位图重写（恢复后立即可写——§4.1）
        ReconcileBitmapToReachable();
        // 日志重放（raw-journal §6）：对账后叠加日志尾（物理事实来自记录——零载体写）；
        // 只读同样执行（否则读者看到检查点旧态）
        if (_journalOn)
        {
            JournalReplay();
            _deltaDirtyComplete = false;   // ★ V2 §1.2：重放窗口的原地覆写不可重构——增量导出拒至下次检查点
        }
        if (_readOnly || _degraded) return;   // 降级 = 零写（位图重写/翻转跳过——内存态即服务形态）

        // 写意图打开：置 dirty 已隐含（winner dirty）——确保双侧 superblock 反映 dirty 后再服务
        if (!wasClean)
        {
            RotateSuperblocks();
        }
    }

    /// <summary>★位图=可达集 对账：重算可达块集，重写内存位图并落盘（dirty 恢复路径）。
    /// V2 §1.1：快照冻结块并入可达（<see cref="IsBlockFrozen"/> 内联判据——冻结块在恢复后保持 used）。</summary>
    private void ReconcileBitmapToReachable()
    {
        var reachable = BuildReachableSet(includeSnapFrozen: false);

        var mismatch = 0UL;
        for (var b = 0UL; b < _sb.CapacityBlocks; b++)
        {
            var shouldUse = reachable.Contains(b) || IsBlockFrozen(b);
            if (shouldUse != IsBlockUsed(b))
            {
                MarkBlocks(b, 1, shouldUse);
                mismatch++;
            }
        }
        if (mismatch > 0)
        {
            _logger?.LogWarning("位图对账：回收 {Count} 个孤儿/泄漏块（dirty 恢复，§4.1）", mismatch);
            if (!_readOnly && !_degraded) WriteBitmapToCarrier();
        }
    }

    /// <summary>clean 关闭协议（§4.1）：提交 → 置 clean → 双侧轮写。（仅 Dispose 在置 _disposed 后调用——此处不再查该标志）</summary>
    private void CleanShutdown()
    {
        if (_readOnly) return;
        lock (MetadataLock)
        {
            if (_journalOn)
                JournalCommit();   // 待屏障记录先提交（镜像含其效果——记录落不落盘均一致，落盘更整洁）
            TryFreeRetiredLocked();   // D1b：关卷前尽数回收安全批次（残留批次入孤儿回收——下次打开对账兜底）
            if (MetadataDirty || _timestampsDirty) CommitMetadata();   // lazytime 收口：关卷前时间戳一次付清
            else FlushDirtyPages(sync: true);   // RM-40：非日志卷数据-only 脏也须排干+屏障（旧实现 clean 关闭静默丢数据——脏页从未写出即释放）
            _sb.Flags |= FlagClean;
            RotateSuperblocks();
        }
    }

    /// <summary>格式化前检测：载体上已有有效 superblock（RAW1）或已是某卷成员（RAWC 成员头）→ AlreadyExists
    /// （显式格式化语义——格式化覆盖另一卷的成员 = 毁卷脚枪，与 AddCarrier 同门拒绝）。</summary>
    private void ThrowIfAlreadyFormatted()
    {
        try
        {
            // 设备载体 fstat 长度恒 0（st_size）——长度前置检查仅对文件载体有意义；设备直接探测 magic
            //（RM-05 loop 实测：旧前置检查使设备 Format 静默重格——数据丢失脚枪）
            if (!_carrier.IsDevice && RandomAccess.GetLength(_members[0].Handle) < 8) return;
            var probe = new byte[8];
            ReadCarrierExactly(0, probe);   // O_DIRECT 载体下短读经弹跳通道（对齐纪律）
            if (probe.AsSpan(0, 4).SequenceEqual("RAW1"u8))
                throw new FileIOException(IOError.AlreadyExists,
                    $"载体已格式化：{_carrier.Path}（格式化显式非幂等——重格须先删除）", null, "Format");
            if (probe.AsSpan(0, 4).SequenceEqual("RAWC"u8))
                throw new FileIOException(IOError.AlreadyExists,
                    $"载体已是 TC 卷成员（RAWC 成员头）：{_carrier.Path}——格式化会摧毁所属卷的完整性（§3.8）", null, "Format");
        }
        catch (IOException ex) when (ex is not FileIOException)
        {
            throw new FileIOException(IOError.IOFailure, $"格式化前检测失败：{ex.Message}", null, "Format");
        }
    }

    private SuperblockData? TryDecodeSuperblock(long offset, byte[] buffer)
    {
        try
        {
            ReadCarrierExactly(offset, buffer.AsSpan(0, Sb.TotalSize));
            return DecodeSuperblock(buffer.AsSpan(0, Sb.TotalSize));
        }
        catch (FileIOException ex) when (ex.Error is IOError.IOFailure or IOError.NotFound)
        {
            return null;   // 此侧物理损坏（CRC/magic）——可换侧采纳
        }
        // 版本/未知 flags/日志字段非零 = 确定性拒开（Unsupported）——直接传播（不可换侧掩盖）
    }
}
