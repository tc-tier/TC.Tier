namespace TC.Tier.Core.Tests.NativeInterop;

/// <summary>
/// Compact 算法原型——验证"拷贝有效区间到新文件 + 生成映射表 + 回收旧区间"的核心逻辑。
/// <para>不依赖完整 Device 脚手架（LogicalAddress/Allocator/AddressMap），用裸文件操作 +
///   已验证的 <see cref="FileNative.PunchHole"/>。</para>
/// <para>这是 §6.8 Compact 设计的地基验证——证明算法可行后再集成进正式 Device。</para>
/// </summary>
internal static class CompactPrototype
{
    /// <summary>有效区间（旧文件中需保留的连续字节段）。</summary>
    public readonly struct KeepExtent(long offset, long length)
    {
        public readonly long Offset = offset;
        public readonly long Length = length;
    }

    /// <summary>迁移映射：旧文件偏移 → 新文件偏移。</summary>
    public readonly struct Migration(long oldOffset, long newOffset, long length)
    {
        public readonly long OldOffset = oldOffset;
        public readonly long NewOffset = newOffset;
        public readonly long Length = length;
    }

    /// <summary>
    /// Compact 核心算法：把旧文件的有效区间紧凑拷贝到新文件，返回迁移映射 + 回收旧区间。
    /// </summary>
    /// <param name="oldPath">旧文件路径。</param>
    /// <param name="newPath">新文件路径（紧凑，无空洞）。</param>
    /// <param name="extents">旧文件中需保留的有效区间列表（须按 Offset 升序）。</param>
    /// <param name="logger">可选日志。</param>
    /// <returns>迁移映射列表（旧→新）。</returns>
    public static List<Migration> Compact(string oldPath, string newPath, IReadOnlyList<KeepExtent> extents, ILogger? logger = null)
    {
        if (extents.Count == 0)
            return new List<Migration>();  // 空保留：不创建新文件，不回收

        // 须按 Offset 升序
        var sorted = extents.OrderBy(e => e.Offset).ToList();

        using var oldHandle = File.OpenHandle(oldPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var newHandle = File.OpenHandle(newPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        var migrations = new List<Migration>(sorted.Count);
        long newOffset = 0;
        const int BufSize = 64 * 1024;
        var buf = new byte[BufSize];

        // 1. 拷贝有效区间到新文件（紧凑连续）
        foreach (var ext in sorted)
        {
            long remaining = ext.Length;
            long oldOff = ext.Offset;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min(remaining, BufSize);
                int read = RandomAccess.Read(oldHandle, buf.AsSpan(0, toRead), oldOff);
                if (read == 0) break;  // EOF 保护
                RandomAccess.Write(newHandle, buf.AsSpan(0, read), newOffset);
                oldOff += read;
                newOffset += read;
                remaining -= read;
            }
            migrations.Add(new Migration(ext.Offset, newOffset - ext.Length, ext.Length));
        }
        RandomAccess.FlushToDisk(newHandle);

        // 2. 回收旧文件中"未被保留的区间"（间隙 = 空洞）
        //    间隙 = [prevEnd, cur.Offset) 逐段 PunchHole
        long prevEnd = 0;
        foreach (var ext in sorted)
        {
            if (ext.Offset > prevEnd)
            {
                // [prevEnd, ext.Offset) 是要回收的空洞
                var pr = FileNative.PunchHole(oldHandle, prevEnd, ext.Offset - prevEnd, logger);
                logger?.LogWarning("Compact PunchHole [{Off},{End}) = {Pr}", prevEnd, ext.Offset, pr);
            }
            prevEnd = ext.Offset + ext.Length;
        }
        // 尾部 [prevEnd, fileSize) 也回收
        var fileSize = new FileInfo(oldPath).Length;
        if (fileSize > prevEnd)
        {
            var pr2 = FileNative.PunchHole(oldHandle, prevEnd, fileSize - prevEnd, logger);
            logger?.LogWarning("Compact tail PunchHole [{Off},{End}) = {Pr}", prevEnd, fileSize, pr2);
        }
        RandomAccess.FlushToDisk(oldHandle);  // 确保打洞生效

        return migrations;
    }

    /// <summary>
    /// 用映射表翻译旧偏移到新偏移（二分查找）。
    /// </summary>
    public static long? Translate(IReadOnlyList<Migration> migrations, long oldOffset)
    {
        // 找包含 oldOffset 的迁移段
        foreach (var m in migrations)
        {
            if (oldOffset >= m.OldOffset && oldOffset < m.OldOffset + m.Length)
                return m.NewOffset + (oldOffset - m.OldOffset);
        }
        return null;  // 不在任何保留区间内
    }

    /// <summary>
    /// 计算文件的空洞率（HoleRatio）= (逻辑大小 - 实际磁盘占用) / 逻辑大小。
    /// 上层据此 + 自己的阈值决定是否 Compact。设备只报告，不自行触发。
    /// </summary>
    public static double ComputeHoleRatio(string path)
    {
        var logicalSize = new FileInfo(path).Length;
        if (logicalSize == 0) return 0.0;
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
        var allocatedSize = FileNative.GetFileAllocatedDiskSize(handle);
        // AllocatedSize 可能 > Length（预分配/簇对齐），clamp 到 [0, Length]
        if (allocatedSize > logicalSize) allocatedSize = logicalSize;
        return (double)(logicalSize - allocatedSize) / logicalSize;
    }
}
