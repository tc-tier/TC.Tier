using System.Buffers.Binary;
using System.IO.Hashing;
namespace TC.Tier.Runtime.Storage.Compact;

internal sealed partial class DefaultCompactor
{
    // ═══════════════════════════════════════════════════════════════
    //  commit marker
    // ═══════════════════════════════════════════════════════════════

    /// <summary>marker 根空间相对路径（引擎子目录下，'/' 分隔）。</summary>
    private string MarkerPath => SupportsMarker
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
        if (!string.IsNullOrEmpty(MarkerPath) && _fileSystem.Exists(MarkerPath))
        {
            throw new InvalidOperationException(
                $"A pending Compact marker exists at '{MarkerPath}'. Restart the engine to complete recovery.");
        }
    }

    /// <summary>写 commit marker（SupportsMarker=false 时跳过）。</summary>
    private void WriteCommitMarker(CompactType compactType,
        int firstNewSegId, int newSegCount, List<OldSegmentDisposition> dispositions)
    {
        string markerPath = MarkerPath;
        if (string.IsNullOrEmpty(markerPath)) return;

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
        header.Crc = Crc32.HashToUInt32(span);
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

    /// <summary>读 commit marker。</summary>
    private bool TryReadCommitMarker(out CompactMarkerHeader header, out int[] newSegIds,
        out OldSegmentDisposition[] dispositions)
    {
        header = default;
        newSegIds = Array.Empty<int>();
        dispositions = Array.Empty<OldSegmentDisposition>();

        string path = MarkerPath;
        if (string.IsNullOrEmpty(path)) return false;

        try
        {
            if (!_fileSystem.Exists(path)) return false;

            byte[] bytes;
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

            if (bytes.Length < CompactMarkerHeaderCodec.StructSize)
                return false;

            header = CompactMarkerHeaderCodec.Read(bytes);
            if (!header.IsValid) return false;

            int bodySize = bytes.Length - CompactMarkerHeaderCodec.StructSize;
            int expectedSize = sizeof(int) * header.NewSegCount +
                               OldSegmentDispositionCodec.StructSize * header.OldSegDispositionCount;
            if (bodySize != expectedSize) return false;

            uint storedCrc = header.Crc;
            bytes.AsSpan(CompactMarkerHeaderCodec.Offset_Crc, sizeof(uint)).Clear();
            uint computedCrc = Crc32.HashToUInt32(bytes);
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
        catch
        {
            return false;
        }
    }

    private void DeleteCommitMarker()
    {
        string path = MarkerPath;
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

    private void DeleteCommitMarkerRequired()
    {
        string path = MarkerPath;
        if (string.IsNullOrEmpty(path) || !_fileSystem.Exists(path)) return;
        _fileSystem.Delete(path);
        if (_fileSystem.Exists(path))
            throw new FileIOException(IOError.SharingViolation,
                $"Compact marker '{path}' could not be deleted.", path, nameof(DeleteCommitMarkerRequired));
    }

    // ═══════════════════════════════════════════════════════════════
    //  Recover（marker 补执行）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>启动时 marker 恢复——补执行未完成的 Compact。</summary>
    private void RecoverCompactMarker(CompactLeaseFactory leaseFactory)
    {
        // 内存模式（无 marker）→ 仅清临时段
        if (!SupportsMarker)
        {
            DeleteAllTemps();
            return;
        }

        if (!_fileSystem.Exists(MarkerPath))
        {
            DeleteAllTemps();
            return;
        }

        if (!TryReadCommitMarker(out var header, out var newSegIds, out var dispositions))
        {
            _logger?.LogWarning(
                "Compact marker '{Path}' is invalid or uses an unsupported version; "
                + "deleting corrupt marker and cleaning up temp artifacts.",
                MarkerPath);
            DeleteCommitMarker();
            DeleteAllTemps();
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

            DeleteCommitMarkerRequired();
            DeleteAllTemps();
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
                // ★ 先 Remove + Flush 再删文件（同 ProcessOldSegDispositions——VII-1 家族封口）
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


        DeleteCommitMarkerRequired();
        DeleteAllTemps();
    }
}
