using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.Ring;

public abstract partial class RingBase<TKey>
{
    private unsafe LogicalAddress WriteRecordCore(TKey key, ReadOnlySpan<byte> payload, ushort flags, LogicalAddress previousAddress = default)
    {
        int keyLen = KeySize, payloadLen = payload.Length;
        uint totalPayload = (uint)(keyLen + payloadLen);
        int unaligned = RingCodec.HeaderSize + (int)totalPayload;
        int aligned = (unaligned + RingCodec.Alignment - 1) & ~(RingCodec.Alignment - 1);
        ushort paddingLen = (ushort)(aligned - unaligned);

        _epoch.Resume();
        try
        {
            LogicalAddress addr = Allocate(aligned);
            long phys = GetPhysicalAddress(addr);

            var fields = new RingRecordFields(
                (ushort)(flags | RecordFlags.FLAG_RINGRECORD_VALID | RecordFlags.FLAG_RINGRECORD_SEALED),
                totalPayload, paddingLen, previousAddress);
            var headerSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize);
            RingCodec.WriteHeader(headerSpan, in fields);
            Unsafe.WriteUnaligned((void*)(phys + RingCodec.HeaderSize), key);
            payload.CopyTo(new Span<byte>((void*)(phys + RingCodec.HeaderSize + keyLen), payloadLen));
            if (paddingLen > 0)
                new Span<byte>((void*)(phys + unaligned), paddingLen).Clear();
            var recordSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize + (int)totalPayload);
            RingCodec.FillCrc(recordSpan, RingCodec.HeaderSize, (int)totalPayload);
            Seal(addr, aligned);
            return addr;
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>★ 公开写入：大 value 自动溢出到溢出引擎（同步）。flags 由引擎内部管理，不对外暴露。</summary>
    public LogicalAddress Write(TKey key, ReadOnlySpan<byte> value)
        => WriteWithFlags(key, value, 0);

    /// <summary>
    /// ★ 引擎/子类扩展点：带 flags 的写入（如将来设 <see cref="RecordFlags.FLAG_RINGRECORD_TOMBSTONE"/>）。
    /// <para>flags 是 <see cref="RecordFlags"/> 内部位（internal 可见），故不暴露在 public API。</para>
    /// </summary>
    private protected LogicalAddress WriteWithFlags(TKey key, ReadOnlySpan<byte> value, ushort flags)
    {
        EnsureNotDisposed();
        EnsureReady();
        if (_overflowPolicy == OverflowPolicy.Enabled && value.Length > _minOverflowSize)
        {
            LogicalAddress ovAddr = WriteOverflow(value);
            var ai = AddressInfo.WriteInfo(ovAddr, value.Length);
            return WriteRecordCore(key, MemoryMarshal.AsBytes(
                new ReadOnlySpan<AddressInfo>(in ai)),
                (ushort)(flags | RecordFlags.FLAG_VALUE_OVERFLOW));
        }
        // ★ 注意：WriteRecordCore 里 payload = key + AddressInfo(24B)，PayloadLength = keyLen + 24。
        return WriteRecordCore(key, value, flags);
    }

    /// <summary>
    /// ★ 公开异步写入：大 value 走真异步溢出路径（<see cref="WriteOverflowAsync"/>），小 value 走同步快路径。
    /// <para>★ 快/慢路径分离——inline 写是纯内存操作无 I/O，仅溢出落盘需 await。</para>
    /// </summary>
    public ValueTask<LogicalAddress> WriteAsync(TKey key, ReadOnlyMemory<byte> value, CancellationToken ct = default)
    {
        EnsureNotDisposed();
        EnsureReady();
        if (_overflowPolicy == OverflowPolicy.Enabled && value.Length > _minOverflowSize)
            return WriteOverflowThenRecordAsync(key, value, ct);   // 慢路径:真异步
        return new ValueTask<LogicalAddress>(WriteRecordCore(key, value.Span, 0));  // 快路径:同步,零 async 开销
    }

    /// <summary>★ 引擎/子类异步扩展点（带 flags）。对齐 <see cref="WriteRecordWithFlags"/> 的异步版。</summary>
    private protected ValueTask<LogicalAddress> WriteWithFlagsAsync(TKey key, ReadOnlyMemory<byte> value, ushort flags, CancellationToken ct)
    {
        EnsureNotDisposed();
        EnsureReady();
        if (_overflowPolicy == OverflowPolicy.Enabled && value.Length > _minOverflowSize)
            return WriteOverflowThenRecordWithFlagsAsync(key, value, flags, ct);
        return new ValueTask<LogicalAddress>(WriteRecordCore(key, value.Span, flags));
    }

    /// <summary>★ 慢路径：异步写溢出帧 → 同步写 inline record（含 FLAG_VALUE_OVERFLOW + 调用方 flags）。</summary>
    private async ValueTask<LogicalAddress> WriteOverflowThenRecordAsync(TKey key, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        LogicalAddress ovAddr = await WriteOverflowAsync(value, ct).ConfigureAwait(false);
        var ai = AddressInfo.WriteInfo(ovAddr, value.Length);
        return WriteRecordCore(key, MemoryMarshal.AsBytes(new ReadOnlySpan<AddressInfo>(in ai)),
                               RecordFlags.FLAG_VALUE_OVERFLOW);
    }

    /// <summary>★ 慢路径带 flags 版（引擎/子类扩展点用）。</summary>
    private async ValueTask<LogicalAddress> WriteOverflowThenRecordWithFlagsAsync(TKey key, ReadOnlyMemory<byte> value, ushort flags, CancellationToken ct)
    {
        LogicalAddress ovAddr = await WriteOverflowAsync(value, ct).ConfigureAwait(false);
        var ai = AddressInfo.WriteInfo(ovAddr, value.Length);
        return WriteRecordCore(key, MemoryMarshal.AsBytes(new ReadOnlySpan<AddressInfo>(in ai)),
                               (ushort)(flags | RecordFlags.FLAG_VALUE_OVERFLOW));
    }

    public unsafe void UpdateValue(LogicalAddress addr, ReadOnlySpan<byte> newValue)
    {
        EnsureNotDisposed();
        EnsureReady();
        long phys = GetPhysicalAddress(addr);
        var headerSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize);
        RingCodec.TryReadHeader(headerSpan, out var fields);
        int keyLen = KeySize;   // key 长度是类型事实（v2.0 header 已无 KeyLength 字段）
        bool wasOverflow = (fields.Flags & RecordFlags.FLAG_VALUE_OVERFLOW) != 0;
        bool willOverflow = _overflowPolicy == OverflowPolicy.Enabled && newValue.Length > _minOverflowSize;
        int valOff = RingCodec.HeaderSize + keyLen;

        if (wasOverflow && willOverflow)
        {
            LogicalAddress ovAddr = WriteOverflow(newValue);
            var ai = AddressInfo.WriteInfo(ovAddr, newValue.Length);
            Unsafe.WriteUnaligned(ref Unsafe.AsRef<byte>((void*)(phys + valOff)), ai);
            fields = new RingRecordFields(fields.Flags, (uint)(keyLen + 24), fields.PaddingLength, fields.PreviousAddress);
            RingCodec.WriteHeader(headerSpan, in fields);
            var recordSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize + keyLen + 24);
            RingCodec.FillCrc(recordSpan, RingCodec.HeaderSize, keyLen + 24);
        }
        else if (!wasOverflow && willOverflow)
        {
            LogicalAddress ovAddr = WriteOverflow(newValue);
            var ai = AddressInfo.WriteInfo(ovAddr, newValue.Length);
            fields = new RingRecordFields((ushort)(fields.Flags | RecordFlags.FLAG_VALUE_OVERFLOW), (uint)(keyLen + 24), fields.PaddingLength, fields.PreviousAddress);
            RingCodec.WriteHeader(headerSpan, in fields);
            Unsafe.WriteUnaligned(ref Unsafe.AsRef<byte>((void*)(phys + valOff)), ai);
            var recordSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize + keyLen + 24);
            RingCodec.FillCrc(recordSpan, RingCodec.HeaderSize, keyLen + 24);
        }
        else if (wasOverflow && !willOverflow)
        {
            var (_, allocated) = GetRecordSize(phys);
            int originalInlineCapacity = allocated - RingCodec.HeaderSize - keyLen;
            if (newValue.Length > originalInlineCapacity)
                throw new InvalidOperationException(
                    $"Value too large for inline slot after overflow revert: {newValue.Length} > {originalInlineCapacity}");
            fields = new RingRecordFields((ushort)(fields.Flags & ~RecordFlags.FLAG_VALUE_OVERFLOW), (uint)(keyLen + newValue.Length), fields.PaddingLength, fields.PreviousAddress);
            RingCodec.WriteHeader(headerSpan, in fields);
            newValue.CopyTo(new Span<byte>((void*)(phys + valOff), newValue.Length));
            int newPayloadLen = keyLen + newValue.Length;
            var recordSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize + newPayloadLen);
            RingCodec.FillCrc(recordSpan, RingCodec.HeaderSize, newPayloadLen);
        }
        else
        {
            fields = new RingRecordFields(fields.Flags, (uint)(keyLen + newValue.Length), fields.PaddingLength, fields.PreviousAddress);
            RingCodec.WriteHeader(headerSpan, in fields);
            newValue.CopyTo(new Span<byte>((void*)(phys + valOff), newValue.Length));
            int newPayloadLen = keyLen + newValue.Length;
            var recordSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize + newPayloadLen);
            RingCodec.FillCrc(recordSpan, RingCodec.HeaderSize, newPayloadLen);
        }
    }

    /// <summary>
    /// ★ 异步更新值：溢出翻转场景（newValue 落溢出引擎）走 <see cref="WriteOverflowAsync"/> 真异步；
    /// 纯 inline 翻转（含 overflow→inline 回退）是内存操作，同步完成返回 <see cref="ValueTask.CompletedTask"/>。
    /// <para>对齐 <see cref="UpdateValue"/> 的 4 分支语义。</para>
    /// </summary>
    public unsafe ValueTask UpdateValueAsync(LogicalAddress addr, ReadOnlyMemory<byte> newValue, CancellationToken ct = default)
    {
        EnsureNotDisposed();
        EnsureReady();
        long phys = GetPhysicalAddress(addr);
        var headerSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize);
        RingCodec.TryReadHeader(headerSpan, out var fields);
        int keyLen = KeySize;   // key 长度是类型事实（v2.0 header 已无 KeyLength 字段）
        bool wasOverflow = (fields.Flags & RecordFlags.FLAG_VALUE_OVERFLOW) != 0;
        bool willOverflow = _overflowPolicy == OverflowPolicy.Enabled && newValue.Length > _minOverflowSize;

        // 纯 inline 分支（无溢出写）：同步完成
        if (!willOverflow)
        {
            UpdateInline(addr, phys, headerSpan, in fields, keyLen, wasOverflow, newValue.Span);
            return ValueTask.CompletedTask;
        }

        // 溢出写分支：异步写溢出帧 → 同步更新 inline 指针/header/CRC
        return UpdateValueWithOverflowAsync(phys, fields, keyLen, wasOverflow, newValue, ct);
    }

    /// <summary>★ 慢路径：异步写溢出帧后同步更新 inline record（overflow→overflow / inline→overflow 共用）。</summary>
    private async ValueTask UpdateValueWithOverflowAsync(
        long phys, RingRecordFields fields, int keyLen, bool wasOverflow,
        ReadOnlyMemory<byte> newValue, CancellationToken ct)
    {
        LogicalAddress ovAddr = await WriteOverflowAsync(newValue, ct).ConfigureAwait(false);
        var ai = AddressInfo.WriteInfo(ovAddr, newValue.Length);
        // overflow→overflow：flags 保持（已含 OVERFLOW 位）；inline→overflow：补 OVERFLOW 位
        ushort newFlags = wasOverflow ? fields.Flags : (ushort)(fields.Flags | RecordFlags.FLAG_VALUE_OVERFLOW);
        var updated = new RingRecordFields(newFlags, (uint)(keyLen + 24), fields.PaddingLength, fields.PreviousAddress);
        WriteOverflowPointerInline(phys, keyLen, in updated, ai);
    }

    /// <summary>★ 同步把溢出指针写回 inline record（header + AddressInfo + CRC）——unsafe 集中点。</summary>
    private unsafe void WriteOverflowPointerInline(long phys, int keyLen, in RingRecordFields updated, AddressInfo ai)
    {
        int valOff = RingCodec.HeaderSize + keyLen;
        var headerSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize);
        RingCodec.WriteHeader(headerSpan, in updated);
        Unsafe.WriteUnaligned(ref Unsafe.AsRef<byte>((void*)(phys + valOff)), ai);
        var recordSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize + keyLen + 24);
        RingCodec.FillCrc(recordSpan, RingCodec.HeaderSize, keyLen + 24);
    }

    /// <summary>
    /// ★ 纯 inline 更新（内存操作，同步路径专用）：
    /// overflow→inline 回退（清 OVERFLOW 位 + 容量校验 + 拷值）或 inline→inline（重写 payload 长度 + 拷值）。
    /// </summary>
    private unsafe void UpdateInline(LogicalAddress addr, long phys, Span<byte> headerSpan,
        in RingRecordFields fields, int keyLen, bool wasOverflow, ReadOnlySpan<byte> newValue)
    {
        int valOff = RingCodec.HeaderSize + keyLen;
        if (wasOverflow)
        {
            // overflow→inline 回退：校验原 inline slot 容量
            var (_, allocated) = GetRecordSize(phys);
            int originalInlineCapacity = allocated - RingCodec.HeaderSize - keyLen;
            if (newValue.Length > originalInlineCapacity)
                throw new InvalidOperationException(
                    $"Value too large for inline slot after overflow revert: {newValue.Length} > {originalInlineCapacity}");
            var reverted = new RingRecordFields((ushort)(fields.Flags & ~RecordFlags.FLAG_VALUE_OVERFLOW),
                (uint)(keyLen + newValue.Length), fields.PaddingLength, fields.PreviousAddress);
            RingCodec.WriteHeader(headerSpan, in reverted);
            newValue.CopyTo(new Span<byte>((void*)(phys + valOff), newValue.Length));
            int newPayloadLen = keyLen + newValue.Length;
            var recordSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize + newPayloadLen);
            RingCodec.FillCrc(recordSpan, RingCodec.HeaderSize, newPayloadLen);
        }
        else
        {
            // inline→inline
            var updated = new RingRecordFields(fields.Flags, (uint)(keyLen + newValue.Length),
                fields.PaddingLength, fields.PreviousAddress);
            RingCodec.WriteHeader(headerSpan, in updated);
            newValue.CopyTo(new Span<byte>((void*)(phys + valOff), newValue.Length));
            int newPayloadLen = keyLen + newValue.Length;
            var recordSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize + newPayloadLen);
            RingCodec.FillCrc(recordSpan, RingCodec.HeaderSize, newPayloadLen);
        }
    }
}
