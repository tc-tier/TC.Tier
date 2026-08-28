using System.Runtime.CompilerServices;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.SortedIndex;

public partial class SkipListIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>
    /// 插入条目（key → valueAddress）——塔链下降记录各层前驱、同 key 原位覆写 value 不增计数；
    /// 新节点逐层 CAS 入塔链（epoch 读保护内完成）。
    /// </summary>
    /// <param name="key">条目键。</param>
    /// <param name="valueAddress">条目 value 逻辑地址。</param>
    /// <param name="beginAddress">结构起始地址（重放路径约定参数——跳表插入不消费）。</param>
    /// <returns>插入后地址。</returns>
    public override unsafe LogicalAddress Insert(TKey key, LogicalAddress valueAddress, LogicalAddress beginAddress)
    {
        _epoch.Resume();
        try
        {
            // ★ 层分配与插入时间解耦：纯几何分布（p=1/2，封顶 _maxLevel）——旧"纪元式"钳制
            //   （currentLevel 随条目数增长、早插入 key 全被钳 level-1）令早区链稀疏，查早 key 退化线性扫
            //   （实测 50k 条平均 386 跳/Find，正常 ~16）。_currentLevel 改由实际节点层驱动（只作搜索起点）。
            int level = RandomLevel();
            if (level > _currentLevel)
                _currentLevel = level;

            // ★ 只存前驱地址（stackalloc 16×16B）；前驱节点经 GetNode 驻留指针直访（读后回填不变量的 arena 形）。
            Span<LogicalAddress> updates = stackalloc LogicalAddress[_maxLevel];
            var current = _headPtr;
            LogicalAddress currentAddr = LogicalAddress.Empty;

            for (int i = _currentLevel - 1; i >= 0; i--)
            {
                var nextAddr = ReadLevel(current, i);
                while (nextAddr != LogicalAddress.Empty)
                {
                    var next = GetNode(nextAddr);
                    if (KeyComparer.Compare(ReadKey(next), key) < 0)
                    {
                        current = next;
                        currentAddr = nextAddr;
                        nextAddr = ReadLevel(current, i);
                    }
                    else
                    {
                        break;
                    }
                }

                if (i < level)
                {
                    updates[i] = currentAddr;
                }
            }

            // ★ 同 key 判重：下降结束时 current 是 level-0 前驱，其首个后继若 == key → 原位覆写 value
            //   （不建新节点、不推 EntryCount）——否则同 key 重复 Insert 留双节点（计数虚高、有序遍历吐重复 key）。
            var dupAddr = ReadLevel(current, 0);
            if (dupAddr != LogicalAddress.Empty)
            {
                var dup = GetNode(dupAddr);
                if (KeyComparer.Equals(ReadKey(dup), key))
                {
                    WriteValue(dup, valueAddress);
                    MarkDirty(dupAddr);   // ★ 值覆写延迟写回（读路径走驻留缓存——引擎副本只服务物化）
                    return valueAddress;
                }
            }

            var newNodeSize = ComputeNodeSize(level);
            var nodeAddr = AllocateNode(newNodeSize);
            var newNode = _nodeArena.Alloc(newNodeSize);
            Unsafe.InitBlock(newNode, 0, (uint)newNodeSize);
            Unsafe.Write(newNode, key);
            WriteValue(newNode, valueAddress);
            newNode[LevelCountOffset] = (byte)level;
            MarkDirty(nodeAddr);   // ★ 新节点延迟写回（物化前 dump 批量写回；崩溃窗口重放修复）

            bool headChanged = false;
            for (int i = 0; i < level; i++)
            {
                var predAddr = updates[i];
                if (predAddr == LogicalAddress.Empty)
                {
                    var old = ReadLevel(_headPtr, i);
                    WriteLevel(newNode, i, old);
                    if (!CasLevel(ref LevelRef(_headPtr, i), old, nodeAddr))
                        throw new InvalidOperationException($"CAS insert failed for head level {i}, key {key}");
                    headChanged = true;
                }
                else
                {
                    // 前驱必在缓存（下降路径 GetNode 读后即 admit）——指针直写驻留塔链
                    var pred = GetNode(predAddr);
                    var old = ReadLevel(pred, i);
                    WriteLevel(newNode, i, old);
                    WriteLevel(pred, i, nodeAddr);
                    MarkDirty(predAddr);   // ★ 前驱链变更延迟写回（引擎副本恒完整含链——物化前 dump 批量写回）
                }
            }
            if (headChanged)
                MarkDirty(_headAddress);   // ★ 塔顶变更延迟写回（同上）

            Interlocked.Increment(ref _entryCount);
            _cachedNodes.Upsert(nodeAddr, (nint)newNode);
            return valueAddress;
        }
        finally
        {
            _epoch.Suspend();
        }
    }
}
