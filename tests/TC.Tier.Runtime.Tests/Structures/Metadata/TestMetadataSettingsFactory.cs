using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Metadata;

namespace TC.Tier.Runtime.Tests.Structures.Metadata;

/// <summary>VersionedMetadata 测试 Settings 工厂（当前 API：TestVolume 介质 + StorageEngineOptions）。</summary>
internal static class TestMetadataSettingsFactory
{
    /// <summary>创建 VersionedMetadataSettings + 独立测试卷（介质默认 memory:，TC_TEST_FS_SPEC 切真磁盘）。
    /// Dispose 卷即清理（mem=拔盘；local=递归删目录）。</summary>
    public static (VersionedMetadataSettings settings, TestVolume vol) CreateSettings(
        int payloadSize = 256, long segmentSize = 1L << 24, MetaPolicyKind metaKind = MetaPolicyKind.Disabled)
    {
        var vol = new TestVolume();
        return (CreateSettings(vol, payloadSize, segmentSize, metaKind, deleteOnClose: true), vol);
    }

    /// <summary>在既有卷上创建 VersionedMetadataSettings（跨实例恢复场景共用同一卷）。</summary>
    /// <param name="vol">测试卷（两实例先后用同一 fs，引擎名相同即同子目录）。</param>
    /// <param name="payloadSize">元数据结构体字节数。</param>
    /// <param name="segmentSize">段增长上限。</param>
    /// <param name="metaKind">meta 持久化模式。</param>
    /// <param name="deleteOnClose">引擎 Dispose 是否删除产物（跨实例首实例须 false 留数据）。</param>
    /// <param name="payloadCapacity">Transport meta 的 opaque 容量。</param>
    public static VersionedMetadataSettings CreateSettings(TestVolume vol,
        int payloadSize = 256, long segmentSize = 1L << 24,
        MetaPolicyKind metaKind = MetaPolicyKind.Disabled, bool deleteOnClose = true,
        int payloadCapacity = 0)
        => new(new StorageEngineOptions("test.0", segmentSize, enableSegmentation: false)
                .WithDeleteOnClose(deleteOnClose))
        {
            PayloadSize = payloadSize,
            MetaPolicyKind = metaKind,
            MetaOpaqueBytes = payloadCapacity,
        };
}
