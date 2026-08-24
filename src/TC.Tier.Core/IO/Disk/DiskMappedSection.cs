using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.Disk;

/// <summary>
/// 磁盘内存映射区——手工映射路径（Win=CreateFileMappingW/MapViewOfFile，Unix=mmap）。
/// <para>★ 为什么不走 BCL <c>MemoryMappedFile.CreateFromFile(FileStream)</c>：父句柄以
///   FILE_FLAG_OVERLAPPED 打开，复刻句柄同为 overlapped——BCL FileStream 对复刻句柄的 IOCP 绑定
///   （SafeFileHandle.InitThreadPoolBinding）在 DuplicateHandle 句柄上失败；手工路径完全不牵涉 IOCP。</para>
/// <para>★ 生命周期独立于父句柄：持有复刻的 OS 句柄（Win=DuplicateHandle / Unix=dup——同一 file object，
///   独立关闭互不影响），父句柄 Dispose/池淘汰不产生野视图。必须 Dispose（fd/section 泄漏）。</para>
/// <para>★ 映射偏移对齐：MapViewOfFile 要求 offset 对齐系统分配粒度（64K），mmap 要求页对齐——
///   实现向下对齐后映射放大窗口，View 指针 = 基址 + 差值。</para>
/// <para>★ View 越界 = 崩溃（AV，unsafe 语义，文档警示）。</para>
/// </summary>
internal sealed unsafe class DiskMappedSection : IMappedSection
{
    private readonly string _path;
    private readonly ILogger? _logger;
    private readonly SafeFileHandle _dup;       // 拥有复刻句柄
    private readonly byte* _view;               // View 起点（= 映射基址 + 对齐差值）
    private readonly long _length;
    private readonly nint _section;             // Win section 句柄（Unix = 0）
    private readonly byte* _mapBase;            // 映射基址（Unmap 用）
    private readonly long _mapLength;
    private readonly UnmanagedViewManager _manager;
    private readonly Memory<byte> _memory;
    private int _disposed;

    internal DiskMappedSection(SafeFileHandle duplicatedHandle, long offset, long length,
        AccessMode access, string path, ILogger? logger)
    {
        _path = path;
        _logger = logger;
        _dup = duplicatedHandle;
        _length = length;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Win：offset 须对齐到系统分配粒度（64K）——向下对齐 + 放大窗口
                const long granularity = 1L << 16;
                var mapOffset = offset & ~(granularity - 1);
                var delta = (int)(offset - mapOffset);
                _mapLength = length + delta;
                _mapBase = (byte*)MapWin(access, mapOffset, _mapLength, out _section);
                _view = _mapBase + delta;
            }
            else
            {
                // Unix：mmap 的 offset 须页对齐
                var pageSize = Environment.SystemPageSize;
                var mapOffset = offset & ~(long)(pageSize - 1);
                var delta = (int)(offset - mapOffset);
                _mapLength = length + delta;
                _mapBase = MapUnix(access, mapOffset, _mapLength);
                _view = _mapBase + delta;
            }
            _manager = new UnmanagedViewManager(this);
            _memory = _manager.Memory;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReleaseResources();
            throw ex.Wrap("Map", path);
        }
    }

    private byte* MapWin(AccessMode access, long mapOffset, long mapLength, out nint section)
    {
        var protect = access == AccessMode.Read ? Kernel32.PageReadOnly : Kernel32.PageReadWrite;
        section = Kernel32.CreateFileMapping(_dup, 0, protect,
            (uint)((ulong)mapLength >> 32), (uint)((ulong)mapLength & 0xFFFFFFFF), 0);
        if (section == 0)
        {
            var e = Marshal.GetLastWin32Error();
            throw new IOException($"CreateFileMappingW failed, error={e}.");
        }
        var desired = access == AccessMode.Read ? Kernel32.FileMapRead : Kernel32.FileMapReadWrite;
        var map = Kernel32.MapViewOfFile(section, desired,
            (uint)((ulong)mapOffset >> 32), (uint)((ulong)mapOffset & 0xFFFFFFFF), (nuint)mapLength);
        if (map == 0)
        {
            var e = Marshal.GetLastWin32Error();
            throw new IOException($"MapViewOfFile failed, error={e}.");
        }
        return (byte*)map;
    }

    private byte* MapUnix(AccessMode access, long mapOffset, long mapLength)
    {
        var borrowed = false;
        try
        {
            _dup.DangerousAddRef(ref borrowed);
            var fd = _dup.DangerousGetHandle().ToInt32();
            var prot = LibC.ProtRead | (access == AccessMode.Read ? 0 : LibC.ProtWrite);
            var p = LibC.Mmap(null, (nuint)mapLength, prot, LibC.MapShared, fd, mapOffset);
            if (p == (void*)-1)
            {
                var errno = Marshal.GetLastPInvokeError();
                throw new IOException($"mmap failed, errno={errno}.");
            }
            return (byte*)p;
        }
        finally
        {
            if (borrowed) _dup.DangerousRelease();
        }
    }

    /// <inheritdoc/>
    public Memory<byte> View
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            return _memory;
        }
    }

    /// <inheritdoc/>
    /// <remarks>★ 手工路径无 view 级 madvise 暴露——Windows/Unix 均 no-op（文档化回退）。</remarks>
    public void Advise(FileAdvise advise)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
    }

    /// <inheritdoc/>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!Kernel32.FlushViewOfFile((nint)_mapBase, (nuint)_mapLength))
                {
                    var e = Marshal.GetLastWin32Error();
                    throw new IOException($"FlushViewOfFile failed, error={e}.");
                }
            }
            else
            {
                if (LibC.Msync(_mapBase, (nuint)_mapLength, LibC.MsSync) != 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    throw new IOException($"msync failed, errno={errno}.");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw ex.Wrap("MappedSection.Flush", _path); }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        ReleaseResources();
    }

    private void ReleaseResources()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (_mapBase != null) _ = Kernel32.UnmapViewOfFile((nint)_mapBase);
                if (_section != 0) _ = Kernel32.CloseHandle(_section);
            }
            else if (_mapBase != null)
            {
                _ = LibC.Munmap(_mapBase, (nuint)_mapLength);
            }
            _dup.Dispose();
        }
        catch (Exception ex)
        { _logger?.LogWarning(ex, "DiskMappedSection dispose failed path={Path}", _path); }
    }

    /// <summary>
    /// 非托管内存视图的 <see cref="MemoryManager{Byte}"/>——持 (view, length) 暴露 Span/Memory。
    /// Dispose 后访问抛 <see cref="ObjectDisposedException"/>（不返回悬垂 Memory）。
    /// </summary>
    private sealed class UnmanagedViewManager(DiskMappedSection owner) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref owner._disposed) != 0, owner);
            return new Span<byte>(owner._view, (int)owner._length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref owner._disposed) != 0, owner);
            return new MemoryHandle(owner._view + elementIndex);
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
            // 实际释放在 owner.Dispose——manager 仅是视图出口
        }
    }
}
