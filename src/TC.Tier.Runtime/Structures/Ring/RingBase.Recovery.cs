using TC.Tier.Core.Primitives;
using TC.Tier.Core.Shared;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase 恢复 partial（DefaultRingRecovery——<see cref="RecoveryBase{THints}"/> 模板派生，用户纠偏：不裸写 IRecovery）。
/// <para>★ 四级回退：RingRecoveryHints → meta(O(1) 读 RingMetaPayload + KeySize 锚点校验) → 引擎 CommittedTail → 扫盘。</para>
/// <para>★ 时序（lifecycle.md §3 模板）：WaitForDependenciesAsync（主+溢出+meta 三引擎异步 join）→
///   OnRecoveryCoreAsync（页池/水位/策略装配 → 四级回退）。CAS 闸门/状态机/MarkReady 全在模板——
///   裸写状态机与 LifecycleBase 闸门语义不同步，满套调度延迟下 WaitForReady 在水位应用前放行（旧病销案）。</para>
/// </summary>
public abstract partial class RingBase<TKey>
{
    /// <summary>
    /// 默认恢复实现（模板派生——只填恢复算法）。
    /// </summary>
    private protected class DefaultRingRecovery(RingBase<TKey> owner) : RecoveryBase<RingRecoveryHints>
    {
        /// <summary>层间 join——主引擎 + 溢出引擎 + meta 引擎（Managed 模式）全异步轨（OnInitializeBegin 已并行启动）。</summary>
        protected override async ValueTask WaitForDependenciesAsync(CancellationToken ct)
        {
            await owner._engine.WaitForReadyAsync(ct).ConfigureAwait(false);
            if (owner._overflowEngine is { } ov)
                await ov.WaitForReadyAsync(ct).ConfigureAwait(false);
            if (owner._metaEngine is { } me)
                await me.WaitForReadyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// ★ 恢复核心（模板唯一必 override）——装配序列 + 四级回退。
        /// <para>装配顺序固定：(a) InitializePagePool（纯内存）→ (b) InitWatermarks（引擎基线，必须在
        ///   ApplyWatermarks 前否则覆盖被抹）→ (c) OnInitialize（coldPageCache + RecoverOverflowTail）。</para>
        /// </summary>
        protected override async ValueTask OnRecoveryCoreAsync(RingRecoveryHints hints, CancellationToken ct)
        {
            RaiseProgress(10, "page pool / watermarks / policies");

            owner.InitializePagePool(owner._settings.Preallocate);
            owner.InitWatermarks();
            owner.OnInitialize();
            ct.ThrowIfCancellationRequested();

            // ① hints.RecoveredTail（外部主动注入最高）
            if (hints.RecoveredTail is { } hintTail)
            {
                ApplyWatermarks(hintTail, hints.FlushedUntilAddress ?? hintTail);
                return;
            }
            if (hints.FlushedUntilAddress is { } hintFlushed)
            {
                ApplyWatermarks(hintFlushed, hintFlushed);
                return;
            }

            // ② meta（O(1) 正路）——KeySize 锚点校验：防拿错特化开同一个卷（fail-fast，设计稿 §1.3）
            RaiseProgress(20, "meta");
            if (await owner.MetaPolicy.LoadAsync(ct).ConfigureAwait(false) && owner.MetaPolicy.ReadMetaPayload() is { } p)
            {
                owner.ValidateKeySizeAnchor(p.KeySize);
                ApplyWatermarks(p.TailAddress, p.FlushedUntilAddress, p.BeginAddress,
                                p.ReadOnlyAddress, p.SafeReadOnlyAddress, p.LastCommittedSeq);
                // ★ 2PC 事务水位还原（悬干裁决依据——TransactionLog.LoadAndReconcile 据此判
                //   LastPreparedSeq > LastCommittedSeq 并驱动 Abort；不还原则恢复后恒 -1、悬干永不可见）。
                Volatile.Write(ref owner._lastCommittedSeq, p.LastCommittedSeq);
                Volatile.Write(ref owner._lastPreparedSeq, p.LastPreparedSeq);
                owner._txRollbackTail = p.CommittedTailAddress;   // D2 回退点（旧块缺省 Empty = 无窗口）
                return;
            }
            ct.ThrowIfCancellationRequested();

            // ③ 引擎 CommittedTail（已落盘区域）
            LogicalAddress engineTail = owner._engine.CommittedTail;
            if (engineTail > owner._engine.MinAddress)
            {
                ApplyWatermarks(engineTail, engineTail);
                return;
            }

            // ④ 扫盘找 torn write 边界
            RaiseProgress(50, "scanning tail");
            LogicalAddress scannedTail = await ScanTailAsync(ct).ConfigureAwait(false);
            ApplyWatermarks(scannedTail, scannedTail);
            RaiseProgress(90, $"scanned tail={scannedTail}");
        }

        /// <summary>应用恢复的水位（推进 owner 的 LogicalAddress 指针字段）。</summary>
        private void ApplyWatermarks(LogicalAddress tail, LogicalAddress flushedUntil,
            LogicalAddress? begin = null, LogicalAddress? readOnly = null, LogicalAddress? safeReadOnly = null, long? committedSeq = null)
        {
            owner._tailAddress = tail;
            owner._flushedUntilAddress = flushedUntil;
            if (begin is { } ba) owner._beginAddress = ba;
            if (readOnly is { } roa) owner._readOnlyAddress = roa;
            if (safeReadOnly is { } sroa) owner._safeReadOnlyAddress = sroa;
            RaiseProgress(95, $"tail={tail}, flushedUntil={flushedUntil}" +
                (committedSeq is { } cs ? $", committedSeq={cs}" : ""));
        }

        /// <summary>★ 扫盘找 tail（MagicLocator 粗锚点 + 前向 CRC 扫 record）。
        /// Monotone 快速档——稠密 record 流含 magic 页单调（Ring 布局断言）。</summary>
        private async ValueTask<LogicalAddress> ScanTailAsync(CancellationToken ct)
        {
            uint[] magics = [RecordMagic.BlittableRing];
            var loc = await owner._engine.LocateAsync(magics, MagicDirection.Last,
                owner._engine.MinAddress, owner._engine.AllocatedTail,
                owner.PageSize, owner.RingCodec.Alignment, MagicLocateStrategy.Monotone, ct).ConfigureAwait(false);
            if (!loc.Found) return owner.BeginAddress;
            // 从 PageAddress 页起点开始前向扫（保证从页起点解析 record）
            return await ForwardScanRecordsAsync(loc.PageAddress, ct).ConfigureAwait(false);
        }

        /// <summary>★ 阶段 2：从 region 起点前向逐页扫 record，返回最后有效 record 尾。</summary>
        private async ValueTask<LogicalAddress> ForwardScanRecordsAsync(LogicalAddress regionStart, CancellationToken ct)
        {
            var engine = owner._engine;
            int pageSize = owner.PageSize;
            int headerSize = owner.RingCodec.HeaderSize;
            int alignment = owner.RingCodec.Alignment;
            var codec = owner.RingCodec;
            byte[] pageBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(pageSize);
            LogicalAddress lastValidEnd = owner.BeginAddress;
            try
            {
                LogicalAddress addr = regionStart;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    int got = await engine.ReadAsync(addr, pageBuf.AsMemory(0, pageSize), ct).ConfigureAwait(false);
                    if (got <= 0) break;
                    var scanResult = ScanPageForRecords(pageBuf.AsSpan(0, got), addr, pageSize, owner.PageSizeMask, headerSize, alignment, codec);
                    if (scanResult is { } end) lastValidEnd = end;
                    else break;   // 坏帧/空页 → 停
                    addr = engine.CalculationAddress(addr, pageSize);
                }
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(pageBuf); }
            return lastValidEnd;
        }

        /// <summary>扫描单页内的 records，返回最后一个有效 record 的尾地址。null = 遇坏帧。</summary>
        private static LogicalAddress? ScanPageForRecords(Span<byte> pageData, LogicalAddress pageAddr, int pageSize, int pageMask, int headerSize, int alignment, IRingCodec codec)
        {
            LogicalAddress lastValidEnd = LogicalAddress.Empty;
            long pageOff = pageAddr.Offset;
            long pageEndOff = pageOff + pageSize;
            // ★ 同段内推进：pageAddr.SegId 不变，Offset += aligned（跨段时上层 reader 已切段）。
            for (long addrOff = pageOff; addrOff + headerSize <= pageEndOff; )
            {
                int off = (int)(addrOff - pageOff);
                if (!codec.TryReadHeader(pageData.Slice(off, headerSize), out var fields))
                {
                    if (codec.IsEmptyRecord(pageData.Slice(off, headerSize)))
                    {
                        addrOff += alignment;
                        continue;
                    }
                    return lastValidEnd != LogicalAddress.Empty ? lastValidEnd : null;
                }

                int payloadLen = (int)fields.PayloadLength;
                int total = headerSize + payloadLen + fields.PaddingLength;
                int crcCoverEnd = headerSize + payloadLen;

                if (off + crcCoverEnd > pageSize) return lastValidEnd != LogicalAddress.Empty ? lastValidEnd : null;

                if (!codec.VerifyCrc(pageData.Slice(off, crcCoverEnd), headerSize, payloadLen))
                    return lastValidEnd != LogicalAddress.Empty ? lastValidEnd : null;

                int aligned = (total + alignment - 1) & ~(alignment - 1);

                if ((fields.Flags & RecordFlags.FLAG_ENTRY_IS_META) != 0)
                {
                    addrOff += aligned;
                    continue;
                }

                lastValidEnd = new LogicalAddress(pageAddr.SegId, addrOff + aligned);
                addrOff += aligned;
            }

            return lastValidEnd != LogicalAddress.Empty ? lastValidEnd : null;
        }
    }
}
