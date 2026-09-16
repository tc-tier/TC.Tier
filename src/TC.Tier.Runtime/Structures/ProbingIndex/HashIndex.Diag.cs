using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.ProbingIndex;

/// <summary>
/// ★ 诊断专用 partial：bucket 访问的 ref 对照,用于验证"128B bucket 隐式栈拷贝"假设(已实测推翻)。
/// <para>★ 仅供 benchmark 使用（InternalsVisibleTo("TC.Tier.Benchmarks")）。生产路径请用 <see cref="HashIndex{TKey}.Find"/>。</para>
/// </summary>
public partial class HashIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>
    /// ★ 诊断方法：与 <see cref="HashIndex{TKey}.FindNoEpoch"/> 逻辑一致,唯一区别是 bucket 访问改用 <c>ref</c>
    /// （避免 <c>arr[i].AsSpan()</c> 对 128B 大 struct 的隐式栈拷贝）。用于实测"128B bucket copy"是不是 Find 的主成本。
    /// <para>★ 警告：仅供 benchmark 对照,非生产路径。实测结论见 hashindex-find-bottleneck-investigation.md §4.2(已推翻)。</para>
    /// </summary>
    internal LogicalAddress FindNoEpoch_Ref(TKey key)
    {
        var hash = ComputeHash(key);
        var tag = ComputeTag(key);

        return FindInTableRef(hash, tag, key, _table);
    }

    /// <summary>★ 诊断方法：dump 某 key 所在桶（主表+溢出链）的全部原始槽位（临时排障用）。</summary>
    internal string DiagDumpBucketRaw(TKey key)
    {
        var hash = ComputeHash(key);
        var mainBucket = (int)(hash & Volatile.Read(ref _table).SizeMask);
        var sb = new System.Text.StringBuilder();
        var table = Volatile.Read(ref _table);
        void DumpBuckets(HashBucket[] buckets, bool isOverflow)
        {
            for (long b = 0; b < buckets.LongLength; b++)
            {
                var slots = buckets[b].AsSpan();
                for (int sI = 0; sI < 8; sI++)
                {
                    var entry = ReadSlotStable(ref slots[sI]);
                    if (entry.Equals(LogicalAddress.Empty)) continue;
                    sb.Append($"{(isOverflow ? "ofb" : "tbl")}{b}:{sI}=({entry.SegId},0x{entry.Extension:X},0x{entry.Offset:X}) ");
                }
            }
        }
        DumpBuckets(table.TableRaw, isOverflow: false);
        DumpBuckets(table.OverflowPool, isOverflow: true);
        return sb.ToString();
    }

    /// <summary>
    /// ★ 诊断方法：当前代<b>全表扫描</b>收集某 key 的全部实体（不受 hash 路由限制——幽灵实体定位用）。
    /// <para>★ O(全表)，仅供测试诊断，非生产路径。返回 "Offset@桶:槽" 定位串。</para>
    /// </summary>
    internal string[] DiagScanKeyAllSlots(TKey key)
    {
        var hash = ComputeHash(key);
        var tag = ComputeTag(key);
        var found = new List<string>();
        var table = Volatile.Read(ref _table);

        void ScanBuckets(HashBucket[] buckets, bool isOverflow)
        {
            for (long b = 0; b < buckets.LongLength; b++)
            {
                var slots = buckets[b].AsSpan();
                for (int s = 0; s < MaxOverflowSlots; s++)
                {
                    var entry = ReadSlotStable(ref slots[s]);
                    if (HashEntry.GetState(entry) != HashEntry.Occupied || HashEntry.GetTag(entry) != tag)
                        continue;
                    if (KeyResolver!.TryGetKey(entry, out var k) && KeyComparer.Equals(k, key))
                        found.Add($"{entry.Offset}@{(isOverflow ? "ofb" : "tbl")}{b}:{s}");
                }
            }
        }

        ScanBuckets(table.TableRaw, isOverflow: false);
        ScanBuckets(table.OverflowPool, isOverflow: true);
        return found.ToArray();
    }

    /// <summary>
    /// ★ <see cref="FindInTable"/> 的 ref 版本：bucket 用 <c>ref</c> 引用而非值拷贝,
    /// overflow 同理。逻辑与 <see cref="FindInTable"/> 完全一致。
    /// </summary>
    private LogicalAddress FindInTableRef(ulong hash, ushort tag, TKey key, InternalHashTable table)
    {
        var ofbPool = table.OverflowPool;
        var ofbCap = ofbPool.Length;

        var bucketIndex = hash & table.SizeMask;
        ref var bucketRef = ref table.TableRaw[bucketIndex];   // ★ ref：无 128B 拷贝
        var slots = bucketRef.AsSpan();

        while (true)
        {
            for (int i = 0; i < MaxOverflowSlots; i++)
            {
                var entry = slots[i];
                if (HashEntry.GetState(entry) == HashEntry.Occupied && HashEntry.GetTag(entry) == tag)
                {
                    if (KeyResolver!.TryGetKey(entry, out var existingKey)
                        && KeyComparer.Equals(existingKey, key))
                        return entry;
                }
            }

            var overflowPtr = slots[7];
            if (HashEntry.IsEmpty(overflowPtr))
                return LogicalAddress.Empty;

            var ofbIndex = (int)((uint)overflowPtr.Offset % ofbCap);
            bucketRef = ref ofbPool[ofbIndex];   // ★ ref：overflow 也无拷贝
            slots = bucketRef.AsSpan();
        }
    }
}
