using System.Buffers.Binary;
using TC.Tier.Runtime.Storage.Compact;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// Compact marker 多文件形态测试（#297 S1：单 lease 单文件）——引擎级：真实 marker 落盘于
/// 引擎子目录（{engine}/{engine}.compact.marker.{seq}，与生产写入同路径契约）+ 恢复逐文件消化。
/// <para>覆盖：完整残留补执行后删除、残缺 marker 丢弃、多残留依序消化、残留不被新 Compact
/// 覆盖（S1 核心语义——旧单文件形态会覆盖丢失）。</para>
/// <para>★ 路径契约：marker 必须落在引擎子目录（DeviceName 前缀）——写到根空间的杂散文件
/// 不被引擎枚举（曾致本测试首版假阳性：写错位置后断言恒真）。</para>
/// </summary>
public class CompactMarkerMultiFileTests : IDisposable
{
    private readonly List<TestVolume> _vols = new();

    public void Dispose()
    {
        foreach (var vol in _vols) vol.Dispose();
        GC.SuppressFinalize(this);
    }

    private TestVolume NewVol()
    {
        var vol = new TestVolume();
        _vols.Add(vol);
        return vol;
    }

    private static StorageEngine NewDevice(TestVolume vol, string name)
    {
        var options = new StorageEngineOptions(name, segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false);
        var dev = (StorageEngine)options.Builder(vol.Fs).Start();
        dev.WaitForReady();
        return dev;
    }

    /// <summary>引擎子目录下的 marker 路径（与 DefaultCompactor.MarkerPathOf 同契约）。</summary>
    private static string MarkerPath(string engineName, int seq)
        => $"{engineName}/{engineName}.compact.marker.{seq}";

    [Fact]
    public void Recovery_MultipleResidualMarkers_AllConsumedAndDeleted()
    {
        // S1 核心：崩溃残留多份 marker（模拟多 lease 写入后各自崩溃）——恢复逐文件消化并全删
        var vol = NewVol();
        var dev = NewDevice(vol, "cmf-a");

        // 构造 3 份完整 marker（Range 型 = 纯 promote + meta 恢复路径，对不存在段幂等 no-op）
        WriteMarkerFile(vol, "cmf-a", 0, CompactType.Range, [1001, 1002]);
        WriteMarkerFile(vol, "cmf-a", 1, CompactType.Range, [1003]);
        WriteMarkerFile(vol, "cmf-a", 2, CompactType.Range, [1004, 1005]);
        foreach (var seq in new[] { 0, 1, 2 })
            vol.Fs.Exists(MarkerPath("cmf-a", seq)).Should().BeTrue($"前置：marker.{seq} 已落引擎子目录");

        dev.Dispose();

        // 重开——恢复枚举 3 份并逐文件消化
        var dev2 = NewDevice(vol, "cmf-a");
        using (dev2)
        {
            foreach (var seq in new[] { 0, 1, 2 })
                vol.Fs.Exists(MarkerPath("cmf-a", seq)).Should().BeFalse($"marker.{seq} 恢复后必须删除");
        }
    }

    [Fact]
    public void Recovery_CorruptMarkerAmongValid_Dropped_ValidOnesConsumed()
    {
        var vol = NewVol();
        var dev = NewDevice(vol, "cmf-b");

        WriteMarkerFile(vol, "cmf-b", 0, CompactType.Range, [2001]);
        WriteCorruptMarkerFile(vol, "cmf-b", 1);   // 残缺：CRC 破坏
        WriteMarkerFile(vol, "cmf-b", 2, CompactType.Range, [2002]);

        dev.Dispose();

        var dev2 = NewDevice(vol, "cmf-b");
        using (dev2)
        {
            foreach (var seq in new[] { 0, 1, 2 })
                vol.Fs.Exists(MarkerPath("cmf-b", seq)).Should().BeFalse(
                    $"seq={seq}：完整文件消化删除、残缺文件丢弃删除——全部清场");
        }
    }

    [Fact]
    public void StartCompact_WithResidualMarker_Rejected_ResidualUntouched()
    {
        // ★ EnsureNoPending 语义（引擎级证明）：pending marker 存在时新 Compact 被拒绝
        //   （残留必须先重启恢复消化再发起）——且拒绝路径不清残留（marker 原样保留，
        //   供下次重启恢复消化）。旧单文件形态下此处新写入会覆盖残留 → 处置条目丢失。
        var vol = NewVol();
        var dev = NewDevice(vol, "cmf-c");

        WriteMarkerFile(vol, "cmf-c", 0, CompactType.Range, [3001]);
        var contentBefore = ReadAllBytes(vol, MarkerPath("cmf-c", 0));

        using (dev)
        {
            var act = () => dev.StartCompact().WaitAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
            act.Should().Throw<InvalidOperationException>().WithMessage("*pending Compact marker*");

            vol.Fs.Exists(MarkerPath("cmf-c", 0)).Should().BeTrue("拒绝路径不清残留");
            ReadAllBytes(vol, MarkerPath("cmf-c", 0)).Should().Equal(contentBefore,
                "残留内容逐字节不动（重启恢复才消化）");
        }
    }

    [Fact]
    public void Recovery_LegacySingleMarker_AlsoConsumed()
    {
        // 存量盘兼容：旧单文件形态（无序号后缀）同样被启动恢复识别消化
        var vol = NewVol();
        var dev = NewDevice(vol, "cmf-e");

        WriteMarkerFile(vol, "cmf-e", null, CompactType.Range, [5001]);
        vol.Fs.Exists($"cmf-e/cmf-e.compact.marker").Should().BeTrue("前置：legacy marker 已落盘");

        dev.Dispose();

        var dev2 = NewDevice(vol, "cmf-e");
        using (dev2)
        {
            vol.Fs.Exists("cmf-e/cmf-e.compact.marker").Should().BeFalse("legacy marker 恢复后删除");
        }
    }

    // ═══ helpers ═══

    /// <summary>写一份真实编码的 marker 到引擎子目录（走 DefaultCompactor 同源 codec）。
    /// seq=null = legacy 单文件形态（无序号后缀）。</summary>
    private static void WriteMarkerFile(TestVolume vol, string engineName, int? seq, CompactType type, int[] newSegIds)
    {
        int bodySize = sizeof(int) * newSegIds.Length;
        var bytes = new byte[CompactMarkerHeaderCodec.StructSize + bodySize];
        for (int i = 0; i < newSegIds.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(CompactMarkerHeaderCodec.StructSize + i * sizeof(int)), newSegIds[i]);
        var header = new CompactMarkerHeader(type, newSegIds.Length, 0);
        CompactMarkerHeaderCodec.Write(bytes, in header);
        bytes.AsSpan(CompactMarkerHeaderCodec.Offset_Crc, sizeof(uint)).Clear();
        header.Crc = UnifiedCrc.ComputeCrc32(bytes);
        CompactMarkerHeaderCodec.Write(bytes, in header);
        WriteRaw(vol, seq is null ? $"{engineName}/{engineName}.compact.marker" : MarkerPath(engineName, seq.Value), bytes);
    }

    private static void WriteCorruptMarkerFile(TestVolume vol, string engineName, int seq)
    {
        var bytes = new byte[CompactMarkerHeaderCodec.StructSize];
        var header = new CompactMarkerHeader(CompactType.Range, 1, 0);
        CompactMarkerHeaderCodec.Write(bytes, in header);
        bytes[^1] ^= 0xFF;   // 破坏 CRC
        WriteRaw(vol, MarkerPath(engineName, seq), bytes);
    }

    private static void WriteRaw(TestVolume vol, string path, byte[] bytes)
    {
        using var h = vol.Fs.Open(path, new FileOpenOptions
        {
            Access = AccessMode.Write,
            Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.None,
        });
        h.Write(0, bytes);
        h.Flush();
    }

    private static byte[] ReadAllBytes(TestVolume vol, string path)
    {
        using var h = vol.Fs.Open(path, new FileOpenOptions
        {
            Access = AccessMode.Read,
            Mode = FileOpenMode.OpenExisting,
            Sharing = FileSharing.ReadWrite | FileSharing.Delete,
        });
        var bytes = new byte[h.Length];
        h.Read(0, bytes).Should().Be(bytes.Length);
        return bytes;
    }
}
