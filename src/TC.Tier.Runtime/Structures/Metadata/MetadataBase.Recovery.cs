namespace TC.Tier.Runtime.Structures.Metadata;

/// <summary>恢复 partial（DefaultMetadataRecovery 嵌套类——<see cref="RecoveryBase{THints}"/> 模板派生）。</summary>
public abstract partial class MetadataBase
{
    /// <summary>
    /// 默认恢复实现（基类内置嵌套类，派生 <see cref="RecoveryBase{MetadataRecoveryHints}"/>——
    /// CAS 闸门/状态机/进度上报/MarkReady 全在模板，本类只填恢复算法）。
    /// 三级回退：MetadataRecoveryHints → meta.Load(O(1) 水位) → 扫盘按版本号定位 Head。
    /// <para>★ 时序（lifecycle.md §3 模板）：WaitForDependenciesAsync（等主引擎就绪）→
    ///   OnRecoveryCoreAsync（装配 MetaPolicy——"装配策略"是 Core 钩子职责 → meta.Load → 三级回退）。</para>
    /// </summary>
    private protected class DefaultMetadataRecovery(MetadataBase owner) : RecoveryBase<MetadataRecoveryHints>
    {
        /// <summary>层间 join——主引擎 + meta 引擎（Managed 模式）双 await，全异步轨（OnInitializeBegin 已并行启动）。</summary>
        protected override async ValueTask WaitForDependenciesAsync(CancellationToken ct)
        {
            await owner._engine.WaitForReadyAsync(ct).ConfigureAwait(false);
            if (owner._metaEngine is { } metaEngine)
                await metaEngine.WaitForReadyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// ★ 恢复核心（模板唯一必 override）——装配 MetaPolicy → meta.Load → 三级回退定位链头 → 加载内存镜像。
        /// <para>★ 不用地址值（== Empty）判断"有没有找到"——Empty 是合法地址（地址空间起点），
        ///   用 headFound 布尔标志表示"已获得有效链头"。</para>
        /// </summary>
        protected override async ValueTask OnRecoveryCoreAsync(MetadataRecoveryHints hints, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            // ★ MetaPolicy 装配（引擎就绪后——Managed 模式建独立 meta 引擎；Transport 回落 MetaHost 追加依赖主引擎）
            RaiseProgress(10, "meta policy");

            // meta.Load（水位 O(1)；Disabled no-op）。false = 空/无数据/损坏三态——正常，走全新初始化
            if (owner._settings.MetaPolicyKind != MetaPolicyKind.Disabled)
                await owner.MetaPolicy.LoadAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // 三级回退：hints → meta → 扫盘
            LogicalAddress head = default;
            long committedSeq = -1;
            bool headFound = false;

            // 1. hints 优先
            if (hints.HighestVersionAddress is { } hAddr) { head = hAddr; headFound = true; }
            if (hints.LastCommittedSeq is { } hSeq) committedSeq = hSeq;

            // 2. meta（LoadAsync 已就绪，直接读 payload）
            if (!headFound && owner._settings.MetaPolicyKind != MetaPolicyKind.Disabled)
            {
                var payload = owner.MetaPolicy.ReadMetaPayload();
                if (payload is { } p)
                {
                    head = p.HighestVersionAddress;
                    committedSeq = p.LastCommittedSeq;
                    owner._lowestVersionAddress = p.LowestVersionAddress;
                    headFound = true;
                }
            }

            // 3. 扫盘按版本号定位 Head（无 meta / meta 损坏兜底）
            if (!headFound)
            {
                RaiseProgress(50, "scanning version chain");
                head = ScanForHead(ct);
                headFound = owner._currentVersion > 0;  // ScanForHead 扫到合法版本时设 _currentVersion
            }

            owner._highestVersionAddress = head;
            if (committedSeq >= 0)
            {
                owner._lastCommittedSeq = committedSeq;
                owner._lastPreparedSeq = committedSeq;
            }

            // ★ 加载链头版本到内存镜像——用 headFound 判断（不用地址值，Empty 是合法地址）
            if (headFound)
            {
                RaiseProgress(80, "load head version");
                owner.LoadVersionToMemory(head);
            }
        }

        /// <summary>
        /// 扫盘找最高版本号 record（链头）。
        /// ★ 不依赖 CommittedTail/AllocatedTail——直接从 MinAddress 正向扫，按每条 record 自身的
        ///   PayloadLength+PaddingLength 算总长跳到下一条（统一三段式 §1.3 记录总长可确定计算）。
        ///   magic 不匹配 = 链结束（torn write 或空区）。最后一条合法 record = 链头（最新版本）。
        ///   对齐 Ring tier-4 扫盘找 magic 范式——没有 meta 就扫盘恢复水位。
        /// ★ 跳过 IS_META record（Transport meta 嵌入流写入的 meta block）——meta record 不参与
        ///   数据版本号定位，只占位；几何跳进照常（按其 PayloadLength+PaddingLength）。
        /// </summary>
        /// <summary>起点定位的扫描读页步进（64KB——几何跳进前的 magic 首扫）。</summary>
        private const int ScanProbePageSize = 1 << 16;

        private LogicalAddress ScanForHead(CancellationToken ct)
        {
            // ★ 起点定位：LocateFirstNonZero 已退役（非零语义=格式判断，零是合法数据形态）——
            //   按 codec magic 定位首条 record（比"首个非零扇区"更精确：magic 命中即链头候选）。
            //   Linear 档：零富集/前缀洞（ReclaimOldVersions PunchHole 全零区）天然免疫。
            var startLoc = owner._engine.Locate([owner._codec.Magic], MagicDirection.First,
                owner._engine.MinAddress, owner._engine.CommittedTail,
                ScanProbePageSize, magicAlignment: 4, MagicLocateStrategy.Linear);
            var addr = startLoc.Found ? startLoc.MagicAddress : owner._engine.MinAddress;
            var sectorSize = (int)owner._engine.SectorSize;
            int headerSize = owner._codec.HeaderSize;
            LogicalAddress head = default;
            long headVersion = -1;
            bool lowestFound = false;
            long maxScanBytes = 64 * 1024 * 1024;

            for (long scanned = 0; scanned < maxScanBytes; )
            {
                ct.ThrowIfCancellationRequested(); // IO checkpoint 取消检查（lifecycle.md §3）
                // 读 header
                using var hdrBuf = new AlignedMemoryManager(headerSize, sectorSize);
                int got;
                try { got = owner._engine.Read(addr, hdrBuf.GetSpan()); }
                catch { break; }
                if (got < headerSize) break;
                if (!owner._codec.TryReadHeader(hdrBuf.GetSpanUnsafe(0, headerSize), out var fields))
                    break;  // magic 不匹配 = 链结束

                int recTotal = headerSize + (int)fields.PayloadLength + fields.PaddingLength;
                // ★ IS_META record（Transport meta block）跳过——不参与数据版本链头定位
                bool isMeta = (fields.Flags & RecordFlags.FLAG_ENTRY_IS_META) != 0;
                if (!isMeta)
                {
                    // ★ 链头 = 最高地址的数据 record（版本链追加流，地址单调递增；最高地址 = 最新写入）。
                    //   不用版本号比较——同一版本号可能被多次持久化（Write 落盘 + Prepare 再落盘），
                    //   此时版本号相同但地址递增，最新地址才是当前链头。
                    head = addr;
                    if (fields.MetadataVersion > headVersion)
                    {
                        headVersion = fields.MetadataVersion;
                        owner._currentVersion = headVersion;
                    }
                    else
                    {
                        // 同版本号多份记录：_currentVersion 取该版本号（已是最高）
                        owner._currentVersion = Math.Max(owner._currentVersion, fields.MetadataVersion);
                    }
                    // 跟踪最低地址（链尾/最老）——首次见到的合法数据 record 即最低地址
                    if (!lowestFound)
                    {
                        owner._lowestVersionAddress = addr;
                        lowestFound = true;
                    }
                }

                scanned += recTotal;
                addr = owner._engine.CalculationAddress(addr, recTotal);
            }
            return head;
        }
    }

    /// <summary>
    /// 加载指定地址的版本 record 到内存（冷热分离——设计决策）。
    /// ★ 按 record 自身 PayloadLength 从自持池租借只读缓冲，热区不写：历史大小 ≠ 当前
    ///   _payloadSize 时不补零、不截断——Read/AsSpan 按盘上真实大小完整交付使用方。
    /// ★ 首次 Write 前：当前内容 = 加载版本（历史真实大小）；Write 后：当前内容 = 热区
    ///   （本启动配置大小）。_payloadSize 只参与本次运行的版本几何（Write/Prepare 追加的 record）。
    /// </summary>
    private protected void LoadVersionToMemory(LogicalAddress addr)
    {
        // ★ Empty 是合法地址（地址空间起点），不判断 addr == Empty
        // 先按 Header 大小读 header
        var sectorSize = (int)_engine.SectorSize;
        int headerSize = _codec.HeaderSize;
        using var headerBuf = new AlignedMemoryManager(headerSize, sectorSize);
        int got = _engine.Read(addr, headerBuf.GetSpan());
        if (got < headerSize) return;
        if (!_codec.TryReadHeader(headerBuf.GetSpan(), out var fields)) return;

        // 按 record 自身的 PayloadLength + PaddingLength 算总长，分配 buffer 读取整条 record
        int histPayloadLen = (int)fields.PayloadLength;
        int histPaddingLen = fields.PaddingLength;
        int histTotal = headerSize + histPayloadLen + histPaddingLen;
        using var recordBuf = new AlignedMemoryManager(histTotal, sectorSize);
        got = _engine.Read(addr, recordBuf.GetSpanUnsafe(0, histTotal));
        bool crcOk = _codec.VerifyCrc(recordBuf.GetSpanUnsafe(0, histTotal), headerSize, histPayloadLen, histPaddingLen);
        if (!crcOk) return;

        // ★ 冷热分离：加载版本按盘上真实大小从自持池租借只读缓冲（不截断不补零）；
        //   热区（本启动 _payloadSize）保持未写状态——首次 Write 才把当前内容切到热区。
        var loaded = _bufferPool.RentAligned(Math.Max(histPayloadLen, 1), sectorSize);
        recordBuf.GetSpanUnsafe(headerSize, histPayloadLen).CopyTo(loaded.GetSpanUnsafe(0, histPayloadLen));
        _loadedVersion = loaded;
        _loadedVersionLength = histPayloadLen;
        _serveLoaded = true;
        _hotVersionCount = 0;   // 热区无镜像——窗口回退源 = 加载版本（Abort 回退界）
        _currentVersion = fields.MetadataVersion;
        _baseVersion = _currentVersion;       // Abort 回退基准 = 加载版本号
        _persistedVersion = _currentVersion;  // 链头已落此版本——无新 Write 的 Prepare 不重复追加
    }
}
