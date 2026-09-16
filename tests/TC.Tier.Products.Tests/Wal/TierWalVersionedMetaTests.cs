using System.Text;
using TC.Tier.Contracts.Layout;
using TC.Tier.Runtime.Meta;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Log.Contracts;
using TC.Tier.Runtime.Structures.Metadata;

namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// TierWAL 元数据托管版本链（meta.md §7 3a 推荐形态——构建期 With 注入，零产品改动）：
/// <c>WithMetaPolicyKind(Transport) + WithMetaTransport(MetadataMetaTransport)</c> → meta 块
/// 追加进 VersionedMetadata 版本链（追加式版本 record 替代 Managed 固定块覆盖写）；
/// 重启经版本链恢复水位与 opaque/raft 元数据。生命周期由结构自管（构造即启动、读写自等就绪）。
/// </summary>
public class TierWalVersionedMetaTests
{
    private static VersionedMetadataSettings VMetaSettings() => new(
        new StorageEngineOptions("wal.vmeta", enableSegmentation: false, preallocateFile: false))
    {
        // PayloadSize ≥ meta 块上界（meta.md §7）：12B 头 + 水位 struct + opaque(16KB) + 4B 尾
        PayloadSize = LogMetaHeaderCodec.StructSize + LogMetaPayloadCodec.StructSize
            + 16 * 1024 + Crc32FooterCodec.StructSize,
    };

    private static TierWalOptions BaseOptions() => TierWalOptions.Default
        .WithMetaPolicyKind(MetaPolicyKind.Transport)
        .WithCommitInterval(TimeSpan.FromMilliseconds(-1))
        .WithMaxUnflushedBytes(long.MaxValue)
        .WithMaxUnflushedCount(int.MaxValue);

    [Fact]
    public async Task WithMetaTransport_VersionedChain_MetaAndWatermarkSurviveRestart()
    {
        using var vol = new TestVolume();
        var metaBytes = Encoding.UTF8.GetBytes("raft-meta-versioned-chain");

        // 阶段 1：写入（组提交禁用——显式提交驱动）+ raft 元数据提交（进版本链）
        using (var ext = new MetadataMetaTransport(vol.Fs, VMetaSettings()))
        await using (var wal = await BaseOptions().Builder(vol.Fs).WithMetaTransport(ext).StartAsync())
        {
            await wal.AppendBatchAsync(new ReadOnlyMemory<byte>[] { WalTestFactory.Entry(1), WalTestFactory.Entry(2) }, default);
            await wal.WriteMetaAsync(metaBytes, default);   // opaque 容器随显式提交原子落盘
            wal.PersistedIndex.Should().Be(2);
        }

        // 阶段 2：重启（新托管实例同引擎名）——版本链恢复 latest 版本（水位 + opaque/raft 元数据）
        using (var ext2 = new MetadataMetaTransport(vol.Fs, VMetaSettings()))
        await using (var wal2 = await BaseOptions().Builder(vol.Fs).WithMetaTransport(ext2).StartAsync())
        {
            wal2.ReadMeta().ToArray().Should().Equal(metaBytes);
            wal2.AllocatedIndex.Should().Be(2);
            wal2.PersistedIndex.Should().Be(2);
        }
    }
}
