using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase 写入 partial——公开 K/V 写入面（Write/WriteTombstone 及异步版、分段写、原位 UpdateValue）。
/// </summary>
public abstract partial class RingBase<TKey>
{
    private unsafe LogicalAddress WriteRecordCore(TKey key, ReadOnlySpan<byte> payload, ushort flags, LogicalAddress previousAddress = default)
        => WriteRecordCore(key, [], payload, flags, previousAddress);

    private unsafe LogicalAddress WriteRecordCore(TKey key, scoped ReadOnlySpan<byte> prefix,
        scoped ReadOnlySpan<byte> payload, ushort flags, LogicalAddress previousAddress = default)
    {
        EnsureReady();   // ★ #258：Dispose 竞态 fail-fast（写入口统一门禁）
        int keyLen = KeySize, payloadLen = checked(prefix.Length + payload.Length);
        uint totalPayload = (uint)(keyLen + payloadLen);
        int unaligned = RingCodec.HeaderSize + (int)totalPayload;
        int aligned = (unaligned + RingCodec.Alignment - 1) & ~(RingCodec.Alignment - 1);
        ushort paddingLen = (ushort)(aligned - unaligned);

        _epoch.Resume();
        try
        {
            while (true)
            {
                LogicalAddress addr;
                // ★ 分配+完整写入同一临界区（分配原子化）：TryAllocateLocked 成功即写全
                //   header/payload/CRC 并推进 _safeSnapshotTail——锁外不存在「已分配未写完」的
                //   在途槽（其被扫描跳过并越过 ⇒ 写者后续换绑落旧索引即永久丢失，压强回归实锤）。
                lock (_tailLock)
                {
                    addr = TryAllocateLocked(aligned);
                    if (addr.IsValid)
                    {
                        long phys = GetPhysicalAddress(addr);

                        var fields = new RingRecordFields(
                            (ushort)(flags | RecordFlags.FLAG_RINGRECORD_VALID | RecordFlags.FLAG_RINGRECORD_SEALED),
                            totalPayload, paddingLen, previousAddress);
                        var headerSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize);
                        RingCodec.WriteHeader(headerSpan, in fields);
                        Unsafe.WriteUnaligned((void*)(phys + RingCodec.HeaderSize), key);
                        var destination = new Span<byte>((void*)(phys + RingCodec.HeaderSize + keyLen), payloadLen);
                        prefix.CopyTo(destination);
                        payload.CopyTo(destination[prefix.Length..]);
                        if (paddingLen > 0)
                            new Span<byte>((void*)(phys + unaligned), paddingLen).Clear();
                        var recordSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize + (int)totalPayload);
                        RingCodec.FillCrc(recordSpan, RingCodec.HeaderSize, (int)totalPayload);
                        Seal(addr, aligned);
                        // ★ header+payload+CRC 完整——锁内推进安全快照尾（锁内顺序=分配序，单调无洞；
                        //   8B 读侧判据水位同点 CAS-max 发布——读路径无锁分档的可见性前提）
                        var addrEnd = _engine.CalculationAddress(addr, aligned);
                        AdvanceSafeSnapshotTail(addrEnd);
                    }
                }

                if (addr.IsValid)
                    return addr;

                // ★ 背压：环形满——锁外 flush readonly 区腾 slot（锁内禁止 flush/evict IO 路径）
                var ro = ReadOnlyAddress;
                var flushed = FlushedUntilAddress;
                if (ro > flushed) WriteThroughUntil(ro);
                else Thread.Yield();
            }
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>★ 公开写入：大 value 自动溢出到溢出引擎（同步）。flags 由引擎内部管理，不对外暴露。</summary>
    /// <param name="key">待写 key（TKey 定长 blittable）。</param>
    /// <param name="value">payload 字节（超溢出阈值时整值写溢出引擎，record 内只留指针）。</param>
    /// <returns>本条 record 的逻辑地址（写完即在页池完整可见；永不返回 <see cref="LogicalAddress.Empty"/>）。</returns>
    public LogicalAddress Write(TKey key, ReadOnlySpan<byte> value)
        => WriteWithFlags(key, value, 0);

    /// <summary>
    /// 分段写入 inline value；prefix/value 直接复制到 Ring，避免调用方拼接临时数组。
    /// Overflow 开启时应使用连续 value 的现有 Write/WriteAsync 路径。
    /// </summary>
    /// <param name="key">待写 key（TKey 定长 blittable）。</param>
    /// <param name="prefix">value 前段字节（与 value 顺序拼接为完整 value）。</param>
    /// <param name="value">value 后段字节。</param>
    /// <returns>本条 record 的逻辑地址（超溢出阈值时抛 InvalidOperationException）。</returns>
    public unsafe LogicalAddress Write(TKey key, scoped ReadOnlySpan<byte> prefix,
        scoped ReadOnlySpan<byte> value)
    {
        EnsureNotDisposed();
        EnsureReady();
        int valueLength = checked(prefix.Length + value.Length);
        if (_overflowPolicy == OverflowPolicy.Enabled && valueLength > _minOverflowSize)
            throw new InvalidOperationException("超过 Overflow 阈值的分段 Ring 写入需要使用连续 value 的 Write/WriteAsync");

        return WriteRecordCore(key, prefix, value, 0);
    }

    /// <summary>
    /// ★ 公开墓碑写入（KV 删除标记——spec §1 KV 组合配方）：写入 FLAG_RINGRECORD_TOMBSTONE + 空 value，
    /// 返回墓碑地址。恢复时索引重建跳过墓碑（RecordKey.IsTombstone 过滤——已删 key 不复活）。
    /// <para>大 value 场景墓碑为空 payload（无溢出——删除标记轻量）。</para>
    /// </summary>
    /// <param name="key">要删除的 key（写入 FLAG_RINGRECORD_TOMBSTONE 墓碑 record）。</param>
    /// <returns>墓碑 record 的逻辑地址（恢复/索引重建按 RecordKey.IsTombstone 过滤）。</returns>
    public LogicalAddress WriteTombstone(TKey key)
        => WriteWithFlags(key, [], RecordFlags.FLAG_RINGRECORD_TOMBSTONE);

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
        // ★ 注意：WriteRecordCore 里 payload = key + AddressInfo（AddressInfoCodec.StructSize），PayloadLength = keyLen + AddressInfoCodec.StructSize。
        return WriteRecordCore(key, value, flags);
    }

    /// <summary>
    /// ★ 公开异步写入：大 value 走真异步溢出路径（<see cref="WriteOverflowAsync"/>），小 value 走同步快路径。
    /// <para>★ 快/慢路径分离——inline 写通常纯内存操作（页满让渡时 Allocate 内可能触发
    /// FlushUntil→引擎 Flush/fsync，同步完成）；溢出落盘才需 await。</para>
    /// </summary>
    /// <param name="key">待写 key（TKey 定长 blittable）。</param>
    /// <param name="value">payload 字节（超溢出阈值走真异步溢出路径）。</param>
    /// <param name="ct">取消令牌（溢出慢路径响应取消）。默认 <c>default</c>。</param>
    /// <returns>完成后结果为本条 record 的逻辑地址（inline 快路径同步完成）。</returns>
    public ValueTask<LogicalAddress> WriteAsync(TKey key, ReadOnlyMemory<byte> value, CancellationToken ct = default)
    {
        EnsureNotDisposed();
        EnsureReady();
        if (_overflowPolicy == OverflowPolicy.Enabled && value.Length > _minOverflowSize)
            return WriteOverflowThenRecordAsync(key, value, ct);   // 慢路径:真异步
        return new ValueTask<LogicalAddress>(WriteRecordCore(key, value.Span, 0));  // 快路径:同步,零 async 开销
    }

    /// <summary>★ 公开异步墓碑写入（KV 删除——对齐 <see cref="WriteTombstone"/> 的异步版）。</summary>
    /// <param name="key">要删除的 key。</param>
    /// <param name="ct">取消令牌（当前实现纯内存写，未消费）。默认 <c>default</c>。</param>
    /// <returns>完成后结果为墓碑 record 的逻辑地址（同步完成，零 async 开销）。</returns>
    public ValueTask<LogicalAddress> WriteTombstoneAsync(TKey key, CancellationToken ct = default)
    {
        EnsureNotDisposed();
        EnsureReady();
        return new ValueTask<LogicalAddress>(WriteRecordCore(key, [], RecordFlags.FLAG_RINGRECORD_TOMBSTONE));
    }

    /// <summary>★ 引擎/子类异步扩展点（带 flags）。对齐 <see cref="WriteWithFlags"/> 的异步版。</summary>
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

    /// <summary>
    /// ★ 原位更新既有 record 的 value（同步）——按「旧/新是否溢出」四分支翻转：
    /// overflow→overflow 更新溢出帧指针、inline→overflow 补 OVERFLOW 位并写溢出帧、
    /// overflow→inline 回退（清 OVERFLOW 位 + 容量校验 + 拷值）、inline→inline 重写 payload 长度 + 拷值，
    /// 每分支后重写 header 与 CRC。
    /// </summary>
    /// <param name="addr">目标 record 的逻辑地址。</param>
    /// <param name="newValue">新 value 字节。</param>
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
            fields = new RingRecordFields(fields.Flags, (uint)(keyLen + AddressInfoCodec.StructSize), fields.PaddingLength, fields.PreviousAddress);
            RingCodec.WriteHeader(headerSpan, in fields);
            var recordSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize + keyLen + AddressInfoCodec.StructSize);
            RingCodec.FillCrc(recordSpan, RingCodec.HeaderSize, keyLen + 24);
        }
        else if (!wasOverflow && willOverflow)
        {
            LogicalAddress ovAddr = WriteOverflow(newValue);
            var ai = AddressInfo.WriteInfo(ovAddr, newValue.Length);
            fields = new RingRecordFields((ushort)(fields.Flags | RecordFlags.FLAG_VALUE_OVERFLOW), (uint)(keyLen + AddressInfoCodec.StructSize), fields.PaddingLength, fields.PreviousAddress);
            RingCodec.WriteHeader(headerSpan, in fields);
            Unsafe.WriteUnaligned(ref Unsafe.AsRef<byte>((void*)(phys + valOff)), ai);
            var recordSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize + keyLen + AddressInfoCodec.StructSize);
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
    /// <param name="addr">目标 record 的逻辑地址。</param>
    /// <param name="newValue">新 value 字节（overflow→inline 回退时受原槽 inline 容量约束）。</param>
    /// <param name="ct">取消令牌（溢出慢路径响应取消）。默认 <c>default</c>。</param>
    /// <returns>完成后原位更新结束（纯 inline 分支同步完成）。</returns>
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
        var updated = new RingRecordFields(newFlags, (uint)(keyLen + AddressInfoCodec.StructSize), fields.PaddingLength, fields.PreviousAddress);
        WriteOverflowPointerInline(phys, keyLen, in updated, ai);
    }

    /// <summary>★ 同步把溢出指针写回 inline record（header + AddressInfo + CRC）——unsafe 集中点。</summary>
    private unsafe void WriteOverflowPointerInline(long phys, int keyLen, in RingRecordFields updated, AddressInfo ai)
    {
        int valOff = RingCodec.HeaderSize + keyLen;
        var headerSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize);
        RingCodec.WriteHeader(headerSpan, in updated);
        Unsafe.WriteUnaligned(ref Unsafe.AsRef<byte>((void*)(phys + valOff)), ai);
        var recordSpan = new Span<byte>((void*)phys, RingCodec.HeaderSize + keyLen + AddressInfoCodec.StructSize);
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
