using BenchmarkDotNet.Running;
using TC.Tier.Runtime.Benchmarks.Kv;
using TC.Tier.Runtime.Benchmarks.Storage.AddressSpace;
using TC.Tier.Runtime.Benchmarks.Storage.Engine;

namespace TC.Tier.Runtime.Benchmarks;

public class Program
{
    public static int Main(string[] args)
    {
        // 稳定性诊断路由
        if (args.Length > 0 && args[0] == "--stability")
        {
            LeaseStabilityProbe.Run();
            return 0;
        }
        // ★ M5 高并发 Append 探针（no-op CAS 竞争验证）
        if (args.Length > 0 && args[0] == "--lease-append-probe")
            return LeaseAppendProbe.Run(args);
        // ★ 临时取证：Write_Memory 形态探针（同二进制绕开 BDN harness）
        if (args.Length > 0 && args[0] == "--wm-probe")
        {
            WmProbe.Run();
            return 0;
        }
        // ★ 临时取证：恢复重放/写路径托管分配分解探针
        if (args.Length > 0 && args[0] == "--replay-alloc-probe")
        {
            ReplayAllocProbe.Run().GetAwaiter().GetResult();
            return 0;
        }
        // ★ L19 销案性能论证：占用扫描协议成本曲线（十万级区间表二分 vs 全扫）
        if (args.Length > 0 && args[0] == "--scan-perf")
        {
            ExtentTableScanPerfProbe.Run();
            return 0;
        }
        // ★ 临时取证：镜像恢复阶段分解探针
        if (args.Length > 0 && args[0] == "--mirror-probe")
        {
            MirrorProbe.Run();
            return 0;
        }
        // ★ 临时取证：fsync 单点成本分解探针
        if (args.Length > 0 && args[0] == "--fs-flush-probe")
        {
            FsFlushProbe.Run(args.Length > 1 ? args[1] : "local");
            return 0;
        }
        // ★ Ring 并发写吞吐探针（多写者无锁窗口改造前后对照）
        if (args.Length > 0 && args[0] == "--concurrent-write-probe")
        {
            int writers = args.Length > 1 ? int.Parse(args[1]) : 8;
            int perWriter = args.Length > 2 ? int.Parse(args[2]) : 50_000;
            ConcurrentRingWriteProbe.Run(writers, perWriter);
            return 0;
        }
        // ★ Log 写/恢复吞吐探针（现行版 Log 独有压测报表）
        if (args.Length > 0 && args[0] == "--log-write-probe")
        {
            int count = args.Length > 1 ? int.Parse(args[1]) : 500_000;
            int entrySize = args.Length > 2 ? int.Parse(args[2]) : 64;
            int writers = args.Length > 3 ? int.Parse(args[3]) : 8;
            LogWriteProbe.Run(count, entrySize, writers);
            return 0;
        }
        // ★ Session 提交管线吞吐探针（v2 入档口径：回合/s 阈值 mem ≥30k @8 会话）
        if (args.Length > 0 && args[0] == "--session-pipeline-probe")
        {
            SessionPipelineProbe.Run(
                args.Length > 1 ? args[1] : "mem",
                args.Length > 2 ? int.Parse(args[2]) : 8,
                args.Length > 3 ? int.Parse(args[3]) : 2000);
            return 0;
        }
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
