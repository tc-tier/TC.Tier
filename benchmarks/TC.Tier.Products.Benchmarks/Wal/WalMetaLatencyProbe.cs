using System.Text;
using TC.Tier.Core.IO.TierVolume;
using TC.Tier.Runtime.Benchmarks.Storage;

namespace TC.Tier.Products.Benchmarks.Wal;

/// <summary>
/// 契约①：选举时间窗口 ↔ 元数据持久化（设计稿 §3）。
/// <para>raft 语义：投票/任期变更必须<b>持久化后才应答</b>——持久化延迟必须远小于选举超时随机窗口
///   （典型 150–300ms）。每次循环 = WriteMetaAsync + CommitAsync（opaque 搭水位线 fsync 落盘）→ 应答。</para>
/// <para>判定：p99.9 &lt; 150ms 窗口下界才算有余量（抖动不触发选举失败）。</para>
/// </summary>
public static class WalMetaLatencyProbe
{
    public static async Task Run(string spec = "local", int iterations = 1000, string hints = "all",
        bool carrierWriteThrough = false)
    {
        WalProbeCommon.PrintHeader($"契约① 选举窗口：元数据槽原子写延迟（{spec}，IO 模式 {hints}" +
            (carrierWriteThrough ? "，载体写穿档" : "") + "）");
        if (!carrierWriteThrough && spec is "virtual")
            Console.WriteLine("★ 提示：本表 IO 模式 = 句柄级 hints（RM-07 逐写 journal 提交——buffered 载体上每写仍付 fsync）。" +
                "virtual 的「写透免 fsync」（IS-03）= 挂载级 CarrierWriteThrough 旋钮——第 4 参传 true（如 --wal-meta-probe virtual 1000 all true）。");
        const double windowLowMs = 150.0;

        var modes = hints == "all" ? WalProbeCommon.IoModes : [(WalProbeCommon.HintsOf(hints), WalProbeCommon.HintsName(WalProbeCommon.HintsOf(hints)))];
        foreach (var (h, hName) in modes)
        {
            // IS-03：载体写穿档经 TierFs 合流挂载（TierVolumeFormatOptions.CarrierWriteThrough）
            using var vol = carrierWriteThrough
                ? new BenchVolume(WalProbeCommon.SpecOf(spec), new TierVolumeFormatOptions { CarrierWriteThrough = true })
                : new BenchVolume(WalProbeCommon.SpecOf(spec));
            var options = TierWalOptions.Default.WithHints(h);
            await using var wal = await WalProbeCommon.StartAsync(vol.Fs, options);
            Console.WriteLine($"── IO 模式 {hName}：迭代 {iterations:N0} 次（128B term/vote 元数据；WriteMetaAsync = stage + 一次 fsync 提交 = 应答持久化）");

            var lat = new double[iterations];
            var gen0 = GC.CollectionCount(0);

            // 预热：排除引擎冷启动一次性成本（首个 Commit 的初始化/文件创建路径）
            for (var i = 0; i < 3; i++)
                await wal.WriteMetaAsync(new byte[128], default).ConfigureAwait(false);

            for (var i = 0; i < iterations; i++)
            {
                var blob = new byte[128];
                BitConverter.TryWriteBytes(blob, (long)i);   // term 号低位
                var sw = Stopwatch.StartNew();
                await wal.WriteMetaAsync(blob, default).ConfigureAwait(false);   // 内部 = stage + 一次 fsync 提交
                lat[i] = sw.Elapsed.TotalMilliseconds;
            }

            Array.Sort(lat);
            var p = WalProbeCommon.Percentiles(lat);
            var ok = p.P999 < windowLowMs;
            Console.WriteLine($"  {WalProbeCommon.Format(p)} | GC Gen0 {GC.CollectionCount(0) - gen0}");
            Console.WriteLine($"  判定：p99.9={p.P999:F3}ms {(ok ? "<" : "≥")} {windowLowMs}ms 选举窗口下界 → {(ok ? "[达标]" : "[不达标]")}");
            Console.WriteLine();
        }
    }
}
