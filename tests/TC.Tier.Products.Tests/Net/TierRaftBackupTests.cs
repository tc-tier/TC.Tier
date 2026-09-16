using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Image;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Products.Net.Node;
using TC.Tier.Products.Net.Host;
using TC.Tier.Products.Net.Admin;
using TC.Tier.Products.Net.RaftStore;
using TC.Tier.Products.Wal;
using Xunit;

namespace TC.Tier.Products.Tests.Net;

/// <summary>
/// 备份/恢复编排（二期-F10——验证矩阵 F10 行）：一致性点冻结内 Fs 根空间镜像采集（数据面走
/// Fs——RootSpaceImage TCA1）→ 归档 → 恢复进新 fs → 新节点重放校验（归档→空节点恢复）。
/// </summary>
public class TierRaftBackupTests
{
    private sealed class RecordingStateMachine : IStateMachine
    {
        public readonly ConcurrentDictionary<long, byte[]> Applied = new();
        public ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
        {
            Applied[index] = command.ToArray();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>F10：单节点写入 → 一致性点备份 → 恢复进新 fs → 新节点重放，状态逐字节一致。</summary>
    [Fact]
    public async Task Backup_Restore_ReplaysStateOnFreshNode()
    {
        var work = Path.Combine(Path.GetTempPath(), "tctier-bk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var srcDir = Path.Combine(work, "src");
        var dstDir = Path.Combine(work, "dst");
        var archivePath = Path.Combine(work, "backup.tca1");
        try
        {
            await using var hub = new InProcessTransportHub();
            var id = NodeId.NewRandom();
            var srcTransport = hub.Register(id);
            srcTransport.Start();
            var machine = new RecordingStateMachine();
            await using var node = await TierRaftNodeBuilder.Create(id,
                    TierFs.OpenOrCreate($"local:///{srcDir.Replace('\\', '/')}"),
                    new ClusterConfig([new ClusterMember(id, "")]), machine)
                .WithTransport(srcTransport)
                .WithOptions(FastOptions())
                .StartAsync();

            // 写 5 条（内容可区分——恢复后逐条校验）
            for (var i = 1; i <= 5; i++)
                await node.Raft.ReplicateAsync(new byte[] { (byte)i }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForAsync(() => machine.Applied.Count >= 5, TimeSpan.FromSeconds(10));

            // 一致性点备份（append 门内冻结——数据面走 Fs 根空间镜像）
            ImageSummary summary;
            byte[] archiveBytes;
            using (var archive = new MemoryStream())
            {
                summary = await node.BackupToAsync(archive).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                archiveBytes = archive.ToArray();
            }
            summary.EntryCount.Should().BeGreaterThanOrEqualTo(5, "镜像覆盖已提交写入");
            summary.FrameCount.Should().BeGreaterThan(0, "TCA1 帧化采集");

            await node.DisposeAsync();

            // 恢复进新 fs → 新节点重放
            var dstFs = TierFs.OpenOrCreate($"local:///{dstDir.Replace('\\', '/')}");
            using (var archive = new MemoryStream(archiveBytes))
            {
                var restored = TierRaftNode.RestoreArchive(archive, dstFs);
                restored.EntryCount.Should().Be(summary.EntryCount, "恢复摘要与备份对账一致");
            }

            await using var hub2 = new InProcessTransportHub();
            var t = hub2.Register(id);
            t.Start();
            var restoredMachine = new RecordingStateMachine();
            await using var recovered = await TierRaftNodeBuilder.Create(id,
                    dstFs, new ClusterConfig([new ClusterMember(id, "")]), restoredMachine)
                .WithTransport(t)
                .WithOptions(FastOptions())
                .StartAsync();

            recovered.Raft.IsLeader.Should().BeTrue("恢复节点以自身配置重选（N=1 standalone）");
            await WaitForAsync(() => restoredMachine.Applied.Count >= 5, TimeSpan.FromSeconds(10));
            for (var i = 1; i <= 5; i++)
                restoredMachine.Applied.Values.Should().Contain(v => v.SequenceEqual(new[] { (byte)i }),
                    $"条目 {i} 重放逐字节一致");
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }

    private static TierRaftNodeOptions FastOptions() => TierRaftNodeOptions.Default
        .WithWal(TierWalOptions.Default.WithCommitInterval(TimeSpan.FromMilliseconds(-1)))
        // ★ 钉死 appliedIndex 不节流落盘（重放校验前提）：PersistInterval=100ms 落盘点若跨过备份
        //   采集时刻，镜像携带 appliedIndex>0——恢复节点自该点续跑、全新状态机缺日志前缀（2026-09-12
        //   判例 #423：~50% 复现的"重放停摆"实为 appliedIndex 竞速跳过重放）。F10 校验"归档→空节点
        //   重放"，镜像必须含 appliedIndex=0 → 全量重放确定性成立。
        .WithApply(ApplyPipelineOptions.Default
            .WithPersistEvery(int.MaxValue)
            .WithPersistInterval(TimeSpan.FromHours(1)))
        .WithRaft(RaftOptions.Default.WithElectionTimeout(TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(1200)));

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("条件未满足（超时）。");
            await Task.Delay(50);
        }
    }
}
