using TC.Tier.Runtime.Tests;

namespace TC.Tier.Products.Tests.Blob;

/// <summary>
/// TierBlob 会话测试（tierblob-spec §8 验证矩阵 2/3/6——流式大对象/定长契约/Abort 尾截断回滚）。
/// </summary>
public sealed class TierBlobSessionTests
{
    // ══ 矩阵 2：流式会话大对象——分块流式写读，CRC64 校验过；
    //             写路径内存恒定 = 双缓冲会话结构保证（会话 buffer 64KB，数据 2MB >> buffer）══

    [Fact]
    public async Task StreamingSession_LargeObject_ChunkedWriteRead_CrcValid()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(vol);

        const int chunkSize = 64 * 1024;
        const int chunks = 32;   // 2MB >> SessionBufferSize(64KB)——多轮 buffer swap/flush 流水线
        var handle = default(TC.Tier.Contracts.Storage.LogicalAddress);
        await using (var session = blob.OpenWrite())
        {
            for (var i = 0; i < chunks; i++)
                await session.WriteAsync(TierBlobTestFactory.MakeData(chunkSize, (byte)(i % 251)));
            var put = await session.CompleteAsync(ct: default);
            put.Length.Should().Be(chunkSize * chunks);
            handle = put.ObjectId;
        }

        await using (var reader = blob.OpenRead(handle))
        {
            var buf = new byte[chunkSize];
            for (var i = 0; i < chunks; i++)
            {
                var offset = 0;
                while (offset < chunkSize)
                {
                    var n = await reader.ReadAsync(buf.AsMemory(offset, chunkSize - offset));
                    n.Should().BeGreaterThan(0, $"chunk #{i} 未读完不应 EOF");
                    offset += n;
                }
                buf.Should().OnlyContain(b => b == (byte)(i % 251), $"chunk #{i} 值一致");
            }
            (await reader.ReadAsync(buf)).Should().Be(0, "对象读完 = EOF");
            reader.IsVerified.Should().BeTrue("流式读 CRC64 逐帧校验通过");
        }
    }

    [Fact]
    public async Task EmptyObject_PutGetRoundtrip()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(vol);

        var put = await blob.PutAsync(ReadOnlyMemory<byte>.Empty);
        put.Length.Should().Be(0);
        (await blob.GetAsync(put.ObjectId, new byte[8])).Should().Be(0, "空对象 = 0 字节");

        await using var reader = blob.OpenRead(put.ObjectId);
        (await reader.ReadAsync(new byte[8])).Should().Be(0);
        reader.IsVerified.Should().BeTrue("空对象帧校验同样收口");
    }

    // ══ 矩阵 3：定长预分配——OpenWrite(N) 写满 → Complete；不足/超过 N = 定长契约拒绝 ══

    [Fact]
    public async Task FixedLengthSession_UnderfillRejected_ExactFillAccepted()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(vol);

        // 不足 N → Complete 拒绝，会话 Abort（尾截断），对象不存在
        var h1 = default(TC.Tier.Contracts.Storage.LogicalAddress);
        await using (var session = blob.OpenWrite(expectedLength: 4096))
        {
            session.ExpectedLength.Should().Be(4096);
            h1 = session.ObjectId;
            await session.WriteAsync(TierBlobTestFactory.MakeData(3000, 0x11));
            await FluentActions.Awaiting(async () => await session.CompleteAsync(ct: default))
                .Should().ThrowAsync<InvalidOperationException>("定长契约：不足 N 拒绝");
        }

        await FluentActions.Awaiting(async () => await blob.GetInfoAsync(h1))
            .Should().ThrowAsync<KeyNotFoundException>("拒绝的定长会话不产生对象");

        // 恰好 N → Complete 通过
        await using (var session = blob.OpenWrite(expectedLength: 4096))
        {
            var h2 = session.ObjectId;
            await session.WriteAsync(TierBlobTestFactory.MakeData(4096, 0x22));
            var put = await session.CompleteAsync(ct: default);
            put.Length.Should().Be(4096);
            put.ObjectId.Should().Be(h2);
        }

        // 超过 N → Complete 同样拒绝（写超后缓冲已入会话——契约在收口点裁决）
        await using (var session = blob.OpenWrite(expectedLength: 100))
        {
            await session.WriteAsync(TierBlobTestFactory.MakeData(200, 0x33));
            await FluentActions.Awaiting(async () => await session.CompleteAsync(ct: default))
                .Should().ThrowAsync<InvalidOperationException>("定长契约：超过 N 拒绝");
        }

        (await GetBytesAsync(blob)).Should().Be(FrameLengthOf(4096), "仅恰满对象存活");
    }

    private static long FrameLengthOf(int length)
        => TierBlobTestFactory.FrameLengthOf(length, 512);   // mem 卷扇区基准 512

    private static async Task<long> GetBytesAsync(TierBlob blob) => (await blob.GetStatsAsync()).Bytes;

    // ══ 矩阵 6：会话 Abort——Dispose 未 Complete → 尾截断回滚 → 对象不可见 + 空间回退 ══

    [Fact]
    public async Task SessionDisposeWithoutComplete_AbortsAndRollsBack()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(vol);

        var kept = await blob.PutAsync(TierBlobTestFactory.MakeData(1000, 0x01));
        var bytesBefore = await GetBytesAsync(blob);

        var abortedHandle = default(TC.Tier.Contracts.Storage.LogicalAddress);
        await using (var session = blob.OpenWrite())
        {
            abortedHandle = session.ObjectId;
            await session.WriteAsync(TierBlobTestFactory.MakeData(70000, 0x02));   // > buffer：部分数据已 flush
            // 不 Complete——Dispose = Abort
        }

        abortedHandle.CompareTo(kept.ObjectId).Should().BeGreaterThan(0, "Abort 会话的句柄区间在存活对象之后");
        await FluentActions.Awaiting(async () => await blob.GetInfoAsync(abortedHandle))
            .Should().ThrowAsync<KeyNotFoundException>("Abort 回滚——对象不存在");

        var dst = new byte[1000];
        await blob.GetAsync(kept.ObjectId, dst);
        dst.Should().OnlyContain(b => b == 0x01, "Abort 不影响前序对象");

        // 写尾回退：Bytes 回到 Abort 前水平（尾截断回滚——结构层 append 可回滚语义）
        var bytesAfter = await GetBytesAsync(blob);
        bytesAfter.Should().Be(bytesBefore, "尾截断回滚——空间占用线回退");

        // Abort 后写通道立即可复用：新对象从存活尾继续（覆盖 Abort 残段）
        var next = await blob.PutAsync(TierBlobTestFactory.MakeData(500, 0x03));
        next.ObjectId.CompareTo(kept.ObjectId).Should().BeGreaterThan(0);
        var dst3 = new byte[500];
        await blob.GetAsync(next.ObjectId, dst3);
        dst3.Should().OnlyContain(b => b == 0x03);
    }

    [Fact]
    public async Task SessionAfterComplete_DisposeIsNoOp_HandlePersists()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(vol);

        LogicalAddress handle;
        await using (var session = blob.OpenWrite())
        {
            await session.WriteAsync(TierBlobTestFactory.MakeData(128, 0x44));
            handle = (await session.CompleteAsync(ct: default)).ObjectId;
            await FluentActions.Awaiting(async () => await session.WriteAsync(new byte[1]))
                .Should().ThrowAsync<InvalidOperationException>("Complete 后会话已收口——不可再写");
        }

        var dst = new byte[128];
        await blob.GetAsync(handle, dst);
        dst.Should().OnlyContain(b => b == 0x44, "Complete 后 Dispose 幂等——对象持久存活");
    }

    // ══ 写通道互斥：会话存活期独占——PutAsync 排队等待（多生产者串行诚实契约）══

    [Fact]
    public async Task ConcurrentPuts_SerializedByWriteGate_AllRegistered()
    {
        using var vol = new TestVolume();
        await using var blob = await TierBlobTestFactory.StartAsync(vol);

        const int writers = 8;
        var results = new BlobPutResult[writers];
        await Task.WhenAll(Enumerable.Range(0, writers).Select(async i =>
        {
            results[i] = await blob.PutAsync(TierBlobTestFactory.MakeData(512 + i, (byte)(i + 1)));
        }));

        results.Select(r => r.ObjectId).Should().OnlyHaveUniqueItems("写闸串行——地址分配无冲突");
        results.Select(r => r.ObjectId).Should().BeInAscendingOrder("串行写地址单调");
        for (var i = 0; i < writers; i++)
        {
            var dst = new byte[512 + i];
            await blob.GetAsync(results[i].ObjectId, dst);
            dst.Should().OnlyContain(b => b == (byte)(i + 1));
        }
    }
}
