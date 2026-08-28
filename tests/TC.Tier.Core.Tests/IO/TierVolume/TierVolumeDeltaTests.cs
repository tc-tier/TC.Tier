using System.IO.Hashing;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Image;
using TC.Tier.Core.IO.TierVolume;

namespace TC.Tier.Core.Tests.IO.TierVolume;

/// <summary>
/// 增量导出（V2 §1.2——journal delta 帧 = 操作级增量）契约测试族：
/// 导出/还原等价 / 基线校验（UUID·头对齐·镜像 CRC）/ 链路增量 / 检查点截断窗口 / 管线面。
/// <para>★ 副本纪律：dd 副本与源卷同 UUID——同进程互斥（一卷一实例，§2.4）→ 备份流 = 序贯
/// （源导出 → 关源 → 开副本应用；跨进程/跨机由副本独立锁文件自然支持）。</para>
/// </summary>
public sealed class TierVolumeDeltaTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-tv-delta");
    private readonly string _srcPath;
    private readonly string _replicaPath;

    public TierVolumeDeltaTests()
    {
        _srcPath = Path.Combine(_dir, "src.tier");
        _replicaPath = Path.Combine(_dir, "replica.tier");
    }

    public void Dispose() => TestTempDir.TryCleanup(_dir);

    private TierVolumeFs Format(string path, long capacity = 128L << 20)
        => TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions
        {
            QuotaBytes = capacity,
            JournalReserveBytes = 8L << 20,
        });

    private static FileOpenOptions RWO() => new()
    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite };

    private static FileOpenOptions RO() => new()
    { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite };

    /// <summary>写入基准集（确定性数据）+ 检查点（基线 = CkptLsn）。</summary>
    private static ulong WriteBaseline(TierVolumeFs fs, int files, int bytesPerFile)
    {
        for (var i = 0; i < files; i++)
        {
            using var h = fs.Open($"base{i}", RWO());
            var buf = new byte[bytesPerFile];
            new Random(i + 1).NextBytes(buf);
            h.Write(0, buf);
        }
        fs.FlushRoot();
        return fs.JournalCheckpointLsn;
    }

    /// <summary>卷状态指纹（名字:长度:内容 CRC——还原等价判据）。</summary>
    private static string Fingerprint(TierVolumeFs fs)
    {
        var parts = new List<string>();
        foreach (var e in fs.EnumerateEntries(recursive: true).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            using var h = fs.Open(e.Name, RO());
            var crc = new Crc32();
            var buf = new byte[64 * 1024];
            long done = 0;
            while (done < h.Length)
            {
                var n = h.Read(done, buf);
                if (n <= 0) break;
                crc.Append(buf.AsSpan(0, n));
                done += n;
            }
            parts.Add($"{e.Name}:{h.Length}:{crc.GetCurrentHashAsUInt32():X8}");
        }
        return string.Join("|", parts);
    }

    /// <summary>源卷演进 + 导出（导出内部提交在途记录、不检查点——CkptLsn 不漂移）→ 关源。</summary>
    private (byte[] Delta, SnapshotDeltaSummary Summary, string Fingerprint, ulong EndLsn)
        ChurnAndExport(ulong baseLsn, Action<TierVolumeFs> churn)
    {
        using var src = TierVolumeFs.Open(TierVolumeCarrier.File(_srcPath));
        churn(src);
        var delta = new MemoryStream();
        var summary = src.ExportDelta(delta, baseLsn);
        var fp = Fingerprint(src);
        var end = src.JournalCommittedLsn;
        src.Dispose();
        return (delta.ToArray(), summary, fp, end);
    }

    [Fact]
    public void ExportApply_RoundTrip_StateEquivalent()
    {
        using (var src = Format(_srcPath))
        {
            var baseLsn = WriteBaseline(src, 8, 32 * 1024);
            src.Dispose();
            File.Copy(_srcPath, _replicaPath);   // 全量基座（dd 快道——副本保卷身份）

            var result = ChurnAndExport(baseLsn, fs =>
            {
                for (var i = 0; i < 4; i++)
                {
                    using var h = fs.Open($"new{i}", RWO());
                    h.Write(0, new byte[16 * 1024]);
                }
                fs.Delete("base0");
                fs.Move("base1", "base1-renamed");
            });
            result.Summary.RecordCount.Should().BeGreaterThan(0, "变更 = 操作级记录流（无数据面扫描）");

            using var replica = TierVolumeFs.Open(TierVolumeCarrier.File(_replicaPath));
            replica.JournalCommittedLsn.Should().Be(baseLsn, "clean 副本零重放——提交头 == 基点");
            var applied = replica.ApplyDelta(new MemoryStream(result.Delta));
            applied.EndLsn.Should().Be(result.EndLsn);
            Fingerprint(replica).Should().Be(result.Fingerprint, "还原后状态与源一致（语义级增量重放）");
        }
    }

    [Fact]
    public void ExportDelta_ThroughPipeline_StateEquivalent()
    {
        using (var src = Format(_srcPath))
        {
            var baseLsn = WriteBaseline(src, 4, 16 * 1024);
            src.Dispose();
            File.Copy(_srcPath, _replicaPath);

            var result = ChurnAndExport(baseLsn, fs =>
            {
                using var h = fs.Open("n", RWO());
                h.Write(0, new byte[8 * 1024]);
            });

            using var replica = TierVolumeFs.Open(TierVolumeCarrier.File(_replicaPath));
            RootSpaceImage.ApplyDelta(new MemoryStream(result.Delta), replica);
            Fingerprint(replica).Should().Be(result.Fingerprint);
        }
    }

    [Fact]
    public void ApplyDelta_WrongVolume_Rejected()
    {
        var delta = Array.Empty<byte>();
        {
            var src = Format(_srcPath);
            var baseLsn = WriteBaseline(src, 2, 8 * 1024);
            src.Dispose();
            delta = ChurnAndExport(baseLsn, fs =>
            {
                using var h = fs.Open("n", RWO());
                h.Write(0, new byte[4 * 1024]);
            }).Delta;
        }

        var otherPath = Path.Combine(_dir, "other.tier");
        using var other = Format(otherPath);   // 异卷——UUID 不符
        Action apply = () => other.ApplyDelta(new MemoryStream(delta));
        apply.Should().Throw<FileIOException>().Where(ex => ex.Message.Contains("卷身份不符"));
    }

    [Fact]
    public void ApplyDelta_TargetNotAtBase_Rejected()
    {
        var delta = Array.Empty<byte>();
        using (var src = Format(_srcPath))
        {
            var baseLsn = WriteBaseline(src, 2, 8 * 1024);
            src.Dispose();
            File.Copy(_srcPath, _replicaPath);
            delta = ChurnAndExport(baseLsn, fs =>
            {
                using var h = fs.Open("n", RWO());
                h.Write(0, new byte[4 * 1024]);
            }).Delta;
        }

        using var replica = TierVolumeFs.Open(TierVolumeCarrier.File(_replicaPath));
        using (var h = replica.Open("drift", RWO()))
        {
            h.Write(0, new byte[1024]);
            h.Flush();   // 副本头推进——缺口含命名空间操作（双重应用风险）
        }
        Action apply = () => replica.ApplyDelta(new MemoryStream(delta));
        apply.Should().Throw<FileIOException>().Where(ex => ex.Message.Contains("目标不在基点"));
    }

    [Fact]
    public void ExportDelta_BaseOlderThanCheckpoint_Rejected()
    {
        using var src = Format(_srcPath);
        var oldBase = WriteBaseline(src, 2, 8 * 1024);
        using (var h = src.Open("a", RWO()))
            h.Write(0, new byte[4 * 1024]);
        src.FlushRoot();   // 检查点——oldBase 之前的记录截断
        var newBase = src.JournalCheckpointLsn;
        newBase.Should().BeGreaterThan(oldBase);
        using (var h = src.Open("b", RWO()))
            h.Write(0, new byte[4 * 1024]);
        Action exportOld = () => src.ExportDelta(new MemoryStream(), oldBase);
        exportOld.Should().Throw<FileIOException>().Where(ex => ex.Message.Contains("基点过旧"));
        Action exportNew = () => src.ExportDelta(new MemoryStream(), newBase);
        exportNew.Should().NotThrow();
    }

    [Fact]
    public void DeltaChain_ApplySequentialDeltas_StateConverges()
    {
        var d1 = Array.Empty<byte>();
        var d2 = Array.Empty<byte>();
        string finalFp = "";
        ulong end2 = 0;
        using (var src = Format(_srcPath))
        {
            var baseLsn = WriteBaseline(src, 4, 16 * 1024);
            src.Dispose();
            File.Copy(_srcPath, _replicaPath);

            var r1 = ChurnAndExport(baseLsn, fs =>
            {
                using var h = fs.Open("c1", RWO());
                h.Write(0, new byte[8 * 1024]);
            });
            d1 = r1.Delta;
            var r2 = ChurnAndExport(r1.EndLsn, fs =>
            {
                using var h = fs.Open("c2", RWO());
                h.Write(0, new byte[8 * 1024]);
                fs.Delete("base0");
            });
            d2 = r2.Delta;
            finalFp = r2.Fingerprint;
            end2 = r2.EndLsn;
        }

        using var replica = TierVolumeFs.Open(TierVolumeCarrier.File(_replicaPath));
        var a1 = replica.ApplyDelta(new MemoryStream(d1));
        var a2 = replica.ApplyDelta(new MemoryStream(d2));   // 应用后检查点 CkptLsn = 头——链路基点匹配
        a2.EndLsn.Should().Be(end2);
        a1.EndLsn.Should().BeLessThan(a2.EndLsn);
        Fingerprint(replica).Should().Be(finalFp, "链路增量逐段收敛");
    }

    [Fact]
    public void SnapshotAsBase_ExportFromCaptureLsn_RestoresPastSnapshot()
    {
        var delta = Array.Empty<byte>();
        string finalFp = "";
        using (var src = Format(_srcPath))
        {
            WriteBaseline(src, 4, 16 * 1024);
            var snap = src.CreateSnapshot("s-base");   // 捕获 = 检查点——CaptureLsn = 基点
            snap.CaptureLsn.Should().Be(src.JournalCheckpointLsn);
            src.Dispose();
            File.Copy(_srcPath, _replicaPath);   // 快照时刻全量基座（副本含快照表 + SnapshotCreate 记录已提交）

            var result = ChurnAndExport(snap.CaptureLsn, fs =>
            {
                using var h = fs.Open("after", RWO());
                h.Write(0, new byte[8 * 1024]);
            });
            result.Summary.BaseLsn.Should().Be(snap.CaptureLsn);
            delta = result.Delta;
            finalFp = result.Fingerprint;
        }

        // 副本头 = 捕获 LSN+1（clean 关闭提交了 SnapshotCreate 记录）——缺口仅快照表变更 → 放行
        using var replica = TierVolumeFs.Open(TierVolumeCarrier.File(_replicaPath));
        replica.ApplyDelta(new MemoryStream(delta));
        Fingerprint(replica).Should().Be(finalFp);
    }

    [Fact]
    public void ExportDelta_NonJournaledVolume_Rejected()
    {
        var path = Path.Combine(_dir, "nj.tier");
        using var fs = TierVolumeFs.New(TierVolumeCarrier.File(path), new TierVolumeFormatOptions
        {
            QuotaBytes = 32L << 20,
            JournalReserveBytes = 0,
        });
        Action export = () => fs.ExportDelta(new MemoryStream(), 0);
        export.Should().Throw<FileIOException>().Where(ex => ex.Error == IOError.Unsupported);
    }

    [Fact]
    public void ApplyDelta_CorruptStream_Rejected()
    {
        var delta = Array.Empty<byte>();
        using (var src = Format(_srcPath))
        {
            var baseLsn = WriteBaseline(src, 2, 8 * 1024);
            src.Dispose();
            File.Copy(_srcPath, _replicaPath);   // 全量基座（clean——副本提交头 = 基点）
            delta = ChurnAndExport(baseLsn, fs =>
            {
                using var h = fs.Open("n", RWO());
                h.Write(0, new byte[4 * 1024]);
            }).Delta;
        }

        delta[DeltaHeaderSize + 32 + 4] ^= 0xFF;   // 破坏首条记录体字节

        using var replica = TierVolumeFs.Open(TierVolumeCarrier.File(_replicaPath));
        Action apply = () => replica.ApplyDelta(new MemoryStream(delta));
        apply.Should().Throw<FileIOException>().Where(ex => ex.Message.Contains("CRC"));
    }

    private const int DeltaHeaderSize = 44;
}
