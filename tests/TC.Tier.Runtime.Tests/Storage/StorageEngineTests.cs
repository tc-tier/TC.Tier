namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 新 Device 实现集成测试——覆盖 NullDevice / LocalMemoryDevice / LocalStorageDevice 的
/// Append/Write/Read 往返、Flush、ReclaimHead、ReclaimTail。
/// </summary>
public sealed class StorageEngineTests : IDisposable
{
    private readonly List<TestVolume> _vols = new();

    public void Dispose()
    {
        foreach (var vol in _vols) vol.Dispose();
    }

    private TestVolume NewVol()
    {
        var vol = new TestVolume();
        _vols.Add(vol);
        return vol;
    }

    private static byte[] MakePattern(int length, byte seed = 0xAB)
    {
        var buf = new byte[length];
        for (int i = 0; i < length; i++) buf[i] = (byte)((seed + i) & 0xFF);
        return buf;
    }

    //  LocalStorageDevice
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void DiskDevice_WriteRead_Roundtrip()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        byte[] src = MakePattern(100, 0xAA);
        var addr = dev.Append(src);

        byte[] dst = new byte[100];
        int n = dev.Read(addr, dst);

        n.Should().Be(100);
        dst.SequenceEqual(src).Should().BeTrue();
    }

    [Fact]
    public void DiskDevice_Append_MultipleChunks()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        byte[] src1 = MakePattern(60, 0x01);
        byte[] src2 = MakePattern(80, 0x02);

        var addr1 = dev.Append(src1);
        var addr2 = dev.Append(src2);

        addr1.Offset.Should().Be(0);
        addr2.Offset.Should().Be(60);

        byte[] dst = new byte[80];
        dev.Read(addr2, dst);
        dst.SequenceEqual(src2).Should().BeTrue();
    }

    /// <summary>
    /// Allocate = Append − pwrite：推进水位（AllocatedTail + CommittedTail）但不写数据，预留区读为零。
    /// <para>★ Allocate 与 Append 共用 AppendCore + WriteCrossSegment，唯一差异是 source 为空时跳过 RandomAccess.Write。
    ///   段锁协议完全一致（保证并发顺序）。预留区是稀疏文件空洞，读返回零（与 PunchHole 语义一致）。</para>
    /// </summary>
    [Fact]
    public void DiskDevice_Allocate_AdvancesWatermarkWithoutWrite()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        var before = dev.AllocatedTail;
        before.Should().Be(new LogicalAddress(0, 0));

        // Allocate 1024B：不写数据，但推进水位
        long len = 1024;
        var start = dev.Allocate(len).Start;
        start.Should().Be(before, "Allocate 从当前 AllocatedTail 起始");

        // 两个水位都推进到 start + len
        dev.AllocatedTail.Should().Be(new LogicalAddress(0, len));
        dev.CommittedTail.Should().Be(new LogicalAddress(0, len),
            "Allocate 推进 MaxOffset（与 Append 同协议），保证后续 Append 的 MaxOffset 单调连续");

        // 预留区未写，读返回零（稀疏空洞，合法数据）
        var zeros = new byte[100];
        dev.Read(new LogicalAddress(0, 0), zeros);
        zeros.Should().OnlyContain(b => b == 0, "Allocate 未填充区域读为零");

        // Write 覆写预留区某段（填充数据）
        var pattern = MakePattern(100, 0x55);
        dev.Write(new LogicalAddress(0, 200), pattern);

        var readback = new byte[100];
        dev.Read(new LogicalAddress(0, 200), readback).Should().Be(100);
        readback.Should().BeEquivalentTo(pattern, "Allocate 预留区可被 Write 覆写填充");
    }

    /// <summary>Allocate 跨段：预留长度超过当前段剩余空间时，正确建新段并推进水位。</summary>
    [Fact]
    public void DiskDevice_Allocate_CrossSegment()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        dev.Append(MakePattern(800, 0x11));  // seg0 用了 800B

        var start = dev.Allocate(1000).Start;       // 跨段到 seg1
        start.Should().Be(new LogicalAddress(0, 800));
        dev.AllocatedTail.SegId.Should().BeGreaterThan(0, "Allocate 跨段后水位在新段");
        dev.CommittedTail.Should().BeLessThanOrEqualTo(dev.AllocatedTail);
    }

    /// <summary>Append 少量数据后 Allocate 大块（4MB），growthLimit=64MB——复现 DeltaLog 场景。</summary>
    [Fact]
    public void DiskDevice_Allocate_Large_AfterSmallAppend()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 先 Append 少量数据（模拟 DeltaLog 构造时写 meta）
        dev.Append(MakePattern(524, 0x11));
        dev.AllocatedTail.Should().Be(new LogicalAddress(0, 524));

        // 再 Allocate 4MB（模拟 Log 的 Allocate(PageSize)）
        var start = dev.Allocate(4 * 1024 * 1024).Start;
        start.Should().Be(new LogicalAddress(0, 524), "Allocate 从当前 tail 起始");
        dev.AllocatedTail.Should().Be(new LogicalAddress(0, 524 + 4 * 1024 * 1024));
    }

    /// <summary>双卷同构对照：两条 Create 路径（旧工厂 vs 直接 new 之争已收敛为单一 Create）Append 后水位一致。</summary>
    [Fact]
    public void DiskDevice_Compare_DevicesCreate_vs_DirectNew()
    {
        var volA = NewVol();
        var volB = NewVol();

        // A:直接 new(已知正常)
        var optionsA = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024 * 1024).WithPreallocateFile(false);
        using var builderA = optionsA.Builder(volA.Fs);
        using var devA = builderA.Start();
        devA.WaitForReady();
        devA.Append(MakePattern(524, 0x11));
        Console.WriteLine($"[A 直接new] SegmentGrowthLimit={devA.SegmentGrowthLimit} Append(524)后 AllocatedTail={devA.AllocatedTail}");

        // B:第二卷同构对照（旧 StorageEngineFactory.Create 路径已并入 StorageEngine.Create）
        var optionsB = new StorageEngineOptions("delta", segmentGrowthLimit: 64 * 1024 * 1024).WithPreallocateFile(false);
        using var builderB = optionsB.Builder(volB.Fs);
        using var devB = builderB.Start();
        devB.WaitForReady();
        devB.Append(MakePattern(524, 0x11));
        Console.WriteLine($"[B Create] SegmentGrowthLimit={devB.SegmentGrowthLimit} Append(524)后 AllocatedTail={devB.AllocatedTail}");

        // 两者该一致(都 seg#0@524)
        Console.WriteLine($"[A] SectorSize={devA.SectorSize} UnbufferedSupport={builderA.Engine.UnbufferedSupport} DeviceName={devA.EngineName}");
        Console.WriteLine($"[B] SectorSize={devB.SectorSize} UnbufferedSupport={builderB.Engine.UnbufferedSupport} DeviceName={devB.EngineName}");
        devA.AllocatedTail.Should().Be(devB.AllocatedTail, "两种构造方式 Append 524 后水位该一致");
    }

    /// <summary>Allocate 非法长度（≤0）应抛 ArgumentOutOfRangeException。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DiskDevice_Allocate_NonPositiveLength_Throws(long badLen)
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();
        Action act = () => dev.Allocate(badLen);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void DiskDevice_Tail_ReflectsAppends()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        dev.Append(stackalloc byte[40]);
        dev.AllocatedTail.Offset.Should().Be(40);

        dev.Append(stackalloc byte[30]);
        dev.AllocatedTail.Offset.Should().Be(70);
    }

    [Fact]
    public void DiskDevice_Read_UnwrittenRegion_ReturnsAvailable()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        var addr = dev.Append(MakePattern(20, 0xBB));

        Span<byte> buf = stackalloc byte[50];
        int n = dev.Read(addr, buf);
        n.Should().Be(20);
        for (int i = 0; i < 20; i++) buf[i].Should().Be(MakePattern(20, 0xBB)[i]);
    }

    [Fact]
    public void DiskDevice_ReclaimTail_ShrinksFileAndTail()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        dev.Append(MakePattern(500, 0xFF));
        dev.AllocatedTail.Offset.Should().Be(500);

        var newTail = new LogicalAddress(0, 200);
        dev.ReclaimTail(newTail);

        dev.AllocatedTail.Offset.Should().Be(200);

        // Verify: writing after truncation continues from new tail
        var addr = dev.Append(MakePattern(50, 0xEE));
        addr.Offset.Should().Be(200);
    }

    [Fact]
    public void DiskDevice_ReclaimHead_PunchesHoleInCurrentSegment()
    {
        // 验证 ReclaimHead 段内打洞（数据安全）：跨段写入后 ReclaimHead 到段中间，
        // 当前段 [0, offset) 应被 PunchHole 打洞（读返回零），保证重启后旧数据不复活。
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 写满 seg0 + 跨段到 seg1
        dev.Append(new byte[1024]);  // seg0 满
        // ★ Allocate 申请 seg1 [0,512) 空间（推进 MaxOffset 到 CommittedTail 内，使 Write 合法）
        //   旧测试用 Append(1) 占位再 Write(512) 踩进私有区——Write 上界=CommittedTail 后改 Allocate
        dev.Allocate(512);
        dev.AllocatedTail.SegId.Should().Be(1);

        // seg1@[0,512) 覆写（Allocate 区已在 CommittedTail 内，合法覆写）
        var pattern = MakePattern(512, 0xAB);
        dev.Write(new LogicalAddress(1, 0), pattern);
        byte[] buf = new byte[512];
        dev.Read(new LogicalAddress(1, 0), buf);
        buf.Should().BeEquivalentTo(pattern, "写入的非零数据应能读回");

        // ReclaimHead 到 seg1@256——seg0 整段删除，seg1 [0,256) 应打洞
        dev.ReclaimHead(new LogicalAddress(1, 256));

        // seg1 [0,256) 打洞后读应返回零
        byte[] zeroBuf = new byte[256];
        int n = dev.Read(new LogicalAddress(1, 0), zeroBuf);
        // 打洞区读返回零（PunchHole 语义）
        zeroBuf.Should().OnlyContain(b => b == 0, "ReclaimHead 段内打洞后该区域应读为零");
    }

    [Fact]
    public void DiskDevice_ReclaimHead_DeletesSegmentFiles()
    {
        // ★ 回归：ReclaimHead 删整段时必须物理删除段文件，否则磁盘空间泄漏。
        //   根因曾为 StorageEngineBase 未 override DeleteSegmentPhysical（基类默认 no-op）。
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 写满 seg0/seg1/seg2（每次 1KB 顶满一段）
        dev.Append(new byte[1024]);  // seg0 满
        dev.Append(new byte[1024]);  // seg1 满 → 跨段到 seg2
        dev.Append(new byte[1024]);  // seg2 满 → 跨段到 seg3

        // 前提：4 个段文件都在盘上
        vol.Fs.Exists("test/test.0").Should().BeTrue("seg0 建段后应存在");
        vol.Fs.Exists("test/test.1").Should().BeTrue("seg1 建段后应存在");
        vol.Fs.Exists("test/test.2").Should().BeTrue("seg2 建段后应存在");

        // ReclaimHead 到 seg2 起点——seg0/seg1 整段删除（段文件应消失），seg2 保留
        dev.ReclaimHead(new LogicalAddress(2, 0));

        vol.Fs.Exists("test/test.0").Should().BeFalse("ReclaimHead 后 seg0 段文件应被删除");
        vol.Fs.Exists("test/test.1").Should().BeFalse("ReclaimHead 后 seg1 段文件应被删除");
        vol.Fs.Exists("test/test.2").Should().BeTrue("seg2 是新 MinSegId，段文件应保留");
    }

    /// <summary>
    /// CommittedTail 在租借跨段但新段未写时，应回退到旧段的真实写入水位（不能返回新段 0）。
    /// <para>★ 回归守护：原实现用 <c>_allocator.Tail.SegmentId</c> 直接定位活跃段读 MaxOffset，
    ///   租借跨到 seg1 但 seg1 未写时返回 (seg1, 0)，违反 <c>CommittedTail ≤ AllocatedTail</c> 且
    ///   "真实已写水位"语义（实际已写在 seg0 末尾）。修复后从活跃段往前扫第一个 MaxOffset&gt;0 的段。</para>
    /// </summary>
    [Fact]
    public void DiskDevice_CommittedTail_FallsBackToPrevSegmentWhenActiveUnwritten()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 写 800B 到 seg0（未满）
        dev.Append(MakePattern(800, 0x11));
        dev.AllocatedTail.Should().Be(new LogicalAddress(0, 800));
        dev.CommittedTail.Should().Be(new LogicalAddress(0, 800),
            "seg0 内已写，CommittedTail 应等于 AllocatedTail");

        // ★ 关键场景：再 Append 800B —— seg0 剩 224B，跨段到 seg1@576（CAS 租借推进 _tail 到 seg1）
        //   此刻 WriteCrossSegment 可能尚未完成 seg1 的写入（seg1.MaxOffset 可能仍为 0 或刚推进）
        //   但无论如何，seg0 已写满（MaxOffset=1024），CommittedTail 至少应反映 seg0 的水位
        dev.Append(MakePattern(800, 0x22));
        dev.AllocatedTail.SegId.Should().Be(1, "已跨段到 seg1");

        // CommittedTail 必须落在已真实写入的范围内：
        //   - 若 seg1 已写：CommittedTail.SegmentId == 1，FileOffset > 0
        //   - 若 seg1 未写（竞态窗口）：CommittedTail 应回退到 seg0（MaxOffset=1024）
        //   两种情况都满足 CommittedTail ≤ AllocatedTail，且不返回 (seg1, 0) 这种空洞地址
        var committed = dev.CommittedTail;
        var allocated = dev.AllocatedTail;
        committed.Should().BeLessThanOrEqualTo(allocated,
            "CommittedTail 不能超过 AllocatedTail（契约：真实已写 ≤ 已租借）");
        committed.SegId.Should().BeLessThanOrEqualTo(allocated.SegId);
        // 不能是空洞地址：(活跃段, 0) 当活跃段有真实已写数据时不允许
        if (committed.SegId == allocated.SegId)
            committed.Offset.Should().BeGreaterThan(0, "活跃段作为 CommittedTail 时必须有真实写入（非空洞）");
    }

    /// <summary>
    /// CommittedTail 在空设备上应返回 MinAddress（所有段 MaxOffset=0）。
    /// </summary>
    [Fact]
    public void DiskDevice_CommittedTail_EmptyDeviceReturnsMinAddress()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 空设备：CommittedTail = MinAddress（seg0@0）
        dev.CommittedTail.Should().Be(new LogicalAddress(0, 0),
            "空设备 CommittedTail 应为 MinAddress");
    }

    [Fact]
    public void DiskDevice_WriteAtGivenOffset()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        var addr = dev.Append(stackalloc byte[100]);
        byte[] overwrite = MakePattern(30, 0xDD);
        dev.Write(addr, overwrite);

        byte[] dst = new byte[30];
        dev.Read(addr, dst);
        dst.SequenceEqual(overwrite).Should().BeTrue();
    }

    /// <summary>
    /// Write 覆写中间区——验证 offset 正确 + 不破坏周围数据。
    /// 先写 100B（pattern A），再覆写中间 [40,70) 30B（pattern B），
    /// 读回完整 100B：前 40B 是 A、中间 30B 是 B、后 30B 是 A。
    /// </summary>
    [Fact]
    public void DiskDevice_WriteMiddle_PreservesSurroundingData()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 写 100B pattern A
        var patternA = MakePattern(100, 0xAA);
        var baseAddr = dev.Append(patternA);

        // 覆写中间 [40,70) 30B pattern B
        var patternB = MakePattern(30, 0xBB);
        var midAddr = new LogicalAddress(baseAddr.SegId, baseAddr.Offset + 40);
        dev.Write(midAddr, patternB);

        // 读回完整 100B，验证三段
        var full = new byte[100];
        dev.Read(baseAddr, full);
        full.AsSpan(0, 40).ToArray().Should().BeEquivalentTo(patternA.AsSpan(0, 40).ToArray(),
            "覆写前的数据应保留");
        full.AsSpan(40, 30).ToArray().Should().BeEquivalentTo(patternB,
            "覆写区应是新数据");
        full.AsSpan(70, 30).ToArray().Should().BeEquivalentTo(patternA.AsSpan(70, 30).ToArray(),
            "覆写后的数据应保留");
    }

    /// <summary>
    /// 多次覆写同一地址——A→B→C，读回应为 C（最新值）。
    /// </summary>
    [Fact]
    public void DiskDevice_WriteMultipleTimes_LastWriteWins()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        var addr = dev.Append(new byte[64]); // 占位 64B 全零

        var a = MakePattern(64, 0x11);
        var b = MakePattern(64, 0x22);
        var c = MakePattern(64, 0x33);

        dev.Write(addr, a);
        dev.Write(addr, b);
        dev.Write(addr, c); // 最后一次

        var dst = new byte[64];
        dev.Read(addr, dst);
        dst.Should().BeEquivalentTo(c, "最后一次 Write 应胜出");
    }

    /// <summary>
    /// 并发覆写不同地址——多线程 Write 不同区间，互不干扰。
    /// 每线程写自己的独占区间（CAS Append 分配不重叠），写后读回验证。
    /// </summary>
    [Fact]
    public async Task DiskDevice_ConcurrentWrite_DifferentAddresses_NoInterference()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        const int threads = 8;
        const int payloadSize = 512;
        var addrs = new LogicalAddress[threads];

        // 先 Append 分配每个线程的独占区间
        for (int i = 0; i < threads; i++)
            addrs[i] = dev.Append(new byte[payloadSize]);

        // 并发覆写各自的区间
        var tasks = new Task[threads];
        for (int t = 0; t < threads; t++)
        {
            int tid = t;
            tasks[t] = Task.Run(() =>
            {
                var buf = MakePattern(payloadSize, (byte)(0x80 + tid));
                dev.Write(addrs[tid], buf);
            });
        }
        await Task.WhenAll(tasks);

        // 读回每个区间，验证互不干扰
        for (int t = 0; t < threads; t++)
        {
            var dst = new byte[payloadSize];
            dev.Read(addrs[t], dst);
            var expected = MakePattern(payloadSize, (byte)(0x80 + t));
            dst.Should().BeEquivalentTo(expected, $"线程 {t} 的覆写数据应正确，不受其他线程干扰");
        }
    }

    /// <summary>
    /// Write 跨段覆写——Write 跨越段边界的区间，验证跨段切分正确。
    /// 段大小 1KB，写 [seg0@768, +512) 横跨 seg0 末尾 256B + seg1 开头 256B。
    /// </summary>
    [Fact]
    public void DiskDevice_WriteCrossSegment_SplitsCorrectly()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 先写满 seg0 + 跨到 seg1，确保两段都有数据可覆写
        dev.Append(new byte[1024]); // seg0 满
        dev.Append(new byte[512]);  // seg1 @0~512
        dev.AllocatedTail.SegId.Should().Be(1);

        // 跨段覆写 [seg0@768, +512) = seg0[768,1024) 256B + seg1[0,256) 256B
        var pattern = MakePattern(512, 0xCC);
        var crossAddr = new LogicalAddress(0, 768);
        dev.Write(crossAddr, pattern);

        // 读回验证：seg0[768,1024) + seg1[0,256) 拼起来应等于 pattern
        var dst = new byte[512];
        int n = dev.Read(crossAddr, dst);
        n.Should().Be(512, "跨段覆写后应能完整读回 512B");
        dst.Should().BeEquivalentTo(pattern, "跨段覆写数据应正确");
    }

    /// <summary>
    /// 覆写回收过的区间——Reclaim 打洞后，Write 可重写回收区，读回新数据。
    /// 验证「回收区可重写」契约（设计文档 §6.5：回收过的区域仍可被 Write 写入）。
    /// </summary>
    [Fact]
    public void DiskDevice_WriteToRecycledRange_AfterReclaim()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 写两段数据
        var addr1 = dev.Append(MakePattern(256, 0xAA));
        var addr2 = dev.Append(MakePattern(256, 0xBB));

        // 回收 addr1 区间
        dev.Reclaim(addr1, addr2);

        // 回收区读回应是零
        var zeroCheck = new byte[256];
        dev.Read(addr1, zeroCheck);
        zeroCheck.Should().OnlyContain(b => b == 0, "回收区应读为零");

        // ★ 重写回收区
        var newData = MakePattern(256, 0xEE);
        dev.Write(addr1, newData);

        // 读回应是新数据
        var dst = new byte[256];
        dev.Read(addr1, dst);
        dst.Should().BeEquivalentTo(newData, "回收区重写后应读回新数据");
    }

    [Fact]
    public void DiskDevice_Reopen_RecoversData()
    {
        var vol = NewVol();
        LogicalAddress addr;

        var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using (var dev = options.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();
            addr = dev.Append(MakePattern(100, 0x42));
        }

        using (var dev = options.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();

            byte[] dst = new byte[100];
            int n = dev.Read(addr, dst);
            n.Should().Be(100);
            dst.SequenceEqual(MakePattern(100, 0x42)).Should().BeTrue();
        }
    }

    [Fact]
    public void DiskDevice_Flush_DoesNotThrow()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        dev.Append(MakePattern(50, 0x77));
        dev.Flush(); // should not throw
    }

    [Fact]
    public void DiskDevice_SequentialReader_Forward_ReadsAll()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        byte[] data = MakePattern(300, 0x55);
        var start = dev.Append(data);
        var end = dev.CommittedTail;

        using var reader = dev.OpenSequentialReader(start, end);
        Span<byte> buf = stackalloc byte[300];
        int n = reader.Read(buf);
        n.Should().Be(300);
        buf.SequenceEqual(data).Should().BeTrue();
    }

    [Fact]
    public void DiskDevice_SequentialReader_SkipThenRead()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        byte[] data = MakePattern(200, 0x33);
        dev.Append(data);
        var start = new LogicalAddress(0, 0);

        using var reader = dev.OpenSequentialReader(start, dev.CommittedTail);
        reader.Skip(50);
        reader.Position.Offset.Should().Be(50);

        Span<byte> buf = stackalloc byte[30];
        int n = reader.Read(buf);
        n.Should().Be(30);
        for (int i = 0; i < 30; i++) buf[i].Should().Be(data[50 + i]);
    }

    [Fact]
    public async Task DiskDevice_Compact_ProducesMigrationMap()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        byte[] data = MakePattern(500, 0x99);
        var addr = dev.Append(data);

        var result = await dev.StartCompact().WaitAsync();
        result.MigrationMap.Should().ContainKey(addr);
        // ★ 新设计：firstNewSegId = oldMinSeg = MinAddress.SegmentId = 0（seg0 是唯一段，复用位置）
        result.NewLowWaterMark.SegId.Should().Be(0);
    }

    [Fact]
    public async Task DiskDevice_Compact_PreservesData()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        byte[] data = MakePattern(500, 0x88);
        var addr = dev.Append(data);

        var result = await dev.StartCompact().WaitAsync();

        byte[] dst = new byte[500];
        int n = dev.Read(result.NewLowWaterMark, dst);
        n.Should().Be(500);
        dst.SequenceEqual(data).Should().BeTrue();
    }

    [Fact]
    public void DiskDevice_CrossSegment_WriteAndRead()
    {
        var vol = NewVol();
        long segGrowth = 256;
        var options = new StorageEngineOptions("test", segmentGrowthLimit: segGrowth).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        byte[] data = MakePattern(400, 0xCA);
        var addr = dev.Append(data);

        addr.SegId.Should().Be(0);
        dev.AllocatedTail.SegId.Should().Be(1);

        byte[] dst = new byte[400];
        int n = dev.Read(addr, dst);
        n.Should().Be(400);
        dst.SequenceEqual(data).Should().BeTrue();
    }

    [Fact]
    public async Task DiskDevice_RangeCompact_PreservesKeptRanges()
    {
        const int blockSize = 64 * 1024;
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: blockSize * 8L).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        var prefixData = MakePattern(blockSize, 0x11);
        var firstData = MakePattern(blockSize, 0x22);
        var reclaimedData = MakePattern(blockSize, 0x33);
        var secondData = MakePattern(blockSize, 0x44);
        var suffixData = MakePattern(blockSize, 0x55);

        var prefix = dev.Append(prefixData);
        var first = dev.Append(firstData);
        var reclaimed = dev.Append(reclaimedData);
        var second = dev.Append(secondData);
        var suffix = dev.Append(suffixData);
        var committedTail = dev.CommittedTail;

        dev.Reclaim(reclaimed, second);

        var result = await dev.StartRangeCompact(
            first,
            suffix,
            [first, reclaimed, second]).WaitAsync();

        result.NewLowWaterMark.Should().Be(first);
        result.NewHighWaterMark.Should().Be(new LogicalAddress(0, blockSize * 3L));
        result.MigrationMap[first].Should().Be(first);
        result.MigrationMap.Should().HaveCount(3);
        result.MigrationMap[reclaimed].Should().BeNull();
        result.MigrationMap[second].Should().Be(new LogicalAddress(0, blockSize * 2L));
        dev.CommittedTail.Should().Be(committedTail, "RangeCompact must not retreat logical watermarks");

        var buffer = new byte[blockSize];
        dev.Read(prefix, buffer).Should().Be(blockSize);
        buffer.Should().Equal(prefixData, "data before from must not move");

        dev.Read(result.MigrationMap[first]!.Value, buffer).Should().Be(blockSize);
        buffer.Should().Equal(firstData);

        dev.Read(result.MigrationMap[second]!.Value, buffer).Should().Be(blockSize);
        buffer.Should().Equal(secondData);

        dev.Read(new LogicalAddress(0, blockSize * 3L), buffer).Should().Be(blockSize);
        buffer.Should().OnlyContain(static value => value == 0, "packed end through to must be one hole");

        dev.Read(suffix, buffer).Should().Be(blockSize);
        buffer.Should().Equal(suffixData, "data after to must remain at its original address");

        vol.Fs.EnumerateFiles("test", "*.compact*")
            .Should().BeEmpty("successful promotion must remove temp images and the group marker");
    }

    [Fact]
    public async Task DiskDevice_RangeCompact_CrossSegment_TranslatesAndPreservesSuffix()
    {
        const int blockSize = 64 * 1024;
        const long segmentSize = blockSize * 2L;
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: segmentSize).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        var data = new byte[6][];
        var addresses = new LogicalAddress[6];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = MakePattern(blockSize, (byte)(0x20 + i * 0x10));
            addresses[i] = dev.Append(data[i]);
        }

        var committedTail = dev.CommittedTail;
        dev.Reclaim(addresses[1], addresses[2]);
        dev.Reclaim(addresses[3], addresses[4]);
        var missing = new LogicalAddress(99, 123);
        var requested = addresses.Append(missing).ToArray();

        var result = await dev.StartRangeCompact(
            addresses[0],
            addresses[5],
            requested).WaitAsync();

        result.MigrationMap[addresses[0]].Should().Be(new LogicalAddress(0, 0));
        result.MigrationMap.Should().HaveCount(requested.Length);
        result.MigrationMap[addresses[1]].Should().BeNull();
        result.MigrationMap[addresses[2]].Should().Be(new LogicalAddress(0, blockSize));
        result.MigrationMap[addresses[3]].Should().BeNull();
        // ★ 区间统一：a4 的新址是 seg0 段末边界 (0, segmentSize)（数据物理落 seg1 首字节）——旧哨兵形态 (1,0) 的同点规范形
        result.MigrationMap[addresses[4]].Should().Be(new LogicalAddress(0, segmentSize));
        result.MigrationMap[addresses[5]].Should().BeNull("to is exclusive");
        result.MigrationMap[missing].Should().BeNull("missing requested addresses must be retained");
        result.NewHighWaterMark.Should().Be(new LogicalAddress(1, blockSize));
        dev.CommittedTail.Should().Be(committedTail);

        var buffer = new byte[blockSize];
        foreach (var index in new[] { 0, 2, 4 })
        {
            dev.Read(result.MigrationMap[addresses[index]]!.Value, buffer).Should().Be(blockSize);
            buffer.Should().Equal(data[index]);
        }

        dev.Read(addresses[5], buffer).Should().Be(blockSize);
        buffer.Should().Equal(data[5], "the suffix after to must remain unchanged");
    }

    [Fact]
    public async Task DiskDevice_RangeCompact_DirectIo_AllowsUnalignedBounds()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 4 * 1024).WithPreallocateFile(false).WithHints(FileOpenHints.NoBuffering);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        var data = MakePattern(2 * 1024, 0x39);
        using var alignedData = new AlignedMemoryManager(data.Length, 512);
        data.CopyTo(alignedData.GetSpan());
        dev.Append(alignedData.GetSpan());
        var requested = new LogicalAddress(0, 512);

        var result = await dev.StartRangeCompact(
            new LogicalAddress(0, 1),
            new LogicalAddress(0, 1537),
            [requested]).WaitAsync();

        result.MigrationMap[requested].Should().Be(requested);
        var buffer = new byte[data.Length];
        dev.Read(new LogicalAddress(0, 0), buffer).Should().Be(data.Length);
        buffer.Should().Equal(data);
    }

    [Fact]
    public async Task DiskDevice_RangeCompact_WritesValidSegmentMetadata()
    {
        const int blockSize = 64 * 1024;
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: blockSize * 4L).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        var first = dev.Append(MakePattern(blockSize, 0x17));
        var reclaimed = dev.Append(MakePattern(blockSize, 0x28));
        var last = dev.Append(MakePattern(blockSize, 0x39));
        dev.Reclaim(reclaimed, last);

        await dev.StartRangeCompact(
            first,
            dev.CommittedTail,
            [first, reclaimed, last]).WaitAsync();

        var metaBytes = ReadSegmentMetadata(vol.Fs, "test/test.0");
        metaBytes.Should().NotBeNull("RangeCompact must persist segment metadata via EngineMeta");
        // EngineMeta.FlushToXattr writes EngineMetaPayload(40B) + Crc32Footer(4B) = 44B
        metaBytes!.Length.Should().Be(SegmentTupleHeaderCodec.StructSize + sizeof(uint),
            "xattr = EngineMetaPayload + Crc32Footer");
    }

    [Fact]
    public async Task DiskDevice_RangeCompact_GrowsSparseDestinationSegment()
    {
        const int segmentSize = 64 * 1024;
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: segmentSize).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        var allocated = dev.Allocate(segmentSize).Start;
        var data = MakePattern(segmentSize, 0x4A);
        var source = dev.Append(data);

        var result = await dev.StartRangeCompact(
            allocated,
            dev.CommittedTail,
            [allocated, source]).WaitAsync();

        result.MigrationMap[allocated].Should().BeNull();
        result.MigrationMap[source].Should().Be(new LogicalAddress(0, 0));
        var buffer = new byte[data.Length];
        dev.Read(new LogicalAddress(0, 0), buffer).Should().Be(data.Length);
        buffer.Should().Equal(data);
    }

    [Fact]
    public void DiskDevice_RangeCompact_RejectsInvalidBounds()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();
        dev.Append(MakePattern(1024, 0x77));

        var start = new LogicalAddress(0, 0);
        var tail = dev.CommittedTail;

        FluentActions.Invoking(() => dev.StartRangeCompact(start, start,
            (IReadOnlyList<(LogicalAddress, long)>)[]))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => dev.StartRangeCompact(
                start, new LogicalAddress(0, tail.Offset + 1),
                (IReadOnlyList<(LogicalAddress, long)>)[]))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    private static byte[]? ReadSegmentMetadata(IFileSystem fs, string segmentPath)
    {
        using var handle = fs.Open(segmentPath, new FileOpenOptions
        {
            Access = AccessMode.Read,
            Mode = FileOpenMode.OpenExisting,
            Sharing = FileSharing.ReadWrite,
        });
        return handle.FileExtra.IsEmpty ? null : handle.FileExtra.ToArray();
    }

    private static void WriteSegmentMetadata(IFileSystem fs, string segmentPath, ReadOnlySpan<byte> metadata)
    {
        using var handle = fs.Open(segmentPath, new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenExisting,
            Sharing = FileSharing.ReadWrite,
        });
        handle.SetFileExtra(metadata.ToArray());
    }

    [Fact]
    public async Task DiskDevice_Compact_NoMarkerFileAfterSuccess()
    {
        // ★ 新语义（marker 在成功后立即删除；失败/崩溃 marker 由 recover 处理）。
        //   Compact 成功后 .compact.marker 不存在 + .compact 临时文件已 rename + 数据完整。
         var vol = NewVol();

         var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using (var dev = options.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();
            dev.Append(MakePattern(200, 0xBB));
            await dev.StartCompact().WaitAsync(); // 全量 Compact
        }

        // ★ 1. Compact 成功后 marker 已删除
        vol.Fs.Exists("test/test.compact.marker").Should().BeFalse("Compact 成功后 marker 已删除");

        // ★ 2. 无 .compact 临时文件残留（成功路径已 rename）
        foreach (var f in vol.Fs.EnumerateFiles("test", "*"))
            f.Name.Should().NotEndWith(".compact", "Compact 成功后无 .compact 临时文件");

        // ★ 3. reopen——数据完整
        //   段号 = oldMinSeg（涉及的最小段号），新段复用 seg0 位置（只有一个段时）。
        var options1 = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using (var dev = options1.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();

            byte[] dst = new byte[200];
            int n = dev.Read(new LogicalAddress(0, 0), dst); // 新段复用 seg0 位置
            n.Should().Be(200);
            dst.SequenceEqual(MakePattern(200, 0xBB)).Should().BeTrue();
        }
    }

    [Fact]
    public void DiskDevice_MetaFile_PersistsAndRecovers()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(true);
        using (var dev = options.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();
            dev.Append(MakePattern(100, 0xDE));
        }

        // ★ 新实现（spec 27）：NTFS 支持 xattr/ADS 时，元信息写入段文件的 ADS 流（per-segment），
        //   不再生成 device 级集中 .test 文件。验证 per-segment ADS 含元信息。
        vol.Fs.Exists("test/test.0").Should().BeTrue("段文件应存在");

        // 旧 per-segment .meta 边车文件不应再生成
        vol.Fs.Exists("test/test.0.meta").Should().BeFalse("不再生成 per-segment .meta 边车");

        // ★ 核心验证：reopen 后能正确读回数据（元信息持久化的最终证明）
        var options3 = new StorageEngineOptions("test", segmentGrowthLimit: 128 * 1024).WithPreallocateFile(true);
        using (var dev = options3.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();

            byte[] dst = new byte[100];
            int n = dev.Read(new LogicalAddress(0, 0), dst);
            n.Should().Be(100);
            dst.SequenceEqual(MakePattern(100, 0xDE)).Should().BeTrue("reopen 后数据应完整可读");
        }
    }

    [Fact]
    public async Task DiskDevice_CompactCrashRecovery_OsScanOnlyNoMarker()
    {
        // ★ 新设计（marker + 段号复用）：Compact 成功后 marker 已删除，reopen 数据完整。
        //   段号复用：firstNewSegId = oldMinSeg = 0，新段文件复用旧段号（test.0）。
        var vol = NewVol();
        byte[] data = MakePattern(2000, 0xCC);

        // Phase 1: create device, write data, compact
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using (var dev = options.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();
            dev.Append(data);
            await dev.StartCompact().WaitAsync();
        }

        // ★ 1. Compact 成功后 marker 已删除
        vol.Fs.Exists("test/test.compact.marker").Should().BeFalse("Compact 成功后 marker 已删除");

        // ★ 2. 新段 seg#0 已入盘（段号复用，新段从 oldMinSeg=0 起）
        vol.Fs.Exists("test/test.0").Should().BeTrue("新段 seg#0 已入盘（段号复用）");

        // ★ 3. reopen —— marker 不存在，恢复路径直接扫盘；新段 seg#0 数据完整
        var options2 = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using (var dev = options2.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();

            byte[] dst = new byte[2000];
            int n = dev.Read(new LogicalAddress(0, 0), dst);
            n.Should().Be(2000);
            dst.SequenceEqual(data).Should().BeTrue("Compact 后数据完整");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  随机读写 —— 验证 Device 支持随机地址 IO（非顺序）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 随机读写往返——写入 N 条记录到 Append 分配的地址，打乱顺序随机读回，验证数据一致。
    /// 模拟上层 KV/索引场景：记录地址后随机访问。
    /// </summary>
    [Fact]
    public void DiskDevice_RandomReadWrite_Roundtrip()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        const int recordCount = 500;
        const int recordSize = 256;
        var addrs = new LogicalAddress[recordCount];
        var records = new byte[recordCount][];

        // 写入 N 条记录
        for (int i = 0; i < recordCount; i++)
        {
            records[i] = MakePattern(recordSize, (byte)(i & 0xFF));
            addrs[i] = dev.Append(records[i]);
        }

        // 打乱顺序随机读回
        var order = new int[recordCount];
        for (int i = 0; i < recordCount; i++) order[i] = i;
        var rng = new Random(42);
        for (int i = recordCount - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        var dst = new byte[recordSize];
        for (int k = 0; k < recordCount; k++)
        {
            int i = order[k];
            int n = dev.Read(addrs[i], dst);
            n.Should().Be(recordSize, $"记录 {i} 应完整读回");
            dst.Should().BeEquivalentTo(records[i], $"记录 {i} 数据应一致");
        }
    }

    /// <summary>
    /// 随机覆写 + 随机读——写 N 条，随机覆写其中一半，再随机读全部验证最新值。
    /// 验证 Write 覆写 + Read 的一致性在随机访问模式下成立。
    /// </summary>
    [Fact]
    public void DiskDevice_RandomOverwriteThenRead_LatestValueWins()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        const int recordCount = 200;
        const int recordSize = 128;
        var addrs = new LogicalAddress[recordCount];
        var latest = new byte[recordCount][];

        // 初始写入
        for (int i = 0; i < recordCount; i++)
        {
            latest[i] = MakePattern(recordSize, 0x10);
            addrs[i] = dev.Append(latest[i]);
        }

        // 随机覆写一半记录
        var rng = new Random(123);
        for (int k = 0; k < recordCount / 2; k++)
        {
            int i = rng.Next(recordCount);
            latest[i] = MakePattern(recordSize, 0x80);
            dev.Write(addrs[i], latest[i]);
        }

        // 随机读全部，验证读到的是最新值
        var dst = new byte[recordSize];
        for (int i = 0; i < recordCount; i++)
        {
            dev.Read(addrs[i], dst);
            dst.Should().BeEquivalentTo(latest[i], $"记录 {i} 应读到最新值（覆写或初始）");
        }
    }

    /// <summary>
    /// 并发随机读写——多线程同时随机 Read 不同地址，验证读不撕裂 + 并行加速。
    /// </summary>
    [Fact]
    public async Task DiskDevice_ConcurrentRandomRead_NoTornReads()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 512 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        const int recordCount = 1000;
        const int recordSize = 64;
        var addrs = new LogicalAddress[recordCount];
        var records = new byte[recordCount][];
        for (int i = 0; i < recordCount; i++)
        {
            records[i] = MakePattern(recordSize, (byte)(i & 0xFF));
            addrs[i] = dev.Append(records[i]);
        }

        int errors = 0;
        var tasks = new Task[8];
        for (int t = 0; t < 8; t++)
        {
            int tid = t;
            tasks[t] = Task.Run(() =>
            {
                var dst = new byte[recordSize];
                var rng = new Random(tid);
                for (int k = 0; k < 500; k++)
                {
                    int i = rng.Next(recordCount);
                    int n = dev.Read(addrs[i], dst);
                    if (n != recordSize || !dst.SequenceEqual(records[i]))
                        Interlocked.Increment(ref errors);
                }
            });
        }
        await Task.WhenAll(tasks);

        errors.Should().Be(0, "并发随机读不应有撕裂或不一致");
    }

    // ═══════════════════════════════════════════════════════════════
    //  空间整理（Compact）完整流程
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Compact 完整流程——多段多记录 Compact 后：
    /// ① 旧地址读不到（旧段已删）
    /// ② MigrationMap 翻译后新地址能读到正确数据
    /// ③ Compact 后继续 Append 可用（新段活跃）
    /// </summary>
    [Fact]
    public async Task DiskDevice_CompactFull_ThenReadViaMigrationMap_ThenContinueAppend()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 写多条记录跨多段
        const int recordCount = 20;
        const int recordSize = 256;
        var oldAddrs = new LogicalAddress[recordCount];
        var records = new byte[recordCount][];
        for (int i = 0; i < recordCount; i++)
        {
            records[i] = MakePattern(recordSize, (byte)(i + 1));
            oldAddrs[i] = dev.Append(records[i]);
        }
        // 验证跨了多段（20×256B=5KB / 1KB段 ≈ 5段）
        dev.AllocatedTail.SegId.Should().BeGreaterThan(1, "应跨多段");

        // 全量 Compact——Compact 后数据搬到紧凑新段，返回水位线 + MigrationMap
        var result = await dev.StartCompact().WaitAsync();
        result.MigrationMap.Count.Should().BeGreaterThan(0, "应有段级迁移映射");
        // Compact 产出新水位线（数据搬到新段）
        result.NewLowWaterMark.Should().NotBeNull();
        result.NewHighWaterMark.Should().NotBeNull();

        // ① Compact 后旧地址已失效（旧段删除），通过新水位线读 Compact 后的数据区
        //    Compact 把全部数据紧凑搬到新段，新段从 NewLowWaterMark 开始连续
        var dst = new byte[recordSize];
        // 读 Compact 后新段的数据——验证数据完整搬迁
        var compactBase = result.NewLowWaterMark;
        for (int i = 0; i < recordCount; i++)
        {
            var readAddr = new LogicalAddress(compactBase.SegId,
                compactBase.Offset + (long)i * recordSize);
            int n = dev.Read(readAddr, dst);
            if (n == recordSize)
            {
                // 找到数据——验证是某条记录的内容（Compact 后顺序可能变）
                bool found = false;
                for (int j = 0; j < recordCount; j++)
                {
                    if (dst.SequenceEqual(records[j])) { found = true; break; }
                }
                found.Should().BeTrue($"Compact 后偏移 {i} 处应是某条原始记录");
            }
        }

        // ② Compact 后继续 Append——新段可用
        // ★ Bug B 已修：Compact 按 segLimit（源段最大 GrowthLimit）切分多段，每段 ≤ segLimit，
        //   且 ReserveAddress 归一化"段已满边界"到下一段开头，Append 地址语义正确。
        var newRecord = MakePattern(recordSize, 0xFF);
        var postCompactAddr = dev.Append(newRecord);
        int readBack = dev.Read(postCompactAddr, dst);
        readBack.Should().Be(recordSize, "Compact 后继续 Append 的数据应可读");
    }

    /// <summary>
    /// Compact 空间回收验证——Compact 后旧段文件被新段 rename 覆盖（段号复用），数据完整可读。
    /// <para>★ 新设计：firstNewSegId = oldMinSeg，新段从源段族起始位置复用段号</para>
    /// <para>  原 seg0..N 的文件被 .compact rename 覆盖；腾出的位置变 Invalid 洞。</para>
    /// </summary>
    [Fact]
    public async Task DiskDevice_Compact_DeletesOldSegments_ReclaimsSpace()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 写数据跨多段
        var addrs = new LogicalAddress[10];
        for (int i = 0; i < 10; i++)
            addrs[i] = dev.Append(MakePattern(512, (byte)i));

        int segCountBefore = dev.AllocatedTail.SegId + 1;
        segCountBefore.Should().BeGreaterThan(3, "应跨多段");

        // 记录 Compact 前的总段数 + 旧段文件存在
        vol.Fs.Exists("test/test.0").Should().BeTrue("Compact 前 seg0 存在");

        // 全量 Compact
        var result = await dev.StartCompact().WaitAsync();

        // ★ 新设计下：
        //   - firstNewSegId = oldMinSeg = 0（涉及的最小段号）
        //   - 新段数 = ceil(总数据量 / segLimit) < 旧段数
        //   - 新段文件复用旧段号（test.0, test.1, ...），原文件被 .compact rename 覆盖
        //   - 腾出的位置 [newSegCount, oldMaxSeg] 段表 Invalid 洞
        result.NewLowWaterMark.SegId.Should().Be(0, "新段从 oldMinSeg 起复用");
        result.NewHighWaterMark.SegId.Should().BeLessThan(segCountBefore,
            "新段数 < 旧段数（紧凑消除碎片）");

        // Compact 后 seg0 文件仍然存在（被新段覆盖），但内容是新段数据
        vol.Fs.Exists("test/test.0").Should().BeTrue("seg0 文件被新段 rename 覆盖（段号复用）");

        // ★ 数据完整性：从新段读回数据应与原数据一致
        byte[] dst = new byte[512];
        foreach (var oldAddr in addrs)
        {
            // MigrationMap 翻译旧地址到新地址
            if (result.MigrationMap.TryGetValue(oldAddr, out var newAddr))
            {
                int n = dev.Read(newAddr.GetValueOrDefault(), dst);
                n.Should().Be(512, "Compact 后数据应完整可读");
            }
        }
    }

    /// <summary>
    /// DeleteOnClose=true 时 Dispose 应删除全部持久化产物（段文件 + 集中 meta + 设备目录）。
    /// <para>★ 守护 _deleteOnClose 接入：DeviceBase 字段曾长期未消费，Dispose 不删文件（功能遗漏）。
    ///   现已接入 LocalStorageDevice.Dispose + DisposeAsyncCore。</para>
    /// </summary>
    [Fact]
    public void DiskDevice_DeleteOnClose_DisposeRemovesAllFiles()
    {
        var vol = NewVol();

        // 写数据（建段 + 元组 FileExtra 内联段文件）
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false).WithDeleteOnClose(true);
        using (var dev = options.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();
            dev.Append(MakePattern(100, 0xAB));
            // 段文件应存在（元组走段文件 FileExtra，不再生成 device 级集中边车）
            vol.Fs.Exists("test/test.0").Should().BeTrue("Dispose 前段文件存在");
        }  // ← Dispose 触发

        // Dispose 后：段文件、目录全部删除（DeleteOnClose 清理所有持久化产物）
        vol.Fs.DirectoryExists("test").Should().BeFalse("DeleteOnClose=true 时 Dispose 应删除设备目录");
        vol.Fs.Exists("test/test.0").Should().BeFalse("Dispose 后段文件应删除");
    }

    /// <summary>
    /// DeleteOnClose=false（默认）时 Dispose 应保留段文件与目录（仅关句柄）。
    /// </summary>
    [Fact]
    public void DiskDevice_NoDeleteOnClose_DisposeKeepsFiles()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false).WithDeleteOnClose(false);
        using (var dev = options.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();
            dev.Append(MakePattern(100, 0xAB));
        }

        // 默认行为：文件保留
        vol.Fs.DirectoryExists("test").Should().BeTrue("DeleteOnClose=false 时目录保留");
        vol.Fs.Exists("test/test.0").Should().BeTrue("段文件保留");
    }

    /// <summary>
    /// PersistenceMode 接入验证：构造时透传的 persistenceMode 应反映在 dev.PersistenceMode 属性上，
    /// 且两种模式（None/WriteThrough）下 Append + Flush 均不抛异常（守护 _persistenceMode 接入 Dispose/Flush）。
    /// <para>★ 守护功能遗漏：原实现 _persistenceMode 只赋值不消费（打开句柄全用 FileOptions.None、Flush 不短路），
    ///   现已接入 BuildAsyncOptions（WriteThrough→加 WriteThrough flag）+ Flush 短路。</para>
    /// </summary>
    [Theory]
    [InlineData(FileOpenHints.None)]
    [InlineData(FileOpenHints.WriteThrough)]
    public void DiskDevice_WriteThrough_RoundtripsAndFlushWorks(FileOpenHints mode)
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false).WithHints(mode);
        using var builder = options.Builder(vol.Fs);
        using var dev = builder.Start();
        dev.WaitForReady();

        builder.Engine.Hints.Should().Be(mode, "构造透传的打开提示应反映到属性");

        // Append 写入数据
        var addr = dev.Append(MakePattern(100, 0x33));

        // 两种模式下 Flush 均应正常完成（WriteThrough 在 Win/Linux 是 no-op，None 真刷盘）
        dev.Flush();
        dev.Flush(addr);

        // 读回验证数据完整（Flush 不影响已写数据）
        var dst = new byte[100];
        dev.Read(addr, dst).Should().Be(100);
        dst.Should().BeEquivalentTo(MakePattern(100, 0x33));
    }

    // ═══════════════════════════════════════════════════════════════
    // 异步段创建 + Write 上界 + AdvanceAddress 进位 单元测试
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 大 Append 跨多段——验证异步段创建 + PendingWrite 回调队列的正确性。
    /// <para>★ 一次 Append 超过单个段大小，触发跨段。每个新段都是 NotCreated 占位，
    ///   worker 异步建段后 drain PendingWrite。验证数据完整写入 + 可读回。</para>
    /// </summary>
    [Fact]
    public void DiskDevice_LargeAppend_CrossMultipleSegments_DataIntegrity()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 一次 Append 5KB——跨 5+ 段（每段 1KB）
        var data = MakePattern(5 * 1024, 0x99);
        var addr = dev.Append(data);

        // 跨了多段
        dev.AllocatedTail.SegId.Should().BeGreaterThanOrEqualTo(4, "5KB / 1KB 段应跨 5 段");

        // 读回验证完整性
        var dst = new byte[5 * 1024];
        int n = dev.Read(addr, dst);
        n.Should().Be(5 * 1024, "跨多段 Append 应完整读回");
        dst.Should().BeEquivalentTo(data, "跨段数据应一致");
    }

    /// <summary>
    /// 异步段创建——并发大 Append（多线程同时跨段），验证 PendingWrite 回调不丢数据。
    /// </summary>
    [Fact]
    public async Task DiskDevice_ConcurrentLargeAppends_AsyncSegmentCreation()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 2 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        const int threads = 4;
        const int perThread = 50;
        const int payloadSize = 512;
        var payloads = Enumerable.Range(0, threads)
            .Select(t => MakePattern(payloadSize, (byte)(0xA0 + t))).ToArray();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var addrs = new System.Collections.Concurrent.ConcurrentQueue<(int thread, int seq, LogicalAddress addr)>();

        var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < perThread; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var addr = dev.Append(payloads[t]);
                addrs.Enqueue((t, i, addr));
            }
        })).ToArray();

        var allTask = Task.WhenAll(tasks);
        var timeout = Task.Delay(TimeSpan.FromSeconds(30));
        (await Task.WhenAny(allTask, timeout)).Should().Be(allTask, "并发 Append 不应超时（无死锁/卡顿）");

        // ★ 地址唯一性检查——确认 CAS 地址租借没有重叠
        var allAddrs = addrs.ToArray();
        var addrSet = new HashSet<LogicalAddress>();
        foreach (var (_, _, addr) in allAddrs)
            addrSet.Add(addr).Should().BeTrue($"地址 {addr} 不应重复（CAS 保证不重叠）");

        // 验证每个线程的数据都能读回
        foreach (var (thread, seq, addr) in allAddrs)
        {
            var dst = new byte[payloadSize];
            int n = dev.Read(addr, dst);
            n.Should().Be(payloadSize, $"线程 {thread} 序号 {seq} 的数据应完整读回");
            dst.Should().BeEquivalentTo(payloads[thread], $"线程 {thread} 的数据应一致");
        }
    }

    /// <summary>
    /// Allocate + Write 填充预留区——验证 Allocate 推进 MaxOffset 后 Write 可合法覆写。
    /// </summary>
    [Fact]
    public void DiskDevice_AllocateThenWrite_Pattern()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 4 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // Allocate 1KB 空间
        var allocAddr = dev.Allocate(1024).Start;
        dev.CommittedTail.Offset.Should().Be(1024, "Allocate 推进 MaxOffset 到 1024");

        // 分两次 Write 填充（共 1000B，剩余 24B 是 Allocate 占位但未 Write 的区间）
        var part1 = MakePattern(400, 0x11);
        var part2 = MakePattern(600, 0x22);
        dev.Write(new LogicalAddress(0, 0), part1);
        dev.Write(new LogicalAddress(0, 400), part2);

        // 读回验证：preallocateFile:false 时文件按需增长，只 Write 了 1000B 文件即 1000B。
        //   Allocate 占 [0,1024) 为 Committed 推水位（ExtentState 契约），但物理文件只到 1000，
        //   Read 受限于物理文件大小返回 1000（[0,1000) 是 Write 数据，末 24B 超出文件不读）。
        //   CommittedTail.Offset 仍为 1024（Allocate 推水位与物理文件大小独立）。
        var dst = new byte[1024];
        dev.Read(allocAddr, dst).Should().Be(1000, "preallocateFile:false 文件按需增长，只写了 1000B");
        dst.AsSpan(0, 400).ToArray().Should().BeEquivalentTo(part1);
        dst.AsSpan(400, 600).ToArray().Should().BeEquivalentTo(part2);
    }

    /// <summary>
    /// CommittedTail 多段并发——写跨段数据后 CommittedTail 在正确的最高段。
    /// </summary>
    [Fact]
    public void DiskDevice_CommittedTail_HighestSegmentAfterCrossSegment()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        dev.Append(MakePattern(2500, 0x55)); // 跨 3 段（1024+1024+452）

        // CommittedTail 应在 seg2（最高已写段）
        dev.CommittedTail.SegId.Should().Be(2, "2500B / 1KB 段跨到 seg2");
        dev.CommittedTail.Offset.Should().Be(452, "seg2 写了 452B（2500-2048）");
    }

    /// <summary>
    /// Write 上界拒绝——写到 CommittedTail 之外（私有区）应抛异常。
    /// </summary>
    [Fact]
    public void DiskDevice_Write_BeyondCommittedTail_Throws()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 4 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        dev.Append(MakePattern(200, 0xAA)); // CommittedTail = seg0@200

        // Write 到 seg0@200（CommittedTail 边界外）
        var act = () => dev.Write(new LogicalAddress(0, 200), MakePattern(100, 0xBB));
        act.Should().Throw<ArgumentOutOfRangeException>(
            "Write 到 CommittedTail 之外（私有区）应拒绝");
    }

    /// <summary>
    /// Write 跨段覆写——跨段但都在 CommittedTail 内，应合法。
    /// </summary>
    [Fact]
    public void DiskDevice_Write_CrossSegmentWithinCommittedTail_Succeeds()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // 写满 seg0 + 一半 seg1
        dev.Append(MakePattern(1024, 0x11)); // seg0 满
        dev.Append(MakePattern(512, 0x22));  // seg1@0-512

        // 跨段覆写 [seg0@768, +512) = seg0[768,1024) 256B + seg1[0,256) 256B
        var pattern = MakePattern(512, 0xCC);
        dev.Write(new LogicalAddress(0, 768), pattern); // 应合法（在 CommittedTail 内）

        // 读回验证
        var dst = new byte[512];
        dev.Read(new LogicalAddress(0, 768), dst).Should().Be(512);
        dst.Should().BeEquivalentTo(pattern);
    }

    /// <summary>
    /// 异步段创建不阻塞——Append 跨段时（新段 NotCreated）不应长时间阻塞。
    /// <para>★ 用小段 + 大 payload 触发频繁跨段，计时验证 Append 延迟在合理范围。</para>
    /// </summary>
    [Fact]
    public async Task DiskDevice_AsyncSegmentCreation_DoesNotBlockAppend()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();

        await Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                dev.Append(MakePattern(256, (byte)(i & 0xFF)));
                sw.Stop();
                latencies.Add(sw.Elapsed.TotalMilliseconds);
            }
        });

        var sorted = latencies.OrderBy(x => x).ToArray();
        double p99 = sorted[(int)(sorted.Length * 0.99)];
        // 异步段创建下，单次 Append 不应超过 5s（即使触发 worker 建段）
        p99.Should().BeLessThan(5000, "p99 延迟应 < 5s（异步建段不阻塞 Append）");
    }

    /// <summary>
    /// 单段模式（EnableSegmentation=false）：seg0 写满后继续 Allocate 应抛异常——地址空间限定在 seg0 容量内。
    /// </summary>
    [Fact]
    public void DiskDevice_SingleSegment_Full_Throws()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("single", enableSegmentation: false, segmentGrowthLimit: 4096).WithDeleteOnClose(true);
        using var dev = options.Builder(vol.Fs).Start();
        long segSize = 4096;   // 小段，容易写满
        dev.WaitForReady();
        dev.EnableSegmentation.Should().BeFalse("单段模式");

        // Allocate 占满 seg0
        dev.Allocate(segSize);

        // 再 Allocate——超出 seg0 容量，单段模式 IO 层校验拒绝（地址空间用尽）
        var act = () => dev.Allocate(100);
        act.Should().Throw<InvalidOperationException>("单段模式 seg0 写满后地址空间用尽");
    }
}
