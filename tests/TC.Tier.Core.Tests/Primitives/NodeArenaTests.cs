using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Tests.Primitives;

/// <summary>
/// NodeArena（分块 CAS-bump 节点竞技场）契约测试——1:1 于 src/TC.Tier.Core/Primitives/NodeArena.cs。
/// <para>★ 契约面：8 对齐/指针恒稳（跨块增长不搬移）/块跨越/超块直通/并发 Alloc 无重叠/Dispose 全释放幂等。</para>
/// </summary>
public unsafe class NodeArenaTests
{
    [Fact]
    public void Alloc_ReturnsDistinctAlignedPointers()
    {
        using var arena = new NodeArena();
        var seen = new HashSet<nint>();

        for (int i = 1; i <= 1000; i++)
        {
            var p = arena.Alloc(i);
            ((nint)p & 7).Should().Be(0, "8 对齐契约");
            seen.Add((nint)p).Should().BeTrue("分配不得重叠");
            Unsafe.Write(p, i);   // 写入即占位——后续分配覆盖它会在稳定性测试露馅
        }
    }

    [Fact]
    public void Pointers_StableAcrossChunkGrowth()
    {
        using var arena = new NodeArena();
        var ptrs = new List<nint>();
        var values = new List<int>();

        for (int i = 0; i < 200; i++)
        {
            var p = arena.Alloc(32 * 1024);   // 200×32KB = 6.4MB——跨越 4MB 块
            Unsafe.Write(p, i);
            ptrs.Add((nint)p);
            values.Add(i);
        }

        arena.ChunkCount.Should().BeGreaterThanOrEqualTo(2, "6.4MB 必然跨块");
        for (int i = 0; i < ptrs.Count; i++)
            Unsafe.Read<int>((byte*)ptrs[i]).Should().Be(values[i], "块增长不搬移已分配指针（恒稳契约）");
    }

    [Fact]
    public void Oversized_AllocatesDedicatedBlock()
    {
        using var arena = new NodeArena();
        var big = arena.Alloc(NodeArena.ChunkSize + 4096);
        Unsafe.Write(big, 0x1234_5678);
        Unsafe.Read<int>(big).Should().Be(0x1234_5678);

        var small = arena.Alloc(64);   // 超块之后常规分配照常
        Unsafe.Write(small, 42);
        Unsafe.Read<int>(small).Should().Be(42);
    }

    [Fact]
    public void UsedBytes_TracksAllocations()
    {
        using var arena = new NodeArena();
        var before = arena.UsedBytes;
        arena.Alloc(100);   // 8 对齐 → 104
        (arena.UsedBytes - before).Should().Be(104);
    }

    [Fact]
    public void ConcurrentAlloc_NoOverlap_AllWritesSurvive()
    {
        using var arena = new NodeArena();
        const int Threads = 4, PerThread = 20_000, BlockSize = 64;
        var blocks = new byte*[Threads * PerThread];

        Parallel.For(0, Threads, t =>
        {
            for (int i = 0; i < PerThread; i++)
            {
                var idx = t * PerThread + i;
                var p = arena.Alloc(BlockSize);
                Unsafe.Write(p, idx);   // 写块专属标记——重叠会在终验互踩
                blocks[idx] = p;
            }
        });

        arena.UsedBytes.Should().BeGreaterThanOrEqualTo((long)Threads * PerThread * BlockSize);
        for (int i = 0; i < blocks.Length; i++)
            Unsafe.Read<int>(blocks[i]).Should().Be(i, "并发分配重叠=标记互踩（CAS-bump 契约破坏）");
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var arena = new NodeArena();
        arena.Alloc(128);
        arena.Dispose();
        arena.Dispose();

        Action act = () => _ = arena.Alloc(8);
        act.Should().Throw<ObjectDisposedException>();
    }
}
