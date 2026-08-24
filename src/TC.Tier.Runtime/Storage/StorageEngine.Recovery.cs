namespace TC.Tier.Runtime.Storage;

internal sealed partial class StorageEngine
{
    /// <summary>
    /// 恢复算法工厂——统一扫盘恢复（全介质同构：空目录 ⇒ 零段 ⇒ 合成 seg0；mem 卷新进程天然如此）。
    /// </summary>
    protected override IRecovery<EngineRecoveryHints> CreateRecovery() => new DefaultEngineRecovery(this);
    /// <summary>
    /// 默认引擎恢复——基类默认实现：合成单段 seg0（内存/null 引擎用）。
    /// <para>★ 嵌套类可访问 <see cref="StorageEngine"/> 的所有 private 成员（RecoverAndBuildReader/RebuildCapacityCounters/StartEpochProtection 等）。</para>
    /// </summary>
    private sealed class DefaultEngineRecovery(StorageEngine owner) : RecoveryBase<EngineRecoveryHints>
    {
        /// <summary>构造——捕获 owner（引擎引用）。</summary>
        protected override async ValueTask OnRecoveryCoreAsync(EngineRecoveryHints hints, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RaiseProgress(10, "config");
            ct.ThrowIfCancellationRequested();
            var reader = await RecoverAndBuildReaderAsync(hints, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await RecoverAndBuildWriterAsync(hints, reader, ct).ConfigureAwait(false);
            RaiseProgress(98, "Start Epoch Protection");
            owner.StartEpochProtection();
        }

        /// <summary>
        /// 恢复并构建地址表读取器——默认实现合成单段 seg0（内存/null 引擎用）。
        /// </summary>
        /// <param name="hints">恢复提示信息</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>地址表读取器</returns>
        private ValueTask<IAddressTableReader> RecoverAndBuildReaderAsync(EngineRecoveryHints hints,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (owner._checkpoint is { } cp) return new ValueTask<IAddressTableReader>(cp.Reader);
            RaiseProgress(20, "Begin Build Segment");
            var growthLimit = owner.SegmentGrowthLimit;   // 构造期传入
            // seg0 预创建：fs.CreateFile（磁盘建文件，内存分配 pinned byte*）。
            // ★ 预分配与否由 options.PreallocateFile 决定（CreateSegmentFile 内部读取）。
            owner.CreateSegmentFile(segId: 0, growthLimit);
            var segments = new List<SegmentEntry>
            {
                // ★ 命名构造（fresh 空 seg0：minOffset=0、maxOffset=0）。
                //   旧 5 元组字面量位序错位把 growthLimit 塞进 minOffset 位 → RealSize 为负 →
                //   SequentialReader 跳段抛 PartitionInvalid、水位/容量计数全歪（2026-08-14 取证）。
                //   SegmentScanEntry 构造强校验后此类错位当场爆炸。
                new(new SegmentSpec(minOffset: 0, growthLimit: growthLimit, maxOffset: 0, stableState: StableState.Ready))
            };
            RaiseProgress(70, "End Build Segment");
            return new ValueTask<IAddressTableReader>(new DefaultAddressTableReader(growthLimit: growthLimit, segments));
        }

        /// <summary>
        /// 恢复并构建地址表写入器——默认实现：LoadAddressTable（建段表）+ ApplyHints（水位修正）（内存/null 引擎用）。
        /// </summary>
        /// <param name="hints">恢复提示信息</param>
        /// <param name="reader">地址表读取器</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>异步操作任务</returns>
        private ValueTask RecoverAndBuildWriterAsync(EngineRecoveryHints hints, IAddressTableReader reader,
            CancellationToken ct)
        {
            RaiseProgress(75, "Begin Recover Segment");
            // ★ ApplyHints 小值截断的物理联动【提前处理】（hint 驱动、扫描结果定位）：
            //   建表之前就地整段删文件 + 段内打洞，并按 hint 过滤/截断 reader 条目——
            //   表建成后与物理现状天然一致（ApplyHints 只剩水位修正；表侧 TruncateSegmentsAfter 无段可截，退化为安全网）。
            //   方向约束：引擎正向驱动（hint + 扫描结果），不从段表反向取回待办。
            reader = ApplyHintTruncationUpfront(hints, reader);
            owner._segmentTable
                .LoadAddressTable(reader); // ★ 建段表 + tail + 水位 + lifecycle worker（段表就绪后启 lifecycle worker）
            RaiseProgress(90, "End Recover Segment");
            // ★ 恢复 hints 双尾 → 段表启动参数（语义与旧 ApplyHints 逐分支等价）：
            //   committed-only → 单值（双尾同址，allocated 联动对齐）；both → 双值；
            //   allocated-only → 以表当前 committed 补双值（不动提交水位）
            if (hints.CommittedTailHint is { } c)
                owner._segmentTable.SetStartupTails(hints.AllocatedTailHint is { } a
                    ? new StartupParameters(c, a) : new StartupParameters(c));
            else if (hints.AllocatedTailHint is { } aOnly)
                owner._segmentTable.SetStartupTails(new StartupParameters(owner._segmentTable.CommittedTail, aOnly));

            // ★ Compact 崩溃恢复（marker 补执行 / 损坏清理）——ICompact.Recover 一直存在但从未被调用
            //   （孤儿入口，Compact_CrashCorruptedMarker 取证：损坏 marker 重启后残留）。
            //   损坏 marker → 删除；有效 marker → 补执行 Phase 2（lease 由段表按缓存区间重建）。
            owner._compact.Recover((start, end) =>
            {
                // ★ 存量盘旧形态归一（L4 取证 2026-08-21 首案；2026-08-21 区间统一后仅服务存量 marker）：
                //   旧二进制持久化的 marker to=(maxSeg+1,0) 与扫盘尾 (seg,growthLimit) 是线性等价地址，
                //   但元组比较越界。新写不再产出旧形态（AdvanceAddress 恰满停驻段末），此钳制只归一
                //   存量盘的旧形态 marker——线性等价时钳到扫盘尾，真越界（尾段未满）仍抛。
                var tail = owner._segmentTable.CommittedTail;
                if (end.CompareTo(tail) > 0
                    && end.SegId == tail.SegId + 1 && end.Offset == 0
                    && owner._segmentTable.TryGetSegment(tail.SegId, out var tailView)
                    && tailView is { IsValid: true }
                    && tail.Offset >= tailView.Value.GrowthLimit)
                {
                    end = tail;
                }
                return owner._segmentTable.CompactLease(start, end);
            });
            return ValueTask.CompletedTask;
        }

        /// <summary>
        /// hint 小值截断的提前物理处理——hint 之后的整段删文件、hint 所在段打洞 [hint.Offset, MaxOffset)，
        /// reader 条目同步过滤/截断。无 hint 时原样返回。
        /// </summary>
        private IAddressTableReader ApplyHintTruncationUpfront(EngineRecoveryHints hints, IAddressTableReader reader)
        {
            if (hints.CommittedTailHint is not { } hint) return reader;

            // 恢复期单线程——按协议先 ReadHeader（磁盘扫描 reader 未读 header 时 ReadSegment 恒 false），
            // 再全量消费 reader，按 hint 重建条目集
            reader.ReadHeader(out _);
            var entries = new List<SegmentEntry>();
            while (reader.ReadSegment(out var segId, out var spec))
                entries.Add(new SegmentEntry(segId, spec));

            var growthLimit = owner.SegmentGrowthLimit;   // 构造期传入
            var rebuilt = new List<SegmentEntry>(entries.Count);
            var truncated = false;
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.SegId > hint.SegId)
                {
                    // hint 之后整段——物理删除（提前：建表前，段表不会包含它们）
                    owner.ReleaseSegmentHandles(e.SegId);
                    owner.DeleteSegment(e.SegId, 0);
                    truncated = true;
                    continue;
                }

                if (e.SegId == hint.SegId && e.Spec.MaxOffset > hint.Offset)
                {
                    // hint 所在段——物理打洞 [hint.Offset, MaxOffset)，条目水位截到 hint（状态保持，对齐 RetreatOffset 语义）
                    owner.PunchHoleViaDrain(e.SegId, hint.Offset, e.Spec.MaxOffset - hint.Offset,
                        CancellationToken.None);
                    e = new SegmentEntry(e.SegId, new SegmentSpec(e.Spec.MinOffset, e.Spec.GrowthLimit,
                        hint.Offset, e.Spec.StableState));
                    truncated = true;
                }

                rebuilt.Add(e);
            }

            if (truncated)
            {
                // ★ 截断发生过——抑制本生命周期的段预备池：预建会把刚删的段以空文件"复活"，
                //   违反"hint 之后整段删除"的可观测语义（SuppressSegmentPoolForLifecycle 原为孤儿，池默认开后为实害）。
                owner.SuppressSegmentPoolForLifecycle();
            }

            // reader 已被消费——从过滤后的条目集重建
            return new DefaultAddressTableReader(growthLimit, rebuilt);
        }
        /// <summary>
        /// 扫盘结果 reader——把段元组列表适配为 <see cref="IAddressTableReader"/>，供 LoadAddressTable 重建段表。
        /// <para>★ 各设备 RecoverAndBuildReader 扫盘/合成段元组后构造此 reader 返回。</para>
        /// <para>★ 纯内存枚举（无 IO），物理副作用（PunchHole/CompactMarker 恢复）在构造前作为预处理完成。</para>
        /// </summary>
        private sealed class DefaultAddressTableReader : IAddressTableReader
        {
            private readonly long _growthLimit;

            private readonly IReadOnlyList<SegmentEntry> _segments;

            private int _index;

            internal DefaultAddressTableReader(long growthLimit,
                IReadOnlyList<SegmentEntry> segments)
            {
                _growthLimit = growthLimit;
                _segments = segments;
            }

            /// <inheritdoc/>
            public bool ReadHeader(out long growthLimit)
            {
                growthLimit = _growthLimit;
                return true;
            }


            /// <inheritdoc/>
            public bool ReadSegment(out int segId, out SegmentSpec entry)
            {
                if (_index >= _segments.Count)
                {
                    entry = default!;
                    segId = 0;
                    return false;
                }

                var segment = _segments[_index++];
                segId = segment.SegId;
                entry = segment.Spec;   // ★ 条目在构造时已强校验——此处零防御开销
                return true;
            }

            /// <inheritdoc/>
            public bool ReadFooter(out LogicalAddress? committedTail, out LogicalAddress? allocatedTail)
            {
                committedTail = null;
                allocatedTail = null;
                return true;
            }
        }
    }
}