using System.Buffers;
using System.Runtime.InteropServices;

namespace TC.Tier.Core.IO.Remote;
/// <summary>
/// 远程物化映射（G11——medium-protocol-and-parity-design §5.12）：
/// <para>★ Read = Range GET 整段快照——纯快照零写回（与 mem Sparse RO 映射同构）；</para>
/// <para>★ ReadWrite = staging 视图（物化副本）：视图写在 Flush/Dispose <b>无条件全量写回</b> staging
///   （Memory&lt;byte&gt; 无法拦截写——脏标记不可靠，与 mem Sparse 同判）；持久化上传由父句柄 Flush 承担
///   （写穿透契约：视图写 Flush 才上传）。</para>
/// <para>★ 生命周期：父句柄关闭后视图访问抛 <see cref="ObjectDisposedException"/>（staging 已丢弃——
///   未 Flush 的视图写随句柄丢弃，与"未 fsync 即丢"同语义）。</para>
/// </summary>
internal sealed class RemoteMappedSection : IMappedSection
{
    private readonly RemoteFileHandle _handle;
    private readonly long _offset;
    private readonly bool _readOnly;
    private byte[] _copy;
    private ViewManager? _manager;
    private Memory<byte> _memory;
    private int _disposed;

    internal RemoteMappedSection(RemoteFileHandle handle, long offset, byte[] snapshot, bool readOnly)
    {
        _handle = handle;
        _offset = offset;
        _copy = snapshot;
        _readOnly = readOnly;
        _manager = new ViewManager(this);
        _memory = _manager.Memory;
    }

    /// <inheritdoc/>
    public Memory<byte> View
    {
        get
        {
            ThrowIfUnusable();
            return _memory;
        }
    }

    /// <inheritdoc/>
    /// <param name="advise">访问提示（本实现 no-op——仅做存活性校验）。</param>
    public void Advise(FileAdvise advise)
    {
        ThrowIfUnusable();   // no-op（映射级提示）
    }

    /// <inheritdoc/>
    public void Flush()
    {
        ThrowIfUnusable();
        WriteBack();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (!_readOnly)
            WriteBack();   // Dispose 写回（写穿透契约收尾——无条件，脏标记不可靠）
        _manager = null;
        _copy = null!;
    }

    private void WriteBack()
    {
        if (_readOnly) return;
        _handle.WriteBackFromMap(_offset, _copy);
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_handle.IsClosed)
            throw new ObjectDisposedException(nameof(RemoteFileHandle), "父句柄已关闭——映射视图失效（未 Flush 的视图写已丢弃）。");
    }

    /// <summary>副本视图（Dispose/句柄关闭后访问抛 <see cref="ObjectDisposedException"/>——不返回悬垂 Memory）。</summary>
    private sealed class ViewManager(RemoteMappedSection owner) : MemoryManager<byte>
    {
        /// <summary>暴露物化副本的内存视图。</summary>
        /// <returns>覆盖整个映射区间的 <see cref="Span{T}"/>。</returns>
        public override Span<byte> GetSpan()
        {
            owner.ThrowIfUnusable();
            return owner._copy;
        }

        /// <summary>钉住物化副本（托管数组——真 GCHandle 钉定）。</summary>
        /// <param name="elementIndex">起始元素索引（0 基）。</param>
        /// <returns>指向该元素的 <see cref="MemoryHandle"/>（Dispose 自动释放 GCHandle）。</returns>
        public override unsafe MemoryHandle Pin(int elementIndex = 0)
        {
            owner.ThrowIfUnusable();
            // 托管数组必须真钉住（GC 可移动）——MemoryHandle.Dispose 自动释放 GCHandle
            var gc = GCHandle.Alloc(owner._copy, GCHandleType.Pinned);
            return new MemoryHandle((byte*)gc.AddrOfPinnedObject() + elementIndex, gc, null);
        }

        /// <summary>解除固定——GCHandle 由 <see cref="MemoryHandle.Dispose"/> 自管理，no-op。</summary>
        public override void Unpin()
        {
        }

        /// <summary>实际释放在 owner.Dispose 统一执行——no-op。</summary>
        /// <param name="disposing">true = 显式释放；false = 终结器。</param>
        protected override void Dispose(bool disposing)
        {
            // 实际释放在 owner.Dispose
        }
    }
}
