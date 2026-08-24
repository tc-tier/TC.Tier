using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Raw;

namespace TC.Tier.Core.Tests.IO.Raw;

/// <summary>
/// DONTNEED 流式纪律与异步预读回归族（2026-08-19 性能轮）：
/// 文件载体缓冲 IO + fadvise(DONTNEED)（内核吸收写突发、OS 缓存驻留受控）/ 预取窗口驻留 /
/// MMF 写入对直达读可见（视图关闭排干收口）。
/// </summary>
public sealed class RawDioAndPrefetchTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-raw-dio");
    private readonly RawFileSystem _fs;

    public RawDioAndPrefetchTests()
    {
        _fs = RawFileSystem.New(RawCarrier.File(Path.Combine(_dir, "v.raw")),
            new RawFormatOptions { QuotaBytes = 64L << 20, JournalReserveBytes = 0 });
    }

    public void Dispose()
    {
        _fs.Dispose();
        TestTempDir.TryCleanup(_dir);
    }

    private static FileOpenOptions RWOpts() => new()
    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite };

    private static FileOpenOptions ROpts() => new()
    { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite };

    // ═══════════════ DONTNEED 纪律下的载体基础语义 ═══════════════

    [Fact]
    public void FileCarrier_WriteReadRoundtrip()
    {
        // 文件载体 = 缓冲 IO + DONTNEED 流式纪律（设备载体 O_DIRECT——本族不涉及）
        using (var h = _fs.Open("f", RWOpts()))
        {
            var data = new byte[64 << 10].Select((_, i) => (byte)(i % 251)).ToArray();
            h.Write(0, data);
            var buf = new byte[64 << 10];
            h.Read(0, buf).Should().Be(64 << 10);
            buf.Should().BeEquivalentTo(data, "载体读写往返保真（DONTNEED 纪律下数据完整）");
        }
        // 部分块写零基（B1 族）在 O_DIRECT 载体上依然成立
        using (var h = _fs.Open("partial", RWOpts()))
        {
            h.Write(0, new byte[4096].Select((_, i) => (byte)0x11).ToArray());
            h.Write(4096 + 512, new byte[] { 0x99 });
            var z = new byte[512];
            h.Read(4096, z).Should().Be(512);
            z.Should().OnlyContain(x => x == 0, "洞未写部分零基（DONTNEED 纪律下语义不变）");
        }
    }

    [Fact]
    public void DirectHandle_Reads_SeeCarrierData()
    {
        using (var h = _fs.Open("f", RWOpts()))
            h.Write(0, new byte[1 << 20].Select((_, i) => (byte)(i % 97)).ToArray());

        var dio = new FileOpenOptions
        {
            Access = AccessMode.Read,
            Mode = FileOpenMode.OpenExisting,
            Sharing = FileSharing.ReadWrite,
            Hints = FileOpenHints.NoBuffering,
        };
        using var dh = _fs.Open("f", dio);
        var buf = new byte[4096];
        dh.Read(123 << 10, buf).Should().Be(4096);
        buf.Should().BeEquivalentTo(Enumerable.Range(0, 4096).Select(i => (byte)((123 * 1024 + i) % 97)),
            "直达读（绕过自管缓存）读回写入数据（写后 DONTNEED 不损数据）");
    }

    // ═══════════════ 异步预取 ═══════════════

    [Fact]
    public void SequentialReads_PrefetchesAheadWindow()
    {
        using (var h = _fs.Open("big", RWOpts()))
            h.Write(0, new byte[2 << 20].Select((_, i) => (byte)(i % 251)).ToArray());

        var baseResident = _fs.ResidentPageCount;
        using (var rh = _fs.Open("big", ROpts()))
        {
            var buf = new byte[4096];
            rh.Read(0, buf).Should().Be(4096);
            rh.Read(4096, buf).Should().Be(4096);   // 连续读（自动顺序检测——无需 Advise）

            // 预读线程装入后续窗口（32 块）——轮询等待驻留页增长（尽力而为，时限宽容）
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (_fs.ResidentPageCount < baseResident + 4 && DateTime.UtcNow < deadline)
                Thread.Sleep(20);
            _fs.ResidentPageCount.Should().BeGreaterThan(baseResident + 2,
                "连续读自动触发异步预取后续窗口（内核 readahead 同款启发式）");
        }
    }

    [Fact]
    public void PrefetchedPages_ServeSubsequentReads()
    {
        using (var h = _fs.Open("big", RWOpts()))
            h.Write(0, new byte[2 << 20].Select((_, i) => (byte)(i % 251)).ToArray());

        using (var rh = _fs.Open("big", ROpts()))
        {
            var buf = new byte[4096];
            rh.Read(0, buf);
            rh.Read(4096, buf);   // 触发预取
            Thread.Sleep(300);   // 预读窗口装填

            // 后续读命中预取页——内容保真
            rh.Read(64 << 10, buf).Should().Be(4096);
            buf.Should().BeEquivalentTo(Enumerable.Range(0, 4096).Select(i => (byte)((64 * 1024 + i) % 251)),
                "预取页内容保真（与按需装入同源）");
        }
    }

    // ═══════════════ MMF 写入对 O_DIRECT 读可见 ═══════════════

    [Fact]
    public void MappedWrite_VisibleToDirectRead_AfterViewDispose()
    {
        using (var h = _fs.Open("m", RWOpts()))
        {
            h.Write(0, new byte[1 << 20].Select((_, i) => (byte)0x11).ToArray());
            using (var map = h.Map(4096, 4096, AccessMode.ReadWrite))
            {
                var view = map.View.Span;
                for (var i = 0; i < view.Length; i++) view[i] = (byte)(i % 251);
            }   // 视图关闭 → msync + fsync（DIO 载体一致性收口）
        }

        var dio = new FileOpenOptions
        {
            Access = AccessMode.Read,
            Mode = FileOpenMode.OpenExisting,
            Sharing = FileSharing.ReadWrite,
            Hints = FileOpenHints.NoBuffering,
        };
        using var dh = _fs.Open("m", dio);
        var buf = new byte[4096];
        dh.Read(4096, buf).Should().Be(4096);
        buf.Should().BeEquivalentTo(Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)),
            "MMF 视图写入经排干收口后对直达读可见");
        dh.Read(0, buf).Should().Be(4096);
        buf.Should().OnlyContain(b => b == 0x11, "视图外数据未被触碰");
    }
}
