namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 顺序读句柄契约测试（OpenSequentialReader——游标 + 读/跳分离、双向、快照/脏读双模式）。
/// <para>★ 倒序语义：Position 初始 = end，每次 Read 返回 Position 之前的 chunk（目的缓冲内字节序自然），
///   Position 随之后退；读到 start 停（EOF=0）。跨段边界正/倒序都要正确进/借位。</para>
/// </summary>
public sealed class StorageEngineSequentialReaderTests : StorageEngineTestBase, IDisposable
{
    private readonly List<TestVolume> _vols = new();

    public void Dispose()
    {
        foreach (var vol in _vols) vol.Dispose();
    }

    private IStorageEngine NewEngine()
    {
        var vol = new TestVolume();
        _vols.Add(vol);
        var options = new StorageEngineOptions("seq-reader", segmentGrowthLimit: 4096)
            .WithPreallocateFile(false);
        var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();
        return dev;
    }

    private static byte[] Rec(byte seed, int size) => MakePattern(size, seed);

    [Fact]
    public void Forward_SequentialChunks_ThenEof()
    {
        using var dev = NewEngine();
        var r1 = dev.Append(Rec(0x11, 128));
        dev.Append(Rec(0x22, 128));
        dev.Append(Rec(0x33, 128));

        using var reader = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail);
        reader.Position.Should().Be(r1, "正序 Position 初始 = start");
        foreach (var seed in new byte[] { 0x11, 0x22, 0x33 })
        {
            var buf = new byte[128];
            reader.Read(buf).Should().Be(128);
            buf.Should().Equal(Rec(seed, 128));
        }
        reader.Read(new byte[64]).Should().Be(0, "读到 end 停（EOF）");
    }

    [Fact]
    public void Forward_CrossSegmentBoundary_WindowSplits()
    {
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 4000));                 // seg0 @0
        dev.Append(Rec(0xEE, 96));                   // 垫满 seg0（4096）
        dev.Append(Rec(0x22, 1000));                 // seg1 @0

        using var reader = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail);
        var first = new byte[4096];                  // 跨读第一窗 = seg0 全量
        reader.Read(first).Should().Be(4096);
        first.Take(4000).Should().Equal(Rec(0x11, 4000));
        first.Skip(4000).Should().Equal(Rec(0xEE, 96));

        var second = new byte[1000];                 // 第二窗 = seg1（正序跨段进位）
        reader.Read(second).Should().Be(1000);
        second.Should().Equal(Rec(0x22, 1000));
    }

    [Fact]
    public void Backward_ChunksInReverseOrder_PositionRetreatsToStart()
    {
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 128));
        dev.Append(Rec(0x22, 128));
        var r3 = dev.Append(Rec(0x33, 128));

        using var reader = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail,
            ReadDirection.Backward);
        reader.Position.Should().Be(dev.CommittedTail, "倒序 Position 初始 = end");

        foreach (var seed in new byte[] { 0x33, 0x22, 0x11 })   // 从尾往头：r3 → r2 → r1
        {
            var buf = new byte[128];
            reader.Read(buf).Should().Be(128, $"倒序读应返回 Position 之前的记录（seed {seed:X2}）");
            buf.Should().Equal(Rec(seed, 128), "chunk 内字节序自然（非镜像）");
        }
        reader.Read(new byte[64]).Should().Be(0, "倒序读到 start 停（EOF）");
        reader.Position.Should().Be(dev.MinAddress);
        _ = r3;
    }

    [Fact]
    public void Backward_CrossSegmentBoundary_BorrowsIntoPreviousSegment()
    {
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 4000));
        dev.Append(Rec(0xEE, 96));                   // 垫满 seg0
        dev.Append(Rec(0x22, 1000));                 // seg1

        using var reader = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail,
            ReadDirection.Backward);
        var tail = new byte[1000];                   // 先读 seg1 全量
        reader.Read(tail).Should().Be(1000);
        tail.Should().Equal(Rec(0x22, 1000));

        var head = new byte[4096];                   // 再读 seg0 全量（倒序跨段借位）
        reader.Read(head).Should().Be(4096);
        head.Take(4000).Should().Equal(Rec(0x11, 4000));
        head.Skip(4000).Should().Equal(Rec(0xEE, 96));
    }

    [Fact]
    public async Task ForwardAsync_EqualsSyncContent()
    {
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 128));
        dev.Append(Rec(0x22, 128));

        using var reader = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail);
        var first = new byte[128];
        (await reader.ReadAsync(first, CancellationToken.None)).Should().Be(128);
        first.Should().Equal(Rec(0x11, 128));
        var second = new byte[128];
        (await reader.ReadAsync(second, CancellationToken.None)).Should().Be(128);
        second.Should().Equal(Rec(0x22, 128));
        (await reader.ReadAsync(new byte[16], CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task BackwardAsync_ChunksInReverseOrder()
    {
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 128));
        dev.Append(Rec(0x22, 128));
        dev.Append(Rec(0x33, 128));

        using var reader = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail,
            ReadDirection.Backward);
        foreach (var seed in new byte[] { 0x33, 0x22, 0x11 })
        {
            var buf = new byte[128];
            (await reader.ReadAsync(buf, CancellationToken.None)).Should().Be(128);
            buf.Should().Equal(Rec(seed, 128));
        }
        (await reader.ReadAsync(new byte[16], CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public void Skip_Forward_AdvancesCursorWithoutReading()
    {
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 128));
        dev.Append(Rec(0x22, 128));
        dev.Append(Rec(0x33, 128));

        using var reader = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail);
        reader.Skip(128);                            // 跳过 r1
        var buf = new byte[128];
        reader.Read(buf).Should().Be(128);
        buf.Should().Equal(Rec(0x22, 128), "Skip 后应读到第二条");
    }

    [Fact]
    public void Skip_Backward_RetreatsCursor()
    {
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 128));
        dev.Append(Rec(0x22, 128));
        dev.Append(Rec(0x33, 128));

        using var reader = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail,
            ReadDirection.Backward);
        reader.Skip(128);                            // 从尾跳过 r3
        var buf = new byte[128];
        reader.Read(buf).Should().Be(128);
        buf.Should().Equal(Rec(0x22, 128), "倒序 Skip 后应读到倒数第二条");
    }

    [Fact]
    public void Seek_JumpsWithinWindow()
    {
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 128));
        var r2 = dev.Append(Rec(0x22, 128));
        dev.Append(Rec(0x33, 128));

        using var reader = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail);
        reader.Seek(r2);
        reader.Position.Should().Be(r2);
        var buf = new byte[128];
        reader.Read(buf).Should().Be(128);
        buf.Should().Equal(Rec(0x22, 128));
    }

    [Fact]
    public void DirtyRead_Forward_WindowReadable()
    {
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 256));

        using var reader = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail,
            ReadDirection.Forward, usePageCache: true, SnapshotMode.DirtyRead);
        var buf = new byte[256];
        reader.Read(buf).Should().Be(256);
        buf.Should().Equal(Rec(0x11, 256));
    }

    // ══ 边界值补全（2026-08-27 Net 对抗层间接撞出两形态后补——窗口/Skip/可见性/截断的
    //    exact 边界确定性单测化，不再依赖上层间歇复现）══

    [Fact]
    public void Forward_EndInsideSegment_StopsExactly()
    {
        // 窗口 end 在段中间（非 CommittedTail 整体）——读到 end 精确停、不多读一字节
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 128));
        dev.Append(Rec(0x22, 128));
        dev.Append(Rec(0x33, 128));

        var endAddr = dev.CalculationAddress(dev.MinAddress, 192);   // 第一条 + 第二条半
        using var reader = dev.OpenSequentialReader(dev.MinAddress, endAddr);
        var buf = new byte[256];
        reader.Read(buf).Should().Be(192, "读到段中 end 精确停");
        buf.Take(128).Should().Equal(Rec(0x11, 128));
        reader.Read(new byte[16]).Should().Be(0, "end 之后 EOF");
    }

    [Fact]
    public void Forward_AfterAppend_ReturnsImmediatelyVisible()
    {
        // ★ 可见性边界（Net Stress 卡 1027 病灶的单测化）：Append 返回后立即可读——
        //   引擎契约：Append 返回 = 数据可读（Committed/读路径一致）；间歇丢读 = 违约。
        //   注：读窗口是打开时快照（追加不扩已开窗口）——可见性由新开 reader 验证
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 128));

        using var reader = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail);
        dev.Append(Rec(0x22, 128));
        reader.Read(new byte[128]).Should().Be(128, "旧窗口仍是快照 [start, 打开时 end)");

        using var reader2 = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail);
        var buf = new byte[256];
        reader2.Read(buf).Should().Be(256, "新窗口含追加后的全部数据（立即可见）");
        buf.Take(128).Should().Equal(Rec(0x11, 128));
        buf.Skip(128).Should().Equal(Rec(0x22, 128));
    }

    [Fact]
    public void Forward_PartialReadAtWindowTail_ReturnsPartialThenEof()
    {
        // 缓冲 > 窗口剩余：返回部分（n < buf.Length），下一轮 EOF——不是 0 也不是挂
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 100));

        using var reader = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail);
        var buf = new byte[256];
        reader.Read(buf).Should().Be(100, "部分读（窗口只有 100 字节）");
        buf.Take(100).Should().Equal(Rec(0x11, 100));
        reader.Read(new byte[16]).Should().Be(0);
    }

    [Fact]
    public void Forward_ThreeSegments_ContinuousCarry()
    {
        // 跨 3 段连续进位（现有只测 2 段——多段链的段号推进）
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 4000));   // seg0
        dev.Append(Rec(0xEE, 96));     // 垫满 seg0
        dev.Append(Rec(0x22, 4000));   // seg1
        dev.Append(Rec(0xDD, 96));     // 垫满 seg1
        dev.Append(Rec(0x33, 1000));   // seg2

        using var reader = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail);
        reader.Skip(4096);              // 跳过 seg0
        var buf = new byte[4096 + 1000];
        reader.Read(buf).Should().Be(4096 + 1000, "跨 seg1+seg2 连续读全");
        buf.Take(4000).Should().Equal(Rec(0x22, 4000));
        buf.Skip(4096).Take(1000).Should().Equal(Rec(0x33, 1000));
    }

    [Fact]
    public void Forward_SkipExactlyToWindowEnd_ReadEof()
    {
        // Skip 恰好 = 窗口剩余（exact 边界）——Position == end → Read EOF；越界 Skip 不挂
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 128));
        dev.Append(Rec(0x22, 128));

        using var reader = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail);
        reader.Skip(256);                       // 恰好全部
        reader.Read(new byte[16]).Should().Be(0);
        reader.Skip(1000);                      // 越界 Skip——不挂（越过即停）
        reader.Read(new byte[16]).Should().Be(0);
    }

    [Fact]
    public void Forward_SkipAcrossSegmentBoundary_ReadsNextSegment()
    {
        // Skip 跨段（跳过量 > 单段剩余）——目标在下下段
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 4000));
        dev.Append(Rec(0xEE, 96));     // 垫满 seg0
        dev.Append(Rec(0x22, 4000));   // seg1
        dev.Append(Rec(0xDD, 96));     // 垫满 seg1
        dev.Append(Rec(0x33, 1000));   // seg2

        using var reader = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail);
        reader.Skip(8192);             // 跳过 seg0+seg1 整段
        var buf = new byte[1000];
        reader.Read(buf).Should().Be(1000);
        buf.Should().Equal(Rec(0x33, 1000));
    }

    [Fact]
    public void Forward_ExactFillSegmentEnd_ThenCarryOnRead()
    {
        // ★ 段末停驻边界（(seg, limit) 规范形）：读到恰好填满段的最后字节 → 停驻段末 →
        //   下一轮 Read 从下段原点继续（不跳字/不重读/不挂）
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 4000));
        dev.Append(Rec(0xEE, 96));     // 垫满 seg0（exact-fill）
        dev.Append(Rec(0x22, 1000));   // seg1

        using var reader = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail);
        var first = new byte[4096];
        reader.Read(first).Should().Be(4096, "恰好读满整段（停驻段末）");
        first.Take(4000).Should().Equal(Rec(0x11, 4000));
        first.Skip(4000).Should().Equal(Rec(0xEE, 96));
        var second = new byte[1000];
        reader.Read(second).Should().Be(1000, "从段末继续 → 下段原点续读");
        second.Should().Equal(Rec(0x22, 1000));
    }

    [Fact]
    public void Forward_WindowStartInsideSegment_ReadsFromStart()
    {
        // 窗口 start 非段首（段中起点）——从 start 精确读到 end
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 4000));
        dev.Append(Rec(0xEE, 96));
        dev.Append(Rec(0x22, 1000));

        var start = dev.CalculationAddress(dev.MinAddress, 100);   // 段中偏移 100
        using var reader = dev.OpenSequentialReader(start, dev.CommittedTail);
        var buf = new byte[4096 - 100 + 1000];
        var n = reader.Read(buf);
        n.Should().Be(4096 - 100 + 1000);
        buf.Take(3900).Should().Equal(Rec(0x11, 4000).Skip(100).Take(3900));
        buf.Skip(3900).Take(96).Should().Equal(Rec(0xEE, 96));
    }

    [Fact]
    public void Forward_AfterTailReclaim_StopsAtNewTail()
    {
        // ★ 尾截断后读（Net 分叉覆盖场景的单测化）：ReclaimTail 回收尾部后——
        //   读到新尾精确停（不进回收区自旋/不挂）
        using var dev = NewEngine();
        var r1 = dev.Append(Rec(0x11, 4000));
        dev.Append(Rec(0xEE, 96));
        dev.Append(Rec(0x22, 1000));   // seg1（将被回收）

        var newTail = dev.CalculationAddress(r1, 4000);   // 第一条数据末（r1 是起点）
        dev.ReclaimTail(newTail);      // 回收第一条之后（seg1 整段 + seg0 尾垫）

        using var reader = dev.OpenSequentialReader(dev.MinAddress, newTail);
        var buf = new byte[8192];
        var n = reader.Read(buf);
        n.Should().Be(4000, "截断后只读到新尾");
        buf.Take(4000).Should().Equal(Rec(0x11, 4000));
        reader.Read(new byte[16]).Should().Be(0);
    }

    [Fact]
    public async Task ForwardAsync_AcrossReclaimedHollow_CompletesBounded()
    {
        // ★ 跨回收段（Hollow）读：终点在 Hollow 之后的新段——30s 有界完成
        //   （占位连续跨过或数据边界停——不自旋不挂：Net 自旋病灶单测化）
        using var dev = NewEngine();
        dev.Append(Rec(0x11, 4000));
        dev.Append(Rec(0xEE, 96));     // 垫满 seg0
        dev.Append(Rec(0x22, 1000));   // seg1
        var keep = dev.CalculationAddress(dev.MinAddress, 4096);   // seg1 起点

        dev.ReclaimTail(keep);         // 回收 seg1（seg0 完整保留）
        dev.Append(Rec(0x33, 500));    // 追加到 seg2（seg1 成 Hollow 占位）

        using var reader = dev.OpenSequentialReader(dev.MinAddress, dev.CommittedTail);
        var buf = new byte[4096 + 500 + 4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var n = await reader.ReadAsync(buf, cts.Token);   // 自旋/挂死即超时暴露
        n.Should().Be(4096 + 500, "seg0 全量 + 跨过 Hollow seg1 + seg2 新数据");
        buf.Take(4000).Should().Equal(Rec(0x11, 4000));
        buf.Skip(4096).Take(500).Should().Equal(Rec(0x33, 500));
    }
}
