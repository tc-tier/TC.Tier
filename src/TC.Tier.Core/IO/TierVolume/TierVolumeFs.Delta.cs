using System.Buffers.Binary;
using System.IO.Hashing;

namespace TC.Tier.Core.IO.TierVolume;

/// <summary>
/// 增量导出 partial（V2 §1.2——journal CkptLsn = 操作级增量）。
/// <para>★ 机制（初始推荐——journal delta 帧）：基点 = 快照/检查点 LSN；delta = (baseLsn, 已提交头] 的
///   重放记录流——语义级增量（qcow2 dirty bitmap 是块级：导出时全量读脏块；我们 op 级：无数据面扫描，
///   导出体积 ∝ 变更量）。</para>
/// <para>★ 流形态（TCA1 管线新帧族「delta 帧」）：
///   [头 "TCD1" | 版本 u16 | flags u16 | 块大小 u32 | 卷 UUID 16B | baseLsn u64 | 基线镜像 CRC u32 | 头 CRC]
///   [记录流：日志区原帧字节直拷（RJRN 头 + 体 + 对齐 padding——复用 EncodeRecord 族，零重编码）]
///   [尾 "TCD2" | 记录数 u64 | 载荷 CRC u32]
///   还原 = 对同基线卷 <see cref="ApplyJournalRecord"/> 重放（共用函数——物理事实内嵌，确定性重放）。</para>
/// <para>★ 纪律：① 检查点截断——baseLsn &lt; CkptLsn 拒导（增量窗口丢失，先导全量）；② 链路——还原后
///   检查点推进 CkptLsn = 应用头 → 下一增量以头为基点（导出侧同纪律：期间不得检查点）；③ 快照表记录
///   （SnapshotCreate/Delete）不参与导出——目标快照表 = 自身（快照 = 卷本地存档视图，非复制对象）。</para>
/// <para>★ 判定门（不钉死）：① delta 体积 vs 文件级 diff（高覆写负载记录膨胀实测选优）；② 重放速度 vs
///   目标卷重写——膨胀失控即切文件级 diff / 混合形态。验收探针 <c>--tier-volume-delta-probe</c>。</para>
/// </summary>
public sealed partial class TierVolumeFs : IJournaledVolume
{
    /// <summary>delta 流头/尾 magic（TCA1 管线新帧族——V2 §1.2）。</summary>
    private static ReadOnlySpan<byte> DeltaHeaderMagic => "TCD1"u8;
    private static ReadOnlySpan<byte> DeltaFooterMagic => "TCD2"u8;
    private static ReadOnlySpan<byte> DeltaDataMagic => "TCD3"u8;
    private const ushort DeltaFormatVersion = 1;
    private const int DeltaHeaderSize = 44;
    private const int DeltaFooterSize = 16;

    SnapshotDeltaSummary IJournaledVolume.ExportDeltaTo(Stream output, ulong baseLsn)
        => ExportDelta(output, baseLsn);

    SnapshotDeltaSummary IJournaledVolume.ApplyDeltaFrom(Stream input)
        => ApplyDelta(input);

    // ═══════════════ 增量窗口脏块跟踪（数据面——journal 只记元数据操作，数据内容须随流携带）═══════════════
    // ★ 增量 = 操作记录流 + 窗口内写块内容（记录给出物理事实，内容给出数据——op 级增量的数据面）。
    //   跟踪点 = 数据写路径（WritePhysical 触及块 + 物化/迁移/拷贝块）；窗口 = 检查点以来的写入；
    //   崩溃恢复后窗口不完整（无记录的原地覆写不可重构）→ 拒导，检查点后恢复。

    /// <summary>窗口内写块集（null = 未启用跟踪——快照挂载/非日志卷）。</summary>
    private HashSet<ulong>? _deltaDirtyBlocks;

    /// <summary>脏块集登记锁（写计划数据段锁外登记——Parallel 档并发写者；消费侧在 MetadataLock 内）。</summary>
    private readonly object _deltaDirtyGate = new();

    /// <summary>窗口完整性：崩溃恢复（重放）后的窗口无法重构原地覆写 → false，检查点复位 true。</summary>
    private bool _deltaDirtyComplete = true;

    /// <summary>数据写块跟踪（WritePhysical/物化/迁移/拷贝路径——写计划数据段锁外调用）。</summary>
    private void TrackDeltaDirtyBlocks(ulong firstBlock, ulong count)
    {
        var set = _deltaDirtyBlocks;
        if (set is null) return;
        lock (_deltaDirtyGate)
            for (ulong b = firstBlock; b < firstBlock + count; b++) set.Add(b);
    }

    /// <summary>增量导出：baseLsn 起至已提交头的记录流（检查点/快照基点；须日志卷）。
    /// baseLsn &lt; CkptLsn → 拒导（检查点已截断——增量窗口丢失，先导全量）。
    /// ★ 调用纪律：导出读载体数据块——须静默（无并发写；管线面 <see cref="Image.RootSpaceImage.ExportDelta"/>
    /// 自动经维护门闩静默 WriteOperations——Parallel 档数据段锁外写载体会撕裂嵌入块）。</summary>
    public SnapshotDeltaSummary ExportDelta(Stream output, ulong baseLsn)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(output);
        lock (MetadataLock)
        {
            if (!_journalOn)
                throw new FileIOException(IOError.Unsupported,
                    "增量导出须日志卷（op 级增量 = journal 记录流——V2 §1.2）", null, nameof(ExportDelta));
            if (_degraded)
                throw new FileIOException(IOError.Unsupported,
                    "降级卷不接受增量导出（成员缺失——日志区完整性不可断言）", null, nameof(ExportDelta));
            if (!_readOnly)
                JournalCommit();   // 待屏障记录先提交——导出覆盖 Flush 态（只读卷无在途记录）
            if (baseLsn < _sb.JournalCkptLsn)
                throw new FileIOException(IOError.IOFailure,
                    $"增量基点过旧：baseLsn {baseLsn} < CkptLsn {_sb.JournalCkptLsn}（检查点已截断日志——先导全量或换基点）",
                    null, nameof(ExportDelta));
            if (baseLsn > _committedLsn)
                throw new FileIOException(IOError.IOFailure,
                    $"增量基点超前：baseLsn {baseLsn} > 已提交 {_committedLsn}（基点尚未存在）",
                    null, nameof(ExportDelta));
            if (!_deltaDirtyComplete)
                throw new FileIOException(IOError.IOFailure,
                    "增量窗口不完整（崩溃恢复后未经检查点——原地覆写/映射写不可重构；先 FlushRoot 检查点再导出）",
                    null, nameof(ExportDelta));
            // 基线镜像 CRC：仅快照基点携带（目标 = 快照时刻字节副本——镜像 CRC 逐字一致，强锚成立）。
            // 检查点/链路基点 → 0（目标合法分叉：应用后的检查点镜像与源镜像物理布局分叉——
            // 时间戳/分配决策不随记录流确定性重放；完整性由头对齐 + 逐记录 CRC + 载荷 CRC 承担）。
            var baseCrc = _sb.Snapshots.FirstOrDefault(s => s.CaptureLsn == baseLsn)?.ImageCrc ?? 0u;

            Span<byte> header = stackalloc byte[DeltaHeaderSize];
            header.Clear();
            DeltaHeaderMagic.CopyTo(header);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], DeltaFormatVersion);
            BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);   // flags 保留（未知拒读）
            BinaryPrimitives.WriteUInt32LittleEndian(header[8..], _sb.BlockSize);
            _sb.Uuid.TryWriteBytes(header[12..28]);
            BinaryPrimitives.WriteUInt64LittleEndian(header[28..], baseLsn);
            BinaryPrimitives.WriteUInt32LittleEndian(header[36..], baseCrc);
            BinaryPrimitives.WriteUInt32LittleEndian(header[40..],
                Crc32.HashToUInt32(header[..40]));
            output.Write(header);

            // 记录流：日志区原帧字节直拷（零重编码；Pad/快照表记录跳过——见类型注释）
            var frames = ScanJournalFrames(out _);
            var buf = new byte[1 << 20];
            var payloadCrc = new Crc32();
            ulong count = 0;
            foreach (var f in frames)
            {
                if (f.Type is JournalRecordType.Pad
                    or JournalRecordType.SnapshotCreate
                    or JournalRecordType.SnapshotDelete) continue;
                if (f.Lsn <= baseLsn || f.Lsn > _committedLsn) continue;
                var carrierOffset = _journalAreaStart + f.AreaOffset;
                for (var done = 0; done < f.FramedLen;)
                {
                    var take = Math.Min(buf.Length, f.FramedLen - done);
                    ReadCarrierExactly(carrierOffset + done, buf.AsSpan(0, take));
                    payloadCrc.Append(buf.AsSpan(0, take));
                    output.Write(buf, 0, take);
                    done += take;
                }
                count++;
            }

            // 数据段（V2 §1.2 数据面）：窗口内写块内容——记录给出物理事实，内容给出数据。
            // 仅嵌入仍在使用块（已释放块无需内容——重放侧释放它们；已重用块 = 新属主内容，收敛一致）
            Span<byte> dataHeader = stackalloc byte[12];
            dataHeader.Clear();
            DeltaDataMagic.CopyTo(dataHeader);
            ulong[] dirty;
            lock (_deltaDirtyGate)
                dirty = _deltaDirtyBlocks!.Where(IsBlockUsed).OrderBy(b => b).ToArray();   // 锁内摘快照（Parallel 档数据段并发登记）
            BinaryPrimitives.WriteUInt64LittleEndian(dataHeader[4..], (ulong)dirty.LongLength);
            payloadCrc.Append(dataHeader);
            output.Write(dataHeader);
            var pageBuf = new byte[_pageSize];
            foreach (var b in dirty)
            {
                ReadCarrierExactly((long)(b * (ulong)_pageSize), pageBuf);
                Span<byte> entry = stackalloc byte[12];
                BinaryPrimitives.WriteUInt64LittleEndian(entry, b);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], Crc32.HashToUInt32(pageBuf));
                payloadCrc.Append(entry);
                payloadCrc.Append(pageBuf);
                output.Write(entry);
                output.Write(pageBuf);
            }

            Span<byte> footer = stackalloc byte[DeltaFooterSize];
            footer.Clear();
            DeltaFooterMagic.CopyTo(footer);
            BinaryPrimitives.WriteUInt64LittleEndian(footer[4..], count);
            BinaryPrimitives.WriteUInt32LittleEndian(footer[12..], payloadCrc.GetCurrentHashAsUInt32());
            output.Write(footer);
            return new SnapshotDeltaSummary(count, baseLsn, _committedLsn);
        }
    }

    /// <summary>增量还原：基线校验（卷 UUID + 头对齐 + 可选镜像 CRC）→ 逐记录重放 → 检查点收口
    /// （应用态原子持久 + CkptLsn 前进——链路增量可续）。目标必须恰在基点（头 == baseLsn）。</summary>
    public SnapshotDeltaSummary ApplyDelta(Stream input)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(input);
        lock (MetadataLock)
        {
            if (!_journalOn)
                throw new FileIOException(IOError.Unsupported,
                    "增量还原须日志卷（重放面 = journal 记录语义——V2 §1.2）", null, nameof(ApplyDelta));
            ThrowIfReadOnly(nameof(ApplyDelta));
            JournalCommit();   // 在途提交先落（还原后 CkptLsn 前进——旧记录语义被检查点吸收）

            Span<byte> header = stackalloc byte[DeltaHeaderSize];
            ReadDeltaExact(input, header, required: false);
            if (!header[..4].SequenceEqual(DeltaHeaderMagic))
                throw NewDeltaError("流头 magic 不符（非 TierVolume delta 流）");
            if (BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != DeltaFormatVersion)
                throw NewDeltaError($"delta 版本不支持：{BinaryPrimitives.ReadUInt16LittleEndian(header[4..])}");
            if (BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != 0)
                throw NewDeltaError($"delta 含未知 flags：0x{BinaryPrimitives.ReadUInt16LittleEndian(header[6..]):X4}（未知保留值拒读）");
            if (BinaryPrimitives.ReadUInt32LittleEndian(header[8..]) != _sb.BlockSize)
                throw NewDeltaError($"块大小不符：delta {BinaryPrimitives.ReadUInt32LittleEndian(header[8..])} vs 卷 {_sb.BlockSize}");
            if (new Guid(header[12..28].ToArray()) != _sb.Uuid)
                throw NewDeltaError("卷身份不符——还原目标必须同卷/同基线副本（UUID 基线校验）");
            if (Crc32.HashToUInt32(header[..40]) != BinaryPrimitives.ReadUInt32LittleEndian(header[40..]))
                throw NewDeltaError("delta 头 CRC 校验失败");
            var baseLsn = BinaryPrimitives.ReadUInt64LittleEndian(header[28..]);
            var baseCrc = BinaryPrimitives.ReadUInt32LittleEndian(header[36..]);
            // 目标头对齐校验：目标须恰在基点，或缺口仅含快照表变更记录（命名空间零影响——
            // 导出侧已滤除快照表记录；如：快照捕获后 clean 关闭提交了 SnapshotCreate 记录，
            // 副本头 = 捕获 LSN+1——缺口可验证即放行，流内命名空间记录全部在缺口之上，无双重应用）。
            if (baseLsn > _committedLsn || baseLsn < _sb.JournalCkptLsn)
                throw NewDeltaError($"目标不在基点：目标已提交头 {_committedLsn}，delta 基点 {baseLsn}（先还原到基点态再应用）");
            if (baseLsn < _committedLsn)
            {
                var gapVerified = false;
                foreach (var f in ScanJournalFrames(out _))
                {
                    if (f.Lsn <= baseLsn || f.Lsn > _committedLsn) continue;
                    gapVerified = true;
                    if (f.Type is not (JournalRecordType.SnapshotCreate or JournalRecordType.SnapshotDelete))
                        throw NewDeltaError(
                            $"目标不在基点：缺口 ({baseLsn}, {_committedLsn}] 含命名空间操作——双重应用风险（先还原到基点态）");
                }
                if (!gapVerified)
                    throw NewDeltaError("目标不在基点：缺口不可验证（快照表变更记录缺失——先还原到基点态）");
            }
            if (baseCrc != 0 && baseCrc != _sb.ImageCrc)
                throw NewDeltaError($"基线镜像 CRC 不符：delta {baseCrc} vs 卷 {_sb.ImageCrc}（目标状态非基点）");

            // 记录重放（与崩溃恢复同闸——发射器静默、共用操作函数）
            var payloadCrc = new Crc32();
            var recHeader = new byte[JournalHeaderSize];
            var bodyBuf = new byte[256 << 10];
            var padBuf = new byte[4096];
            ulong count = 0;
            ulong maxLsn = _committedLsn;
            _journalReplaying = true;
            try
            {
                while (true)
                {
                    if (!TryReadDeltaExact(input, recHeader.AsSpan(0, 4)))
                        throw NewDeltaError(count == 0
                            ? "delta 流尾缺失（记录后须有 TCD2 尾帧）"
                            : "delta 流截断（记录不完整）");
                    if (recHeader.AsSpan(0, 4).SequenceEqual(DeltaFooterMagic))
                    {
                        ReadDeltaExact(input, recHeader.AsSpan(4, DeltaFooterSize - 4), required: true);
                        var wantCount = BinaryPrimitives.ReadUInt64LittleEndian(recHeader.AsSpan(4, 8));
                        var wantCrc = BinaryPrimitives.ReadUInt32LittleEndian(recHeader.AsSpan(12, 4));
                        if (wantCount != count)
                            throw NewDeltaError($"记录数与尾帧不符：流 {count} vs 尾 {wantCount}");
                        if (wantCrc != payloadCrc.GetCurrentHashAsUInt32())
                            throw NewDeltaError("delta 载荷 CRC 校验失败（记录/数据流损毁）");
                        break;
                    }
                    if (recHeader.AsSpan(0, 4).SequenceEqual(DeltaDataMagic))
                    {
                        // 数据段：窗口内写块内容 → 直落载体（记录重放已给出物理事实——写块即数据面）
                        ReadDeltaExact(input, recHeader.AsSpan(4, 8), required: true);
                        payloadCrc.Append(recHeader.AsSpan(0, 12));
                        var dataCount = BinaryPrimitives.ReadUInt64LittleEndian(recHeader.AsSpan(4, 8));
                        var pageBuf = new byte[_pageSize];
                        for (ulong i = 0; i < dataCount; i++)
                        {
                            Span<byte> entry = stackalloc byte[12];
                            ReadDeltaExact(input, entry, required: true);
                            payloadCrc.Append(entry);
                            var phys = BinaryPrimitives.ReadUInt64LittleEndian(entry);
                            var blockCrc = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
                            ReadDeltaExact(input, pageBuf, required: true);
                            payloadCrc.Append(pageBuf);
                            if (Crc32.HashToUInt32(pageBuf) != blockCrc)
                                throw NewDeltaError($"数据块 CRC 校验失败（phys {phys}）");
                            if (phys >= _sb.CapacityBlocks)
                                throw NewDeltaError($"数据块越界（phys {phys} ≥ 容量 {_sb.CapacityBlocks}）");
                            WriteCarrier((long)(phys * (ulong)_pageSize), pageBuf);
                            InvalidateCacheBlocks(phys, 1);
                        }
                        continue;
                    }
                    ReadDeltaExact(input, recHeader.AsSpan(4, JournalHeaderSize - 4), required: true);
                    payloadCrc.Append(recHeader);
                    if (!recHeader.AsSpan(0, 4).SequenceEqual("RJRN"u8))
                        throw NewDeltaError("记录帧 magic 不符");
                    var type = (JournalRecordType)recHeader[4];
                    var lsn = BinaryPrimitives.ReadUInt64LittleEndian(recHeader.AsSpan(8));
                    var bodyLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(recHeader.AsSpan(24));
                    var bodyCrc = BinaryPrimitives.ReadUInt32LittleEndian(recHeader.AsSpan(28));
                    var framed = FramedSize(bodyLen);
                    if (bodyLen < 0 || bodyLen > bodyBuf.Length)
                        throw NewDeltaError($"记录体长非法：{bodyLen}");
                    if (type is JournalRecordType.SnapshotCreate or JournalRecordType.SnapshotDelete)
                        throw NewDeltaError("快照表记录不得出现于增量流（目标快照表 = 自身——V2 §1.2 裁决）");
                    if (type == JournalRecordType.Pad)
                        throw NewDeltaError("Pad 记录不得出现于增量流");
                    var body = new byte[bodyLen];
                    ReadDeltaExact(input, body, required: true);
                    payloadCrc.Append(body);
                    if (Crc32.HashToUInt32(body) != bodyCrc)
                        throw NewDeltaError($"记录体 CRC 校验失败（LSN {lsn}）");
                    var pad = framed - JournalHeaderSize - bodyLen;
                    while (pad > 0)
                    {
                        var take = Math.Min(padBuf.Length, pad);
                        ReadDeltaExact(input, padBuf.AsSpan(0, take), required: true);
                        payloadCrc.Append(padBuf.AsSpan(0, take));   // 载荷 CRC 覆盖对齐 padding（与导出侧逐帧全字节一致）
                        pad -= take;
                    }
                    ApplyJournalRecord(type, body);
                    maxLsn = Math.Max(maxLsn, lsn);
                    count++;
                }
            }
            finally
            {
                _journalReplaying = false;
            }
            _lsn = Math.Max(_lsn, maxLsn);
            _committedLsn = Math.Max(_committedLsn, maxLsn);
            CommitMetadata();   // 应用态原子持久 + CkptLsn 前进（链路增量可续——下一 delta 基点头 = 本批头）
            return new SnapshotDeltaSummary(count, baseLsn, maxLsn);
        }
    }

    private static void ReadDeltaExact(Stream input, Span<byte> dest, bool required)
    {
        var done = 0;
        while (done < dest.Length)
        {
            var n = input.Read(dest[done..]);
            if (n <= 0)
            {
                if (!required && done == 0) break;   // 允许的 EOF（流尾探测）
                throw new FileIOException(IOError.IOFailure,
                    $"增量流截断（帧不完整——期望 {dest.Length} 字节实得 {done}）", null, "ApplyDelta");
            }
            done += n;
        }
    }

    private static bool TryReadDeltaExact(Stream input, Span<byte> dest)
    {
        var done = 0;
        while (done < dest.Length)
        {
            var n = input.Read(dest[done..]);
            if (n <= 0) return done == 0 ? false
                : throw new FileIOException(IOError.IOFailure, "增量流截断（帧不完整）", null, "ApplyDelta");
            done += n;
        }
        return true;
    }

    private static FileIOException NewDeltaError(string detail)
        => new(IOError.IOFailure, $"增量还原失败：{detail}", null, "ApplyDelta");
}

/// <summary>增量导出/还原摘要（V2 §1.2——体积与范围信息）。</summary>
public readonly record struct SnapshotDeltaSummary(ulong RecordCount, ulong BaseLsn, ulong EndLsn);
