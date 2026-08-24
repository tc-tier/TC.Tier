using System.Buffers;
using System.Runtime.InteropServices;

namespace TC.Tier.Core.IO.Mem;

/// <summary>
/// Reserved 模式映射——槽直址零拷贝（视图即文件内存：视图写=文件写，实时可见——写穿透天然成立）。
/// <para>★ 生命周期独立：Pin 槽 SlotData（Refs++——Grow 换租后旧 buffer 延迟到本映射 Dispose 才归还）。</para>
/// <para>★ Dispose 后访问 View / 卷拔盘后访问视图均抛 <see cref="ObjectDisposedException"/>（无悬垂窗口）。</para>
/// </summary>
internal sealed unsafe class MemDirectMappedSection : IMappedSection
{
    private readonly MemoryFileSystem _fs;
    private readonly int _slotIdx;
    private readonly long _offset;
    private readonly long _length;
    private readonly MemoryFileSystem.SlotData _data;
    private readonly UnmanagedViewManager _manager;
    private readonly Memory<byte> _memory;
    private int _disposed;

    private MemDirectMappedSection(MemoryFileSystem fs, int slotIdx, long offset, long length,
        MemoryFileSystem.SlotData data)
    {
        _fs = fs;
        _slotIdx = slotIdx;
        _offset = offset;
        _length = length;
        _data = data;
        MemoryFileSystem.PinSlotData(data);
        _manager = new UnmanagedViewManager(this);
        _memory = _manager.Memory;
    }

    internal static MemDirectMappedSection Create(MemoryFileSystem fs, int slotIdx, long offset, long length,
        AccessMode access, string path)
    {
        // ★ ReadOnly 在直址模式下不可强制（Memory<byte> 无法只读化）——文档纪律：可移植消费者不得依赖
        //   ReadOnly 直址视图的写抑制（写仍会落文件）；需要真只读语义用 Sparse 卷物化快照。
        lock (fs.SyncRoot)
        {
            fs.RegisterMap(slotIdx);
            var data = fs.GetSlot(slotIdx).Data
                       ?? throw new FileIOException(IOError.NotFound, "文件数据缺失。", path, "Map");
            return new MemDirectMappedSection(fs, slotIdx, offset, length, data);
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
    public void Advise(FileAdvise advise)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);   // no-op（mem 无页缓存概念）
    }

    /// <inheritdoc/>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);   // 直址即文件——无需刷回
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _fs.UnpinSlotData(_data);
        _fs.ReleaseMap(_slotIdx);
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_fs.IsDisposed)
            throw new ObjectDisposedException(nameof(MemoryFileSystem), "内存卷已拔盘——映射视图失效。");
    }

    private sealed class UnmanagedViewManager(MemDirectMappedSection owner) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan()
        {
            owner.ThrowIfUnusable();
            return new Span<byte>(owner._data.Ptr + owner._offset, (int)owner._length);
        }

        public override MemoryHandle Pin(int elementIndex = 0)
        {
            owner.ThrowIfUnusable();
            return new MemoryHandle(owner._data.Ptr + owner._offset + elementIndex);
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
            // 实际释放在 owner.Dispose
        }
    }
}

/// <summary>
/// Sparse 模式映射——物化拷贝（创建时全量快照）。
/// <para>★ 写穿透契约（ReadWrite）：视图写在 Flush/Dispose 时写回文件——可见时点=Flush/Dispose 非实时
///   （与磁盘"视图写立即可读"的可见性时差在 io.md 差异声明）；ReadOnly 纯快照零写回。
///   Memory&lt;byte&gt; 无法拦截写——可写映射的 Flush/Dispose <b>无条件全量写回</b>（脏标记不可靠）。</para>
/// <para>★ 空间操作同步应用（平权契约）：fs 的 PunchHole/截断/整理在 fs 锁内回调本类的
///   ZeroRange/TruncateTo/RebuildFrom——副本与文件保持一致（否则"打洞后映射读到旧数据"= 平权破绽）。</para>
/// </summary>
internal sealed class MemSparseMappedSection : IMappedSection
{
    private readonly MemoryFileSystem _fs;
    private readonly int _slotIdx;
    private readonly int _gen;
    private readonly long _offset;
    private readonly bool _readOnly;
    private byte[] _copy;
    private ArrayViewManager? _manager;
    private Memory<byte> _memory;
    private int _disposed;

    private MemSparseMappedSection(MemoryFileSystem fs, int slotIdx, int gen, long offset,
        byte[] snapshot, bool readOnly)
    {
        _fs = fs;
        _slotIdx = slotIdx;
        _gen = gen;
        _offset = offset;
        _copy = snapshot;
        _readOnly = readOnly;
        _manager = new ArrayViewManager(this);
        _memory = _manager.Memory;
    }

    internal static MemSparseMappedSection Create(MemoryFileSystem fs, int slotIdx, int gen, long offset,
        long length, AccessMode access, string path)
    {
        var snapshot = fs.MaterializeSparse(slotIdx, gen, offset, length);
        var section = new MemSparseMappedSection(fs, slotIdx, gen, offset, snapshot, access == AccessMode.Read);
        lock (fs.SyncRoot)
        {
            fs.RegisterMap(slotIdx);
            fs.RegisterMaterializedMap(slotIdx, section);
        }
        return section;
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
    public void Advise(FileAdvise advise)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);   // no-op
    }

    /// <inheritdoc/>
    public void Flush() => WriteBack();

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!_readOnly)
            WriteBack();   // Dispose 写回（写穿透契约收尾——无条件，脏标记不可靠）
        _manager = null;
        _fs.ReleaseMap(_slotIdx);
    }

    private void WriteBack()
    {
        if (_readOnly) return;
        try
        {
            _fs.WriteBackSparse(_slotIdx, _gen, _offset, _copy);
        }
        catch (FileIOException ex) when (ex.Error == IOError.NotFound)
        {
            // 文件已被删除/替换——写回无处落地（POSIX mmap 后 unlink 的等价语义：数据保留在映射）
        }
        catch (ObjectDisposedException)
        {
            // 卷已拔盘——无处写回（视图数据随映射消亡）
        }
    }

    // ── fs 锁内回调（空间操作同步应用）──

    internal void ZeroRange(long offset, long length)
    {
        var relStart = offset - _offset;
        var relEnd = relStart + length;
        if (relEnd <= 0 || relStart >= _copy.Length) return;
        var start = (int)Math.Max(0, relStart);
        var end = (int)Math.Min(_copy.Length, relEnd);
        _copy.AsSpan(start, end - start).Clear();
    }

    internal void TruncateTo(long newLength)
    {
        var visible = (int)Math.Min(_copy.Length, Math.Max(0, newLength - _offset));
        if (visible == _copy.Length) return;
        Array.Resize(ref _copy, Math.Max(0, visible));
        RebuildMemory();
    }

    internal void RebuildFrom(byte[] newContent)
    {
        var visible = (int)Math.Min(_copy.Length, Math.Max(0, newContent.Length - (int)_offset));
        var resized = new byte[Math.Max(0, visible)];
        if (visible > 0)
            newContent.AsSpan((int)_offset, visible).CopyTo(resized);
        _copy = resized;
        RebuildMemory();
    }

    /// <summary>副本尺寸变更后重建视图（TruncateTo/RebuildFrom 调用——持有旧 Memory 的消费者自然失效）。</summary>
    private void RebuildMemory()
    {
        if (_manager is not null)
            _memory = _manager.Memory;   // manager 从 _copy 现取——Memory 惰性求值
    }

    /// <summary>副本视图（Dispose/拔盘后访问抛 <see cref="ObjectDisposedException"/>——不返回悬垂 Memory）。</summary>
    private sealed class ArrayViewManager(MemSparseMappedSection owner) : MemoryManager<byte>
    {
        public override Span<byte> GetSpan()
        {
            owner.ThrowIfUnusable();
            return owner._copy;
        }

        public override unsafe MemoryHandle Pin(int elementIndex = 0)
        {
            owner.ThrowIfUnusable();
            // 托管数组必须真钉住（GC 可移动）——MemoryHandle.Dispose 自动释放 GCHandle
            var gc = GCHandle.Alloc(owner._copy, GCHandleType.Pinned);
            return new MemoryHandle((byte*)gc.AddrOfPinnedObject() + elementIndex, gc, null);
        }

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
            // 实际释放在 owner.Dispose
        }
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_fs.IsDisposed)
            throw new ObjectDisposedException(nameof(MemoryFileSystem), "内存卷已拔盘——映射视图失效。");
    }
}
