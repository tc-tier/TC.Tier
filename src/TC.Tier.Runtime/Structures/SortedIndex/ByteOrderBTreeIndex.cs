using TC.Tier.Core.Epochs;
using TC.Tier.Core.Primitives;
using TC.Tier.Contracts.Structures;

namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// key 字节序 B+树封闭形态（W7 F1 范围/前缀扫描的排序底座）：BTreeIndex 的编译期封闭 +
/// 内置 <see cref="KeyByteOrderComparer{TKey}"/>——字节字典序（RocksDB/LMDB 同款范围扫描通用序，
/// 前缀 = 字节区间，多字段结构体 key 按字段布局序排布）。
/// <para>★ 消费面：TierKv 范围索引挂载（EnableRangeIndex）/ 任何需要 key 字节序有序索引的组合。
/// 开放泛型 BTreeIndex（默认数值/结构序）不落消费面，同 BlittableRing 律。</para>
/// </summary>
/// <typeparam name="TKey">键类型（unmanaged——字节序 = blitted 字节序）。</typeparam>
public sealed class ByteOrderBTreeIndex<TKey> : BTreeIndex<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>ctor（排序比较器固定字节序——非可注入，类型语义即排序语义）。</summary>
    /// <param name="fileSystem">文件系统（TierFs 空间根）。</param>
    /// <param name="settings">BTree 配置（引擎选项/几何）。</param>
    /// <param name="keyResolver">key 解析器（帧 W 锚点/重放回读——挂数据真源 Ring）。</param>
    /// <param name="epoch">时代保护（可选）。</param>
    public ByteOrderBTreeIndex(TC.Tier.Core.IO.IFileSystem fileSystem, BTreeIndexSettings settings,
        IKeyResolver<TKey>? keyResolver = null, LightEpoch? epoch = null)
        : base(fileSystem, settings, epoch, keyResolver: keyResolver,
              keyComparer: new KeyByteOrderComparer<TKey>())
    {
    }
}
