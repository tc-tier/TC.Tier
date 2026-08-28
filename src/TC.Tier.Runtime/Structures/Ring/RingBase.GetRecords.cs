using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Ring;

public abstract partial class RingBase<TKey>
{
    /// <summary>
    /// 按地址读单条 key（IKeyResolver 契约）——热冷透明：热区直读 native 页，冷区自动整页回源后读。
    /// key 位置 = record 起始 + header 字节数（定长 sizeof(TKey) 字节）。
    /// </summary>
    /// <param name="addr">record 的起始逻辑地址。</param>
    /// <param name="key">输出的 key 值。</param>
    /// <returns>读取成功返回 true（本实现读路径恒成功）。</returns>
    public bool TryGetKey(LogicalAddress addr, out TKey key)
    {
        EnsureReady();
        key = ReadUnmanagedAt<TKey>(addr, RingCodec.HeaderSize);
        return true;
    }

    private unsafe TKey TryGetKeyHot(LogicalAddress addr)
    {
        long phys = GetPhysicalAddress(addr);
        return Unsafe.ReadUnaligned<TKey>((byte*)phys + RingCodec.HeaderSize);
    }

    private unsafe TKey TryGetKeyFromColdPage(AlignedMemoryManager pageMem, LogicalAddress addr)
    {
        int offset = (int)(addr.Offset & PageSizeMask);
        return Unsafe.ReadUnaligned<TKey>(pageMem.BytePtr + offset + RingCodec.HeaderSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe int ReadValueOrOverflowFromPtr(byte* recordBase, ushort flags, int keyLen, uint payloadLen,
        Span<byte> dest)
    {
        if ((flags & RecordFlags.FLAG_VALUE_OVERFLOW) != 0)
        {
            var ai = ReadOverflowPointerFromPtr(recordBase, keyLen);
            return ReadOverflow(ai.Address, dest[..(int)ai.Size]);
        }

        int valLen = (int)payloadLen - keyLen;
        new ReadOnlySpan<byte>(recordBase + RingCodec.HeaderSize + keyLen, valLen).CopyTo(dest);
        return valLen;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadValueOrOverflowFromSpan(ReadOnlySpan<byte> span, ushort flags, int keyLen, uint payloadLen,
        Span<byte> dest)
    {
        int hdr = RingCodec.HeaderSize;
        if ((flags & RecordFlags.FLAG_VALUE_OVERFLOW) != 0)
        {
            var ai = ReadOverflowPointerFromSpan(span, keyLen);
            return ReadOverflow(ai.Address, dest[..(int)ai.Size]);
        }

        int valLen = (int)payloadLen - keyLen;
        span.Slice(hdr + keyLen, valLen).CopyTo(dest);
        return valLen;
    }


    private int CalcValueLengthFromSpan(ushort flags, ReadOnlySpan<byte> recordSpan, int keyLen, int payloadLen)
    {
        if ((flags & RecordFlags.FLAG_VALUE_OVERFLOW) != 0)
        {
            var ai = ReadOverflowPointerFromSpan(recordSpan, keyLen);
            return (int)ai.Size;
        }

        return payloadLen - keyLen;
    }

    private unsafe int CalcValueLengthFromPtr(ushort flags, byte* recordBase, int keyLen, uint payloadLen)
    {
        if ((flags & RecordFlags.FLAG_VALUE_OVERFLOW) != 0)
        {
            var ai = ReadOverflowPointerFromPtr(recordBase, keyLen);
            return (int)ai.Size;
        }

        return (int)payloadLen - keyLen;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe (long phys, ushort flags, ushort keyLen, uint payloadLen) ReadKeyFields(LogicalAddress addr)
    {
        long phys = GetPhysicalAddress(addr);
        var h = new ReadOnlySpan<byte>((void*)phys, RingCodec.HeaderSize);
        return (phys, RingCodec.ReadFlags(h), (ushort)KeySize, RingCodec.ReadPayloadLength(h));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe (ushort flags, ushort keyLen, uint payloadLen) ReadKeyFieldsFromPtr(byte* ptr)
    {
        var h = new ReadOnlySpan<byte>(ptr, RingCodec.HeaderSize);
        return (RingCodec.ReadFlags(h), (ushort)KeySize, RingCodec.ReadPayloadLength(h));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private (ushort flags, ushort keyLen, uint payloadLen) ReadKeyFieldsFromSpan(ReadOnlySpan<byte> header)
        => (RingCodec.ReadFlags(header), (ushort)KeySize, RingCodec.ReadPayloadLength(header));

    /// <summary>
    /// 读 addr 处 record 的 key（返回指向热区 native 页的 span）。
    /// </summary>
    /// <remarks>★ STORAGE-007 设计契约（中间层 epoch 责任划分）：
    /// 本方法（及 GetValue/GetRecord/GetRecords/TryGetKey/TryGetValue）<b>不内部持 _epoch</b>——
    /// 刻意为高性能中间层 API。epoch 保护责任在调用方：
    /// <list type="bullet">
    /// <item>上层（Index/RecordStore）若已注入共享 epoch（RingBase 构造 epoch 参数，RingBase.cs:127）并
    ///   在其 Resume/Suspend 上下文内调用本方法，则 native 页在读取期间不会被驱逐（drain 等 epoch 退出）。</item>
    /// <item>裸调用方（无 epoch 保护）需自行保证：addr 所在页在读期间不会被自动驱逐（如 mutable 区、
    ///   或显式持 Ring._epoch：调用方线程 _epoch.Resume() ... 读 ... _epoch.Suspend()）。</item>
    /// </list>
    /// 在内部持 epoch 会与上层注入的 epoch 重复 Resume/Suspend（多余开销）且无法保护返回的 span
    /// （方法返回即 Suspend，调用方持 span 期间仍可能被驱逐）——故采用中间层 NoEpoch 契约，
    /// 由上层统一管理 epoch 生命周期。参见 FASTER ClientSession 模式。</remarks>
    public unsafe RecordKey<TKey> GetKey(LogicalAddress addr)
    {
        EnsureReady();
        if (addr >= FlushedUntilAddress)
        {
            var (phys, flags, keyLen, payloadLen) = ReadKeyFields(addr);
            int valLen = CalcValueLengthFromPtr(flags, (byte*)phys, keyLen, payloadLen);
            return new RecordKey<TKey>(Unsafe.ReadUnaligned<TKey>((void*)(phys + RingCodec.HeaderSize)), valLen,
                flags, addr);
        }

        return _coldCacheCapacity == 0 ? GetKeyColdPartial(addr) : GetKeyCold(addr);
    }

    private RecordKey<TKey> GetKeyColdPartial(LogicalAddress addr)
    {
        int hdrSize = RingCodec.HeaderSize;
        var headerSpan = LoadColdRecord(addr, hdrSize);
        var (flags, keyLen, payloadLen) = ReadKeyFieldsFromSpan(headerSpan);
        int totalLen = hdrSize + (int)payloadLen;
        var span = LoadColdRecord(addr, totalLen);
        int valLen = CalcValueLengthFromSpan(flags, span, keyLen, (int)payloadLen);
        return new RecordKey<TKey>(MemoryMarshal.Read<TKey>(span.Slice(hdrSize, keyLen)), valLen, flags, addr);
    }

    private unsafe RecordKey<TKey> GetKeyCold(LogicalAddress addr)
    {
        long pageIntra = addr.Offset & PageSizeMask;
        LogicalAddress pageAddr = pageIntra == 0 ? addr : _engine.CalculationAddress(addr, -pageIntra);
        var pageMem = LoadColdPage(pageAddr);
        int offset = (int)(addr.Offset & PageSizeMask);
        byte* basePtr = pageMem.BytePtr + offset;
        var (flags, keyLen, payloadLen) = ReadKeyFieldsFromPtr(basePtr);
        int totalLen = RingCodec.HeaderSize + (int)payloadLen;
        var buf = RentColdRecordBuf(totalLen);
        new ReadOnlySpan<byte>(basePtr, totalLen).CopyTo(buf);
        var bufSpan = buf.AsSpan();
        var (bFlags, bKeyLen, bPayloadLen) = ReadKeyFieldsFromSpan(bufSpan);
        int valLen = CalcValueLengthFromSpan(bFlags, bufSpan, bKeyLen, (int)bPayloadLen);
        return new RecordKey<TKey>(MemoryMarshal.Read<TKey>(bufSpan.Slice(RingCodec.HeaderSize, bKeyLen)), valLen,
            bFlags, addr);
    }

    private unsafe RecordKey<TKey> GetKeyHot(LogicalAddress addr)
    {
        var (phys, flags, keyLen, payloadLen) = ReadKeyFields(addr);
        var keyVal = Unsafe.ReadUnaligned<TKey>((void*)(phys + RingCodec.HeaderSize));
        int valLen = CalcValueLengthFromPtr(flags, (byte*)phys, keyLen, payloadLen);
        return new RecordKey<TKey>(keyVal, valLen, flags, addr);
    }

    private unsafe RecordKey<TKey> GetKeyFromColdPage(AlignedMemoryManager pageMem, LogicalAddress addr)
    {
        int offset = (int)(addr.Offset & PageSizeMask);
        byte* basePtr = pageMem.BytePtr + offset;
        var (flags, keyLen, payloadLen) = ReadKeyFieldsFromPtr(basePtr);
        int totalLen = RingCodec.HeaderSize + (int)payloadLen;
        var buf = RentColdRecordBuf(totalLen);
        new ReadOnlySpan<byte>(basePtr, totalLen).CopyTo(buf);
        var bufSpan = buf.AsSpan();
        var (bFlags, bKeyLen, bPayloadLen) = ReadKeyFieldsFromSpan(bufSpan);
        int valLen = CalcValueLengthFromSpan(bFlags, bufSpan, bKeyLen, (int)bPayloadLen);
        return new RecordKey<TKey>(
            MemoryMarshal.Read<TKey>(bufSpan.Slice(RingCodec.HeaderSize, bKeyLen)),
            valLen, bFlags, addr);
    }

    /// <summary>
    /// 读 addr 处 record 的 value（拷贝交付到 <paramref name="destination"/>）。
    /// 热区直读 native 页拷贝；冷区自动回源后拷贝；溢出 record（FLAG_VALUE_OVERFLOW）从溢出引擎读入。
    /// </summary>
    /// <param name="addr">record 的起始逻辑地址。</param>
    /// <param name="destination">value 字节的拷贝目的地。</param>
    /// <returns>写入 destination 的 value 字节数。</returns>
    public unsafe int GetValue(LogicalAddress addr, Span<byte> destination)
    {
        EnsureReady();
        if (addr >= FlushedUntilAddress)
        {
            var (phys, flags, keyLen, payloadLen) = ReadKeyFields(addr);
            return ReadValueOrOverflowFromPtr((byte*)phys, flags, keyLen, payloadLen, destination);
        }

        return _coldCacheCapacity == 0 ? GetValueColdPartial(addr, destination) : GetValueCold(addr, destination);
    }

    private int GetValueColdPartial(LogicalAddress addr, Span<byte> destination)
    {
        int hdrSize = RingCodec.HeaderSize;
        var headerSpan = LoadColdRecord(addr, hdrSize);
        var (flags, keyLen, payloadLen) = ReadKeyFieldsFromSpan(headerSpan);
        int totalLen = hdrSize + (int)payloadLen;
        var span = LoadColdRecord(addr, totalLen);
        return ReadValueOrOverflowFromSpan(span, flags, keyLen, payloadLen, destination);
    }

    private unsafe int GetValueCold(LogicalAddress addr, Span<byte> destination)
    {
        long pageIntra = addr.Offset & PageSizeMask;
        LogicalAddress pageAddr = pageIntra == 0 ? addr : _engine.CalculationAddress(addr, -pageIntra);
        var pageMem = LoadColdPage(pageAddr);
        int offset = (int)(addr.Offset & PageSizeMask);
        byte* basePtr = pageMem.BytePtr + offset;
        var (flags, keyLen, payloadLen) = ReadKeyFieldsFromPtr(basePtr);
        return ReadValueOrOverflowFromPtr(basePtr, flags, keyLen, payloadLen, destination);
    }

    /// <summary>
    /// ★ 零拷贝值交付：value 字节直访切片（热页 native 内存 / 冷区经 ClockCache 缓存页），无 64B 级出参拷贝。
    /// <para>★ 生命周期契约（与 FASTER InputRef 同语义）：<b>span 须在 <see cref="EnterReadScope"/>
    ///   持有的读 scope 内消费</b>——页驱逐（热页窗口滑移/冷缓存淘汰）经 epoch 排水延迟到 scope 退出，
    ///   持 epoch 期间底层页恒稳不被释放/复用；跨 scope 持有=未定义。单写者+并发读者容忍下
    ///   UpdateValue 原位改写的撕裂读同既有教义。</para>
    /// <para>★ 溢出 record（FLAG_VALUE_OVERFLOW，值在溢出引擎）无页内切片——回退 thread-static
    ///   缓冲拷贝交付（零拷贝纯度让位于正确性；返回 span 有效期同上）。</para>
    /// </summary>
    public unsafe ReadOnlySpan<byte> GetValueSpan(LogicalAddress addr)
    {
        EnsureReady();
        if (addr >= FlushedUntilAddress)
        {
            var (phys, flags, keyLen, payloadLen) = ReadKeyFields(addr);
            byte* p = (byte*)phys;
            if ((flags & RecordFlags.FLAG_VALUE_OVERFLOW) != 0)
                return OverflowCopySpan(p, keyLen);
            return new ReadOnlySpan<byte>(p + RingCodec.HeaderSize + keyLen, (int)payloadLen - keyLen);
        }

        long pageIntra = addr.Offset & PageSizeMask;
        LogicalAddress pageAddr = pageIntra == 0 ? addr : _engine.CalculationAddress(addr, -pageIntra);
        var pageMem = LoadColdPage(pageAddr);
        int offset = (int)(addr.Offset & PageSizeMask);
        byte* basePtr = pageMem.BytePtr + offset;
        var (cFlags, cKeyLen, cPayloadLen) = ReadKeyFieldsFromPtr(basePtr);
        if ((cFlags & RecordFlags.FLAG_VALUE_OVERFLOW) != 0)
            return OverflowCopySpan(basePtr, cKeyLen);
        return new ReadOnlySpan<byte>(basePtr + RingCodec.HeaderSize + cKeyLen, (int)cPayloadLen - cKeyLen);
    }

    /// <summary>溢出值回退：溢出引擎读入 thread-static 缓冲（有效期同 span 契约——下次同线程调用覆盖）。</summary>
    private unsafe ReadOnlySpan<byte> OverflowCopySpan(byte* recordBase, int keyLen)
    {
        var ai = ReadOverflowPointerFromPtr(recordBase, keyLen);
        var buf = RentColdRecordBuf((int)ai.Size);
        int got = ReadOverflow(ai.Address, buf.AsSpan(0, (int)ai.Size));
        return buf.AsSpan(0, got);
    }

    /// <summary>
    /// ★ Ring 读 scope（持 epoch）——零拷贝 <see cref="GetValueSpan"/> 的生命周期护栏：
    /// 页驱逐经 epoch 排水（STORAGE-009 契约），scope 内底层页恒稳。
    /// </summary>
    public ReadScope EnterReadScope() => new(this);

    /// <summary>★ epoch 读保护协议实现（IEpochProtected——Session 读 scope 聚合入口；ReadScope 转发此真源）。</summary>
    public void EnterEpoch()
    {
        ThrowIfDisposed();
        _epoch.Resume();
    }

    /// <summary>退出 epoch 读保护（IEpochProtected 契约——与 <see cref="EnterEpoch"/> 同线程配对）。</summary>
    public void ExitEpoch() => _epoch.Suspend();

    /// <summary>
    /// ★ 零拷贝读 scope（ref struct 栈护栏）——构造时进入 epoch 读保护，<see cref="ReadScope.Dispose"/> 时退出。
    /// 持有期间（即 <see cref="GetValueSpan"/> 返回 span 的有效期）热页驱逐/冷缓存淘汰经 epoch 排水被阻塞，
    /// 底层页恒稳不被释放/复用；跨 scope 持有 span = 未定义。
    /// </summary>
    public readonly ref struct ReadScope
    {
        private readonly RingBase<TKey> _owner;
        internal ReadScope(RingBase<TKey> owner)
        {
            owner.EnterEpoch();
            _owner = owner;
        }

        /// <summary>退出 epoch 读保护（owner 已释放时为空操作）。</summary>
        public void Dispose()
        {
            _owner?.ExitEpoch();
        }
    }

    /// <summary>
    /// ★ 批量读 record（按页号聚簇，减少冷区回源次数）。
    /// </summary>
    /// <param name="addresses">要读取的逻辑地址数组</param>
    /// <param name="handler">处理每个读取到的 record 的处理器</param>
    /// <typeparam name="THandler">处理器类型</typeparam>
    public unsafe void GetRecords<THandler>(ReadOnlySpan<LogicalAddress> addresses, THandler handler)
        where THandler : IReadOnlyRecordHandler<TKey>
    {
        EnsureReady();
        if (addresses.Length == 0) return;

        // ★ 按页号排序聚簇——页号经 GetDistance 算（§8 铁律——Offset 跨段无意义）
        var indexed = new (LogicalAddress addr, long pageNo, int origIdx)[addresses.Length];
        for (int i = 0; i < addresses.Length; i++)
        {
            var a = addresses[i];
            indexed[i] = (a, _engine.GetDistance(_dataStart, a) >> PageSizeBits, i);
        }

        Array.Sort(indexed, (a, b) => a.pageNo.CompareTo(b.pageNo));

        LogicalAddress flushedUntil = FlushedUntilAddress;
        long lastColdPageNo = -1;
        AlignedMemoryManager? currentPage = null;

        foreach (var (addr, pageNo, origIdx) in indexed)
        {
            if (addr >= flushedUntil)
            {
                var (phys, flags, keyLen, payloadLen) = ReadKeyFields(addr);
                int vLen = CalcValueLengthFromPtr(flags, (byte*)phys, keyLen, payloadLen);
                handler.Handle(addr,
                    Unsafe.ReadUnaligned<TKey>((void*)(phys + RingCodec.HeaderSize)),
                    vLen, flags);
            }
            else
            {
                if (pageNo != lastColdPageNo)
                {
                    long pageIntra = addr.Offset & PageSizeMask;
                    LogicalAddress pageAddr = pageIntra == 0 ? addr : _engine.CalculationAddress(addr, -pageIntra);
                    currentPage = LoadColdPage(pageAddr);
                    lastColdPageNo = pageNo;
                }
                if (currentPage == null) continue;
                int offset = (int)(addr.Offset & PageSizeMask);
                byte* basePtr = currentPage.BytePtr + offset;
                var (flags, keyLen, payloadLen) = ReadKeyFieldsFromPtr(basePtr);
                int totalLen = RingCodec.HeaderSize + (int)payloadLen;
                var buf = RentColdRecordBuf(totalLen);
                new ReadOnlySpan<byte>(basePtr, totalLen).CopyTo(buf);
                var bufSpan = buf.AsSpan();
                var (bFlags, bKeyLen, bPayloadLen) = ReadKeyFieldsFromSpan(bufSpan);
                int vLen2 = CalcValueLengthFromSpan(bFlags, bufSpan, bKeyLen, (int)bPayloadLen);
                handler.Handle(addr,
                    MemoryMarshal.Read<TKey>(bufSpan.Slice(RingCodec.HeaderSize, bKeyLen)),
                    vLen2, bFlags);
            }
        }
    }

    /// <summary>异步读单条 key：热区同步快路径直读；冷区异步整页回源后读。</summary>
    /// <param name="addr">record 的起始逻辑地址。</param>
    /// <param name="ct">取消令牌——冷区异步回源途中响应取消。</param>
    /// <returns>record 的 key 封装（Key/ValueLength/Flags/Address）。</returns>
    public async ValueTask<RecordKey<TKey>> GetKeyAsync(LogicalAddress addr, CancellationToken ct = default)
    {
        EnsureReady();
        if (addr >= FlushedUntilAddress)
            return GetKeyHot(addr);
        long pageIntra = addr.Offset & PageSizeMask;
        LogicalAddress pageAddr = pageIntra == 0 ? addr : _engine.CalculationAddress(addr, -pageIntra);
        var pageMem = await LoadColdPageAsync(pageAddr, ct).ConfigureAwait(false);
        return GetKeyFromColdPage(pageMem, addr);
    }

    /// <summary>
    /// 异步读 value（拷贝交付到 <paramref name="destination"/>）：热区同步快路径
    /// （溢出值走异步溢出引擎读）；冷区异步整页回源后同步拷贝。
    /// </summary>
    /// <param name="addr">record 的起始逻辑地址。</param>
    /// <param name="destination">value 字节的拷贝目的地。</param>
    /// <param name="ct">取消令牌——溢出值/冷区异步读途中响应取消。</param>
    /// <returns>写入 destination 的 value 字节数。</returns>
    public async ValueTask<int> GetValueAsync(LogicalAddress addr, Memory<byte> destination,
        CancellationToken ct = default)
    {
        EnsureReady();
        if (addr >= FlushedUntilAddress)
        {
            // ★ hot 区：同步读 header 判溢出；溢出 payload 走异步 ReadOverflowAsync。
            var (isOverflow, flags, keyLen, payloadLen, ai) = ReadValueMetaHot(addr);
            if (isOverflow)
                return await ReadOverflowAsync(ai.Address, destination[..(int)ai.Size], ct).ConfigureAwait(false);
            return ReadValueInlineHot(addr, flags, keyLen, payloadLen, destination.Span);
        }

        long pageIntra = addr.Offset & PageSizeMask;
        LogicalAddress pageAddr = pageIntra == 0 ? addr : _engine.CalculationAddress(addr, -pageIntra);
        var pageMem = await LoadColdPageAsync(pageAddr, ct).ConfigureAwait(false);
        // ★ cold 区：pageMem 已异步加载（非 span，跨 await 安全）；判溢出后溢出 payload 走异步。
        var (cIsOverflow, cFlags, cKeyLen, cPayloadLen, cAi) = ReadValueMetaFromColdPage(pageMem, addr);
        if (cIsOverflow)
            return await ReadOverflowAsync(cAi.Address, destination[..(int)cAi.Size], ct).ConfigureAwait(false);
        return ReadValueInlineFromColdPage(pageMem, addr, cFlags, cKeyLen, cPayloadLen, destination.Span);
    }

    /// <summary>★ hot 区同步读 header + 溢出指针（unsafe 集中在此，async 主体不持指针）。</summary>
    private unsafe (bool isOverflow, ushort flags, int keyLen, uint payloadLen, AddressInfo ai) ReadValueMetaHot(
        LogicalAddress addr)
    {
        var (phys, flags, keyLen, payloadLen) = ReadKeyFields(addr);
        bool isOverflow = (flags & RecordFlags.FLAG_VALUE_OVERFLOW) != 0;
        var ai = isOverflow ? ReadOverflowPointerFromPtr((byte*)phys, keyLen) : default;
        return (isOverflow, flags, keyLen, payloadLen, ai);
    }

    /// <summary>★ hot 区同步拷贝 inline value（非溢出快路径）。</summary>
    private unsafe int ReadValueInlineHot(LogicalAddress addr, ushort flags, int keyLen, uint payloadLen,
        Span<byte> dest)
    {
        long phys = GetPhysicalAddress(addr);
        return ReadValueOrOverflowFromPtr((byte*)phys, flags, keyLen, payloadLen, dest);
    }

    /// <summary>★ cold 区同步读 header + 溢出指针（unsafe 集中在此）。</summary>
    private unsafe (bool isOverflow, ushort flags, int keyLen, uint payloadLen, AddressInfo ai)
        ReadValueMetaFromColdPage(AlignedMemoryManager pageMem, LogicalAddress addr)
    {
        int offset = (int)(addr.Offset & PageSizeMask);
        byte* basePtr = pageMem.BytePtr + offset;
        var (flags, keyLen, payloadLen) = ReadKeyFieldsFromPtr(basePtr);
        bool isOverflow = (flags & RecordFlags.FLAG_VALUE_OVERFLOW) != 0;
        var ai = isOverflow ? ReadOverflowPointerFromPtr(basePtr, keyLen) : default;
        return (isOverflow, flags, keyLen, payloadLen, ai);
    }

    /// <summary>★ cold 区同步拷贝 inline value（非溢出快路径）。</summary>
    private unsafe int ReadValueInlineFromColdPage(AlignedMemoryManager pageMem, LogicalAddress addr, ushort flags,
        int keyLen, uint payloadLen, Span<byte> dest)
    {
        int offset = (int)(addr.Offset & PageSizeMask);
        byte* basePtr = pageMem.BytePtr + offset;
        return ReadValueOrOverflowFromPtr(basePtr, flags, keyLen, payloadLen, dest);
    }
    /// <summary>异步读单条 key（<see cref="TryGetKey"/> 的异步版）：热区同步快路径，冷区异步整页回源后读。</summary>
    /// <param name="addr">record 的起始逻辑地址。</param>
    /// <param name="ct">取消令牌——冷区异步回源途中响应取消。</param>
    /// <returns>(Key=读到的 key 值, Success=是否成功)。</returns>
    public async ValueTask<(TKey Key, bool Success)> TryGetKeyAsync(LogicalAddress addr, CancellationToken ct = default)
    {
        EnsureReady();
        if (addr >= FlushedUntilAddress)
            return (TryGetKeyHot(addr), true);
        long pageIntra = addr.Offset & PageSizeMask;
        LogicalAddress pageAddr = pageIntra == 0 ? addr : _engine.CalculationAddress(addr, -pageIntra);
        var pageMem = await LoadColdPageAsync(pageAddr, ct).ConfigureAwait(false);
        return (TryGetKeyFromColdPage(pageMem, addr), true);
    }
}