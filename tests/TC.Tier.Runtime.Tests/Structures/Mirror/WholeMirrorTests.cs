using FluentAssertions;
using TC.Tier.Runtime.Structures.Mirror;

namespace TC.Tier.Runtime.Tests.Structures.Mirror;

/// <summary>
/// WholeMirror 单元测试（v2 流式帧：BeginSession→AppendChunk×N→EndSession + CRC64 流式 +
/// N=2 轮替 + 2PC + 尾锚恢复 + 假 magic 重同步 + 零富集回归）。
/// 生命周期：new + Initialize()（后台恢复）+ WaitForReady()。
/// </summary>
public class WholeMirrorTests
{
    private static byte[] MakePayload(int size, byte fill)
    {
        var b = new byte[size];
        Array.Fill(b, fill);
        return b;
    }

    private static LogicalAddress WriteCheckpoint(WholeMirror mirror, long totalSize, byte fill)
    {
        var addr = mirror.BeginSession();
        var chunk = MakePayload(4096, fill);
        long off = 0;
        while (off < totalSize)
        {
            int n = (int)Math.Min(chunk.Length, totalSize - off);
            mirror.AppendChunk(chunk.AsSpan(0, n));
            off += n;
        }
        mirror.EndSession();
        return addr;
    }

    [Fact]
    public void Initialize_WaitForReady_Ready()
    {
        var (settings, vol) = TestMirrorSettingsFactory.CreateWholeSettings();
        try
        {
            using var mirror = new WholeMirror(vol.Fs, settings);
            mirror.Initialize();
            mirror.WaitForReady();
            mirror.IsReady.Should().BeTrue();
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Write_Verify_ReadChunk_Roundtrip()
    {
        var (settings, vol) = TestMirrorSettingsFactory.CreateWholeSettings();
        try
        {
            using var mirror = new WholeMirror(vol.Fs, settings);
            mirror.Initialize();
            mirror.WaitForReady();

            long total = 10000; // 任意帧长（v2 无 padding——帧尾不必扇区对齐）
            var addr = mirror.BeginSession();
            var data = MakePayload((int)total, 0xAB);
            mirror.AppendChunk(data.AsSpan(0, 6000));
            mirror.AppendChunk(data.AsSpan(6000));
            mirror.EndSession();

            mirror.Verify(addr).Should().BeTrue("写后 CRC64 应一致");
            mirror.GetPayloadLength(addr).Should().Be(total, "像长 = 尾位−头−头尾结构（推导）");

            var dst = new byte[total];
            int n = mirror.ReadChunk(addr, 0, dst);
            n.Should().Be((int)total);
            dst.Should().Equal(data);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void AppendChunk_BeforeBegin_Throws()
    {
        var (settings, vol) = TestMirrorSettingsFactory.CreateWholeSettings();
        try
        {
            using var mirror = new WholeMirror(vol.Fs, settings);
            mirror.Initialize();
            mirror.WaitForReady();

            var act = () => mirror.AppendChunk(new byte[100]);
            act.Should().Throw<InvalidOperationException>().WithMessage("*帧未开始*");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Write_ThenRead_BeforeCommit_StillReadable()
    {
        // 会话写入后未 Confirm——数据可读（checkpoint 门面直读 record 地址，可见性由调用方管理）
        var (settings, vol) = TestMirrorSettingsFactory.CreateWholeSettings();
        try
        {
            using var mirror = new WholeMirror(vol.Fs, settings);
            mirror.Initialize();
            mirror.WaitForReady();

            long total = 4096;
            var addr = mirror.BeginSession();
            mirror.AppendChunk(MakePayload((int)total, 0x5A));
            mirror.EndSession();

            var dst = new byte[total];
            mirror.ReadChunk(addr, 0, dst);
            dst[0].Should().Be(0x5A);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Write_Prepare_Confirm_2PC()
    {
        var (settings, vol) = TestMirrorSettingsFactory.CreateWholeSettings();
        try
        {
            using var mirror = new WholeMirror(vol.Fs, settings);
            mirror.Initialize();
            mirror.WaitForReady();

            var v1Addr = WriteCheckpoint(mirror, 8192, 0x11);
            mirror.Prepare(seq: 1);
            ((ITransactionParticipant)mirror).LastPreparedSeq.Should().Be(1);
            ((ITransactionParticipant)mirror).LastCommittedSeq.Should().Be(-1);

            mirror.ConfirmCommitted(seq: 1);
            ((ITransactionParticipant)mirror).LastCommittedSeq.Should().Be(1);
            mirror.CurrentVersion.Should().Be(1);
            // ★ 首 record 地址就是 Empty（合法地址空间起点）——链头断言用写入返回值，不用地址值判空
            mirror.HighestVersionAddress.Should().Be(v1Addr);

            var dst = new byte[8192];
            mirror.ReadChunk(mirror.HighestVersionAddress, 0, dst);
            dst[0].Should().Be(0x11);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Write_Prepare_Abort_RollbackTail()
    {
        var (settings, vol) = TestMirrorSettingsFactory.CreateWholeSettings();
        try
        {
            using var mirror = new WholeMirror(vol.Fs, settings);
            mirror.Initialize();
            mirror.WaitForReady();

            // v1 提交
            WriteCheckpoint(mirror, 8192, 0x11);
            mirror.Prepare(seq: 1);
            mirror.ConfirmCommitted(seq: 1);
            var v1Addr = mirror.HighestVersionAddress;

            // v2 写完 + Prepare → Abort（悬干回退）
            WriteCheckpoint(mirror, 8192, 0x22);
            mirror.Prepare(seq: 2);
            mirror.Abort(seq: 2);

            mirror.HighestVersionAddress.Should().Be(v1Addr, "Abort 应回退链头到上一已提交 checkpoint");
            mirror.CurrentVersion.Should().Be(1, "版本号不推进");
            ((ITransactionParticipant)mirror).LastPreparedSeq.Should().Be(1);

            var dst = new byte[8192];
            mirror.ReadChunk(v1Addr, 0, dst);
            dst[0].Should().Be(0x11, "已提交 checkpoint 不受 Abort 影响");
            mirror.Verify(v1Addr).Should().BeTrue("Abort 尾截断后 v1 CRC 完整");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void N2_Rotation_OldestCheckpointReclaimed()
    {
        var (settings, vol) = TestMirrorSettingsFactory.CreateWholeSettings();
        try
        {
            using var mirror = new WholeMirror(vol.Fs, settings);
            mirror.Initialize();
            mirror.WaitForReady();

            LogicalAddress v1Addr = default;
            for (int i = 1; i <= 3; i++)
            {
                WriteCheckpoint(mirror, 4096, (byte)(0x10 + i));
                mirror.Prepare(seq: i);
                mirror.ConfirmCommitted(seq: i);
                if (i == 1) v1Addr = mirror.HighestVersionAddress;
            }

            mirror.CurrentVersion.Should().Be(3);
            // N=2：第 3 次提交后 v1 被头截断——v1 帧头被 punch hole 清零（magic 失效）
            mirror.LowestVersionAddress.Should().NotBe(LogicalAddress.Empty, "已有回收边界");
            mirror.GetFrameInfo(v1Addr).Should().BeNull("v1 已被 N=2 轮替回收——帧头不可再定位");

            // v2/v3 仍可读
            var dst = new byte[4096];
            mirror.ReadChunk(mirror.HighestVersionAddress, 0, dst);
            dst[0].Should().Be(0x13, "链头是 v3");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void CrossInstance_Recovery_HeadRestored()
    {
        var vol = new TestVolume();
        var settings1 = new WholeMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(false));
        LogicalAddress v2Addr;
        using (var mirror1 = new WholeMirror(vol.Fs, settings1))
        {
            mirror1.Initialize();
            mirror1.WaitForReady();
            WriteCheckpoint(mirror1, 4096, 0x11);
            mirror1.Prepare(1);
            mirror1.ConfirmCommitted(1);
            WriteCheckpoint(mirror1, 4096, 0x22);
            mirror1.Prepare(2);
            mirror1.ConfirmCommitted(2);
            v2Addr = mirror1.HighestVersionAddress;
        }

        // 同卷第二实例（尾锚扫盘恢复链头）
        var settings2 = new WholeMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(true));
        using var mirror2 = new WholeMirror(vol.Fs, settings2);
        mirror2.Initialize();
        mirror2.WaitForReady();

        mirror2.CurrentVersion.Should().Be(2);
        mirror2.HighestVersionAddress.Should().Be(v2Addr, "尾锚直达最新数据帧");
        var dst = new byte[4096];
        mirror2.ReadChunk(mirror2.HighestVersionAddress, 0, dst);
        dst[0].Should().Be(0x22, "恢复到最新 checkpoint");
        vol.Dispose();
    }

    [Fact]
    public void EmptyVolume_Ready_EmptyState()
    {
        var (settings, vol) = TestMirrorSettingsFactory.CreateWholeSettings();
        try
        {
            using var mirror = new WholeMirror(vol.Fs, settings);
            mirror.Initialize();
            mirror.WaitForReady();
            mirror.IsReady.Should().BeTrue();
            mirror.CurrentVersion.Should().Be(0);
            mirror.HighestVersionAddress.Should().Be(LogicalAddress.Empty);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>
    /// 假 magic 命中（标准命题）：payload 埋诱饵 + 帧后垃圾区塞假尾（结构烂 / 结构完好但 CRC 错两式）——
    /// magic 只提名候选，CRC/结构才是裁决；假命中必须被跳过（缩窗重同步），真帧数据无损。
    /// </summary>
    [Fact]
    public void DecoyMagic_CrcRejectsAndResyncs()
    {
        var vol = new TestVolume();
        var settings1 = new WholeMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(false));
        using (var mirror1 = new WholeMirror(vol.Fs, settings1))
        {
            mirror1.Initialize();
            mirror1.WaitForReady();
            // v1：payload 埋诱饵（对齐+非对齐偏移塞双 magic——v2 alignment=1 全都看得见）
            var decoy = MakePayload(4096, 0x33);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(decoy.AsSpan(64),
                RecordMagic.WholeMirror);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(decoy.AsSpan(199),
                RecordMagic.WholeMirrorFooter);
            var addr = mirror1.BeginSession();
            mirror1.AppendChunk(decoy);
            mirror1.EndSession();
            mirror1.Prepare(1);
            mirror1.ConfirmCommitted(1);
            mirror1.Verify(addr).Should().BeTrue("诱饵在 payload 内不影响本帧 CRC（覆盖域原样入算）");
        }

        // 帧后垃圾区（模拟撕裂写残留）：① 结构烂的假尾（magic 对、version 垃圾）
        // ② 结构完好的假尾（magic/version/flags 全对、MirrorVersion 错配真头、CRC 必不过）
        using (var engine = new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(false).Builder(vol.Fs).Start())
        {
            engine.WaitForReady();
            var junk1 = MakePayload(40, 0xFF);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(junk1, RecordMagic.WholeMirrorFooter);
            engine.Append(junk1);
            var junk2 = MakePayload(MirrorFrameFooterCodec.StructSize, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(junk2, RecordMagic.WholeMirrorFooter);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(junk2.AsSpan(4),
                MirrorFrameFooter.CurrentVersion);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(junk2.AsSpan(6),
                RecordFlags.FLAG_CRC64);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(junk2.AsSpan(24), 777);   // 假版本
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(junk2.AsSpan(32), 0xDEAD_BEEF);
            engine.Append(junk2);
        }

        var settings2 = new WholeMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(true));
        using var mirror2 = new WholeMirror(vol.Fs, settings2);
        mirror2.Initialize();
        mirror2.WaitForReady();

        // 尾锚从 CommittedTail 倒扫先撞 junk2（结构完好版本错配）→ 孤儿尾缩窗 → junk1（结构烂）→ 缩窗
        // → 真尾 → 倒扫真头（payload 诱饵 WMHD 在真头之后、真尾之前——CRC 裁决必过真头）→ 恢复成功
        mirror2.CurrentVersion.Should().Be(1, "假 magic 不产生幻影版本");
        var dst = new byte[4096];
        mirror2.ReadChunk(mirror2.HighestVersionAddress, 0, dst);
        dst[0].Should().Be(0x33, "真帧数据无损——诱饵只是 payload 字节");
        vol.Dispose();
    }

    /// <summary>
    /// 零富集回归（L30 命题）：payload 全零（索引空桶区合法形态）——
    /// v2 尾锚按 magic 定位（无"非零=数据"假设），全零载荷帧照常恢复。
    /// </summary>
    [Fact]
    public void ZeroRichPayload_RecoveryStillFindsNewest()
    {
        var vol = new TestVolume();
        var settings1 = new WholeMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(false));
        using (var mirror1 = new WholeMirror(vol.Fs, settings1))
        {
            mirror1.Initialize();
            mirror1.WaitForReady();
            WriteCheckpoint(mirror1, 8192, 0x00);   // 全零载荷（99% 桶区为零的真实索引像形态）
            mirror1.Prepare(1);
            mirror1.ConfirmCommitted(1);
            WriteCheckpoint(mirror1, 8192, 0x00);
            mirror1.Prepare(2);
            mirror1.ConfirmCommitted(2);
        }

        var settings2 = new WholeMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(true));
        using var mirror2 = new WholeMirror(vol.Fs, settings2);
        mirror2.Initialize();
        mirror2.WaitForReady();

        mirror2.CurrentVersion.Should().Be(2, "零富集载荷不骗走尾锚——最新帧直达");
        mirror2.Verify(mirror2.HighestVersionAddress).Should().BeTrue("零载荷帧 CRC 完整");
        var dst = new byte[8192];
        int n = mirror2.ReadChunk(mirror2.HighestVersionAddress, 0, dst);
        n.Should().Be(8192);
        dst.Should().OnlyContain(b => b == 0, "全零载荷原样恢复");
        vol.Dispose();
    }

    /// <summary>
    /// 尾锚新代可达（旧代再烂也遮不住新代）：v2 帧体损坏（旧代 payload 打脏）——
    /// 最新帧照常恢复（尾锚只看最新方向 + PreviousVersion 链对旧代只验结构）。
    /// </summary>
    [Fact]
    public void OlderFrameCorrupted_NewestStillReachable()
    {
        var vol = new TestVolume();
        var settings1 = new WholeMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(false));
        LogicalAddress v1Addr = default, v2Addr;
        using (var mirror1 = new WholeMirror(vol.Fs, settings1))
        {
            mirror1.Initialize();
            mirror1.WaitForReady();
            v1Addr = WriteCheckpoint(mirror1, 4096, 0x11);
            mirror1.Prepare(1);
            mirror1.ConfirmCommitted(1);
            WriteCheckpoint(mirror1, 4096, 0x22);
            mirror1.Prepare(2);
            mirror1.ConfirmCommitted(2);
            v2Addr = mirror1.HighestVersionAddress;
        }

        // 裸引擎打脏 v1 帧 payload（帧中段写垃圾——不碰 v2）
        using (var engine = new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(false).Builder(vol.Fs).Start())
        {
            engine.WaitForReady();
            var junk = MakePayload(64, 0xEE);
            engine.Write(engine.CalculationAddress(v1Addr, 64), junk);   // v1 payload 中段
        }

        var settings2 = new WholeMirrorSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(true));
        using var mirror2 = new WholeMirror(vol.Fs, settings2);
        mirror2.Initialize();
        mirror2.WaitForReady();

        mirror2.CurrentVersion.Should().Be(2, "旧代损坏不遮新代——尾锚直达 v2");
        mirror2.HighestVersionAddress.Should().Be(v2Addr);
        var dst = new byte[4096];
        mirror2.ReadChunk(mirror2.HighestVersionAddress, 0, dst);
        dst[0].Should().Be(0x22, "链头是完好的 v2");
        vol.Dispose();
    }
}
