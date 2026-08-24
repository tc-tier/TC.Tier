using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// Ring 测试工厂（当前 API：TestVolume 介质 + StorageEngineOptions + 统一生命周期构造 helper）。
/// <para>★ 对齐 TestLogSettingsFactory 形态：Create* 独立卷 / *On 既有卷（跨实例恢复复用）/ NewRing 一步生命周期。</para>
/// <para>★ mem 介质默认（随 TC_TEST_FS_SPEC 平权切介质）；Buffered 语义（mem 探测忽略 hints，路径不变）。</para>
/// <para>★ 旧字段（RootDirectory/DeviceName/DirectIoMode/PersistenceMode/RecoverDevice/RecoveryTailHint）已随
///   Settings 基类 MainEngine 模型 + 水位线归层裁定消亡——恢复场景 = 同卷同名 + DeleteOnClose=false 重开（引擎自恢复）。</para>
/// </summary>
internal static class TestRingSettingsFactory
{
    private static StorageEngineOptions Opts(string name, bool deleteOnClose, bool preallocate = true,
        FileOpenHints hints = FileOpenHints.None)
        => new StorageEngineOptions(name, 1L << 24, enableSegmentation: true, preallocate, deleteOnClose)
            .WithHints(hints);

    // ════ 标准配置 ════

    /// <summary>小内存测试配置 + 独立测试卷：PageSize=4K，MemorySize=64K → 16 页，MutableFraction=0.5。</summary>
    public static (BlittableRingSettings settings, TestVolume vol) Create(
        int pageSize = AlignmentConst.Alignment4K,
        long memorySize = 64 * 1024,
        double coldReadRatio = 0.25,
        int? clockCacheCapacity = null,
        int coldRecordBufferLimit = 1 << 20)
    {
        var vol = new TestVolume();
        return (On(vol, "ring", pageSize: pageSize, memorySize: memorySize,
            coldReadRatio: coldReadRatio, clockCacheCapacity: clockCacheCapacity,
            coldRecordBufferLimit: coldRecordBufferLimit), vol);
    }

    /// <summary>在既有卷上建 BlittableRingSettings（跨实例恢复场景共用同一卷/引擎名）。</summary>
    public static BlittableRingSettings On(TestVolume vol, string engineName = "ring",
        FileOpenHints hints = FileOpenHints.None,
        int pageSize = AlignmentConst.Alignment4K,
        long memorySize = 64 * 1024,
        double mutableFraction = 0.5,
        MetaPolicyKind metaKind = MetaPolicyKind.Disabled,
        bool deleteOnClose = true,
        double coldReadRatio = 0.25,
        int? clockCacheCapacity = null,
        int coldRecordBufferLimit = 1 << 20,
        OverflowPolicy overflowPolicy = OverflowPolicy.Disabled,
        int minOverflowSize = 32)
        => new(Opts(engineName, deleteOnClose, hints: hints))
        {
            PageSize = pageSize,
            MemorySize = memorySize,
            MutableFraction = mutableFraction,
            Preallocate = true,               // 小内存测试预分配（简化）
            MetaPolicyKind = metaKind,
            ColdReadRatio = coldReadRatio,
            ClockCacheCapacity = clockCacheCapacity,
            ColdRecordBufferLimit = coldRecordBufferLimit,
            OverflowPolicy = overflowPolicy,
            MinOverflowSize = minOverflowSize,
        };

    /// <summary>★ 构造 + Initialize + WaitForReady 一步到位（epoch 可注入——驱逐测试用已知 epoch）。</summary>
    public static BlittableRing<TKey> NewRing<TKey>(TestVolume vol, BlittableRingSettings settings,
        LightEpoch? epoch = null)
        where TKey : unmanaged, IEquatable<TKey>
    {
        var ring = new BlittableRing<TKey>(settings, vol.Fs, epoch: epoch);
        ring.Initialize();
        ring.WaitForReady();
        return ring;
    }

    /// <summary>溢出测试配置 + 独立测试卷：OverflowPolicy=Enabled，MinOverflowSize=32（>32B 的 Value 溢出）。</summary>
    public static (BlittableRingSettings settings, TestVolume vol) CreateOverflow(
        int pageSize = AlignmentConst.Alignment4K,
        long memorySize = 64 * 1024,
        double coldReadRatio = 0.25,
        int minOverflowSize = 32)
    {
        var vol = new TestVolume();
        return (On(vol, "ring", pageSize: pageSize, memorySize: memorySize,
            coldReadRatio: coldReadRatio,
            overflowPolicy: OverflowPolicy.Enabled, minOverflowSize: minOverflowSize), vol);
    }

    public static byte[] MakePattern(byte value, int count)
    {
        var buf = new byte[count];
        Array.Fill(buf, value);
        return buf;
    }
}
