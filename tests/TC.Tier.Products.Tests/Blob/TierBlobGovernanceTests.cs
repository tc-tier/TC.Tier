using TC.Tier.Runtime.Tests;

namespace TC.Tier.Products.Tests.Blob;

/// <summary>
/// TierBlob 治理测试（tierblob-spec §8 验证矩阵 9/10 + §6 统计面——Delete 墓碑/ReclaimDeleted/MaxBytes/Stats）。
/// </summary>
public sealed class TierBlobGovernanceTests
{
    private static async Task<long> GetBytesAsync(TierBlob blob) => (await blob.GetStatsAsync()).Bytes;

    // ══ 矩阵 9：Delete/List——Delete 后 Get 不可达 + List 不见 + 空间保留；
    //             ReclaimDeleted 后 Bytes 下降（死亡前缀头截断）══

    [Fact]
    public async Task DeleteTombstone_ListInvisible_SpaceRetainedUntilReclaim()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(vol);

        var h1 = (await blob.PutAsync(TierBlobTestFactory.MakeData(1000, 0x01))).ObjectId;
        var h2 = (await blob.PutAsync(TierBlobTestFactory.MakeData(2000, 0x02))).ObjectId;
        var h3 = (await blob.PutAsync(TierBlobTestFactory.MakeData(500, 0x03))).ObjectId;
        var bytesAll = await GetBytesAsync(blob);

        await blob.DeleteAsync(h1);
        await blob.DeleteAsync(h1);   // 幂等

        // Get/GetInfo 不可达；List 不见；空间保留（墓碑——Bytes 不降）
        await FluentActions.Awaiting(async () => await blob.GetAsync(h1, new byte[1000]))
            .Should().ThrowAsync<KeyNotFoundException>();
        (await blob.GetInfoAsync(h1)).State.Should().Be(BlobState.Tombstone);
        var active = await TierBlobTests.CollectAsync(blob.ListAsync());
        active.Select(b => b.ObjectId).Should().Equal(h2, h3);
        (await GetBytesAsync(blob)).Should().Be(bytesAll, "墓碑 = 空间保留（裁定⑥）");

        // ReclaimDeleted：打洞 + 死亡前缀头截断——Bytes 实际下降
        var reclaimed = await blob.ReclaimDeletedAsync();
        reclaimed.Should().BeGreaterThan(0);
        (await GetBytesAsync(blob)).Should().BeLessThan(bytesAll, "死亡前缀截断——Bytes 下降");

        // 存活对象零影响
        var dst2 = new byte[2000];
        await blob.GetAsync(h2, dst2);
        dst2.Should().OnlyContain(b => b == 0x02);
        var dst3 = new byte[500];
        await blob.GetAsync(h3, dst3);
        dst3.Should().OnlyContain(b => b == 0x03);

        // 删除后继续写——新对象地址仍单调（截断头推进不影响写尾）
        var h4 = (await blob.PutAsync(TierBlobTestFactory.MakeData(300, 0x04))).ObjectId;
        h4.CompareTo(h3).Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DeleteUnknownHandle_Throws()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(vol);
        await FluentActions.Awaiting(async () => await blob.DeleteAsync(new TC.Tier.Contracts.Storage.LogicalAddress(7, 42)))
            .Should().ThrowAsync<KeyNotFoundException>();
    }

    // ══ 矩阵 10：MaxBytes——超限 Put/OpenWrite fail-fast；容量终判（不定长完成时点）══

    [Fact]
    public async Task MaxBytes_FailFast()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(
            vol, configure: o => o with { MaxBytes = 4096 });

        var first = await blob.PutAsync(TierBlobTestFactory.MakeData(3000, 0x01));   // 帧 3072B < 4096——可写

        // Put 超限 fail-fast
        await FluentActions.Awaiting(async () => await blob.PutAsync(TierBlobTestFactory.MakeData(3000, 0x02)))
            .Should().ThrowAsync<InvalidOperationException>("已用 3072 + 待写 3072 > 4096");

        // OpenWrite 定长超限——开门即拒
        await FluentActions.Awaiting(() => Task.Run(() => blob.OpenWrite(expectedLength: 8192)))
            .Should().ThrowAsync<InvalidOperationException>();

        // 不定长流：容量完成时点终判——超限即回滚
        var dynamicHandle = default(TC.Tier.Contracts.Storage.LogicalAddress);
        await using (var session = blob.OpenWrite())
        {
            dynamicHandle = session.ObjectId;
            await session.WriteAsync(TierBlobTestFactory.MakeData(2000, 0x03));
            await FluentActions.Awaiting(async () => await session.CompleteAsync(ct: default))
                .Should().ThrowAsync<InvalidOperationException>("写入后 5120 > 4096——终判拒绝");
        }
        await FluentActions.Awaiting(async () => await blob.GetInfoAsync(dynamicHandle))
            .Should().ThrowAsync<KeyNotFoundException>("终判拒绝的会话已回滚");

        // 首对象不受影响
        var dst = new byte[3000];
        await blob.GetAsync(first.ObjectId, dst);
        dst.Should().OnlyContain(b => b == 0x01);
    }

    // ══ §6 统计面：ObjectCount/TombstoneCount/Bytes/ReclaimableBytes ══

    [Fact]
    public async Task Stats_ReflectsLifecycle()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(vol);

        var empty = await blob.GetStatsAsync();
        empty.ObjectCount.Should().Be(0);
        empty.TombstoneCount.Should().Be(0);
        empty.Bytes.Should().Be(0);

        var h1 = (await blob.PutAsync(TierBlobTestFactory.MakeData(1000, 0x01))).ObjectId;
        var h2 = (await blob.PutAsync(TierBlobTestFactory.MakeData(2000, 0x02))).ObjectId;
        var h3 = (await blob.PutAsync(TierBlobTestFactory.MakeData(500, 0x03))).ObjectId;

        var full = await blob.GetStatsAsync();
        full.ObjectCount.Should().Be(3);
        full.TombstoneCount.Should().Be(0);
        full.Bytes.Should().BeGreaterThan(0);
        full.ReclaimableBytes.Should().Be(0);
        full.TailAddress.CompareTo(full.HeadAddress).Should().BeGreaterThan(0);

        await blob.DeleteAsync(h1);   // 删前缀对象——死亡前缀头截断可见（Bytes 实际下降）
        var afterDelete = await blob.GetStatsAsync();
        afterDelete.ObjectCount.Should().Be(2);
        afterDelete.TombstoneCount.Should().Be(1);
        afterDelete.ReclaimableBytes.Should().BeGreaterThan(0, "墓碑帧长计入可回收");

        await blob.ReclaimDeletedAsync();
        var afterReclaim = await blob.GetStatsAsync();
        afterReclaim.ObjectCount.Should().Be(2, "墓碑记录保留（表上仍可见）");
        afterReclaim.TombstoneCount.Should().Be(1);
        afterReclaim.Bytes.Should().BeLessThan(afterDelete.Bytes, "死亡前缀截断——Bytes 下降");
    }

    // ══ FlushAsync 落盘屏障幂等（数据引擎 fsync；空实例亦可用）══

    [Fact]
    public async Task FlushAsync_IdempotentBarrier()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(vol);

        await blob.FlushAsync(default);
        var h = (await blob.PutAsync(TierBlobTestFactory.MakeData(100, 0x01))).ObjectId;
        await blob.FlushAsync(default);
        await blob.FlushAsync(default);

        var dst = new byte[100];
        await blob.GetAsync(h, dst);
        dst.Should().OnlyContain(b => b == 0x01);
    }
}
