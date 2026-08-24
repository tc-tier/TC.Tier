
namespace TC.Tier.Runtime.Tests.Structures.SortedIndex;

/// <summary>
/// SortedIndex 测试工厂（当前 API：TestVolume 介质 + StorageEngineOptions + 统一生命周期构造 helper）。
/// <para>★ 对齐 TestRingSettingsFactory/TestLogSettingsFactory 形态：Create* 独立卷 / *On 既有卷
///   （跨实例恢复复用）/ New{BTree,SkipList} 一步生命周期（比较族 ctor 无 RecordStore——key 物化条目内）。</para>
/// <para>★ mem 介质默认（随 TC_TEST_FS_SPEC 平权切介质）。旧字段（RootDirectory/DeviceName/DirectIoMode/
///   PersistenceMode/RecoverDevice/SegmentSize）已随 Settings 基类 MainEngine 模型消亡。</para>
/// </summary>
internal static class TestSortedIndexSettingsFactory
{
    private static StorageEngineOptions Opts(string name, bool deleteOnClose)
        => new StorageEngineOptions(name, 1L << 24, enableSegmentation: true, preallocateFile: true, deleteOnClose);

    // ════ BTreeIndex ════

    /// <summary>BTreeIndexSettings + 独立测试卷。</summary>
    public static (BTreeIndexSettings settings, TestVolume vol) CreateBTree(bool deleteOnClose = true)
    {
        var vol = new TestVolume();
        return (BTreeOn(vol, "bt", deleteOnClose), vol);
    }

    /// <summary>在既有卷上建 BTreeIndexSettings（跨实例恢复场景共用同一卷/引擎名）。</summary>
    public static BTreeIndexSettings BTreeOn(TestVolume vol, string engineName = "bt", bool deleteOnClose = true,
        SortedIndexPersistenceKind persistenceKind = SortedIndexPersistenceKind.Builtin,
        SortedIndexPersistencePolicy? persistencePolicy = null)
        => new(Opts(engineName, deleteOnClose))
        {
            PersistenceKind = persistenceKind,
            PersistencePolicy = persistencePolicy ?? new SortedIndexPersistencePolicy(),
        };

    /// <summary>★ 构造 + Initialize + WaitForReady 一步到位（keyResolver 重放数据面可选；hints 可带重放窗口）。</summary>
    public static BTreeIndex<TKey> NewBTree<TKey>(TestVolume vol, BTreeIndexSettings settings,
        LightEpoch? epoch = null, IKeyResolver<TKey>? keyResolver = null,
        SortedIndexRecoveryHints hints = default)
        where TKey : unmanaged, IEquatable<TKey>
    {
        var index = new BTreeIndex<TKey>(vol.Fs, settings, epoch, keyResolver: keyResolver);
        index.Initialize(hints);
        index.WaitForReady();
        return index;
    }

    // ════ SkipListIndex ════

    /// <summary>SkipListIndexSettings + 独立测试卷。</summary>
    public static (SkipListIndexSettings settings, TestVolume vol) CreateSkipList(
        int maxLevel = 12, bool deleteOnClose = true)
    {
        var vol = new TestVolume();
        return (SkipListOn(vol, "sl", maxLevel, deleteOnClose), vol);
    }

    /// <summary>在既有卷上建 SkipListIndexSettings（跨实例恢复场景共用同一卷/引擎名）。</summary>
    public static SkipListIndexSettings SkipListOn(TestVolume vol, string engineName = "sl",
        int maxLevel = 12, bool deleteOnClose = true,
        SortedIndexPersistenceKind persistenceKind = SortedIndexPersistenceKind.Builtin,
        SortedIndexPersistencePolicy? persistencePolicy = null)
        => new(Opts(engineName, deleteOnClose))
        {
            MaxLevel = maxLevel,
            PersistenceKind = persistenceKind,
            PersistencePolicy = persistencePolicy ?? new SortedIndexPersistencePolicy(),
        };

    /// <summary>★ 构造 + Initialize + WaitForReady 一步到位（keyResolver 重放数据面可选；hints 可带重放窗口）。</summary>
    public static SkipListIndex<TKey> NewSkipList<TKey>(TestVolume vol, SkipListIndexSettings settings,
        LightEpoch? epoch = null, IKeyResolver<TKey>? keyResolver = null,
        SortedIndexRecoveryHints hints = default)
        where TKey : unmanaged, IEquatable<TKey>
    {
        var index = new SkipListIndex<TKey>(vol.Fs, settings, epoch, keyResolver: keyResolver);
        index.Initialize(hints);
        index.WaitForReady();
        return index;
    }
}
