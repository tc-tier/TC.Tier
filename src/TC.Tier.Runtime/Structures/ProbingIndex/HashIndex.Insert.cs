using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.ProbingIndex;

public partial class HashIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    public override LogicalAddress Insert(TKey key, LogicalAddress valueAddress, LogicalAddress beginAddress)
    {
        var hash = ComputeHash(key);
        var tag = ComputeTag(key);

        _epoch.Resume();
        try
        {
            // ★ 增长在<b>本次插入之前</b>触发：此刻全部既有条目都已返回给调用方（调用方对
            //   resolver/ring 的注册已完成）——rehash 逐条 TryGetKey 必可解析。若在插入后触发，
            //   刚落位的条目可能尚未注册（Insert 返回前的窗口），rehash 会静默丢弃它。
            if (_entryCount > _table.Size * GrowthLoadFactor)
                GrowIndex();

            var inserted = InsertIntoTable(hash, tag, key, valueAddress, beginAddress, _table);
            if (inserted.NewEntry)
                _entryCount++;
            return inserted.Entry;
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>插入结果（Entry=落位/覆写条目；NewEntry=是否新条目——增长计数只认新条目）。</summary>
    private readonly record struct InsertResult(LogicalAddress Entry, bool NewEntry);

    private InsertResult InsertIntoTable(ulong hash, ushort tag, TKey key,
        LogicalAddress valueAddress, LogicalAddress beginAddress, InternalHashTable table)
    {
        var ofbPool = table.OverflowPool;
        var ofbCap = ofbPool.Length;
        var bucketIndex = hash & table.SizeMask;
        ref var bucketRef = ref table.TableRaw[bucketIndex];
        var slots = bucketRef.AsSpan();
        int versionCounter = 0;

        while (true)
        {
            for (int i = 0; i < MaxOverflowSlots; i++)
            {
                var current = slots[i];

                if (HashEntry.GetTag(current) == tag && HashEntry.GetState(current) == HashEntry.Occupied)
                {
                    // ★ 判等闭环：tag 匹配后读回真 key 校验。
                    //   key 相等 → 同 key 覆写（CAS 更新 value 地址）；
                    //   key 不同（tag 冲突）→ 不覆盖，落到下面空位查找建新条目（杜绝静默覆盖）。
                    if (KeyResolver!.TryGetKey(current, out var existingKey)
                        && KeyComparer.Equals(existingKey, key))
                    {
                        var newEntry = HashEntry.CreateOccupied(valueAddress.SegId, valueAddress.Offset,
                            tag, HashEntry.NextVersion(current));
                        if (CasSlot(ref bucketRef.AsSpan()[i], current, newEntry))
                            return new InsertResult(newEntry, NewEntry: false);
                        continue;
                    }
                    // tag 冲突但 key 不同 → 继续找空位建新条目
                }

                if (HashEntry.IsEmpty(current) || current.CompareTo(beginAddress) < 0)
                {
                    var tentative = HashEntry.CreateTentative(valueAddress.SegId, valueAddress.Offset,
                        tag, versionCounter++);
                    if (CasSlot(ref bucketRef.AsSpan()[i], current, tentative))
                    {
                        var occupied = HashEntry.CreateOccupied(valueAddress.SegId, valueAddress.Offset,
                            tag, HashEntry.NextVersion(tentative));
                        CasSlot(ref bucketRef.AsSpan()[i], tentative, occupied);
                        return new InsertResult(occupied, NewEntry: true);
                    }
                }
            }

            bucketRef = ref AllocateOverflow(ref bucketRef, table);
            slots = bucketRef.AsSpan();
        }
    }

    /// <summary>桶满分配溢出桶（池 bump 在表代内——与表同代共存亡）。</summary>
    private ref HashBucket AllocateOverflow(ref HashBucket sourceBucket, InternalHashTable table)
    {
        var ofbPool = table.OverflowPool;
        var ofbCap = ofbPool.Length;
        lock (_overflowLock)
        {
            if (table.OverflowCount >= ofbCap)
                throw new InvalidOperationException("Overflow pool exhausted");

            int index = table.OverflowCount++;

            ref var ovSlot = ref sourceBucket.AsSpan()[7];
            if (HashEntry.IsEmpty(ovSlot))
            {
                ovSlot = new LogicalAddress(1, index);
            }
            else
            {
                var chain = ovSlot;
                while (true)
                {
                    var chainIdx = (int)chain.Offset;
                    ref var chainBucket = ref ofbPool[chainIdx];
                    ref var chainOv = ref chainBucket.AsSpan()[7];
                    if (HashEntry.IsEmpty(chainOv))
                    {
                        chainOv = new LogicalAddress(1, index);
                        break;
                    }
                    chain = chainOv;
                }
            }

            return ref ofbPool[index];
        }
    }

    public override bool Delete(TKey key)
    {
        var hash = ComputeHash(key);
        var tag = ComputeTag(key);

        _epoch.Resume();
        try
        {
            var table = _table;
            var ofbPool = table.OverflowPool;
            var ofbCap = ofbPool.Length;
            var bucketIndex = hash & table.SizeMask;

            ref var bucket = ref table.TableRaw[bucketIndex];
            var slots = bucket.AsSpan();

            while (true)
            {
                for (int i = 0; i < MaxOverflowSlots; i++)
                {
                    var entry = slots[i];
                    if (HashEntry.GetTag(entry) == tag && HashEntry.GetState(entry) == HashEntry.Occupied)
                    {
                        // ★ 判等闭环：tag 匹配后读回真 key 校验，避免误删同 tag 异 key 的 entry。
                        if (!(KeyResolver!.TryGetKey(entry, out var existingKey)
                              && KeyComparer.Equals(existingKey, key)))
                            continue;   // tag 冲突，继续找

                        var removed = CasSlot(ref bucket.AsSpan()[i], entry, LogicalAddress.Empty);
                        if (removed)
                            _entryCount--;
                        return removed;
                    }
                }

                var overflowPtr = slots[7];
                if (HashEntry.IsEmpty(overflowPtr)) return false;

                var ofbIndex = (int)((uint)overflowPtr.Offset % ofbCap);
                bucket = ref ofbPool[ofbIndex];
                slots = bucket.AsSpan();
            }
        }
        finally
        {
            _epoch.Suspend();
        }
    }
}
