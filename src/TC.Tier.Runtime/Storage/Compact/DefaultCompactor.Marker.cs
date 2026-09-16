using System.Buffers.Binary;
namespace TC.Tier.Runtime.Storage.Compact;

internal sealed partial class DefaultCompactor
{
    // ═══════════════════════════════════════════════════════════════
    //  commit marker（#297 S1：单 lease 单文件）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>marker 根目录相对路径（引擎子目录下，'/' 分隔）。</summary>
    private string MarkerDirectory => SupportsMarker ? $"{DeviceName}" : string.Empty;

    /// <summary>marker 文件名（引擎名 + lease 序号）——每 lease 独立文件：崩溃恢复逐文件补执行、
    /// 残缺丢弃，多 lease 互不覆盖（旧单文件形态"合并到最后一个"会丢先前 lease 的处置条目）。</summary>
    private string MarkerPathOf(int leaseSeq) => SupportsMarker
        ? $"{DeviceName}/{LastComponent(DeviceName)}{MarkerFileNameSuffix}.{leaseSeq}"
        : string.Empty;

    /// <summary>兼容形态：旧单文件 marker（无序号后缀，存量盘）。</summary>
    private string LegacyMarkerPath => SupportsMarker
        ? $"{DeviceName}/{LastComponent(DeviceName)}{MarkerFileNameSuffix}"
        : string.Empty;

    /// <summary>取路径最后组件（'/' 唯一分隔符）。</summary>
    private static string LastComponent(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx < 0 ? path : path[(idx + 1)..];
    }

    private void EnsureNoPendingCommitMarker()
    {
        if (!SupportsMarker) return;
        // 新形态逐文件 + 旧形态单文件——任一存在即拒新 Compact（重启恢复后再发起）
        if (EnumerateMarkerPaths().Count > 0)
        {
            throw new InvalidOperationException(
                $"A pending Compact marker exists under '{DeviceName}'. Restart the engine to complete recovery.");
        }
    }

    /// <summary>枚举当前全部待恢复 marker（新形态 {name}.marker.{seq} 逐文件 + 旧形态单文件，升序）。</summary>
    private List<(string Path, int Seq)> EnumerateMarkerPaths()
    {
        var result = new List<(string, int)>();
        if (!SupportsMarker) return result;

        var legacy = LegacyMarkerPath;
        try
        {
            // 新形态：引擎子目录下以 {name}.compact.marker. 为前缀的文件（seq = 文件名尾段整数）。
            // ★ 必须**双参显式** EnumerateFiles(dir, pattern)——单实参调用会经 C# 重载解析命中
            //   pattern 单参重载（在根空间找同名文件→恒空，实测陷阱）；整目录枚举 + 前缀过滤
            //   为跨介质稳态（pattern 通配语义不再依赖）。
            string markerPrefix = $"{LastComponent(DeviceName)}{MarkerFileNameSuffix}.";
            string tmpSuffix = ".tmp";
            foreach (var entry in _fileSystem.EnumerateFiles(MarkerDirectory, "*"))
            {
                if (!entry.Name.StartsWith(markerPrefix, StringComparison.Ordinal)) continue;
                var tail = entry.Name.Substring(markerPrefix.Length);
                if (tail.EndsWith(tmpSuffix, StringComparison.Ordinal))
                    tail = tail[..^tmpSuffix.Length];   // .tmp 残留不算待恢复 marker（DeleteAllTemps 清）
                if (int.TryParse(tail, out var seq) && seq >= 0)
                    result.Add(($"{MarkerDirectory}/{entry.Name}", seq));
            }
        }
        catch (FileIOException ex) when (ex.Error == IOError.NotFound)
        {
            // 子目录不存在 = 无新形态 marker（首启/内存引擎变体）——非错误
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "EnumerateMarkerPaths: 目录枚举失败 dir={Dir}", MarkerDirectory);
        }

        if (_fileSystem.Exists(legacy)) result.Add((legacy, -1));   // 旧形态排最前（先消化存量）
        result.Sort(static (a, b) => a.Item2.CompareTo(b.Item2));
        return result;
    }

    /// <summary>写 commit marker（SupportsMarker=false 时跳过）——每 lease 一份（#297 S1：
    /// 序号 = 当前待恢复 marker 最大序号 + 1，崩溃残留文件不覆盖、恢复逐文件消化）。
    /// 写后记入 <see cref="_lastWrittenMarkerPath"/>——同一次 Compact 的后续删除收口用。</summary>
    private void WriteCommitMarker(CompactType compactType,
        int firstNewSegId, int newSegCount, List<OldSegmentDisposition> dispositions)
    {
        if (!SupportsMarker) return;

        var pending = EnumerateMarkerPaths();
        int leaseSeq = pending.Count > 0 ? pending[^1].Item2 + 1 : 0;   // 旧形态(-1)存在时新文件从 0 起，互不干扰
        _lastWrittenMarkerPath = MarkerPathOf(leaseSeq);
        WriteCommitMarkerTo(_lastWrittenMarkerPath, compactType, firstNewSegId, newSegCount, dispositions);
    }

    /// <summary>本次 Compact 最近写入的 marker 路径（null = 本次未写——内存模式/失败早退）。</summary>
    private string? _lastWrittenMarkerPath;

    /// <summary>删除本次 Compact 写入的 marker（无记录 = no-op）。</summary>
    private void DeleteLastWrittenCommitMarker()
    {
        if (_lastWrittenMarkerPath is null) return;
        DeleteCommitMarkerRequired(_lastWrittenMarkerPath);
        _lastWrittenMarkerPath = null;
    }

    private void WriteCommitMarkerTo(string markerPath, CompactType compactType,
        int firstNewSegId, int newSegCount, List<OldSegmentDisposition> dispositions)
    {
        int bodySize = sizeof(int) * newSegCount +
                       OldSegmentDispositionCodec.StructSize * dispositions.Count;
        int totalSize = CompactMarkerHeaderCodec.StructSize + bodySize;

        using var buf = new AlignedMemoryManager(totalSize, AlignmentConst.Alignment4K);
        Span<byte> span = buf.GetSpan();

        for (int i = 0; i < newSegCount; i++)
            BinaryPrimitives.WriteInt32LittleEndian(
                span.Slice(CompactMarkerHeaderCodec.StructSize + i * sizeof(int)),
                firstNewSegId + i);

        int dispositionStart = CompactMarkerHeaderCodec.StructSize + sizeof(int) * newSegCount;
        for (int i = 0; i < dispositions.Count; i++)
        {
            var d = dispositions[i];
            OldSegmentDispositionCodec.Write(
                span.Slice(dispositionStart + i * OldSegmentDispositionCodec.StructSize),
                in d);
        }

        var header = new CompactMarkerHeader(compactType, newSegCount, dispositions.Count);
        CompactMarkerHeaderCodec.Write(span, in header);

        span.Slice(CompactMarkerHeaderCodec.Offset_Crc, sizeof(uint)).Clear();
        header.Crc = UnifiedCrc.ComputeCrc32(span);
        CompactMarkerHeaderCodec.Write(span, in header);

        string tmpPath = markerPath + ".tmp";
        using (var h = _fileSystem.Open(tmpPath, new FileOpenOptions
               {
                   Access = AccessMode.Write,
                   Mode = FileOpenMode.OpenOrCreate,   // ★ 存在与否皆开（L4 取证）：CreateNew 在上次写失败
                   //   残留的空 tmp 上撞 AlreadyExists（单次 marker 写失败永久砖死后续 Compact）；
                   //   Truncate 模式磁盘对不存在文件抛 NotFound（介质行为分歧）——统一 OpenOrCreate+截断
                   Sharing = FileSharing.None,
                   Hints = FileOpenHints.WriteThrough,
               }))
        {
            h.SetLength(0);   // 截残留（上次失败遗留更长 tmp 时防尾部垃圾破坏 marker 定长校验）
            h.Write(0, span);
            h.Flush();
        }

        _fileSystem.Move(tmpPath, markerPath, overwrite: true);   // 原子换名 + 父目录 fsync 内建
    }

    /// <summary>读指定 commit marker。</summary>
    private bool TryReadCommitMarker(string path, out CompactMarkerHeader header, out int[] newSegIds,
        out OldSegmentDisposition[] dispositions)
    {
        header = default;
        newSegIds = Array.Empty<int>();
        dispositions = Array.Empty<OldSegmentDisposition>();

        if (string.IsNullOrEmpty(path)) return false;

        byte[] bytes;
        try
        {
            if (!_fileSystem.Exists(path)) return false;

            using (var h = _fileSystem.Open(path, new FileOpenOptions
                   {
                       Access = AccessMode.Read,
                       Mode = FileOpenMode.OpenExisting,
                       Sharing = FileSharing.ReadWrite | FileSharing.Delete,
                   }))
            {
                bytes = new byte[h.Length];
                var read = h.Read(0, bytes);
                if (read != bytes.Length) return false;
            }
        }
        catch (IOException)
        {
            return false;
        }

        return TryParseCommitMarker(bytes, out header, out newSegIds, out dispositions);
    }

    /// <summary>校验并解码完整 marker；格式违约返回 false，资源类异常向上传播。</summary>
    internal static bool TryParseCommitMarker(byte[] bytes, out CompactMarkerHeader header, out int[] newSegIds,
        out OldSegmentDisposition[] dispositions)
    {
        header = default;
        newSegIds = Array.Empty<int>();
        dispositions = Array.Empty<OldSegmentDisposition>();
        if (bytes.Length < CompactMarkerHeaderCodec.StructSize)
            return false;

        header = CompactMarkerHeaderCodec.Read(bytes);
        if (!header.IsValid || header.NewSegCount < 0 || header.OldSegDispositionCount < 0)
            return false;

        int bodySize = bytes.Length - CompactMarkerHeaderCodec.StructSize;
        long expectedSize = sizeof(int) * (long)header.NewSegCount +
                            OldSegmentDispositionCodec.StructSize * (long)header.OldSegDispositionCount;
        if (bodySize != expectedSize) return false;

        uint storedCrc = header.Crc;
        bytes.AsSpan(CompactMarkerHeaderCodec.Offset_Crc, sizeof(uint)).Clear();
        uint computedCrc = UnifiedCrc.ComputeCrc32(bytes);
        if (storedCrc != computedCrc) return false;

        newSegIds = new int[header.NewSegCount];
        for (int i = 0; i < header.NewSegCount; i++)
            newSegIds[i] = BinaryPrimitives.ReadInt32LittleEndian(
                bytes.AsSpan(CompactMarkerHeaderCodec.StructSize + i * sizeof(int), sizeof(int)));

        dispositions = new OldSegmentDisposition[header.OldSegDispositionCount];
        int dispStart = CompactMarkerHeaderCodec.StructSize + sizeof(int) * header.NewSegCount;
        for (int i = 0; i < header.OldSegDispositionCount; i++)
            dispositions[i] = OldSegmentDispositionCodec.Read(
                bytes.AsSpan(dispStart + i * OldSegmentDispositionCodec.StructSize,
                    OldSegmentDispositionCodec.StructSize));
        return true;
    }

    private void DeleteCommitMarker(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (_fileSystem.Exists(path)) _fileSystem.Delete(path);   // 耐久删除（Core 契约）
        }
        catch (FileIOException)
        {
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "DeleteCommitMarker 失败 path={path}", path);
        }
    }

    private void DeleteCommitMarkerRequired(string path)
    {
        if (string.IsNullOrEmpty(path) || !_fileSystem.Exists(path)) return;
        _fileSystem.Delete(path);
        if (_fileSystem.Exists(path))
            throw new FileIOException(IOError.SharingViolation,
                $"Compact marker '{path}' could not be deleted.", path, nameof(DeleteCommitMarkerRequired));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Recover（marker 补执行）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>启动时 marker 恢复——逐文件补执行未完成的 Compact（#297 S1：每 lease 一份，
    /// 崩溃残留多份时依序消化；残缺文件丢弃重试）。</summary>
    private void RecoverCompactMarker(CompactLeaseFactory leaseFactory)
    {
        // 内存模式（无 marker）→ 仅清临时段
        if (!SupportsMarker)
        {
            DeleteAllTemps();
            return;
        }

        var markers = EnumerateMarkerPaths();
        if (markers.Count == 0)
        {
            DeleteAllTemps();
            return;
        }

        foreach (var (markerPath, _) in markers)
            RecoverOneMarker(markerPath, leaseFactory);

        DeleteAllTemps();
    }

    /// <summary>补执行单个 marker（完整 → 消化后删除；残缺 → 删除告警——Phase 2 补执行以
    /// 已落盘段文件为准，残缺 marker 对应的 lease 由其自身段现状自洽）。</summary>
    private void RecoverOneMarker(string markerPath, CompactLeaseFactory leaseFactory)
    {
        if (!TryReadCommitMarker(markerPath, out var header, out var newSegIds, out var dispositions))
        {
            _logger?.LogWarning(
                "Compact marker '{Path}' is invalid or uses an unsupported version; "
                + "deleting corrupt marker and cleaning up temp artifacts.",
                markerPath);
            DeleteCommitMarker(markerPath);
            return;
        }

        if (header.CompactType == CompactType.Range)
        {
            foreach (var segId in newSegIds)
            {
                if (TempExists(segId))
                    PromoteTemp(segId);
            }

            WriteSegmentMetaForRecoveredRangeSegments(newSegIds);

            DeleteCommitMarkerRequired(markerPath);
            return;
        }

        // Phase 2 崩溃 → 补执行（通过 ICompactLease 原子替换段表）
        if (newSegIds.Length > 0)
        {
            int minSeg = newSegIds.Min();
            int maxSeg = Math.Max(
                newSegIds.Max(),
                dispositions.Length > 0 ? dispositions.Max(d => d.SegId) : 0);
            var from = new LogicalAddress(minSeg, 0);
            var to = new LogicalAddress(maxSeg + 1, 0);

            using var lease = leaseFactory(from, to);
            var chunks = lease.Chunks.ToList();

            // ★ STORAGE-001 (#221)：先把仍存在的临时段（.compact）提升为正式段。
            //   崩溃可能发生在主路径 PromoteTemp 之前/之中——此时正式段不存在、.compact 临时段含数据。
            //   若不先 PromoteTemp，下方 SetReplacement 的 SegmentExists 检查会全部跳过，
            //   最后 DeleteAllTemps 删掉唯一含数据的 .compact → 整批数据丢失。
            foreach (var segId in newSegIds)
            {
                if (TempExists(segId))
                    PromoteTemp(segId);
            }

            // 新段恢复：SetReplacement（PromoteTemp 后正式段已就位）
            foreach (var segId in newSegIds)
            {
                if (!SegmentExists(segId)) continue;
                long fileSize = GetSegmentLength(segId);
                var chunk = chunks.FirstOrDefault(c => c.SegId == segId);
                chunk?.SetReplacement(fileSize, fileSize);
            }

            // 旧段处置：MarkInvalid（删除）
            foreach (var d in dispositions)
            {
                if (!d.IsDelete) continue;
                var chunk = chunks.FirstOrDefault(c => c.SegId == d.SegId);
                chunk?.MarkInvalid();
            }

            lease.Commit();

            WriteSegmentMetaForRecoveredFullSegments(newSegIds);
        }

        // 旧段物理处置（PunchHole / DeleteFile）
        foreach (var d in dispositions)
        {
            if (d.IsDelete)
            {
                try
                {


                }
                catch
                {
                    /* ignored */
                }
                DeleteSegment(d.SegId);
            }
            else
            {
                try
                {
                    PunchHoleSegment(d.SegId, d.PunchStart, d.PunchEnd - d.PunchStart);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Recover PunchHole 失败 segId={segId}", d.SegId);
                }
            }
        }


        DeleteCommitMarkerRequired(markerPath);
    }

}
