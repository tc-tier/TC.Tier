namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// A7 裁定回归（引擎级）——建段失败 → Broken 终态 → 分配烧洞跳过 + IO 层失败清理 + 重开语义。
/// <para>★ 失败注入：seg1 的文件路径预放一个<b>目录</b>——文件创建必失败（无法子类化引擎注入，
///   StorageEngineBase internal sealed / 基类构造 private protected）。目录即物理占位，
///   注入完成后由测试侧移除（重开前），重开按无 seg1 干净重建。</para>
/// <para>★ 覆盖：① 正式建段异步失败（worker callback false → Broken）——注册该段的在途 lease
///   物理门快失败（≤1 次，有界不研磨）；② 后续分配烧洞跳过（地址永不落 seg1）；③ 满段/数据
///   正常（成功 append 全部可回读）；④ 重开恢复后追加照常。表级确定性语义见
///   AddressSpace/SegmentTableBrokenSkipTests。</para>
/// </summary>
[Collection("LargeScaleIO")]
public sealed class SegmentBrokenSkipTests : IDisposable
{
    private readonly List<TestVolume> _vols = new();
    private const string DeviceName = "test";
    private const long Growth = 4 * 1024;

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

    [Fact]
    public void BuildFailure_BrokenSkip_AppendsSurvive_DataIntact_ReopenWorks()
    {
        var vol = NewVol();
        var seg1RelPath = $"{DeviceName}/{DeviceName}.1";   // ★ 注入用相对路径（引擎段文件={root}/{engine}/{engine}.{segId}）
        vol.Fs.CreateDirectory(seg1RelPath);   // ★ 失败注入：seg1 文件路径被目录占位

        var payload = new byte[512];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);
        var written = new List<(LogicalAddress Addr, byte[] Payload)>();
        var failures = 0;

        var options = new StorageEngineOptions(DeviceName, segmentGrowthLimit: Growth).WithPreallocateFile(false);
        using var builder = options.Builder(vol.Fs, logger: TestConsoleLogger.Instance);
        builder.Engine.SuppressSegmentPoolForLifecycle();   // ★ 构建后、启动前配置经 Builder 引擎（白盒）——池关：失败注入只走正式建段路径（建段日志恰好一次）
        using (var dev = builder.Start())
        {

            // 40 × 512B = 20KB ≈ 5 个 4KB 段——必然跨过 seg1（注入失败段）
            for (var i = 0; i < 40; i++)
            {
                try
                {
                    var addr = dev.Append(payload);
                    written.Add((addr, payload));
                }
                catch (SegmentCreationException)
                {
                    failures++;   // 注册 seg1 的在途 lease 吃物理门快失败——有界（≤1）
                }
            }

            failures.Should().BeLessThanOrEqualTo(1, "注册失败段的在途 lease 吃恰好一次快失败，此后烧洞跳过——绝不允许研磨");
            written.Count.Should().BeGreaterThanOrEqualTo(39, "除首次跨洞失败外全部成功");

            // ★ 烧洞核心断言：成功地址永不落在 Broken 段
            written.Where(w => w.Addr.SegId == 1).Should().BeEmpty("Broken 段地址永不可交付（A7 烧洞）");

            // ★ single-flight：失败段恰好一次物理构建尝试（池关——只有正式路径）
            builder.Engine.PhysicalBuildLog.Count(x => x == 1).Should().Be(1, "失败段恰好一次构建尝试（Broken 终态不重试）");

            // ★ 数据回读：全部成功 append 的数据完整可读
            foreach (var (addr, data) in written)
            {
                var buf = new byte[data.Length];
                var n = dev.Read(addr, buf);
                n.Should().Be(data.Length, $"addr={addr} 完整可读（部分读=数据丢失）");
                buf.Should().Equal(data, $"addr={addr} 数据逐字节一致");
            }

            var tail = dev.AllocatedTail.SegId;
            tail.Should().BeGreaterThanOrEqualTo(4, "20KB/4KB 段应推进到 seg4+（seg1 被烧洞跳过）");
        }

        // ★ 重开语义：移除注入目录（其物理占位使命完成），恢复后追加照常、旧数据可读
        vol.Fs.DeleteDirectory(seg1RelPath);
        var options1 = new StorageEngineOptions(DeviceName, segmentGrowthLimit: Growth).WithPreallocateFile(false);
        using (var reopened = options1.Builder(vol.Fs).Start())
        {
            reopened.WaitForReady();

            var addr = reopened.Append(payload);
            addr.SegId.Should().BeGreaterThanOrEqualTo(4, "重开恢复尾段后追加照常");
            var buf = new byte[payload.Length];
            var spot = written[^1];
            var n = reopened.Read(spot.Addr, buf);
            n.Should().Be(payload.Length, "重开后旧数据可读");
            buf.Should().Equal(spot.Payload);
        }
    }
}
