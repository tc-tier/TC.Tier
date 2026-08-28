using System.IO.Hashing;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.TierVolume;

namespace TC.Tier.Core.Tests.IO.TierVolume;

/// <summary>
/// 自动扩容契约测试（medium-protocol-and-parity-design §5.3——quota=-1 文件载体按需增长）：
/// 初始小界 / 空间耗尽触发 / 碎片化触发 / 崩溃一致性（翻转两侧）/ Open 配额收紧 /
/// 多载体退出自动扩容 / TierFs spec 形态（New 无 quota 直通）。
/// </summary>
public sealed class TierVolumeAutoExpandTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-tv-autoexpand");
    private readonly List<TierVolumeFs> _openFs = [];

    private string NewVolumePath() => Path.Combine(_dir, $"vol-{Guid.NewGuid():N}.tier");

    private TierVolumeFs NewAutoVolume()
    {
        var fs = TierVolumeFs.New(TierVolumeCarrier.File(NewVolumePath()), new TierVolumeFormatOptions());   // QuotaBytes 缺省 -1
        _openFs.Add(fs);
        return fs;
    }

    private TierVolumeFs Reopen(string path, TierVolumeOpenOptions? options = null)
    {
        var fs = TierVolumeFs.Open(TierVolumeCarrier.File(path), options);
        _openFs.Add(fs);
        return fs;
    }

    public void Dispose()
    {
        foreach (var fs in _openFs) fs.Dispose();
        TestTempDir.TryCleanup(_dir);
    }

    private static FileOpenOptions RWOpts() => new()
    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite };

    private static FileOpenOptions ROpts() => new()
    { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite };

    // ═══════════════ 初始界与读面 ═══════════════

    [Fact]
    public void New_WithoutQuota_InitialBoundAndVolumeInfo()
    {
        using var fs = NewAutoVolume();
        fs.Volume.TotalSpace.Should().Be(64L << 20, "初始小界 = 64 MiB（§5.3 初始小界）");
        fs.Volume.QuotaBytes.Should().Be(-1, "自动扩容卷无上限——与 spec quota= 同名往返（§5.4）");
        fs.Volume.UsedBytes.Should().BeGreaterThan(0, "日志/头部/位图保留计入用量");
    }

    [Fact]
    public void New_WithoutQuota_SurvivesCleanCloseAndReopen()
    {
        var path = NewVolumePath();
        using (var fs = TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions { Label = "auto" }))
        {
            fs.CreateFile("seed", preallocateSize: 4096);
        }
        using var reopened = Reopen(path, new TierVolumeOpenOptions { Label = "auto" });
        reopened.Volume.TotalSpace.Should().Be(64L << 20, "旗标与容量持久（clean 关闭）");
        reopened.Volume.QuotaBytes.Should().Be(-1, "Open 继承自动扩容卷属性");
    }

    // ═══════════════ 空间耗尽触发 + 数据保真 ═══════════════

    [Fact]
    public void SpaceExhaustion_GrowsAndDataSurvivesReopen()
    {
        var path = NewVolumePath();
        var payload = new byte[80L << 20];   // 超初始界 64 MiB
        Random.Shared.NextBytes(payload);
        var crc = Crc32.HashToUInt32(payload);

        using (var fs = TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions()))
        {
            fs.CreateFile("big");
            using var h = fs.Open("big", RWOpts());
            WriteChunked(h, 0, payload);   // 触发扩容（64 → 128 MiB）
            fs.Volume.TotalSpace.Should().Be(128L << 20, "几何倍增：64 → 128 MiB");
            fs.Volume.QuotaBytes.Should().Be(-1);
        }

        using var reopened = Reopen(path);
        reopened.Volume.TotalSpace.Should().Be(128L << 20, "扩容结果持久（superblock）");
        using var rh = reopened.Open("big", ROpts());
        var read = ReadChunked(rh, 0, payload.Length);
        Crc32.HashToUInt32(read).Should().Be(crc, "扩容后数据保真（reopen 读回）");
    }

    [Fact]
    public void CrashAfterGrowth_JournalRecoversDataAndCapacity()
    {
        var path = NewVolumePath();
        var payload = new byte[70L << 20];
        Random.Shared.NextBytes(payload);
        var crc = Crc32.HashToUInt32(payload);

        var fs = TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions());
        fs.CreateFile("crash-big");
        using (var h = fs.Open("crash-big", RWOpts()))
        {
            WriteChunked(h, 0, payload);   // 扩容后写入跨越新旧界的区块
        }
        fs.FlushRoot();   // 日志提交 + 检查点（崩溃窗口收口——pending 记录落区后才可确定性恢复）
        fs.CrashSimulate();   // 跳过 clean 关闭——dirty 残留（翻转后崩溃形态）

        using var reopened = Reopen(path);   // 恢复路径（日志重放 + 对账）
        reopened.Volume.TotalSpace.Should().Be(128L << 20, "扩容容量经恢复路径保真");
        using var rh = reopened.Open("crash-big", ROpts());
        var read = ReadChunked(rh, 0, payload.Length);
        Crc32.HashToUInt32(read).Should().Be(crc, "崩溃恢复后跨界数据完整");
    }

    // ═══════════════ 碎片化触发 ═══════════════

    [Fact]
    public void Fragmentation_TriggersGrowthWithContiguousNewRegion()
    {
        var path = NewVolumePath();
        var names = new List<string>();
        // 填满阶段钉住配额（= 初始界）——禁止填充期扩容
        using (var fill = ReopenAfterNew(path, quotaBytes: 64L << 20))
        {
            for (var i = 0; ; i++)
            {
                var name = $"f{i}";
                try
                {
                    fill.CreateFile(name, preallocateSize: 1L << 20);
                    names.Add(name);
                }
                catch (FileIOException)
                {
                    break;   // 初始界填满（配额执法——DiskFull）
                }
            }
            names.Count.Should().BeGreaterThan(10, "初始界已被交替文件填满");
            for (var i = 0; i < names.Count; i += 2)
                fill.Delete(names[i]);   // 界内只剩 1 MiB 孔洞
        }
        // 重开（无配额）——2 MiB 连续请求界内无 run：碎片化路径触发扩容
        using var fs = Reopen(path);
        fs.CreateFile("wide", preallocateSize: 2L << 20);
        fs.Volume.TotalSpace.Should().BeGreaterThan(64L << 20, "碎片化触发扩容——新界整段连续");
    }

    private TierVolumeFs ReopenAfterNew(string path, long quotaBytes)
    {
        using (TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions())) { }
        return Reopen(path, new TierVolumeOpenOptions { QuotaBytes = quotaBytes });
    }

    private static void WriteChunked(IFileHandle h, long offset, byte[] payload)
    {
        const int chunk = 4 << 20;
        for (var off = 0L; off < payload.LongLength; off += chunk)
            h.Write(offset + off, payload.AsSpan((int)off, (int)Math.Min(chunk, payload.LongLength - off)));
    }

    private static byte[] ReadChunked(IFileHandle h, long offset, long length)
    {
        const int chunk = 4 << 20;
        var result = new byte[length];
        for (var off = 0L; off < length; off += chunk)
        {
            var take = (int)Math.Min(chunk, length - off);
            var got = h.Read(offset + off, result.AsSpan((int)off, take));
            if (got != take) throw new IOException($"短读 @{off}: {got}/{take}");
        }
        return result;
    }

    // ═══════════════ Open 配额收紧 ═══════════════

    [Fact]
    public void OpenQuota_CapsGrowthAtQuota()
    {
        var path = NewVolumePath();
        using (TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions())) { }

        // quota=72M：供给动态——扩容目标对齐到配额界（min 规则随增长自然成立）
        using var fs = Reopen(path, new TierVolumeOpenOptions { QuotaBytes = 72L << 20 });
        var files = 0;
        while (true)
        {
            try
            {
                fs.CreateFile($"f{files}", preallocateSize: 1L << 20);   // 顺序小文件——填满供给
                files++;
            }
            catch (FileIOException ex) when (ex.Error == IOError.DiskFull)
            {
                break;
            }
        }
        files.Should().BeGreaterThan(40, "供给填满（1 MiB 文件）");
        fs.Volume.TotalSpace.Should().Be(72L << 20, "扩容目标 = min(几何倍增, 配额界)——72 MiB");
        fs.Volume.QuotaBytes.Should().Be(72L << 20, "收紧配额 = 界本身（读面）");
    }

    // ═══════════════ 多载体退出自动扩容 ═══════════════

    [Fact]
    public void AddCarrier_DisablesAutoExpand()
    {
        using var fs = NewAutoVolume();
        fs.AddCarrier(TierVolumeCarrier.File(Path.Combine(_dir, $"m1-{Guid.NewGuid():N}.tier")), capacityBytes: 16L << 20);
        fs.Volume.TotalSpace.Should().Be((64L + 16L) << 20, "多载体容量拼接");
        // 界内装不下的请求不再触发扩容（成员 0 容量变更会使成员基块漂移——扩容机制退出）
        var ex = Assert.Throws<FileIOException>(() => fs.CreateFile("huge", preallocateSize: 100L << 20));
        ex.Error.Should().Be(IOError.DiskFull, "多载体卷扩容走 AddCarrier 显式路径");
    }

    // ═══════════════ TierFs spec 形态 ═══════════════

    [Fact]
    public void TierFs_VirtualNewWithoutQuota_SucceedsAndGrows()
    {
        var vol = Path.Combine(_dir, "spec-grow.tier");
        var spec = vol.Replace('\\', '/');
        using (var fs = (TierVolumeFs)TierFs.New($"virtual:///{spec}?label=grow"))
        {
            fs.Volume.TotalSpace.Should().Be(64L << 20);
            fs.CreateFile("big", preallocateSize: 70L << 20);   // spec 无 quota = 自动扩容
            // 70 MiB 单 extent 需 ≥70M 连续 run：128M 卷新界 run 仅 ~63.9M（扣除日志/位图）——二次倍增至 256M
            fs.Volume.TotalSpace.Should().Be(256L << 20, "按需倍增（连续分配边界下自动二次扩容）");
        }
        using (var reopened = (TierVolumeFs)TierFs.Open($"virtual:///{spec}"))
        {
            reopened.Exists("big").Should().BeTrue();
            reopened.Volume.TotalSpace.Should().Be(256L << 20);
        }
    }

    [Fact]
    public void TierFs_VirtualNewMultiCarrierWithoutQuota_FailsFast()
    {
        var vol = Path.Combine(_dir, "spec-multi.tier");
        var spec = vol.Replace('\\', '/');
        var ex = Assert.Throws<NotSupportedException>(
            () => TierFs.New($"virtual:///{spec}?member=/nonexistent/v2.tier"));
        ex.Message.Should().Contain("quota", "多载体 New 须显式供给——自动扩容仅限单文件载体");
    }
}
