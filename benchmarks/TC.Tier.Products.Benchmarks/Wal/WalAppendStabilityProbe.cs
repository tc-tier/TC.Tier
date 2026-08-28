using TC.Tier.Core.IO;
using TC.Tier.Core.IO.TierVolume;
using TC.Tier.Runtime.Benchmarks.Storage;

namespace TC.Tier.Products.Benchmarks.Wal;

/// <summary>
/// §4 稳定性矩阵：三介质 × 两提交形态（设计稿 §4——验证口径 = 稳定性 p99/p999 抖动有界，
/// 不只平均吞吐；raft 对"偶发慢"比"平均慢"更敏感）。
/// <para>组提交形态：AppendBatchAsync 批 100（模拟 AppendEntries 攒批，三维度禁用 = 纯缓冲）——
///   采样每批延迟。</para>
/// <para>单条提交形态：三维度全 0（每次 Append 即触发提交 = 每写 fsync）——采样每条延迟；
///   磁盘单条格默认 5000 条（fsync 主导，秒级）。</para>
/// <para>spec=all 跑三介质（默认 IO 模式）；spec=io-matrix 跑 local × 四 IO 模式
///   （buffered/WriteThrough/DIO/DIO+WriteThrough）× 两形态——磁盘 IO 模式矩阵。</para>
/// <para>★ 第 5 参 = 载体写穿档（IS-03，virtual 专属——TierVolume 载体句柄级写穿，journal 提交
///   免独立 fsync；local/memory 无载体概念自动忽略）。</para>
/// </summary>
public static class WalAppendStabilityProbe
{
    public static async Task Run(string spec = "all", int groupBatches = 1000, int singleCount = 5000,
        string hints = "none", bool carrierWriteThrough = false)
    {
        if (spec == "io-matrix")
        {
            WalProbeCommon.PrintHeader($"§4 稳定性矩阵：local 磁盘 × 四 IO 模式 × 两提交形态（组提交 {groupBatches:N0} 批 × 100 条；单条 {singleCount:N0} 条）");
            foreach (var (h, hName) in WalProbeCommon.IoModes)
            {
                Console.WriteLine($"════════ IO 模式 {hName} ════════");
                await RunMedia("local", groupBatches, singleCount, h, carrierWriteThrough: false).ConfigureAwait(false);
            }
            Console.WriteLine();
            Console.WriteLine("矩阵完成——判定口径：p99/p999 抖动有界（p99/p50 比值越小越稳；raft 对偶发慢敏感）");
            return;
        }

        WalProbeCommon.PrintHeader($"§4 稳定性矩阵：介质 × 两提交形态（组提交 {groupBatches:N0} 批 × 100 条；单条 {singleCount:N0} 条，IO 模式 {WalProbeCommon.HintsName(WalProbeCommon.HintsOf(hints))}" +
            (carrierWriteThrough ? "，载体写穿档" : "") + "）");
        if (!carrierWriteThrough && spec is "virtual")
            Console.WriteLine("★ 提示：本表 = 句柄级 hints（RM-07 逐写 journal 提交——buffered 载体每写仍付 fsync）。" +
                "virtual 的「写透免 fsync」（IS-03）= 挂载级 CarrierWriteThrough 旋钮——第 5 参传 true（如 --wal-append-probe virtual 1000 5000 none true）。");
        await RunMedia(spec, groupBatches, singleCount, WalProbeCommon.HintsOf(hints), carrierWriteThrough).ConfigureAwait(false);
        Console.WriteLine();
        Console.WriteLine("矩阵完成——判定口径：p99/p999 抖动有界（p99/p50 比值越小越稳；raft 对偶发慢敏感）");
    }

    private static async Task RunMedia(string spec, int groupBatches, int singleCount, FileOpenHints hints,
        bool carrierWriteThrough)
    {
        var media = spec == "all"
            ? WalProbeCommon.MatrixMedia
            : [(WalProbeCommon.SpecOf(spec), WalProbeCommon.SpecOf(spec))];

        foreach (var (name, specStr) in media)
        {
            Console.WriteLine($"──────── {name} ────────");
            // ★ IS-03 载体写穿档（virtual 专属——TierVolume 载体挂载级；local/memory 无载体自动忽略）
            using var vol = carrierWriteThrough && specStr.StartsWith("virtual", StringComparison.Ordinal)
                ? new BenchVolume(specStr, new TierVolumeFormatOptions { CarrierWriteThrough = true })
                : new BenchVolume(specStr);

            // === 组提交形态（显式 CommitAsync 驱动）===
            await using (var wal = await WalProbeCommon.StartAsync(vol.Fs, WalProbeCommon.GroupCommit().WithHints(hints)))
            {
                var lat = new double[groupBatches];
                const int batchSize = 100;
                var bytes = 0L;
                for (var w = 0; w < 3; w++)   // 预热：排除首次 Append 的引擎冷路径
                {
                    var wb = new ReadOnlyMemory<byte>[batchSize];
                    for (var j = 0; j < batchSize; j++) wb[j] = WalProbeCommon.Entry(j + 1);
                    await wal.AppendBatchAsync(wb, default).ConfigureAwait(false);
                }
                for (var i = 0; i < groupBatches; i++)
                {
                    var batch = new ReadOnlyMemory<byte>[batchSize];
                    for (var j = 0; j < batchSize; j++) batch[j] = WalProbeCommon.Entry(i * batchSize + j + 1);
                    var sw = Stopwatch.StartNew();
                    await wal.AppendBatchAsync(batch, default).ConfigureAwait(false);
                    lat[i] = sw.Elapsed.TotalMilliseconds;
                    bytes += batchSize * 64L;
                }
                await wal.CommitAsync(default).ConfigureAwait(false);
                var elapsed = lat.Sum() / 1000.0;
                Array.Sort(lat);
                var p = WalProbeCommon.Percentiles(lat);
                Console.WriteLine($"  组提交（批 100）：{WalProbeCommon.Format(p)} | 吞吐 {groupBatches * batchSize / elapsed:N0} 条/s " +
                    $"（{bytes / 1024.0 / 1024.0 / elapsed:F1} MB/s），抖动 p99/p50={p.P99 / Math.Max(p.P50, 1e-9):F1}×");
            }

            // === 单条提交形态（每写即提交 = 每写 fsync）===
            await using (var wal = await WalProbeCommon.StartAsync(vol.Fs, WalProbeCommon.SingleForce().WithHints(hints)))
            {
                for (var w = 0; w < 3; w++)   // 预热
                    await wal.AppendSingleAsync(WalProbeCommon.Entry(w + 1), default).ConfigureAwait(false);
                var lat = new double[singleCount];
                for (var i = 0; i < singleCount; i++)
                {
                    var sw = Stopwatch.StartNew();
                    await wal.AppendSingleAsync(WalProbeCommon.Entry(i + 1), default).ConfigureAwait(false);
                    lat[i] = sw.Elapsed.TotalMilliseconds;
                }
                var elapsed = lat.Sum() / 1000.0;
                Array.Sort(lat);
                var p = WalProbeCommon.Percentiles(lat);
                Console.WriteLine($"  单条提交（每写 fsync）：{WalProbeCommon.Format(p)} | 吞吐 {singleCount / elapsed:N0} 条/s，" +
                    $"抖动 p99/p50={p.P99 / Math.Max(p.P50, 1e-9):F1}×");
            }
        }
    }
}
