using BenchmarkDotNet.Running;
using TC.Tier.Core.Benchmarks.Collections;

namespace TC.Tier.Core.Benchmarks;

public class Program
{
    public static int Main(string[] args)
    {
        // 独立压测探针（非 BDN）——BDN Parallel 组高度不确定的场景用独立计时程序
        if (args.Length > 0 && args[0] == "--pq-probe")
            return PriorityQueueStressProbe.Run(args);
        if (args.Length > 0 && args[0] == "--pq-wedge")
            return PqWedgeRepro.Run(args);
        if (args.Length > 0 && args[0] == "--sync-probe")
            return Primitives.SyncModesProbe.Run(args);
        if (args.Length > 0 && args[0] == "--tier-volume-page-cache-probe")
            return TierVolumePageCacheProbe.Run(args);
        if (args.Length > 0 && args[0] == "--tier-volume-write-probe")
            return TierVolumeWriteProbe.Run(args);
        if (args.Length > 0 && args[0] == "--tier-volume-snapshot-probe")
            return TierVolumeSnapshotProbe.Run(args);
        if (args.Length > 0 && args[0] == "--tier-volume-delta-probe")
            return TierVolumeDeltaProbe.Run(args);
        if (args.Length > 0 && args[0] == "--disk-syscall-probe")
            return DiskSyscallProbe.Run(args);
        if (args.Length > 0 && args[0] == "--s3-protocol-probe")
            return S3ProtocolProbe.Run(args);
        if (args.Length > 0 && args[0] == "--core-repro-probe")
            return CoreReproProbe.Run(args);
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return 0;
    }
}
