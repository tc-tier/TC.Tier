using System.Buffers;

namespace TC.Tier.Core.IO.Raw;

/// <summary>
/// 区间操作 partial（§3.2 三态语义的文件级投影）——截断/预分配/打洞/枚举/塌缩/插入。
/// <para>★ CollapseRange/InsertRange 全平台支持（§3.5 增强行——不依赖 OS fallocate）：
///   数据移位 = 分块读改写，性能次之、语义先行。</para>
/// </summary>
public sealed partial class RawFileSystem
{
    /// <summary>截断：收缩 = 尾部区间块粒度切分回收 + 逻辑长度回退；扩展 = 纯逻辑（零物理分配）。</summary>
    internal void TruncateEntry(Entry e, long length)
    {
        if (length < e.LogicalLength)
        {
            var bs = (long)_pageSize;
            var keep = RoundUp(length, bs);
            var newExtents = new List<Extent>();
            foreach (var x in e.Extents)
            {
                if (x.LogicalEnd <= keep)
                {
                    newExtents.Add(x);
                    continue;
                }
                if (x.LogicalStart < keep)
                {
                    var cut = keep - x.LogicalStart;
                    newExtents.Add(x with { Length = cut });
                    FreePhysical(e, keep, x.LogicalEnd, x with { Length = x.Length }, bs);
                }
                else
                {
                    FreePhysical(e, x.LogicalStart, x.LogicalEnd, x, bs);
                }
            }
            e.Extents = newExtents;
        }
        e.LogicalLength = length;
        if (_appendCursors.TryGetValue(e.Path, out var cursor))
            Interlocked.Exchange(ref cursor.Value, length);   // 追加预留权威复位
        MetadataDirty = true;
        JnlSetLength(e.Path, length);
    }

    /// <summary>预分配（fallocate 语义，§3.2）：unwritten 区间——物理预留、读零、写时转 Written。</summary>
    internal void PreallocateEntry(Entry e, long targetSize)
    {
        if (targetSize <= e.LogicalLength) return;
        var bs = (long)_pageSize;
        var start = RoundUp(e.LogicalLength, bs);
        var end = RoundUp(targetSize, bs);
        var blocks = (uint)((end - start) / bs);
        if (blocks > 0)
        {
            var phys = AllocateBlocks(blocks, "Preallocate");
            var list = new List<Extent>(e.Extents) { new Extent(start, end - start, phys, ExtentState.Unwritten) };   // CoW（RM-12）
            list.Sort((a, b) => a.LogicalStart.CompareTo(b.LogicalStart));
            e.Extents = list;
            JnlExtentAppend(e.Path, start, end - start, phys, ExtentState.Unwritten, targetSize);
        }
        e.LogicalLength = Math.Max(e.LogicalLength, targetSize);
        MetadataDirty = true;
    }

    /// <summary>打洞（§3.2）：物理回收 + 逻辑长度不动 + 洞读零——Sparse 能力位真回收。</summary>
    internal void PunchHoleEntry(Entry e, long offset, long length)
    {
        var bs = (long)_pageSize;
        var end = offset + length;
        var newExtents = new List<Extent>();
        foreach (var x in e.Extents)
        {
            if (x.LogicalEnd <= offset || x.LogicalStart >= end)
            {
                newExtents.Add(x);
                continue;
            }
            var cutStart = Math.Max(x.LogicalStart, offset);
            var cutEnd = Math.Min(x.LogicalEnd, end);
            if (x.LogicalStart < cutStart)
                newExtents.Add(x with { Length = cutStart - x.LogicalStart });
            if (cutEnd < x.LogicalEnd)
                newExtents.Add(new Extent(cutEnd, x.LogicalEnd - cutEnd,
                    x.PhysicalBlock + (ulong)((cutEnd - x.LogicalStart) / bs), x.State));
            FreePhysical(e, cutStart, cutEnd, x, bs);
        }
        e.Extents = newExtents;
        MetadataDirty = true;
        JnlPunchHole(e.Path, offset, length);
    }

    /// <summary>已分配区间（§3.2）：unwritten + written 并集（物理占用真相）。</summary>
    internal IReadOnlyCollection<(long Start, long End)> AllocatedRangesOf(Entry e)
        => e.Extents.Select(x => (x.LogicalStart, x.LogicalEnd)).ToList();

    /// <summary>
    /// CopyRange 块级快道（RM-32）：单锁 + extent 对齐块级搬运——compact/migrate 形态 2-5x。
    /// 适用判据（保守）：源/目标/长度块对齐 × 源区间全 Written 承载（洞/unwritten 回退）×
    /// 目标纯追加（覆盖既有区间回退）。判据不符返回 -1（调用方走公共逐块路径——语义不变）。
    /// 机制与 MigrateMemberData 同族：分配 dest run → 载体块级读写 → 尾追加 extent + 日志记录；
    /// 源脏页先行排干（载体读一致性——与 RemoveCarrier 前置 JournalCommit 同纪律）。
    /// </summary>
    internal long TryCopyRangeBlockLevel(Entry src, Entry dest, long srcOffset, long destOffset, long length)
    {
        var bs = (long)_pageSize;
        if (length <= 0) return 0;
        if (srcOffset % bs != 0 || destOffset % bs != 0 || length % bs != 0) return -1;
        if (destOffset != dest.LogicalLength) return -1;   // 覆盖既有区间 = 公共路径（ApplyExtentCover 语义）
        lock (MetadataLock)
        {
            // 源区间全 Written 承载判据（洞/unwritten 段回退——公共路径语义保真）
            var cover = 0L;
            foreach (var x in src.Extents)
            {
                if (x.State != ExtentState.Written) continue;
                var s = Math.Max(x.LogicalStart, srcOffset);
                var e = Math.Min(x.LogicalEnd, srcOffset + length);
                if (e > s) cover += e - s;
            }
            if (cover != length) return -1;

            FlushDirtyPages(sync: false);   // 源脏页先行排干（载体读一致性）
            var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * _pageSize);
            try
            {
                long copied = 0;
                foreach (var x in src.Extents)   // 有序区间——顺序搬运
                {
                    var s = Math.Max(x.LogicalStart, srcOffset);
                    var e = Math.Min(x.LogicalEnd, srcOffset + length);
                    for (var pos = s; pos < e;)
                    {
                        var take = (int)Math.Min(buf.Length, e - pos);
                        var srcPhys = (long)(x.PhysicalBlock * (ulong)_pageSize) + (pos - x.LogicalStart);
                        ReadCarrierExactly(srcPhys, buf.AsSpan(0, take));
                        var blocks = (uint)(take / bs);
                        var phys = AllocateBlocks(blocks, "CopyRange");
                        WriteCarrier((long)(phys * (ulong)_pageSize), buf.AsSpan(0, take));
                        var destLogical = destOffset + copied;
                        dest.Extents = new List<Extent>(dest.Extents) { new Extent(destLogical, take, phys, ExtentState.Written) };   // CoW（RM-12）
                        dest.LogicalLength = Math.Max(dest.LogicalLength, destLogical + take);
                        JnlExtentAppend(dest.Path, destLogical, take, phys, ExtentState.Written, dest.LogicalLength);
                        copied += take;
                        pos += take;
                    }
                }
                TouchModified(dest);
                MetadataDirty = true;
                return copied;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buf);
            }
        }
    }

    /// <summary>区间重定位手术（RM-04 v2a 迁移式缩容——在线/重放共用）：
    /// 逻辑区间 [oldStart, oldStart+oldLen) 的承载替换为 newRuns（物理事实来自调用方——
    /// 在线 = 迁移分配器；重放 = 记录载荷）。旧物理块不释放（迁移源成员即将整体摘除）。</summary>
    internal void ApplyExtentRelocate(Entry e, long oldStart, long oldLen, List<(ulong Phys, long Len)> newRuns)
    {
        var oldEnd = oldStart + oldLen;
        var replacement = new List<Extent>();
        var state = e.Extents.FirstOrDefault(x => x.LogicalStart <= oldStart && oldStart < x.LogicalEnd).State;
        var logicalCursor = oldStart;
        foreach (var (phys, len) in newRuns)
        {
            replacement.Add(new Extent(logicalCursor, len, phys, state));
            logicalCursor += len;
        }
        // 拼接：旧区间前后的原区间保留 + 替换段（表序 = 逻辑序）
        var merged = new List<Extent>();
        foreach (var x in e.Extents)
        {
            if (x.LogicalEnd <= oldStart || x.LogicalStart >= oldEnd)
            {
                merged.Add(x);
                continue;
            }
            if (x.LogicalStart < oldStart)
                merged.Add(x with { Length = oldStart - x.LogicalStart });
            merged.AddRange(replacement.Where(rx => rx.LogicalStart < oldEnd && rx.LogicalEnd > oldStart));
            if (x.LogicalEnd > oldEnd)
                merged.Add(new Extent(oldEnd, x.LogicalEnd - oldEnd,
                    x.PhysicalBlock + (ulong)((oldEnd - x.LogicalStart) / _pageSize), x.State));
        }
        merged.Sort((a, b) => a.LogicalStart.CompareTo(b.LogicalStart));
        e.Extents = merged;
        MetadataDirty = true;
    }

    /// <summary>
    /// 物化整理（RM-08——碎片文件 Map 前置）：重写为 [0, len) 单连续 Written 区间。
    /// 洞/unwritten 归零写入（读语义等价——AllocatedSize 增长为代价）；日志经 ExtentCover 单记录
    /// （重放侧相交释放从在档区间推导——多旧区间一并回收）。
    /// D8：目标 run 成员内分配（跨成员 extent 不可 MMF——单成员落点是 Map 补救路径的真实化）。
    /// </summary>
    internal void DefragmentEntry(Entry e)
    {
        var bs = (long)_pageSize;
        var roundedLen = RoundUp(e.LogicalLength, bs);
        if (roundedLen == 0) return;   // 空文件无从映射（Map 前置长度检查已挡）
        var blocks = (uint)(roundedLen / bs);

        // D8：逐成员尝试连续分配（数据区 [bitmap 尾, 成员尾)）——任一成员容纳即落
        ulong? phys = null;
        foreach (var m in _members)
        {
            var mBase = m.BaseBlock + m.Info.BitmapStartLocal + m.Info.BitmapBlocksLocal;
            var mEnd = m.BaseBlock + m.Info.CapacityBlocks;
            if (mEnd < mBase + blocks) continue;
            try
            {
                phys = AllocateBlocks(blocks, "Defrag", maxBlock: mEnd, minBlock: mBase);
                break;
            }
            catch (FileIOException ex) when (ex.Error == IOError.DiskFull)
            {
                // 本成员无连续段——试下一成员
            }
        }
        if (phys is null)
            throw new FileIOException(IOError.DiskFull,
                $"无单成员可容纳 {blocks} 连续块（物化整理须落单成员——Map 前提；文件超过任一成员容量或成员碎片化）",
                e.Path, "Defrag");
        var physBlock = phys.Value;

        // 逐块搬运：读侧经在档区间（洞读零），写侧直落新 run（区间结构最后一步切换——中途崩溃 = 旧版完整 + 新 run 孤儿回收）
        const int chunkBlocks = 64;   // 256KB
        var buf = ArrayPool<byte>.Shared.Rent(chunkBlocks * _pageSize);
        try
        {
            long done = 0;
            while (done < roundedLen)
            {
                var take = (int)Math.Min(roundedLen - done, (long)chunkBlocks * _pageSize);
                ReadData(e, done, buf.AsSpan(0, take));   // 旧视图（含洞读零）
                WriteCarrier((long)(physBlock * (ulong)_pageSize) + done, buf.AsSpan(0, take));
                done += take;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }

        // 切换：释放旧区间（D1b epoch 延迟回收 + 退出缓存——RM-12）→ 单区间替换 → 日志
        foreach (var x in e.Extents)
        {
            var oldBlocks = (uint)((x.Length + _pageSize - 1) / _pageSize);
            RetireBlocks(x.PhysicalBlock, oldBlocks);
            InvalidateCacheBlocks(x.PhysicalBlock, oldBlocks);
        }
        e.Extents = [new Extent(0, roundedLen, physBlock, ExtentState.Written)];
        MetadataDirty = true;
        JnlExtentCover(e.Path, 0, roundedLen, physBlock, e.LogicalLength);
    }

    /// <summary>物理占用字节（AllocatedSize——按区间块数计）。</summary>
    internal long AllocatedSizeOf(Entry e)
    {
        long bytes = 0;
        foreach (var x in e.Extents)
            bytes += (x.Length + _pageSize - 1) / _pageSize * _pageSize;
        return bytes;
    }

    /// <summary>区间塌缩（§3.5 增强行——全平台）：[offset, offset+length) 移除、后续前移、长度回退。</summary>
    internal void CollapseEntry(Entry e, long offset, long length)
    {
        var moveFrom = offset + length;
        var moveLen = e.LogicalLength - moveFrom;
        if (moveLen > 0) ShiftData(e, moveFrom, offset, moveLen);
        TruncateEntry(e, e.LogicalLength - length);
    }

    /// <summary>区间插入（全平台）：offset 处插入零区、后续后移、长度增长。</summary>
    internal void InsertEntryRange(Entry e, long offset, long length)
    {
        var newLength = e.LogicalLength + length;
        var moveLen = e.LogicalLength - offset;
        if (moveLen > 0) ShiftData(e, offset, offset + length, moveLen);
        PunchHoleEntry(e, offset, length);   // 插入区清零（移位保留的外侧块含旧数据——打洞归零，§3.2）
        e.LogicalLength = newLength;   // ShiftData 的 WriteData 已按 Max 抬至此——幂等设定（防双重累加）
        if (_appendCursors.TryGetValue(e.Path, out var cursor))
            Interlocked.Exchange(ref cursor.Value, e.LogicalLength);
        MetadataDirty = true;
        JnlSetLength(e.Path, newLength);
    }

    /// <summary>数据移位（从后往前搬运——目标区与源区重叠安全）。</summary>
    private void ShiftData(Entry e, long from, long to, long length)
    {
        var chunk = 1 << 20;
        var buf = new byte[chunk];
        long moved = 0;
        if (to > from)
        {
            // 后移：从尾往前
            for (var pos = length; pos > 0; )
            {
                var take = (int)Math.Min(chunk, pos);
                var src = from + pos - take;
                var got = ReadData(e, src, buf.AsSpan(0, take));
                WriteData(e, to + pos - take, buf.AsSpan(0, got));
                pos -= take;
                moved += got;
            }
        }
        else
        {
            for (var pos = 0L; pos < length; pos += chunk)
            {
                var take = (int)Math.Min(chunk, length - pos);
                var got = ReadData(e, from + pos, buf.AsSpan(0, take));
                WriteData(e, to + pos, buf.AsSpan(0, got));
                moved += got;
            }
        }
    }
}
