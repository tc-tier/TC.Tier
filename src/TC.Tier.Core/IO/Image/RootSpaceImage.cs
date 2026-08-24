namespace TC.Tier.Core.IO.Image;

/// <summary>
/// 根空间采集/还原管线（raw-medium-and-conversion-design §5）——四态互转的唯一机构。
/// <para>★ 只依赖 <see cref="IFileSystem"/>/IFileHandle 接口平面：对根空间内部住着谁一无所知，
///   任意介质 ↔ 任意介质（含 Raw 载体——virtual 介质的 .raw 文件/块设备载体层，已落地点亮）。</para>
/// <para>★ 采集契约：枚举（<see cref="IFileSystem.EnumerateEntries(string?, string, bool)"/> 递归）→
///   Stat/FileExtra/区间表 → 清单先行 → 逐区间帧化（稀疏保真：洞不占帧）。静默前置（§5.5）：
///   <see cref="ImageOptions.QuietSource"/> 且源置位 MaintenanceGate 时经维护门闩包夹（WriteOperations 档）。</para>
/// <para>★ 还原契约：目标根空间必须为空（非空即拒——显式失败优于静默合并，§5.3）；目录（父先）→
///   文件（帧落位 → SetLength 定逻辑长度 → FileExtra 回填）→ 逐句柄 Flush（Remote 介质 Flush 即持久化）。</para>
/// <para>★ Stat 时间戳仅入清单审计（<c>ModifiedTicks</c>），不承诺还原——IFileSystem 无时间戳写入平面（§5.2 诚实表达）。</para>
/// </summary>
public static class RootSpaceImage
{
    /// <summary>
    /// 采集根空间 → TCA1 流。源须已静默（或 <see cref="ImageOptions.QuietSource"/> 自动门闩）。
    /// </summary>
    public static ImageSummary Capture(IFileSystem source, Stream output, ImageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(output);
        options ??= new ImageOptions();
        options.Validate();

        IDisposable? lease = null;
        try
        {
            if (options.QuietSource && source.Capabilities.HasFlag(FileSystemCapabilities.MaintenanceGate))
                lease = source.EnterMaintenance("image-capture", MaintenanceScope.WriteOperations, CancellationToken.None);

            return CaptureCore(source, output, options);
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private static ImageSummary CaptureCore(IFileSystem source, Stream output, ImageOptions options)
    {
        // ── 第一阶段：清单（枚举 + Stat + FileExtra + 区间表）──────────────────────
        var entries = new List<ImageFormat.ManifestEntry>();
        foreach (var e in source.EnumerateEntries(recursive: true))
        {
            if (e.Type == FsEntryType.Directory)
            {
                entries.Add(new ImageFormat.ManifestEntry(e.Name, FsEntryType.Directory, 0, [], 0, []));
                continue;
            }
            var info = source.Stat(e.Name);
            byte[] extra;
            using (var h = source.Open(e.Name, new FileOpenOptions
            {
                Access = AccessMode.Read,
                Mode = FileOpenMode.OpenExisting,
                Sharing = FileSharing.ReadWrite,
            }))
            {
                h.Advise(FileAdvise.Sequential);   // 能力位置位才生效——best-effort
                extra = h.FileExtra.ToArray();
                // D4：unwritten 保真（§5.2）——区间带状态；unwritten 段不占数据帧
                var ranges = h.EnumerateAllocatedRangesDetailed()
                    .Select(r => (r.Start, r.End, r.Unwritten ? ImageFormat.RangeUnwritten : (byte)0))
                    .ToList();
                entries.Add(new ImageFormat.ManifestEntry(e.Name, FsEntryType.File, h.Length, extra,
                    info.LastWriteTime.UtcTicks, ranges));
            }
        }

        // 确定性：路径 Ordinal 排序（目录先于其内容——还原端父先建的天然序）
        entries.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));

        using var bw = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        ImageFormat.WriteManifest(bw, options, entries);

        // ── 第二阶段：数据帧（逐条目逐区间，稀疏跳洞）──────────────────────────
        long frameCount = 0, rawBytes = 0;
        uint framesCrc = 0;
        var buffer = new byte[options.FrameBytes];
        for (var idx = 0; idx < entries.Count; idx++)
        {
            var entry = entries[idx];
            if (entry.Type != FsEntryType.File) continue;
            using var h = source.Open(entry.Path, new FileOpenOptions
            {
                Access = AccessMode.Read,
                Mode = FileOpenMode.OpenExisting,
                Sharing = FileSharing.ReadWrite,
            });
            foreach (var (start, end, flags) in entry.Ranges)
            {
                if ((flags & ImageFormat.RangeUnwritten) != 0)
                    continue;   // D4（§5.2）：unwritten 段不占数据帧——还原端按预分配语义重建
                for (var pos = start; pos < end; pos += buffer.Length)
                {
                    var want = (int)Math.Min(buffer.Length, end - pos);
                    var got = h.Read(pos, buffer.AsSpan(0, want));
                    if (got <= 0) break;   // EOF 契约——区间表领先物理长度时按实际截断
                    var (_, rawLen, crc) = ImageFormat.WriteFrame(bw, (uint)idx, pos, buffer.AsSpan(0, got),
                        options.Compression);
                    frameCount++;
                    rawBytes += rawLen;
                    framesCrc = ImageFormat.AggregateCrc(framesCrc, crc);
                    if (got < want) break;
                }
            }
        }

        ImageFormat.WriteFooter(bw, frameCount, rawBytes, framesCrc);
        bw.Flush();
        return new ImageSummary(entries.Count, frameCount, rawBytes);
    }

    /// <summary>
    /// 从 TCA1 流还原到目标根空间（必须为空）。逐帧 CRC + 流尾对账（<see cref="ImageOptions.VerifyChecksums"/> 可关）。
    /// </summary>
    public static ImageSummary Restore(Stream input, IFileSystem destination, ImageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(destination);
        options ??= new ImageOptions();
        options.Validate();

        // 空目标前置：先保根存在（Disk 根目录可能尚未创建——Restore 自保，
        // 消费方无须先 EnsureRoot——可用性修复：此前对全新 Disk 根直接枚举抛 DirectoryNotFound）
        destination.EnsureRoot();
        if (destination.EnumerateEntries(recursive: true).Any())
            throw new FileIOException(IOError.AlreadyExists,
                "还原目标根空间必须为空（v1 无合并语义——显式失败优于静默覆盖，设计 §5.3）。",
                null, nameof(Restore));

        using var br = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
        var (_, entries) = ImageFormat.ReadManifest(br);

        // 目录先建（排序保证父先）；文件随后（帧落位 → 长度 → FileExtra）
        foreach (var e in entries.Where(e => e.Type == FsEntryType.Directory).OrderBy(e => e.Path.Length))
            destination.CreateDirectory(e.Path);

        var handles = new Dictionary<int, IFileHandle>();
        try
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Type != FsEntryType.File) continue;
                // D4（§5.2）：unwritten 保真——预分配语义重建（物理预留 + 读零 + 写转换）。
                // 各介质 CreateFile(preallocateSize) 语义对齐（Disk=fallocate / Mem=Reserved / Raw=unwritten 区间；
                // Remote 服务端 no-op——预留退化为洞，读零语义保持，诚实降级）。
                var prealloc = 0L;
                foreach (var (_, end, flags) in e.Ranges)
                    if ((flags & ImageFormat.RangeUnwritten) != 0 && end > prealloc)
                        prealloc = end;
                destination.CreateFile(e.Path, prealloc);
                handles[i] = destination.Open(e.Path, new FileOpenOptions
                {
                    Access = AccessMode.ReadWrite,
                    Mode = FileOpenMode.OpenExisting,
                    Sharing = FileSharing.ReadWrite,
                });
            }

            long frameCount = 0, rawBytes = 0;
            uint framesCrc = 0;
            // D5：magic 探测帧/流尾边界——非可寻址流（网络/管道）统一支持，
            // 不再依赖 Position/Length（帧 entryIdx 与尾 magic 的碰撞由下方条目界校验兜底）
            var probe = new byte[4];
            try
            {
                while (true)
                {
                    if (!ReadProbe(br.BaseStream, probe))
                        throw ImageFormat.NewFormatError(null, "流在流尾前结束（缺 TCE1 流尾——流不完整）");
                    if (probe.AsSpan().SequenceEqual(ImageFormat.FooterMagic))
                        break;   // 流尾 magic 已消费——字段对账在下方
                    var entryIdx = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(probe);
                    if (entryIdx >= (uint)entries.Count || !handles.TryGetValue((int)entryIdx, out var h))
                        throw ImageFormat.NewFormatError(null, $"帧引用未知条目 #{entryIdx}——清单/数据不一致");
                    var (offset, raw) = ImageFormat.ReadFrameCore(br, options.VerifyChecksums);
                    h.Write(offset, raw);
                    frameCount++;
                    rawBytes += raw.Length;
                    framesCrc = ImageFormat.AggregateCrc(framesCrc, Crc32Of(raw));
                }

                // 尾对账 + 长度/FileExtra 定稿
                ImageFormat.ReadFooterFieldsAndVerify(br, frameCount, rawBytes, framesCrc);
            }
            catch (EndOfStreamException ex)
            {
                throw ImageFormat.NewFormatError(null, $"流中途截断（缺 TCE1 流尾/帧不完整）：{ex.Message}");
            }
            foreach (var (idx, h) in handles)
            {
                var e = entries[idx];
                h.SetLength(e.LogicalLength);   // 逻辑长度权威（尾洞/尾零区不在帧中）
                if (e.Extra.Length > 0)
                    h.SetFileExtra(e.Extra);
                h.Flush();   // Remote 介质 Flush 即持久化；Disk/Mem 无害
            }
            return new ImageSummary(entries.Count, frameCount, rawBytes);
        }
        finally
        {
            foreach (var h in handles.Values)
                h.Dispose();
        }
    }

    /// <summary>读满固定字节（纯流式支持——D5）；EOF 恰在边界返回 false，中途截断抛格式错误。</summary>
    private static bool ReadProbe(Stream s, byte[] buffer)
    {
        var got = 0;
        while (got < buffer.Length)
        {
            var n = s.Read(buffer, got, buffer.Length - got);
            if (n <= 0)
                return got == 0
                    ? false
                    : throw ImageFormat.NewFormatError(null, $"流中途截断（{got}/{buffer.Length} 字节）");
            got += n;
        }
        return true;
    }

    private static uint Crc32Of(byte[] raw)
    {
        // 复用格式侧同一 CRC（帧校验在 ReadFrame 内已完成；此处为聚合对账重算）
        return System.IO.Hashing.Crc32.HashToUInt32(raw);
    }

    /// <summary>
    /// 介质间转移（dd 快道路由入口——raw-medium-and-conversion-design §6.2）。
    /// <para>★ 路由（能力位驱动，非按介质硬编码）：源与目标<b>均</b>置位
    ///   <see cref="FileSystemCapabilities.ContiguousCapture"/> 且实现 <see cref="IContiguousVolume"/>
    ///   → **字节直拷快道**：双侧维护租约内整卷原始字节顺序拷贝（真·dd，产物 = 整卷字节镜像）。
    ///   其余组合 → 结构化管线（TCA1 流经内存中转）。</para>
    /// <para>★ 快道契约：租约由本方法进出（源 WriteOperations 档、目标 AllOperations 档——写入端完全隔离）；
    ///   拷贝后长度对账（短读即 <see cref="IOError.IOFailure"/>）。压缩选项对快道不生效
    ///   （字节镜像语义——压缩属结构化产物，§6.3）。</para>
    /// </summary>
    public static ImageSummary Transfer(IFileSystem source, IFileSystem target, ImageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        if (source is IContiguousVolume srcVolume
            && target is IContiguousVolume dstVolume
            && source.Capabilities.HasFlag(FileSystemCapabilities.ContiguousCapture)
            && target.Capabilities.HasFlag(FileSystemCapabilities.ContiguousCapture))
            return FastPath(srcVolume, source, dstVolume, target);

        // 结构化回退：TCA1 流经临时文件中转（D3 修复——MemoryStream 中转 = O(数据量) 内存，
        // 大卷备份/归档直接 OOM；溢出到宿主临时文件，DeleteOnClose 自清理）。
        // P3 网络流落地时此中转移交传送层。
        var stagingPath = Path.Combine(Path.GetTempPath(), $"tc-tier-image-{Guid.NewGuid():N}.tca1");
        using var staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 1 << 20, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
        Capture(source, staging, options);
        staging.Position = 0;
        return Restore(staging, target, options);
    }

    private static ImageSummary FastPath(IContiguousVolume srcVolume, IFileSystem source,
        IContiguousVolume dstVolume, IFileSystem target)
    {
        using var srcLease = source.EnterMaintenance("transfer-fastpath-src", MaintenanceScope.WriteOperations,
            CancellationToken.None);
        using var dstLease = target.EnterMaintenance("transfer-fastpath-dst", MaintenanceScope.AllOperations,
            CancellationToken.None);

        using var srcStream = srcVolume.OpenRawBacking(writable: false);
        using var dstStream = dstVolume.OpenRawBacking(writable: true);

        // ★ 容量预检（D6——设计 §10：整卷覆盖是破坏性操作，先验后写，盲写禁止）：
        // 目标卷容量 < 源卷字节数 → 写前拒绝，目标零字节受损。
        if (dstStream.Length < srcStream.Length)
            throw new FileIOException(IOError.DiskFull,
                $"目标卷容量不足：{dstStream.Length} < 源卷 {srcStream.Length} 字节——" +
                "整卷覆盖已预检拒绝（目标未受任何写入）", null, "Transfer");

        srcStream.CopyTo(dstStream);
        var copied = dstStream.Position;
        if (copied != srcStream.Length)
            throw new FileIOException(IOError.IOFailure,
                $"快道拷贝长度不符：复制 {copied} ≠ 源 {srcStream.Length}（短读）——镜像不完整", null, "Transfer");
        dstStream.Flush();
        dstVolume.OnMirrorCompleted();   // 目标实例内存态从盘重建（镜像覆盖了盘上状态）
        return new ImageSummary(0, 0, copied);
    }
}
