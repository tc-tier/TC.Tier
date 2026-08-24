using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Log;

namespace TC.Tier.Runtime.Tests.Structures.Log;

/// <summary>
/// Log 测试工厂（当前 API：TestVolume 介质 + StorageEngineOptions + 统一生命周期构造 helper）。
/// <para>★ 生命周期：new + Initialize + WaitForReady（<see cref="NewEntryLog"/>/<see cref="NewDeltaLog"/> 一步到位）。</para>
/// <para>★ DIO 模式：hints=NoBuffering 请求（mem 介质探测结果 Ignored，对齐路径仍生效）。</para>
/// </summary>
internal static class TestLogSettingsFactory
{
    private static StorageEngineOptions Opts(string name, bool deleteOnClose, bool preallocate, FileOpenHints hints)
        => new StorageEngineOptions(name, 8L << 20, enableSegmentation: true, preallocate, deleteOnClose).WithHints(hints);

    // ════ EntryLog ════

    /// <summary>Buffered 模式 EntryLogSettings + 独立测试卷（默认 Disabled meta、DeleteOnClose）。</summary>
    public static (EntryLogSettings settings, TestVolume vol) CreateEntry(
        int logPageSizeBits = 22, MetaPolicyKind metaKind = MetaPolicyKind.Disabled,
        bool deleteOnClose = true, bool preallocate = false, int payloadCapacity = 0,
        FileOpenHints hints = FileOpenHints.None)
    {
        var vol = new TestVolume();
        return (EntryOn(vol, "entry", logPageSizeBits, metaKind, deleteOnClose, preallocate, payloadCapacity, hints), vol);
    }

    /// <summary>在既有卷上建 EntryLogSettings（跨实例恢复场景共用同一卷/引擎名）。</summary>
    public static EntryLogSettings EntryOn(TestVolume vol, string engineName = "entry",
        int logPageSizeBits = 22, MetaPolicyKind metaKind = MetaPolicyKind.Disabled,
        bool deleteOnClose = true, bool preallocate = false, int payloadCapacity = 0,
        FileOpenHints hints = FileOpenHints.None)
        => new(Opts(engineName, deleteOnClose, preallocate, hints))
        {
            LogPageSizeBits = logPageSizeBits,
            MetaPolicyKind = metaKind,
            MetaOpaqueBytes = payloadCapacity,
        };

    /// <summary>DIO 模式（NoBuffering 请求——覆盖 FlushPage 扇区对齐路径）。</summary>
    public static (EntryLogSettings settings, TestVolume vol) CreateEntryDIO(int logPageSizeBits = 14)
        => CreateEntry(logPageSizeBits, hints: FileOpenHints.NoBuffering);

    /// <summary>★ 构造 + Initialize + WaitForReady 一步到位。</summary>
    public static EntryLog NewEntryLog(TestVolume vol, EntryLogSettings settings, ICommitPolicy? policy = null)
    {
        var log = new EntryLog(vol.Fs, settings, policy);
        log.Initialize();
        log.WaitForReady();
        return log;
    }

    // ════ DeltaLog ════

    /// <summary>DeltaLogSettings + 独立测试卷（Kind 缺省 Transport 嵌入——DeltaLogSettings 语义）。</summary>
    public static (DeltaLogSettings settings, TestVolume vol) CreateDelta(
        int logPageSizeBits = 22, bool deleteOnClose = true, FileOpenHints hints = FileOpenHints.None)
    {
        var vol = new TestVolume();
        return (DeltaOn(vol, "delta", logPageSizeBits, deleteOnClose, hints), vol);
    }

    /// <summary>在既有卷上建 DeltaLogSettings。</summary>
    public static DeltaLogSettings DeltaOn(TestVolume vol, string engineName = "delta",
        int logPageSizeBits = 22, bool deleteOnClose = true, FileOpenHints hints = FileOpenHints.None)
        => new(Opts(engineName, deleteOnClose, preallocate: false, hints))
        {
            LogPageSizeBits = logPageSizeBits,
        };

    /// <summary>DIO 模式 DeltaLog。</summary>
    public static (DeltaLogSettings settings, TestVolume vol) CreateDeltaDIO(int logPageSizeBits = 14)
        => CreateDelta(logPageSizeBits, hints: FileOpenHints.NoBuffering);

    /// <summary>★ 构造 + Initialize + WaitForReady 一步到位。</summary>
    public static DeltaLog NewDeltaLog(TestVolume vol, DeltaLogSettings settings)
    {
        var log = new DeltaLog(vol.Fs, settings);
        log.Initialize();
        log.WaitForReady();
        return log;
    }
}
