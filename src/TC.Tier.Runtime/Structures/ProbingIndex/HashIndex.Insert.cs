using System.Runtime.CompilerServices;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.ProbingIndex;

/// <summary>
/// HashIndex 插入删除 partial——Insert（两遍扫描判等/落位 + CAS 单步写入 + 落位校验重试）
/// 与 Delete（判等闭环 CAS 清槽）。
/// </summary>
public partial class HashIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>
    /// 插入条目（key → valueAddress；tag 命中后经 KeyResolver 读回真 key 判等，同 key 覆写 value 不增计数）。
    /// epoch 读保护内完成。
    /// <para>★ 增长在<b>本次插入之前</b>触发（装载超阈值）——此刻全部既有条目已返回调用方且注册完成，
    ///   rehash 逐条 TryGetKey 必可解析；若在插入后触发，刚落位条目可能尚未注册而被 rehash 静默丢弃。</para>
    /// <para>★ 并发协议（W0 收口——审计 #173/#237/#268/#240/#274 家族）：
    ///   <b>条带锁写互斥</b>（同 key 同 hash → 同条带锁——判等扫描与落位/覆写/删除在锁内全序化，
    ///   同 key 单实体可证、无活锁；读路径 Find/Grow 不持锁，稳定读防撕裂）+
    ///   <b>单步 CAS 落位</b>（废 Tentative 两阶段——旧协议第二跳 CAS 失败被丢弃，条目"插入成功但永远查不到"；
    ///   CAS expected=完整 16B 槽值，写入即 Occupied，无中间态，ABA 随全等判等收口）+
    ///   <b>落位校验重试</b>（收集门闩 + 换代校验——堵 GrowIndex 收集漏收窗，见 <see cref="GrowIndex"/>）。</para>
    /// </summary>
    /// <param name="key">条目键。</param>
    /// <param name="valueAddress">条目 value 逻辑地址。</param>
    /// <param name="beginAddress">探测下限地址——槽内旧条目地址小于它视为陈旧，可覆写落位（重放路径约定参数）。</param>
    /// <returns>插入后地址（新条目=落位地址；同 key 覆写=新 value 地址）。</returns>
    public override LogicalAddress Insert(TKey key, LogicalAddress valueAddress, LogicalAddress beginAddress)
    {
        var hash = ComputeHash(key);
        var tag = ComputeTag(key);

        _epoch.Resume();
        try
        {
            var counted = false;   // ★ 跨重试轮的计数标记：首轮落位（NewEntry）可能因收集窗/换代而重试，
                                   //   重试轮判等命中变"覆写"（NewEntry=false）——同 key 逻辑条目只计一次
            while (true)
            {
                var table = Volatile.Read(ref _table);   // ★ 捕获代表（表+溢出池同代原子对；Volatile 防 JIT 循环提升）

                // ★ 增长在本次插入之前触发：此刻全部既有条目都已返回给调用方（调用方对
                //   resolver/ring 的注册已完成）——rehash 逐条 TryGetKey 必可解析。判断基于捕获代；
                //   过期（他人刚增长完）由 GrowIndex 单飞重判断兜底（不重复翻倍）。
                if (Volatile.Read(ref _entryCount) > table.Size * GrowthLoadFactor)
                {
                    GrowIndex();
                    continue;   // 重读新代插入
                }

                InsertResult inserted;
                lock (SlotLock(hash))
                {
                    inserted = InsertIntoTable(hash, tag, key, valueAddress, beginAddress, table);
                }

                // ★ 计数紧跟落位（校验前）：NewEntry=key 新建 → 本 key 计 1 封顶；后续重试轮
                //   （收集窗/换代）的覆写换绑不再计——避免"首轮实体被收编+重轮覆写不计"的净 0 漏计。
                if (inserted.NewEntry && !counted)
                {
                    Interlocked.Increment(ref _entryCount);
                    counted = true;
                }

                // ★ 落位校验（W0 换代竞态）：并发 GrowIndex 已发布新代 → 本条目落在废弃旧代
                //   （收集静默窗内未落位，新代收集中缺席）→ 重插新代。收集窗由全条带锁静默化
                //   （<see cref="GrowIndex"/>），此处只需拦截「捕获代已过期」。
                if (!ReferenceEquals(Volatile.Read(ref _table), table))
                {
                    Thread.Yield();   // 收集/发布窗内让出——发布后重读新代
                    continue;
                }

                return inserted.Entry;
            }
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>插入结果（Entry=落位/覆写条目；NewEntry=是否新条目——增长计数只认新条目）。</summary>
    private readonly record struct InsertResult(LogicalAddress Entry, bool NewEntry);

    /// <summary>条带锁（写互斥粒度=hash 低 <see cref="SlotLockCount"/> 位——同 key 同 hash 必同锁，
    /// 判等扫描+落位/覆写/删除全序化；不同条带并发无碍）。</summary>
    private object SlotLock(ulong hash) => _slotLocks[hash & SlotLockMask];

    private InsertResult InsertIntoTable(ulong hash, ushort tag, TKey key,
        LogicalAddress valueAddress, LogicalAddress beginAddress, InternalHashTable table)
    {
        var ofbPool = table.OverflowPool;
        var ofbCap = ofbPool.Length;
        var mainBucket = (int)(hash & table.SizeMask);
        var curBucket = mainBucket;
        var curOverflow = false;   // ★ 当前桶定位（桶号+所在池）——ref 不跨方法传递（悬垂 ref=未定义行为，W0 实测）

        while (true)
        {
            ref var head = ref curOverflow ? ref ofbPool[curBucket] : ref table.TableRaw[curBucket];

            // ══ Pass 1：整桶链判等扫描（同 key 命中 → 换绑）══
            // ★ 两遍扫描是同 key 单实体的结构保证（W0 幽灵实体根因）：收集搬家后同 key 实体可能
            //   落在本桶靠后槽——若空位路径在靠前空槽立即落位 return，跳过后续槽的既有实体=双实体。
            //   故必须先扫完全桶链（判等），确认无同 key 才进入 Pass 2 空位落位。
            var hit = FindOccupiedSlot(hash, tag, key, head, ofbPool, ofbCap);
            if (hit.Found)
            {
                ref var hitBucket = ref hit.InOverflow ? ref ofbPool[hit.BucketIndex] : ref head;
                var updated = HashEntry.CreateOccupied(valueAddress.SegId, valueAddress.Offset,
                    tag, HashEntry.NextVersion(hit.Entry));
                if (CasSlot(ref hitBucket.AsSpan()[hit.SlotIndex], hit.Entry, updated))
                    return new InsertResult(updated, NewEntry: false);
                continue;   // 槽被并发改写——整桶重扫
            }

            // ══ Pass 2：整桶链找落位槽（Empty 或 < beginAddress 陈旧槽）══
            var slot = FindInsertableSlot(head, ofbPool, ofbCap, beginAddress);
            if (!slot.IsValid)
            {
                if (curOverflow)
                    throw new InvalidOperationException("Overflow chain tail without insertable slot");
                curBucket = AllocateOverflow(mainBucket, table);
                curOverflow = true;
                continue;   // 新溢出桶上重走两遍
            }

            ref var slotBucket = ref slot.InOverflow ? ref ofbPool[slot.BucketIndex] : ref head;
            var occupied = HashEntry.CreateOccupied(valueAddress.SegId, valueAddress.Offset,
                tag, HashEntry.NextVersion(LogicalAddress.Empty));
            if (CasSlot(ref slotBucket.AsSpan()[slot.SlotIndex], slot.Entry, occupied))
                return new InsertResult(occupied, NewEntry: true);
            // 空位被抢/槽被并发改写——整桶重扫
        }
    }

    /// <summary>桶链扫描定位（桶号+槽号+槽值快照——不携带桶 ref，杜绝悬垂）。</summary>
    private readonly struct SlotScan
    {
        public readonly int BucketIndex;     // 溢出池下标（InOverflow）——主表桶号由调用方持有
        public readonly int SlotIndex;
        public readonly LogicalAddress Entry;
        public readonly bool Found;
        public readonly bool InOverflow;
        public bool IsValid => SlotIndex >= 0;

        public SlotScan(int bucketIndex, int slotIndex, LogicalAddress entry, bool found, bool inOverflow)
        {
            BucketIndex = bucketIndex;
            SlotIndex = slotIndex;
            Entry = entry;
            Found = found;
            InOverflow = inOverflow;
        }
    }

    /// <summary>Pass 1：桶链逐槽判等扫描（tag 命中 → KeyResolver 读回真 key 比对），返回首个同 key 实体定位。</summary>
    private SlotScan FindOccupiedSlot(ulong hash, ushort tag, TKey key,
        HashBucket startBucket, HashBucket[] ofbPool, int ofbCap)
    {
        var bucketIndex = -1;   // -1=主桶（主桶号由调用方持有）
        ref var bucketRef = ref startBucket;
        while (true)
        {
            var slots = bucketRef.AsSpan();
            for (int i = 0; i < MaxOverflowSlots; i++)
            {
                var entry = ReadSlotStable(ref slots[i]);
                if (HashEntry.GetState(entry) != HashEntry.Occupied || HashEntry.GetTag(entry) != tag)
                    continue;
                if (KeyResolver!.TryGetKey(entry, out var existingKey) && KeyComparer.Equals(existingKey, key))
                    return new SlotScan(bucketIndex, i, entry, found: true, inOverflow: bucketIndex >= 0);
            }

            var overflowPtr = ReadSlotStable(ref slots[7]);
            if (HashEntry.IsEmpty(overflowPtr))
                return new SlotScan(-1, -1, default, found: false, inOverflow: false);
            bucketIndex = (int)((uint)overflowPtr.Offset % ofbCap);
            bucketRef = ref ofbPool[bucketIndex];
        }
    }

    /// <summary>Pass 2：桶链找首个可落位槽（Empty 或地址低于 beginAddress 的陈旧槽）。</summary>
    private SlotScan FindInsertableSlot(HashBucket startBucket, HashBucket[] ofbPool, int ofbCap,
        LogicalAddress beginAddress)
    {
        var bucketIndex = -1;
        ref var bucketRef = ref startBucket;
        while (true)
        {
            var slots = bucketRef.AsSpan();
            for (int i = 0; i < MaxOverflowSlots; i++)
            {
                var current = ReadSlotStable(ref slots[i]);
                if (HashEntry.IsEmpty(current) || current.CompareTo(beginAddress) < 0)
                    return new SlotScan(bucketIndex, i, current, found: false, inOverflow: bucketIndex >= 0);
            }

            var overflowPtr = ReadSlotStable(ref slots[7]);
            if (HashEntry.IsEmpty(overflowPtr))
                return new SlotScan(-1, -1, default, found: false, inOverflow: false);
            bucketIndex = (int)((uint)overflowPtr.Offset % ofbCap);
            bucketRef = ref ofbPool[bucketIndex];
        }
    }

    /// <summary>
    /// 桶满分配溢出桶（池 bump 在表代内——与表同代共存亡）。返回池下标（调用方经 ofbPool[index] 定位）。
    /// <para>★ #218：全局锁 → 链级条带锁 + Interlocked 计数（原"写者单线程"假设随槽条带化多
    ///   writer 失效）。分配（bump）全局原子；挂链按源桶条带串行——不同源桶的链互不相交（每溢出桶
    ///   属且仅属一条链），跨条带并发无结构冲突。链指针写走 16B CAS（expected=空值）——读者不持锁，
    ///   普通 16B 赋值会被并发读撕裂（审计 #240/#322）。</para>
    /// </summary>
    private int AllocateOverflow(int sourceBucketIndex, InternalHashTable table)
    {
        var ofbPool = table.OverflowPool;
        var ofbCap = ofbPool.Length;

        var index = Interlocked.Increment(ref table.OverflowCount) - 1;
        if (index >= ofbCap)
        {
            Interlocked.Decrement(ref table.OverflowCount);   // 池满回吐（瞬态 +1 对诊断读无害）
            throw new InvalidOperationException("Overflow pool exhausted");
        }

        lock (OverflowChainLock(sourceBucketIndex))
        {
            ref var sourceBucket = ref table.TableRaw[sourceBucketIndex];
            ref var ovSlot = ref sourceBucket.AsSpan()[7];
            if (HashEntry.IsEmpty(ReadSlotStable(ref ovSlot)))
            {
                var chainPtr = new LogicalAddress(1, index);
                if (!CasSlot(ref ovSlot, LogicalAddress.Empty, chainPtr))
                    throw new InvalidOperationException("Overflow chain pointer raced outside allocation lock");
            }
            else
            {
                var chain = ReadSlotStable(ref ovSlot);
                while (true)
                {
                    var chainIdx = (int)chain.Offset;
                    ref var chainBucket = ref ofbPool[chainIdx];
                    ref var chainOv = ref chainBucket.AsSpan()[7];
                    var tail = ReadSlotStable(ref chainOv);
                    if (HashEntry.IsEmpty(tail))
                    {
                        var chainPtr = new LogicalAddress(1, index);
                        if (!CasSlot(ref chainOv, LogicalAddress.Empty, chainPtr))
                            throw new InvalidOperationException("Overflow chain pointer raced outside allocation lock");
                        break;
                    }
                    chain = tail;
                }
            }

            return index;
        }
    }

    /// <summary>
    /// 删除条目：tag 命中后经 KeyResolver 读回真 key 判等确认（避免误删同 tag 异 key 条目），
    /// CAS 清槽 + 条目计数递减。条带锁内执行（与插入/覆写全序化——同 key 删插竞争干净收敛）。
    /// <para>★ CAS 失败=条目正被并发改写——<b>重扫重试</b>直到删掉或确认消失；
    ///   契约：true=真删到，false=不存在。</para>
    /// </summary>
    /// <param name="key">条目键。</param>
    /// <returns>true = 真删到；false = 不存在（含 tag 冲突未命中）。</returns>
    public override bool Delete(TKey key)
    {
        var hash = ComputeHash(key);
        var tag = ComputeTag(key);

        _epoch.Resume();
        try
        {
            while (true)
            {
                var table = Volatile.Read(ref _table);   // ★ 捕获代（与 Insert 同款落位校验——收集窗/换代期间
                                                         //   旧代删除/扫空的结果对最新代不可信）
                bool deleted;
                lock (SlotLock(hash))
                {
                    deleted = DeleteFromTable(hash, tag, key, table);
                }

                // ★ 落位校验（W0 换代竞态）：捕获代已过期（GrowIndex 已发布新代）→ 旧代删除/扫空的
                //   结果对最新代不可信（收集静默窗内未删除，新代收集中仍带条目）→ 按新代重删（幂等）。
                if (!ReferenceEquals(Volatile.Read(ref _table), table))
                {
                    Thread.Yield();
                    continue;
                }

                if (deleted)
                    Interlocked.Decrement(ref _entryCount);
                return deleted;
            }
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>单代内删除（tag 命中 → KeyResolver 判等 → CAS 清槽；true=本代删到）。</summary>
    private bool DeleteFromTable(ulong hash, ushort tag, TKey key, InternalHashTable table)
    {
        var ofbPool = table.OverflowPool;
        var ofbCap = ofbPool.Length;
        var bucketIndex = hash & table.SizeMask;

        ref var bucket = ref table.TableRaw[bucketIndex];
        var slots = bucket.AsSpan();

        while (true)
        {
            // ★ CAS 失败重启整桶扫描（同 InsertIntoTable——不滑向链遍历产生漏判）
            var casFailed = false;

            for (int i = 0; i < MaxOverflowSlots; i++)
            {
                var entry = ReadSlotStable(ref slots[i]);
                if (HashEntry.GetState(entry) == HashEntry.Occupied && HashEntry.GetTag(entry) == tag)
                {
                    // ★ 判等闭环：tag 匹配后读回真 key 校验，避免误删同 tag 异 key 的 entry。
                    if (!(KeyResolver!.TryGetKey(entry, out var existingKey)
                          && KeyComparer.Equals(existingKey, key)))
                        continue;   // tag 冲突，继续找

                    if (CasSlot(ref bucket.AsSpan()[i], entry, LogicalAddress.Empty))
                        return true;
                    casFailed = true;
                    break;   // 被并发改写——重启整桶扫描
                }
            }
            if (casFailed) continue;

            var overflowPtr = ReadSlotStable(ref slots[7]);
            if (HashEntry.IsEmpty(overflowPtr)) return false;

            var ofbIndex = (int)((uint)overflowPtr.Offset % ofbCap);
            bucket = ref ofbPool[ofbIndex];
            slots = bucket.AsSpan();
        }
    }
}
