using System.Diagnostics;
using TC.Tier.Runtime.Structures.Ring.Contracts;

namespace TC.Tier.Runtime.Benchmarks.Kv;

/// <summary>
/// ★ W1.1 溢出写基准探针（docs/design/ring-overflow-chunked-write-design.md §8——先测后改强制前置）。
/// <para>矩阵：值 128KB/1MB/16MB/128MB/1GB × 并发 1/8/32；mem / local 双介质。</para>
/// <para>口径两路：<b>e2e</b> = ring.Write 整值溢出写吞吐（用户可见指标，改造前后对照主口径）；
///   <b>stages</b> = 现写路径同构分解（rent/copy/crc/write/return 五段占比——定位成本结构），
///   探针自持 PinnedBufferPool + StorageEngine（与 Ring 内部池/溢出引擎同形同介质）。</para>
/// <para>内存维度：报告每写者峰值帧租赁（2 的幂取整）——W1 目标即消除 ≤2×N 整值帧。</para>
/// <para>运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks --
///   --overflow-write-probe [mem|local]</para>
/// </summary>
public static class OverflowWriteProbe
{
    private static readonly (string Name, int Bytes)[] ValueTiers =
    {
        ("128KB", 1 << 17), ("1MB", 1 << 20), ("16MB", 1 << 24), ("128MB", 1 << 27), ("1GB", 1 << 30),
    };

    private static readonly int[] ConcurrencyTiers = { 1, 8, 32 };

    /// <summary>单组合总写入上限（mem 介质数据驻留 + 本地盘占用都有界；超限组合显式跳过不静默）。</summary>
    private const long ComboWriteCap = 1L << 30;

    /// <summary>迭代数选择目标：小值档凑 ~256MB 写入量保证计时稳定，大值档落 1。</summary>
    private const long TargetBytesPerCombo = 256L << 20;

    private const int SectorAssumed = 4096;   // 探针自持池的对齐口径（mem 介质扇区值不影响成本结构）

    public static void Run(string medium = "mem")
    {
        Console.WriteLine($"[probe] W1.1 溢出写基准 medium={medium} " +
                          $"值档={string.Join('/', ValueTiers.Select(t => t.Name))} 并发={string.Join('/', ConcurrencyTiers)}");
        string? rootDir = null;
        if (medium == "local")
        {
            rootDir = Path.GetFullPath($"test_out/ovprobe-{Guid.NewGuid():N}");
            Directory.CreateDirectory(rootDir);
        }

        foreach (var (tierName, bytes) in ValueTiers)
        {
            foreach (var conc in ConcurrencyTiers)
            {
                int iters = (int)Math.Max(1, Math.Min(1024, TargetBytesPerCombo / ((long)bytes * conc)));
                long totalBytes = (long)bytes * conc * iters;
                if (totalBytes > ComboWriteCap)
                {
                    Console.WriteLine($"[skip ] tier={tierName,-6} conc={conc,2}  写入量 {totalBytes / (1 << 20)}MB > 上限 {ComboWriteCap >> 20}MB（内存有界纪律）");
                    continue;
                }
                try
                {
                    RunCombo(medium, rootDir, tierName, bytes, conc, iters);
                }
                catch (AggregateException ae)
                {
                    // 基线取证：整值租赁形态的崩溃档位也是数据点（如 1GB 帧取整 2GB 溢出 int32）
                    Console.WriteLine($"[crash] tier={tierName,-6} conc={conc,2}  {ae.GetBaseException().Message}");
                }
            }
        }

        if (rootDir is not null) Directory.Delete(rootDir, recursive: true);
        Console.WriteLine("[probe] 完成");
    }

    private static void RunCombo(string medium, string? rootDir, string tierName, int valueBytes, int conc, int iters)
    {
        var value = new byte[valueBytes];
        new Random(42).NextBytes(value);

        // ════ ① e2e：ring.Write 整值溢出写（真路径） ════
        double e2eSecs;
        int prodFrameBytes, oldShapeRoundedBytes;
        {
            using var fs = NewFs(medium, rootDir, "e2e");
            var settings = new BlittableRingSettings(
                new StorageEngineOptions("ovring", 4L << 30, enableSegmentation: true, preallocateFile: false, deleteOnClose: true))
            {
                PageSize = 8192,
                MemorySize = 64L << 20,
                OverflowPolicy = OverflowPolicy.Enabled,
                MinOverflowSize = 64,
            };
            using var ring = new RingOfLong(settings, fs);
            ring.Initialize();
            ring.WaitForReady();

            // 分块后生产路径瞬时帧缓冲：小帧 = 取整帧长；大帧 = 恒定 chunk。旧形态取整值另列对照。
            int frameLen = 18 + ((valueBytes + 3) & ~3);
            oldShapeRoundedBytes = RoundedSize(frameLen);
            prodFrameBytes = frameLen <= 256 * 1024 ? oldShapeRoundedBytes : 256 * 1024;
            var sw = Stopwatch.StartNew();
            var workers = Enumerable.Range(0, conc).Select(w => Task.Run(() =>
            {
                long keyBase = (long)w * iters;
                for (int i = 0; i < iters; i++)
                    ring.Write(keyBase + i, value);
            })).ToArray();
            Task.WaitAll(workers);
            sw.Stop();
            e2eSecs = sw.Elapsed.TotalSeconds;
        }
        Console.WriteLine(
            $"[probe] tier={tierName,-6} conc={conc,2} iters={iters,4} | " +
            $"e2e {totalComboBytes(valueBytes, conc, iters) / 1e9:F2}GB in {e2eSecs:F2}s → {totalComboBytes(valueBytes, conc, iters) / e2eSecs / (1 << 20):N0} MB/s | " +
            $"瞬时帧缓冲/写者 {prodFrameBytes >> 10}KB（旧形态取整 {oldShapeRoundedBytes >> 20}MB）");
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // ════ ② stages：写路径同构分解（探针自持池 + 溢出引擎，单写者消除调度噪音） ════
        double rentMs, copyMs, crcMs, writeMs, retMs;
        {
            using var fs = NewFs(medium, rootDir, "stages");
            using var pool = new PinnedBufferPool();
            var ovOpts = new StorageEngineOptions("ovprobe.ov", 4L << 30, enableSegmentation: true,
                preallocateFile: false, deleteOnClose: true);
            using var ov = new StorageEngine(fs, ovOpts);
            ov.Initialize();
            ov.WaitForReady();

            int paddedLen = (valueBytes + 3) & ~3;
            int padLen = paddedLen - valueBytes;
            int frameLen = 18 + paddedLen;

            rentMs = copyMs = crcMs = writeMs = retMs = 0;
            var sw = new Stopwatch();
            try
            {
                // 预热 2 次（池建桶/引擎首段）
                for (int warm = 0; warm < 2; warm++) StageOnce(pool, ov, value, paddedLen, padLen, frameLen, sw, ref rentMs, ref copyMs, ref crcMs, ref writeMs, ref retMs);
                rentMs = copyMs = crcMs = writeMs = retMs = 0;
                for (int i = 0; i < iters; i++)
                    StageOnce(pool, ov, value, paddedLen, padLen, frameLen, sw, ref rentMs, ref copyMs, ref crcMs, ref writeMs, ref retMs);
            }
            catch (ArgumentOutOfRangeException)
            {
                // 整值租赁形态的上限取证：帧取整超 int32（如 1GB 值 → 2GB 帧）——旧形态在此档位不可运行
                Console.WriteLine($"[stages] tier={tierName,-6} conc={conc,2}  整值租赁副本崩溃（帧取整溢出 int32——旧形态硬上限）；e2e 行仍为生产路径实测");
                return;
            }
        }

        double stageTotal = rentMs + copyMs + crcMs + writeMs + retMs;
        static double Pct(double part, double total) => total > 0 ? 100 * part / total : 0;
        Console.WriteLine(
            $"[stages] tier={tierName,-6} conc={conc,2} iters={iters,4} | " +
            $"rent {rentMs / iters:F3}ms({Pct(rentMs, stageTotal):F0}%) copy {copyMs / iters:F3}ms({Pct(copyMs, stageTotal):F0}%) " +
            $"crc {crcMs / iters:F3}ms({Pct(crcMs, stageTotal):F0}%) write {writeMs / iters:F3}ms({Pct(writeMs, stageTotal):F0}%) ret {retMs / iters:F3}ms({Pct(retMs, stageTotal):F0}%)");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static long totalComboBytes(int valueBytes, int conc, int iters) => (long)valueBytes * conc * iters;

    /// <summary>现 WriteOverflow 同构单步（Rent→填充→CRC→Allocate+Write→Return），分段累计。</summary>
    private static void StageOnce(PinnedBufferPool pool, StorageEngine ov, byte[] value,
        int paddedLen, int padLen, int frameLen, Stopwatch sw,
        ref double rentMs, ref double copyMs, ref double crcMs, ref double writeMs, ref double retMs)
    {
        sw.Restart();
        var frameMem = pool.RentAligned(frameLen, SectorAssumed);
        rentMs += sw.Elapsed.TotalMilliseconds;

        var frame = frameMem.GetSpan(0, frameLen);
        sw.Restart();
        value.CopyTo(frame.Slice(18, value.Length));
        if (padLen > 0) frame.Slice(18 + value.Length, padLen).Clear();
        copyMs += sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        uint crc = UnifiedCrc.ComputeCrc32C(frame[..14]);
        crc = UnifiedCrc.ComputeCrc32C(crc, frame.Slice(18, paddedLen));
        UnifiedCrc.ComputeCrc32C(crc, stackalloc byte[4]);   // 保持 crc 消费形态（防消除）
        crcMs += sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var adr = ov.Allocate(frameLen).Start;
        ov.Write(adr, frame[..frameLen]);
        writeMs += sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        pool.ReturnAligned(frameMem);
        retMs += sw.Elapsed.TotalMilliseconds;
    }

    private static IFileSystem NewFs(string medium, string? rootDir, string tag)
        => medium == "mem"
            ? TierFs.New("memory:")
            : TierFs.New($"local:///{Path.Combine(rootDir!, tag)}");

    /// <summary>2 的幂取整（与 PinnedBufferPool.RentAligned 同口径）——内存峰值维度报告用。</summary>
    private static int RoundedSize(int size)
    {
        if (size <= 1) return 1;
        if ((size & (size - 1)) == 0) return size;
        return 1 << (32 - System.Numerics.BitOperations.LeadingZeroCount((uint)size));
    }
}
