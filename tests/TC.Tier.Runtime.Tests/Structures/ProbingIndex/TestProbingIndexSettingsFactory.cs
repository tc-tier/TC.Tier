
namespace TC.Tier.Runtime.Tests.Structures.ProbingIndex;

/// <summary>
/// ProbingIndex 测试工厂（当前 API：TestVolume 介质 + StorageEngineOptions + 统一生命周期构造 helper）。
/// <para>★ 对齐 TestRingSettingsFactory/TestLogSettingsFactory 形态：Create* 独立卷 / *On 既有卷
///   （跨实例恢复复用）/ NewHash 一步生命周期（探测族 ctor 必传 IKeyResolver——判等闭环硬依赖）。</para>
/// <para>★ mem 介质默认（随 TC_TEST_FS_SPEC 平权切介质）。旧字段（RootDirectory/DeviceName/DirectIoMode/
///   PersistenceMode/RecoverDevice/SegmentSize）已随 Settings 基类 MainEngine 模型消亡。</para>
/// </summary>
internal static class TestProbingIndexSettingsFactory
{
    private static StorageEngineOptions Opts(string name, bool deleteOnClose)
        => new StorageEngineOptions(name, 1L << 24, enableSegmentation: true, preallocateFile: true, deleteOnClose);

    // ════ 标准配置 ════

    /// <summary>HashIndexSettings + 独立测试卷（默认 1M 桶 + 256K 溢出池——对齐旧测试缺省）。</summary>
    public static (HashIndexSettings settings, TestVolume vol) Create(
        int hashTableCapacity = 1 << 20,
        int overflowPoolCapacity = 1 << 18,
        bool deleteOnClose = true)
    {
        var vol = new TestVolume();
        return (On(vol, "hash", hashTableCapacity, overflowPoolCapacity, deleteOnClose), vol);
    }

    /// <summary>在既有卷上建 HashIndexSettings（跨实例恢复场景共用同一卷/引擎名）。</summary>
    public static HashIndexSettings On(TestVolume vol, string engineName = "hash",
        int hashTableCapacity = 1 << 20,
        int overflowPoolCapacity = 1 << 18,
        bool deleteOnClose = true,
        ProbingIndexPersistenceKind persistenceKind = ProbingIndexPersistenceKind.Builtin,
        ProbingIndexPersistencePolicy? persistencePolicy = null,
        int persistenceKeepVersions = 2)
        => new(Opts(engineName, deleteOnClose))
        {
            HashTableCapacity = hashTableCapacity,
            OverflowPoolCapacity = overflowPoolCapacity,
            PersistenceKind = persistenceKind,
            PersistencePolicy = persistencePolicy ?? new ProbingIndexPersistencePolicy(),
            PersistenceKeepVersions = persistenceKeepVersions,
        };

    /// <summary>★ 构造 + Initialize + WaitForReady 一步到位（探测族判等闭环必传 IKeyResolver；hints 可带重放窗口；
    /// 主存储开关注入经 settings.PersistenceKind——Builtin=载帧加速，None=纯重放）。</summary>
    public static HashIndex<TKey> NewHash<TKey>(TestVolume vol, HashIndexSettings settings,
        IKeyResolver<TKey> resolver, LightEpoch? epoch = null,
        ProbingIndexRecoveryHints hints = default)
        where TKey : unmanaged, IEquatable<TKey>
    {
        var index = new HashIndex<TKey>(vol.Fs, settings, epoch, resolver);
        index.Initialize(hints);
        index.WaitForReady();
        return index;
    }
}
