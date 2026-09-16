namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// ReclaimTail 物理打洞路径回归（真磁盘 3 节点 raft 卡死定针）。
/// <para>★ 现场：follower 日志冲突回退 → WAL TruncateSuffix(0) → LogBase.TruncateSuffix →
///   engine.ReclaimTail(段首 8B)。尾区间仅几十字节：DiskFileHandle.PunchHole 走 WriteZeroes
///   零写兜底；旧实现零写借用 DIO（FILE_FLAG_NO_BUFFERING）写句柄，ArrayPool 缓冲/偏移/长度
///   非 4096 对齐 → WriteFile 返回 error 87（.NET 误报 "file will become too long"）。</para>
/// <para>★ 修复：打洞/零写一律走缓冲句柄（StorageEngine.GetPunchHandle——DioActive 时
///   Acquire 无 NoBuffering 的写选项）。本组以 FileOpenHints.NoBuffering 开 DIO 复现，
///   mem/真磁盘随 TC_TEST_FS_SPEC 平权（TestVolume）。</para>
/// <para>★ 数据写缓冲纪律：DIO 对齐 chunk 的数据写须 4096 对齐缓冲（生产侧 LogBase 池化
///   AlignedMemoryManager 保证，见 LogBase.Write.cs 头注释）——本组 AppendAligned 同款，
///   非对齐 chunk 引擎自动路由缓冲句柄（GetWriteHandleForChunk）。</para>
/// </summary>
public sealed class StorageEngineReclaimTailTests : StorageEngineTestBase, IDisposable
{
    private readonly List<TestVolume> _vols = new();

    public void Dispose()
    {
        foreach (var vol in _vols) vol.Dispose();
    }

    private IStorageEngine NewDioEngine()
    {
        var vol = new TestVolume();
        _vols.Add(vol);
        var options = new StorageEngineOptions("reclaim-tail-dio", segmentGrowthLimit: 4096,
                enableSegmentation: true, preallocateFile: false, deleteOnClose: true)
            .WithHints(FileOpenHints.NoBuffering);   // ★ 复现真磁盘 DIO（TierWal 默认 hints）
        var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();
        return dev;
    }

    /// <summary>DIO 纪律写：4096 对齐原生缓冲承载 payload（对齐 chunk 走 DIO 句柄三重对齐之一）。</summary>
    private static LogicalAddress AppendAligned(IStorageEngine dev, int length, byte seed)
    {
        using var amm = new AlignedMemoryManager(length, 4096);
        var span = amm.GetSpan();
        for (var i = 0; i < length; i++) span[i] = (byte)((seed + i) & 0xFF);
        return dev.Append(span.Slice(0, length));
    }

    [Fact]
    public void ReclaimTail_PartiallyWrittenAllocateWindow_ClampsPunchToPhysicalEof()
    {
        // 回归（尾截断第 4 层定针）：LogBase Allocate 窗口（PageSize×16=64KB）零 IO 预留地址空间，
        // 尾窗未写满即 TruncateSuffix → ReclaimTail 时，extent 终点（AllocatedTail）合法超过物理 EOF
        // （preallocate:false 下 sparse 文件长度 = 最高写入偏移）。旧句柄守卫对越 EOF 区间直接抛
        // FileIOException（"PunchHole 区间超出文件长度"）——真磁盘 follower 清尾必败。契约修正：
        // EOF 之外本就读零、无物可打，打洞钳到 EOF 即 no-op，且绝不零写扩文件（IO-17 原契约不破）。
        var vol = new TestVolume();
        _vols.Add(vol);
        var options = new StorageEngineOptions("rt-clamp", segmentGrowthLimit: 256 * 1024,
            enableSegmentation: true, preallocateFile: false, deleteOnClose: true);
        var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        var (wStart, _) = dev.Allocate(16L * 4096);        // 预留 64KB 窗口
        for (var i = 0; i < 7; i++)                        // 只写 7 帧 = 28672B（现场实测长度）
            dev.Write(dev.CalculationAddress(wStart, i * 4096L), new byte[4096]);

        dev.ReclaimTail(new LogicalAddress(0, 0));         // 旧实现：[0,65536) vs 文件 28672 抛
        Assert.Equal(new LogicalAddress(0, 0), dev.AllocatedTail);
    }

    [Fact]
    public void ReclaimTail_DioHints_TruncateToSegmentStart_SucceedsAndTailReusable()
    {
        using var dev = NewDioEngine();
        AppendAligned(dev, 3000, 0xDD);                    // 非对齐尾（3000 < 4096 段块）
        var newTail = new LogicalAddress(0, 8);              // WAL 清尾图景：段首 8B 哨兵后全截

        dev.ReclaimTail(newTail);                            // ★ 旧实现：DIO 句柄非对齐零写 → IOException 87

        dev.AllocatedTail.Should().Be(newTail, "截断后分配尾退到新尾");
        dev.CommittedTail.Should().Be(newTail, "截断后提交尾退到新尾");

        // 空间复用（follower 重收条目图景）：追加落点 = 截断边界，读回一致
        var reAddr = AppendAligned(dev, 1000, 0xEE);
        reAddr.Should().Be(newTail, "复用截断边界继续追加");
        var buf = new byte[1000];
        dev.Read(reAddr, buf).Should().Be(1000);
        buf.Should().Equal(MakePattern(1000, 0xEE), "重灌数据可读回");
    }

    [Fact]
    public void ReclaimTail_DioHints_CrossSegmentThenReAppend_RepeatedTruncationsIdempotent()
    {
        using var dev = NewDioEngine();
        AppendAligned(dev, 5000, 0xDD);                    // 跨段：seg0 满 4096 + seg1 [0,904)
        var firstTail = new LogicalAddress(0, 100);

        dev.ReclaimTail(firstTail);                         // 跨段尾截断（seg0 尾打洞 + seg1 整段清零）
        dev.AllocatedTail.Should().Be(firstTail);
        dev.CommittedTail.Should().Be(firstTail);

        // AE 多轮冲突回退图景：再写 → 再截（幂等，打洞零写/FSCTL_SET_ZERO_DATA 均可重入）
        AppendAligned(dev, 2000, 0xEE);
        var secondTail = new LogicalAddress(0, 8);
        dev.ReclaimTail(secondTail);
        dev.AllocatedTail.Should().Be(secondTail);
        dev.CommittedTail.Should().Be(secondTail);

        var reAddr = AppendAligned(dev, 777, 0x5A);
        reAddr.Should().Be(secondTail);
        var buf = new byte[777];
        dev.Read(reAddr, buf).Should().Be(777);
        buf.Should().Equal(MakePattern(777, 0x5A), "二次截断后重灌数据仍可读回");
    }
}
