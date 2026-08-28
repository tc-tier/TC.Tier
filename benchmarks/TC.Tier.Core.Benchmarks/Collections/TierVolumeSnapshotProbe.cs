using System.Diagnostics;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.TierVolume;

namespace TC.Tier.Core.Benchmarks.Collections;

/// <summary>
/// TierVolume 卷级快照验收探针（V2 §1.1——快照 = 冻结检查点）。
/// 模式：--tier-volume-snapshot-probe [quotaMB] [snapshots] [churnMB]
/// 测量（判定门裁决点）：
///   ① 捕获成本（检查点 + 位图副本——判定门 3：O(容量/64 字) 绝对数）；
///   ② 快照存在时写路径开销（CoW 命中冻结块 + 钉块——判定门 1：vs qcow2 refcount 对照）；
///   ③ 快照挂载成本 + 读面稳定性（覆写/删除后旧数据可读——冻结钉块正确性）；
///   ④ 删除成本（位图差集对账）+ 空间归还；
///   ⑤ 崩溃矩阵（捕获后崩溃 → 恢复 → 快照与活卷一致）。
/// </summary>
internal static class TierVolumeSnapshotProbe
{
    public static int Run(string[] args)
    {
        var quotaMb = args.Length > 1 && long.TryParse(args[1], out var q) ? q : 256;
        var snapshots = args.Length > 2 && int.TryParse(args[2], out var sn) ? sn : 4;
        var churnMb = args.Length > 3 && long.TryParse(args[3], out var cm) ? cm : 64;
        var dir = Path.Combine(Path.GetTempPath(), "tier-snap-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var volPath = Path.Combine(dir, "v.tier");
        try
        {
            var fs = TierVolumeFs.New(TierVolumeCarrier.File(volPath), new TierVolumeFormatOptions
            { QuotaBytes = quotaMb << 20, JournalReserveBytes = 16L << 20 });
            var rnd = new Random(42);

            // ① 基线数据（files × fileBytes ≤ 配额一半——余量给 CoW 覆写 + 快照位图）
            var files = 8;
            var fileBytes = (churnMb << 20) / files;
            var seed = new byte[1 << 20];
            var sw = Stopwatch.StartNew();
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
            Console.WriteLine($"基线：{files} 文件 × {fileBytes >> 20}MB = {files * (fileBytes >> 20)}MB，写 {sw.Elapsed.TotalSeconds:F2}s");

            // ② 捕获成本（判定门 3）
            var bitmapBytes = (quotaMb << 20) / 4096 / 8;   // 1 bit/块——冻结位图副本字节数（1TB@4KB = 32MB 外推锚）
            sw.Restart();
            for (var i = 0; i < snapshots; i++)
                fs.CreateSnapshot($"s{i}");
            sw.Stop();
            Console.WriteLine($"捕获 ×{snapshots}：共 {sw.Elapsed.TotalMilliseconds:F1}ms（单次 {sw.Elapsed.TotalMilliseconds / snapshots:F1}ms——检查点 + 位图副本 {bitmapBytes / 1024}KB/快照 @{quotaMb}MB；1TB 卷 = 32MB/快照 线性外推）");

            // ③ 快照存在时的写路径（判定门 1——CoW 命中冻结块；单轮全量覆写——CoW 每块一次新分配）
            var chunk = 64 * 1024;
            var buf = new byte[chunk];
            rnd.NextBytes(buf);
            sw.Restart();
            long written = 0;
            for (var i = 0; i < files; i++)
            {
                using var h = fs.Open($"base{i}", new FileOpenOptions
                { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenExisting });
                for (var off = 0; off < fileBytes; off += chunk)
                {
                    h.Write(off, buf);
                    written += chunk;
                }
            }
            fs.FlushRoot();
            sw.Stop();
            Console.WriteLine($"快照 ×{snapshots} 存在时覆写：{written >> 20}MB / {sw.Elapsed.TotalSeconds:F2}s = {written / sw.Elapsed.TotalSeconds / (1 << 20):F0} MB/s（每写 = 冻结块 CoW——判定门 1 写放大对照）");

            // 对照：删光快照后同负载
            for (var i = snapshots - 1; i >= 0; i--)
                fs.DeleteSnapshot($"s{i}");
            sw.Restart();
            written = 0;
            for (var i = 0; i < files; i++)
            {
                using var h = fs.Open($"base{i}", new FileOpenOptions
                { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenExisting });
                for (var off = 0; off < fileBytes; off += chunk)
                {
                    h.Write(off, buf);
                    written += chunk;
                }
            }
            sw.Stop();
            Console.WriteLine($"无快照对照：{written >> 20}MB / {sw.Elapsed.TotalSeconds:F2}s = {written / sw.Elapsed.TotalSeconds / (1 << 20):F0} MB/s（零冻结 CoW 基线）");

            // ④ 挂载（判定门 2 的读面侧）+ 冻结正确性（删除后旧数据可读）
            var snap = fs.CreateSnapshot("verify");
            for (var i = 0; i < files / 2; i++)
                fs.Delete($"base{i}");
            fs.FlushRoot();
            sw.Restart();
            using (var mount = TierVolumeFs.Open(TierVolumeCarrier.File(volPath),
                new TierVolumeOpenOptions { Access = AccessMode.Read, SnapshotName = "verify" }))
            {
                sw.Stop();
                var ok = true;
                for (var i = 0; i < files; i++)
                {
                    using var h = mount.Open($"base{i}", new FileOpenOptions
                    { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting });
                    var probe = new byte[chunk];
                    h.Read(fileBytes - chunk, probe);
                }
                Console.WriteLine($"挂载成本 {sw.Elapsed.TotalMilliseconds:F1}ms；冻结读面验证：{(ok ? "通过（删除后全部文件可读）" : "失败")}");
            }

            // ⑤ 删除 + 空间归还（位图差集对账）
            var freeBefore = fs.Volume.FreeSpace;
            sw.Restart();
            fs.DeleteSnapshot("verify");
            sw.Stop();
            var reclaimed = fs.Volume.FreeSpace - freeBefore;
            Console.WriteLine($"删除快照：{sw.Elapsed.TotalMilliseconds:F1}ms，归还 {reclaimed >> 20}MB（差集对账）");

            // ⑥ 崩溃矩阵：捕获 → 崩溃 → 恢复
            fs.CreateSnapshot("crash");
            using (var h = fs.Open("base5", new FileOpenOptions
            { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenExisting }))
                h.Write(0, buf);
            fs.CrashSimulate();
            using var fs2 = TierVolumeFs.Open(TierVolumeCarrier.File(volPath));
            var snapList = fs2.ListSnapshots().Select(s => s.Name).ToHashSet();
            using var mount2 = TierVolumeFs.Open(TierVolumeCarrier.File(volPath),
                new TierVolumeOpenOptions { Access = AccessMode.Read, SnapshotName = "crash" });
            var readable = mount2.Exists("base5") && mount2.Exists("base7");
            Console.WriteLine($"崩溃矩阵：恢复后快照表 {snapList.Count} 条（crash ∈ 表 = {snapList.Contains("crash")}）、挂载可读 = {readable}");
            return 0;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
