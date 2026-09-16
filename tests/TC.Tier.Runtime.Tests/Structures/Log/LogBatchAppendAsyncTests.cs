using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Shared;
using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Log;
using TC.Tier.Runtime.Structures.Log.Contracts;

namespace TC.Tier.Runtime.Tests.Structures.Log;

/// <summary>
/// 批协议让渡化契约测试——AppendBatch 页满让渡（异步刷盘不阻塞）+ AppendAsync 三档
/// （同步快路 / 让渡同步完成 / 背压真异步 Exit→await→Re-Enter + 孤儿锁认领）。
/// <para>★ 背压确定性：GatedWriteFileSystem 把 WriteAsync 拴在 TCS 上——在途刷盘可被测试
/// 冻结，双页都在途的背压点 100% 复现（mem 介质自然形态下写微秒完成，背压不可稳定观测）。</para>
/// <para>★ 消费形态（C#12）：ref struct 批不能跨 await 持有——背压慢路经 sync GetResult 消费
/// （慢路在续体线程完成，孤儿锁协议让消费方线程重新认领写锁——每跳一致不炸）。</para>
/// </summary>
public class LogBatchAppendAsyncTests
{
    /// <summary>恒不提前提交策略（页契约提交不受影响——排除策略噪声，让时序全确定性）。</summary>
    private sealed class NeverCommitPolicy : ICommitPolicy
    {
        public bool ShouldCommit(in CommitSnapshot snapshot) => false;
    }

    private static EntryLog NewLog(IFileSystem fs, EntryLogSettings settings)
    {
        var log = new EntryLog(fs, settings, new NeverCommitPolicy());
        log.Initialize();
        log.WaitForReady();
        return log;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 同步 Append——页满让渡（内容/地址正确性）
    // ═══════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BatchAppend_PageFullYield_ContentIntactAcrossPages(bool dio)
    {
        var (settings, vol) = dio
            ? TestLogSettingsFactory.CreateEntryDIO(logPageSizeBits: 12)
            : TestLogSettingsFactory.CreateEntry(logPageSizeBits: 12);   // 4KB 页
        try
        {
            using var log = NewLog(vol.Fs, settings);
            var payload = new byte[2000];
            payload.AsSpan().Fill(0xA5);

            LogicalAddress first;
            using (var batch = log.BeginAppendBatch())
            {
                first = default;
                for (var i = 0; i < 7; i++)   // 2 条/页——7 条跨 4 页（多次让渡）
                {
                    var addr = batch.Append(payload);
                    if (i == 0) first = addr;
                }
                Assert.Equal(7, batch.Count);
                Assert.Equal(first, batch.FirstOffset);
            }

            log.Flush();
            Assert.Equal(7, CountReplay(log));
        }
        finally { vol.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // AppendAsync——同步快路 / 让渡同步完成（批不跨 await）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void BatchAppendAsync_SpaceAvailable_CompletesSynchronously()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 12);
        try
        {
            using var log = NewLog(vol.Fs, settings);
            var payload = new byte[100];

            using (var batch = log.BeginAppendBatch())
            {
                var vt = batch.AppendAsync(payload);   // 页有空间——纯 memcpy 快路
                Assert.True(vt.IsCompletedSuccessfully, "页有空间的 AppendAsync 必须同步完成（热路零开销契约）。");
                var addr = vt.IsCompletedSuccessfully ? vt.Result : LogicalAddress.Empty;
                Assert.True(addr != LogicalAddress.Empty);
            }

            log.Flush();
            Assert.Equal(1, CountReplay(log));
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void BatchAppendAsync_MixedSyncAndAsync_AddressesSequential()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 12);
        try
        {
            using var log = NewLog(vol.Fs, settings);
            var payload = new byte[2000];
            payload.AsSpan().Fill(0x3C);

            var addresses = new List<LogicalAddress>();
            using (var batch = log.BeginAppendBatch())
            {
                for (var i = 0; i < 6; i++)
                {
                    var vt = batch.AppendAsync(payload);
                    addresses.Add(vt.IsCompletedSuccessfully ? vt.Result : LogicalAddress.Empty);
                }
            }

            // 地址严格递增（顺序读依赖物理连续性）
            for (var i = 1; i < addresses.Count; i++)
                Assert.True(addresses[i] > addresses[i - 1], $"entry[{i}] 地址必须递增。");

            log.Flush();
            Assert.Equal(6, CountReplay(log));
        }
        finally { vol.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // AppendAsync——背压真异步（GatedWriteFileSystem 冻结在途刷盘；
    // 消费形态 = sync GetResult——孤儿锁协议在消费方线程重新认领）
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void BatchAppendAsync_Backpressure_ReallyAwaits_AndLockReclaimed()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 12);
        var gated = new GatedWriteFileSystem(vol.Fs);
        try
        {
            using var log = NewLog(gated, settings);
            var payload = new byte[2000];   // 2 条/页

            using (var batch = log.BeginAppendBatch())
            {
                _ = batch.Append(payload);            // e1 → 页 A
                _ = batch.Append(payload);            // e2 → 页 A（4032）
                _ = batch.Append(payload);            // e3 → 页满让渡①：WriteAsync 被闸门冻结（在途）
                _ = batch.Append(payload);            // e4 → 页 B

                var vt = batch.AppendAsync(payload);  // e5 → 页 B 满 + 在途①未完成 = 背压（唯一真异步点）
                Assert.False(vt.IsCompleted, "背压下 AppendAsync 必须真异步挂起（在途刷盘未完成）。");

                // ★ 双水位脱钩实锤：在途数据不可读不可提交——FlushedTail 停在已确认写完成边界
                Assert.True(log.FlushedTail < log.TailAddress,
                    "在途刷盘未完成时 FlushedTail（已确认写完成边界）必须落后于写游标 TailAddress。");

                // 解冻（后台）→ 慢路在续体线程完成（drain + 提交链补跑 + 让渡 + 写入 + 锁交还孤儿）
                _ = Task.Run(async () => { await Task.Delay(100); gated.ReleaseWrites(); });
                var addr5 = vt.AsTask().GetAwaiter().GetResult();   // C#12 消费形态：sync 等真异步慢路

                // 孤儿锁在消费方线程重新认领——不炸 SynchronizationLockException
                _ = batch.Append(payload);            // e6
                Assert.Equal(6, batch.Count);
                Assert.True(addr5 != LogicalAddress.Empty);
            }                                         // Dispose：写锁已由上一条 Append 认领，干净退出

            log.Flush();
            Assert.Equal(6, CountReplay(log));
        }
        finally { gated.Dispose(); vol.Dispose(); }
    }

    [Fact]
    public void BatchAppend_Backpressure_SyncAppend_SyncWaitsHonest()
    {
        // 背压下同步 Append = 同步等（诚实阻塞语义）——不挂死、内容完整
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 12);
        var gated = new GatedWriteFileSystem(vol.Fs);
        try
        {
            using var log = NewLog(gated, settings);
            var payload = new byte[2000];

            using (var batch = log.BeginAppendBatch())
            {
                for (var i = 0; i < 4; i++) _ = batch.Append(payload);   // e1-e2 页 A，e3 让渡①（冻结），e4 页 B
                gated.ReleaseWrites();
                _ = batch.Append(payload);   // e5 → 页 B 满 → 背压同步等（在途①此时已解冻）→ 让渡
            }

            log.Flush();
            Assert.Equal(5, CountReplay(log));
        }
        finally { gated.Dispose(); vol.Dispose(); }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 让渡轨水位语义 + 重启正确性
    // ═══════════════════════════════════════════════════════════════════

    [Fact]
    public void BatchAppend_FlushedTail_ConfirmsOnlyCompletedWrites()
    {
        // 让渡轨先行推进写游标；FlushedTail 只在写完成观察点推进——屏障后两者合拢
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 12);
        try
        {
            using var log = NewLog(vol.Fs, settings);
            var payload = new byte[2000];
            using (var batch = log.BeginAppendBatch())
            {
                for (var i = 0; i < 5; i++) _ = batch.Append(payload);
                Assert.True(log.FlushedTail <= log.TailAddress);
            }
            log.Flush();
            Assert.Equal(log.TailAddress, log.FlushedTail);   // 屏障后：写游标 = 已确认写完成边界
            Assert.Equal(5, CountReplay(log));
        }
        finally { vol.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BatchAppend_PageCrossing_SurvivesRestart(bool dio)
    {
        var hints = dio ? FileOpenHints.NoBuffering : FileOpenHints.None;
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(logPageSizeBits: 12, deleteOnClose: false, hints: hints);
        try
        {
            using (var log = NewLog(vol.Fs, TestLogSettingsFactory.EntryOn(vol, "entry", logPageSizeBits: 12, deleteOnClose: false, hints: hints)))
            {
                var payload = new byte[2000];
                payload.AsSpan().Fill(0x77);
                using (var batch = log.BeginAppendBatch())
                {
                    for (var i = 0; i < 7; i++) _ = batch.Append(payload);   // 跨 4 页——多次让渡
                }
                log.Dispose();   // Dispose 链：观察在途让渡（补提交链）+ 刷末页
            }

            // 重开（同卷同引擎名）——让渡写盘的数据必须全部恢复可读
            using (var log2 = NewLog(vol.Fs, TestLogSettingsFactory.EntryOn(vol, "entry", logPageSizeBits: 12, deleteOnClose: false, hints: hints)))
            {
                Assert.Equal(7, CountReplay(log2));
            }
        }
        finally { vol.Dispose(); }
    }

    /// <summary>重放计数（跳过 meta entry——页契约提交链会追加 meta，不占业务条数）。</summary>
    private static int CountReplay(EntryLog log)
    {
        var count = 0;
        log.Replay((payload, isMeta, address) =>
        {
            if (!isMeta) count++;
        });
        return count;
    }
}

/// <summary>
/// 测试装饰器：WriteAsync 闸门——把数据写冻结在 TCS 上，让"在途刷盘"成为测试可控状态
/// （背压/双水位时序的确定性拦截点）。其余成员全部直通内层。
/// </summary>
internal sealed class GatedWriteFileSystem : IFileSystem
{
    private readonly IFileSystem _inner;
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GatedWriteFileSystem(IFileSystem inner) => _inner = inner;

    /// <summary>解冻全部被闸的 WriteAsync（幂等）。</summary>
    public void ReleaseWrites() => _release.TrySetResult();

    public IFileHandle Open(string path, FileOpenOptions options) => new GatedHandle(_inner.Open(path, options), _release);
    public void EnsureRoot() => _inner.EnsureRoot();
    public void FlushRoot() => _inner.FlushRoot();
    public FileSystemCapabilities Capabilities => _inner.Capabilities;
    public VolumeInfo Volume => _inner.Volume;
    public void CreateDirectory(string path) => _inner.CreateDirectory(path);
    public void DeleteDirectory(string path) => _inner.DeleteDirectory(path);
    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
    public void MoveDirectory(string source, string dest) => _inner.MoveDirectory(source, dest);
    public void CreateFile(string path, long preallocateSize = 0, ReadOnlyMemory<byte> extra = default)
        => _inner.CreateFile(path, preallocateSize, extra);
    public bool Exists(string path) => _inner.Exists(path);
    public void Delete(string path) => _inner.Delete(path);
    public void Move(string source, string dest, bool overwrite = false) => _inner.Move(source, dest, overwrite);
    public FsEntryInfo Stat(string path) => _inner.Stat(path);
    public IEnumerable<FsEntry> EnumerateFiles(string pattern = "*", bool recursive = false) => _inner.EnumerateFiles(pattern, recursive);
    public IEnumerable<FsEntry> EnumerateFiles(string path, string pattern, bool recursive = false) => _inner.EnumerateFiles(path, pattern, recursive);
    public IEnumerable<FsEntry> EnumerateDirectories(string pattern = "*", bool recursive = false) => _inner.EnumerateDirectories(pattern, recursive);
    public IEnumerable<FsEntry> EnumerateDirectories(string path, string pattern, bool recursive = false) => _inner.EnumerateDirectories(path, pattern, recursive);
    public IEnumerable<FsEntry> EnumerateEntries(string pattern = "*", bool recursive = false) => _inner.EnumerateEntries(pattern, recursive);
    public IEnumerable<FsEntry> EnumerateEntries(string path, string pattern, bool recursive = false) => _inner.EnumerateEntries(path, pattern, recursive);
    public IDisposable AcquireExclusive(TimeSpan timeout) => _inner.AcquireExclusive(timeout);
    public IDisposable EnterMaintenance(string reason, MaintenanceScope scope, CancellationToken ct = default)
        => _inner.EnterMaintenance(reason, scope, ct);
    public void Dispose() => _inner.Dispose();

    /// <summary>
    /// 句柄装饰器——仅 WriteAsync 过闸，其余直通。
    /// ★ 句柄池契约透传（<c>IPoolAttachable</c> 委托内层）——池按 (path, options) 缓存句柄并
    /// 强转本接口管理借用计数，包装器必须代为应答（附件归内层句柄所有）。
    /// </summary>
    private sealed class GatedHandle(IFileHandle inner, TaskCompletionSource release) : IFileHandle, IPoolAttachable
    {
        public string Path => inner.Path;
        public UnbufferedIoSupport UnbufferedSupport => inner.UnbufferedSupport;
        public long RequiredAlignment => inner.RequiredAlignment;
        public long Position => inner.Position;
        public long Length => inner.Length;
        public long AllocatedSize => inner.AllocatedSize;
        public ReadOnlyMemory<byte> FileExtra => inner.FileExtra;

        HandlePoolAttachment? IPoolAttachable.PoolAttachment => ((IPoolAttachable)inner).PoolAttachment;
        HandlePoolAttachment IPoolAttachable.AttachPool(FileHandlePool pool) => ((IPoolAttachable)inner).AttachPool(pool);
        void IPoolAttachable.CloseUnderlying() => ((IPoolAttachable)inner).CloseUnderlying();

        public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken ct)
        {
            await release.Task.ConfigureAwait(false);   // ★ 闸门——在途刷盘冻结点
            await inner.WriteAsync(offset, source, ct).ConfigureAwait(false);
        }

        public void Write(long offset, ReadOnlySpan<byte> source) => inner.Write(offset, source);
        public int Read(long offset, Span<byte> destination) => inner.Read(offset, destination);
        public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken ct) => inner.ReadAsync(offset, destination, ct);
        public long Append(ReadOnlySpan<byte> source) => inner.Append(source);
        public ValueTask<long> AppendAsync(ReadOnlyMemory<byte> source, CancellationToken ct) => inner.AppendAsync(source, ct);
        public long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public void Preallocate() => inner.Preallocate();
        public void SetLength(long length) => inner.SetLength(length);
        public void PunchHole(long offset, long length) => inner.PunchHole(offset, length);
        public IReadOnlyCollection<(long Start, long End)> EnumerateAllocatedRanges() => inner.EnumerateAllocatedRanges();
        public void CollapseRange(long offset, long length) => inner.CollapseRange(offset, length);
        public void InsertRange(long offset, long length) => inner.InsertRange(offset, length);
        public long CopyRange(IFileHandle destination, long sourceOffset, long destinationOffset, long length)
            => inner.CopyRange(destination, sourceOffset, destinationOffset, length);
        public long CloneRange(IFileHandle destination) => inner.CloneRange(destination);
        public void WriteVector(long offset, ReadOnlySpan<ReadOnlyMemory<byte>> sources) => inner.WriteVector(offset, sources);
        public ValueTask WriteVectorAsync(long offset, ReadOnlyMemory<ReadOnlyMemory<byte>> sources, CancellationToken ct)
            => inner.WriteVectorAsync(offset, sources, ct);
        public int ReadVector(long offset, ReadOnlySpan<Memory<byte>> destinations) => inner.ReadVector(offset, destinations);
        public ValueTask<int> ReadVectorAsync(long offset, Memory<Memory<byte>> destinations, CancellationToken ct)
            => inner.ReadVectorAsync(offset, destinations, ct);
        public void Flush() => inner.Flush();
        public void FlushData() => inner.FlushData();
        public void Advise(FileAdvise advise) => inner.Advise(advise);
        public void Lock(long offset, long length, FileLockMode mode) => inner.Lock(offset, length, mode);
        public bool TryLock(long offset, long length, FileLockMode mode) => inner.TryLock(offset, length, mode);
        public void Unlock(long offset, long length) => inner.Unlock(offset, length);
        public IMappedSection Map(long offset, long length, AccessMode access) => inner.Map(offset, length, access);
        public int ReadFileExtra(long offset, Span<byte> destination) => inner.ReadFileExtra(offset, destination);
        public void WriteFileExtra(long offset, ReadOnlySpan<byte> data) => inner.WriteFileExtra(offset, data);
        public void SetFileExtra(ReadOnlyMemory<byte> extra) => inner.SetFileExtra(extra);
        public void Dispose() => inner.Dispose();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
