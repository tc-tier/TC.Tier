using System.Collections.Concurrent;
using System.Diagnostics;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Tests.Storage;

public sealed class CasDiagnosticTests : IDisposable
{
    private readonly List<TestVolume> _vols = new();

    public void Dispose()
    {
        foreach (var vol in _vols) vol.Dispose();
    }

    private TestVolume NewVol()
    {
        var vol = new TestVolume();
        _vols.Add(vol);
        return vol;
    }

    private static byte[] P(int len) => Enumerable.Range(0, len).Select(i => (byte)(i & 0xFF)).ToArray();

    [Fact]
    public void Cas_SingleThread_ExactByteCount()
    {
        var vol = NewVol();

        using var dev = new StorageEngineOptions("test", segmentGrowthLimit: 10 * 1024 * 1024, enableSegmentation: false).WithPreallocateFile(false).Builder(vol.Fs).Start();
        dev.WaitForReady();

        for (int i = 0; i < 1000; i++)
            dev.Append(P(64));

        dev.AllocatedTail.Offset.Should().Be(64_000);
    }

    [Fact]
    public void Cas_TwoThreads_NoLoss()
    {
        var vol = NewVol();

        using var dev = new StorageEngineOptions("test", segmentGrowthLimit: 10 * 1024 * 1024, enableSegmentation: false).WithPreallocateFile(false).Builder(vol.Fs).Start();
        dev.WaitForReady();

        Parallel.For(0, 2, t =>
        {
            for (int i = 0; i < 500; i++)
                dev.Append(P(64));
        });

        dev.AllocatedTail.Offset.Should().Be(64_000);
    }

    [Fact]
    public void Cas_Stress_Diagnostic()
    {
        var vol = NewVol();

        using var dev = new StorageEngineOptions("test", segmentGrowthLimit: 10 * 1024 * 1024, enableSegmentation: false).WithPreallocateFile(false).Builder(vol.Fs).Start();
        dev.WaitForReady();

        int threadCount = 4;
        int writesPerThread = 100;
        long writeSize = 64;
        long expectedTotal = threadCount * writesPerThread * writeSize;

        int casFailures = 0;
        int totalAttempts = 0;
        var errorOffsets = new ConcurrentBag<long>();

        Parallel.For(0, threadCount, t =>
        {
            for (int i = 0; i < writesPerThread; i++)
            {
                var addr = dev.Append(P((int)writeSize));
                long expectedOffset = addr.Offset % writeSize;
                if (expectedOffset != 0)
                {
                    Interlocked.Increment(ref casFailures);
                    errorOffsets.Add(addr.Offset);
                }
                Interlocked.Increment(ref totalAttempts);
            }
        });

        long actual = dev.AllocatedTail.Offset;
        long diff = expectedTotal - actual;

        Console.WriteLine($"Expected: {expectedTotal}, Actual: {actual}, Lost: {diff}");
        Console.WriteLine($"CAS failures (misaligned): {casFailures}/{totalAttempts}");
        Console.WriteLine($"Offsets with issues: {string.Join(", ", errorOffsets.Take(10))}");

        if (diff > 0)
        {
            // Try to find which offset ranges are missing
            var allReserved = new HashSet<long>();
            for (long off = 0; off < actual; off += writeSize)
                allReserved.Add(off);

            var expected = new HashSet<long>();
            for (long off = 0; off < expectedTotal; off += writeSize)
                expected.Add(off);

            expected.ExceptWith(allReserved);
            Console.WriteLine($"Missing offsets (first 20): {string.Join(", ", expected.Take(20).Select(o => $"{o}"))}");
        }

        diff.Should().Be(0, $"should be no lost bytes, lost {diff} ({diff / writeSize} writes)");
    }
}
