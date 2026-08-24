using System.Buffers.Binary;

namespace TC.Tier.Runtime.Structures.Snapshot;

/// <summary>恢复 partial（DefaultSnapshotRecovery 嵌套类——<see cref="RecoveryBase{THints}"/> 模板派生）。</summary>
public abstract partial class SnapshotBase
{
    /// <summary>
    /// 默认恢复实现（派生 <see cref="RecoveryBase{SnapshotRecoveryHints}"/>——状态机/闸门/进度全在模板）。
    /// 三级回退：SnapshotRecoveryHints → meta.Load(O(1) 水位) → Backward 扫描找帧尾兜底。
    /// <para>★ GB/TB 级数据量恢复绝不能全盘扫描——meta O(1) 拿真尾是刚需，Backward 扫描（O(1) 块级
    ///   从尾找 FooterMagic）只是 Disabled 兜底。</para>
    /// </summary>
    private protected class DefaultSnapshotRecovery(SnapshotBase owner) : RecoveryBase<SnapshotRecoveryHints>
    {
        /// <summary>层间 join——主引擎 + meta 引擎（Managed 模式）双 await，全异步轨（OnInitializeBegin 已并行启动）。</summary>
        protected override async ValueTask WaitForDependenciesAsync(CancellationToken ct)
        {
            await owner._engine.WaitForReadyAsync(ct).ConfigureAwait(false);
            if (owner._metaEngine is { } metaEngine)
                await metaEngine.WaitForReadyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// ★ 恢复核心——装配 MetaPolicy → meta.Load → 三级回退恢复三水位 → 悬干裁决（append 回滚）。
        /// </summary>
        protected override async ValueTask OnRecoveryCoreAsync(SnapshotRecoveryHints hints, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();


            SnapshotMetaPayload? metaPayload = null;
            if (owner._settings.MetaPolicyKind != MetaPolicyKind.Disabled)
            {
                await owner.MetaPolicy.LoadAsync(ct).ConfigureAwait(false);
                metaPayload = owner.MetaPolicy.ReadMetaPayload();
            }
            ct.ThrowIfCancellationRequested();

            // 三级回退：hints → meta → Backward 扫描
            if (hints.WriteAddress is { } hWrite)
            {
                // 1. hints 优先
                owner._writeAddress = hWrite;
                owner._physicalWriteAddress = hints.PhysicalWriteAddress ?? owner.AlignUpToSector(hWrite);
                RaiseProgress(90, $"hints write={hWrite}");
            }
            else if (metaPayload is { } p)
            {
                // 2. meta O(1) 水位
                owner._writeAddress = p.WriteAddress;
                owner._physicalWriteAddress = p.PhysicalWriteAddress;
                owner._truncatedAddress = p.TruncatedAddress;
                owner._committedWriteAddress = p.CommittedWriteAddress;
                owner._lastCommittedSeq = p.LastCommittedSeq;
                owner._lastPreparedSeq = p.LastPreparedSeq;
                RaiseProgress(90, "meta watermarks");
            }
            else
            {
                // 3. Backward 扫描找帧尾（Disabled 兜底；前缀洞天然免疫——从尾向头找 FooterMagic）
                RaiseProgress(50, "backward scan frame end");
                if (owner.LocateLastFrameEnd() is { } frameEnd)
                {
                    owner._writeAddress = frameEnd;
                    owner._physicalWriteAddress = owner.AlignUpToSector(frameEnd);
                    RaiseProgress(90, $"scanned frame end={frameEnd}");
                }
            }

            // ★ 物理尾 = 实际 flush 位置（不含 Allocate 预留的未写窗口）——后续 Append 紧跟其后不留流空洞；
            //   写窗口 = 物理尾到 AllocatedTail 的剩余预留（窗口内 EOF 读语义由 backward 扫描自担）
            owner._writeWindow = owner._engine.GetDistance(
                owner._physicalWriteAddress, owner._engine.AllocatedTail);

            // 悬干裁决（append 回滚）：meta prepared > committed → 尾截断到 CommittedWriteAddress
            if (metaPayload is { } mp && mp.LastPreparedSeq > mp.LastCommittedSeq)
            {
                RaiseProgress(80, "truncating dangling append");
                owner.TruncateSuffix(mp.CommittedWriteAddress);
                owner._lastPreparedSeq = mp.LastCommittedSeq;
            }
        }
    }

    /// <summary>地址扇区上取整（物理 flush 位置基准）。</summary>
    private protected LogicalAddress AlignUpToSector(LogicalAddress addr)
        => _engine.CalculationAddress(addr, SectorAlignment.AlignUp(addr.Offset, _sectorSize) - addr.Offset);

    /// <summary>
    /// ★ Backward 扫描找最后一个帧尾地址（= 逻辑写尾）：从 CommittedTail 向头按块扫描找 FooterMagic。
    /// <para>从尾向头——前缀洞（TruncatePrefix 段内打洞）天然免疫。找到的 magic 需后跟完整 Footer。</para>
    /// </summary>
    /// <returns>最后一个帧尾地址；无帧返回 null。</returns>
    private protected LogicalAddress? LocateLastFrameEnd()
    {
        const int chunkSize = 64 * 1024;
        var sectorSize = _sectorSize;
        var lo = _engine.MinAddress;
        var hi = _engine.CommittedTail;
        if (hi <= lo) return null;
        long dist = _engine.GetDistance(lo, hi);
        if (dist <= 0) return null;

        Span<byte> magic = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(magic, _codec.FooterMagic);

        using var buf = new AlignedMemoryManager(chunkSize, sectorSize);
        long scanned = 0; // 从尾向头已扫的字节数
        while (scanned < dist)
        {
            long want = Math.Min(chunkSize, dist - scanned);
            // 块起点 = lo + (dist - scanned - want) —— 距离算术定块位（CalculationAddress 已支持 ±）
            var chunkStart = _engine.CalculationAddress(lo, dist - scanned - want);
            int got = _engine.Read(chunkStart, buf.GetSpan(0, (int)want));
            if (got > 0)
            {
                var span = buf.GetSpan(0, got);
                for (int i = got - 4; i >= 0; i--)
                {
                    if (span.Slice(i, 4).SequenceEqual(magic) && i + _codec.FooterSize <= got)
                    {
                        // footer magic 命中 + Footer 完整可读 → 帧尾 = magic 位置 + FooterSize
                        return _engine.CalculationAddress(chunkStart, i + _codec.FooterSize);
                    }
                }
            }

            // ★ got==0（Allocate 预留窗口未写区=EOF 语义）也按 want 推进——backward 扫描不受未写窗口阻断
            scanned += want;
        }

        return null;
    }

    /// <summary>
    /// 地址回退 n（引擎 CalculationAddress 支持 ±——负长度=回退，跨段借位正确；
    /// 回退越过 MinAddress 引擎返回 Invalid，由调用方保证不越界）。
    /// </summary>
    /// <param name="addr">目标地址。</param>
    /// <param name="n">回退字节数。</param>
    private protected LogicalAddress RetreatFrom(LogicalAddress addr, long n)
        => _engine.CalculationAddress(addr, -n);
}
