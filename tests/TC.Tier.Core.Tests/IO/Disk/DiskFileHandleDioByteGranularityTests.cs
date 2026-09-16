using System.Buffers;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;
using TC.Tier.Core.NativeInterop;
using TC.Tier.Core.Primitives;
using Xunit;
using Skip = Xunit.Skip;

namespace TC.Tier.Core.Tests.IO.Disk;

/// <summary>
/// DIO 句柄字节粒度读写矩阵——DIO 语义单一缓存形态（形态由能力探测决定，无逐 IO 换道）：
/// <see cref="DiskFileHandle.Write"/>/<see cref="DiskFileHandle.WriteAsync"/> 非对齐 = 边缘扇区 RMW +
/// 中段对齐直写；<see cref="DiskFileHandle.Read"/>/<see cref="DiskFileHandle.ReadAsync"/> 非对齐 =
/// 对齐块拷出。对齐快路径与原直写等价。介质不支持 DIO 时探针回落缓冲形态——字节语义断言两态皆绿
/// （Supported 介质上额外实战 RMW 路径）。
/// <para>★ gate：卷在 <b>open 阶段即拒</b> O_DIRECT 时（GH arm runner EINVAL 实锤）无法进入
/// 任何形态——整类 skip（探测回落仅覆盖"open 成功但探测 Unsupported"路径；"open 即拒"的
/// 回落语义是产品兼容性台账项）。</para>
/// </summary>
public sealed class DiskFileHandleDioByteGranularityTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-dio-bg");
    private readonly DiskFileSystem _fs;

    public DiskFileHandleDioByteGranularityTests()
    {
        _fs = DiskFileSystem.OpenOrCreate(_dir);
        _fs.EnsureRoot();
    }

    public void Dispose()
    {
        _fs.Dispose();
        TestTempDir.TryCleanup(_dir);
    }

    private static FileOpenOptions Opts(FileOpenHints hints = FileOpenHints.None) => new()
    {
        Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite, Hints = hints
    };

    /// <summary>DIO 句柄（NoBuffering 请求——探测 Supported 则实战 RMW，否则语义等价缓冲路径）。</summary>
    private IFileHandle OpenDio(string name = "dio")
    {
        Skip.IfNot(DiskMediumGate.DirectIo, "卷不支持 O_DIRECT（open 即拒）——DIO 字节粒度矩阵跳过。");
        var h = _fs.Open(name, Opts(FileOpenHints.NoBuffering));
        h.UnbufferedSupport.Should().NotBe(UnbufferedIoSupport.NotRequested, "已请求 NoBuffering——探测必出结论");
        return h;
    }

    /// <summary>缓冲句柄（验证面——页缓存直读，不经被测写路径）。</summary>
    private IFileHandle OpenBuffered(string name = "dio") => _fs.Open(name, Opts());

    /// <summary>0xFF 掩底 + 块界可辨识pattern：字节值 = (index * 7 + 3) 截断。</summary>
    private static byte Pat(int i) => (byte)(i * 7 + 3);

    private void SeedBuffered(IFileHandle h, int length)
    {
        var seed = new byte[length];
        for (var i = 0; i < length; i++) seed[i] = Pat(i);
        h.Write(0, seed);
    }

    /// <summary>经独立缓冲句柄全文件读回并与期望逐字节比对（期望 = 掩底 + 写入补丁的合成）。</summary>
    private void VerifyContent(string name, byte[] expected)
    {
        using var v = OpenBuffered(name);
        v.Length.Should().Be(expected.Length, "文件长度精确（写语义 EOF / 打洞大小不变）");
        var actual = new byte[expected.Length];
        v.Read(0, actual).Should().Be(expected.Length);
        actual.Should().Equal(expected, "全文件逐字节一致");
    }

    /// <summary>期望数组构造：掩底基础上打补丁。</summary>
    private static byte[] Expected(int fileLength, params (int Off, byte[] Data)[] patches)
    {
        var e = new byte[fileLength];
        for (var i = 0; i < fileLength; i++) e[i] = Pat(i);
        foreach (var (off, data) in patches) data.CopyTo(e, off);
        return e;
    }

    // ══════════════════ 写矩阵（同步） ══════════════════

    [SkippableFact]
    public void Write_SubSector_InBlock_RmwPreservesNeighbors()
    {
        using var h = OpenDio();
        SeedBuffered(h, 8192);
        var patch = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
        h.Write(3, patch);   // 块内非对齐（offset 3, len 5——单块 RMW）
        VerifyContent("dio", Expected(8192, (3, patch)));
    }

    [SkippableFact]
    public void Write_CrossBlock_HeadMiddleTail()
    {
        using var h = OpenDio();
        SeedBuffered(h, 16384);
        var patch = new byte[300];
        for (var i = 0; i < patch.Length; i++) patch[i] = 0x50;
        h.Write(4000, patch);   // 头边缘 96B + 中段 4B + 尾边缘 200B（4096 块界）
        VerifyContent("dio", Expected(16384, (4000, patch)));
    }

    [SkippableFact]
    public void Write_Unaligned_ExtendEof_PreciseLength()
    {
        using var h = OpenDio();
        SeedBuffered(h, 100);
        var patch = new byte[300];
        for (var i = 0; i < patch.Length; i++) patch[i] = 0x77;
        h.Write(50, patch);   // [50, 350) 越 EOF——RMW 块写撑界后必须复原精确长度
        h.Length.Should().Be(350, "写语义 EOF = 写尾（不得停在块界）");
        VerifyContent("dio", Expected(350, (50, patch)));
    }

    [SkippableFact]
    public void Write_AlignedFastPath_Direct()
    {
        using var h = OpenDio();
        SeedBuffered(h, 16384);
        using var amm = new AlignedMemoryManager(4096);
        for (var i = 0; i < 4096; i++) amm.GetSpan()[i] = 0x11;
        h.Write(4096, amm.GetSpan());   // 三重对齐——直写快路径（对齐源零拷贝）
        VerifyContent("dio", Expected(16384, (4096, amm.GetSpan().ToArray())));
    }

    // ══════════════════ 写矩阵（异步） ══════════════════

    [SkippableFact]
    public async Task WriteAsync_SubSector_InBlock()
    {
        using var h = OpenDio("dio-a");
        SeedBuffered(h, 8192);
        var patch = new byte[] { 0x0F, 0x1E, 0x2D };
        await h.WriteAsync(5, patch, CancellationToken.None);
        VerifyContent("dio-a", Expected(8192, (5, patch)));
    }

    [SkippableFact]
    public async Task WriteAsync_CrossBlock_BothEdges()
    {
        using var h = OpenDio("dio-b");
        SeedBuffered(h, 16384);
        var patch = new byte[5000];
        for (var i = 0; i < patch.Length; i++) patch[i] = 0x60;
        await h.WriteAsync(8100, patch, CancellationToken.None);   // 跨 3 块（8100..13099），双边缘 + 对齐中段
        VerifyContent("dio-b", Expected(16384, (8100, patch)));
    }

    [SkippableFact]
    public async Task WriteAsync_Unaligned_ExtendEof()
    {
        using var h = OpenDio("dio-c");
        SeedBuffered(h, 100);
        var patch = new byte[777];
        for (var i = 0; i < patch.Length; i++) patch[i] = 0x88;
        await h.WriteAsync(30, patch, CancellationToken.None);
        h.Length.Should().Be(807);
        VerifyContent("dio-c", Expected(807, (30, patch)));
    }

    // ══════════════════ 读矩阵 ══════════════════

    [SkippableFact]
    public void Read_Unaligned_CopyOut_ExactBytes()
    {
        using var h = OpenDio();
        SeedBuffered(h, 16384);
        var buf = new byte[300];
        var got = h.Read(4003, buf);   // 非对齐 offset/len——对齐块拷出
        got.Should().Be(300);
        var expected = new byte[300];
        for (var i = 0; i < 300; i++) expected[i] = Pat(4003 + i);
        buf.Should().Equal(expected);
    }

    [SkippableFact]
    public void Read_AcrossEof_ShortRead()
    {
        using var h = OpenDio();
        SeedBuffered(h, 100);
        var buf = new byte[500];
        var got = h.Read(60, buf);   // 越界读——EOF 收敛短读
        got.Should().Be(40, "EOF 短读 = 实际可得字节数");
        var expected = new byte[40];
        for (var i = 0; i < 40; i++) expected[i] = Pat(60 + i);
        buf.AsSpan(0, 40).ToArray().Should().Equal(expected);
    }

    [SkippableFact]
    public void Read_BeyondEof_ZeroBytes()
    {
        using var h = OpenDio();
        SeedBuffered(h, 100);
        h.Read(100, new byte[10]).Should().Be(0, "越 EOF 读 = 0");
        h.Read(500, new byte[10]).Should().Be(0);
    }

    [SkippableFact]
    public async Task ReadAsync_Unaligned_CopyOut()
    {
        using var h = OpenDio();
        SeedBuffered(h, 16384);
        var buf = new byte[1000];
        var got = await h.ReadAsync(8000, buf, CancellationToken.None);
        got.Should().Be(1000);
        var expected = new byte[1000];
        for (var i = 0; i < 1000; i++) expected[i] = Pat(8000 + i);
        buf.Should().Equal(expected);
    }

    // ══════════════════ 打洞边缘（WriteZeroes 统一 DioWrite） ══════════════════

    [SkippableFact]
    public void PunchHole_Edges_OnDioHandle_ZeroesAndKeepsSize()
    {
        using var h = OpenDio();
        SeedBuffered(h, 16384);
        h.PunchHole(4000, 300);   // 头/尾边缘零写（非对齐）+ 对齐中段 FSCTL
        var expected = new byte[16384];
        for (var i = 0; i < 16384; i++) expected[i] = i is >= 4000 and < 4300 ? (byte)0 : Pat(i);
        VerifyContent("dio", expected);
    }

    // ══════════════════ 生命周期 ══════════════════

    [SkippableFact]
    public void Write_PooledUnalignedBuffers_RepeatedRounds_Consistent()
    {
        using var h = OpenDio();
        SeedBuffered(h, 32768);
        // 多轮池化非对齐缓冲写（ArrayPool 地址漂移——RMW 每轮独立正确）；各轮区间重叠——期望累积打补丁
        var expected = new byte[32768];
        for (var i = 0; i < 32768; i++) expected[i] = Pat(i);
        for (var round = 0; round < 8; round++)
        {
            var buf = ArrayPool<byte>.Shared.Rent(100);
            try
            {
                for (var i = 0; i < 100; i++) buf[i] = (byte)(round + 1);
                h.Write(4090 + round * 3, buf.AsSpan(0, 100));
                for (var i = 0; i < 100; i++) expected[4090 + round * 3 + i] = (byte)(round + 1);
                VerifyContent("dio", expected);
            }
            finally { ArrayPool<byte>.Shared.Return(buf); }
        }
    }
}
