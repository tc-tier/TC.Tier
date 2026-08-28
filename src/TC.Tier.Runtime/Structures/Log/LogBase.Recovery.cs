namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// LogBase 恢复嵌套类 DefaultLogRecovery / <see cref="LogRecovery{TLogBase}"/>——
/// <see cref="RecoveryBase{THints}"/> 模板派生（CAS 闸门/状态机/进度上报/MarkReady 全在模板，
/// 本类只填层间 join 与恢复算法）。恢复优先级统一（设计决策）：① hints（外部主动注入，最高）
/// → ② meta（持久化水位）→ ③ 扫盘——对齐 Metadata/Mirror/Snapshot。
/// </summary>
public abstract partial class LogBase
{
    /// <summary>★ 恢复算法工厂——默认 DefaultLogRecovery。在 Initialize 的 CAS 闸门内被调一次
    /// （基类单一创建点）；注入实例经构造函数直接赋 _recovery，不经本工厂。</summary>
    protected override IRecovery<LogRecoveryHints> CreateRecovery()
        => new DefaultLogRecovery(this);

    /// <summary>默认恢复策略（嵌套类）。</summary>
    private sealed class DefaultLogRecovery(LogBase owner) : LogRecovery<LogBase>(owner);

    /// <summary>
    /// Log 恢复基类（<see cref="RecoveryBase{THints}"/> 模板派生，子类可继承后 override
    /// <see cref="OnLogRecovered"/> 做依赖恢复结果的装配）。
    /// </summary>
    /// <typeparam name="TLogBase">日志基类类型。</typeparam>
    protected abstract class LogRecovery<TLogBase>(TLogBase owner) : RecoveryBase<LogRecoveryHints>
        where TLogBase : LogBase
    {
        /// <summary>层间 join——主引擎 + meta 引擎（Managed 模式）双 await，全异步轨。
        /// <para>两引擎在 OnInitializeBegin 已并行启动，此处只 join——零同步阻塞。</para></summary>
        protected override async ValueTask WaitForDependenciesAsync(CancellationToken ct)
        {
            await owner._engine.WaitForReadyAsync(ct).ConfigureAwait(false);
            if (owner._metaEngine is { } metaEngine)
                await metaEngine.WaitForReadyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// ★ 恢复核心：InitializeForWrites（依赖 SectorSize 的装配，引擎就绪后）→ 三级回退
        /// ① hints（外部主动注入的初始化水位——最高优先级，TailAddress 精确 → FileSize 近似）
        /// ② meta（结构自管持久化水位）③ 扫盘。与 Metadata/Mirror/Snapshot 统一（设计决策）。
        /// </summary>
        protected override async ValueTask OnRecoveryCoreAsync(LogRecoveryHints hints, CancellationToken ct)
        {
            // ★ 依赖引擎就绪的初始化（SectorSize）——引擎后台恢复未完成时 SectorSize=0 会导致
            //   AlignedMemoryManager alignment 越界，故在恢复核心头部（依赖已 join）执行。
            owner.InitializeForWrites();
            ct.ThrowIfCancellationRequested();

            RaiseProgress(10, "hints/meta/scan");

            // meta 无条件 Load（O(1)——供后续 ReadOpaqueMeta/水位读取；Disabled no-op 返回 false），
            // 但地址裁决 hints 优先：外部主动注入是调用方最强知识。
            LogMetaPayload? metaPayload =
                await owner.MetaPolicy.LoadAsync(ct).ConfigureAwait(false)
                    ? owner.MetaPolicy.ReadMetaPayload()
                    : null;

            // ① hints.TailAddress（上层已知 tail——外部主动注入，最高优先级）
            if (hints.TailAddress is { } hintTail)
            {
                owner._logicalTail = hintTail;
                RaiseProgress(90, $"hints tail={hintTail}");
                ReconcileEngineTail();
                OnLogRecovered();
                return;
            }

            // ①' hints.FileSize（DeltaLog 临时文件场景——近似水位，仍属外部注入）
            if (hints.FileSize is { } fileSize)
            {
                owner._logicalTail = new LogicalAddress(0, fileSize);
                RaiseProgress(90, $"file size={fileSize}");
                ReconcileEngineTail();
                OnLogRecovered();
                return;
            }

            // ② meta（持久化水位）
            if (metaPayload is { } payload && payload.TailAddress > LogicalAddress.Empty)
            {
                owner._logicalTail = payload.TailAddress;
                // ★ 2PC 事务水位还原（悬干裁决依据——TransactionLog.LoadAndReconcile 据此判
                //   LastPreparedSeq > LastCommittedSeq 并驱动 Abort；不还原则恢复后恒 -1、悬干永不可见）。
                Volatile.Write(ref owner._lastCommittedSeq, payload.LastCommittedSeq);
                Volatile.Write(ref owner._lastPreparedSeq, payload.LastPreparedSeq);
                owner._txRollbackTail = payload.PreparedTailAddress;   // 回退点（旧块缺省 Empty = 无窗口）
                RaiseProgress(90, $"meta tail={payload.TailAddress}");
                ReconcileEngineTail();
                OnLogRecovered();
                return;
            }

            // ③ 扫盘兜底
            RaiseProgress(50, "scanning tail");
            var scannedTail = await ScanTailAsync(ct).ConfigureAwait(false);
            owner._logicalTail = scannedTail;
            RaiseProgress(90, $"scanned tail={scannedTail}");
            ReconcileEngineTail();
            OnLogRecovered();
        }

        /// <summary>★ 恢复后引擎尾对齐：退到 Log 真实尾（引擎 AllocatedTail 可能超前——窗口预留
        ///   空洞），否则恢复后续写从引擎尾起（与 Log 尾之间间隙空洞——重放撞零停）。
        ///   水位在恢复裁决后、OnLogRecovered 前统一执行。</summary>
        /// <remarks>★ 帧边界对齐：meta/hints 记录的尾 = 末帧<b>数据尾</b>（header+data+CRC 终点），
        ///   但帧在扇区对齐后还有尾部 padding——恢复后续写的首个 frame 必须落在 padding 之后的
        ///   帧边界（padded end），否则 cursor 读完末帧 CRC 跳过 padding 后找不到新帧 header，
        ///   重启后追加的 entry 全部不可读（Restart_AppendContinues 100/101 实锤）。
        ///   截断点尾（TruncateSuffix 的 entry 起点）不是帧数据尾——残基不同，不误对齐。</remarks>
        private void ReconcileEngineTail()
        {
            LogicalAddress logicalTail = default;
            try
            {
                logicalTail = owner._logicalTail;
                if (!logicalTail.IsValid) return;
                var target = AlignToPaddedFrameEnd(logicalTail);
                // ★ 引擎尾已 ≤ target（物理尾即目标）时无需退（ReclaimTail 只收缩——等于会抛）；
                //   引擎尾超前 target（窗口预留/预分配/元组水位）→ 收缩到 target（退掉预留空洞，
                //   帧边界之后续写，cursor 顺序读跨帧无洞）。
                if (owner._engine.AllocatedTail > target)
                    owner._engine.ReclaimTail(target);
            }
            catch (Exception ex)
            {
                // 对齐失败不阻断恢复（水位裁决已完成）——后续 Allocate 可能留间隙（重放见空洞停）
                owner.Logger?.LogWarning(ex, "ReconcileEngineTail: engine tail reconciliation failed (recovered tail {Tail})",
                    logicalTail);
            }
        }

        /// <summary>
        /// ★ 尾地址 → 帧边界（padded end）对齐：仅当尾恰是末帧数据尾（≡ frameHeader+CRC 开销 mod 扇区）
        ///   且该帧 header 经探测验证（magic + dataLen 自洽）时，把尾推进到 padding 之后的帧边界。
        /// <para>不满足（截断点/未知地址/无 padding 的扇区形态）→ 原样返回，不猜测。</para>
        /// </summary>
        private LogicalAddress AlignToPaddedFrameEnd(LogicalAddress tail)
        {
            int sector = (int)owner.SectorSize;
            int dataEndOverhead = LogPageFrameHeaderCodec.StructSize + Crc32FooterCodec.StructSize;   // 8+4
            // 扇区 ≤ 开销 → ComputeFramePadding 恒 0（无 padding 形态）——无需对齐
            if (sector <= dataEndOverhead) return tail;
            // 帧数据尾 = 帧首 + 8 + dataLen + 4，dataLen 恒扇区倍数 ⇒ 数据尾 ≡ overhead (mod sector)
            if (tail.Offset % sector != dataEndOverhead % sector) return tail;

            // ★ 探测验证：真帧数据尾前必有该帧 header（magic + dataLen == tail−frameStart−overhead）。
            //   排除恰好落在同残基上的截断点（entry 起点 4 对齐——理论上可撞 12 mod 512）。
            Span<byte> hdr = stackalloc byte[LogPageFrameHeaderCodec.StructSize];
            for (long back = 0; ; back += sector)
            {
                long candidate = tail.Offset - dataEndOverhead - back;
                if (candidate < 0) break;
                var addr = new LogicalAddress(tail.SegId, candidate);
                if (owner._engine.Read(addr, hdr) < LogPageFrameHeaderCodec.StructSize) continue;
                var h = LogPageFrameHeaderCodec.Read(hdr);
                if (h.MagicValue != RecordMagic.LogPageFrame) continue;
                if (h.DataLength != tail.Offset - candidate - dataEndOverhead) continue;   // dataLen 自洽
                if (h.DataLength <= 0 || h.DataLength > owner.PageSize) continue;
                int padding = owner.ComputeFramePadding((int)h.DataLength);
                if (padding <= 0) return tail;
                return owner._engine.CalculationAddress(tail, padding);
            }
            return tail;
        }

        /// <summary>★ 恢复后钩子——<see cref="OnRecoveryCoreAsync"/> 末尾、MarkReady 前调用。
        /// <para>子类 override 在此做"依赖恢复结果"的装配（EntryLog 设 CommittedOffset + 启提交循环）。
        /// 默认空——DeltaLog 等无此需求的子类不 override。</para></summary>
        protected virtual void OnLogRecovered() { }

        /// <summary>同步扫盘找 tail——从 MinAddress（已知帧边界）前向走帧到最后一个有效帧尾。</summary>
        private LogicalAddress ScanTailSync()
            => ForwardScanFromRegionSync(owner._engine.MinAddress);

        /// <summary>
        /// 前向走帧，返回最后一个有效 frame 尾（= 真实 _logicalTail）。
        /// <para>★ 帧布局 = 顺序追加 + 扇区填充，<b>非页对齐</b>（帧可跨扫描页）——起点必须是
        ///   已知帧边界（MinAddress/BeginAddress），不能用 MagicLocator 的页起点（会落在上一帧
        ///   中段的填充零里断链——DIO 小页场景实测暴露）。扫到 magic 不连续处 = 空洞/EOF。</para>
        /// </summary>
        private LogicalAddress ForwardScanFromRegionSync(LogicalAddress regionStart)
        {
            LogicalAddress lastFrameEnd = LogicalAddress.Empty;
            int hdrLen = LogPageFrameHeaderCodec.StructSize;
            int crcLen = Crc32FooterCodec.StructSize;
            using var reader = owner._engine.OpenSequentialReader(regionStart, owner._engine.AllocatedTail,
                ReadDirection.Forward, usePageCache: true, SnapshotMode.Consistent);
            Span<byte> hdrBuf = stackalloc byte[hdrLen];
            while (reader.Position < reader.End)
            {
                LogicalAddress frameStart = reader.Position;
                if (reader.Read(hdrBuf) < hdrLen) break;
                var hdr = LogPageFrameHeaderCodec.Read(hdrBuf);
                if (hdr.MagicValue != RecordMagic.LogPageFrame) break;   // 空洞/EOF（全零 magic）
                int dataLen = hdr.DataLength;
                if (dataLen <= 0 || dataLen > owner.PageSize) break;
                reader.Skip(dataLen + crcLen + owner.ComputeFramePadding(dataLen));
                lastFrameEnd = owner._engine.CalculationAddress(frameStart, hdrLen + dataLen + crcLen);
            }
            return lastFrameEnd;
        }

        /// <summary>异步扫盘找 tail——从 MinAddress（已知帧边界）前向走帧到最后一个有效帧尾。</summary>
        private async ValueTask<LogicalAddress> ScanTailAsync(CancellationToken ct)
            => await ForwardScanFromRegionAsync(owner._engine.MinAddress, ct).ConfigureAwait(false);

        /// <summary>阶段 2（异步）：从 page 起点前向扫帧求精。对等同步版。</summary>
        private async ValueTask<LogicalAddress> ForwardScanFromRegionAsync(LogicalAddress regionStart, CancellationToken ct)
        {
            LogicalAddress lastFrameEnd = LogicalAddress.Empty;
            int hdrLen = LogPageFrameHeaderCodec.StructSize;
            int crcLen = Crc32FooterCodec.StructSize;
            using var reader = owner._engine.OpenSequentialReader(regionStart, owner._engine.AllocatedTail,
                ReadDirection.Forward, usePageCache: true, SnapshotMode.Consistent);
            byte[] hdrArr = new byte[hdrLen];
            while (reader.Position < reader.End)
            {
                LogicalAddress frameStart = reader.Position;
                if (await reader.ReadAsync(hdrArr, ct).ConfigureAwait(false) < hdrLen) break;
                var hdr = LogPageFrameHeaderCodec.Read(hdrArr);
                if (hdr.MagicValue != RecordMagic.LogPageFrame) break;
                int dataLen = hdr.DataLength;
                if (dataLen <= 0 || dataLen > owner.PageSize) break;
                reader.Skip(dataLen + crcLen + owner.ComputeFramePadding(dataLen));
                lastFrameEnd = owner._engine.CalculationAddress(frameStart, hdrLen + dataLen + crcLen);
            }
            return lastFrameEnd;
        }
    }
}
