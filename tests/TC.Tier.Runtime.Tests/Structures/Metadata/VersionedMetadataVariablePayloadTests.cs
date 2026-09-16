using FluentAssertions;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Metadata;

namespace TC.Tier.Runtime.Tests.Structures.Metadata;

/// <summary>
/// VersionedMetadata 变长 payload 写入单元测试（MaxPayloadSize 启用——keyed 分区元数据消费形态）。
/// <para>★ 契约：变长档 Write 按实际长度落盘（record 自述几何）；热区按需增长（上限 fail-fast）；
///   Abort 零 IO 回退跨尺寸成立；固定档（不配 MaxPayloadSize）行为逐字节不变（既有套件覆盖）。</para>
/// </summary>
public class VersionedMetadataVariablePayloadTests
{
    private static byte[] MakePayload(int size, byte fill)
    {
        var b = new byte[size];
        Array.Fill(b, fill);
        return b;
    }

    private static (VersionedMetadataSettings settings, TestVolume vol) CreateVariableSettings(
        int payloadSize, int maxPayloadSize, bool deleteOnClose = true)
    {
        var vol = new TestVolume();
        var settings = new VersionedMetadataSettings(new StorageEngineOptions(
                "test.varmeta", 1L << 24, enableSegmentation: false)
            .WithDeleteOnClose(deleteOnClose))
        {
            PayloadSize = payloadSize,
            MaxPayloadSize = maxPayloadSize,
        };
        return (settings, vol);
    }

    [Fact]
    public void VariableWrite_Read_ReturnsActualLength()
    {
        var (settings, vol) = CreateVariableSettings(payloadSize: 16, maxPayloadSize: 1024);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings);
            meta.Initialize();
            meta.WaitForReady();

            // 未写时 = 空镜像（变长档初始内容长度 0）
            var probe = new byte[16];
            meta.Read(probe).Should().Be(0, "变长档未写 = 空");

            meta.Write(MakePayload(16, 0x11));
            var dst1 = new byte[64];
            meta.Read(dst1).Should().Be(16, "读返回实际长度（非槽容量）");
            dst1[0].Should().Be(0x11);

            // 增长越过初始槽尺寸 + 倍增边界
            meta.Write(MakePayload(300, 0x22));
            var dst2 = new byte[1024];
            meta.Read(dst2).Should().Be(300);
            dst2[0].Should().Be(0x22);
            dst2[299].Should().Be(0x22);

            // 缩回小长度（长度随版本走，不残留旧内容）
            meta.Write(MakePayload(8, 0x33));
            var dst3 = new byte[1024];
            meta.Read(dst3).Should().Be(8);
            dst3[0].Should().Be(0x33);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void VariableWrite_ExceedsMaxPayload_Throws()
    {
        var (settings, vol) = CreateVariableSettings(payloadSize: 16, maxPayloadSize: 64);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings);
            meta.Initialize();
            meta.WaitForReady();
            var act = () => meta.Write(MakePayload(65, 0xEE));
            act.Should().Throw<ArgumentException>("超出变长上限 fail-fast（扩容/分段归调用方）");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void VariableWrite_Prepare_Abort_RestoresPreviousLength()
    {
        var (settings, vol) = CreateVariableSettings(payloadSize: 16, maxPayloadSize: 1024);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings);
            meta.Initialize();
            meta.WaitForReady();

            meta.Write(MakePayload(16, 0x11));   // v1：16B
            meta.Prepare(seq: 1);
            meta.Write(MakePayload(300, 0x22));  // v2：300B（触发增长）
            meta.Prepare(seq: 2);
            meta.Abort(seq: 2);

            var dst = new byte[1024];
            meta.Read(dst).Should().Be(16, "Abort 回退到上一版本（长度随内容回退）");
            dst[0].Should().Be(0x11);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void VariableWrite_AbortAfterAppend_NextWritePersists()
    {
        // 回归锚：Abort 尾截断按悬干 record 自身几何回退后，后续 Write+Persist 必须能继续上盘
        var (settings, vol) = CreateVariableSettings(payloadSize: 16, maxPayloadSize: 1024, deleteOnClose: false);
        try
        {
            // 首实例先释放再重启（跨实例恢复惯例——首实例存活时重启实例恢复面不成立）
            using (var meta = new VersionedMetadata(vol.Fs, settings))
            {
                meta.Initialize();
                meta.WaitForReady();

                meta.Write(MakePayload(16, 0x11));
                meta.Prepare(seq: 1);
                meta.Write(MakePayload(400, 0x22));
                meta.Prepare(seq: 2);
                meta.Abort(seq: 2);

                meta.Write(MakePayload(64, 0x33));
                meta.Persist();
            }

            // 重启读回：链头 = v3（64B）
            var settings2 = new VersionedMetadataSettings(new StorageEngineOptions(
                    "test.varmeta", 1L << 24, enableSegmentation: false)
                .WithDeleteOnClose(true))
            {
                PayloadSize = 16,
                MaxPayloadSize = 1024,
            };
            using var meta2 = new VersionedMetadata(vol.Fs, settings2);
            meta2.Initialize();
            meta2.WaitForReady();
            var dst = new byte[1024];
            meta2.Read(dst).Should().Be(64, "重启载入链头 record 按自身 PayloadLength 完整交付");
            dst[0].Should().Be(0x33);
            dst[63].Should().Be(0x33);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void VariableWrite_Restart_MixedSizeChain_ServesHeadAtRealSize()
    {
        // 跨重启混尺寸版本链：小 → 大 → 中，链头按真实大小交付，扫盘按各 record 自述几何推进
        var (settings, vol) = CreateVariableSettings(payloadSize: 16, maxPayloadSize: 1024, deleteOnClose: false);
        try
        {
            using (var meta = new VersionedMetadata(vol.Fs, settings))
            {
                meta.Initialize();
                meta.WaitForReady();
                meta.Write(MakePayload(16, 0x11));
                meta.Write(MakePayload(500, 0x22));
                meta.Write(MakePayload(100, 0x33));
                meta.Persist();
            }

            var settings2 = new VersionedMetadataSettings(new StorageEngineOptions(
                    "test.varmeta", 1L << 24, enableSegmentation: false)
                .WithDeleteOnClose(true))
            {
                PayloadSize = 16,
                MaxPayloadSize = 1024,
            };
            using var meta2 = new VersionedMetadata(vol.Fs, settings2);
            meta2.Initialize();
            meta2.WaitForReady();
            var dst = new byte[1024];
            meta2.Read(dst).Should().Be(100, "链头（100B 中版本）按盘上真实大小交付");
            dst[0].Should().Be(0x33);

            // 载入版本之上继续写更长内容（载入→热区切换跨尺寸）
            meta2.Write(MakePayload(700, 0x44));
            var dst2 = new byte[1024];
            meta2.Read(dst2).Should().Be(700);
            dst2[699].Should().Be(0x44);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void FixedMode_WithoutMaxPayload_BehaviorUnchanged()
    {
        // 固定档回归锚：不配 MaxPayloadSize = 既有截断/补零语义
        var (settings, vol) = CreateVariableSettings(payloadSize: 16, maxPayloadSize: 0);
        try
        {
            using var meta = new VersionedMetadata(vol.Fs, settings);
            meta.Initialize();
            meta.WaitForReady();

            // 未写 = 零镜像按 PayloadSize 交付（既有语义）
            var probe = new byte[16];
            meta.Read(probe).Should().Be(16);

            meta.Write(MakePayload(8, 0x11));   // 不足补零
            var dst = new byte[16];
            meta.Read(dst).Should().Be(16, "固定档恒 PayloadSize（零尾补齐）");
            dst[0].Should().Be(0x11);
            dst[15].Should().Be((byte)0, "尾部补零");

            meta.Write(MakePayload(32, 0x22));  // 超长截断
            var dst2 = new byte[16];
            meta.Read(dst2).Should().Be(16);
            dst2[0].Should().Be(0x22);
        }
        finally { vol.Dispose(); }
    }
}
