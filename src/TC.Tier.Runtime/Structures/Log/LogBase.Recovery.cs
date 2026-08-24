namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// LogBase 恢复嵌套类 DefaultLogRecovery / <see cref="LogRecovery{TLogBase}"/>——
/// <see cref="RecoveryBase{THints}"/> 模板派生（CAS 闸门/状态机/进度上报/MarkReady 全在模板，
/// 本类只填层间 join 与恢复算法）。恢复优先级统一（用户裁定）：① hints（外部主动注入，最高）
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
        /// ② meta（结构自管持久化水位）③ 扫盘。与 Metadata/Mirror/Snapshot 统一（用户裁定）。
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
                OnLogRecovered();
                return;
            }

            // ①' hints.FileSize（DeltaLog 临时文件场景——近似水位，仍属外部注入）
            if (hints.FileSize is { } fileSize)
            {
                owner._logicalTail = new LogicalAddress(0, fileSize);
                RaiseProgress(90, $"file size={fileSize}");
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
                OnLogRecovered();
                return;
            }

            // ③ 扫盘兜底
            RaiseProgress(50, "scanning tail");
            var scannedTail = await ScanTailAsync(ct).ConfigureAwait(false);
            owner._logicalTail = scannedTail;
            RaiseProgress(90, $"scanned tail={scannedTail}");
            OnLogRecovered();
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
