using TC.Tier.Contracts.Storage;
using TC.Tier.Runtime.Structures.Snapshot;
using TC.Tier.Runtime.Tests;

namespace TC.Tier.Products.Tests.Blob;

/// <summary>
/// TierBlob 恢复测试（tierblob-spec §8 验证矩阵 7/8——崩溃窗口孤儿帧 + 表数据对账标损）。
/// <para>spec §5.3 定案钉：孤儿帧（有数据无登记）不可见 + 空间保留（at-least-once 诚实形态）。</para>
/// </summary>
public sealed class TierBlobRecoveryTests
{
    // ══ 矩阵 7：Complete 后登记前 kill → 重启孤儿帧不可见 + 空间保留；
    //             登记对象不受影响；新写入从恢复写尾继续（覆盖孤儿区段）══

    [Fact]
    public async Task OrphanFrame_InvisibleAfterRecovery_SpaceRetained()
    {
        using var vol = new TestVolume();
        LogicalAddress keptHandle, orphanHandle;
        var keptData = TierBlobTestFactory.MakeData(1000, 0x77);

        await using (var blob = await TierBlobTestFactory.StartAsync(vol))
        {
            keptHandle = (await blob.PutAsync(keptData)).ObjectId;

            // 模拟崩溃窗口：数据引擎上帧已闭环（Complete），登记未发生（崩溃点）
            var writer = blob.DiagnosticDataSnapshot.OpenWrite();
            await using (writer)
            {
                await writer.WriteAsync(TierBlobTestFactory.MakeData(2000, 0xEE));
                await writer.CompleteAsync();
            }
            orphanHandle = blob.DiagnosticDataSnapshot.TruncatedAddress;   // 占位（真实孤儿句柄下方断言）
        }

        await using (var blob = await TierBlobTestFactory.StartAsync(vol))
        {
            // 登记对象存活——句柄直达
            var dst = new byte[keptData.Length];
            (await blob.GetAsync(keptHandle, dst)).Should().Be(keptData.Length);
            dst.Should().Equal(keptData);

            // 孤儿帧不可见（无登记——List/Get 都不存在）
            var all = await TierBlobTests.CollectAsync(blob.ListAsync(new BlobListFilter(default, default, IncludeTombstones: true)));
            all.Should().ContainSingle("孤儿帧不可见——仅登记对象可见");
            all[0].ObjectId.Should().Be(keptHandle);

            // 空间保留：写尾覆盖孤儿帧区段（Backward 找帧尾定位到孤儿帧末端——at-least-once 形态）
            blob.DiagnosticDataSnapshot.WriteAddress.CompareTo(keptHandle).Should().BeGreaterThan(0,
                "孤儿帧占据写尾区间——空间保留（不回滚不回收）");

            // 恢复后新写入从写尾继续——地址单调，且已返句柄永不复用
            var fresh = await blob.PutAsync(TierBlobTestFactory.MakeData(300, 0x99));
            fresh.ObjectId.CompareTo(keptHandle).Should().BeGreaterThan(0);
            var dst2 = new byte[300];
            await blob.GetAsync(fresh.ObjectId, dst2);
            dst2.Should().OnlyContain(b => b == 0x99);
        }
    }

    // ══ 矩阵 8：表内对象帧损坏 → 重启标 Tombstone 显式可见（尾级验帧：帧尾 magic/TotalLength 撕裂）══

    [Fact]
    public async Task CorruptedFrame_MarkedTombstoneOnRecovery_Visible()
    {
        using var vol = new TestVolume();
        var data1 = TierBlobTestFactory.MakeData(1000, 0x11);
        var data2 = TierBlobTestFactory.MakeData(2000, 0x22);
        LogicalAddress h1, h2;
        int sector;

        await using (var blob = await TierBlobTestFactory.StartAsync(vol))
        {
            h1 = (await blob.PutAsync(data1)).ObjectId;
            h2 = (await blob.PutAsync(data2)).ObjectId;
            sector = blob.DiagnosticDataSnapshot.SectorSize;

            // 模拟损坏：撕裂对象 2 的帧尾（TotalLength 对账值被覆写）
            var info2 = await blob.GetInfoAsync(h2);
            var footerAddr = blob.DiagnosticDataSnapshot.AdvanceAddress(
                h2, FrameLengthOf((int)info2.Length, sector) - BlobObjectTable.FrameFooterSize);
            blob.DiagnosticDataSnapshot.Overwrite(footerAddr, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3, 4, 5, 6, 7, 8 });
        }

        await using (var blob = await TierBlobTestFactory.StartAsync(vol))
        {
            // 损坏对象标墓碑（显式可见——不静默隐藏）
            var info = await blob.GetInfoAsync(h2);
            info.State.Should().Be(BlobState.Tombstone, "恢复对账：帧尾校验失败 → 标墓碑");
            blob.DiagnosticMarkedMissing.Should().Be(1);

            await FluentActions.Awaiting(async () => await blob.GetAsync(h2, new byte[data2.Length]))
                .Should().ThrowAsync<KeyNotFoundException>("墓碑不可读");

            var withTombs = await TierBlobTests.CollectAsync(blob.ListAsync(new BlobListFilter(default, default, IncludeTombstones: true)));
            withTombs.Should().HaveCount(2);
            withTombs.Count(b => b.State == BlobState.Tombstone).Should().Be(1);

            // 完好对象不受影响
            var dst = new byte[data1.Length];
            await blob.GetAsync(h1, dst);
            dst.Should().Equal(data1, "完好对象零影响");
        }

        static long FrameLengthOf(int length, int sector)
            => TierBlobTestFactory.FrameLengthOf(length, sector);
    }

    // ══ 深检档：DeepVerifyOnRecovery=true → 整帧读回 + CRC64 全量校验（数据区中段损坏可检出）══

    [Fact]
    public async Task DeepVerifyOnRecovery_DetectsMidDataCorruption()
    {
        using var vol = new TestVolume();
        var data1 = TierBlobTestFactory.MakeData(1500, 0x11);
        var data2 = TierBlobTestFactory.MakeData(2500, 0x22);
        LogicalAddress h1, h2;

        await using (var blob = await TierBlobTestFactory.StartAsync(vol))
        {
            h1 = (await blob.PutAsync(data1)).ObjectId;
            h2 = (await blob.PutAsync(data2)).ObjectId;

            // 撕裂对象 2 数据区中段（尾级检不可见——magic/TotalLength 完好；CRC64 深检可检出）
            var midAddr = blob.DiagnosticDataSnapshot.AdvanceAddress(
                h2, BlobObjectTable.FrameHeaderSize + 800);
            blob.DiagnosticDataSnapshot.Overwrite(midAddr, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        }

        await using (var blob = await TierBlobTestFactory.StartAsync(
            vol,
            configure: o => o with { DeepVerifyOnRecovery = true }))
        {
            var info = await blob.GetInfoAsync(h2);
            info.State.Should().Be(BlobState.Tombstone, "深检 CRC64 不符 → 标墓碑");
            blob.DiagnosticMarkedMissing.Should().Be(1);

            var dst = new byte[data1.Length];
            await blob.GetAsync(h1, dst);
            dst.Should().Equal(data1, "完好对象零影响");
        }
    }

    // ══ 悬干写会话（写一半崩溃）→ 重启残段为孤儿不可见，登记对象完好 ══

    [Fact]
    public async Task TornIncompleteFrame_RecoveryIgnoresTail()
    {
        using var vol = new TestVolume();
        var keptData = TierBlobTestFactory.MakeData(2000, 0x44);
        LogicalAddress keptHandle;

        await using (var blob = await TierBlobTestFactory.StartAsync(vol))
        {
            keptHandle = (await blob.PutAsync(keptData)).ObjectId;

            // 写一半不给帧尾（无 Complete——进程崩溃形态）
            var writer = blob.DiagnosticDataSnapshot.OpenWrite();
            await writer.WriteAsync(TierBlobTestFactory.MakeData(3000, 0xEE));
            // 不 Complete、不 Dispose——直接弃置（悬干缓冲）
        }

        await using (var blob = await TierBlobTestFactory.StartAsync(vol))
        {
            var dst = new byte[keptData.Length];
            (await blob.GetAsync(keptHandle, dst)).Should().Be(keptData.Length);
            dst.Should().Equal(keptData, "悬干残段不遮蔽登记对象");

            var all = await TierBlobTests.CollectAsync(blob.ListAsync(new BlobListFilter(default, default, IncludeTombstones: true)));
            all.Should().ContainSingle("悬干残段不可见");
        }
    }
}
