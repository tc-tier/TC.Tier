using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// Ring 扫描游标 MoveNextAsync 真异步测试——验证补全后的异步路径。
/// <para>★ 补全前：MoveNextAsync 是基类同步包装（new(MoveNext())），冷区读走同步 ReadDevicePage 阻塞线程。
///   补全后：override MoveNextAsync，热区同步快速路径 + 冷区异步预载（ReadDevicePageAsync，IOCP）。</para>
/// <para>覆盖：热区同步快速路径行为正确 / 冷区异步扫描结果与同步一致 / IS_META 过滤在异步路径生效。</para>
/// <para>★ 接入形态（当前 API）：TestVolume 组合根 + NewRing&lt;long&gt; 一步生命周期。</para>
/// </summary>
public class RingScanCursorAsyncTests
{
    [Fact]
    public async Task MoveNextAsync_HotRegion_ScansAllRecords()
    {
        // ★ 热区（未 flush）：MoveNextAsync 走同步快速路径，行为应等同 MoveNext
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            ring.Write(1L, TestRingSettingsFactory.MakePattern(0x01, 32));
            ring.Write(2L, TestRingSettingsFactory.MakePattern(0x02, 32));
            ring.Write(3L, TestRingSettingsFactory.MakePattern(0x03, 32));
            // ★ 不 flush——数据全在热区 [FlushedUntilAddress, TailAddress)

            using var cursor = ring.OpenScanCursor();
            int syncCount = 0, asyncCount = 0;

            // 同步基线
            using var syncCursor = ring.OpenScanCursor();
            while (syncCursor.MoveNext()) syncCount++;

            // 异步扫描
            while (await cursor.MoveNextAsync()) asyncCount++;

            asyncCount.Should().Be(syncCount, "异步扫描条数应与同步一致");
            asyncCount.Should().Be(3, "应扫到 3 条 record");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task MoveNextAsync_ColdRegion_AsyncScanConsistentWithSync()
    {
        // ★ 冷区（已 flush）：MoveNextAsync 走异步预载路径（ReadDevicePageAsync）
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            ring.Write(1L, TestRingSettingsFactory.MakePattern(0xAA, 32));
            ring.Write(2L, TestRingSettingsFactory.MakePattern(0xBB, 32));
            ring.Write(3L, TestRingSettingsFactory.MakePattern(0xCC, 32));
            ring.FlushUntil(ring.TailAddress);   // ★ 强制落盘 → 数据移到冷区

            int syncCount = 0, asyncCount = 0;
            LogicalAddress firstSyncAddr = default, firstAsyncAddr = default;

            using (var syncCursor = ring.OpenScanCursor())
            {
                while (syncCursor.MoveNext())
                {
                    syncCount++;
                    if (syncCount == 1) firstSyncAddr = syncCursor.CurrentAddress;
                }
            }

            using (var asyncCursor = ring.OpenScanCursor())
            {
                while (await asyncCursor.MoveNextAsync())
                {
                    asyncCount++;
                    if (asyncCount == 1) firstAsyncAddr = asyncCursor.CurrentAddress;
                }
            }

            asyncCount.Should().Be(syncCount, "冷区异步扫描条数应与同步一致");
            asyncCount.Should().Be(3);
            firstAsyncAddr.Should().Be(firstSyncAddr, "冷区异步首条地址应与同步一致");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task MoveNextAsync_FiltersMetaRecord_AsyncPath()
    {
        // ★ 异步路径也过滤 IS_META record（与同步 MoveNextCore 一致）
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            ring.Write(1L, TestRingSettingsFactory.MakePattern(0x11, 32));
            ring.Write(2L, TestRingSettingsFactory.MakePattern(0x22, 32));
            ring.Prepare(seq: 1);   // ★ 触发 meta record 写入

            // 先 flush 让 meta record 进冷区，验证异步冷区路径也过滤
            ring.FlushUntil(ring.TailAddress);

            // ★ async 方法不可调返回 ref 的 unsafe GetInfo（CS8652）；用条数验证过滤：
            //   若不过滤 meta record，条数会是 3；过滤后应为 2
            using var cursor = ring.OpenScanCursor();
            int count = 0;
            while (await cursor.MoveNextAsync()) count++;

            count.Should().Be(2, "2 条数据 record，meta record 应被异步路径过滤（否则会是 3）");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task MoveNextAsync_Cancellation_ThrowsOperationCanceled()
    {
        // ★ 取消令牌传播：已取消的 ct 应触发 OperationCanceledException
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.Write(1L, TestRingSettingsFactory.MakePattern(0x01, 32));
            ring.FlushUntil(ring.TailAddress);   // 冷区才会检查 ct

            using var cursor = ring.OpenScanCursor();
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Func<Task> act = async () => await cursor.MoveNextAsync(cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
        finally { vol.Dispose(); }
    }
}
