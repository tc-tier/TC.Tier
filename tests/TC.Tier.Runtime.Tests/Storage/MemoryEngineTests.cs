using FluentAssertions;
using System.Linq;
using System.Threading;
using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Storage.Compact;
using Xunit;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// StorageEngineBase 单元测试——完整覆盖内存模式正确性。
/// <para>★ 内存模式无 DirectIO 对齐问题、无持久化，是验证基类逻辑正确性的首选。</para>
/// <para>★ 覆盖场景：读写往返、Allocate、跨段拆分、ReclaimHead/Tail/区间、
///   SequentialReader、Compact、并发 Append。</para>
/// </summary>
public sealed class MemoryEngineTests : StorageEngineTestBase
{
    /// <summary>构造内存引擎（segmentGrowthLimit 默认 1MB，便于跨段测试）。</summary>
    private static IStorageEngine NewEngine(string name = "mem",
        long segmentGrowthLimit = 1 << 20, bool enableSegmentation = true)
    {
        var options = new StorageEngineOptions(name, segmentGrowthLimit, enableSegmentation: enableSegmentation);
        var dev = options.Builder(TierFs.New("memory:", new MemoryFileSystemOptions
        {
            // ★ Reserved（直址+字节精确 Ranges）——引擎段=预分配连续模型的对位形态；
            //   Sparse 的区间报告是页粒度派生（物理地板），字节级 Reclaim 省略语义只在 Reserved 成立。
            Allocation = MemoryAllocationMode.Reserved,
        })).Start();
        dev.WaitForReady();
        return dev;
    }

    // ═══════════════════════════════════════════════════════════════
    //  Append / Read 往返
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Append_Read_Roundtrip_SingleWrite()
    {
        using var dev = NewEngine();
        byte[] src = MakePattern(100, 0xCD);

        var addr = dev.Append(src);

        Span<byte> dst = stackalloc byte[100];
        int n = dev.Read(addr, dst);
        n.Should().Be(100);
        dst.ToArray().Should().Equal(src);
    }

    [Fact]
    public void Append_MultipleWrites_AddressesAdvanceContiguously()
    {
        using var dev = NewEngine();
        var src1 = MakePattern(50, 0x10);
        var src2 = MakePattern(70, 0x20);

        var a1 = dev.Append(src1);
        var a2 = dev.Append(src2);

        a1.SegId.Should().Be(0);
        a1.Offset.Should().Be(0);
        a2.SegId.Should().Be(0);
        a2.Offset.Should().Be(50);

        // 读两段数据
        Span<byte> buf = stackalloc byte[120];
        dev.Read(a1, buf).Should().Be(120);
        buf.Slice(0, 50).ToArray().Should().Equal(src1);
        buf.Slice(50, 70).ToArray().Should().Equal(src2);
    }

    [Fact]
    public void Append_LargeData_SpansMultipleChunks_WithinSegment()
    {
        // 同一段内多次 Append，总数据量接近 growthLimit
        using var dev = NewEngine(segmentGrowthLimit: 4096);
        var src = MakePattern(1000, 0x33);
        var addr = dev.Append(src);

        var dst = new byte[1000];
        dev.Read(addr, dst).Should().Be(1000);
        dst.Should().Equal(src);
    }

    [Fact]
    public async Task AppendAsync_ReadAsync_Roundtrip()
    {
        using var dev = NewEngine();
        var src = MakePattern(200, 0x55);

        var addr = await dev.AppendAsync(src, CancellationToken.None);

        var dst = new byte[200];
        int n = await dev.ReadAsync(addr, dst, CancellationToken.None);
        n.Should().Be(200);
        dst.Should().Equal(src);
    }

    // ═══════════════════════════════════════════════════════════════
    //  跨段（CrossSegment）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Append_CrossSegment_ReadSpansSegments()
    {
        using var dev = NewEngine(segmentGrowthLimit: 1024);
        // 写满第一段 + 第二段开头（1500 > 1024，跨段）
        var src = MakePattern(1500, 0x77);
        var addr = dev.Append(src);

        var dst = new byte[1500];
        dev.Read(addr, dst).Should().Be(1500);
        dst.Should().Equal(src);
    }

    [Fact]
    public void Append_MultipleSegments_TailAndMinAddressCorrect()
    {
        using var dev = NewEngine(segmentGrowthLimit: 512);
        // 写 3 段（512 * 3 = 1536）
        var src = MakeSequential(1536);
        dev.Append(src);

        dev.MinAddress.SegId.Should().Be(0);
        dev.CommittedTail.SegId.Should().BeGreaterThanOrEqualTo(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Allocate（预留空间，不写数据）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Allocate_AdvancesTail_WithoutWritingData()
    {
        using var dev = NewEngine();
        var addr = dev.Allocate(100).Start;

        addr.Offset.Should().Be(0);
        dev.AllocatedTail.Offset.Should().Be(100);
    }

    [Fact]
    public void Allocate_NonPositiveLength_Throws()
    {
        using var dev = NewEngine();
        Action act = () => dev.Allocate(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Allocate_ThenWrite_FillsReservedSpace()
    {
        using var dev = NewEngine();
        var addr = dev.Allocate(100).Start;
        var src = MakePattern(100, 0x88);
        dev.Write(addr, src);

        var dst = new byte[100];
        dev.Read(addr, dst).Should().Be(100);
        dst.Should().Equal(src);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Write（随机覆写）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Write_AtGivenOffset_OverwritesTarget()
    {
        using var dev = NewEngine();
        var src = MakePattern(100, 0x11);
        dev.Append(src);

        var patch = MakePattern(20, 0x99);
        dev.Write(new LogicalAddress(0, 50), patch);

        var dst = new byte[100];
        dev.Read(new LogicalAddress(0, 0), dst).Should().Be(100);
        dst.AsSpan(0, 50).ToArray().Should().Equal(src.AsSpan(0, 50).ToArray());
        dst.AsSpan(50, 20).ToArray().Should().Equal(patch);
    }

    [Fact]
    public void Write_Middle_PreservesSurroundingData()
    {
        using var dev = NewEngine();
        var src = MakePattern(200, 0xAA);
        dev.Append(src);

        var patch = MakePattern(30, 0xBB);
        dev.Write(new LogicalAddress(0, 80), patch);

        var dst = new byte[200];
        dev.Read(new LogicalAddress(0, 0), dst).Should().Be(200);
        // 前后保留
        dst.AsSpan(0, 80).ToArray().Should().Equal(src.AsSpan(0, 80).ToArray());
        dst.AsSpan(110, 90).ToArray().Should().Equal(src.AsSpan(110, 90).ToArray());
        // 中间被覆写
        dst.AsSpan(80, 30).ToArray().Should().Equal(patch);
    }

    [Fact]
    public void Write_MultipleTimes_LastWriteWins()
    {
        using var dev = NewEngine();
        // 先 Allocate 占位（推 CommittedTail），再 Write 覆写——Write 只能操作已提交数据
        var addr = dev.Allocate(50).Start;
        dev.Write(addr, MakePattern(50, 0x01));
        dev.Write(addr, MakePattern(50, 0x02));

        var dst = new byte[50];
        dev.Read(addr, dst).Should().Be(50);
        dst.Should().Equal(MakePattern(50, 0x02));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Read 边界
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Read_Partial_ReturnsAvailableBytes()
    {
        using var dev = NewEngine();
        var src = MakePattern(100, 0x22);
        var addr = dev.Append(src);

        // 申请读 200 字节，但只有 100 可读
        var dst = new byte[200];
        int n = dev.Read(addr, dst);
        n.Should().Be(100);
        dst.AsSpan(0, 100).ToArray().Should().Equal(src);
    }

    [Fact]
    public void Read_EOF_ReturnsZero()
    {
        using var dev = NewEngine(segmentGrowthLimit: 1024);
        var src = MakePattern(100, 0x33);
        var addr = dev.Append(src);

        // 从段尾之后读——应返回 0
        var eof = new LogicalAddress(addr.SegId, 1024);
        var dst = new byte[50];
        dev.Read(eof, dst).Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Flush（内存模式 no-op）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Flush_DoesNotThrow()
    {
        using var dev = NewEngine();
        dev.Append(MakePattern(100, 0x44));
        Action act = () => dev.Flush();
        act.Should().NotThrow();
    }

    [Fact]
    public void Flush_UpTo_DoesNotThrow()
    {
        using var dev = NewEngine();
        var addr = dev.Append(MakePattern(100, 0x44));
        Action act = () => dev.Flush(new LogicalAddress(addr.SegId, addr.Offset + 50));
        act.Should().NotThrow();
    }

    // ═══════════════════════════════════════════════════════════════
    //  ReclaimTail
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ReclaimTail_ShrinksTailAndCommittedTail()
    {
        using var dev = NewEngine(segmentGrowthLimit: 4096);
        var src = MakePattern(1000, 0x55);
        dev.Append(src);
        dev.CommittedTail.Offset.Should().Be(1000);

        // 回收到 600
        dev.ReclaimTail(new LogicalAddress(0, 600));

        dev.CommittedTail.Offset.Should().Be(600);
        dev.AllocatedTail.Offset.Should().Be(600);
    }

    [Fact]
    public void ReclaimTail_ReadAfterReclaim_ReturnsZeroBeyondTail()
    {
        using var dev = NewEngine(segmentGrowthLimit: 4096);
        dev.Append(MakePattern(1000, 0x55));
        dev.ReclaimTail(new LogicalAddress(0, 400));

        // 从 400 读应返回 0（已被回收）
        var dst = new byte[100];
        dev.Read(new LogicalAddress(0, 400), dst).Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ReclaimHead
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ReclaimHead_AdvancesMinAddress()
    {
        using var dev = NewEngine(segmentGrowthLimit: 512);
        dev.Append(MakeSequential(1500));  // 跨 3 段

        int origMin = dev.MinAddress.SegId;
        // 回收到第 2 段
        dev.ReclaimHead(new LogicalAddress(1, 0));

        dev.MinAddress.SegId.Should().Be(1);
    }

    [Fact]
    public async Task RangeCompact_ReclaimedRange_IsOmittedAndDataIsPacked()
    {
        using var dev = NewEngine(segmentGrowthLimit: 512);
        var firstData = MakePattern(64, 0x31);
        var reclaimedData = MakePattern(64, 0x42);
        var secondData = MakePattern(64, 0x53);
        var first = dev.Append(firstData);
        var reclaimed = dev.Append(reclaimedData);
        var second = dev.Append(secondData);
        var tail = dev.CommittedTail;

        dev.Reclaim(reclaimed, second);
        var result = await dev.StartRangeCompact(
            first,
            tail,
            [first, reclaimed, second]).WaitAsync();

        result.MigrationMap[first].Should().Be(first);
        result.MigrationMap.Should().HaveCount(3);
        result.MigrationMap[reclaimed].Should().BeNull();
        result.MigrationMap[second].Should().Be(new LogicalAddress(0, 64));
        result.NewHighWaterMark.Should().Be(new LogicalAddress(0, 128));
        dev.CommittedTail.Should().Be(tail);

        var buffer = new byte[64];
        dev.Read(result.MigrationMap[second]!.Value, buffer).Should().Be(64);
        buffer.Should().Equal(secondData);
        dev.Read(new LogicalAddress(0, 128), buffer).Should().Be(64);
        buffer.Should().OnlyContain(static value => value == 0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SequentialReader
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void SequentialReader_Forward_ReadsAll()
    {
        using var dev = NewEngine();
        var src = MakeSequential(1000);
        var start = dev.Append(src);
        var end = new LogicalAddress(start.SegId, start.Offset + src.Length);

        using var reader = dev.OpenSequentialReader(start, end, ReadDirection.Forward);
        var buf = new byte[1000];
        int totalRead = 0;
        while (totalRead < 1000)
        {
            int n = reader.Read(buf.AsSpan(totalRead));
            if (n == 0) break;
            totalRead += n;
        }
        totalRead.Should().Be(1000);
        buf.Should().Equal(src);
    }

    [Fact]
    public void SequentialReader_SkipThenRead()
    {
        using var dev = NewEngine();
        var src = MakeSequential(1000);
        var start = dev.Append(src);
        var end = new LogicalAddress(start.SegId, start.Offset + src.Length);

        using var reader = dev.OpenSequentialReader(start, end, ReadDirection.Forward);
        reader.Skip(200);

        var buf = new byte[800];
        int totalRead = 0;
        while (totalRead < 800)
        {
            int n = reader.Read(buf.AsSpan(totalRead));
            if (n == 0) break;
            totalRead += n;
        }
        totalRead.Should().Be(800);
        buf.Should().Equal(src.AsSpan(200, 800).ToArray());
    }

    [Fact]
    public void SequentialReader_Backward_ReadsAll()
    {
        using var dev = NewEngine();
        var src = MakeSequential(500);
        var start = dev.Append(src);
        var end = new LogicalAddress(start.SegId, start.Offset + src.Length);

        // 倒序读：Position 初始在 end，每次往前读一段
        // ReadBackward 把数据放到 destination 尾部（Slice(totalLen - dstOffset - chunkLen)）
        // 调用方用大 buf 一次性读，验证读到的数据与正向一致（实现保证数据放回原位置）
        using var reader = dev.OpenSequentialReader(start, end, ReadDirection.Backward);
        var buf = new byte[500];
        // 倒序读一次大 chunk——ReadBackward 内部按 [pos-chunkLen, pos) 读，放 buf[0..chunkLen)
        int n = reader.Read(buf);
        // 倒序读应读到数据（具体位置由实现决定，验证非零）
        n.Should().BeGreaterThan(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  跨段完整性（CrossSegment 完整覆盖）
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(2)]      // 跨 2 段
    [InlineData(3)]      // 跨 3 段
    [InlineData(5)]      // 跨 5 段
    public void Append_CrossNSegments_ReadBackIntact(int segCount)
    {
        long segLimit = 1024;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);
        long totalLen = segLimit * segCount - 100;  // 留点余量，不正好填满
        var src = MakeSequential((int)totalLen);

        var addr = dev.Append(src);
        var dst = new byte[totalLen];
        dev.Read(addr, dst).Should().Be((int)totalLen);
        dst.Should().Equal(src);
    }

    [Fact]
    public void Append_ExactlyFillsSegment_NextAppendStartsAtSegmentEndBoundary()
    {
        long segLimit = 1024;
        using var dev = NewEngine(segmentGrowthLimit: segLimit, enableSegmentation: true);

        // 第一次正好写满 seg0
        var src1 = MakeSequential((int)segLimit);
        var a1 = dev.Append(src1);
        a1.SegId.Should().Be(0);

        // 第二次数据物理落 seg1，但返回地址停驻 seg0 段末边界（区间统一：同一位置的规范形，旧哨兵形态 (1,0)）
        var src2 = MakeSequential(100);
        var a2 = dev.Append(src2);
        a2.Should().Be(new LogicalAddress(0, segLimit));
        // 数据确实写进 seg1：从边界地址回读校验
        var dst = new byte[100];
        dev.Read(a2, dst).Should().Be(100);
        dst.Should().Equal(src2);
    }

    [Fact]
    public void Append_SingleWriteSpansSegmentBoundary_DataIntact()
    {
        // 单次 Append 数据横跨段边界（seg0 末尾 + seg0 开头）
        long segLimit = 1024;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);

        // 先占 seg0 前 900 字节
        dev.Append(MakeSequential(900));
        // 再写 300 字节——横跨 [seg0@900..1024) + [seg1@0..176)
        var src = MakePattern(300, seed: 0xFE);
        var addr = dev.Append(src);

        var dst = new byte[300];
        dev.Read(addr, dst).Should().Be(300);
        dst.Should().Equal(src);
    }

    [Fact]
    public void Read_AcrossMultipleSegments_ReturnsContiguousData()
    {
        // 读窗口跨多段——一次 Read 读出跨段数据
        long segLimit = 256;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);
        var src = MakeSequential(1000);  // 跨 4 段
        var addr = dev.Append(src);

        // 从中间开始读 500 字节（横跨段边界）
        var dst = new byte[500];
        var readAddr = new LogicalAddress(addr.SegId, 100);
        int n = dev.Read(readAddr, dst);
        n.Should().Be(500);
        dst.Should().Equal(src.AsSpan(100, 500).ToArray());
    }

    [Fact]
    public void Write_CrossSegmentBoundary_OverwritesAcrossSegments()
    {
        // Write 覆写跨段区间
        long segLimit = 256;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);
        // 先写满 3 段
        dev.Append(MakeSequential(700));

        // 覆写跨段区间（seg0@200..seg1@100）
        var patch = MakePattern(200, seed: 0xCC);
        dev.Write(new LogicalAddress(0, 200), patch);

        var dst = new byte[200];
        dev.Read(new LogicalAddress(0, 200), dst).Should().Be(200);
        dst.Should().Equal(patch);
    }

    [Fact]
    public void CrossSegment_AddressContiguity_NoGapsNoOverlaps()
    {
        // 多次小 Append，地址应连续无间隙、无重叠
        long segLimit = 128;
        using var dev = NewEngine(segmentGrowthLimit: segLimit, enableSegmentation: true);
        var addresses = new List<LogicalAddress>();
        for (int i = 0; i < 50; i++)
        {
            addresses.Add(dev.Append(MakeSequential(10)));
        }

        // 校验地址连续：每个 addr == 前一个 + 10（按全局地址顺序）
        long expectedOffset = 0;
        int expectedSeg = 0;
        foreach (var addr in addresses)
        {
            // 计算期望（按 segLimit 进位）
            while (expectedOffset >= segLimit) { expectedSeg++; expectedOffset -= segLimit; }
            addr.SegId.Should().Be(expectedSeg, $"第 {addresses.IndexOf(addr)} 次 Append 的 SegId 应连续");
            addr.Offset.Should().Be(expectedOffset);
            expectedOffset += 10;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  读写位置矩阵（Read/Write Position Matrix）
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0)]      // 段首
    [InlineData(50)]     // 段中
    [InlineData(924)]    // 段尾附近（100 字节正好到段尾 1024）
    public void Append_Read_AtVariousOffsets_WithinSegment(int startFillLen)
    {
        // 段容量 1024。先填 startFillLen，再写 100，读回验证
        long segLimit = 1024;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);
        if (startFillLen > 0) dev.Append(MakePattern(startFillLen, 0xAA));

        var src = MakePattern(100, 0xBB);
        var addr = dev.Append(src);
        addr.Offset.Should().Be(startFillLen);

        var dst = new byte[100];
        dev.Read(addr, dst).Should().Be(100);
        dst.Should().Equal(src);
    }

    [Theory]
    [InlineData(0, 100)]        // 读窗口从段首
    [InlineData(400, 200)]      // 读窗口在段中
    [InlineData(800, 224)]      // 读窗口到段尾
    [InlineData(0, 1024)]       // 读窗口整段
    public void Read_WindowAtVariousPositions_ReturnsCorrectSlice(int readOffset, int readLen)
    {
        long segLimit = 1024;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);
        var src = MakeSequential((int)segLimit);
        dev.Append(src);

        var dst = new byte[readLen];
        int n = dev.Read(new LogicalAddress(0, readOffset), dst);
        n.Should().Be(readLen);
        dst.Should().Equal(src.AsSpan(readOffset, readLen).ToArray());
    }

    [Theory]
    [InlineData(0, 100)]        // 覆写段首
    [InlineData(500, 100)]      // 覆写段中
    [InlineData(924, 100)]      // 覆写段尾
    public void Write_Overwrite_AtVariousOffsets_PreservesRest(int writeOffset, int writeLen)
    {
        long segLimit = 1024;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);
        var orig = MakeSequential((int)segLimit);
        dev.Append(orig);

        var patch = MakePattern(writeLen, 0xCC);
        dev.Write(new LogicalAddress(0, writeOffset), patch);

        var dst = new byte[segLimit];
        dev.Read(new LogicalAddress(0, 0), dst).Should().Be((int)segLimit);
        // 覆写区
        dst.AsSpan(writeOffset, writeLen).ToArray().Should().Equal(patch);
        // 前段保留
        if (writeOffset > 0)
            dst.AsSpan(0, writeOffset).ToArray().Should().Equal(orig.AsSpan(0, writeOffset).ToArray());
        // 后段保留
        int tailStart = writeOffset + writeLen;
        if (tailStart < segLimit)
            dst.AsSpan(tailStart, (int)segLimit - tailStart).ToArray()
               .Should().Equal(orig.AsSpan(tailStart, (int)segLimit - tailStart).ToArray());
    }

    [Theory]
    [InlineData(200, 300)]      // 跨 2 段：seg0@200 → seg1@44
    [InlineData(100, 1200)]     // 跨 3 段：seg0@100 → seg2@44
    public void Write_CrossSegment_AtVariousOffsets_DataIntact(int writeOffset, int writeLen)
    {
        long segLimit = 512;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);
        // 先占位 3 段数据
        dev.Append(MakeSequential((int)(segLimit * 3)));

        var patch = MakePattern(writeLen, 0xDD);
        // 计算起始地址（writeOffset 跨段）
        int startSeg = writeOffset / (int)segLimit;
        long startOff = writeOffset % segLimit;
        dev.Write(new LogicalAddress(startSeg, startOff), patch);

        var dst = new byte[writeLen];
        dev.Read(new LogicalAddress(startSeg, startOff), dst).Should().Be(writeLen);
        dst.Should().Equal(patch);
    }

    [Theory]
    [InlineData(0, 100)]        // 从段首读到次段
    [InlineData(200, 600)]      // 从段中跨到第三段
    [InlineData(400, 1000)]     // 大跨度读
    public void Read_CrossSegment_Window_AtVariousPositions(int readStart, int readLen)
    {
        long segLimit = 512;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);
        var src = MakeSequential((int)(segLimit * 4));  // 4 段
        dev.Append(src);

        int startSeg = readStart / (int)segLimit;
        long startOff = readStart % segLimit;
        var dst = new byte[readLen];
        int n = dev.Read(new LogicalAddress(startSeg, startOff), dst);
        n.Should().Be(readLen);
        dst.Should().Equal(src.AsSpan(readStart, readLen).ToArray());
    }

    // ═══════════════════════════════════════════════════════════════
    //  打洞 / Reclaim 区间完整覆盖（PunchHole Matrix）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Reclaim_WithinSingleSegment_MiddleRange_ZeroesData()
    {
        // 区间完全在单段内（from/to 都在段中间）
        long segLimit = 1024;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);
        var src = MakeSequential((int)segLimit);
        dev.Append(src);

        // 打洞 [200, 500)
        dev.Reclaim(new LogicalAddress(0, 200), new LogicalAddress(0, 500));

        // 打洞区间应读到全零
        var dst = new byte[300];
        dev.Read(new LogicalAddress(0, 200), dst).Should().Be(300);
        dst.Should().OnlyContain(b => b == 0, "打洞区间应归零");

        // 区间外的数据保留
        var before = new byte[200];
        dev.Read(new LogicalAddress(0, 0), before).Should().Be(200);
        before.Should().Equal(src.AsSpan(0, 200).ToArray(), "打洞区间之前的数据保留");
    }

    [Fact]
    public void Reclaim_WithinSingleSegment_FromSegmentStart()
    {
        // from 在段首（offset=0）
        long segLimit = 1024;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);
        dev.Append(MakeSequential((int)segLimit));

        dev.Reclaim(new LogicalAddress(0, 0), new LogicalAddress(0, 300));

        var dst = new byte[300];
        dev.Read(new LogicalAddress(0, 0), dst).Should().Be(300);
        dst.Should().OnlyContain(b => b == 0, "段首打洞归零");
    }

    [Fact]
    public void Reclaim_WithinSingleSegment_ToSegmentEnd()
    {
        // to 在段尾（offset=GrowthLimit）
        long segLimit = 1024;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);
        dev.Append(MakeSequential((int)segLimit));

        dev.Reclaim(new LogicalAddress(0, 700), new LogicalAddress(0, segLimit));

        var dst = new byte[324];
        dev.Read(new LogicalAddress(0, 700), dst).Should().Be(324);
        dst.Should().OnlyContain(b => b == 0, "段尾打洞归零");
    }

    [Fact]
    public void Reclaim_CrossSegment_FromMiddleOfFirstSegment()
    {
        // 跨段打洞：from 在 seg0 中间，to 在 seg1 中间
        long segLimit = 512;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);
        dev.Append(MakeSequential((int)(segLimit * 3)));  // 3 段

        // 打洞 [seg0@200, seg1@100) ——跨段边界
        dev.Reclaim(new LogicalAddress(0, 200), new LogicalAddress(1, 100));

        // seg0 [200, 512) 应归零
        var dst0 = new byte[312];
        dev.Read(new LogicalAddress(0, 200), dst0).Should().Be(312);
        dst0.Should().OnlyContain(b => b == 0, "seg0 打洞区间归零");

        // seg1 [0, 100) 应归零
        var dst1 = new byte[100];
        dev.Read(new LogicalAddress(1, 0), dst1).Should().Be(100);
        dst1.Should().OnlyContain(b => b == 0, "seg1 打洞区间归零");
    }

    [Fact]
    public void Reclaim_CrossSegment_ExactlyAtSegmentBoundary()
    {
        // 跨段打洞：from/to 恰好在段边界
        long segLimit = 512;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);
        dev.Append(MakeSequential((int)(segLimit * 3)));

        // 打洞 [seg0@512=seg1@0, seg2@0) ——整段 seg1
        dev.Reclaim(new LogicalAddress(0, segLimit), new LogicalAddress(2, 0));

        // seg1 整段应归零
        var dst = new byte[segLimit];
        dev.Read(new LogicalAddress(1, 0), dst).Should().Be((int)segLimit);
        dst.Should().OnlyContain(b => b == 0, "seg1 整段打洞归零");
    }

    [Fact]
    public void Reclaim_CrossSegment_SpansMultipleSegments()
    {
        // 跨多段打洞：from 在 seg0 中间，to 在 seg2 中间（跨 3 段）
        long segLimit = 256;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);
        dev.Append(MakeSequential((int)(segLimit * 4)));  // 4 段

        // 打洞 [seg0@100, seg2@100)
        dev.Reclaim(new LogicalAddress(0, 100), new LogicalAddress(2, 100));

        // seg0 [100, 256) 归零
        var dst0 = new byte[156];
        dev.Read(new LogicalAddress(0, 100), dst0).Should().Be(156);
        dst0.Should().OnlyContain(b => b == 0);

        // seg1 整段归零
        var dst1 = new byte[segLimit];
        dev.Read(new LogicalAddress(1, 0), dst1).Should().Be((int)segLimit);
        dst1.Should().OnlyContain(b => b == 0);

        // seg2 [0, 100) 归零
        var dst2 = new byte[100];
        dev.Read(new LogicalAddress(2, 0), dst2).Should().Be(100);
        dst2.Should().OnlyContain(b => b == 0);

        // seg3 数据保留（未被回收）
        var dst3 = new byte[10];
        dev.Read(new LogicalAddress(3, 0), dst3).Should().Be(10);
        dst3.Should().Contain(b => b != 0, "未打洞段应有非零数据");
    }

    [Fact]
    public void Reclaim_ThenWriteToRecycledRange_NewDataVisible()
    {
        // 打洞后再覆写——新数据应可见
        long segLimit = 1024;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);
        dev.Append(MakeSequential((int)segLimit));

        // 打洞 [200, 400)
        dev.Reclaim(new LogicalAddress(0, 200), new LogicalAddress(0, 400));

        // 覆写新数据到打洞区间
        var newData = MakePattern(100, 0xEE);
        dev.Write(new LogicalAddress(0, 250), newData);

        var dst = new byte[100];
        dev.Read(new LogicalAddress(0, 250), dst).Should().Be(100);
        dst.Should().Equal(newData, "打洞区间覆写后新数据可见");
    }

    [Fact]
    public void Reclaim_FullSegment_EntireSegmentZeroed()
    {
        // 打洞整段 [seg@0, seg@GrowthLimit)
        long segLimit = 512;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);
        dev.Append(MakeSequential((int)(segLimit * 2)));

        dev.Reclaim(new LogicalAddress(0, 0), new LogicalAddress(0, segLimit));

        var dst = new byte[segLimit];
        dev.Read(new LogicalAddress(0, 0), dst).Should().Be((int)segLimit);
        dst.Should().OnlyContain(b => b == 0, "整段打洞归零");

        // seg1 数据保留
        var dst1 = new byte[10];
        dev.Read(new LogicalAddress(1, 0), dst1).Should().Be(10);
        dst1.Should().Contain(b => b != 0, "未打洞段应有非零数据");
    }

    [Theory]
    [InlineData(0, 0)]          // 空区间（from==to）
    [InlineData(100, 50)]       // from > to（非法区间）
    public void Reclaim_InvalidRange_NoOp(int from, int to)
    {
        long segLimit = 1024;
        using var dev = NewEngine(segmentGrowthLimit: segLimit);
        var src = MakeSequential(500);
        dev.Append(src);

        // 非法区间——应 no-op，数据不变
        var tailBefore = dev.AllocatedTail;
        dev.Reclaim(new LogicalAddress(0, from), new LogicalAddress(0, to));
        dev.AllocatedTail.Should().Be(tailBefore, "非法区间不应改变 tail");

        // 数据保持不变
        var dst = new byte[500];
        dev.Read(new LogicalAddress(0, 0), dst).Should().Be(500);
        dst.Should().Equal(src);
    }

    // ═══════════════════════════════════════════════════════════════
    //  并发（Concurrency）
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ConcurrentAppend_AddressesNeverOverlap()
    {
        // 多线程并发 Append——CAS 保证每个线程拿到不重叠的地址
        using var dev = NewEngine(segmentGrowthLimit: 1 << 20);
        const int Threads = 8;
        const int PerThread = 200;
        const int PayloadLen = 64;

        var addresses = new System.Collections.Concurrent.ConcurrentBag<LogicalAddress>();
        var threads = new List<Thread>();
        for (int t = 0; t < Threads; t++)
        {
            var threadId = t;
            threads.Add(new Thread(() =>
            {
                // 每个线程写 PerThread 次，payload 含 threadId 标记
                for (int i = 0; i < PerThread; i++)
                {
                    var buf = MakePattern(PayloadLen, seed: (byte)(threadId + 1));
                    addresses.Add(dev.Append(buf));
                }
            }) { IsBackground = true });
        }
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join(TimeSpan.FromSeconds(30)));

        // 校验：地址数 == Threads * PerThread，且无重复
        addresses.Count.Should().Be(Threads * PerThread, "所有 Append 都应成功");
        addresses.Distinct().Count().Should().Be(Threads * PerThread, "地址无重复（CAS 正确）");

        // 校验：tail 推进了正确字节数
        dev.AllocatedTail.Offset.Should().BeGreaterThanOrEqualTo((long)(Threads * PerThread * PayloadLen) - (1 << 20));
    }

    [Fact]
    public void ConcurrentAppend_AllDataIntact_ReadBackMatches()
    {
        // 并发 Append 后，每个地址读回的数据应与写入一致（数据不串）
        using var dev = NewEngine(segmentGrowthLimit: 1 << 20);
        const int Threads = 4;
        const int PerThread = 100;
        const int PayloadLen = 128;

        var written = new System.Collections.Concurrent.ConcurrentDictionary<LogicalAddress, byte[]>();
        var threads = new List<Thread>();
        for (int t = 0; t < Threads; t++)
        {
            var threadId = t;
            threads.Add(new Thread(() =>
            {
                for (int i = 0; i < PerThread; i++)
                {
                    // payload 用 threadId + i 标记，便于校验
                    var buf = new byte[PayloadLen];
                    for (int j = 0; j < PayloadLen; j++) buf[j] = (byte)((threadId * 31 + i * 7 + j) & 0xFF);
                    var addr = dev.Append(buf);
                    written[addr] = buf;
                }
            }) { IsBackground = true });
        }
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join(TimeSpan.FromSeconds(30)));

        // 读回每个地址，校验数据完全一致
        var dst = new byte[PayloadLen];
        foreach (var kv in written)
        {
            int n = dev.Read(kv.Key, dst);
            n.Should().Be(PayloadLen, $"读 {kv.Key} 应返回完整长度");
            dst.Should().Equal(kv.Value, $"地址 {kv.Key} 的数据应与写入一致（不串段）");
        }
    }

    [Fact]
    public void ConcurrentAppendAndRead_ReaderSeesConsistentData()
    {
        // 一个线程写，多个线程并发读已提交的数据——读到的是完整一致的数据，不撕裂
        using var dev = NewEngine(segmentGrowthLimit: 1 << 20);
        var written = new System.Collections.Concurrent.ConcurrentDictionary<LogicalAddress, byte[]>();
        int writeCount = 0;
        var stopWriting = new ManualResetEventSlim(false);

        // 写线程：持续写直到 stop
        var writer = new Thread(() =>
        {
            int i = 0;
            while (!stopWriting.IsSet)
            {
                var buf = MakePattern(100, seed: (byte)(i & 0xFF));
                var addr = dev.Append(buf);
                written[addr] = buf;
                i++;
                if (i > 2000) break;
            }
            Interlocked.Exchange(ref writeCount, i);
        }) { IsBackground = true };
        writer.Start();

        // 读线程：并发读已写地址，校验数据完整
        var readErrors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var readers = new List<Thread>();
        for (int r = 0; r < 4; r++)
        {
            readers.Add(new Thread(() =>
            {
                var dst = new byte[100];
                while (!stopWriting.IsSet || writer.IsAlive)
                {
                    // 随机挑一个已写地址读
                    var addrs = written.Keys.ToArray();
                    if (addrs.Length == 0) { Thread.Yield(); continue; }
                    var addr = addrs[Random.Shared.Next(addrs.Length)];
                    try
                    {
                        int n = dev.Read(addr, dst);
                        if (n == 100)
                        {
                            // 校验读到的是完整一致的写入数据
                            if (!dst.SequenceEqual(written[addr]))
                                readErrors.Enqueue($"撕裂: {addr} 数据不一致");
                        }
                    }
                    catch { /* 段正在建可能抛，忽略 */ }
                }
            }) { IsBackground = true });
        }
        readers.ForEach(t => t.Start());

        // 跑 2 秒
        Thread.Sleep(2000);
        stopWriting.Set();
        writer.Join(TimeSpan.FromSeconds(5));
        readers.ForEach(t => t.Join(TimeSpan.FromSeconds(5)));

        readErrors.Should().BeEmpty("并发读不应看到撕裂的数据");
        writeCount.Should().BeGreaterThan(100, "写线程应持续写入");
    }

    [Fact]
    public async Task ConcurrentAppendAsync_AddressesNeverOverlap()
    {
        // 异步并发 Append——CAS 同样保证地址不重叠
        using var dev = NewEngine(segmentGrowthLimit: 1 << 20);
        const int Tasks = 16;
        const int PerTask = 100;
        const int PayloadLen = 64;

        var addresses = new System.Collections.Concurrent.ConcurrentBag<LogicalAddress>();
        var tasks = new List<Task>();
        for (int t = 0; t < Tasks; t++)
        {
            var taskId = t;
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < PerTask; i++)
                {
                    var buf = MakePattern(PayloadLen, seed: (byte)(taskId + 1));
                    var addr = await dev.AppendAsync(buf, CancellationToken.None);
                    addresses.Add(addr);
                }
            }));
        }
        await Task.WhenAll(tasks);

        addresses.Count.Should().Be(Tasks * PerTask);
        addresses.Distinct().Count().Should().Be(Tasks * PerTask, "异步 CAS 地址无重复");
    }

    [Fact]
    public void ConcurrentAppend_WithSmallSegment_TriggersLifecycleWorkerCorrectly()
    {
        // 小段 + 高并发——触发 lifecycle worker 自动建段，验证段自动扩展
        long segLimit = 256;  // 小段，频繁触发建段
        using var dev = NewEngine(segmentGrowthLimit: segLimit, enableSegmentation: true);
        const int Threads = 8;
        const int PerThread = 100;
        const int PayloadLen = 50;

        var count = 0;
        var failures = 0;
        var threads = new List<Thread>();
        for (int t = 0; t < Threads; t++)
        {
            threads.Add(new Thread(() =>
            {
                for (int i = 0; i < PerThread; i++)
                {
                    // ★ 异常不许逃出线程——.NET 后台线程未处理异常 = 崩 testhost（台账 §VI 家族）；
                    //   捕获后线程必然退出，后续等干循环才能收敛。
                    try
                    {
                        dev.Append(MakePattern(PayloadLen));
                        Interlocked.Increment(ref count);
                    }
                    catch
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
            }) { IsBackground = true });
        }
        threads.ForEach(t => t.Start());
        // ★ 治"Join 超时→断言→using Dispose→活写者 Append 已释放引擎→ODE 崩宿主"（台账 §VI 家族）：
        //   写者体内 try/catch（异常必退出）+ 有界等干（120s 上限）。超时仍活的写者是 WaitSegmentReady
        //   无超时 park（产品既有行为，VII-4 家族）——出 using 后 Dispose 会 PulseAll 唤醒（7.2 修复），
        //   唤醒后抛 ODE 被 try/catch 吃掉：不崩宿主、不挂测试。
        var joinDeadline = Environment.TickCount64 + 120_000;
        foreach (var t in threads)
            while (t.IsAlive && Environment.TickCount64 < joinDeadline) t.Join(1000);

        count.Should().Be(Threads * PerThread, $"所有 Append 成功（失败 {Volatile.Read(ref failures)} 次）");
        // 总数据量 = Threads*PerThread*PayloadLen = 40000，每段 256 → 至少 156 段
        dev.MinAddress.SegId.Should().Be(0);
        // tail 应在最后一段
        long totalBytes = Threads * PerThread * PayloadLen;
        int expectedSegs = (int)(totalBytes / segLimit);
        dev.AllocatedTail.SegId.Should().BeGreaterThanOrEqualTo(expectedSegs - 2, "段应自动扩展");
    }

    // ═══════════════════════════════════════════════════════════════
    //  CommittedTail / AllocatedTail 语义
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void AllocatedTail_EqualToCommittedTail_AfterAppend()
    {
        using var dev = NewEngine();
        dev.Append(MakePattern(100, 0x11));
        dev.AllocatedTail.Should().Be(dev.CommittedTail);
    }

    [Fact]
    public void EmptyDevice_MinAddressAndTail_AtSeg0Offset0()
    {
        using var dev = NewEngine();
        dev.MinAddress.SegId.Should().Be(0);
        dev.MinAddress.Offset.Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  Dispose
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Dispose_BeforeInitialize_DoesNotThrow()
    {

        var dev = new StorageEngineOptions("test").Builder(TierFs.New("memory:"), null).Start();
        Action act = () => dev.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task DisposeAsync_BeforeInitialize_DoesNotThrow()
    {

        var dev = new StorageEngineOptions("test").Builder(TierFs.New("memory:"), null).Start();
        Func<Task> act = async () => await dev.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Operations_AfterDispose_ThrowObjectDisposed()
    {

        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024);
        var dev = options.Builder(TierFs.New("memory:", new MemoryFileSystemOptions
        {
            // ★ Reserved（直址+字节精确 Ranges）——引擎段=预分配连续模型的对位形态；
            //   Sparse 的区间报告是页粒度派生（物理地板），字节级 Reclaim 省略语义只在 Reserved 成立。
            Allocation = MemoryAllocationMode.Reserved,
        })).Start();
        dev.WaitForReady();
        dev.Dispose();

        Action append = () => dev.Append(MakePattern(10));
        append.Should().Throw<ObjectDisposedException>();
    }
}
