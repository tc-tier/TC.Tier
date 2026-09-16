using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase 记录读取 partial——内容自愈读内核（热区页池直读/冷区设备回源，CRC 校验互为自愈）
/// 与 key/value 交付 API（TryGetKey/GetKey/TryGetValue/GetValueSpan）。
/// </summary>
public abstract partial class RingBase<TKey>
{
    // ═══ 内容自愈读内核（read-protection-tiering v2）═══
    // ★ 模型：判据 = SafeSnapshotTail（addr < 水位 → 页池直读，≥ → 设备读）；热直读经 magic + CRC
    //   校验，失败自动回退设备重读——页池与设备互为自愈，驱逐复用/撕裂/残留不产生假数据
    //   （读到设备权威快照或 NotFound，由上层定夺）。
    // ★ epoch 读保护从读路径退役（read-protection-tiering v2）——安全性由「写穿先行（I1）+ 水位内完整（I2）
    //   + 内容指纹（I4）」不变式承担，不再依赖调用方编排。

    /// <summary>
    /// ★ 热区/冷页记录验证：TryReadHeaderLight（magic + 长度上界，单字段直读免全量解码）+
    /// 页界钳制 + CRC 校验（与写侧 FillCrc 同源）。
    /// <para>★ 页界钳制：record 不跨页（分配约束）——复用残留的垃圾 PayloadLength 越页即判无效，
    ///   杜绝按垃圾长度构造 span 的越界读（读保护退役后的安全前提）。</para>
    /// <para>★ 轻量门省去的 Version 门对消费面无损：本门只读 Magic/Flags/PayloadLength 三个
    ///   版本间偏移不变的字段（v2.0 改版只动 32~36B 区）；全量版本校验仍在恢复重放
    ///   （游标 TryReadHeader）与 meta KeySize 锚点处承担。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe bool TryReadValidRecordAt(long phys, int pageIntra, out RingRecordFields fields)
    {
        fields = default;
        var header = new ReadOnlySpan<byte>((void*)phys, RingCodec.HeaderSize);
        if (!RingCodec.TryReadHeaderLight(header, out fields)) return false;
        if (fields.PayloadLength > (uint)(PageSize - pageIntra - RingCodec.HeaderSize)) return false;
        var record = new ReadOnlySpan<byte>((void*)phys, RingCodec.HeaderSize + (int)fields.PayloadLength);
        return RingCodec.VerifyCrc(record, RingCodec.HeaderSize, (int)fields.PayloadLength);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe TKey ReadKeyAt(long phys)
        => Unsafe.ReadUnaligned<TKey>((void*)(phys + RingCodec.HeaderSize));

    /// <summary>由已验证 fields 组装 RecordKey（value 长度含溢出解析——溢出时为溢出值长度）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe RecordKey<TKey> RecordKeyFromValidated(long phys, in RingRecordFields f, LogicalAddress addr)
    {
        int valLen = CalcValueLengthFromPtr(f.Flags, (byte*)phys, KeySize, f.PayloadLength);
        return new RecordKey<TKey>(ReadKeyAt(phys), valLen, f.Flags, addr);
    }

    /// <summary>按地址读 key 全链（热验证 → 冷回源验证）。false = 无有效记录（未命中/校验失败）。</summary>
    private bool TryGetRecordKeyCore(LogicalAddress addr, out RecordKey<TKey> recordKey)
    {
        long dist = DistanceFromDataStart(addr);
        if (IsHotRecord(dist))
        {
            long phys = GetPhysicalAddress(addr);
            if (TryReadValidRecordAt(phys, (int)(dist & PageSizeMask), out var f))
            {
                recordKey = RecordKeyFromValidated(phys, in f, addr);
                return true;
            }
            // 内容校验失败 → 设备回退自愈（页复用/撕裂/残留）
        }

        return _coldCacheCapacity == 0
            ? TryGetRecordKeyColdPartial(addr, out recordKey)
            : TryGetRecordKeyColdPage(addr, out recordKey);
    }

    /// <summary>冷区 key 读（部分页回源版，验证后交付）。false = 设备无有效记录。</summary>
    private bool TryGetRecordKeyColdPartial(LogicalAddress addr, out RecordKey<TKey> recordKey)
    {
        int hdrSize = RingCodec.HeaderSize;
        var headerSpan = LoadColdRecord(addr, hdrSize);
        if (!RingCodec.TryReadHeader(headerSpan, out var f))
        {
            recordKey = default;
            return false;
        }

        int totalLen = hdrSize + (int)f.PayloadLength;
        var span = LoadColdRecord(addr, totalLen);
        if (!RingCodec.VerifyCrc(span, hdrSize, (int)f.PayloadLength))
        {
            recordKey = default;
            return false;
        }

        int valLen = CalcValueLengthFromSpan(f.Flags, span, KeySize, (int)f.PayloadLength);
        recordKey = new RecordKey<TKey>(MemoryMarshal.Read<TKey>(span.Slice(hdrSize, KeySize)), valLen, f.Flags, addr);
        return true;
    }

    /// <summary>冷区 key 读（ClockCache 缓存页版，验证后交付）。false = 设备无有效记录。</summary>
    private unsafe bool TryGetRecordKeyColdPage(LogicalAddress addr, out RecordKey<TKey> recordKey)
    {
        long pageIntra = addr.Offset & PageSizeMask;
        LogicalAddress pageAddr = pageIntra == 0 ? addr : _engine.CalculationAddress(addr, -pageIntra);
        var pageMem = LoadColdPage(pageAddr);
        int offset = (int)(addr.Offset & PageSizeMask);
        byte* basePtr = pageMem.BytePtr + offset;
        if (!TryReadValidRecordAt((long)basePtr, offset, out var f))
        {
            recordKey = default;
            return false;
        }

        recordKey = RecordKeyFromValidated((long)basePtr, in f, addr);
        return true;
    }

    /// <summary>
    /// 按地址读单条 key（IKeyResolver 契约）——内容自愈读：热区页池直读（magic+CRC 验证），
    /// 失败自动回退设备重读；冷区整页回源后验证。
    /// </summary>
    /// <param name="addr">record 的起始逻辑地址。</param>
    /// <param name="key">输出的 key 值。</param>
    /// <returns>读到有效记录返回 true；无有效记录（未命中/校验失败）返回 false。</returns>
    public bool TryGetKey(LogicalAddress addr, out TKey key)
    {
        EnsureReady();
        if (TryGetRecordKeyCore(addr, out var recordKey))
        {
            key = recordKey.Key;
            return true;
        }
        key = default;
        return false;
    }

    /// <summary>
    /// 读 addr 处 record 的 key——内容自愈读全链；该地址无有效记录（magic/CRC 校验失败且设备回退
    /// 仍无有效数据）即抛——内容损坏形态的 fail-fast（不需要 NotFound 形态的调用方走 <see cref="TryGetKey"/>）。
    /// </summary>
    /// <param name="addr">record 的逻辑地址。</param>
    /// <returns>组装好的 <see cref="RecordKey{TKey}"/>（key + value 长度 + flags + 地址；value 长度含溢出解析）。</returns>
    public RecordKey<TKey> GetKey(LogicalAddress addr)
    {
        EnsureReady();
        if (!TryGetRecordKeyCore(addr, out var recordKey))
            throw new InvalidOperationException(
                $"record at {addr} 无有效内容（magic/CRC 校验失败且设备回退无有效数据——内容损坏形态，fail-fast）");
        return recordKey;
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

    /// <summary>
    /// 读 addr 处 record 的 value（拷贝交付到 <paramref name="destination"/>）——内容自愈读：
    /// 热区页池直读（magic+CRC 验证），失败自动回退设备重读；冷区回源后验证。
    /// </summary>
    /// <param name="addr">record 的起始逻辑地址。</param>
    /// <param name="destination">value 字节的拷贝目的地。</param>
    /// <returns>写入 destination 的 value 字节数；0 = 该地址无有效记录（未命中/墓碑/校验失败）。</returns>
    public int GetValue(LogicalAddress addr, Span<byte> destination)
    {
        EnsureReady();
        var (found, written) = GetValueValidatedCore(addr, destination);
        return found ? written : 0;
    }

    /// <summary>
    /// ★ 读取 addr 处记录的 value 到调用方缓冲（热直读/冷设备/自愈三态内聚——read-protection-tiering v2 Q2）。
    /// <para>冷区读直接写调用方缓冲——无 thread-static「下次同线程冷读前用完」隐式契约；
    /// 自愈重试（热读 CRC 失败 → 设备重读）以调用方缓冲为落点，零额外缓冲。</para>
    /// </summary>
    /// <param name="addr">record 的起始逻辑地址。</param>
    /// <param name="destination">value 字节的拷贝目的地。</param>
    /// <param name="written">命中时写入 destination 的 value 字节数。</param>
    /// <returns>true = 命中且已拷贝；false = 未命中/墓碑/校验失败（含设备回退失败）。</returns>
    /// <exception cref="ArgumentException">destination 小于 value 长度（拷贝时抛）。</exception>
    public bool TryGetValue(LogicalAddress addr, Span<byte> destination, out int written)
    {
        EnsureReady();
        (bool found, written) = GetValueValidatedCore(addr, destination);
        return found;
    }

    /// <summary>value 拷贝读全链（热验证 → 冷回源验证）。Found=false = 无有效记录（未命中/墓碑/校验失败）。</summary>
    private unsafe (bool Found, int Written) GetValueValidatedCore(LogicalAddress addr, Span<byte> destination)
    {
        long dist = DistanceFromDataStart(addr);
        if (IsHotRecord(dist))
        {
            long phys = GetPhysicalAddress(addr);
            if (TryReadValidRecordAt(phys, (int)(dist & PageSizeMask), out var f))
            {
                if ((f.Flags & RecordFlags.FLAG_RINGRECORD_TOMBSTONE) != 0)
                    return (false, 0);
                return (true, ReadValueOrOverflowFromPtr((byte*)phys, f.Flags, KeySize, f.PayloadLength, destination));
            }
            // 内容校验失败 → 设备回退自愈（页复用/撕裂/残留）
        }

        return _coldCacheCapacity == 0
            ? GetValueColdPartialValidated(addr, destination)
            : GetValueColdValidated(addr, destination);
    }

    /// <summary>冷区 value 拷贝读（ClockCache 缓存页版，验证后交付）。</summary>
    private unsafe (bool Found, int Written) GetValueColdValidated(LogicalAddress addr, Span<byte> destination)
    {
        long pageIntra = addr.Offset & PageSizeMask;
        LogicalAddress pageAddr = pageIntra == 0 ? addr : _engine.CalculationAddress(addr, -pageIntra);
        var pageMem = LoadColdPage(pageAddr);
        int offset = (int)(addr.Offset & PageSizeMask);
        byte* basePtr = pageMem.BytePtr + offset;
        if (!TryReadValidRecordAt((long)basePtr, offset, out var f) ||
            (f.Flags & RecordFlags.FLAG_RINGRECORD_TOMBSTONE) != 0)
            return (false, 0);
        return (true, ReadValueOrOverflowFromPtr(basePtr, f.Flags, KeySize, f.PayloadLength, destination));
    }

    /// <summary>冷区 value 拷贝读（部分页回源版，验证后交付）。</summary>
    private (bool Found, int Written) GetValueColdPartialValidated(LogicalAddress addr, Span<byte> destination)
    {
        int hdrSize = RingCodec.HeaderSize;
        var headerSpan = LoadColdRecord(addr, hdrSize);
        if (!RingCodec.TryReadHeader(headerSpan, out var f))
            return (false, 0);

        int totalLen = hdrSize + (int)f.PayloadLength;
        var span = LoadColdRecord(addr, totalLen);
        if (!RingCodec.VerifyCrc(span, hdrSize, (int)f.PayloadLength) ||
            (f.Flags & RecordFlags.FLAG_RINGRECORD_TOMBSTONE) != 0)
            return (false, 0);
        return (true, ReadValueOrOverflowFromSpan(span, f.Flags, KeySize, f.PayloadLength, destination));
    }

    /// <summary>
    /// ★ 零拷贝值交付：value 字节直访切片（热区页池直读 / 冷区经 ClockCache 缓存页），无 64B 级出参拷贝。
    /// <para>★ 内容自愈读（read-protection-tiering v2）：判据 = SafeSnapshotTail（addr &lt; 水位 → 页池直读；
    ///   ≥ → 设备读）；热直读经 magic + CRC 校验，失败自动回退设备重读——页池与设备互为自愈，
    ///   驱逐复用/撕裂/残留不产生假数据（读到设备权威快照或空 span）。返回空 span = 该地址无有效记录
    ///   （未命中/墓碑/校验失败——上层定夺）。</para>
    /// <para>★ 生命周期契约：span 仅同步栈内合法（消费 = 一次内存拷贝/解析，纳秒级）；跨 await 持有
    ///   必须走拷贝 API（<see cref="TryGetValue"/>）——由 API 形态强制，不依赖约定。单写者+并发读者
    ///   容忍下 UpdateValue 原位改写的撕裂读同既有教义。</para>
    /// <para>★ 溢出 record（FLAG_VALUE_OVERFLOW，值在溢出引擎）无页内切片——回退 thread-static
    ///   缓冲拷贝交付（零拷贝纯度让位于正确性；有效期同上）。</para>
    /// </summary>
    /// <param name="addr">record 的逻辑地址。</param>
    /// <returns>value 字节切片；空 span = 该地址无有效记录（未命中/墓碑/校验失败）。</returns>
    public unsafe ReadOnlySpan<byte> GetValueSpan(LogicalAddress addr)
    {
        EnsureReady();
        long dist = DistanceFromDataStart(addr);
        if (IsHotRecord(dist))
        {
            long phys = GetPhysicalAddress(addr);
            if (TryReadValidRecordAt(phys, (int)(dist & PageSizeMask), out var f))
            {
                if ((f.Flags & RecordFlags.FLAG_RINGRECORD_TOMBSTONE) != 0)
                    return ReadOnlySpan<byte>.Empty;
                byte* p = (byte*)phys;
                if ((f.Flags & RecordFlags.FLAG_VALUE_OVERFLOW) != 0)
                    return OverflowCopySpan(p, KeySize);
                return new ReadOnlySpan<byte>(p + RingCodec.HeaderSize + KeySize, (int)f.PayloadLength - KeySize);
            }
            // 内容校验失败 → 设备回退自愈（页复用/撕裂/残留——I5：读到写穿快照或 NotFound，无假数据）
        }

        long pageIntra = addr.Offset & PageSizeMask;
        LogicalAddress pageAddr = pageIntra == 0 ? addr : _engine.CalculationAddress(addr, -pageIntra);
        var pageMem = LoadColdPage(pageAddr);
        int offset = (int)(addr.Offset & PageSizeMask);
        byte* basePtr = pageMem.BytePtr + offset;
        if (!TryReadValidRecordAt((long)basePtr, offset, out var cf) ||
            (cf.Flags & RecordFlags.FLAG_RINGRECORD_TOMBSTONE) != 0)
            return ReadOnlySpan<byte>.Empty;   // NotFound 语义——上层定夺
        if ((cf.Flags & RecordFlags.FLAG_VALUE_OVERFLOW) != 0)
            return OverflowCopySpan(basePtr, KeySize);
        return new ReadOnlySpan<byte>(basePtr + RingCodec.HeaderSize + KeySize, (int)cf.PayloadLength - KeySize);
    }

    /// <summary>溢出值回退：溢出引擎读入 thread-static 缓冲（有效期同 span 契约——下次同线程调用覆盖）。</summary>
    private unsafe ReadOnlySpan<byte> OverflowCopySpan(byte* recordBase, int keyLen)
    {
        var ai = ReadOverflowPointerFromPtr(recordBase, keyLen);
        var buf = RentColdRecordBuf((int)ai.Size);
        int got = ReadOverflow(ai.Address, buf.AsSpan(0, (int)ai.Size));
        return buf.AsSpan(0, got);
    }

    /// <summary>★ epoch 读保护协议实现（IEpochProtected——Session 读 scope 聚合入口）。
    /// 底层原语合法存在（Q1 拍板）：写路径/驱逐 drain 仍在使用；读路径不再消费——
    /// 读安全性由内容自愈读不变式（I1 写穿先行/I2 水位内完整/I4 内容指纹）承担。</summary>
    public void EnterEpoch()
    {
        ThrowIfDisposed();
        _epoch.Resume();
    }

    /// <summary>退出 epoch 读保护（IEpochProtected 契约——与 <see cref="EnterEpoch"/> 同线程配对）。</summary>
    public void ExitEpoch() => _epoch.Suspend();

    /// <summary>
    /// ★ 批量读 record（按页号聚簇，减少冷区回源次数）——内容自愈读：热区验证失败自动回退设备，
    /// 无有效记录的地址跳过（handler 不回调——调用方经缺席感知）。
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
        var indexed = new (LogicalAddress addr, long pageNo)[addresses.Length];
        for (int i = 0; i < addresses.Length; i++)
        {
            var a = addresses[i];
            indexed[i] = (a, _engine.GetDistance(_dataStart, a) >> PageSizeBits);
        }

        Array.Sort(indexed, (a, b) => a.pageNo.CompareTo(b.pageNo));

        long safeTailDist = Volatile.Read(ref _safeSnapshotTailDist);   // ★ 判据快照（批内一致——同扫描游标形态）
        long lastColdPageNo = -1;
        AlignedMemoryManager? currentPage = null;

        foreach (var (addr, pageNo) in indexed)
        {
            long dist = DistanceFromDataStart(addr);
            if (dist < safeTailDist)
            {
                long phys = GetPhysicalAddress(addr);
                if (TryReadValidRecordAt(phys, (int)(dist & PageSizeMask), out var f))
                {
                    int vLen = CalcValueLengthFromPtr(f.Flags, (byte*)phys, KeySize, f.PayloadLength);
                    handler.Handle(addr,
                        Unsafe.ReadUnaligned<TKey>((void*)(phys + RingCodec.HeaderSize)),
                        vLen, f.Flags);
                    continue;
                }
                // 内容校验失败 → 设备回退自愈（页复用/撕裂/残留）
            }

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
            if (!TryReadValidRecordAt((long)basePtr, offset, out var cf)) continue;   // 无有效记录——跳过
            int vLen2 = CalcValueLengthFromPtr(cf.Flags, basePtr, KeySize, cf.PayloadLength);
            handler.Handle(addr,
                Unsafe.ReadUnaligned<TKey>((void*)(basePtr + RingCodec.HeaderSize)),
                vLen2, cf.Flags);
        }
    }

    /// <summary>异步读单条 key：热区同步快路径直读（验证）；失败/冷区异步整页回源后验证。</summary>
    /// <param name="addr">record 的起始逻辑地址。</param>
    /// <param name="ct">取消令牌——冷区异步回源途中响应取消。</param>
    /// <returns>record 的 key 封装（Key/ValueLength/Flags/Address）。</returns>
    public async ValueTask<RecordKey<TKey>> GetKeyAsync(LogicalAddress addr, CancellationToken ct = default)
    {
        EnsureReady();
        long dist = DistanceFromDataStart(addr);
        if (IsHotRecord(dist))
        {
            long phys = GetPhysicalAddress(addr);
            if (TryReadValidRecordAt(phys, (int)(dist & PageSizeMask), out var f))
                return RecordKeyFromValidated(phys, in f, addr);
            // 内容校验失败 → 设备回退自愈
        }

        long pageIntra = addr.Offset & PageSizeMask;
        LogicalAddress pageAddr = pageIntra == 0 ? addr : _engine.CalculationAddress(addr, -pageIntra);
        var pageMem = await LoadColdPageAsync(pageAddr, ct).ConfigureAwait(false);
        if (!TryReadRecordKeyFromColdPage(pageMem, addr, out var recordKey))
            throw new InvalidOperationException(
                $"record at {addr} 无有效内容（magic/CRC 校验失败且设备回退无有效数据——内容损坏形态，fail-fast）");
        return recordKey;
    }

    /// <summary>冷区 key 读（ClockCache 缓存页版）——unsafe 集中，async 主体不持指针。</summary>
    private unsafe bool TryReadRecordKeyFromColdPage(AlignedMemoryManager pageMem, LogicalAddress addr,
        out RecordKey<TKey> recordKey)
    {
        int offset = (int)(addr.Offset & PageSizeMask);
        byte* basePtr = pageMem.BytePtr + offset;
        if (!TryReadValidRecordAt((long)basePtr, offset, out var f))
        {
            recordKey = default;
            return false;
        }
        recordKey = RecordKeyFromValidated((long)basePtr, in f, addr);
        return true;
    }

    /// <summary>
    /// 异步读 value（拷贝交付到 <paramref name="destination"/>）：热区同步快路径验证（失败自动回退
    /// 冷区设备自愈）；冷区异步整页回源后验证拷贝。溢出 record 溢出 payload 走异步溢出引擎读。
    /// </summary>
    /// <param name="addr">record 的起始逻辑地址。</param>
    /// <param name="destination">value 字节的拷贝目的地。</param>
    /// <param name="ct">取消令牌——溢出值/冷区异步读途中响应取消。</param>
    /// <returns>写入 destination 的 value 字节数；0 = 该地址无有效记录（未命中/墓碑/校验失败）。</returns>
    public async ValueTask<int> GetValueAsync(LogicalAddress addr, Memory<byte> destination,
        CancellationToken ct = default)
    {
        EnsureReady();
        long dist = DistanceFromDataStart(addr);
        if (IsHotRecord(dist))
        {
            // ★ hot 区：验证读 header 判溢出；溢出 payload 走异步 ReadOverflowAsync。
            var hot = ReadValueMetaHotValidated(addr, (int)(dist & PageSizeMask));
            if (hot.Valid)
            {
                if (hot.IsOverflow)
                    return await ReadOverflowAsync(hot.Ai.Address, destination[..(int)hot.Ai.Size], ct)
                        .ConfigureAwait(false);
                return ReadInlineValueFromPhys(hot.Phys, hot.Flags, hot.PayloadLength, destination.Span);
            }
            // 内容校验失败 → 设备回退自愈
        }

        long pageIntra = addr.Offset & PageSizeMask;
        LogicalAddress pageAddr = pageIntra == 0 ? addr : _engine.CalculationAddress(addr, -pageIntra);
        var pageMem = await LoadColdPageAsync(pageAddr, ct).ConfigureAwait(false);
        // ★ cold 区：pageMem 已异步加载（非 span，跨 await 安全）；验证后判溢出，溢出 payload 走异步。
        var cold = ReadValueMetaFromColdPageValidated(pageMem, addr);
        if (!cold.Valid)
            return 0;   // 无有效记录（NotFound 语义）
        if (cold.IsOverflow)
            return await ReadOverflowAsync(cold.Ai.Address, destination[..(int)cold.Ai.Size], ct)
                .ConfigureAwait(false);
        return ReadInlineValueFromPhys(cold.Phys, cold.Flags, cold.PayloadLength, destination.Span);
    }

    /// <summary>★ hot 区验证读 header + 溢出指针（unsafe 集中在此外，async 主体不持指针）。校验失败 Valid=false。</summary>
    private unsafe (bool Valid, long Phys, bool IsOverflow, ushort Flags, uint PayloadLength, AddressInfo Ai)
        ReadValueMetaHotValidated(LogicalAddress addr, int pageIntra)
    {
        long phys = GetPhysicalAddress(addr);
        if (!TryReadValidRecordAt(phys, pageIntra, out var f))
            return default;
        bool isOverflow = (f.Flags & RecordFlags.FLAG_VALUE_OVERFLOW) != 0;
        var ai = isOverflow ? ReadOverflowPointerFromPtr((byte*)phys, KeySize) : default;
        return (true, phys, isOverflow, f.Flags, f.PayloadLength, ai);
    }

    /// <summary>★ cold 区验证读 header + 溢出指针（unsafe 集中在此外）。校验失败 Valid=false。</summary>
    private unsafe (bool Valid, long Phys, bool IsOverflow, ushort Flags, uint PayloadLength, AddressInfo Ai)
        ReadValueMetaFromColdPageValidated(AlignedMemoryManager pageMem, LogicalAddress addr)
    {
        int offset = (int)(addr.Offset & PageSizeMask);
        byte* basePtr = pageMem.BytePtr + offset;
        if (!TryReadValidRecordAt((long)basePtr, offset, out var f))
            return default;
        bool isOverflow = (f.Flags & RecordFlags.FLAG_VALUE_OVERFLOW) != 0;
        var ai = isOverflow ? ReadOverflowPointerFromPtr(basePtr, KeySize) : default;
        return (true, (long)basePtr, isOverflow, f.Flags, f.PayloadLength, ai);
    }

    /// <summary>★ 验证后的 inline value 拷贝（非溢出快路径——unsafe 收口）。</summary>
    private unsafe int ReadInlineValueFromPhys(long phys, ushort flags, uint payloadLen, Span<byte> dest)
        => ReadValueOrOverflowFromPtr((byte*)phys, flags, KeySize, payloadLen, dest);

    /// <summary>异步读单条 key（<see cref="TryGetKey"/> 的异步版）：热区同步快路径验证，失败/冷区异步整页回源后验证。</summary>
    /// <param name="addr">record 的起始逻辑地址。</param>
    /// <param name="ct">取消令牌——冷区异步回源途中响应取消。</param>
    /// <returns>(Key=读到的 key 值, Success=是否读到有效记录)。</returns>
    public async ValueTask<(TKey Key, bool Success)> TryGetKeyAsync(LogicalAddress addr, CancellationToken ct = default)
    {
        EnsureReady();
        long dist = DistanceFromDataStart(addr);
        if (IsHotRecord(dist))
        {
            long phys = GetPhysicalAddress(addr);
            if (TryReadValidRecordAt(phys, (int)(dist & PageSizeMask), out _))
                return (ReadKeyAt(phys), true);
            // 内容校验失败 → 设备回退自愈
        }

        long pageIntra = addr.Offset & PageSizeMask;
        LogicalAddress pageAddr = pageIntra == 0 ? addr : _engine.CalculationAddress(addr, -pageIntra);
        var pageMem = await LoadColdPageAsync(pageAddr, ct).ConfigureAwait(false);
        return TryReadKeyFromColdPage(pageMem, addr, out var key) ? (key, true) : (default, false);
    }

    /// <summary>冷区 key 读（ClockCache 缓存页版）——unsafe 集中，async 主体不持指针。</summary>
    private unsafe bool TryReadKeyFromColdPage(AlignedMemoryManager pageMem, LogicalAddress addr, out TKey key)
    {
        int offset = (int)(addr.Offset & PageSizeMask);
        byte* basePtr = pageMem.BytePtr + offset;
        if (!TryReadValidRecordAt((long)basePtr, offset, out _))
        {
            key = default;
            return false;
        }
        key = Unsafe.ReadUnaligned<TKey>(basePtr + RingCodec.HeaderSize);
        return true;
    }
}
