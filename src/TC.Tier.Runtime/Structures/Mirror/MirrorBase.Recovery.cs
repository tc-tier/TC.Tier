using TC.Tier.Runtime.Structures.Mirror.Contracts;

namespace TC.Tier.Runtime.Structures.Mirror;

/// <summary>恢复 partial（DefaultMirrorRecovery 嵌套类 + 统一帧恢复编排——机制归基类）。</summary>
public abstract partial class MirrorBase
{
    /// <summary>
    /// 默认恢复实现（基类内置嵌套类，派生 <see cref="RecoveryBase{MirrorRecoveryHints}"/>——
    /// CAS 闸门/状态机/进度上报/MarkReady 全在模板，本类只填恢复算法）。
    /// 三级回退：MirrorRecoveryHints → meta.Load(O(1) 水位) → 扫盘按版本号定位 Head。
    /// <para>★ 悬干裁决：meta prepared &gt; committed 时，最后一批帧（按会话版本号识别）视为悬干，
    ///   尾截断物理丢弃后重建链头。</para>
    /// </summary>
    private protected class DefaultMirrorRecovery(MirrorBase owner) : RecoveryBase<MirrorRecoveryHints>
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
        /// ★ 恢复核心（模板唯一必 override）——装配 MetaPolicy → meta.Load → 三级回退 → 悬干裁决 → 重建链头。
        /// </summary>
        protected override async ValueTask OnRecoveryCoreAsync(MirrorRecoveryHints hints, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // MetaPolicy 构造期已装配（构造=配置）——依赖就绪后直接 Load
            RaiseProgress(10, "meta load");

            MirrorMetaPayload? metaPayload = null;
            if (owner._settings.MetaPolicyKind != MetaPolicyKind.Disabled)
            {
                await owner.MetaPolicy.LoadAsync(ct).ConfigureAwait(false);
                metaPayload = owner.MetaPolicy.ReadMetaPayload();
            }
            ct.ThrowIfCancellationRequested();

            // 三级回退：hints → meta → 扫盘（链头裁决）
            LogicalAddress head = default;
            long committedSeq = -1;
            bool headFound = false;

            // 1. hints 优先
            if (hints.HighestVersionAddress is { } hAddr) { head = hAddr; headFound = true; }
            if (hints.LastCommittedSeq is { } hSeq) committedSeq = hSeq;

            // 2. meta（LoadAsync 已就绪，直接读 payload）
            if (!headFound && metaPayload is { } p)
            {
                head = p.HighestVersionAddress;
                committedSeq = p.LastCommittedSeq;
                owner._lowestVersionAddress = p.LowestVersionAddress;
                headFound = true;
            }

            // 悬干裁决依据只在 meta（prepared vs committed）——无 meta（Disabled/损坏）无裁决依据，
            // 扫到的状态视为已提交。（committedSeq=-1 时 Math.Max(·,0) 补 0 会误判 0>-1 恒真——禁止）
            bool truncateDangling = metaPayload is { } mp && mp.LastPreparedSeq > committedSeq;

            // 3. 扫盘重建（无 meta / meta 损坏兜底——统一帧编排）
            RaiseProgress(50, "scanning version chain");
            owner.ScanAndRebuild(ct, truncateDangling);
            if (!headFound)
            {
                head = owner._highestVersionAddress; // 扫盘结果
                headFound = owner._hasCommittedVersion;
            }

            owner._highestVersionAddress = head;
            if (committedSeq >= 0)
            {
                owner._lastCommittedSeq = committedSeq;
                owner._lastPreparedSeq = committedSeq;
            }
        }
    }

    /// <summary>
    /// 扫盘重建版本链状态（<b>统一帧编排——机制归基类，子类零 override</b>，链拓扑经
    /// <see cref="IMirrorCodec.ChainKind"/> 声明分派）：
    /// <para>★ Single（全局单链，WholeMirror）：尾锚主路径——Locate(尾 magic, Last) 直达最新数据帧
    ///   → 倒扫本帧头 + CRC 全验 → PreviousVersion 回跳 N=2 第二新（旧帧只验结构不读体）——
    ///   <b>旧代再烂也遮不住新代</b>（零富集载荷/前缀洞/旧帧损坏全部免疫）。尾锚空手 → 走链兜底。</para>
    /// <para>★ PerKey（per-page 多链，PagedMirror）：全走链——逐帧（验头→前向找尾→头尾版本一致）
    ///   收集后经 <see cref="OnScanFrame"/> 按 PageId 重建各页链头。</para>
    /// <para>★ 帧判定链零长度依赖：双 magic 匹配 + 版本合法 +（权威帧）CRC——假命中重同步
    ///   （cursor+1 逐字节，帧头任意字节边界）。跳过 IS_META 帧（Transport 嵌入 meta）。</para>
    /// </summary>
    /// <param name="ct">取消令牌（IO checkpoint 检查）。</param>
    /// <param name="truncateDangling">true = 最后一批帧（最高会话版本号）视为悬干，尾截断物理丢弃。</param>
    private protected void ScanAndRebuild(CancellationToken ct, bool truncateDangling)
    {
        var frames = _codec.ChainKind == MirrorChainKind.Single
            ? RebuildSingleChainFast(ct) ?? WalkAllFrames(ct)   // 尾锚空手 → 走链兜底
            : WalkAllFrames(ct);

        // 悬干裁决：最后一批（最高会话版本号）帧尾截断
        if (truncateDangling && frames.Count > 0)
        {
            long maxVersion = frames.Max(f => f.Header.MirrorVersion);
            var minDangling = frames.Where(f => f.Header.MirrorVersion == maxVersion)
                .Aggregate((a, b) => a.Head.CompareTo(b.Head) <= 0 ? a : b);
            _engine.ReclaimTail(minDangling.Head); // 物理丢弃悬干会话（引擎退化 AllocatedTail）
            frames.RemoveAll(f => f.Header.MirrorVersion == maxVersion);
        }

        // 重建水位（地址升序 = 时间序）+ 帧账面
        _highestVersionAddress = LogicalAddress.Empty;
        foreach (var f in frames)
        {
            OnScanFrame(f.Header, f.Head, f.FooterAddress);
            _currentVersion = Math.Max(_currentVersion, f.Header.MirrorVersion);
            _hasCommittedVersion = true;
            _committedChainEnd = _engine.CalculationAddress(f.FooterAddress, _codec.FooterSize);
            _lastRecordEnd = _committedChainEnd;
            _frameFooters[f.Head] = f.FooterAddress;
        }
    }

    /// <summary>
    /// 尾锚主路径（Single 链）：Locate(尾 magic, Last) 直达最新数据帧（假尾/meta 帧尾缩窗重试）
    /// → 倒扫最近数据帧头（结构+头尾版本一致+CRC 全验——最新帧是账面权威）→ PreviousVersion
    /// 链回跳收集（结构验证不读体）。尾锚空手/全坏返回 null（走链兜底）。
    /// </summary>
    private List<MirrorFrameInfo>? RebuildSingleChainFast(CancellationToken ct)
    {
        var tail = _engine.CommittedTail;
        Span<byte> hdrScratch = stackalloc byte[_codec.HeaderSize];
        Span<byte> ftrScratch = stackalloc byte[_codec.FooterSize];
        var upper = tail;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var fLoc = _engine.Locate([_codec.FooterMagic], MagicDirection.Last,
                _engine.MinAddress, upper, FrameScanPageSize, magicAlignment: 1, MagicLocateStrategy.Linear);
            if (!fLoc.Found) return null;

            if (!TryReadFrameFooterAt(fLoc.MagicAddress, ftrScratch, out var footer)
                || (footer.Flags & RecordFlags.FLAG_ENTRY_IS_META) != 0)
            {
                upper = fLoc.MagicAddress;   // 假尾 / meta 帧尾（Confirm 后 WriteMeta 追加在数据帧后）→ 缩窗继续
                continue;
            }

            if (FindDataFrameHead(fLoc.MagicAddress, in footer, hdrScratch) is not { } head)
            {
                upper = fLoc.MagicAddress;   // 孤儿尾（头损毁/CRC 全败）→ 缩窗找更早数据帧
                continue;
            }
            TryReadFrameHeaderAt(head, hdrScratch, out var header);
            var frames = new List<MirrorFrameInfo> { new(head, fLoc.MagicAddress, header, footer) };

            // PreviousVersion 链回跳：N=2 第二新直达（旧帧只验头+尾结构，不读体验证——旧代账面）
            var prev = footer.PreviousVersion;
            while (prev.IsValid)   // Invalid = 链尾哨兵（Empty 是合法 seg0@0）
            {
                ct.ThrowIfCancellationRequested();
                if (!TryReadFrameHeaderAt(prev, hdrScratch, out var hdr2)) break;   // 回收洞/损坏 → 链止
                var fLoc2 = _engine.Locate([_codec.FooterMagic], MagicDirection.First,
                    _engine.CalculationAddress(prev, _codec.HeaderSize),
                    head, FrameScanPageSize, magicAlignment: 1, MagicLocateStrategy.Linear);
                if (!fLoc2.Found) break;
                if (!TryReadFrameFooterAt(fLoc2.MagicAddress, ftrScratch, out var footer2)) break;
                if (footer2.MirrorVersion != hdr2.MirrorVersion
                    || (footer2.Flags & RecordFlags.FLAG_ENTRY_IS_META) != 0) break;
                frames.Insert(0, new MirrorFrameInfo(prev, fLoc2.MagicAddress, hdr2, footer2));
                prev = footer2.PreviousVersion;
            }
            return frames;
        }
    }

    /// <summary>
    /// 倒扫最近数据帧头（尾锚的帧头定位）：Locate(头 magic, Last) 候选 → 头校验 + 头尾版本一致 +
    /// CRC 全验。假头/meta 帧头/坏帧 → 缩窗继续倒扫；无候选返回 null。
    /// </summary>
    private LogicalAddress? FindDataFrameHead(LogicalAddress footerAddr, in MirrorFrameFooter footer, Span<byte> hdrScratch)
    {
        var upper = footerAddr;
        while (true)
        {
            var hLoc = _engine.Locate([_codec.HeaderMagic], MagicDirection.Last,
                _engine.MinAddress, upper, FrameScanPageSize, magicAlignment: 1, MagicLocateStrategy.Linear);
            if (!hLoc.Found) return null;

            if (TryReadFrameHeaderAt(hLoc.MagicAddress, hdrScratch, out var hdr)
                && (hdr.Flags & RecordFlags.FLAG_ENTRY_IS_META) == 0
                && hdr.MirrorVersion == footer.MirrorVersion
                && VerifyFrame(hLoc.MagicAddress, footerAddr))
                return hLoc.MagicAddress;

            upper = hLoc.MagicAddress;   // 假头 / meta 帧头 / 版本错配 / CRC 败 → 缩窗
        }
    }

    /// <summary>
    /// 前向帧走链（PerKey 全量重建 / 尾锚兜底）：cursor 起逐帧推进（验头→前向找尾→头尾版本一致），
    /// 假 magic/CRC 失败重同步（cursor+1）。返回全部有效<b>数据帧</b>（地址升序；IS_META 嵌入帧
    /// 结构完好即跳过不入链——MetaHost 扫描复用单步原语自行收集）。
    /// </summary>
    private List<MirrorFrameInfo> WalkAllFrames(CancellationToken ct)
    {
        var tail = _engine.CommittedTail;
        var result = new List<MirrorFrameInfo>();
        Span<byte> hdrScratch = stackalloc byte[_codec.HeaderSize];
        Span<byte> ftrScratch = stackalloc byte[_codec.FooterSize];
        var cursor = _engine.MinAddress;

        while (cursor.CompareTo(tail) < 0)
        {
            ct.ThrowIfCancellationRequested();
            if (TryWalkNextFrame(ref cursor, tail, hdrScratch, ftrScratch, out var info)
                && (info.Footer.Flags & RecordFlags.FLAG_ENTRY_IS_META) == 0)
                result.Add(info);
        }
        return result;
    }

    /// <summary>
    /// 走链单步：从 cursor 找下一个有效帧（含 IS_META）。成功 → cursor=帧尾末并给出帧几何
    /// （结构裁决，体 CRC 惰性）；失败 → cursor 推进 +1（假头重同步——帧头任意字节边界）。
    /// </summary>
    private bool TryWalkNextFrame(ref LogicalAddress cursor, LogicalAddress tail,
        Span<byte> hdrScratch, Span<byte> ftrScratch, out MirrorFrameInfo info)
    {
        var hLoc = _engine.Locate([_codec.HeaderMagic], MagicDirection.First,
            cursor, tail, FrameScanPageSize, magicAlignment: 1, MagicLocateStrategy.Linear);
        if (!hLoc.Found) { info = default; cursor = tail; return false; }   // 无更多头——走链终

        var headAddr = hLoc.MagicAddress;
        if (!TryReadFrameHeaderAt(headAddr, hdrScratch, out var header))
        {
            cursor = _engine.CalculationAddress(headAddr, 1);   // 假头 → 重同步
            info = default;
            return false;
        }

        var fLoc = _engine.Locate([_codec.FooterMagic], MagicDirection.First,
            _engine.CalculationAddress(headAddr, _codec.HeaderSize),
            tail, FrameScanPageSize, magicAlignment: 1, MagicLocateStrategy.Linear);
        if (!fLoc.Found
            || !TryReadFrameFooterAt(fLoc.MagicAddress, ftrScratch, out var footer)
            || footer.MirrorVersion != header.MirrorVersion)
        {
            cursor = _engine.CalculationAddress(headAddr, 1);   // 坏帧/假尾 → 重同步
            info = default;
            return false;
        }

        cursor = _engine.CalculationAddress(fLoc.MagicAddress, _codec.FooterSize);
        info = new MirrorFrameInfo(headAddr, fLoc.MagicAddress, header, footer);
        return true;
    }

    /// <summary>
    /// 扫盘重建钩子——每条有效帧一次（地址升序）。<b>业务钩子（数据结构语义——子类合法 override 面）</b>。
    /// 基类默认：全局单链追踪（最新地址=链头 + 第二新=保留窗口，WholeMirror 直接用）；
    /// PagedMirror override 追加 per-page 字典重建（先调 base 保全局水位）。
    /// <para>★ 第二新用 _hasSecondNewest 标志判存在——不能用地址值（Empty 是合法地址，
    ///   首 record 就在 Empty，second==Empty 无法区分"没有"与"就在地址 0"）。</para>
    /// </summary>
    /// <param name="header">帧头字段。</param>
    /// <param name="head">帧头地址。</param>
    /// <param name="footerAddress">帧尾地址。</param>
    private protected virtual void OnScanFrame(in MirrorFrameHeader header, LogicalAddress head, LogicalAddress footerAddress)
    {
        if (_hasCommittedVersion) // 之前已有帧（重建循环里每帧后置位）
        {
            _secondNewestAddress = _highestVersionAddress;
            _hasSecondNewest = true;
        }
        _highestVersionAddress = head;
    }

    /// <summary>全局第二新帧地址（N=2 保留窗口；存在性看 <see cref="_hasSecondNewest"/>）。</summary>
    private protected LogicalAddress _secondNewestAddress = LogicalAddress.Empty;

    /// <summary>是否已有第二新（Empty 是合法地址——首 record 就在 Empty，不能拿地址值当哨兵）。</summary>
    private protected bool _hasSecondNewest;
}
