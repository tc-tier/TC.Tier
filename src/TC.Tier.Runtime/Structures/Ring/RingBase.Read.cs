using System.Buffers;
using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase 读写原语 partial——引擎读 + 冷页回源（ClockCache 缓存）。
/// <para>★ 新模型（base.md §2.4 + spec §3.3）：冷页读走 engine.Read(LogicalAddress) + PageFrame 身份校验。</para>
/// <para>★ 集中管理所有设备读路径：ReadDevicePage(同步)/ReadDevicePageAsync(异步) +
///   LoadColdPage(同步)/LoadColdPageAsync(异步)。</para>
/// <para>★ 冷页缓存用 _pagePool 池化 AlignedMemoryManager——native 内存回池复用，零 GC 压力。</para>
/// </summary>
public abstract partial class RingBase<TKey>
{
    /// <summary>★ 读引擎页（同步，LogicalAddress）。扫描游标读冷数据用——不经页池，直接走引擎。</summary>
    private protected int ReadDevicePage(LogicalAddress addr, Span<byte> dest) => _engine.Read(addr, dest);

    /// <summary>★ 异步读引擎页。扫描游标读冷数据用。</summary>
    private protected ValueTask<int> ReadDevicePageAsync(LogicalAddress addr, Memory<byte> dest, CancellationToken ct = default)
        => _engine.ReadAsync(addr, dest, ct);

    /// <summary>
    /// ★ 冷页回源——从引擎读整页到 ClockCache，返回页 pinned native 内存（AlignedMemoryManager）。
    /// <para>★ ClockCache 命中则直接返回缓存页（~20ns）；miss 则 ReadDevicePage 读整页后放入缓存。</para>
    /// <para>★ 用页 LogicalAddress 作 cache key（大小无关，照 base.md §2.2）。</para>
    /// </summary>
    private protected AlignedMemoryManager LoadColdPage(LogicalAddress pageAddr)
    {
        if (_coldPageCache!.TryGet(pageAddr, out var cached))
            return cached;   // 缓存命中

        var pageMem = _pagePool.RentAligned(PageSize, SectorSize);
        ReadDevicePage(pageAddr, pageMem.GetSpan(0, PageSize));
        _coldPageCache.Put(pageAddr, pageMem);
        return pageMem;
    }

    /// <summary>★ 异步冷页回源——async 版 LoadColdPage，用 ReadDevicePageAsync。</summary>
    private protected async ValueTask<AlignedMemoryManager> LoadColdPageAsync(LogicalAddress pageAddr, CancellationToken ct)
    {
        if (_coldPageCache!.TryGet(pageAddr, out var cached))
            return cached;

        var pageMem = _pagePool.RentAligned(PageSize, SectorSize);
        await ReadDevicePageAsync(pageAddr, pageMem.Memory, ct).ConfigureAwait(false);
        _coldPageCache.Put(pageAddr, pageMem);
        return pageMem;
    }

    // === 通用冷区读机制（Codec 无关——子类算好 totalLen 后调基类读字节）===

    /// <summary>★ 冷区读 thread-static buffer（max-seen 只增不减，对齐 AllocatorBase s_dioStaging 范式）。</summary>
    [ThreadStatic] private static byte[]? _sColdRecordBuf;

    /// <summary>★ 租用 thread-static buffer（至少 minSize 字节，不够则扩容）。</summary>
    private protected static byte[] RentColdRecordBuf(int minSize)
    {
        if (_sColdRecordBuf == null || _sColdRecordBuf.Length < minSize)
            _sColdRecordBuf = new byte[minSize];
        return _sColdRecordBuf;
    }

    /// <summary>★ 部分页回源：只读 record 的 totalLen 字节，不读整页，不进 ClockCache。
    /// 返回 Span 指向 thread-static _sColdRecordBuf——调用方须在下次冷读前用完。</summary>
    private protected unsafe Span<byte> LoadColdRecord(LogicalAddress addr, int totalLen)
    {
        // ★ 超限回退整页
        if (totalLen > _settings.ColdRecordBufferLimit)
        {
            long pageIntra = addr.Offset & PageSizeMask;
            LogicalAddress pageAddr = pageIntra == 0
                ? addr
                : _engine.CalculationAddress(addr, -pageIntra);
            var pageMem = LoadColdPage(pageAddr);
            var buf = RentColdRecordBuf(totalLen);
            new ReadOnlySpan<byte>(pageMem.BytePtr + pageIntra, totalLen).CopyTo(buf);
            return buf.AsSpan(0, totalLen);
        }

        // 直接从 addr 读 totalLen 字节
        var buf2 = RentColdRecordBuf(totalLen);
        ReadDevicePage(addr, buf2.AsSpan(0, totalLen));
        return buf2.AsSpan(0, totalLen);
    }

    /// <summary>★ 读冷区 record header 字段（codec 解析）。</summary>
    private protected unsafe RingRecordFields ReadFieldsCold(LogicalAddress addr)
    {
        long pageIntra = addr.Offset & PageSizeMask;
        LogicalAddress pageAddr = pageIntra == 0
            ? addr
            : _engine.CalculationAddress(addr, -pageIntra);
        var pageMem = LoadColdPage(pageAddr);
        int offset = (int)(addr.Offset & PageSizeMask);
        var headerSpan = new ReadOnlySpan<byte>(pageMem.BytePtr + offset, RingCodec.HeaderSize);
        RingCodec.TryReadHeader(headerSpan, out var fields);
        return fields;
    }

    /// <summary>★ 读冷区 record header 字段（部分页回源版）。</summary>
    private protected RingRecordFields ReadFieldsColdPartial(LogicalAddress addr)
    {
        int hdrSize = RingCodec.HeaderSize;
        var span = LoadColdRecord(addr, hdrSize);
        RingCodec.TryReadHeader(span, out var fields);
        return fields;
    }

    /// <summary>★ 按地址+偏移读 unmanaged T（热冷透明）。</summary>
    private protected unsafe T ReadUnmanagedAt<T>(LogicalAddress addr, int offset) where T : unmanaged
    {
        if (addr >= FlushedUntilAddress)
        {
            long phys = GetPhysicalAddress(addr);
            return Unsafe.ReadUnaligned<T>(ref Unsafe.AsRef<byte>((byte*)phys + offset));
        }
        long pageIntra = addr.Offset & PageSizeMask;
        LogicalAddress pageAddr = pageIntra == 0
            ? addr
            : _engine.CalculationAddress(addr, -pageIntra);
        var pageMem = LoadColdPage(pageAddr);
        int pageOff = (int)(addr.Offset & PageSizeMask);
        return Unsafe.ReadUnaligned<T>(ref Unsafe.AsRef<byte>(pageMem.BytePtr + pageOff + offset));
    }
}
