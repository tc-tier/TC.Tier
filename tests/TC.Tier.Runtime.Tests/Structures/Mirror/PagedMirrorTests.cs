using FluentAssertions;
using TC.Tier.Runtime.Structures.Mirror;

namespace TC.Tier.Runtime.Tests.Structures.Mirror;

/// <summary>
/// PagedMirror 单元测试（per-page 多链：WritePage 可乱序 + 多链 N=2 + 2PC 整体原子性）。
/// </summary>
public class PagedMirrorTests
{
    private static byte[] MakePage(int size, byte fill)
    {
        var b = new byte[size];
        Array.Fill(b, fill);
        return b;
    }

    [Fact]
    public void Initialize_WaitForReady_Ready()
    {
        var (settings, vol) = TestMirrorSettingsFactory.CreatePagedSettings();
        try
        {
            using var mirror = new PagedMirror(vol.Fs, settings);
            mirror.Initialize();
            mirror.WaitForReady();
            mirror.IsReady.Should().BeTrue();
            mirror.PageSize.Should().Be(4096);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void WritePage_ReadPage_Roundtrip()
    {
        var (settings, vol) = TestMirrorSettingsFactory.CreatePagedSettings();
        try
        {
            using var mirror = new PagedMirror(vol.Fs, settings);
            mirror.Initialize();
            mirror.WaitForReady();

            // 乱序写三页（多链互不影响）——填充值与页号解耦（0x00/0x11/0x22）
            byte[] fills = [0x00, 0x11, 0x22];
            mirror.WritePage(2, 0, MakePage(4096, fills[2]), logicalAddress: 200);
            mirror.WritePage(0, 0, MakePage(4096, fills[0]), logicalAddress: 0);
            mirror.WritePage(1, 0, MakePage(4096, fills[1]), logicalAddress: 100);
            mirror.Prepare(seq: 1);
            mirror.ConfirmCommitted(seq: 1);

            for (byte p = 0; p < 3; p++)
            {
                var dst = new byte[4096];
                var (n, valid) = mirror.ReadPage(p, 0, dst);
                n.Should().Be(4096);
                valid.Should().BeTrue($"page {p} CRC32C 应有效");
                dst[0].Should().Be(fills[p]);
            }
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void LastPartialPage_FlagAndShortRead()
    {
        var (settings, vol) = TestMirrorSettingsFactory.CreatePagedSettings();
        try
        {
            using var mirror = new PagedMirror(vol.Fs, settings);
            mirror.Initialize();
            mirror.WaitForReady();

            mirror.WritePage(0, 0, MakePage(1000, 0x77)); // < PageSize → FLAG_LAST_PARTIAL
            mirror.Prepare(seq: 1);
            mirror.ConfirmCommitted(seq: 1);

            var dst = new byte[4096];
            var (n, valid) = mirror.ReadPage(0, 0, dst);
            n.Should().Be(1000, "末页短 payload 读回实际长度");
            valid.Should().BeTrue();
            dst[0].Should().Be(0x77);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void WritePage_Prepare_Abort_Rollback()
    {
        var (settings, vol) = TestMirrorSettingsFactory.CreatePagedSettings();
        try
        {
            using var mirror = new PagedMirror(vol.Fs, settings);
            mirror.Initialize();
            mirror.WaitForReady();

            // 会话 1：v1 提交
            mirror.WritePage(0, 0, MakePage(4096, 0x11));
            mirror.WritePage(1, 0, MakePage(4096, 0x11));
            mirror.Prepare(seq: 1);
            mirror.ConfirmCommitted(seq: 1);

            // 会话 2：v2 写完 + Prepare → Abort（悬干整体回退——多链原子性）
            mirror.WritePage(0, 0, MakePage(4096, 0x22));
            mirror.WritePage(1, 0, MakePage(4096, 0x22));
            mirror.Prepare(seq: 2);
            mirror.Abort(seq: 2);

            mirror.CurrentVersion.Should().Be(1, "Abort 后版本回退");
            for (byte p = 0; p < 2; p++)
            {
                var dst = new byte[4096];
                var (n, valid) = mirror.ReadPage(p, 0, dst);
                n.Should().Be(4096);
                dst[0].Should().Be(0x11, $"page {p} 应回退到 v1");
                valid.Should().BeTrue();
            }
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void ReadPage_UnknownPage_ReturnsZero()
    {
        var (settings, vol) = TestMirrorSettingsFactory.CreatePagedSettings();
        try
        {
            using var mirror = new PagedMirror(vol.Fs, settings);
            mirror.Initialize();
            mirror.WaitForReady();

            var (n, valid) = mirror.ReadPage(42, 0, new byte[4096]);
            n.Should().Be(0);
            valid.Should().BeFalse();
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void MultiSession_N2_RotationKeepsLatestTwo()
    {
        var (settings, vol) = TestMirrorSettingsFactory.CreatePagedSettings();
        try
        {
            using var mirror = new PagedMirror(vol.Fs, settings);
            mirror.Initialize();
            mirror.WaitForReady();

            for (int s = 1; s <= 3; s++)
            {
                mirror.WritePage(0, 0, MakePage(4096, (byte)(0x10 + s)));
                mirror.WritePage(1, 0, MakePage(4096, (byte)(0x10 + s)));
                mirror.Prepare(seq: s);
                mirror.ConfirmCommitted(seq: s);
            }

            mirror.CurrentVersion.Should().Be(3);
            mirror.LowestVersionAddress.Should().NotBe(LogicalAddress.Empty, "多链 N=2 已产生回收边界");
            for (byte p = 0; p < 2; p++)
            {
                var dst = new byte[4096];
                var (n, valid) = mirror.ReadPage(p, 0, dst);
                n.Should().Be(4096);
                valid.Should().BeTrue();
                dst[0].Should().Be(0x13, $"page {p} 链头是会话 3");
            }
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void CrossInstance_Recovery_PerPageChains()
    {
        var vol = new TestVolume();
        var settings1 = new PagedMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(false))
        { LogPageSizeBits = 12 };
        using (var mirror1 = new PagedMirror(vol.Fs, settings1))
        {
            mirror1.Initialize();
            mirror1.WaitForReady();
            mirror1.WritePage(0, 0, MakePage(4096, 0x11));
            mirror1.WritePage(1, 0, MakePage(4096, 0x11));
            mirror1.Prepare(1);
            mirror1.ConfirmCommitted(1);
            mirror1.WritePage(0, 0, MakePage(4096, 0x22));
            mirror1.Prepare(2);
            mirror1.ConfirmCommitted(2); // 会话 2 只重写 page 0——page 1 链头保持 v1（按页独立推进）
        }

        var settings2 = new PagedMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(true))
        { LogPageSizeBits = 12 };
        using var mirror2 = new PagedMirror(vol.Fs, settings2);
        mirror2.Initialize();
        mirror2.WaitForReady();

        mirror2.CurrentVersion.Should().Be(2);
        var dst0 = new byte[4096];
        mirror2.ReadPage(0, 0, dst0);
        dst0[0].Should().Be(0x22, "page 0 链头恢复到会话 2");
        var dst1 = new byte[4096];
        mirror2.ReadPage(1, 0, dst1);
        dst1[0].Should().Be(0x11, "page 1 未在会话 2 重写——链头保持会话 1");
        vol.Dispose();
    }
}
