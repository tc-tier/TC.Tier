namespace TC.Tier.Runtime.Structures.ProbingIndex;

/// <summary>
/// HashIndex 扩容 partial——GrowIndex（全条带锁静默收集 + 纯函数式建新代 + 单引用原子发布）
/// 与条目数/内存占用估算。
/// </summary>
public partial class HashIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>
    /// ★ 扩容（容量自适应的活器官——装载超 <see cref="GrowthLoadFactor"/> 由 Insert 触发）：表翻倍。
    /// <para>★ <b>全条带锁静默收集</b>（W0 换代竞态终案）：按序持有全部写条带锁——并发写者
    /// （Insert/Delete）全部阻塞在条带锁上，收集窗内零落位 → <b>零漏收</b>（代门闩协议的
    /// 漏收窗在此结构性消除）；收集+单引用发布后放锁，写者重读新代继续。
    /// 写者最坏等待 = 收集时长 O(表)——增长为翻倍序列低频事件，可接受。</para>
    /// <para>★ 纯函数式构建：新表+新溢出池全新分配（旧代零扰动）；并发读者持旧代引用继续
    /// 一致探测（<see cref="HashIndex{TKey}.Find"/> 的换代校验保证删除可见性）——无需 epoch 排水。</para>
    /// <para>★ rehash 逐条 TryGetKey（tag 14 位不足以重建 64 位 hash）——重放/增长期地址序近邻，
    /// Ring 冷页缓存（≥数据页数时全热）承接；均摊成本 O(1)/插（翻倍序列总 rehash ≤ 2×条目数）。</para>
    /// </summary>
    public override void GrowIndex()
    {
        lock (_growLock)
        {
            var oldTable = Volatile.Read(ref _table);
            // ★ 单飞重判断：等锁期间他人可能已完成增长——装载已降则跳过（本代捕获于持锁后，无交错）
            if (Volatile.Read(ref _entryCount) <= oldTable.Size * GrowthLoadFactor)
                return;

            // ★ 静默收集窗：按序持有全部条带写锁（写者全部阻塞于此；单飞锁防并发 Grow）
            for (int i = 0; i < SlotLockCount; i++)
                Monitor.Enter(_slotLocks[i]);
            try
            {
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
                            var entry = ReadSlotStable(ref slots[s]);
                            if (HashEntry.GetState(entry) == HashEntry.Occupied)
                                collected.Add(entry);
                        }
                        var overflowPtr = ReadSlotStable(ref slots[7]);
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

                Volatile.Write(ref _table, newTable);   // ★ 单引用原子发布（表+池同代对）
            }
            finally
            {
                for (int i = SlotLockCount - 1; i >= 0; i--)
                    Monitor.Exit(_slotLocks[i]);
            }
        }
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
        var ofbIndex = AllocateOverflow((int)bucketIndex, newTable);
        newTable.OverflowPool[ofbIndex].AsSpan()[0] = entry;
    }

    /// <summary>条目数（写者维护的 O(1) 计数）。</summary>
    public override long EntryCount => Volatile.Read(ref _entryCount);

    /// <summary>索引内存占用估算（字节）——当前代表桶区 + 溢出池（128B/桶 × 桶数）。</summary>
    public override long IndexSize => _table.Size * 128L + _table.OverflowPool.Length * 128L;
}
