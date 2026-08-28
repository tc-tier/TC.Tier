namespace TC.Tier.Core.IO.Image;

/// <summary>采集/还原选项。</summary>
public sealed record ImageOptions
{
    /// <summary>逐帧压缩编码（默认 ZLib）。</summary>
    public ImageCompression Compression { get; init; } = ImageCompression.ZLib;

    /// <summary>数据帧上限（字节，默认 1 MiB——帧间独立校验/续传粒度）。</summary>
    public int FrameBytes { get; init; } = 1 << 20;

    /// <summary>
    /// 采集前静默源根空间（默认 true）：源置位 <see cref="FileSystemCapabilities.MaintenanceGate"/> 时
    /// 经 <see cref="IFileSystem.EnterMaintenance"/> 包夹采集全程（WriteOperations 档——读继续放行）。
    /// 业务在途收敛仍是消费者契约（设计 §5.5/§8.2）。
    /// </summary>
    public bool QuietSource { get; init; } = true;

    /// <summary>还原端逐条目 CRC 校验（默认 true；关闭换吞吐——慎用）。</summary>
    public bool VerifyChecksums { get; init; } = true;

    /// <summary>
    /// 采集/还原前的选项验证（FrameBytes 范围、zstd 运行库可用性）。
    /// </summary>
    internal void Validate()
    {
        if (FrameBytes is < 512 or > (1 << 26))
            throw new ArgumentException($"FrameBytes 必须在 [512, 64MiB]：{FrameBytes}");
        if (Compression == ImageCompression.Zstd && !NativeInterop.ZstdCodec.IsAvailable)
            throw new ArgumentException("本机 zstd 运行库不可用（libzstd）——Zstd 编码显式拒绝（诚实降级）",
                nameof(Compression));
    }
}