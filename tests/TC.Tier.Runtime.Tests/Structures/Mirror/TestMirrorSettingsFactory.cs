using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Mirror;

namespace TC.Tier.Runtime.Tests.Structures.Mirror;

/// <summary>Mirror 测试 Settings 工厂（当前 API：TestVolume 介质 + StorageEngineOptions）。</summary>
internal static class TestMirrorSettingsFactory
{
    /// <summary>创建 WholeMirrorSettings + 独立测试卷。</summary>
    public static (WholeMirrorSettings settings, TestVolume vol) CreateWholeSettings(
        long segmentSize = 1L << 24, MetaPolicyKind metaKind = MetaPolicyKind.Disabled,
        bool deleteOnClose = true, int payloadCapacity = 0)
    {
        var vol = new TestVolume();
        return (new WholeMirrorSettings(
                new StorageEngineOptions("test.0", segmentSize, enableSegmentation: false)
                    .WithDeleteOnClose(deleteOnClose))
        {
            MetaPolicyKind = metaKind,
            MetaOpaqueBytes = payloadCapacity,
        }, vol);
    }

    /// <summary>创建 PagedMirrorSettings + 独立测试卷（LogPageSizeBits=12 → PageSize 4096，测试轻量）。</summary>
    public static (PagedMirrorSettings settings, TestVolume vol) CreatePagedSettings(
        long segmentSize = 1L << 24, MetaPolicyKind metaKind = MetaPolicyKind.Disabled,
        bool deleteOnClose = true, int payloadCapacity = 0, int logPageSizeBits = 12)
    {
        var vol = new TestVolume();
        return (new PagedMirrorSettings(
                new StorageEngineOptions("test.0", segmentSize, enableSegmentation: false)
                    .WithDeleteOnClose(deleteOnClose))
        {
            LogPageSizeBits = logPageSizeBits,
            MetaPolicyKind = metaKind,
            MetaOpaqueBytes = payloadCapacity,
        }, vol);
    }
}
