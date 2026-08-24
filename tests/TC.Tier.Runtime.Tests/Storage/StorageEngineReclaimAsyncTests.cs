namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 后台区间回收契约测试（StartReclaim + IAsyncOperation——句柄/进度/事件协议）。
/// <para>★ 契约：物理 PunchHole 后台执行，调用方立即拿到句柄（0 等待）；逐段打洞触发
///   <see cref="IAsyncOperation.Progress"/>（完成比例），全部完成终态（<c>await op.WaitAsync()</c> 返回）；
///   空区间（null / from ≥ to）立即完成；打洞区间读回归零，区间外数据保留。</para>
/// </summary>
public sealed class StorageEngineReclaimAsyncTests : StorageEngineTestBase, IDisposable
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

    private IStorageEngine NewEngine(long segmentGrowthLimit)
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("reclaim-async", segmentGrowthLimit: segmentGrowthLimit)
            .WithPreallocateFile(false);
        var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();
        return dev;
    }

    [Fact]
    public async Task StartReclaim_SingleSegment_PunchesZeroes_CompletesWithProgress()
    {
        using var dev = NewEngine(4096);
        var first = dev.Append(MakeSequential(1000));    // (0, 0)
        var second = dev.Append(MakeSequential(1000));   // (0, 1000)

        var op = dev.StartReclaim(new LogicalAddress(0, 200), new LogicalAddress(0, 500),
            CancellationToken.None);
        var progresses = new List<double>();
        op.Progress += (_, ratio) => progresses.Add(ratio);
        // ★ 订阅竞态防护（契约模式）：先订阅、后查 IsCompleted——true = 完成早于订阅（事件已错过，
        //   不可断言；快路径 mem 打洞可能即时完成）；false = 完成晚于查询，订阅必收到事件
        bool missedByRace = op.IsCompleted;

        await op.WaitAsync(CancellationToken.None);

        if (!missedByRace)
        {
            progresses.Should().NotBeEmpty("单段区间至少一次 Progress");
            progresses.Last().Should().BeApproximately(1.0, 1e-9, "末次进度应为 100%");
        }

        var punched = new byte[300];
        dev.Read(new LogicalAddress(0, 200), punched).Should().Be(300);
        punched.Should().OnlyContain(b => b == 0, "打洞区间应归零");

        var before = new byte[200];
        dev.Read(first, before).Should().Be(200);
        before.Should().Equal(MakeSequential(200).Take(200), "区间前数据保留");
        var after = new byte[1000];
        dev.Read(second, after).Should().Be(1000);
        after.Should().Equal(MakeSequential(1000), "区间后数据保留");
    }

    [Fact]
    public async Task StartReclaim_CrossSegment_MultiChunk_ProgressAccumulates()
    {
        using var dev = NewEngine(4096);
        dev.Append(MakeSequential(4096));                // 垫满 seg0 → 尾停驻 (0,4096)
        var a1 = dev.Append(MakeSequential(1000));       // 起步地址 = 段末边界 (0,4096)——数据落 seg1
        var a2 = dev.Append(MakePattern(1000, 0x5C));    // (1, 1000)
        a1.Should().Be(new LogicalAddress(0, 4096));
        a2.SegId.Should().Be(1);

        // 打洞 [(0,1000) .. (1,500))：横跨 seg0 尾部 + seg1 头部 → 两 chunk、两次 Progress
        var op = dev.StartReclaim(new LogicalAddress(0, 1000), new LogicalAddress(1, 500),
            CancellationToken.None);
        var progresses = new List<double>();
        op.Progress += (_, ratio) => progresses.Add(ratio);
        bool missedByRace = op.IsCompleted;   // 订阅后查态（竞态防护，见单段测试注释）

        await op.WaitAsync(CancellationToken.None);

        if (!missedByRace)
        {
            progresses.Count.Should().BeGreaterThanOrEqualTo(2, "跨段打洞逐段触发 Progress");
            progresses.Last().Should().BeApproximately(1.0, 1e-9);
        }

        var tail0 = new byte[500];
        dev.Read(new LogicalAddress(0, 1000), tail0).Should().Be(500);
        tail0.Should().OnlyContain(b => b == 0, "seg0 打洞区归零");
        var head1 = new byte[500];
        dev.Read(a1, head1).Should().Be(500);
        head1.Should().OnlyContain(b => b == 0, "seg1 打洞区归零");
        var keep = new byte[1000];
        dev.Read(a2, keep).Should().Be(1000);
        keep.Should().Equal(MakePattern(1000, 0x5C), "区间外数据保留");
    }

    [Fact]
    public async Task StartReclaim_NullRange_CompletesImmediately_WithoutPunch()
    {
        using var dev = NewEngine(4096);
        var addr = dev.Append(MakeSequential(512));

        var op = dev.StartReclaim(null, null, CancellationToken.None);
        var progresses = new List<double>();
        op.Progress += (_, ratio) => progresses.Add(ratio);

        await op.WaitAsync(CancellationToken.None);

        progresses.Should().BeEmpty("空区间不打洞不报进度");
        var buf = new byte[512];
        dev.Read(addr, buf).Should().Be(512);
        buf.Should().Equal(MakeSequential(512), "数据原样保留");
    }

    [Fact]
    public async Task StartReclaim_FromGreaterEqualTo_CompletesEmpty()
    {
        using var dev = NewEngine(4096);
        var addr = dev.Append(MakeSequential(512));

        var op = dev.StartReclaim(new LogicalAddress(0, 256), new LogicalAddress(0, 256),
            CancellationToken.None);
        await op.WaitAsync(CancellationToken.None);

        var buf = new byte[512];
        dev.Read(addr, buf).Should().Be(512);
        buf.Should().Equal(MakeSequential(512), "from ≥ to 视为空区间，数据原样保留");
    }
}
