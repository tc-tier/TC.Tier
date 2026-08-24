using System.Diagnostics;
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.IO;
using TC.Tier.Core.Logging;
using TC.Tier.Runtime.Storage;

namespace TC.Tier.Runtime.Benchmarks.Storage.Engine;

/// <summary>★ 临时取证探针：Write_Memory 形态在同二进制、绕开 BDN harness 下的表现（46 次迭代对齐 BDN 总量）。</summary>
public static class WmProbe
{
    public static void Run()
    {
        using var vol = new BenchVolume();
        var options = new StorageEngineOptions("mem", segmentGrowthLimit: 1048576).WithPreallocateFile(false);
        options = options.WithOptimization(options.Optimization with { SampleInterval = TimeSpan.FromHours(1) });
        using var mem = (StorageEngine)options.Builder(vol.Fs, logger: new NullLogger()).Start();


        const int Payload = 64, Region = 200, N = 10_000;
        var payload = new byte[Payload];
        for (int i = 0; i < Payload; i++) payload[i] = (byte)(i & 0xFF);
        for (int i = 0; i < Region; i++) mem.Append(payload);
        var writeBase = mem.Allocate((long)Region * Payload).Start;

        int readIdx = 0;
        for (int inv = 0; inv < 46; inv++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < N; i++)
            {
                readIdx = (readIdx + 1) % Region;
                mem.Write(mem.CalculationAddress(writeBase, (long)readIdx * Payload), payload);
            }
            sw.Stop();
            Console.WriteLine($"inv {inv,2}: {sw.Elapsed.TotalMicroseconds / N,8:F1} us/op");
        }
    }
}
