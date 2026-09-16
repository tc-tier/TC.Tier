using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// SkipListIndex 查找 partial——操作闸+epoch 读保护点查与塔链下降（FindNoEpoch）。
/// </summary>
public partial class SkipListIndex<TKey> where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>点查 key → value 逻辑地址（操作闸 + epoch 读保护内转发 <see cref="FindNoEpoch"/>）。</summary>
    /// <param name="key">查找键。</param>
    /// <returns>命中 = value 逻辑地址；未命中 = <see cref="LogicalAddress.Empty"/>。</returns>
    public override LogicalAddress Find(TKey key)
    {
        using var _ = EnterOp();   // ★ 操作闸（读写全互斥）
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
    /// ★ 不含 epoch 进出的查找——epoch 由调用方经 <see cref="SortedIndexBase{TKey}.EnterScope"/> /
    /// <see cref="SortedIndexBase{TKey}.FindBatch"/> 在外层持有。逻辑与旧 <see cref="Find"/> 的 try-body 一致。
    /// <para>★ 零拷贝跳链：GetNode 返回 arena 驻留指针，逐跳只碰 Key+目标层指针（~24B）——
    /// 旧形每跳 288B 全量 header 槽→局部拷贝（~25 跳/Find 的逐跳税）。</para>
    /// </summary>
    /// <param name="key">查找键。</param>
    /// <returns>命中 = value 逻辑地址；未命中 = <see cref="LogicalAddress.Empty"/>。</returns>
    protected override unsafe LogicalAddress FindNoEpoch(TKey key)
    {
        var current = _headPtr;
        for (int i = _currentLevel - 1; i >= 0; i--)
        {
            var nextAddr = ReadLevel(current, i);
            while (nextAddr != LogicalAddress.Empty)
            {
                var next = GetNode(nextAddr);
                if (KeyComparer.Compare(ReadKey(next), key) < 0)
                {
                    current = next;
                    nextAddr = ReadLevel(current, i);
                }
                else
                {
                    break;
                }
            }
        }

        var level0Addr = ReadLevel(current, 0);
        if (level0Addr != LogicalAddress.Empty)
        {
            var level0 = GetNode(level0Addr);
            if (KeyComparer.Equals(ReadKey(level0), key))
                return ReadValue(level0);
        }

        return LogicalAddress.Empty;
    }
}
