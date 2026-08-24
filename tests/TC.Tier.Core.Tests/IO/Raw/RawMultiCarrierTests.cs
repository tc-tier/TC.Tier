using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Raw;

namespace TC.Tier.Core.Tests.IO.Raw;

/// <summary>
/// 多载体卷（RM-04 §3.8）契约测试族——线性拼接 / 在线扩容 / 减空载体 / 身份装配 / 崩溃恢复。
/// </summary>
public sealed class RawMultiCarrierTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-raw-multi");

    public void Dispose() => TestTempDir.TryCleanup(_dir);

    private string PathOf(string name) => System.IO.Path.Combine(_dir, name);

    private static FileOpenOptions RWO() => new()
    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite };

    [Fact]
    public void AddCarrier_ExpandsCapacity_DataSpansMembers()
    {
        using var fs = RawFileSystem.New(RawCarrier.File(PathOf("m0.raw")),
            new RawFormatOptions { QuotaBytes = 8L << 20 });   // 8MB（64 块对齐余量足够）
        var before = fs.Volume.TotalSpace;
        using (var h = fs.Open("old", RWO()))
        {
            h.Write(0, new byte[4096]);
            h.Flush();
        }
        fs.AddCarrier(RawCarrier.File(PathOf("m1.raw")), capacityBytes: 8L << 20);
        fs.Volume.TotalSpace.Should().Be(before + (8L << 20), "在线扩容——容量立即增长");

        // 写跨成员数据（8MB+ 数据必落新成员）
        using (var h = fs.Open("big", RWO()))
        {
            var chunk = new byte[1 << 20];
            new Random(5).NextBytes(chunk);
            for (var i = 0; i < 10; i++)
                h.Append(chunk);   // 10MB > m0 剩余——必跨成员
            h.Flush();
            h.Length.Should().Be(10L << 20);
        }
        // 旧数据完好
        using (var h = fs.Open("old", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
        {
            var buf = new byte[4096];
            h.Read(0, buf).Should().Be(4096);
        }
    }

    [Fact]
    public void MultiCarrier_CrashReplay_OpenWithFullCarrierList()
    {
        var m0 = RawCarrier.File(PathOf("c0.raw"));
        var m1 = RawCarrier.File(PathOf("c1.raw"));
        var fs = RawFileSystem.New(m0, new RawFormatOptions { QuotaBytes = 8L << 20 });
        fs.AddCarrier(m1, capacityBytes: 8L << 20);
        using (var h = fs.Open("span", RWO()))
        {
            var chunk = new byte[1 << 20];
            new Random(7).NextBytes(chunk);
            for (var i = 0; i < 10; i++)
                h.Append(chunk);   // 跨成员数据
            h.Flush();
        }
        fs.CrashSimulate();   // dirty 残留

        // 全量清单重开（身份装配）→ 日志重放 → 数据完整
        using var fs2 = RawFileSystem.Open([m0, m1]);
        using (var h = fs2.Open("span", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
        {
            h.Length.Should().Be(10L << 20, "跨成员数据经日志重放完整");
            var probe = new byte[4096];
            h.Read((10L << 20) - 4096, probe).Should().Be(4096, "尾块在新成员可读");
        }
    }

    [Fact]
    public void Open_MissingCarrier_Refused()
    {
        var m0 = RawCarrier.File(PathOf("d0.raw"));
        var m1 = RawCarrier.File(PathOf("d1.raw"));
        var fs = RawFileSystem.New(m0, new RawFormatOptions { QuotaBytes = 8L << 20 });
        fs.AddCarrier(m1, capacityBytes: 8L << 20);
        fs.Dispose();
        var act = () => RawFileSystem.Open(m0);   // 只给主载体——成员缺失
        act.Should().Throw<FileIOException>("成员载体数不符——全量提交契约（§3.8 成员缺失即拒开）");
    }

    [Fact]
    public void Open_IdentityMismatch_Refused()
    {
        var m0 = RawCarrier.File(PathOf("i0.raw"));
        var m1 = RawCarrier.File(PathOf("i1.raw"));
        var fs = RawFileSystem.New(m0, new RawFormatOptions { QuotaBytes = 8L << 20 });
        fs.AddCarrier(m1, capacityBytes: 8L << 20);
        fs.Dispose();
        // 顶替成员：另一独立格式化卷的载体（UUID 不匹配）
        var stranger = RawCarrier.File(PathOf("stranger.raw"));
        using (var other = RawFileSystem.New(stranger, new RawFormatOptions { QuotaBytes = 8L << 20 })) { }
        var act = () => RawFileSystem.Open([m0, stranger]);
        act.Should().Throw<FileIOException>("成员身份（UUID/索引）不符——拒开（§3.8）");
    }

    [Fact]
    public void RemoveCarrier_EmptyAndMigrated()
    {
        var m0 = RawCarrier.File(PathOf("r0.raw"));
        var m1 = RawCarrier.File(PathOf("r1.raw"));
        var m2 = RawCarrier.File(PathOf("r2.raw"));
        var fs = RawFileSystem.New(m0, new RawFormatOptions { QuotaBytes = 8L << 20 });
        fs.AddCarrier(m1, capacityBytes: 8L << 20);
        fs.AddCarrier(m2, capacityBytes: 8L << 20);
        var total = fs.Volume.TotalSpace;

        // 填充 m0 → m1（写 10MB——m0 剩余 + m1 部分；m2 保持空）
        using (var h = fs.Open("fill", RWO()))
        {
            var chunk = new byte[1 << 20];
            for (var i = 0; i < 10; i++)
                h.Append(chunk);
            h.Flush();
        }
        // 移除非空成员（索引 1 = m1）→ v2a 迁移式缩容（数据搬迁到 m0/m2 后摘除）
        fs.RemoveCarrier(1);
        // 移除空成员（摘除 1 后 m2 = 索引 1）→ 成功
        fs.RemoveCarrier(1);
        fs.Volume.TotalSpace.Should().Be(total - 2 * (8L << 20), "两成员摘除（迁移+空）——容量回收");
        // 数据仍完整（fill 的数据已迁移到 m0）
        using (var h = fs.Open("fill", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
            h.Length.Should().Be(10L << 20);
        // 关闭重开（单清单 = m0）
        fs.Dispose();
        using var fs2 = RawFileSystem.Open([m0]);
        fs2.Exists("fill").Should().BeTrue("迁移+摘除后卷完整（单载体）");
        using (var h = fs2.Open("fill", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
        {
            h.Length.Should().Be(10L << 20);
            var probe = new byte[4096];
            h.Read(0, probe).Should().Be(4096, "迁移后数据可读");
        }
    }

    [Fact]
    public void RemoveCarrier_MigratesData_ContentIntactThroughCrash()
    {
        var m0 = RawCarrier.File(PathOf("mg0.raw"));
        var m1 = RawCarrier.File(PathOf("mg1.raw"));
        var fs = RawFileSystem.New(m0, new RawFormatOptions { QuotaBytes = 32L << 20 });   // 迁入目标容量
        fs.AddCarrier(m1, capacityBytes: 8L << 20);
        // 数据写满 m1（8MB 保留区后余量 ~6.4MB）+ 溢出——移除 m1 必迁移 ~6MB
        var payload = new byte[1 << 20];
        new Random(21).NextBytes(payload);
        using (var h = fs.Open("must-survive", RWO()))
        {
            for (var i = 0; i < 10; i++)
                h.Append(payload);
            h.Flush();
        }
        // 迁移式移除 m1（数据全在 m1——必迁移）
        fs.RemoveCarrier(1);
        fs.CrashSimulate();   // 迁移+摘除后立即崩溃——重放 ExtentRelocate 后数据完整

        using var fs2 = RawFileSystem.Open([m0]);
        using (var h = fs2.Open("must-survive", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
        {
            h.Length.Should().Be(10L << 20, "迁移长度保真");
            var probe = new byte[1 << 20];
            for (var i = 0; i < 10; i += 4)   // 抽查 3 段
            {
                h.Read((long)i << 20, probe).Should().Be(1 << 20);
                probe.Should().BeEquivalentTo(payload, $"迁移块 {i}MB 处内容保真");
            }
        }
    }

    [Fact]
    public void AddCarrier_AlreadyFormattedCarrier_Refused_PrimaryNotRemovable()
    {
        var m0 = RawCarrier.File(PathOf("p0.raw"));
        using var fs = RawFileSystem.New(m0, new RawFormatOptions { QuotaBytes = 8L << 20 });
        using var other = RawFileSystem.New(RawCarrier.File(PathOf("p1.raw")),
            new RawFormatOptions { QuotaBytes = 8L << 20 });
        other.Dispose();
        var act = () => fs.AddCarrier(RawCarrier.File(PathOf("p1.raw")), capacityBytes: 8L << 20);
        act.Should().Throw<FileIOException>().WithMessage("*已是 TC 卷成员*",
            "误并入已格式化载体 = 数据丢失脚枪——显式拒绝");
        var act2 = () => fs.RemoveCarrier(0);
        act2.Should().Throw<ArgumentException>("主载体（superblock 权威）不可移除");
    }

    // ═══ v2b 降级运行（成员缺失只读）═══

    [Fact]
    public void DegradedOpen_MissingMember_ReadOnly_HonestDataAccess()
    {
        var m0 = RawCarrier.File(PathOf("g0.raw"));
        var m1 = RawCarrier.File(PathOf("g1.raw"));
        using (var fs = RawFileSystem.New(m0, new RawFormatOptions { QuotaBytes = 8L << 20 }))
        {
            fs.AddCarrier(m1, capacityBytes: 32L << 20);   // 大成员承接溢出
            using (var h = fs.Open("on-m0", RWO()))
            {
                h.Write(0, new byte[4096]);
                h.Flush();
            }
            using (var h = fs.Open("spill", RWO()))
            {
                var chunk = new byte[1 << 20];
                new Random(31).NextBytes(chunk);
                for (var i = 0; i < 10; i++)   // m0 仅 ~6MB 可用——后半必然溢到 m1
                    h.Append(chunk);
                h.Flush();
            }
        }   // clean 关闭

        // 全量成员缺失 null → 拒开（须显式 AllowDegraded）
        var actStrict = () => RawFileSystem.Open([m0, null]);
        actStrict.Should().Throw<FileIOException>("null 占位须 AllowDegraded——静默降级禁止");

        // 降级打开：只读形态（块作用域——修复路径重开前释放实例登记）
        using (var deg = RawFileSystem.Open([m0, null], new RawOpenOptions { AllowDegraded = true }))
        {
            deg.Exists("on-m0").Should().BeTrue("m0 数据可读（元数据锚定主载体——完整可见）");
            using (var h = deg.Open("on-m0", new FileOpenOptions
            { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
            {
                var buf = new byte[4096];
                h.Read(0, buf).Should().Be(4096, "健康成员数据正常读");
            }
            // 写拒绝
            var actWrite = () => deg.Open("new-file", RWO());
            actWrite.Should().Throw<FileIOException>("降级卷零写（RM-04 v2b）");
            // 缺失成员上的数据诚实拒绝（深偏移——必然落在 m1 的块）
            using (var h = deg.Open("spill", new FileOpenOptions
            { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
            {
                var actRead = () => h.Read((9L << 20), new byte[4096]);
                actRead.Should().Throw<FileIOException>("数据在缺失成员——诚实拒绝（洞数据不可伪造）");
            }
        }
        // 修复路径：全量重开恢复正常
        using (var healed = RawFileSystem.Open([m0, m1]))
        {
            using (var h = healed.Open("spill", new FileOpenOptions
            { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
                h.Length.Should().Be(10L << 20, "全量成员重开 = 修复（数据完整）");
        }
    }
}