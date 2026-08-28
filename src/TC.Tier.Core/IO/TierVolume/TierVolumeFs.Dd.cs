using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TC.Tier.Core.IO.Image;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.TierVolume;

public sealed partial class TierVolumeFs
{
    // ═══════════════ IContiguousVolume（dd 快道——§6.2）═══════════════

    /// <summary>整卷原始字节视图（维护租约内由管线调用——载体访问不出实例）。</summary>
    Stream IContiguousVolume.OpenVolumeBacking(bool writable)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (writable && _readOnly)
            throw new FileIOException(IOError.ReadOnlyVolume, "只读卷不接受可写载体视图", null, "OpenVolumeBacking");
        if (_snapshotMount)
            throw new FileIOException(IOError.Unsupported,
                "快照挂载不支持载体直视（载体 = 活卷现状，非冻结态——V2 §1.1）", null, "OpenVolumeBacking");
        return new VolumeBackingStream(this, (long)(_sb.CapacityBlocks * _sb.BlockSize));
    }

    /// <summary>
    /// 单连续 Written 区间的载体 MMF 直映射（文件载体）——缓存一致性：Map 前排干脏页 + 该文件全部块退出页缓存
    /// （视图 IO 经 OS 页缓存直达载体——视图写入后我们再读经载体可见）。
    /// </summary>
    internal IMappedSection CreateBackingMap(Extent ext, long offset, long length, AccessMode access, string path)
    {
        FlushDirtyPages();
        InvalidateEntryCacheBlocks(ext);
        // ★ V2 §1.2：可写 Map 直落载体不经写路径——增量窗口不可追踪（诚实：拒导至下次检查点）
        if (access != AccessMode.Read)
            _deltaDirtyComplete = false;

        // RM-04：extent 所属成员路由（跨成员 extent 不能 MMF——映射须单成员单文件）
        var m = MemberForBlock(ext.PhysicalBlock);
        if (ext.PhysicalBlock + (ulong)(ext.Length / _pageSize) > m.BaseBlock + m.Info.CapacityBlocks)
            throw new FileIOException(IOError.Unsupported,
                "跨成员区间不支持 Map（MMF 单文件语义）——整理后重试", path, "Map");
        var fileAccess = access == AccessMode.Read ? FileAccess.Read : FileAccess.ReadWrite;
        var stream = new FileStream(m.Carrier.Path, FileMode.Open, fileAccess, FileShare.ReadWrite);
        // ★ MMF 节访问须与句柄访问一致：只读句柄（GENERIC_READ）建 PAGE_READWRITE 节在 Windows 上
        //   必得 ERROR_ACCESS_DENIED（CreateFileMapping 权限要求）——ReadOnly Map 曾在此确定性抛
        //   UnauthorizedAccessException（满套验证时暴露，TierVolumeIntegrity D8/TierVolumeWriteBack 同根因）。
        var mmfAccess = access == AccessMode.Read ? MemoryMappedFileAccess.Read : MemoryMappedFileAccess.ReadWrite;
        var mmf = MemoryMappedFile.CreateFromFile(fileStream: stream, mapName: null, capacity: 0L, access: mmfAccess, inheritability: HandleInheritability.None, leaveOpen: false);
        // 视图偏移对齐（Win=64K 分配粒度 / Unix=页）——CreateViewAccessor 会向下取整，指针差值补偿
        var viewOffset = (long)((ext.PhysicalBlock - m.BaseBlock) * (ulong)_pageSize) + (offset - ext.LogicalStart);
        var granularity = OperatingSystem.IsWindows() ? 65536L : 4096L;
        var aligned = viewOffset / granularity * granularity;
        var delta = (int)(viewOffset - aligned);
        var accessor = mmf.CreateViewAccessor(aligned, length + delta, access == AccessMode.Read
            ? MemoryMappedFileAccess.Read
            : MemoryMappedFileAccess.ReadWrite);
        return new TierVolumeMappedSection(accessor, stream, mmf, this, ext, (int)length, delta);
    }

    /// <summary>该区间覆盖的全部物理块退出页缓存（Map 一致性——先排干后失效，脏页已清空）。</summary>
    private void InvalidateEntryCacheBlocks(Extent ext)
        => InvalidateCacheBlocks(ext.PhysicalBlock, (uint)((ext.Length + _pageSize - 1) / _pageSize));

    /// <summary>区间是否完整落单成员（MMF 单文件语义前提——D8）。</summary>
    internal bool ExtentWithinSingleMember(Extent ext)
    {
        var m = MemberForBlock(ext.PhysicalBlock);
        return ext.PhysicalBlock + (ulong)(ext.Length / _pageSize) <= m.BaseBlock + m.Info.CapacityBlocks;
    }

    /// <summary>Map 视图关闭回调——区间块再失效（末次写入可见性收口；DIO 载体 fsync 后主句柄读可见 MMF 写入）。</summary>
    internal void OnMapClosed(Extent ext)
    {
        InvalidateEntryCacheBlocks(ext);
        FlushCarrier();   // msync 后 fsync——O_DIRECT 主句柄读经设备（MMF 写入必须已达设备）
    }

    /// <summary>TierVolume 映射区——MemoryMappedViewAccessor 包装（View=指针 MemoryManager 零拷贝）。</summary>
    private sealed class TierVolumeMappedSection : IMappedSection
    {
        private readonly MemoryMappedViewAccessor _accessor;
        private readonly FileStream _stream;
        private readonly MemoryMappedFile _mmf;
        private readonly TierVolumeFs _fs;
        private readonly Extent _ext;
        private readonly PointerMemoryManager _manager;
        private readonly int _viewLength;   // 请求长度（视图物理窗口含对齐差值——View 按请求切片）
        private readonly int _delta;         // 对齐差值（指针基址到请求起点的偏移）
        private int _disposed;

        internal TierVolumeMappedSection(MemoryMappedViewAccessor accessor, FileStream stream, MemoryMappedFile mmf,
            TierVolumeFs fs, Extent ext, int viewLength, int delta)
        {
            _delta = delta;
            _accessor = accessor;
            _stream = stream;
            _mmf = mmf;
            _fs = fs;
            _ext = ext;
            _viewLength = viewLength;
            _manager = CreateManager();
        }

        private unsafe PointerMemoryManager CreateManager()
        {
            byte* ptr = null;
            _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            return new PointerMemoryManager(ptr, checked((int)_accessor.SafeMemoryMappedViewHandle.ByteLength));
        }

        public Memory<byte> View
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                return _manager.Memory.Slice(_delta, _viewLength);   // 差值补偿 + 按请求长度暴露
            }
        }

        public void Advise(FileAdvise advise) { /* no-op（映射级提示——v1 未接） */ }

        public void Flush()
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _accessor.Flush();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // DIO 载体一致性：MMF 脏页经 OS 页缓存，O_DIRECT 主句柄读绕过——关闭前排干（msync）+ fsync
            // 后 O_DIRECT 读可见视图写入（缓冲载体无害、语义同构）
            try { _accessor.Flush(); } catch { /* 尽力 */ }
            _manager.Dispose();
            _accessor.Dispose();
            _mmf.Dispose();
            _stream.Dispose();
            _fs.OnMapClosed(_ext);
        }
    }

    /// <summary>原生指针 MemoryManager（MMF 视图零拷贝 View）。</summary>
    private sealed unsafe class PointerMemoryManager(byte* ptr, int length) : MemoryManager<byte>
    {
        private readonly byte* _ptr = ptr;
        private readonly int _length = length;
        private int _disposed;

        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return new Span<byte>(_ptr, _length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            if (elementIndex < 0 || elementIndex >= _length) throw new ArgumentOutOfRangeException(nameof(elementIndex));
            return new MemoryHandle(_ptr + elementIndex);
        }

        public override void Unpin() { }

        public void Dispose() => _disposed = 1;   // MemoryManager 的 Dispose 是显式接口实现——自持 public 出口

        protected override void Dispose(bool disposing) { _disposed = 1; }
    }

    /// <summary>镜像后重载：内存元数据/页缓存清空并从盘重建（管线维护租约内调用——§6.2）。</summary>
    void IContiguousVolume.OnMirrorCompleted()
    {
        lock (MetadataLock)
        {
            _entries.Clear();
            _sortedKeys.Clear();   // RM-11 索引维护
            _directories.Clear();
            _journalReserveBlocks.Clear();
            _pages.Clear();
            _dirtyPages.Clear();   // 脏页索引同步清（漏清 = 陈旧 Page 引用滞留——后续排干会写已重分配块）
            ReturnRecordBuffers(_pendingRecords);   // RM-30：作废记录缓冲归还
            _pendingRecords.Clear();   // 镜像前在途记录作废（盘上日志随 LoadAndRecover 重放重建）
            // D1b：镜像重载后回收队列清空（盘上状态重建——旧批次无意义）。
            // _retireSeq/_safeBatch/_bumpPending 保持单调不复位：在途 bump 回调按旧批次推进无害，
            // 后续新回收取更高批次号走常规协议（过早复位会使陈旧回调把安全批次推高到未保护批次之上）。
            _retiredBlocks.Clear();
            while (_prefetchQueue.TryDequeue(out _)) { }   // 性能债 6：陈旧物理块预取作废（镜像重载后布局全变）
            Interlocked.Exchange(ref _dirtyBytes, 0);
            while (_lru.TryDequeue(out _)) { }
            Interlocked.Exchange(ref _pageBytes, 0);
            LoadAndRecover();
        }
    }

    /// <summary>载体直视流（Position 驱动的定位读写——维护租约内独占使用）。
    /// 经 fs 对齐通道（O_DIRECT 载体下未对齐访问自动弹跳——RM-05 DIO 纪律）。</summary>
    private sealed class VolumeBackingStream(TierVolumeFs fs, long length) : Stream
    {
        private readonly TierVolumeFs _fs = fs;
        private readonly long _length = length;
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, (int)Math.Max(0, _length - _position));
            if (n <= 0) return 0;
            _fs.ReadCarrierExactly(_position, buffer.AsSpan(offset, n));
            _position += n;
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _fs.WriteCarrier(_position, buffer.AsSpan(offset, count));
            _position += count;
        }

        public override long Seek(long offset, SeekOrigin origin)
            => _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };

        public override void Flush() => _fs.FlushCarrier();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

}
