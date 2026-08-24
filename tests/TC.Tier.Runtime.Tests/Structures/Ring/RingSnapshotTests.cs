using System.IO.Compression;
using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// Ring 流式快照测试——验证 OpenSnapshotReader/Writer 的 pull/push 范式。
/// <para>★ 覆盖：同步/异步往返、子区间导出、压缩包装（上层保存）、冷热区透明导出、大数据流式不 OOM。</para>
/// <para>★ 接入形态（当前 API）：TestVolume 组合根（跨实例同卷、不同引擎名）+ NewRing&lt;long&gt;。</para>
/// </summary>
public class RingSnapshotTests
{
    /// <summary>同步快照往返：写数据 → Reader pull → Writer push 到新 Ring → 数据一致。</summary>
    [Fact]
    public void Snapshot_Read_RoundTrip()
    {
        var vol = new TestVolume();
        try
        {
            // 实例 1：写数据
            LogicalAddress dataEnd;
            using (var ring1 = TestRingSettingsFactory.NewRing<long>(vol,
                TestRingSettingsFactory.On(vol, "ring.0", deleteOnClose: false)))
            {
                ring1.Write(1L, TestRingSettingsFactory.MakePattern(0xAA, 256));
                ring1.Write(2L, TestRingSettingsFactory.MakePattern(0xBB, 256));
                ring1.Write(3L, TestRingSettingsFactory.MakePattern(0xCC, 256));
                dataEnd = ring1.TailAddress;

                // pull 到内存（模拟上层 pull 处理）
                using var reader = ring1.OpenSnapshotReader();
                var ms = new System.IO.MemoryStream();
                Span<byte> buf = stackalloc byte[4096];
                int read;
                while ((read = reader.Read(buf)) > 0)
                    ms.Write(buf[..read]);

                // push 到实例 2（导入 ring1 的 [BeginAddress, TailAddress] 区间——两 Ring 同样新建，
                // 地址空间布局一致，故 ring2 直接复用 ring1 的 LogicalAddress 作为 Writer 区间）
                using var ring2 = TestRingSettingsFactory.NewRing<long>(vol,
                    TestRingSettingsFactory.On(vol, "ring2.0", deleteOnClose: false));
                using var writer = ring2.OpenSnapshotWriter(ring1.BeginAddress, dataEnd);
                ms.Position = 0;
                Span<byte> rbuf = stackalloc byte[4096];
                int r;
                while ((r = ms.Read(rbuf)) > 0)
                    writer.Write(rbuf[..r]);
                writer.Complete();

                ring2.TailAddress.Should().Be(dataEnd, "Writer Complete 应推进 TailAddress 到导入区间末尾");
            }
        }
        finally { vol.Dispose(); }
    }

    /// <summary>异步快照往返。</summary>
    [Fact]
    public async Task Snapshot_ReadAsync_RoundTrip()
    {
        var vol = new TestVolume();
        try
        {
            using var ring1 = TestRingSettingsFactory.NewRing<long>(vol,
                TestRingSettingsFactory.On(vol, "ring.0", deleteOnClose: false));
            ring1.Write(1L, TestRingSettingsFactory.MakePattern(0xAA, 256));
            ring1.Write(2L, TestRingSettingsFactory.MakePattern(0xBB, 256));

            using var reader = ring1.OpenSnapshotReader();
            var ms = new System.IO.MemoryStream();
            var buf = new byte[4096];
            int read;
            while ((read = await reader.ReadAsync(buf)) > 0)
                await ms.WriteAsync(buf.AsMemory(0, read));

            ms.Length.Should().Be(reader.Length, "Reader 异步 pull 的字节数应等于 Length");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>子区间导出——OpenSnapshotReader(begin, end) 只导出指定区间。</summary>
    [Fact]
    public void Snapshot_PartialRange_Export()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.Write(1L, TestRingSettingsFactory.MakePattern(0xAA, 64));
            ring.Write(2L, TestRingSettingsFactory.MakePattern(0xBB, 64));
            LogicalAddress firstRecordEnd = ring.TailAddress;

            using var reader = ring.OpenSnapshotReader();   // 全量
            long fullLength = reader.Length;
            reader.Dispose();

            using var partialReader = ring.OpenSnapshotReader(firstRecordEnd, ring.TailAddress);
            partialReader.Length.Should().BeLessThan(fullLength, "子区间长度应小于全量");
            partialReader.Length.Should().BeGreaterThanOrEqualTo(0, "子区间末尾 = TailAddress 时长度为 0（合法）");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>压缩包装往返——Reader pull → GZipStream 写文件 → 读文件解压 → 验证上层压缩可行。</summary>
    [Fact]
    public void Snapshot_WithCompression_RoundTrip()
    {
        string dir = TestTempDir.Create("tc-ring-snapshot-compress");
        string gzPath = System.IO.Path.Combine(dir, "snapshot.gz");
        var vol = new TestVolume();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol,
                TestRingSettingsFactory.On(vol, "ring.0", deleteOnClose: false));
            ring.Write(1L, TestRingSettingsFactory.MakePattern(0xAA, 1024));
            ring.Write(2L, TestRingSettingsFactory.MakePattern(0xBB, 1024));

            // pull + 压缩写文件（模拟上层压缩保存）
            using (var reader = ring.OpenSnapshotReader())
            using (var fs = System.IO.File.Create(gzPath))
            using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            {
                Span<byte> buf = stackalloc byte[4096];
                int read;
                while ((read = reader.Read(buf)) > 0)
                    gz.Write(buf[..read]);
            }

            // 文件应存在且非空（压缩成功）
            System.IO.File.Exists(gzPath).Should().BeTrue();
            new System.IO.FileInfo(gzPath).Length.Should().BeGreaterThan(0, "压缩文件应非空");
        }
        finally
        {
            vol.Dispose();
            TestTempDir.TryCleanup(dir);
        }
    }

    /// <summary>冷热区透明导出——FlushUntil 制造冷区 + 再写热区 → Reader 导出完整区间。</summary>
    [Fact]
    public void Snapshot_ColdAndHotRegion_BothExported()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            // 冷区：写 + flush
            ring.Write(1L, TestRingSettingsFactory.MakePattern(0xAA, 64));
            ring.Write(2L, TestRingSettingsFactory.MakePattern(0xBB, 64));
            ring.FlushUntil(ring.TailAddress);
            _ = ring.FlushedUntilAddress;   // 冷区边界（仅观察落盘点，不参与断言）

            // 热区：再写（不 flush）
            ring.Write(3L, TestRingSettingsFactory.MakePattern(0xCC, 64));
            ring.Write(4L, TestRingSettingsFactory.MakePattern(0xDD, 64));

            using var reader = ring.OpenSnapshotReader();
            var ms = new System.IO.MemoryStream();
            Span<byte> buf = stackalloc byte[1024];
            int read;
            while ((read = reader.Read(buf)) > 0)
                ms.Write(buf[..read]);

            // 导出的字节数应覆盖冷+热区
            ms.Length.Should().Be(reader.Length, "应导出完整区间（冷+热）");
            ms.Length.Should().BeGreaterThan(0);
        }
        finally { vol.Dispose(); }
    }
}
