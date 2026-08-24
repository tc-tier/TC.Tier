using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Ring;

public abstract partial class RingBase<TKey>
{
    /// <summary>溢出写游标（LogicalAddress）。构造时从 meta/扫描恢复，写时递增。</summary>
    private protected LogicalAddress _overflowTailAddress;

    /// <summary>
    /// ★ 溢出写：按 OverflowRecordHeader 帧格式写入溢出引擎，返回帧起始 LogicalAddress。
    /// </summary>
    private LogicalAddress WriteOverflow(ReadOnlySpan<byte> value)
    {
        int paddedLen = AlignUp(value.Length, OverflowRecordHeader.Alignment);
        int padLen = paddedLen - value.Length;
        int frameLen = OverflowRecordHeaderCodec.StructSize + value.Length + padLen;

        // ★ 用 AlignedMemoryManager（引擎提供的 native 对齐内存）+ PinnedBufferPool 池化复用，
        //   不用 GC.AllocateArray（手动 pinned，无池化，GC 压力大）。照 Log _framePool 范式。
        var frameMem = _pagePool.RentAligned(frameLen, SectorSize);
        try
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
            OverflowRecordHeaderCodec.Write(frameBuf[..hdrLen], in header);

            // ★ 溢出引擎 Allocate 推进游标，Write 落盘（照 Log Allocate+Write 模型）
            LogicalAddress ovAddr = _overflowEngine!.Allocate(frameLen).Start;
            _overflowEngine.Write(ovAddr, frameBuf);
            _overflowTailAddress = _overflowEngine.CalculationAddress(ovAddr, frameLen);
            return ovAddr;
        }
        finally { _pagePool.ReturnAligned(frameMem); }
    }

    /// <summary>★ 溢出异步写。</summary>
    /// <remarks>★ C# 12 async 方法不可持有 ref struct（Span）局部——frame 填充抽到同步 helper
    ///   <see cref="FillOverflowFrame"/>（照 <see cref="ReadOverflowAsync"/>/<see cref="ReadOverflowHeader"/> 模式），
    ///   async 方法只 Rent → 同步填充 → await WriteAsync（传 Memory）→ Return。</remarks>
    private async ValueTask<LogicalAddress> WriteOverflowAsync(ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        int paddedLen = AlignUp(value.Length, OverflowRecordHeader.Alignment);
        int padLen = paddedLen - value.Length;
        int frameLen = OverflowRecordHeaderCodec.StructSize + value.Length + padLen;

        var frameMem = _pagePool.RentAligned(frameLen, SectorSize);
        try
        {
            FillOverflowFrame(frameMem, value.Span, padLen, frameLen);

            LogicalAddress ovAddr = _overflowEngine!.Allocate(frameLen).Start;
            await _overflowEngine.WriteAsync(ovAddr, frameMem.Memory[..frameLen], ct).ConfigureAwait(false);
            _overflowTailAddress = _overflowEngine.CalculationAddress(ovAddr, frameLen);
            return ovAddr;
        }
        finally { _pagePool.ReturnAligned(frameMem); }
    }

    /// <summary>同步填充溢出帧（header + payload + padding + CRC）——避开 async 方法 ref struct 限制。</summary>
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
