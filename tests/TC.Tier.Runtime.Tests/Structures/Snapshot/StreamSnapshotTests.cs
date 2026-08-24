using FluentAssertions;
using TC.Tier.Runtime.Structures.Snapshot;

namespace TC.Tier.Runtime.Tests.Structures.Snapshot;

/// <summary>
/// StreamSnapshot 单元测试（流式帧：双缓冲会话 + CRC64 流式 + append/Overwrite + 2PC + Truncate）。
/// 生命周期：new + Initialize()（后台恢复）+ WaitForReady()。
/// </summary>
public class StreamSnapshotTests
{
    private static byte[] MakePayload(int size, byte fill)
    {
        var b = new byte[size];
        Array.Fill(b, fill);
        return b;
    }

    private static async Task<byte[]> WriteFrameAsync(StreamSnapshot snapshot, byte fill, int size)
    {
        var data = MakePayload(size, fill);
        await using (var writer = snapshot.OpenWrite())
        {
            await writer.WriteAsync(data);
            await writer.CompleteAsync();
        }
        return data;
    }

    private static async Task<(byte[] data, bool footerValid, long entries, long total)> ReadAllAsync(
        StreamSnapshot snapshot, LogicalAddress start, LogicalAddress end)
    {
        await using var reader = snapshot.OpenReadRange(start, end);
        var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await reader.ReadDataAsync(buf)) > 0)
            ms.Write(buf, 0, n);
        return (ms.ToArray(), reader.IsFooterValid, reader.EntryCount, reader.TotalLength);
    }

    [Fact]
    public void Initialize_WaitForReady_Ready()
    {
        var (settings, vol) = TestSnapshotSettingsFactory.CreateSettings();
        try
        {
            using var snap = new StreamSnapshot(vol.Fs, settings);
            snap.Initialize();
            snap.WaitForReady();
            snap.IsReady.Should().BeTrue();
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task Frame_Write_Read_Roundtrip_WithCrc()
    {
        var (settings, vol) = TestSnapshotSettingsFactory.CreateSettings();
        try
        {
            using var snap = new StreamSnapshot(vol.Fs, settings);
            snap.Initialize();
            snap.WaitForReady();

            var data = await WriteFrameAsync(snap, 0xAB, 10000); // 非扇区整数倍——padding 路径
            var (read, valid, entries, total) = await ReadAllAsync(snap, snap.TruncatedAddress, snap.WriteAddress);

            read.Should().Equal(data);
            valid.Should().BeTrue("CRC64 流式累积应与读侧一致");
            entries.Should().Be(1);
            total.Should().Be(10000);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task Frame_MultiEntry_MultiChunk_CrcValid()
    {
        var (settings, vol) = TestSnapshotSettingsFactory.CreateSettings();
        try
        {
            using var snap = new StreamSnapshot(vol.Fs, settings);
            snap.Initialize();
            snap.WaitForReady();

            var expected = new List<byte>();
            await using (var writer = snap.OpenWrite())
            {
                for (int i = 0; i < 20; i++)
                {
                    var chunk = MakePayload(1000 + i, (byte)(i % 251));
                    await writer.WriteAsync(chunk);
                    expected.AddRange(chunk);
                }
                await writer.CompleteAsync();
            }

            var (read, valid, entries, total) = await ReadAllAsync(snap, snap.TruncatedAddress, snap.WriteAddress);
            read.Should().Equal(expected.ToArray());
            valid.Should().BeTrue();
            entries.Should().Be(20);
            total.Should().Be(expected.Count);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public Task Append_Overwrite_RawApi()
    {
        var (settings, vol) = TestSnapshotSettingsFactory.CreateSettings();
        try
        {
            using var snap = new StreamSnapshot(vol.Fs, settings);
            snap.Initialize();
            snap.WaitForReady();

            var addr = snap.PhysicalWriteAddress;
            snap.Append(MakePayload(4096, 0x5A));
            var dst = new byte[4096];
            snap.Read(addr, dst);
            dst[0].Should().Be(0x5A);

            snap.Overwrite(addr, MakePayload(16, 0x99)); // 不可回滚覆写
            var dst2 = new byte[16];
            snap.Read(addr, dst2);
            dst2[0].Should().Be(0x99);
            return Task.CompletedTask;
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public Task Append_Prepare_Abort_Rollback()
    {
        var (settings, vol) = TestSnapshotSettingsFactory.CreateSettings();
        try
        {
            using var snap = new StreamSnapshot(vol.Fs, settings);
            snap.Initialize();
            snap.WaitForReady();

            // v1 append + 提交（推 CommittedWriteAddress）
            var v1Addr = snap.PhysicalWriteAddress;
            snap.Append(MakePayload(4096, 0x11));
            snap.Prepare(seq: 1);
            snap.ConfirmCommitted(seq: 1);
            var committedEnd = snap.WriteAddress;

            // v2 append（可回滚）+ Prepare + Abort
            snap.Append(MakePayload(4096, 0x22));
            snap.Prepare(seq: 2);
            snap.Abort(seq: 2);

            snap.WriteAddress.Should().Be(committedEnd, "Abort 尾截断回滚 append 部分到提交点");
            ((ITransactionParticipant)snap).LastPreparedSeq.Should().Be(1);

            var dst = new byte[4096];
            snap.Read(v1Addr, dst);
            dst[0].Should().Be(0x11, "已提交数据不受 Abort 影响");
            return Task.CompletedTask;
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task TruncatePrefix_HeadReclaimed()
    {
        var (settings, vol) = TestSnapshotSettingsFactory.CreateSettings();
        try
        {
            using var snap = new StreamSnapshot(vol.Fs, settings);
            snap.Initialize();
            snap.WaitForReady();

            var data = await WriteFrameAsync(snap, 0x42, 8192);
            var headAddr = snap.TruncatedAddress; // 帧（含头）起点
            var mid = snap.WriteAddress;          // 帧尾

            snap.TruncatePrefix(mid); // 头截断——整帧回收
            snap.Size.Should().Be(0, "截断到写尾=空流");

            // 头部被段内打洞——读回全零（magic 失效）
            var dst = new byte[64];
            snap.Read(headAddr, dst);
            dst.Should().AllBeEquivalentTo(0, "TruncatePrefix 后头部打洞清零");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task CrossInstance_Recovery_BackwardScanFindsTail()
    {
        var vol = new TestVolume();
        var settings1 = new StreamSnapshotSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(false));
        byte[] data;
        LogicalAddress tail;
        await using (var snap1 = new StreamSnapshot(vol.Fs, settings1))
        {
            snap1.Initialize();
            snap1.WaitForReady();
            data = await WriteFrameAsync(snap1, 0x77, 6000);
            tail = snap1.WriteAddress;
        }

        var settings2 = new StreamSnapshotSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(true));
        using var snap2 = new StreamSnapshot(vol.Fs, settings2);
        snap2.Initialize();
        snap2.WaitForReady();

        snap2.WriteAddress.Should().Be(tail, "Backward 扫描定位帧尾（Disabled 无 meta 兜底）");
        var (read, valid, _, _) = await ReadAllAsync(snap2, snap2.TruncatedAddress, snap2.WriteAddress);
        read.Should().Equal(data);
        valid.Should().BeTrue();
        vol.Dispose();
    }

    [Fact]
    public async Task DanglingAppend_TruncatedOnRecovery_WithTransportMeta()
    {
        var vol = new TestVolume();
        var mk = (bool del) => new StreamSnapshotSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(del))
        {
            MetaPolicyKind = MetaPolicyKind.Transport,
            MetaOpaqueBytes = 64,
        };

        LogicalAddress committedEnd;
        await using (var snap1 = new StreamSnapshot(vol.Fs, mk(false)))
        {
            snap1.Initialize();
            snap1.WaitForReady();
            // v1 append + 提交（meta: committed=1）
            snap1.Append(MakePayload(4096, 0x11));
            snap1.Prepare(1);
            snap1.ConfirmCommitted(1);
            committedEnd = snap1.WriteAddress;
            // v2 append + Prepare(2)——此后模拟崩溃（不 Commit 不 Abort）
            snap1.Append(MakePayload(4096, 0x22));
            snap1.Prepare(2);
        }

        using var snap2 = new StreamSnapshot(vol.Fs, mk(true));
        snap2.Initialize();
        snap2.WaitForReady();

        snap2.WriteAddress.Should().Be(committedEnd, "悬干 append 被 meta 裁决尾截断回提交点");
        ((ITransactionParticipant)snap2).LastCommittedSeq.Should().Be(1);
        var dst = new byte[4096];
        // committed 数据起点 = Empty（首 append 在地址 0）
        snap2.Read(LogicalAddress.Empty, dst);
        dst[0].Should().Be(0x11);
        vol.Dispose();
    }
}
