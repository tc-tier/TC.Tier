using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Log;
using DeltaLog = TC.Tier.Runtime.Structures.Log.DeltaLog;

namespace TC.Tier.Runtime.Tests.Structures.Log;

/// <summary>
/// DeltaLog 集成测试——Append 往返 + OpenCursor 顺序扫描 + 跨页 + 嵌入式 meta。
/// <para>★ 接入形态（当前 API）：TestVolume 组合根 + NewDeltaLog 一步生命周期。</para>
/// </summary>
public class DeltaLogTests
{
    [Fact]
    public async Task Append_Then_OpenCursor_ReadsBack_All()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateDelta();
        try
        {
            using var log = TestLogSettingsFactory.NewDeltaLog(vol, settings);

            var payloads = new[] { new byte[10], new byte[20], new byte[30] };
            for (int i = 0; i < payloads.Length; i++)
                Array.Fill(payloads[i], (byte)(i + 1));

            foreach (var p in payloads)
                log.Append(p);

            await log.FlushAsync();

            using var cursor = log.OpenCursor(LogicalAddress.Empty);
            Assert.NotNull(cursor);
            int idx = 0;
            while (cursor!.MoveNext())
            {
                Assert.Equal(payloads[idx].Length, cursor.CurrentEntryLength);
                Assert.False(cursor.CurrentIsMeta);
                idx++;
            }
            Assert.Equal(payloads.Length, idx);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Append_Advances_TailAddress()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateDelta();
        try
        {
            using var log = TestLogSettingsFactory.NewDeltaLog(vol, settings);
            LogicalAddress tailBefore = log.TailAddress;
            log.Append(new byte[100]);
            Assert.True(log.TailAddress > tailBefore);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task DIO_Append_MultiPage_AllEntries_ReadBack()
    {
        // ★ DIO 多页写读回：DeltaLog 的 FlushPage 同样走扇区三重对齐校验。
        var (settings, vol) = TestLogSettingsFactory.CreateDeltaDIO(logPageSizeBits: 10);   // 1KB 页
        try
        {
            using var log = TestLogSettingsFactory.NewDeltaLog(vol, settings);

            int entries = 30;
            var payloads = new byte[entries][];
            for (int i = 0; i < entries; i++)
            {
                payloads[i] = new byte[100];
                Array.Fill(payloads[i], (byte)(i + 1));
                log.Append(payloads[i]);
            }
            await log.FlushAsync();

            using var cursor = log.OpenCursor(LogicalAddress.Empty);
            Assert.NotNull(cursor);
            int idx = 0;
            while (cursor!.MoveNext())
            {
                Assert.Equal(payloads[idx], cursor.CurrentPayload.ToArray());
                Assert.False(cursor.CurrentIsMeta);
                idx++;
            }
            Assert.Equal(entries, idx);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task FlushAsync_Persists_DataReadable()
    {
        // "已落盘"语义由引擎 CommittedTail（pwrite 水位）表达：Append → Flush → OpenCursor 读回。
        var (settings, vol) = TestLogSettingsFactory.CreateDelta();
        try
        {
            using var log = TestLogSettingsFactory.NewDeltaLog(vol, settings);
            var payload = new byte[] { 7, 8, 9 };
            log.Append(payload);
            await log.FlushAsync();

            using var cursor = log.OpenCursor(LogicalAddress.Empty);
            Assert.NotNull(cursor);
            Assert.True(cursor!.MoveNext());
            Assert.Equal(payload, cursor.CurrentPayload.ToArray());
            Assert.False(cursor.MoveNext());
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task Append_MultipleEntries_CrossPage_Continues()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateDelta(logPageSizeBits: 12);
        try
        {
            using var log = TestLogSettingsFactory.NewDeltaLog(vol, settings);

            for (int i = 0; i < 20; i++)
                log.Append(new byte[1024]);
            await log.FlushAsync();

            using var cursor = log.OpenCursor(LogicalAddress.Empty);
            Assert.NotNull(cursor);
            int count = 0;
            while (cursor!.MoveNext()) count++;
            Assert.Equal(20, count);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void OpenCursor_EmptyLog_ReturnsFalse_Immediately()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateDelta();
        try
        {
            using var log = TestLogSettingsFactory.NewDeltaLog(vol, settings);
            using var cursor = log.OpenCursor(LogicalAddress.Empty);
            Assert.NotNull(cursor);
            Assert.False(cursor!.MoveNext());
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task OpenCursor_CurrentPayload_ReadsBackCorrectBytes()
    {
        // CurrentPayload（Span）正确返回 entry payload 字节，非裸指针 cast
        var (settings, vol) = TestLogSettingsFactory.CreateDelta();
        try
        {
            using var log = TestLogSettingsFactory.NewDeltaLog(vol, settings);

            var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            log.Append(payload);
            await log.FlushAsync();

            using var cursor = log.OpenCursor(LogicalAddress.Empty);
            Assert.NotNull(cursor);
            Assert.True(cursor!.MoveNext());
            Assert.Equal(payload.Length, cursor.CurrentEntryLength);
            Assert.False(cursor.CurrentIsMeta);
            byte[] got = cursor.CurrentPayload.ToArray();
            Assert.Equal(payload, got);
        }
        finally { vol.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ 嵌入式 meta 链路（Transport 回落 MetaHost——IsMeta entry 写入 log 流 + 扫描可见）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public async Task EmbeddedMeta_Commit_Then_Load_RoundTrip()
    {
        // DeltaLog meta 嵌入 log 流（IsMeta entry）。手写 meta 块提交，第二实例扫盘应见 IsMeta entry。
        var vol = new TestVolume();
        try
        {
            // 实例 1：写入 + 手动 meta 提交（保留数据 deleteOnClose=false）
            var settings = TestLogSettingsFactory.DeltaOn(vol, "delta-meta", logPageSizeBits: 14, deleteOnClose: false);
            using (var log = TestLogSettingsFactory.NewDeltaLog(vol, settings))
            {
                log.Append(new byte[100]);
                await log.FlushAsync();
                log.MetaPolicy.WriteHeader(default);   // header 由策略块内组装（MetaHost 走 AppendCore isMeta）
                log.MetaPolicy.WritePayload(new LogMetaPayload
                {
                    BeginAddress = log.BeginAddress,
                    TailAddress = log.TailAddress,
                    CommittedOffset = log.TailAddress,
                });
                await log.MetaPolicy.CommitAsync(default);
            }

            // 实例 2：同卷同引擎名重开（扫盘恢复），cursor 应扫到 IsMeta entry
            var settings2 = TestLogSettingsFactory.DeltaOn(vol, "delta-meta", logPageSizeBits: 14, deleteOnClose: true);
            using var log2 = TestLogSettingsFactory.NewDeltaLog(vol, settings2);
            using var cursor = log2.OpenCursor(LogicalAddress.Empty);
            Assert.NotNull(cursor);
            bool foundMeta = false;
            while (cursor!.MoveNext())
            {
                if (cursor.CurrentIsMeta)
                {
                    foundMeta = true;
                    Assert.True(cursor.CurrentPayload.Length >= 0);
                    break;
                }
            }
            Assert.True(foundMeta, "应扫到 IsMeta entry（嵌入式 meta 落盘）");
        }
        finally { vol.Dispose(); }
    }
}
