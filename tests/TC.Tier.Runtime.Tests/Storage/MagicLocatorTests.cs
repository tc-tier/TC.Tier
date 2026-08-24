namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 魔术字方向性定位契约测试（MagicLocator——Locate/LocateAsync：First/Last + [from,to) 范围 +
/// Linear/Monotone 两档）。
/// <para>★ 两步定位协议的引擎侧：返回 <see cref="MagicLocation"/>（Found + 精确 MagicAddress +
///   所在页起点 PageAddress）；上层（Log/Ring/Mirror 恢复）从锚点起结合自身格式精确切界。
///   本测试直接以真引擎 + 构造的 magic 记录验证（纯扩展算法，不依赖 Structures 恢复测试）。</para>
/// <para>★ Monotone 回归钉：二分终局分支曾只查 lo 漏掉 alignedHi 页（尾部页 magic 丢失）；后撤曾传负长给
///   只前进的 CalculationAddress 直接抛——均 0% 覆盖期潜伏。Linear 契约钉：零布局假设
///   （含 magic 页不单调——中段洞形态——恒正确，L30 零富集命题）。</para>
/// </summary>
public sealed class MagicLocatorTests : StorageEngineTestBase, IDisposable
{
    private const int PageSize = 512;      // 2^9
    private const int Align = 4;
    private const uint MagicA = 0xC0FF_EE01;
    private const uint MagicB = 0xFADE_B0BA;

    private readonly List<TestVolume> _vols = new();

    public void Dispose()
    {
        foreach (var vol in _vols) vol.Dispose();
    }

    private IStorageEngine NewEngine()
    {
        var vol = new TestVolume();
        _vols.Add(vol);
        var options = new StorageEngineOptions("magic-locator", segmentGrowthLimit: 4096)
            .WithPreallocateFile(false);
        var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();
        return dev;
    }

    /// <summary>造记录：首 4B magic（LE）+ filler——magic 恒在记录起点（对齐位）。</summary>
    private static byte[] Rec(uint magic, int size = 128)
    {
        var buf = MakePattern(size, 0xAB);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf, magic);
        return buf;
    }

    [Fact]
    public void EmptyDevice_ReturnsNotFound_WithInvalidSentinel()
    {
        using var dev = NewEngine();
        var loc = dev.Locate([MagicA], MagicDirection.Last, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Monotone);
        loc.Found.Should().BeFalse("空设备无 magic 命中");
        loc.MagicAddress.Should().Be(LogicalAddress.Invalid, "未命中 = Invalid（-1）——Empty 是合法 seg0@0 不能当'没有值'");
        loc.MagicAddress.IsValid.Should().BeFalse();
        loc.PageAddress.Should().Be(LogicalAddress.Invalid);
    }

    [Fact]
    public void NoMagicAnywhere_ReturnsNotFound()
    {
        using var dev = NewEngine();
        dev.Append(MakePattern(3000, 0xAB));   // 无 magic 的数据（跨多页）

        var loc = dev.Locate([MagicA], MagicDirection.Last, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Monotone);
        loc.Found.Should().BeFalse();
        loc.MagicAddress.Should().Be(LogicalAddress.Invalid);
    }

    [Fact]
    public void SingleRecord_LessThanOnePage_AddressesDirectlyUsable()
    {
        using var dev = NewEngine();
        var addr = dev.Append(Rec(MagicA, 64));   // 64B << 512B/页——不足一页也要找到（历史回归点）

        var loc = dev.Locate([MagicA], MagicDirection.Last, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Monotone);
        loc.Found.Should().BeTrue();
        loc.MagicAddress.Should().Be(addr, "直接给 LogicalAddress——上层无需再从距离换算");
        loc.PageAddress.Should().Be(dev.MinAddress, "命中在首页 → 页起点 = MinAddress");
        dev.GetDistance(loc.PageAddress, loc.MagicAddress).Should().Be(0);
    }

    [Fact]
    public void MagicAtVeryFirstByte_FoundIsExplicit_NotEmptySentinel()
    {
        using var dev = NewEngine();
        var addr = dev.Append(Rec(MagicA, 32));   // magic 恰在 seg#0@0x0 == LogicalAddress.Empty

        var loc = dev.Locate([MagicA], MagicDirection.Last, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Monotone);
        loc.Found.Should().BeTrue("显式 Found 消除'合法地址 == Empty 哨兵'误判");
        loc.MagicAddress.Should().Be(addr).And.Be(LogicalAddress.Empty);
    }

    [Fact]
    public void MultiPage_ManyRecords_FindsLastMagicInTailPartialPage()
    {
        using var dev = NewEngine();
        var addrs = new List<LogicalAddress>();
        for (int i = 0; i < 40; i++)               // 40 × 128B = 5120B：8 页 + 尾部 1 页
            addrs.Add(dev.Append(Rec(MagicA)));

        var loc = dev.Locate([MagicA], MagicDirection.Last, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Monotone);
        loc.Found.Should().BeTrue();
        loc.MagicAddress.Should().Be(addrs[^1], "最后一条记录在尾部页（5120 % 512 = 0 边界页）——终局分支不可漏查");
        long intra = dev.GetDistance(loc.PageAddress, loc.MagicAddress);
        intra.Should().BeInRange(0, PageSize - 1, "MagicAddress 应落在 PageAddress 页内");
    }

    [Fact]
    public void MultiPage_TailPartialPageWithMagic_Found()
    {
        using var dev = NewEngine();
        // 7 整页（3584B）+ 尾部半页数据（256B）——最后的 magic 只存在于非完整尾页
        for (int i = 0; i < 28; i++)
            dev.Append(Rec(MagicA));
        var last = dev.Append(Rec(MagicA, 256));   // 第 29 条跨入尾部非完整页（3584..3840）

        var loc = dev.Locate([MagicA], MagicDirection.Last, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Monotone);
        loc.Found.Should().BeTrue();
        loc.MagicAddress.Should().Be(last, "尾部非完整页的 magic 不可丢（终局只查 lo 的历史 bug 回归钉）");
    }

    [Fact]
    public void MultiPage_MagicStopsMidStream_BinarySearchGoesLeft_WithoutThrow()
    {
        using var dev = NewEngine();
        var addrs = new List<LogicalAddress>();
        for (int i = 0; i < 20; i++)               // 前 5 页有 magic（20 × 128B = 2560B）
            addrs.Add(dev.Append(Rec(MagicA)));
        dev.Append(MakePattern(5120, 0xCD));       // 后 10 页无 magic——二分须后撤（历史负长抛异常回归钉）

        var loc = dev.Locate([MagicA], MagicDirection.Last, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Monotone);
        loc.Found.Should().BeTrue();
        loc.MagicAddress.Should().Be(addrs[^1], "尾部无 magic 时应缩回最后一个含 magic 记录");
    }

    [Fact]
    public void MultiMagicSet_MatchesAnyKind()
    {
        using var dev = NewEngine();
        dev.Append(Rec(MagicA, 128));
        var last = dev.Append(Rec(MagicB, 128));   // 最后一条是 B

        var loc = dev.Locate([MagicA, MagicB], MagicDirection.Last, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Monotone);
        loc.MagicAddress.Should().Be(last, "magic 集合任一命中皆可，取最后一个");
    }

    [Fact]
    public void Alignment_DecoyAtUnalignedOffset_Ignored()
    {
        using var dev = NewEngine();
        var real = dev.Append(Rec(MagicA, 128));   // magic 在对齐位 0

        // 诱饵：magic 值出现在 payload 内部非对齐偏移（+2）——alignment=4 下不该命中
        var decoy = MakePattern(128, 0x55);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(decoy.AsSpan(2), MagicA);
        dev.Append(decoy);

        var loc = dev.Locate([MagicA], MagicDirection.Last, dev.MinAddress, dev.AllocatedTail, PageSize, 4, MagicLocateStrategy.Monotone);
        loc.MagicAddress.Should().Be(real, "非对齐偏移的 magic 诱饵应被步进跳过");
    }

    [Fact]
    public async Task LocateAsync_LastMonotone_EqualsSyncResult()
    {
        using var dev = NewEngine();
        var addrs = new List<LogicalAddress>();
        for (int i = 0; i < 30; i++)
            addrs.Add(dev.Append(Rec(MagicA)));

        var syncLoc = dev.Locate([MagicA], MagicDirection.Last, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Monotone);
        var asyncLoc = await dev.LocateAsync([MagicA], MagicDirection.Last, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Monotone, CancellationToken.None);

        asyncLoc.Found.Should().BeTrue();
        asyncLoc.MagicAddress.Should().Be(syncLoc.MagicAddress, "异步与同步粗定位一致");
        asyncLoc.MagicAddress.Should().Be(addrs[^1]);
        asyncLoc.PageAddress.Should().Be(syncLoc.PageAddress);
    }

    // ══ First 方向 ══

    [Fact]
    public void First_ManyRecords_FindsOldestMagic()
    {
        using var dev = NewEngine();
        var addrs = new List<LogicalAddress>();
        for (int i = 0; i < 40; i++)
            addrs.Add(dev.Append(Rec(MagicA)));

        var loc = dev.Locate([MagicA], MagicDirection.First, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Linear);
        loc.Found.Should().BeTrue();
        loc.MagicAddress.Should().Be(addrs[0], "First = 地址最小匹配点（最老）");
    }

    [Fact]
    public void First_Monotone_AgreesWithLinear()
    {
        using var dev = NewEngine();
        var addrs = new List<LogicalAddress>();
        for (int i = 0; i < 30; i++)
            addrs.Add(dev.Append(Rec(MagicA)));

        var linear = dev.Locate([MagicA], MagicDirection.First, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Linear);
        var monotone = dev.Locate([MagicA], MagicDirection.First, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Monotone);
        monotone.MagicAddress.Should().Be(linear.MagicAddress, "稠密流（含 magic 页单调）下两档一致");
        monotone.MagicAddress.Should().Be(addrs[0]);
    }

    [Fact]
    public async Task FirstAsync_EqualsSyncResult()
    {
        using var dev = NewEngine();
        dev.Append(Rec(MagicB, 64));
        var first = dev.Append(Rec(MagicA, 64));
        dev.Append(Rec(MagicB, 64));
        var last = dev.Append(Rec(MagicA, 64));

        var syncFirst = dev.Locate([MagicA], MagicDirection.First, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Linear);
        var asyncFirst = await dev.LocateAsync([MagicA], MagicDirection.First, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Linear, CancellationToken.None);
        var asyncLast = await dev.LocateAsync([MagicA], MagicDirection.Last, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Linear, CancellationToken.None);

        asyncFirst.MagicAddress.Should().Be(syncFirst.MagicAddress).And.Be(first, "异步 First 与同步一致");
        asyncLast.MagicAddress.Should().Be(last, "异步 Last 拿最新");
    }

    // ══ 范围参数（[from, to) 半开——使用方格式知识剪域）══

    [Fact]
    public void Range_HalfOpen_ExcludesMagicAtTo()
    {
        using var dev = NewEngine();
        var a = dev.Append(Rec(MagicA));
        var b = dev.Append(Rec(MagicA, 256));

        // [Min, b) —— b 处的 magic 不在范围内（to 排他）
        var loc = dev.Locate([MagicA], MagicDirection.Last, dev.MinAddress, b, PageSize, Align, MagicLocateStrategy.Linear);
        loc.MagicAddress.Should().Be(a, "to 排他——b 的 magic 不算命中");
    }

    [Fact]
    public void Range_NarrowWindow_FindsLastInside()
    {
        using var dev = NewEngine();
        var addrs = new List<LogicalAddress>();
        for (int i = 0; i < 20; i++)
            addrs.Add(dev.Append(Rec(MagicA)));

        // 尾窗剪域（镜像 footer 紧邻流尾的使用方形态）：[addrs[14], tail)
        var from = addrs[14];
        var loc = dev.Locate([MagicA], MagicDirection.Last, from, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Linear);
        loc.MagicAddress.Should().Be(addrs[^1], "窗口内最新命中");
        loc.MagicAddress.CompareTo(from).Should().BeGreaterThanOrEqualTo(0);

        // 窗口内无 magic（错开对齐格）→ NotFound
        var gap = dev.CalculationAddress(addrs[0], 2);   // +2 偏移打破 4B 对齐格
        var none = dev.Locate([MagicA], MagicDirection.First, gap, dev.CalculationAddress(gap, 8), PageSize, Align, MagicLocateStrategy.Linear);
        none.Found.Should().BeFalse("窗口内无对齐命中——范围剪小搜索域");
    }

    [Fact]
    public void Range_EmptyOrClamped_NotFoundNoThrow()
    {
        using var dev = NewEngine();
        dev.Append(Rec(MagicA, 128));

        // 空范围（from ≥ to）
        var empty = dev.Locate([MagicA], MagicDirection.Last, dev.AllocatedTail, dev.MinAddress, PageSize, Align, MagicLocateStrategy.Linear);
        empty.Found.Should().BeFalse();

        // to 超出 AllocatedTail 自动收敛（不抛 GetDistance 越界）
        var beyond = dev.CalculationAddress(dev.AllocatedTail, 1 << 20);
        var act = () => dev.Locate([MagicA], MagicDirection.Last, dev.MinAddress, beyond, PageSize, Align, MagicLocateStrategy.Linear);
        act.Should().NotThrow("范围收敛到已分配域");
        act.Invoke().Found.Should().BeTrue();
    }

    // ══ Linear 档：零布局假设（含 magic 页不单调——L30 零富集命题）══

    [Fact]
    public void Linear_NonMonotoneMagicPages_LastStillCorrect()
    {
        using var dev = NewEngine();
        // 中段洞形态：前段有 magic（8 条），中段无（12 页 0xCD），尾段又有（4 条）——
        // 含 magic 页集合既非前缀也非后缀（Monotone 前置条件破坏——Linear 恒正确）
        var early = new List<LogicalAddress>();
        for (int i = 0; i < 8; i++)
            early.Add(dev.Append(Rec(MagicA)));
        dev.Append(MakePattern(512 * 12, 0xCD));
        var late = new List<LogicalAddress>();
        for (int i = 0; i < 4; i++)
            late.Add(dev.Append(Rec(MagicA)));

        var last = dev.Locate([MagicA], MagicDirection.Last, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Linear);
        last.MagicAddress.Should().Be(late[^1], "Linear 零布局假设——中段无 magic 区不骗方向");

        var first = dev.Locate([MagicA], MagicDirection.First, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Linear);
        first.MagicAddress.Should().Be(early[0], "First 同样免疫");
    }

    [Fact]
    public void Linear_TailPartialPage_FindsLastMagic()
    {
        using var dev = NewEngine();
        for (int i = 0; i < 28; i++)
            dev.Append(Rec(MagicA));
        var last = dev.Append(Rec(MagicA, 256));   // 尾部非完整页

        var loc = dev.Locate([MagicA], MagicDirection.Last, dev.MinAddress, dev.AllocatedTail, PageSize, Align, MagicLocateStrategy.Linear);
        loc.MagicAddress.Should().Be(last, "Linear 从最后一页往回——尾部非完整页不丢");
    }
}
