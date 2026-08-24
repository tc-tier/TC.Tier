using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Snapshot;

namespace TC.Tier.Runtime.Tests.Structures.Snapshot;

/// <summary>Snapshot 测试 Settings 工厂（当前 API：TestVolume 介质 + StorageEngineOptions）。</summary>
internal static class TestSnapshotSettingsFactory
{
    /// <summary>创建 StreamSnapshotSettings + 独立测试卷（单段稀疏，测试轻量）。</summary>
    public static (StreamSnapshotSettings settings, TestVolume vol) CreateSettings(
        long segmentSize = 1L << 24, MetaPolicyKind metaKind = MetaPolicyKind.Disabled,
        bool deleteOnClose = true, int payloadCapacity = 0)
    {
        var vol = new TestVolume();
        return (new StreamSnapshotSettings(
                new StorageEngineOptions("test.0", segmentSize, enableSegmentation: false)
                    .WithDeleteOnClose(deleteOnClose))
        {
            MetaPolicyKind = metaKind,
            MetaOpaqueBytes = payloadCapacity,
        }, vol);
    }
}
