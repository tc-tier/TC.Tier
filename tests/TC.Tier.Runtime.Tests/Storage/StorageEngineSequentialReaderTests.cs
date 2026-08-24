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
}
