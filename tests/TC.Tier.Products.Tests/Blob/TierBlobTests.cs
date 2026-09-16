using TC.Tier.Contracts.Storage;
using TC.Tier.Runtime.Tests;

namespace TC.Tier.Products.Tests.Blob;

/// <summary>
/// TierBlob 核心流测试（tierblob-spec §8 验证矩阵 1/4/5/12 + GetInfo/List 契约）。
/// <para>介质：mem（TestVolume 默认）——重启语义 = 同卷上 Dispose 实例后 Builder 重建（fs 存活保持数据）。</para>
/// </summary>
public sealed class TierBlobTests
{
    /// <summary>ListAsync 收集助手（地址序快照）。</summary>
    internal static async Task<List<BlobInfo>> CollectAsync(IAsyncEnumerable<BlobInfo> source)
    {
        var list = new List<BlobInfo>();
        await foreach (var info in source) list.Add(info);
        return list;
    }

    // ══ 矩阵 1：Put/Get 往返——小对象句柄直达，值一致；GetInfo 零数据 IO 点查 ══

    [Fact]
    public async Task PutGet_Roundtrip_HandleDirectAccess()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(vol);

        var data = TierBlobTestFactory.MakeData(1000, 0xAB);
        var put = await blob.PutAsync(data);

        put.Length.Should().Be(data.Length);
        put.ObjectId.IsValid.Should().BeTrue("句柄 = 帧流起始 LogicalAddress（地址一等公民）");

        var dst = new byte[data.Length + 64];   // 定长读：dst ≥ Length 即可
        var n = await blob.GetAsync(put.ObjectId, dst);
        n.Should().Be(data.Length);
        dst.AsSpan(0, data.Length).ToArray().Should().Equal(data, "值一致（免帧解析直达读）");

        var info = await blob.GetInfoAsync(put.ObjectId);
        info.ObjectId.Should().Be(put.ObjectId);
        info.Length.Should().Be(data.Length);
        info.State.Should().Be(BlobState.Active);
        info.CreatedTicks.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetAsync_MissingOrDeletedHandle_Throws()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(vol);

        var data = TierBlobTestFactory.MakeData(64, 0x11);
        var put = await blob.PutAsync(data);

        var missing = new LogicalAddress(9, 999);
        await FluentActions.Awaiting(async () => await blob.GetAsync(missing, new byte[64]))
            .Should().ThrowAsync<KeyNotFoundException>("未知句柄");

        await FluentActions.Awaiting(async () => await blob.GetAsync(put.ObjectId, new byte[1]))
            .Should().ThrowAsync<ArgumentException>("dst < Length 定长读契约");

        await blob.DeleteAsync(put.ObjectId);
        await FluentActions.Awaiting(async () => await blob.GetAsync(put.ObjectId, new byte[64]))
            .Should().ThrowAsync<KeyNotFoundException>("墓碑不可读");
    }

    // ══ 矩阵 4：不定长流——expectedLength=-1 动态追加，帧尾 TotalLength 收口 ══

    [Fact]
    public async Task DynamicLengthSession_CompletesWithFooterReconciliation()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(vol);

        var chunks = new List<byte[]>();
        long expectedTotal = 0;
        LogicalAddress handle;
        await using (var session = blob.OpenWrite())
        {
            session.ExpectedLength.Should().Be(-1);
            for (var i = 0; i < 7; i++)
            {
                var chunk = TierBlobTestFactory.MakeData(1000 + i * 37, (byte)(i + 1));
                chunks.Add(chunk);
                await session.WriteAsync(chunk);
                expectedTotal += chunk.Length;
            }
            session.BytesWritten.Should().Be(expectedTotal);
            var put = await session.CompleteAsync(ct: default);
            put.Length.Should().Be(expectedTotal);
            put.ObjectId.Should().Be(session.ObjectId, "句柄 Open 即定，Complete 返还");
            handle = put.ObjectId;
        }

        await using var reader = blob.OpenRead(handle);
        var restored = new List<byte>();
        var buf = new byte[4096];
        int n;
        while ((n = await reader.ReadAsync(buf)) > 0)
            restored.AddRange(buf.Take(n));
        reader.IsVerified.Should().BeTrue("读至对象末尾自动补读补零触发帧尾 CRC64 校验");
        restored.Should().Equal(chunks.SelectMany(c => c), "不定长流全量读回 = 分段写拼接");
    }

    // ══ 矩阵 5：多对象连续帧——非扇区整倍数对象连写，逐个 OpenRead 全量顺序读 = 分段拼接 ══

    [Fact]
    public async Task MultipleConsecutiveObjects_EachFrameIndependentlyReadable()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(vol);

        var sizes = new[] { 100, 511, 513, 4096, 77 };   // 补零路径全覆盖
        var handles = new List<(LogicalAddress Id, byte[] Data)>();
        foreach (var (size, idx) in sizes.Select((s, i) => (s, i)))
        {
            var data = TierBlobTestFactory.MakeData(size, (byte)(idx * 17 + 1));
            var put = await blob.PutAsync(data);
            handles.Add((put.ObjectId, data));
        }

        // 地址严格递增（对象句柄 = 写序帧起点）
        handles.Select(h => h.Id).Should().BeInAscendingOrder();

        // 逐对象全量顺序读
        for (var i = 0; i < handles.Count; i++)
        {
            await using var reader = blob.OpenRead(handles[i].Id);
            var restored = new List<byte>();
            var buf = new byte[1000];
            int n;
            while ((n = await reader.ReadAsync(buf)) > 0)
                restored.AddRange(buf.Take(n));
            reader.IsVerified.Should().BeTrue();
            restored.Should().Equal(handles[i].Data, $"对象 #{i} 帧边界独立——逐帧读回一致");
        }
    }

    // ══ 矩阵 12：地址句柄跨端——重启（同卷重建）后句柄直达，GetAsync 值一致 ══

    [Fact]
    public async Task HandlesSurviveRestart_DirectAccessAcrossSessions()
    {
        using var vol = new TestVolume();
        var data1 = TierBlobTestFactory.MakeData(3000, 0x5A);
        var data2 = TierBlobTestFactory.MakeData(700, 0xA5);
        LogicalAddress h1, h2;

        await using (var blob = await TierBlobTestFactory.StartAsync(vol))
        {
            h1 = (await blob.PutAsync(data1)).ObjectId;
            h2 = (await blob.PutAsync(data2)).ObjectId;
        }

        // 重启（跨"端"——新实例同卷恢复）
        await using (var blob = await TierBlobTestFactory.StartAsync(vol))
        {
            var dst1 = new byte[data1.Length];
            (await blob.GetAsync(h1, dst1)).Should().Be(data1.Length);
            dst1.Should().Equal(data1, "重启后句柄直达（恒等映射读回——帧扇区对齐不变式）");

            var dst2 = new byte[data2.Length];
            (await blob.GetAsync(h2, dst2)).Should().Be(data2.Length);
            dst2.Should().Equal(data2);

            // 重启后继续写——新句柄继续可用且地址单调推进
            var data3 = TierBlobTestFactory.MakeData(1234, 0x33);
            var put3 = await blob.PutAsync(data3);
            put3.ObjectId.CompareTo(h2).Should().BeGreaterThan(0, "写尾恢复后地址继续单调推进");
            var dst3 = new byte[data3.Length];
            await blob.GetAsync(put3.ObjectId, dst3);
            dst3.Should().Equal(data3);
        }
    }

    // ══ List 契约：表序（地址序）交付 + 过滤器 ══

    [Fact]
    public async Task ListAsync_AddressOrder_WithFilters()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(vol);

        var handles = new List<LogicalAddress>();
        for (var i = 0; i < 5; i++)
            handles.Add((await blob.PutAsync(TierBlobTestFactory.MakeData(100 + i, (byte)i))).ObjectId);

        var all = await CollectAsync(blob.ListAsync());
        all.Select(b => b.ObjectId).Should().Equal(handles, "表序 = 地址序");

        await blob.DeleteAsync(handles[1]);
        await blob.DeleteAsync(handles[3]);

        var active = await CollectAsync(blob.ListAsync());
        active.Select(b => b.ObjectId).Should().Equal(handles[0], handles[2], handles[4]);

        var withTombs = await CollectAsync(blob.ListAsync(new BlobListFilter(default, default, IncludeTombstones: true)));
        withTombs.Select(b => b.State).Should().Equal(
            BlobState.Active, BlobState.Tombstone, BlobState.Active, BlobState.Tombstone, BlobState.Active);

        // 地址范围过滤：[handles[2], handles[4]) → 只有 handles[2]（Active）
        var ranged = await CollectAsync(blob.ListAsync(new BlobListFilter(handles[2], handles[4], IncludeTombstones: false)));
        ranged.Select(b => b.ObjectId).Should().Equal(handles[2]);
    }
}
