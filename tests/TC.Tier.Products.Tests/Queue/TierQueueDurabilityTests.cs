using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Testing;
using TC.Tier.Contracts.Storage;
using Xunit;

namespace TC.Tier.Products.Tests.Queue;

/// <summary>
/// TierQueue 持久性/崩溃原子性测试（tc-tier-queue-spec §13 验证矩阵 18）——
/// 组状态提交途中故障注入 → 版本链回滚，cursor 不前移不撕裂。
/// <para>故障注入 = FaultInjectingFileSystem（Core IO 测试替身，IVT 放行）。</para>
/// </summary>
public sealed class TierQueueDurabilityTests
{
    private const string QueueName = "tc.queue";

    /// <summary>组状态 meta 段文件路径（enableSegmentation=true → {name}/{name}.0）。</summary>
    private static string GroupMetaSegmentPath(string groupName)
        => $"{QueueName}.group.{groupName}/{QueueName}.group.{groupName}.0";

    // ══ 矩阵 18：组状态原子——Persist 失败 → cursor 不前移（AckMaterialize 在 Persist 之后）══

    [Fact]
    public async Task GroupStatePersistFailure_CursorNotAdvanced()
    {
        var innerFs = TierFs.New("memory:");
        var fi = new FaultInjectingFileSystem(innerFs);
        var opts = new TierQueueOptions
        {
            QueueName = QueueName,
            PageSize = 64 << 10, MemorySize = 8 << 20,
            DeadLetter = null,
        };
        await using var q = await new TierQueueBuilder(fi, opts).StartAsync();

        var addrs = new List<LogicalAddress>();
        for (int i = 0; i < 3; i++)
            addrs.Add((await q.EnqueueAsync(TierQueueTestFactory.Msg(0, i), default)).Address);

        var g = await q.CreateGroupAsync(new GroupOptions { Name = "g" }, default);
        var all = await g.DequeueAsync(10, default);
        all.Should().HaveCount(3);

        // 确认前 2 条成功（cursor 越过 a1）
        await g.AckAsync(all.Take(2).Select(d => new DeliveryReceipt(d.Address, d.Token)).ToList(), default);
        var cursorAfter2 = g.GroupCursor;
        cursorAfter2.Should().BeGreaterThan(addrs[1], "前 2 条确认推进 cursor");

        // ★ 注入：组 meta 段下一次 Write 失败（Persist 路径 = engine.Write → handle.Write）
        fi.AddRule(GroupMetaSegmentPath("g"), "Write", IOError.IOFailure, failAtCallIndex: 1);

        // 确认第 3 条 → Persist 抛异常
        Func<Task> act = async () => await g.AckAsync(
            all.Skip(2).Select(d => new DeliveryReceipt(d.Address, d.Token)).ToList(), default);
        await act.Should().ThrowAsync<Exception>("组状态持久失败——抛异常");

        // cursor 不前移（AckMaterialize 在 Persist 之后——Persist 失败则内存态不变）
        g.GroupCursor.Should().Be(cursorAfter2, "持久失败——cursor 不前移不撕裂");
    }

    // ══ 矩阵 18（重启验证）：Persist 失败后重启 → 恢复到上一已持久版本（版本链回滚）══

    [Fact]
    public async Task GroupStatePersistFailure_AfterRestart_RecoversToLastCommitted()
    {
        var innerFs = TierFs.New("memory:");
        var fi = new FaultInjectingFileSystem(innerFs);
        var opts = new TierQueueOptions
        {
            QueueName = QueueName,
            PageSize = 64 << 10, MemorySize = 8 << 20,
            DeadLetter = null,
        };

        var addrs = new List<LogicalAddress>();
        LogicalAddress cursorAfter2;
        await using (var q = await new TierQueueBuilder(fi, opts).StartAsync())
        {
            for (int i = 0; i < 3; i++)
                addrs.Add((await q.EnqueueAsync(TierQueueTestFactory.Msg(0, i), default)).Address);

            var g = await q.CreateGroupAsync(new GroupOptions { Name = "g" }, default);
            var all = await g.DequeueAsync(10, default);

            await g.AckAsync(all.Take(2).Select(d => new DeliveryReceipt(d.Address, d.Token)).ToList(), default);
            cursorAfter2 = g.GroupCursor;

            // 注入失败 + 触发（吞异常——模拟崩溃窗口：Persist 半路失败，进程存活但状态未持久）
            fi.AddRule(GroupMetaSegmentPath("g"), "Write", IOError.IOFailure, failAtCallIndex: 1);
            try
            {
                await g.AckAsync(all.Skip(2).Select(d => new DeliveryReceipt(d.Address, d.Token)).ToList(), default);
            }
            catch { /* 模拟崩溃——Persist 失败 */ }
        }

        // 重启（无故障注入——fs 数据已落盘到上一已持久版本）
        fi.ClearRules();
        await using (var q2 = await new TierQueueBuilder(fi, opts).StartAsync())
        {
            var g2 = q2.CreateConsumerAsync("g");
            g2.GroupCursor.Should().Be(cursorAfter2, "重启恢复到上一已持久版本——版本链回滚，第 3 条未确认");

            // 第 3 条可重投（at-least-once——未持久的确认视为未确认）
            var redelivered = await g2.DequeueAsync(10, default);
            redelivered.Should().HaveCount(1);
            redelivered[0].Address.Should().Be(addrs[2]);
        }
    }
}
