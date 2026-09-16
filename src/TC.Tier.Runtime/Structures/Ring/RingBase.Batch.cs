using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase 批量写 partial——<see cref="WriteBatch"/> 独占窗口批量追加（窗口领取 + 批内无锁写）。
/// </summary>
public abstract partial class RingBase<TKey>
{
    /// <summary>
    /// ★ 批量写（ref struct，using 包裹）——大批量 key 写入的常用形态（组合层批量编排）+ 多写者窗口。
    /// <para><b>窗口模型（多写者无锁分配）</b>：每个批 = 一个独占窗口（整页段）——领取时在
    ///   <c>_tailLock</c> 内把 tail 一次性推进到窗口尾（预留段，批独占）；批内 record 分配
    ///   <b>完全无锁</b>（页内游标推进，零锁零页检查零 EnsureSpace）。多写者各持窗口并发写——
    ///   锁竞争从"每 record 一次"降到"每窗口一次"（≈ 每页一次/写者）。</para>
    /// <para>★ 窗口 = 整页（record 超页尾 → 领下一页）；未写满的窗口尾 = 预留空白（无 Seal 不可读，
    ///   驱逐按页 flush 零填充无碍）。地址语义与单条 Write 完全一致（批独占连续段）。</para>
    /// </summary>
    /// <returns>新开的 <see cref="WriteBatch"/> 批窗口（ref struct，须 Dispose 释放 epoch）。</returns>
    public WriteBatch BeginWriteBatch()
    {
        EnsureNotDisposed();
        _epoch.Resume();                 // 批生命周期持 epoch（页驱逐保护——批窗口页不驱逐）
        try
        {
            var batch = new WriteBatch(this);
            batch.EnsureWindow();        // 首窗口（领取——tail 预留窗口段）
            return batch;
        }
        catch
        {
            _epoch.Suspend();
            throw;
        }
    }

    /// <summary>批量 ref struct——独占窗口（领取预留 + 批内无锁），Dispose 释放 epoch。</summary>
    public ref struct WriteBatch
    {
        private readonly RingBase<TKey> _owner;
        private bool _open;
        private LogicalAddress _pageAddr;   // 当前窗口页起点（逻辑）
        private long _physBase;             // 当前窗口页物理基址（页内偏移直加，省逐 record 地址解码）
        private int _pageIntra;             // 窗口内页内偏移（record 游标）
        private int _count;

        internal WriteBatch(RingBase<TKey> owner)
        {
            _owner = owner;
            _open = true;
            _pageAddr = LogicalAddress.Empty;
            _physBase = 0;
            _pageIntra = 0;
            _count = 0;
        }

        /// <summary>批内已写 record 数。</summary>
        public int Count => _count;

        /// <summary>批量追加单条 record（窗口内无锁推进；窗口耗尽自动领新窗口）。
        /// 同一 WriteBatch 实例不可并发调用；不同批各持窗口可并发。</summary>
        /// <param name="key">待写 key（TKey 定长 blittable，按 sizeof(TKey) 原样写入）。</param>
        /// <param name="value">payload 字节（与 key 拼为同一条 record 的负载）。</param>
        /// <returns>本条 record 的逻辑地址（写完即在页池完整可见）。</returns>
        public unsafe LogicalAddress Append(TKey key, ReadOnlySpan<byte> value)
            => Append(key, value, []);

        /// <summary>分段追加单条 record；prefix/value 直接写入 Ring，避免调用方拼接临时数组。
        /// 同一 WriteBatch 实例不可并发调用；不同批各持窗口可并发。</summary>
        /// <param name="key">待写 key（TKey 定长 blittable，按 sizeof(TKey) 原样写入）。</param>
        /// <param name="prefix">payload 前段字节（与 value 顺序拼接，免临时数组）。</param>
        /// <param name="value">payload 后段字节。</param>
        /// <returns>本条 record 的逻辑地址（写完即在页池完整可见）。</returns>
        public unsafe LogicalAddress Append(TKey key, scoped ReadOnlySpan<byte> prefix,
            scoped ReadOnlySpan<byte> value)
        {
            if (!_open) throw new ObjectDisposedException(nameof(WriteBatch));

            int keyLen = RingBase<TKey>.KeySize, payloadLen = checked(prefix.Length + value.Length);
            uint totalPayload = (uint)(keyLen + payloadLen);
            int unaligned = _owner.RingCodec.HeaderSize + (int)totalPayload;
            int aligned = (unaligned + _owner.RingCodec.Alignment - 1) & ~(_owner.RingCodec.Alignment - 1);
            ushort paddingLen = (ushort)(aligned - unaligned);

            if (_pageIntra + aligned > _owner.PageSize)
                EnsureWindow();            // 窗口耗尽——领新窗口（lock 领取）

            var addr = _owner._engine.CalculationAddress(_pageAddr, _pageIntra);
            long phys = _physBase + _pageIntra;

            // ★ 写 record（与单条 WriteRecordCore 写段同步——改一处须两处同改）
            var fields = new RingRecordFields(
                (ushort)(RecordFlags.FLAG_RINGRECORD_VALID | RecordFlags.FLAG_RINGRECORD_SEALED),
                totalPayload, paddingLen, default);
            var headerSpan = new Span<byte>((void*)phys, _owner.RingCodec.HeaderSize);
            _owner.RingCodec.WriteHeader(headerSpan, in fields);
            Unsafe.WriteUnaligned((void*)(phys + _owner.RingCodec.HeaderSize), key);
            var payloadSpan = new Span<byte>((void*)(phys + _owner.RingCodec.HeaderSize + keyLen), payloadLen);
            prefix.CopyTo(payloadSpan);
            value.CopyTo(payloadSpan[prefix.Length..]);
            if (paddingLen > 0)
                new Span<byte>((void*)(phys + unaligned), paddingLen).Clear();
            var recordSpan = new Span<byte>((void*)phys, _owner.RingCodec.HeaderSize + (int)totalPayload);
            _owner.RingCodec.FillCrc(recordSpan, _owner.RingCodec.HeaderSize, (int)totalPayload);
            _owner.RingCodec.OrFlags(headerSpan, RecordFlags.FLAG_RINGRECORD_VALID | RecordFlags.FLAG_RINGRECORD_SEALED);

            // ★ header+payload+CRC 完整——推进安全快照尾（与 WriteRecordCore 同规）：batch 消息
            //   写完即在页池完整可见，消费扫描/点查按此判热直读页池（不推则判冷读设备滞后快照 → 漏投递）。
            //   AdvanceSafeSnapshotTail 的 8B 判据水位 CAS-max——多 batch 并发推进无撕裂。
            //   AdvanceSafeSnapshotTail 经 _owner 调（实例方法）；8B 判据水位 CAS-max——多 batch 并发无撕裂。
            _owner.AdvanceSafeSnapshotTail(_owner._engine.CalculationAddress(addr, aligned));

            _pageIntra += aligned;
            _count++;
            return addr;
        }

        /// <summary>
        /// 领取新窗口（lock 内）：tail 推进到页边界（若在页内）+ 背压处理 + 页登记 +
        /// <b>tail 预留整页窗口段</b>（批独占——其他写者从窗口尾后领取）。窗口 = [页起点, 页尾)。
        /// </summary>
        internal unsafe void EnsureWindow()
        {
            lock (_owner._tailLock)
            {
                _owner.EnsureSpace(_owner.PageSize);
                long tailDist = _owner.DistanceFromDataStart(_owner._tailAddress);
                long intraPage = tailDist & _owner.PageSizeMask;

                if (intraPage != 0)
                {
                    // 当前页内非零偏移——推进到下一页边界（对齐 TryAllocate 跨页分支）
                    long advanceToNextPage = _owner.PageSize - intraPage;
                    var nextPageAddr = _owner._engine.CalculationAddress(_owner._tailAddress, advanceToNextPage);
                    _owner.PageAlignedShiftReadOnlyAddress(nextPageAddr);
                    _owner.PageAlignedShiftHeadAddress(nextPageAddr);
                    // ★ 背压：环形满——flush readonly 区腾 slot（对齐 TryAllocate 循环）
                    while (_owner.NeedToWait(nextPageAddr))
                    {
                        var ro = _owner.ReadOnlyAddress;
                        var flushed = _owner.FlushedUntilAddress;
                        if (ro > flushed) _owner.FlushUntil(ro);
                        else Thread.Yield();
                    }
                    long nextPageSeq = (tailDist + advanceToNextPage) >> _owner.PageSizeBits;
                    _owner.EnsurePageAllocated(nextPageSeq);
                    _owner.EnsurePageAllocated(nextPageSeq + 1);
                    _owner._tailAddress = nextPageAddr;
                    tailDist = _owner.DistanceFromDataStart(_owner._tailAddress);
                }

                long curPageSeq = tailDist >> _owner.PageSizeBits;
                _owner.EnsurePageAllocated(curPageSeq);
                _owner.EnsurePageAllocated(curPageSeq + 1);
                _pageAddr = _owner._engine.CalculationAddress(_owner._dataStart, curPageSeq << _owner.PageSizeBits);
                _physBase = _owner.GetPhysicalAddress(_pageAddr);
                _pageIntra = 0;
                // ★ 预留窗口段：tail 推进到页尾（批独占——多写者各窗口互不重叠）
                _owner._tailAddress = _owner._engine.CalculationAddress(_pageAddr, _owner.PageSize);
            }
        }

        /// <summary>收尾：释放批（epoch——窗口段已预留，无锁可释放）。</summary>
        public void Dispose()
        {
            if (!_open) return;
            _open = false;
            _owner._epoch.Suspend();
        }
    }
}
