using TC.Tier.Core.IO;
using TC.Tier.Runtime.Structures.Log;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Log;

/// <summary>
/// LogBase 恢复扫盘 CRC 验收测试——撕裂/坏帧止步于前一有效帧，不把坏数据当"最后有效帧"通过恢复。
/// <para>★ 场景：三帧日志（页满让渡驱动成帧），物理翻转末帧 data 区字节后重开——
///   恢复尾必须回退到末帧之前的帧边界，末帧的 entry 不重放。</para>
/// </summary>
public class LogRecoveryCrcTests
{
    [Fact]
    public void Recovery_CorruptLastFrameData_ScanStopsAtLastValidFrame()
    {
        var vol = new TestVolume();
        const string engineName = "crc-reco";
        var settings = TestLogSettingsFactory.EntryOn(vol, engineName, logPageSizeBits: 14, deleteOnClose: false);
        try
        {
            LogicalAddress tailBeforeCorruption;
            using (var log = TestLogSettingsFactory.NewEntryLog(vol, settings))
            {
                var frame1Payload = new byte[10 * 1024];   // 10KB——页 16KB，第二次追加页满让渡成帧
                var frame2Payload = new byte[1024];
                log.Append(frame1Payload);                 // 帧 1（page1 唯一 entry）
                log.Append(frame1Payload);                 // 页满让渡 → 帧 1 落盘，进 page2
                log.Append(frame2Payload);                 // page2，Dispose 时落盘成帧 2
                tailBeforeCorruption = log.TailAddress;
            }   // Dispose flush → 帧 2 落盘

            // 物理翻转末帧 data 区一个字节（data 区 ⊇ [fileEnd-1024]——padding<512、CRC=4 不在此）
            var path = $"{engineName}/{engineName}.0";
            using (var h = vol.Fs.Open(path, new FileOpenOptions { Access = AccessMode.ReadWrite }))
            {
                long len = h.Length;
                var buf = new byte[1];
                _ = h.Read(len - 1024, buf);
                buf[0] ^= 0xFF;
                h.Write(len - 1024, buf);
            }

            using var log2 = TestLogSettingsFactory.NewEntryLog(vol, settings);
            log2.TailAddress.Should().BeLessThan(tailBeforeCorruption,
                "末帧 CRC 验收失败——恢复尾必须止步于前一有效帧，坏帧不得计入");

            int replayed = 0;
            log2.Replay((payload, isMeta, addr) => replayed++);
            replayed.Should().Be(1, "只有帧 1 的 entry 重放；坏帧 2 的 entry 不得通过恢复");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Recovery_IntactFrames_AllReplay()   // 对照组：同构造不破坏——三 entry 全重放
    {
        var vol = new TestVolume();
        var settings = TestLogSettingsFactory.EntryOn(vol, "crc-ok", logPageSizeBits: 14, deleteOnClose: false);
        try
        {
            LogicalAddress tailBefore;
            using (var log = TestLogSettingsFactory.NewEntryLog(vol, settings))
            {
                var framePayload = new byte[10 * 1024];
                log.Append(framePayload);
                log.Append(framePayload);
                log.Append(new byte[1024]);
                tailBefore = log.TailAddress;
            }

            using var log2 = TestLogSettingsFactory.NewEntryLog(vol, settings);
            // 恢复尾 = 末帧数据尾（padded 对齐边界）≥ 停机前逻辑尾——完好场景不回退
            log2.TailAddress.Should().BeGreaterThanOrEqualTo(tailBefore, "完好帧全数通过验收——恢复不丢数据");

            int replayed = 0;
            log2.Replay((payload, isMeta, addr) => replayed++);
            replayed.Should().Be(3);
        }
        finally { vol.Dispose(); }
    }
}
