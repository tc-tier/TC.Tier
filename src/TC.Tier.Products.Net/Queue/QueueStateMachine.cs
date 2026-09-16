using System.Collections.Concurrent;
using System.Text;
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.IO;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Products.Queue;

namespace TC.Tier.Products.Net.Queue;

/// <summary>
/// QueueStateMachine——TierQueue 复制状态机（tierqueue-replicated-spec §2——IStateMachine 实现，
/// ApplyPipeline 单 worker 串行 apply）。
/// <para>★ 六命令族 apply（确定性核心经 <see cref="ITierQueueReplicationPort"/> 驱动本地 Queue 写路径/
/// 组游标表）：Enqueue（Ring 写——apply 序即地址序，全组同地址）/ Ack（游标推进 + epoch fencing）/
/// Group（生命周期）/ Expire（记账回收 or Claim 代次递增）/ Home（辖权授予/移交/接管 + 组代次 +1）/
/// Retention（回收线推进）。</para>
/// <para>★ 结果表（corrId → 完成源）：提案方在提案前注册、apply 内完成——ReplicateAsync 返回
/// （applied 水位越过该 index）时结果必然就绪；重放条目无 waiter 即不存储（零泄漏）。</para>
/// <para>★ 辖权表（组名 → home）：HomeCmd/GroupCmd(Create) 的 apply 产物——纯内存镜像
/// （快照随组状态一并持久）；变更经 <see cref="GroupHomeChanged"/> 广播（订阅方重连）。</para>
/// <para>★ 快照恢复（spec ⑤——ExportStateAsync/ImportStateAsync 可选路径的自管实现）：
/// ring 检查点帧（地址区间像）+ 组 meta 镜像 + 辖权表——压缩钩子触发导出（applied ≥ N₀ 守卫由
/// TierRaftNode 宿主循环保证）；重启首条 apply 前懒导入，<see cref="AppliedThrough"/> 跳过导入覆盖区间。</para>
/// </summary>
public sealed class QueueStateMachine : IStateMachine, IDisposable
{
    private readonly ITierQueueReplicationPort _port;
    private readonly IFileSystem _snapshotFs;
    private readonly string _snapshotPath;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<GroupApplyResult>> _waiters = new();
    private readonly ConcurrentDictionary<string, NodeId> _homes = new();
    private readonly SemaphoreSlim _exportGate = new(1, 1);   // apply 与导出互斥（apply 单 worker 也经此串行）
    /// <summary>释放导出互斥门（生命周期归宿主——宿主收口时调用）。</summary>
    public void Dispose() => _exportGate.Dispose();

    private long _appliedThrough;   // 已应用水位（含导入覆盖区间——apply worker / 导出 gate 内）
    private bool _imported;         // 懒导入完成标志（apply worker 内）

    /// <summary>构造。</summary>
    /// <param name="port">本地 Queue 的复制 apply 端口（确定性核心）。</param>
    /// <param name="snapshotFs">快照文件系统（组私卷——与 raft 日志同卷不同名空间）。</param>
    /// <param name="snapshotPath">快照文件路径（temp+rename 原子落盘）。</param>
    /// <param name="logger">日志。</param>
    public QueueStateMachine(ITierQueueReplicationPort port, IFileSystem snapshotFs, string snapshotPath,
        ILogger? logger = null)
    {
        _port = port;
        _snapshotFs = snapshotFs;
        _snapshotPath = snapshotPath;
        _logger = logger;
    }

    /// <summary>辖权变更事件（HomeCmd/GroupCmd(Create) apply 产物——接管/移交/授予通知，订阅方重连）。</summary>
    public event Action<string, NodeId>? GroupHomeChanged;

    /// <summary>已应用水位（含快照导入覆盖区间——提案方 warmup/诊断读面）。</summary>
    public long AppliedThrough => Interlocked.Read(ref _appliedThrough);

    /// <summary>指定组的当前辖权节点（本地已应用视图；Empty = 辖权未定）。</summary>
    /// <param name="group">消费组名。</param>
    /// <returns>辖权节点。</returns>
    public NodeId GetHome(string group)
        => _homes.TryGetValue(group, out var home) ? home : NodeId.Empty;

    // ═══ 提案面（TierQueueReplica 调用——waiter 注册先于提案，apply 完成必然可领取）═══

    /// <summary>注册结果领取源（提案前调用——corrId 嵌入命令字节，apply 时完成）。</summary>
    /// <param name="correlation">关联 ID。</param>
    /// <returns>完成源。</returns>
    public TaskCompletionSource<GroupApplyResult> RegisterWaiter(ulong correlation)
    {
        var tcs = new TaskCompletionSource<GroupApplyResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters[correlation] = tcs;
        return tcs;
    }

    /// <summary>注销结果领取源（提案失败/取消时清理——防 waiter 泄漏）。</summary>
    /// <param name="correlation">关联 ID。</param>
    public void ForgetWaiter(ulong correlation) => _waiters.TryRemove(correlation, out _);

    /// <summary>等待本地 apply 水位越过指定 index（入口 read-your-writes——spec ④"apply 后本地即见"；
    /// apply 单调推进，轮询等待）。</summary>
    /// <param name="index">目标日志 index。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>水位越过后完成。</returns>
    public async ValueTask WaitForAppliedAsync(long index, CancellationToken ct)
    {
        while (AppliedThrough < index)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }

    // ═══ IStateMachine（ApplyPipeline 单 worker 调用）═══

    /// <inheritdoc/>
    /// <returns>apply 完成（状态已推进 + 结果表已派发）后完成。</returns>
    public async ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken cancellationToken = default)
    {
        await _exportGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ApplyCoreAsync(index, command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _exportGate.Release();
        }
    }

    private async ValueTask ApplyCoreAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct)
    {
        if (index <= _appliedThrough)
            return;   // 快照导入覆盖区间（重启重放跳过——导入态已含该前缀的效果）

        await EnsureImportedAsync(ct).ConfigureAwait(false);

        GroupApplyResult result;
        if (!QueueCommandCodec.TryDecode(command.Span, out var cmd))
        {
            // 日志损坏形态——fail-fast（raft 存储契约上游保证条目完整；此处不可静默吞）
            throw new InvalidDataException($"复制命令解码失败（未知 tag/截断/超上限，index={index}）。");
        }

        try
        {
            result = await ApplyCommandAsync(cmd, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // ★ 确定性拒绝（组已存在/epoch 落后——重放也必然同判）→ 终局结果；
            //   瞬态失败（IO/取消——重放可能成功）→ 原样上抛交还 apply 管道重试（at-least-once 契约），
            //   吞成终局 = 副本状态永久发散（HomeCmd/游标推进丢失且不可恢复）。
            if (e is StaleDeliveryException || e is InvalidOperationException)
                result = FromException(e);
            else
                throw;
        }
        if (result.Status == GroupApplyStatus.Ok)
            result = result with { AppliedIndex = index };
        Interlocked.Exchange(ref _appliedThrough, index);

        if (_waiters.TryRemove(cmd.Correlation, out var waiter))
            waiter.TrySetResult(result);
    }

    /// <summary>异常转结果（apply 确定性异常归类——迟交 StaleEpoch/其余 Rejected；产品异常形态归队列层）。</summary>
    /// <param name="e">apply 抛出的异常。</param>
    /// <returns>归类后的失败结果。</returns>
    private static GroupApplyResult FromException(Exception e) => e switch
    {
        StaleDeliveryException => new(GroupApplyStatus.StaleEpoch, default, 0, e.Message),
        _ => new(GroupApplyStatus.Rejected, default, 0, e.Message),
    };

    private async ValueTask<GroupApplyResult> ApplyCommandAsync(QueueCommand message, CancellationToken ct)
    {
        switch (message)
        {
            case QueueEnqueueCommand e:
            {
                var addr = await _port.ApplyEnqueueAsync(e.Delayed, e.DueTime, e.Idempotent, e.ProducerId, e.Seq,
                    e.Payload, ct).ConfigureAwait(false);
                return GroupApplyResult.FromOk(addr);
            }
            case QueueAckCommand a:
            {
                await _port.ApplyAckAsync(Name(a.Name), a.Epoch, a.Addresses, ct).ConfigureAwait(false);
                return GroupApplyResult.FromOk();
            }
            case QueueGroupCommand g:
            {
                var name = Name(g.Name);
                switch (g.Operation())
                {
                    case QueueGroupOp.Create:
                        await _port.ApplyGroupCreateAsync(new GroupOptions
                        {
                            Name = name,
                            StartAt = g.StartPoint(),
                            StartAddress = g.StartAddress,
                            VisibilityTimeout = TimeSpan.FromMilliseconds(Math.Max(1, g.VisibilityTimeoutMs)),
                            MaxRedeliveries = g.MaxRedeliveries,
                        }, ct).ConfigureAwait(false);
                        SetHome(name, g.Home);
                        break;
                    case QueueGroupOp.Delete:
                        await _port.ApplyGroupDeleteAsync(name).ConfigureAwait(false);
                        if (_homes.TryRemove(name, out _))
                            GroupHomeChanged?.Invoke(name, NodeId.Empty);
                        break;
                    case QueueGroupOp.Reset:
                        await _port.ApplyGroupResetAsync(name, g.StartPoint(), g.StartAddress, ct).ConfigureAwait(false);
                        break;
                }
                return GroupApplyResult.FromOk();
            }
            case QueueExpireCommand x:
            {
                var name = Name(x.Name);
                if (x.Operation() == QueueExpireOp.Claim)
                    await _port.ApplyEpochBumpAsync(name).ConfigureAwait(false);
                else
                    await _port.ApplyExpireAsync(name, x.Entries.Select(ToEntry).ToArray(), ct)
                        .ConfigureAwait(false);
                return GroupApplyResult.FromOk();
            }
            case QueueHomeCommand h:
            {
                var name = Name(h.Name);
                await _port.ApplyEpochBumpAsync(name).ConfigureAwait(false);   // 辖权变更恒 fencing 递增（spec ③）
                SetHome(name, h.NewHome);
                return GroupApplyResult.FromOk();
            }
            case QueueRetentionCommand r:
                _port.ApplyRetention(r.HasTarget ? r.Target : null);
                return GroupApplyResult.FromOk();
            default:
                throw new FormatException($"命令族未知：{message.GetType().Name}");
        }
    }

    /// <summary>组名 blob（UTF8）还原。</summary>
    private static string Name(ReadOnlyMemory<byte> blob) => Encoding.UTF8.GetString(blob.Span);

    /// <summary>到期条目线帧 → 域条目（跨程序集布局判例——帧类型留在 Products.Net）。</summary>
    private static QueueExpireEntry ToEntry(QueueExpireEntryFrame frame)
        => new(frame.Address, frame.RedeliveryCount, frame.DeadLettered != 0);

    private void SetHome(string group, NodeId home)
    {
        _homes[group] = home;
        GroupHomeChanged?.Invoke(group, home);
    }

    // ═══ 快照（spec ⑤——ring 检查点帧 + 组 meta 镜像 + 辖权表；temp+rename 原子落盘）═══

    /// <summary>导出快照（压缩钩子触发——TierRaftNode 宿主循环保证 applied ≥ N₀；导出与 apply 互斥）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>导出覆盖的应用水位 S（重启懒导入后 _appliedThrough 起点）。</returns>
    public async ValueTask<long> ExportSnapshotAsync(CancellationToken ct = default)
    {
        await _exportGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (begin, tail) = _port.GetRingBounds();
            long ringLength;
            if (begin.IsValid && tail.IsValid && tail > begin)
            {
                await using var probe = _port.OpenRingReader(begin, tail);
                ringLength = probe.Length;
            }
            else
            {
                ringLength = 0;
            }

            var groups = _port.GetGroupNames();
            var homes = _homes.ToArray();

            var tempPath = _snapshotPath + ".tmp";
            await using (var handle = _snapshotFs.Open(tempPath, new FileOpenOptions
                     {
                         Access = AccessMode.ReadWrite,
                         Mode = FileOpenMode.OpenOrCreate,   // 介质平权（Truncate 不建文件的介质差异规避）
                         Sharing = FileSharing.None,
                     }))
            {
                handle.SetLength(0);   // 覆写旧 temp（残留截断）
                var writer = new SnapshotWriter(handle);
                writer.Write(MagicBytes);
                writer.WriteInt64(_appliedThrough);
                writer.WriteAddress(begin);
                writer.WriteAddress(tail);
                writer.WriteInt64(ringLength);
                if (ringLength > 0)
                {
                    await using var reader = _port.OpenRingReader(begin, tail);
                    var buffer = new byte[64 << 10];
                    long remaining = ringLength;
                    while (remaining > 0)
                    {
                        int read = await reader.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct)
                            .ConfigureAwait(false);
                        if (read <= 0)
                            throw new InvalidDataException("Ring 快照读取提前结束（区间像不完整）。");
                        await writer.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        remaining -= read;
                    }
                }

                writer.WriteInt32(groups.Count);
                foreach (var name in groups)
                {
                    var (vis, maxRedeliveries) = _port.TryGetGroupConfig(name)
                        ?? throw new InvalidDataException($"组 {name} 配置缺失（导出面不一致）。");
                    writer.WriteName(name);
                    writer.WriteInt64((long)vis.TotalMilliseconds);
                    writer.WriteInt32(maxRedeliveries);
                    var state = _port.GetGroupState(name);
                    writer.WriteAddress(state.Cursor);
                    writer.WriteInt32(state.Skip.Count);
                    foreach (var s in state.Skip)
                        writer.WriteAddress(s);
                    writer.WriteInt32(state.Retry.Count);
                    foreach (var r in state.Retry)
                    {
                        writer.WriteAddress(r.Address);
                        writer.WriteInt32(r.Count);
                    }
                    writer.WriteInt64(state.Epoch);
                    writer.WriteByte(state.Fenced ? (byte)1 : (byte)0);
                }

                writer.WriteInt32(homes.Length);
                foreach (var (name, home) in homes)
                {
                    writer.WriteName(name);
                    writer.WriteNodeId(home);
                }
                writer.Flush();   // fsync——rename 前数据落盘（temp+rename 原子契约）
            }

            if (_snapshotFs.Exists(_snapshotPath))
                _snapshotFs.Delete(_snapshotPath);
            _snapshotFs.Move(tempPath, _snapshotPath, overwrite: true);   // 原子切换（内建父目录刷盘）
            var applied = Interlocked.Read(ref _appliedThrough);
            _logger?.LogInformation("QueueStateMachine 快照导出：S={Applied} groups={Groups} ringBytes={RingBytes}",
                applied, groups.Count, ringLength);
            return applied;
        }
        finally
        {
            _exportGate.Release();
        }
    }

    /// <summary>快照存在性（诊断/测试观测）。</summary>
    /// <returns>true = 快照文件在。</returns>
    public bool SnapshotExists() => _snapshotFs.Exists(_snapshotPath);

    /// <summary>重启导入（Builder warmup 前显式调用——"零新日志"形态下懒导入永不触发的兜底；
    /// 与 apply 经 gate 互斥，幂等）。有快照 = 导入并置 <see cref="AppliedThrough"/>=S；无快照 = no-op。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>导入完成（状态就绪）后完成。</returns>
    public async ValueTask ImportIfPendingAsync(CancellationToken ct = default)
    {
        await _exportGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureImportedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _exportGate.Release();
        }
    }

    /// <summary>重启首条 apply 前懒导入（apply worker 内调用——gate 保证与导出/显式导入互斥）。</summary>
    /// <param name="ct">取消令牌。</param>
    private async ValueTask EnsureImportedAsync(CancellationToken ct)
    {
        if (_imported)
            return;
        _imported = true;
        if (!_snapshotFs.Exists(_snapshotPath))
            return;

        await using var handle = _snapshotFs.Open(_snapshotPath, new FileOpenOptions
        {
            Access = AccessMode.Read,
            Mode = FileOpenMode.OpenExisting,
            Sharing = FileSharing.Read,
        });
        var reader = new SnapshotReader(handle);
        var magic = reader.ReadBytes(MagicBytes.Length);
        if (!magic.AsSpan().SequenceEqual(MagicBytes))
            throw new InvalidDataException("队列快照魔数不符（损坏或异版）——拒绝导入（raft 全量重放兜底需快照缺位，见 spec ⑤）。");

        long s = reader.ReadInt64();
        var begin = reader.ReadAddress();
        var tail = reader.ReadAddress();
        long ringLength = reader.ReadInt64();
        if (ringLength > 0)
        {
            await using var writer = _port.OpenRingWriter(begin, tail);
            var buffer = new byte[64 << 10];
            long remaining = ringLength;
            while (remaining > 0)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct)
                    .ConfigureAwait(false);
                if (read <= 0)
                    throw new InvalidDataException("队列快照 Ring 像截断。");
                await writer.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                remaining -= read;
            }
            writer.Complete();
            await _port.FlushRingAsync(ct).ConfigureAwait(false);   // 导入页落设备——冷读路径可服务
        }

        int groupCount = reader.ReadInt32();
        for (var i = 0; i < groupCount; i++)
        {
            string name = reader.ReadName();
            long visMs = reader.ReadInt64();
            int maxRedeliveries = reader.ReadInt32();
            var cursor = reader.ReadAddress();
            int skipCount = reader.ReadInt32();
            var skip = new LogicalAddress[skipCount];
            for (var j = 0; j < skipCount; j++)
                skip[j] = reader.ReadAddress();
            int retryCount = reader.ReadInt32();
            var retry = new RetryEntry[retryCount];
            for (var j = 0; j < retryCount; j++)
            {
                var addr = reader.ReadAddress();
                retry[j] = new RetryEntry(addr, reader.ReadInt32());
            }
            long epoch = reader.ReadInt64();
            bool fenced = reader.ReadByte() != 0;
            await _port.ApplyGroupRestoreAsync(name, TimeSpan.FromMilliseconds(Math.Max(1, visMs)), maxRedeliveries,
                new QueueGroupState.State(cursor, skip, retry, epoch, fenced), ct).ConfigureAwait(false);
        }

        int homeCount = reader.ReadInt32();
        for (var i = 0; i < homeCount; i++)
        {
            string name = reader.ReadName();
            var home = reader.ReadNodeId();
            _homes[name] = home;
        }

        await _port.ReconcileDelayIndexAsync(ct).ConfigureAwait(false);
        Interlocked.Exchange(ref _appliedThrough, s);
        _logger?.LogInformation("QueueStateMachine 快照导入：S={Applied} groups={Groups} ringBytes={RingBytes}",
            s, groupCount, ringLength);
    }

    private static readonly byte[] MagicBytes = "TQSNAP01"u8.ToArray();

    /// <summary>释放（等待导出 gate 空闲——防导出中途释放端口）。</summary>
    /// <returns>gate 获取并释放后完成。</returns>
    public async ValueTask DisposeAsync()
    {
        await _exportGate.WaitAsync().ConfigureAwait(false);
        _exportGate.Release();
        _exportGate.Dispose();
    }

    // ═══ 快照字节流原语（IFileHandle pwrite/pread——小端定长，布局唯一真源）═══

    private sealed class SnapshotWriter(IFileHandle handle)
    {
        private long _offset;

        public void Write(ReadOnlySpan<byte> bytes)
        {
            handle.Write(_offset, bytes);
            _offset += bytes.Length;
        }

        public void WriteByte(byte b) => Write([b]);

        public void WriteInt32(int v)
        {
            Span<byte> buf = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf, v);
            Write(buf);
        }

        public void WriteInt64(long v)
        {
            Span<byte> buf = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buf, v);
            Write(buf);
        }

        public void WriteAddress(LogicalAddress a)
        {
            WriteInt32(a.SegId);
            WriteInt32(a.Extension);
            WriteInt64(a.Offset);
        }

        public void WriteName(string name)
        {
            WriteInt32(Encoding.UTF8.GetByteCount(name));
            Write(Encoding.UTF8.GetBytes(name));
        }

        public void WriteNodeId(NodeId node)
        {
            var buf = new byte[NodeId.Size];
            node.CopyTo(buf);
            Write(buf);
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
        {
            await handle.WriteAsync(_offset, bytes, ct).ConfigureAwait(false);
            _offset += bytes.Length;
        }

        public void Flush() => handle.Flush();
    }

    private sealed class SnapshotReader(IFileHandle handle) : IAsyncDisposable
    {
        private long _offset;

        public byte[] ReadBytes(int count)
        {
            var buf = new byte[count];
            int read = handle.Read(_offset, buf);
            if (read < count)
                throw new EndOfStreamException($"快照截断：需 {count} 字节，实读 {read}");
            _offset += count;
            return buf;
        }

        public byte ReadByte() => ReadBytes(1)[0];

        public int ReadInt32()
        {
            Span<byte> buf = stackalloc byte[4];
            Fill(buf);
            return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buf);
        }

        public long ReadInt64()
        {
            Span<byte> buf = stackalloc byte[8];
            Fill(buf);
            return System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(buf);
        }

        public LogicalAddress ReadAddress()
        {
            int segId = ReadInt32();
            int ext = ReadInt32();
            long offset = ReadInt64();
            return new LogicalAddress(segId, ext, offset);
        }

        public string ReadName()
        {
            int len = ReadInt32();
            if (len is < 0 or > 512)
                throw new InvalidDataException($"快照组名长度非法：{len}");
            return Encoding.UTF8.GetString(ReadBytes(len));
        }

        public NodeId ReadNodeId() => new(ReadBytes(NodeId.Size));

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            int read = await handle.ReadAsync(_offset, buffer, ct).ConfigureAwait(false);
            _offset += read;
            return read;
        }

        private void Fill(Span<byte> buf)
        {
            int read = handle.Read(_offset, buf);
            if (read < buf.Length)
                throw new EndOfStreamException($"快照截断：需 {buf.Length} 字节，实读 {read}");
            _offset += buf.Length;
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync() => await handle.DisposeAsync().ConfigureAwait(false);
    }
}
