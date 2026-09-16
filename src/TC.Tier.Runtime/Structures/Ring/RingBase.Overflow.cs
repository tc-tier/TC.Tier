using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase 溢出 partial——WiscKey 分离 value 流（溢出引擎写读 + 溢出尾水位恢复/推进）。
/// </summary>
public abstract partial class RingBase<TKey>
{
    /// <summary>溢出写游标（LogicalAddress）。构造时从 meta/扫描恢复，写时递增。</summary>
    private protected LogicalAddress _overflowTailAddress;

    /// <summary>溢出尾发布/快照门闩（STORAGE-083）：<see cref="_overflowTailAddress"/> 是 16B 复合结构，
    /// 并发溢出写者直接赋值、meta 快照直接读值均可撕裂且可能回退（后完成者写旧值）。
    /// 并发写者只做单调 max 发布，meta 经同门闩取一致 16B 快照。</summary>
    private readonly object _overflowTailLock = new();

    /// <summary>★ 发布溢出尾水位（单调前进；并发溢出写者安全）。</summary>
    private void PublishOverflowTailAddress(LogicalAddress tail)
    {
        lock (_overflowTailLock)
        {
            if (tail > _overflowTailAddress) _overflowTailAddress = tail;
        }
    }

    /// <summary>★ 取溢出尾水位的一致 16B 快照（与 <see cref="PublishOverflowTailAddress"/> 同门闩）。</summary>
    private LogicalAddress SnapshotOverflowTailAddress()
    {
        lock (_overflowTailLock) return _overflowTailAddress;
    }

    /// <summary>★ 溢出帧分块大小（internal const 非 Options——基准说话前不加旋钮；与 Log PageSize 同量级）。
    /// 大帧写入恒定 256KB 中转缓冲，内存上界 = ChunkSize × 并发数（与值大小解耦）。</summary>
    private protected const int OverflowChunkSize = 256 * 1024;

    /// <summary>
    /// ★ 溢出写：按 OverflowRecordHeader 帧格式写入溢出引擎，返回帧起始 LogicalAddress。
    /// 小帧（帧长 ≤ <see cref="OverflowChunkSize"/>）走单帧单写原路径；大帧分块 pwrite + 链式 CRC + header 最后写。
    /// </summary>
    private LogicalAddress WriteOverflow(ReadOnlySpan<byte> value)
    {
        int paddedLen = AlignUp(value.Length, OverflowRecordHeader.Alignment);
        int padLen = paddedLen - value.Length;
        int frameLen = OverflowRecordHeaderCodec.StructSize + paddedLen;

        // ★ 小帧：现路径原样（单帧单写，无额外 syscall）
        if (frameLen <= OverflowChunkSize)
        {
            var frameMem = _pagePool.RentAligned(frameLen, SectorSize);
            try
            {
                FillOverflowFrame(frameMem, value, padLen, frameLen);
                LogicalAddress ovAddr = _overflowEngine!.Allocate(frameLen).Start;
                _overflowEngine.Write(ovAddr, frameMem.GetSpan(0, frameLen));
                PublishOverflowTailAddress(_overflowEngine.CalculationAddress(ovAddr, frameLen));
                return ovAddr;
            }
            finally { _pagePool.ReturnAligned(frameMem); }
        }

        // ★ 大帧：分块写（磁盘格式零变更；帧布局/Version/CRC 覆盖域与单写逐字节同构）
        LogicalAddress addr = _overflowEngine!.Allocate(frameLen).Start;
        var chunkMem = _pagePool.RentAligned(OverflowChunkSize, SectorSize);
        try
        {
            var header = OverflowRecordHeaderCodec.Create();
            header.PayloadLength = (uint)value.Length;
            header.PaddingLength = (ushort)padLen;

            int hdrLen = OverflowRecordHeaderCodec.StructSize;
            Span<byte> chunkBuf = chunkMem.GetSpan(0, OverflowChunkSize);
            // header 前缀（crc 字段前 14B）先入链——内容帧起始即知，只入链一次
            OverflowRecordHeaderCodec.Write(chunkBuf, in header);
            uint crc = UnifiedCrc.ComputeCrc32C(chunkBuf[..OverflowRecordHeaderCodec.Offset_Crc32C]);

            LogicalAddress chunkAddr = _overflowEngine.CalculationAddress(addr, hdrLen);
            for (int offset = 0; offset < value.Length; )
            {
                int len = Math.Min(OverflowChunkSize, value.Length - offset);
                // 末 pad（≤3B）随末块同缓冲同写并入链——pad>0 蕴含末块非整块（ChunkSize 是 4 的倍数）
                bool withPad = padLen > 0 && offset + len == value.Length;
                int writeLen = len + (withPad ? padLen : 0);
                value.Slice(offset, len).CopyTo(chunkBuf);
                if (withPad) chunkBuf.Slice(len, padLen).Clear();
                crc = UnifiedCrc.ComputeCrc32C(crc, chunkBuf[..writeLen]);
                _overflowEngine.Write(chunkAddr, chunkBuf[..writeLen]);
                chunkAddr = _overflowEngine.CalculationAddress(chunkAddr, writeLen);
                offset += len;
            }

            header.Crc32C = crc;
            OverflowRecordHeaderCodec.Write(chunkBuf[..hdrLen], in header);
            _overflowEngine.Write(addr, chunkBuf[..hdrLen]);   // ★ header 最后写（帧有效性的唯一判据）
            PublishOverflowTailAddress(_overflowEngine.CalculationAddress(addr, frameLen));
            return addr;
        }
        finally { _pagePool.ReturnAligned(chunkMem); }
    }

    /// <summary>★ 溢出异步写（分块骨架同 <see cref="WriteOverflow"/>；小帧走原单帧路径）。</summary>
    /// <remarks>★ C# 12 async 方法不可持有 ref struct（Span）局部——chunk 拷贝/CRC 经
    ///   <see cref="AlignedMemoryManager.GetSpan(int, int)"/> 内联表达式完成（不跨 await 存活），
    ///   header 借 chunk 缓冲前 18B 组装（零额外分配）。</remarks>
    private async ValueTask<LogicalAddress> WriteOverflowAsync(ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        int paddedLen = AlignUp(value.Length, OverflowRecordHeader.Alignment);
        int padLen = paddedLen - value.Length;
        int frameLen = OverflowRecordHeaderCodec.StructSize + paddedLen;

        // ★ 小帧：现路径原样
        if (frameLen <= OverflowChunkSize)
        {
            var frameMem = _pagePool.RentAligned(frameLen, SectorSize);
            try
            {
                FillOverflowFrame(frameMem, value.Span, padLen, frameLen);

                LogicalAddress ovAddr = _overflowEngine!.Allocate(frameLen).Start;
                await _overflowEngine.WriteAsync(ovAddr, frameMem.Memory[..frameLen], ct).ConfigureAwait(false);
                PublishOverflowTailAddress(_overflowEngine.CalculationAddress(ovAddr, frameLen));
                return ovAddr;
            }
            finally { _pagePool.ReturnAligned(frameMem); }
        }

        // ★ 大帧：分块写
        LogicalAddress addr = _overflowEngine!.Allocate(frameLen).Start;
        var chunkMem = _pagePool.RentAligned(OverflowChunkSize, SectorSize);
        try
        {
            var header = OverflowRecordHeaderCodec.Create();
            header.PayloadLength = (uint)value.Length;
            header.PaddingLength = (ushort)padLen;

            int hdrLen = OverflowRecordHeaderCodec.StructSize;
            OverflowRecordHeaderCodec.Write(chunkMem.GetSpan(0, hdrLen), in header);
            uint crc = UnifiedCrc.ComputeCrc32C(chunkMem.GetSpan(0, OverflowRecordHeaderCodec.Offset_Crc32C));

            LogicalAddress chunkAddr = _overflowEngine.CalculationAddress(addr, hdrLen);
            for (int offset = 0; offset < value.Length; )
            {
                int len = Math.Min(OverflowChunkSize, value.Length - offset);
                bool withPad = padLen > 0 && offset + len == value.Length;
                int writeLen = len + (withPad ? padLen : 0);
                value.Span.Slice(offset, len).CopyTo(chunkMem.GetSpan(0, writeLen));
                if (withPad) chunkMem.GetSpan(0, writeLen).Slice(len, padLen).Clear();
                crc = UnifiedCrc.ComputeCrc32C(crc, chunkMem.GetSpan(0, writeLen));
                await _overflowEngine.WriteAsync(chunkAddr, chunkMem.Memory[..writeLen], ct).ConfigureAwait(false);
                chunkAddr = _overflowEngine.CalculationAddress(chunkAddr, writeLen);
                offset += len;
            }

            header.Crc32C = crc;
            OverflowRecordHeaderCodec.Write(chunkMem.GetSpan(0, hdrLen), in header);
            await _overflowEngine.WriteAsync(addr, chunkMem.Memory[..hdrLen], ct).ConfigureAwait(false);   // ★ header 最后写
            PublishOverflowTailAddress(_overflowEngine.CalculationAddress(addr, frameLen));
            return addr;
        }
        finally { _pagePool.ReturnAligned(chunkMem); }
    }

    /// <summary>同步填充整帧溢出帧（header + payload + padding + CRC）——小帧路径（帧长 ≤ chunk）专用，
    /// 并服务 async 小帧路径（避开 async 方法 ref struct 限制）。</summary>
    private void FillOverflowFrame(AlignedMemoryManager frameMem, ReadOnlySpan<byte> value, int padLen, int frameLen)
    {
        var frameBuf = frameMem.GetSpan(0, frameLen);
        // ★ Create()：ValidEquals 规范字段（Magic/Version/Flags）自动填常量——只填变化字段
        var header = OverflowRecordHeaderCodec.Create();
        header.PayloadLength = (uint)value.Length;
        header.PaddingLength = (ushort)padLen;

        int hdrLen = OverflowRecordHeaderCodec.StructSize;
        OverflowRecordHeaderCodec.Write(frameBuf, in header);
        value.CopyTo(frameBuf.Slice(hdrLen, value.Length));
        if (padLen > 0)
            frameBuf.Slice(hdrLen + value.Length, padLen).Clear();

        uint crc = UnifiedCrc.ComputeCrc32C(frameBuf[..OverflowRecordHeaderCodec.Offset_Crc32C]);
        crc = UnifiedCrc.ComputeCrc32C(crc, frameBuf.Slice(hdrLen, value.Length + padLen));
        header.Crc32C = crc;
        OverflowRecordHeaderCodec.Write(frameBuf.Slice(0, hdrLen), in header);
    }

    /// <summary>
    /// ★ 从溢出地址读取值（调用方已验证 magic+CRC）。
    /// </summary>
    private int ReadOverflow(LogicalAddress overflowAddr, Span<byte> destination)
    {
        int hdrLen = OverflowRecordHeaderCodec.StructSize;
        Span<byte> headerBuf = stackalloc byte[hdrLen];
        OverflowEngine!.Read(overflowAddr, headerBuf);
        var h = OverflowRecordHeaderCodec.Read(headerBuf);
        var payloadAddr = OverflowEngine!.CalculationAddress(overflowAddr, hdrLen);
        return OverflowEngine!.Read(payloadAddr, destination[..(int)h.PayloadLength]);
    }

    /// <summary>★ 异步读取溢出值。</summary>
    private async ValueTask<int> ReadOverflowAsync(LogicalAddress overflowAddr, Memory<byte> destination, CancellationToken ct)
    {
        int hdrLen = OverflowRecordHeaderCodec.StructSize;
        // ★ async 方法不可 stackalloc——用同步 helper 读 header（header 读本身同步，仅 payload 走异步）
        var h = ReadOverflowHeader(overflowAddr);
        var payloadAddr = OverflowEngine!.CalculationAddress(overflowAddr, hdrLen);
        return await OverflowEngine!.ReadAsync(payloadAddr, destination[..(int)h.PayloadLength], ct).ConfigureAwait(false);
    }

    /// <summary>同步读溢出帧 header（避开 async 方法 stackalloc 限制）。</summary>
    private OverflowRecordHeader ReadOverflowHeader(LogicalAddress overflowAddr)
    {
        int hdrLen = OverflowRecordHeaderCodec.StructSize;
        Span<byte> headerBuf = stackalloc byte[hdrLen];
        OverflowEngine!.Read(overflowAddr, headerBuf);
        return OverflowRecordHeaderCodec.Read(headerBuf);
    }

    /// <summary>★ 从 record 基指针读 AddressInfo。</summary>
    private unsafe AddressInfo ReadOverflowPointerFromPtr(byte* recordBase, int keyLen)
        => Unsafe.ReadUnaligned<AddressInfo>(ref Unsafe.AsRef<byte>(recordBase + RingCodec.HeaderSize + keyLen));

    /// <summary>★ 从 record span 读 AddressInfo。</summary>
    private AddressInfo ReadOverflowPointerFromSpan(ReadOnlySpan<byte> recordSpan, int keyLen)
        => MemoryMarshal.Read<AddressInfo>(recordSpan.Slice(RingCodec.HeaderSize + keyLen, 24));

    // ════════════════════════════════════════════════════════════
    // 溢出恢复
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ 恢复溢出写游标：meta → 引擎 OpenSequentialReader 扫描 → 引擎 AllocatedTail 近似。
    /// </summary>
    private void RecoverOverflowTail(LogicalAddress? hintTail)
    {
        if (OverflowEngine is null) { _overflowTailAddress = LogicalAddress.Empty; return; }

        // Tier 1: hints 注入
        if (hintTail is { } t && t > LogicalAddress.Empty) { _overflowTailAddress = t; return; }

        // Tier 2: meta 持久化
        if (MetaPolicy.Load() && MetaPolicy.ReadMetaPayload() is { } payload
            && payload.OverflowTailAddress > LogicalAddress.Empty)
        {
            _overflowTailAddress = payload.OverflowTailAddress;
            return;
        }

        // Tier 3: 引擎 OpenSequentialReader 扫描溢出帧 + 前向 CRC 求精
        var scanned = ScanOverflowTail();
        if (scanned > LogicalAddress.Empty)
        {
            _overflowTailAddress = scanned;
            return;
        }

        // Tier 4: 引擎 AllocatedTail 近似
        _overflowTailAddress = OverflowEngine.AllocatedTail;

        AlignOverflowEngineTail();
    }

    /// <summary>★ 恢复后引擎尾对齐（照 LogBase.Recovery.ReconcileEngineTail 先例——物理真相引擎自恢复、
    /// 逻辑水位结构自管）：恢复裁决出的 <see cref="_overflowTailAddress"/> 落后于引擎物理尾时
    /// （撕裂写预留/持久 Wasted 区间），把引擎尾退齐到逻辑尾——否则新溢出帧从撕裂区中间分配，
    /// 与遗留 Wasted 区间相交被读门拦截（PartitionInvalidException）。</summary>
    private void AlignOverflowEngineTail()
    {
        if (_overflowTailAddress > LogicalAddress.Empty && OverflowEngine!.AllocatedTail > _overflowTailAddress)
            OverflowEngine.ReclaimTail(_overflowTailAddress);
    }

    /// <summary>★ 用 OpenSequentialReader 扫描溢出引擎，逐帧 CRC 校验找最后有效尾。</summary>
    private LogicalAddress ScanOverflowTail()
    {
        const int hdrLen = OverflowRecordHeaderCodec.StructSize;
        LogicalAddress lastEnd = LogicalAddress.Empty;
        // ★ usePageCache: true——按 18B 未对齐 header 读，DIO 模式下未对齐读会报 "参数错误"(error 87)。
        //   走页缓存（buffered）允许任意长度/偏移读；整页扫描才用 usePageCache:false。
        using var reader = OverflowEngine!.OpenSequentialReader(
            OverflowEngine.MinAddress, OverflowEngine.AllocatedTail,
            ReadDirection.Forward, usePageCache: true, SnapshotMode.Consistent);

        Span<byte> hdrBuf = stackalloc byte[hdrLen];
        while (reader.Position < reader.End)
        {
            var frameStart = reader.Position;
            if (reader.Read(hdrBuf) < hdrLen) break;
            var h = OverflowRecordHeaderCodec.Read(hdrBuf);
            if (h.MagicValue != OverflowRecordHeader.Magic) break;
            var frameLen = hdrLen + (int)h.PayloadLength + (int)h.PaddingLength;
            // 跳过 payload+padding（不验 CRC，恢复路径容忍——上层读时再校验）
            reader.Skip(frameLen - hdrLen);
            lastEnd = OverflowEngine.CalculationAddress(frameStart, frameLen);
        }
        return lastEnd;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AlignUp(int len, int alignment) => (len + alignment - 1) & ~(alignment - 1);
}
