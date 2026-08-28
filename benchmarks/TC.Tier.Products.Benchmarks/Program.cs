using BenchmarkDotNet.Running;
using TC.Tier.Products.Benchmarks.Wal;

namespace TC.Tier.Products.Benchmarks;

public class Program
{
    public static int Main(string[] args)
    {
        // ★ TierWAL 夹具基准路由（三份性能契约 + 稳定性矩阵——设计稿 docs/design/tierwal-design.md §3-§5）
        //   同二进制绕开 BDN harness；介质切换 = spec 参数（mem/local/virtual）；
        //   IO 模式 = hints 参数（none/wt/dio/dio+wt，meta probe 默认 all 四模式）。
        if (args.Length > 0 && args[0] == "--wal-meta-probe")
        {
            WalMetaLatencyProbe.Run(
                args.Length > 1 ? args[1] : "local",
                args.Length > 2 ? int.Parse(args[2]) : 1000,
                args.Length > 3 ? args[3] : "all",
                args.Length > 4 && bool.Parse(args[4])).GetAwaiter().GetResult();   // [4]=true 载体写穿档（IS-03）
            return 0;
        }
        if (args.Length > 0 && args[0] == "--wal-replay-probe")
        {
            WalReplayProbe.Run(
                args.Length > 1 ? args[1] : "memory",
                args.Length > 2 ? int.Parse(args[2]) : 500_000,
                args.Length > 3 ? int.Parse(args[3]) : 64,
                args.Length > 4 ? int.Parse(args[4]) : null,
                args.Length > 5 ? args[5] : "none").GetAwaiter().GetResult();
            return 0;
        }
        if (args.Length > 0 && args[0] == "--wal-catchup-probe")
        {
            WalCatchupProbe.Run(
                args.Length > 1 ? args[1] : "memory",
                args.Length > 2 ? int.Parse(args[2]) : 200_000,
                args.Length > 3 ? int.Parse(args[3]) : 20_000,
                args.Length > 4 ? int.Parse(args[4]) : 64,
                args.Length > 5 ? args[5] : "none").GetAwaiter().GetResult();
            return 0;
        }
        if (args.Length > 0 && args[0] == "--wal-append-probe")
        {
            WalAppendStabilityProbe.Run(
                args.Length > 1 ? args[1] : "all",
                args.Length > 2 ? int.Parse(args[2]) : 1000,
                args.Length > 3 ? int.Parse(args[3]) : 5000,
                args.Length > 4 ? args[4] : "none",
                args.Length > 5 && bool.Parse(args[5])).GetAwaiter().GetResult();   // [5]=true 载体写穿档（IS-03，virtual 专属）
            return 0;
        }
        if (args.Length > 0 && args[0] == "--wal-tier-volume-flush-probe")
        {
            WalTierVolumeFlushProbe.Run(
                args.Length > 1 ? args[1] : "virtual",
                args.Length > 2 ? args[2] : "overwrite",
                args.Length > 3 ? int.Parse(args[3]) : 2000);
            return 0;
        }
        if (args.Length > 0 && args[0] == "--wal-snapshot-store-probe")
        {
            // ★ 快照存储选型基准（镜像快照设计稿 §3.4——方案 A 段增量 vs B 全量重写）
            WalSnapshotStoreProbe.Run(
                args.Length > 1 ? args[1] : "virtual",
                args.Length > 2 ? int.Parse(args[2]) : 256,
                args.Length > 3 ? int.Parse(args[3]) : 4,
                args.Length > 4 ? int.Parse(args[4]) : 64,
                args.Length > 5 ? args[5] : "none").GetAwaiter().GetResult();
            return 0;
        }
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
