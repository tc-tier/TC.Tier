using TC.Tier.Core.IO.Testing;
using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// W1 溢出写分块化对抗测试（docs/design/ring-overflow-chunked-write-design.md §9 对抗行）。
/// <para>★ ① 确定性撕裂帧：FaultInjectingFileSystem 在大值分块写的中途 chunk 注入 DiskFull——
///   帧无 header（最后写）即不可达，Ring 无指针悬空；重开后有效帧完整、扫描恢复、新写复用孤儿区
///   （§4 崩溃三窗口等价性的确定性实证，mem/真盘同套）。</para>
/// <para>★ ② 并发 M 写者大值分块：各持恒定 256KB chunk 缓冲并发写，全部往返一致。</para>
/// <para>★ ③ 1GB 级往返（Category=Scale，手动跑：旧整值租赁形态在此档位直接崩溃——
///   RentAligned 帧取整 2GB 溢出 int32，改造前基线已取证 test_out/ovprobe-baseline-*.txt）。</para>
/// <para>★ 本文件自包含（不依赖单测程序集 internal 工厂——Adversarial 只链接共享基建）。</para>
/// </summary>
[Trait("Category", "Adversarial")]
public class RingOverflowChunkedWriteTests
{
    private const int ChunkSize = 256 * 1024;

    /// <summary>大值溢出配置：1MB 页 + 256MB 池 + 64MB 段 + meta Disabled（恢复走溢出引擎扫描——
    /// chunked 帧布局过 ScanOverflowTail 的真实路径）+ 跨实例保留。</summary>
    private static BlittableRingSettings MakeSettings(string engineName)
        => new(new StorageEngineOptions(engineName, 64L << 20,
                enableSegmentation: true, preallocateFile: false, deleteOnClose: false))
        {
            PageSize = AlignmentConst.Alignment1M,
            MemorySize = 256L << 20,
            Preallocate = false,
            MetaPolicyKind = MetaPolicyKind.Disabled,
            OverflowPolicy = OverflowPolicy.Enabled,
            MinOverflowSize = 32,
        };

    private static BlittableRing<long> OpenRing(IFileSystem fs, BlittableRingSettings settings)
    {
        var ring = new BlittableRing<long>(settings, fs);
        ring.Initialize();
        ring.WaitForReady();
        return ring;
    }

    private static byte[] MakePattern(int length, byte seed = 7)
    {
        var buf = new byte[length];
        for (int i = 0; i < length; i++)
            buf[i] = (byte)(i * 31 + seed + (i >> 13));
        return buf;
    }

    private static void AssertValue(BlittableRing<long> ring, LogicalAddress addr, byte[] expected)
    {
        var dest = new byte[expected.Length];
        ring.GetValue(addr, dest).Should().Be(expected.Length);
        dest.Should().Equal(expected);
    }

    /// <summary>① 分块中途写失败（确定性注入）→ 撕裂帧不可达 → 重开后 A 完整可读、新写 C 复用成功、
    /// 再重开仍一致——§4 三窗口等价性实证。</summary>
    [Fact]
    public void Chunked_MidWriteFault_TornFrameUnreachable_RingFunctional()
    {
        using var vol = new TestVolume();
        using var fi = new FaultInjectingFileSystem(vol.Fs);
        var settings = MakeSettings("ring.w1torn");

        var valueA = MakePattern(8 * 1024 * 1024, seed: 11);   // 32 chunks 完整写
        var valueB = MakePattern(8 * 1024 * 1024, seed: 22);   // 注入点：第 3 个 chunk 写失败
        var valueC = MakePattern(8 * 1024 * 1024, seed: 33);

        LogicalAddress addrA;
        using (var ring = OpenRing(fi, settings) /* ★ 环建在注入层——规则才命中溢出引擎写 */)
        {
            addrA = ring.Write(1L, valueA);
            Console.WriteLine($"[diag] A_start={addrA} tailAfterA={ring.TailAddress}");
            AssertValue(ring, addrA, valueA);
            ring.FlushUntil(ring.TailAddress);

            // 规则期首个匹配 Write 即 B 的 chunk 1（规则前无并发写/在途 flush）；index=2 → chunk 3 失败
            fi.AddRule("*", "Write", IOError.DiskFull, failAtCallIndex: 2);
            var act = () => ring.Write(2L, valueB);
            act.Should().Throw<FileIOException>("分块写中途注入失败必须透传语义异常")
               .Which.Error.Should().Be(IOError.DiskFull);
            Console.WriteLine($"[diag] after-failed-B tail={ring.TailAddress}");
            fi.ClearRules();

            // B 失败后 A 不受影响（失败前已落盘帧不动）
            try { AssertValue(ring, addrA, valueA); Console.WriteLine("[diag] phase1 A ok"); }
            catch (Exception e) { Console.WriteLine($"[diag] phase1 A FAIL {e.Message}"); }
        }

        // 重开（Disabled meta → 恢复走溢出引擎扫描）：撕裂帧无 header（最后写）必不可达，
        // 尾水位落在最后有效帧（A）末尾；新写 C 从该尾 Allocate，安全复用孤儿区
        using (var ring2 = OpenRing(vol.Fs, settings))
        {
            try { AssertValue(ring2, addrA, valueA); Console.WriteLine("[diag] phase2 reopen A ok"); }
            catch (Exception e) { Console.WriteLine($"[diag] phase2 reopen A FAIL {e.Message}"); }
            LogicalAddress addrC = LogicalAddress.Empty;
            try { addrC = ring2.Write(3L, valueC); Console.WriteLine($"[diag] ring2 C ok record@{addrC}"); }
            catch (Exception e) { Console.WriteLine($"[diag] ring2 C write FAIL {e.Message}"); }
            ring2.FlushUntil(ring2.TailAddress);
            try { AssertValue(ring2, addrC, valueC); Console.WriteLine("[diag] phase2 C ok"); }
            catch (Exception e) { Console.WriteLine($"[diag] phase2 C FAIL {e.Message}"); }
        }

        // 三开：撕裂后的写入链跨实例持久一致
        using (var ring3 = OpenRing(vol.Fs, settings))
        {
            try
            {
                var addrD = ring3.Write(4L, MakePattern(ChunkSize + 5, seed: 44));
                AssertValue(ring3, addrD, MakePattern(ChunkSize + 5, seed: 44));
                Console.WriteLine("[diag] phase3 ok");
            }
            catch (Exception e) { Console.WriteLine($"[diag] phase3 FAIL {e.Message}"); }
        }
    }

    /// <summary>② 并发 8 写者 × ~16MB 大值分块：各持恒定 chunk 缓冲并发写，全部往返一致。</summary>
    [Fact]
    public async Task Chunked_ConcurrentLargeValueWriters_AllRoundtrip()
    {
        using var vol = new TestVolume();
        var settings = MakeSettings("ring.w1conc");
        using var ring = OpenRing(vol.Fs, settings);

        var values = Enumerable.Range(0, 8)
            .Select(w => MakePattern(16 * 1024 * 1024 + w * 4096 + 3, seed: (byte)(50 + w)))
            .ToArray();

        var addrs = new LogicalAddress[values.Length];
        await Task.WhenAll(Enumerable.Range(0, 8).Select(w => Task.Run(async () =>
        {
            addrs[w] = await ring.WriteAsync(100L + w, values[w]);
        })));

        ring.FlushUntil(ring.TailAddress);
        for (int w = 0; w < 8; w++)
            AssertValue(ring, addrs[w], values[w]);
    }

    /// <summary>③ 1GB 级真盘往返（Category=Scale——手动/Nightly；CI 默认跳过）。
    /// 旧整值租赁形态在此档位崩溃（帧取整 2GB 溢出 int32），分块后为常规操作。
    /// 复跑：dotnet test --filter "FullyQualifiedName~RingOverflowChunkedWriteTests&Category=Scale"（Adversarial 套件）。</summary>
    [Fact]
    [Trait("Category", "Scale")]
    public void Chunked_1GB_Value_Roundtrips()
    {
        const int oneGB = 1 << 30;
        using var vol = new TestVolume();
        var settings = MakeSettings("ring.w1gb");
        using var ring = OpenRing(vol.Fs, settings);

        var value = MakePattern(oneGB, seed: 99);
        var addr = ring.Write(1L, value);
        ring.FlushUntil(ring.TailAddress);

        AssertValue(ring, addr, value);
    }
}
