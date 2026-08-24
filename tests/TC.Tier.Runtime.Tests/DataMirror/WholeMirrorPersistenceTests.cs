using TC.Tier.Runtime.DataMirror;
using TC.Tier.Runtime.Storage;
using TC.Tier.Runtime.Structures.Mirror;
using TC.Tier.Contracts.Structures;

namespace TC.Tier.Runtime.Tests.DataMirror;

/// <summary>
/// WholeMirrorPersistence 契约测试——1:1 于 src/.../DataMirror/WholeMirrorPersistence.cs
/// （+ 宿主 WholeMirror 流式写会话）。
/// <para>★ 契约面：三段式写读往返 / 多代最新 / 写尾前 Abort → 只见上一完整像（原子性）/
///   账面三态等价 / 损坏自校 false / 传输上限超限抛 / 相位违约抛 / 大像流式（增量预留+修齐后走链）。</para>
/// <para>设计稿：docs/design/V2/mirror-persistence-interface-design.md §7。</para>
/// </summary>
public class WholeMirrorPersistenceTests : IDisposable
{
    readonly TestVolume _vol;
    bool _disposed;

    public WholeMirrorPersistenceTests() { _vol = new TestVolume(); }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
        _vol.Dispose();
    }

    private WholeMirrorSettings Opts(string name)
        => new(new StorageEngineOptions(name, 1L << 24, enableSegmentation: false)
            .WithDeleteOnClose(false));

    /// <summary>开写会话（断言成功 + 非空收紧）。</summary>
    private static ITransferWriter OpenWriter(ITransferPersistence bridge, int max = ITransferPersistence.DefaultMaxTransferBytes)
    {
        bridge.TryOpenWrite(out var w,max).Should().BeTrue();
        return w!;
    }

    /// <summary>开读会话（断言成功 + 非空收紧）。</summary>
    private static ITransferReader OpenReader(ITransferPersistence bridge, int max = ITransferPersistence.DefaultMaxTransferBytes)
    {
        bridge.TryOpenRead(out var r, max).Should().BeTrue();
        return r!;
    }

    /// <summary>写一代完整像（三段式：头 16B / 体 / 尾 32B——消费方格式形状）。</summary>
    private static void WriteImage(ITransferPersistence bridge, byte fill, int bodySize, int chunkSize = 64 * 1024)
    {
        using var w = OpenWriter(bridge);
        var header = new byte[16];
        header.AsSpan().Fill(fill);
        w.WriteHeader(header);

        var chunk = new byte[Math.Min(chunkSize, bodySize)];
        long written = 0;
        while (written < bodySize)
        {
            int n = (int)Math.Min(chunk.Length, bodySize - written);
            chunk.AsSpan(0, n).Fill(fill);
            w.WritePayload(chunk.AsSpan(0, n));
            written += n;
        }

        var footer = new byte[32];
        footer.AsSpan().Fill(fill);
        w.WriteFooter(footer);
    }

    /// <summary>读回当前像（三段式相位；体长由消费方自定界——返回头/体/尾拼接字节）。</summary>
    private static byte[] ReadImage(ITransferPersistence bridge, int bodySize)
    {
        using var r = OpenReader(bridge);
        var header = new byte[16];
        r.ReadHeader(header).Should().Be(16);

        var acc = new MemoryStream();
        var buf = new byte[64 * 1024];
        long remaining = bodySize;
        while (remaining > 0)
        {
            int n = r.ReadPayload(buf.AsSpan(0, (int)Math.Min(buf.Length, remaining)));
            n.Should().BePositive("体未读尽（消费方按自己格式定界）");
            acc.Write(buf, 0, n);
            remaining -= n;
        }

        var footer = new byte[32];
        r.ReadFooter(footer).Should().Be(32);
        return [.. header, .. acc.ToArray(), .. footer];
    }

    [Fact]
    public void WriteRead_RoundTrip_ThreePhase()
    {
        using var bridge = new WholeMirrorPersistence(_vol.Fs, Opts("br-rt"));
        var body = new byte[10_000];
        new Random(3).NextBytes(body);

        using (var w = OpenWriter(bridge))
        {
            w.WriteHeader(new byte[16]);
            w.WritePayload(body);
            w.WriteFooter(new byte[32]);
        }

        var image = ReadImage(bridge, 10_000);
        image.Length.Should().Be(16 + 10_000 + 32);
        image[16..(16 + 10_000)].Should().Equal(body);
    }

    [Fact]
    public void MultipleGenerations_LatestVisible_AfterReopen()
    {
        const string name = "br-gen";
        using (var bridge = new WholeMirrorPersistence(_vol.Fs, Opts(name)))
        {
            WriteImage(bridge, 1, 1000);
            WriteImage(bridge, 2, 2000);
            WriteImage(bridge, 3, 3000);
        }

        using (var bridge = new WholeMirrorPersistence(_vol.Fs, Opts(name)))
        {
            var image = ReadImage(bridge, 3000);
            image.Length.Should().Be(16 + 3000 + 32, "重开恢复走链取最新代");
            image.Should().OnlyContain(b => b == 3);
        }
    }

    [Fact]
    public void AbortBeforeFooter_PreviousImageVisible_Atomicity()
    {
        using var bridge = new WholeMirrorPersistence(_vol.Fs, Opts("br-abort"));
        WriteImage(bridge, 1, 512);

        // 第 2 代写头+半截体后弃置（未写尾 = Abort）
        using (var w = OpenWriter(bridge))
        {
            w.WriteHeader(new byte[16]);
            w.WritePayload(new byte[256]);
        }   // Dispose 未写尾 → Abort

        var image = ReadImage(bridge, 512);
        image.Length.Should().Be(16 + 512 + 32, "写尾前弃置 → 只见上一完整像");
        image.Should().OnlyContain(b => b == 1);
    }

    [Fact]
    public void CompleteFalse_AbortsSession_PreviousImageVisible()
    {
        using var bridge = new WholeMirrorPersistence(_vol.Fs, Opts("br-cfalse"));
        WriteImage(bridge, 1, 512);

        // 第 2 代写头+半截体后主动 Complete(false)（= Abort：尾截断回退，非静默置相位）
        using (var w = OpenWriter(bridge))
        {
            w.WriteHeader(new byte[16]);
            w.WritePayload(new byte[256]);
            w.Complete(isSuccess: false);
        }

        var image = ReadImage(bridge, 512);
        image.Length.Should().Be(16 + 512 + 32, "Complete(false) = Abort → 只见上一完整像");
        image.Should().OnlyContain(b => b == 1);
    }

    [Fact]
    public void CompleteTrue_BeforeFooter_Throws_PhaseViolation()
    {
        using var bridge = new WholeMirrorPersistence(_vol.Fs, Opts("br-ctrue"));
        using (var w = OpenWriter(bridge))
        {
            w.WriteHeader(new byte[16]);
            var act = () => w.Complete(isSuccess: true);
            act.Should().Throw<InvalidOperationException>().WithMessage("*未写尾*",
                "WriteFooter 才是原子提交点——未写尾 Complete(true) = 把未完成洗成完成");
        }
    }

    [Fact]
    public void NoImage_TryOpenReadFalse_ThreeStateEquivalent()
    {
        using var bridge = new WholeMirrorPersistence(_vol.Fs, Opts("br-empty"));
        bridge.TryOpenRead(out var r).Should().BeFalse("无像 = fail-safe 回退全量重放");
        r.Should().BeNull();
    }

    [Fact]
    public void CorruptPayload_BookkeepingCrcFails_False()
    {
        const string name = "br-corrupt";
        using (var bridge = new WholeMirrorPersistence(_vol.Fs, Opts(name)))
        {
            WriteImage(bridge, 0xAB, 4096);
        }

        // 裸引擎覆写载荷区（record 头 ≤64B——偏移 200 必在载荷内）——镜像自身 CRC 必失配
        using (var engine = new StorageEngineOptions(name, 1L << 24, enableSegmentation: false)
                       .WithDeleteOnClose(false).Builder(_vol.Fs).Start())
        {
            engine.WaitForReady();
            engine.Write(engine.CalculationAddress(engine.MinAddress, 200), new byte[16]);
        }

        using var bridge2 = new WholeMirrorPersistence(_vol.Fs, Opts(name));
        bridge2.TryOpenRead(out _).Should().BeFalse("账面 CRC 失配 → false（内容有效性归消费方，此处桥接器账面即拒）");
    }

    [Fact]
    public void MaxTransfer_Exceeded_Throws()
    {
        using var bridge = new WholeMirrorPersistence(_vol.Fs, Opts("br-max"));
        WriteImage(bridge, 7, 256);

        using (var w = OpenWriter(bridge, 128))
        {
            var act = () => w.WriteHeader(new byte[129]);
            act.Should().Throw<ArgumentOutOfRangeException>("超限 = 契约违约 fail-fast");
            w.WriteHeader(new byte[16]);
            var act2 = () => w.WritePayload(new byte[200]);
            act2.Should().Throw<ArgumentOutOfRangeException>();
            w.WritePayload(new byte[64]);
            w.WriteFooter(new byte[16]);
        }

        using (var r = OpenReader(bridge, 128))
        {
            var act = () => r.ReadHeader(new byte[256]);
            act.Should().Throw<ArgumentOutOfRangeException>("读侧同受传输上限约束");
        }
    }

    [Fact]
    public void ConcurrentSecondWriter_TryOpenWriteFalse_SingleWriter()
    {
        using var bridge = new WholeMirrorPersistence(_vol.Fs, Opts("br-single"));
        using (var w1 = OpenWriter(bridge))
        {
            bridge.TryOpenWrite(out var w2).Should().BeFalse("并发双写者 → false（非抛）");
            w2.Should().BeNull();

            w1.WriteHeader(new byte[16]);
            w1.WritePayload(new byte[32]);
            w1.WriteFooter(new byte[16]);
        }

        using (var w3 = OpenWriter(bridge))
        {
            w3.WriteHeader(new byte[16]);
            w3.WritePayload(new byte[32]);
            w3.WriteFooter(new byte[16]);
        }
    }

    [Fact]
    public void PhaseViolations_Throw()
    {
        using var bridge = new WholeMirrorPersistence(_vol.Fs, Opts("br-phase"));
        WriteImage(bridge, 5, 128);

        using (var w = OpenWriter(bridge))
        {
            var writeBeforeHeader = () => w.WritePayload(new byte[8]);
            writeBeforeHeader.Should().Throw<InvalidOperationException>("数据相位必须先写头");

            w.WriteHeader(new byte[16]);
            var headerTwice = () => w.WriteHeader(new byte[16]);
            headerTwice.Should().Throw<InvalidOperationException>("头相位不可重复");

            w.WritePayload(new byte[8]);
            w.WriteFooter(new byte[8]);
            var afterFooter = () => w.WritePayload(new byte[8]);
            afterFooter.Should().Throw<InvalidOperationException>("写尾后会话完成");
        }

        using (var r = OpenReader(bridge))
        {
            var readBeforeHeader = () => r.ReadPayload(new byte[8]);
            readBeforeHeader.Should().Throw<InvalidOperationException>("读数据相位必须先读头");

            r.ReadHeader(new byte[8]).Should().Be(8);
            var headerTwice = () => r.ReadHeader(new byte[8]);
            headerTwice.Should().Throw<InvalidOperationException>("头相位不可重复");

            r.ReadFooter(new byte[8]).Should().Be(8);
            var afterFooter = () => r.ReadPayload(new byte[8]);
            afterFooter.Should().Throw<InvalidOperationException>("读尾后会话完成");
        }
    }

    [Fact]
    public void LargeImage_StreamingReserveAndTrim_ChainStaysWalkable()
    {
        // 3MB 像（> 1MB 流式预留步长 ×3——增量预留 + 收官修齐路径），64K 分段写
        const int bodySize = 3 * 1024 * 1024;
        using var bridge = new WholeMirrorPersistence(_vol.Fs, Opts("br-large"));

        WriteImage(bridge, 0x11, bodySize, chunkSize: 64 * 1024);
        var image = ReadImage(bridge, bodySize);
        image.Length.Should().Be(16 + bodySize + 32);
        image.Should().OnlyContain(b => b == 0x11);

        // 大像（含预留 slack 修齐）之后再写一代——证明下一 record 紧贴、恢复走链不被间隔打断
        WriteImage(bridge, 0x22, 512);
        ReadImage(bridge, 512).Should().OnlyContain(b => b == 0x22);
    }
}
