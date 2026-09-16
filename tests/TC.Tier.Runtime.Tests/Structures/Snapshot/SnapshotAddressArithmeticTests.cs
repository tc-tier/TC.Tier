using FluentAssertions;
using TC.Tier.Runtime.Structures.Snapshot;

namespace TC.Tier.Runtime.Tests.Structures.Snapshot;

/// <summary>
/// SnapshotBase 地址算术公开面测试（AdvanceAddress/Distance——段感知推算，产品层帧几何计算依赖）。
/// <para>★ 语义（SegmentTable.Addressing）：恰好填满一段停驻 (segId, segLimit) 段末边界规范形——
///   跨段进位在下一次前进发生；(N, 0) 只保留段首身份。</para>
/// </summary>
public class SnapshotAddressArithmeticTests
{
    private static byte[] MakePayload(int size, byte fill)
    {
        var b = new byte[size];
        Array.Fill(b, fill);
        return b;
    }

    [Fact]
    public async Task AdvanceAddress_CrossesSegmentBoundary_ReadsCorrect()
    {
        const int growthLimit = 4096;   // 小段强制跨段
        var vol = new TestVolume();
        var settings = new StreamSnapshotSettings(
            new StorageEngineOptions("test.addr", growthLimit, enableSegmentation: true));
        try
        {
            await using var snap = new StreamSnapshot(vol.Fs, settings);
            snap.Initialize();
            snap.WaitForReady();

            // 写 2.5 段（fill 标记段号）——首段恰好填满停驻段末，第二次前进跨段进位
            var addrA = snap.PhysicalWriteAddress;
            snap.Append(MakePayload(growthLimit, 0x11));
            var addrB = snap.PhysicalWriteAddress;
            snap.Append(MakePayload(growthLimit / 2, 0x22));
            var addrC = snap.PhysicalWriteAddress;
            snap.Append(MakePayload(growthLimit / 2, 0x33));

            addrB.SegId.Should().Be(0, "恰好填满一段停驻段末（段末边界规范形）");
            addrB.Offset.Should().Be(growthLimit);
            addrC.SegId.Should().Be(1, "段满进位——下一次前进跨段");
            (addrC.SegId > addrB.SegId).Should().BeTrue();

            // Distance：段感知字节距离（跨段不借位错）
            snap.Distance(addrA, addrC).Should().Be(growthLimit + growthLimit / 2);
            snap.Distance(addrC, addrA).Should().Be(-(growthLimit + growthLimit / 2), "负 delta = 回退");

            // AdvanceAddress 跨段推算 + 读回验证
            var expectC = snap.AdvanceAddress(addrA, growthLimit + growthLimit / 2);
            expectC.Should().Be(addrC, "跨段进位推算正确");
            var back = snap.AdvanceAddress(addrC, -(growthLimit + growthLimit / 2));
            back.Should().Be(addrA, "负位移借位正确");

            var dst = new byte[16];
            snap.Read(snap.AdvanceAddress(addrC, 7), dst);
            dst.Should().OnlyContain(b => b == 0x33, "跨段推算地址读回段 1 数据");
        }
        finally
        {
            vol.Dispose();
        }
    }
}
