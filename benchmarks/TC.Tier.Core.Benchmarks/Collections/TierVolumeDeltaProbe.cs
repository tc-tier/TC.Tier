using System.Diagnostics;
using System.IO.Hashing;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.TierVolume;

namespace TC.Tier.Core.Benchmarks.Collections;

/// <summary>
/// TierVolume 增量导出验收探针（V2 §1.2——journal delta 帧 = 操作级增量）。
/// 模式：--tier-volume-delta-probe [baseMB] [churnMB] [rewritePercent]
/// 测量（判定门裁决点）：
///   ① delta 体积 vs 文件级 diff（判定门 1：记录+数据块体积 ∝ 变更量 vs 数据面扫描）；
///   ② 导出/还原时长 vs 目标卷重写（判定门 2：记录重放 CPU 成本 vs 数据面拷贝 IO）；
///   ③ 检查点截断窗口行为（基点过旧拒导——增量链纪律）；
///   ④ 还原等价（逐文件内容 CRC 对账）。
/// 流程：基线 → 检查点 → dd 副本（全量基座）→ 源卷演进 → 导出 → 关源 → 副本还原 → 对账。
/// </summary>
internal static class TierVolumeDeltaProbe
{
    public static int Run(string[] args)
    {
        var baseMb = args.Length > 1 && long.TryParse(args[1], out var b) ? b : 64;
        var churnMb = args.Length > 2 && long.TryParse(args[2], out var c) ? c : 32;
        var rewritePct = args.Length > 3 && int.TryParse(args[3], out var p) ? p : 50;
        var dir = Path.Combine(Path.GetTempPath(), "tier-delta-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var srcPath = Path.Combine(dir, "src.tier");
        var replicaPath = Path.Combine(dir, "replica.tier");
        try
        {
            var rnd = new Random(7);
            var seed = new byte[1 << 20];

            // ① 基线 + 检查点（基点 = CkptLsn）
            var fs = TierVolumeFs.New(TierVolumeCarrier.File(srcPath), new TierVolumeFormatOptions
            { QuotaBytes = 512L << 20, JournalReserveBytes = 32L << 20 });
            var files = 8;
            var fileBytes = (baseMb << 20) / files;
            for (var i = 0; i < files; i++)
            {
                using var h = fs.Open($"base{i}", new FileOpenOptions
                { Access = AccessMode.ReadWrite, Mode = FileOpenMode.CreateNew });
                for (long off = 0; off < fileBytes; off += seed.Length)
                {
                    rnd.NextBytes(seed);
                    h.Write(off, seed);
                }
            }
            fs.FlushRoot();
            var baseLsn = fs.JournalCheckpointLsn;
            fs.Dispose();
            File.Copy(srcPath, replicaPath);   // dd 全量基座（副本保卷身份）

            // ② 源卷演进（覆写 + 新写 + 删除——混合变更形态）
            var deltaPath = Path.Combine(dir, "delta.tcd");
            string sourceFp;
            using (var src = TierVolumeFs.Open(TierVolumeCarrier.File(srcPath)))
            {
                var buf = new byte[64 * 1024];
                rnd.NextBytes(buf);
                var sw = Stopwatch.StartNew();
                long churned = 0;
                for (var i = 0; i < files; i++)
                {
                    using var h = src.Open($"base{i}", new FileOpenOptions
                    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenExisting });
                    for (long off = 0; off < fileBytes * rewritePct / 100; off += buf.Length)
                    {
                        h.Write(off, buf);
                        churned += buf.Length;
                    }
                }
                for (var i = 0; i < 4; i++)
                {
                    using var h = src.Open($"new{i}", new FileOpenOptions
                    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.CreateNew });
                    h.Write(0, buf);
                    churned += buf.Length;
                }
                src.Delete("base0");
                sw.Stop();
                Console.WriteLine($"演进：覆写 {churned >> 20}MB + 新写/删除，{sw.Elapsed.TotalSeconds:F2}s");

                // ③ 导出（判定门 1：delta 体积 ∝ 变更量）
                sw.Restart();
                using (var delta = new FileStream(deltaPath, FileMode.Create, FileAccess.Write))
                {
                    var summary = src.ExportDelta(delta, baseLsn);
                    sw.Stop();
                    var deltaBytes = new FileInfo(deltaPath).Length;
                    Console.WriteLine($"导出：{summary.RecordCount} 条记录 + 数据块，{deltaBytes >> 20}MB / {sw.Elapsed.TotalMilliseconds:F0}ms" +
                        $"（变更量 {churned >> 20}MB + 元数据——体积比 {deltaBytes * 100 / Math.Max(1, churned)}%）");
                    Console.WriteLine($"检查点截断窗口：CkptLsn={src.JournalCheckpointLsn}（= 基点? {src.JournalCheckpointLsn == baseLsn}——期间无检查点则增量窗口保持）");
                }
                sourceFp = Fingerprint(src);
            }

            // ④ 还原 + 对账（判定门 2：重放 vs 重写）
            using (var replica = TierVolumeFs.Open(TierVolumeCarrier.File(replicaPath)))
            using (var delta = new FileStream(deltaPath, FileMode.Open, FileAccess.Read))
            {
                var sw = Stopwatch.StartNew();
                var applied = replica.ApplyDelta(delta);
                sw.Stop();
                var equal = Fingerprint(replica) == sourceFp;
                Console.WriteLine($"还原：{applied.RecordCount} 条记录重放 + 数据落位，{sw.Elapsed.TotalMilliseconds:F0}ms；等价对账：{(equal ? "通过（逐文件内容 CRC 一致）" : "失败")}");
            }

            // ⑤ 目标卷重写对照（判定门 2 的另一极：数据面拷贝 IO——dd 全量副本耗时代理）
            var swCopy = Stopwatch.StartNew();
            var copyTarget = Path.Combine(dir, "dd-copy.tier");
            File.Copy(srcPath, copyTarget, overwrite: true);
            swCopy.Stop();
            Console.WriteLine($"对照（dd 全卷副本 = 数据面拷贝 IO）：{swCopy.Elapsed.TotalMilliseconds:F0}ms（载体 {new FileInfo(srcPath).Length >> 20}MB）");
            return 0;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static string Fingerprint(TierVolumeFs fs)
    {
        var parts = new List<string>();
        foreach (var e in fs.EnumerateEntries(recursive: true).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            using var h = fs.Open(e.Name, new FileOpenOptions
            { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting });
            var crc = new Crc32();
            var buf = new byte[64 * 1024];
            for (long off = 0; off < h.Length; off += buf.Length)
            {
                var n = h.Read(off, buf);
                if (n <= 0) break;
                crc.Append(buf.AsSpan(0, n));
            }
            parts.Add($"{e.Name}:{h.Length}:{crc.GetCurrentHashAsUInt32():X8}");
        }
        return string.Join("|", parts);
    }
}
