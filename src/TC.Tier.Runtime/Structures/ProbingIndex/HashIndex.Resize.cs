namespace TC.Tier.Runtime.Structures.ProbingIndex;

public partial class HashIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>
    /// ★ 扩容（容量自适应的活器官——装载超 <see cref="GrowthLoadFactor"/> 由 Insert 触发）：表翻倍。
    /// <para>★ 纯函数式构建：新表+新溢出池全新分配（旧代零扰动），收集旧代全部 Occupied entry
    /// （主表 slot 0..6 + 每条 overflow 链）→ 经 KeyResolver 读回真 key 重算 hash 按新 mask 落位 →
    /// <c>_table</c> 单引用原子发布。并发读者持旧代引用继续一致探测（条目仍真、仅缺发布后新插，
    /// stale-but-valid——与 BTree 根晋升缓存全清同级容忍），旧代归 GC——<b>无需 epoch 排水</b>
    /// （无内存回收竞态：溢出指针只在同代内解引用）。</para>
    /// <para>★ rehash 逐条 TryGetKey（tag 14 位不足以重建 64 位 hash）——重放/增长期地址序近邻，
    /// Ring 冷页缓存（≥数据页数时全热）承接；均摊成本 O(1)/插（翻倍序列总 rehash ≤ 2×条目数）。</para>
    /// </summary>
    public override void GrowIndex()
    {
        var oldTable = _table;
        var newSize = oldTable.Size * 2;
        var newTable = BuildTable(newSize, overflowCapacity: Math.Max(1024, (int)(newSize / 2)));

        var collected = new List<LogicalAddress>();
        var ofbPool = oldTable.OverflowPool;
        var ofbCap = ofbPool.Length;
        for (long i = 0; i < oldTable.Size; i++)
        {
            var current = oldTable.TableRaw[i];
            while (true)
            {
                var slots = current.AsSpan();
                for (int s = 0; s < MaxOverflowSlots; s++)
                {
                    var entry = slots[s];
                    if (HashEntry.GetState(entry) == HashEntry.Occupied)
                        collected.Add(entry);
                }
                var overflowPtr = slots[7];
                if (HashEntry.IsEmpty(overflowPtr)) break;
                int ofbIndex = (int)((uint)overflowPtr.Offset % ofbCap);
                current = ofbPool[ofbIndex];
            }
        }

        foreach (var entry in collected)
        {
            if (!KeyResolver!.TryGetKey(entry, out var key)) continue;
            ulong hash = ComputeHash(key);
            long newBucket = (long)(hash & newTable.SizeMask);
            InsertEntryIntoNewTable(newTable, newBucket, entry);
        }

        _table = newTable;   // ★ 单引用原子发布（表+池同代对）
    }

    /// <summary>把 entry 落到新表指定 bucket 的首个空 slot；slot 0..6 满则分配新代 overflow。</summary>
    private void InsertEntryIntoNewTable(InternalHashTable newTable, long bucketIndex, LogicalAddress entry)
    {
        ref var bucket = ref newTable.TableRaw[bucketIndex];
        var slots = bucket.AsSpan();
        for (int i = 0; i < MaxOverflowSlots; i++)
        {
            if (HashEntry.IsEmpty(slots[i]))
            {
                slots[i] = entry;
                return;
            }
        }
        // 主桶满 → 新代 overflow 池（全新 bump，从 0 重新分配）
        bucket = ref AllocateOverflow(ref bucket, newTable);
        bucket.AsSpan()[0] = entry;
    }

    /// <summary>条目数（写者维护的 O(1) 计数——旧形全表扫描）。</summary>
    public override long EntryCount => _entryCount;

    /// <summary>索引内存占用估算（字节）——当前代表桶区 + 溢出池（128B/桶 × 桶数）。</summary>
    public override long IndexSize => _table.Size * 128L + _table.OverflowPool.Length * 128L;
}
