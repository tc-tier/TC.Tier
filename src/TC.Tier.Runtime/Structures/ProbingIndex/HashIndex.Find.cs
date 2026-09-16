using System.Runtime.CompilerServices;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.ProbingIndex;

/// <summary>
/// HashIndex 查找 partial——epoch 读保护点查 + 捕获代探测换代校验（FindNoEpoch）。
/// </summary>
public partial class HashIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>点查 key → value 逻辑地址（epoch 读保护内完成——Resume → <see cref="FindNoEpoch(TKey)"/> → Suspend）。</summary>
    /// <param name="key">查找键。</param>
    /// <returns>命中 = value 逻辑地址；未命中 = <see cref="LogicalAddress.Empty"/>。</returns>
    public override LogicalAddress Find(TKey key)
    {
        _epoch.Resume();
        try
        {
            return FindNoEpoch(key);
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>
    /// ★ 不含 epoch 进出的查找——epoch 由调用方经 <see cref="ProbingIndexBase{TKey}.EnterScope"/> /
    /// <see cref="ProbingIndexBase{TKey}.FindBatch"/> 在外层持有。逻辑与旧 <see cref="Find"/> 的 try-body 一致。
    /// <para>★ 捕获代探测 + 换代校验：单引用捕获 <c>_table</c>（表+溢出池同代原子对）并发增长期间
    ///   持旧代一致探测；但旧代结果可能含<b>已删除条目</b>（并发 Delete 走最新代）——换代则对新代
    ///   重查（结果权威），未换代（稳态）一次返回零额外开销。</para>
    /// </summary>
    /// <param name="key">查找键。</param>
    /// <returns>命中 = value 逻辑地址；未命中 = <see cref="LogicalAddress.Empty"/>。</returns>
    protected override LogicalAddress FindNoEpoch(TKey key)
    {
        var hash = ComputeHash(key);
        var tag = ComputeTag(key);

        while (true)
        {
            var table = Volatile.Read(ref _table);   // Volatile——换代校验依赖每轮读最新发布
            var found = FindInTable(hash, tag, key, table);
            if (ReferenceEquals(Volatile.Read(ref _table), table))
                return found;   // 未换代——本代结果权威
        }
    }

    /// <summary>
    /// 在单个 table 内查找：tag 匹配后经 KeyResolver 读回 record 的真 key 校验（FASTER 判等闭环）。
    /// <para>★ tag 只是加速器——命中后必须读回真 key 判等，否则 tag 冲突会返回错误的 value（假阳性）。</para>
    /// </summary>
    private LogicalAddress FindInTable(ulong hash, ushort tag, TKey key, InternalHashTable table)
    {
        var ofbPool = table.OverflowPool;
        var ofbCap = ofbPool.Length;

        var bucketIndex = hash & table.SizeMask;
        var slots = table.TableRaw[bucketIndex].AsSpan();

        while (true)
        {
            for (int i = 0; i < MaxOverflowSlots; i++)
            {
                var entry = ReadSlotStable(ref slots[i]);
                if (HashEntry.GetState(entry) == HashEntry.Occupied && HashEntry.GetTag(entry) == tag)
                {
                    // ★ 判等闭环：tag 匹配后读回 record 真 key 比对，key 相等才确认命中。
                    //   tag 冲突（key 不同）→ 继续扫下一个 slot。
                    if (KeyResolver!.TryGetKey(entry, out var existingKey)
                        && KeyComparer.Equals(existingKey, key))
                        return entry;
                }
            }

            var overflowPtr = ReadSlotStable(ref slots[7]);
            if (HashEntry.IsEmpty(overflowPtr))
                return LogicalAddress.Empty;

            var ofbIndex = (int)((uint)overflowPtr.Offset % ofbCap);
            slots = ofbPool[ofbIndex].AsSpan();
        }
    }
}
