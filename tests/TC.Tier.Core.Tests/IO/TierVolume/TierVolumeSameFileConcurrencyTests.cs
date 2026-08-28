using TC.Tier.Core.IO;
using TC.Tier.Core.IO.TierVolume;

namespace TC.Tier.Core.Tests.IO.TierVolume;

/// <summary>
/// §2.1 Parallel 协议——同文件并发写契约测试：
/// 不相交区间写完全并行（引擎模式 A：预分配+复写形态）；重叠写结构不损坏；
/// 并发结构变更（追加）经合并提交互不覆盖，崩溃重放全量在场。
/// </summary>
public sealed class TierVolumeSameFileConcurrencyTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-tv-samefile");
    private readonly string _volPath;

    public TierVolumeSameFileConcurrencyTests() => _volPath = Path.Combine(_dir, "v.tier");

    public void Dispose() => TestTempDir.TryCleanup(_dir);

    private TierVolumeFs Format()
        => TierVolumeFs.New(TierVolumeCarrier.File(_volPath), new TierVolumeFormatOptions
        {
            QuotaBytes = 64L << 20,
            WriteConcurrency = WriteConcurrencyMode.Parallel,   // §2.1：并发契约显式跑 Parallel 档（缺省 Serial 测不到新协议）
        });

    private static FileOpenOptions RWO() => new()
    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite };

    private static FileOpenOptions RO() => new()
    { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite };

    [Fact]
    public async Task SameFile_DisjointRegions_ConcurrentRewrite_AllDataIntact()
    {
        // 模式 A 稳态：预分配覆盖 + 各写者不相交区间反复覆写（纯命中写——无结构变更热路径）
        const int writers = 4;
        const int region = 256 * 1024;
        using var fs = Format();
        fs.CreateFile("shared", preallocateSize: (long)writers * region);
        using (var seed = fs.Open("shared", RWO()))
        {
            seed.Write(0, new byte[(long)writers * region]);   // 全覆盖首写（unwritten → Written——复写稳态）
            seed.Flush();
        }
        var payloads = new byte[writers][];
        for (var i = 0; i < writers; i++)
        {
            payloads[i] = new byte[region];
            new Random(31 + i).NextBytes(payloads[i]);
        }
        var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, writers).Select(i => Task.Run(() =>
        {
            using var h = fs.Open("shared", RWO());
            gate.Wait();
            for (var round = 0; round < 20; round++)
                h.Write((long)i * region, payloads[i]);   // 各自不相交区间并发覆写
        })).ToArray();
        gate.Set();
        await Task.WhenAll(tasks);

        using var vh = fs.Open("shared", RO());
        for (var i = 0; i < writers; i++)
        {
            var buf = new byte[region];
            vh.Read((long)i * region, buf).Should().Be(region);
            buf.Should().BeEquivalentTo(payloads[i], $"写者 {i} 数据完整（并发不相交区间写互不覆盖）");
        }
    }

    [Fact]
    public async Task SameFile_ConcurrentAppends_CrashReplay_AllCommittedPresent()
    {
        // 结构变更路径并发：多句柄原子追加（落点两两不交）+ 逐轮 Flush → 崩溃重放全量在场
        // （合并提交 + 并发 journal 记录 LSN 单调——有效前缀重放确定性）
        const int writers = 4;
        const int rounds = 15;
        const int chunk = 64 * 1024;
        using var fs = Format();
        var gate = new ManualResetEventSlim(false);
        var payloads = new byte[writers][];
        for (var i = 0; i < writers; i++)
        {
            payloads[i] = new byte[chunk];
            new Random(41 + i).NextBytes(payloads[i]);
        }
        var tasks = Enumerable.Range(0, writers).Select(i => Task.Run(() =>
        {
            using var h = fs.Open("shared", RWO());
            gate.Wait();
            for (var r = 0; r < rounds; r++)
            {
                h.Append(payloads[i]);   // AppendCursor 原子预留——并发落点两两不交（D10）
                h.Flush();
            }
        })).ToArray();
        gate.Set();
        await Task.WhenAll(tasks);
        var expected = (long)writers * rounds * chunk;
        fs.CrashSimulate();   // 崩溃（跳过 clean 关闭——journal 有效前缀重放）

        using var fs2 = TierVolumeFs.Open(TierVolumeCarrier.File(_volPath));
        using var h2 = fs2.Open("shared", RO());
        h2.Length.Should().Be(expected, "并发追加 + 逐轮 Flush 全部屏障——重放后长度完整");
        // 数据完整：每 64KB 块必属于某写者的负载（并发预留的落点顺序非确定——只断言内容集合与全覆盖）
        var probe = new byte[chunk];
        for (long off = 0; off < expected; off += chunk)
        {
            h2.Read(off, probe).Should().Be(chunk);
            payloads.Should().Contain(p => p.SequenceEqual(probe), "每块内容 ∈ 写者负载集合（无撕裂无空洞）");
        }
    }

    [Fact]
    public async Task SameFile_OverlappingWrites_StructureNotCorrupted()
    {
        // 重叠写（引擎 lease 契约之外）：数据内容未指定、结构不损坏——后续写读正确
        using var fs = Format();
        using (var h = fs.Open("f", RWO()))
        {
            h.Write(0, new byte[16 * 4096]);
            h.Flush();
        }
        var a = new byte[12 * 4096];
        var b = new byte[12 * 4096];
        new Random(51).NextBytes(a);
        new Random(52).NextBytes(b);
        var gate = new ManualResetEventSlim(false);
        var t1 = Task.Run(() => { using var h = fs.Open("f", RWO()); gate.Wait(); h.Write(0, a); });
        var t2 = Task.Run(() => { using var h = fs.Open("f", RWO()); gate.Wait(); h.Write(4 * 4096, b); });
        gate.Set();
        await Task.WhenAll(t1, t2);
        fs.CrashSimulate();

        using var fs2 = TierVolumeFs.Open(TierVolumeCarrier.File(_volPath));
        using var h2 = fs2.Open("f", RO());
        h2.Length.Should().Be(16 * 4096, "重叠并发写不破坏逻辑长度");
        var buf = new byte[4096];
        h2.Read(0, buf).Should().Be(4096);   // 可读（内容未指定——只断言结构完整）
        h2.Read(15 * 4096, buf).Should().Be(4096);
        // 后续写读正确（结构未被并发重叠损坏）
        using var h3 = fs2.Open("f", RWO());
        h3.Write(0, a);
        h3.Read(0, buf).Should().Be(4096);
    }
}
