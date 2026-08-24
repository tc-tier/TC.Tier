// ReSharper disable InconsistentNaming
namespace TC.Tier.Core.Primitives;

/// <summary>
/// 对齐常量
/// </summary>
public static class AlignmentConst
{
    /// <summary>
    /// 字节数/千字节数
    /// </summary>
    private const int BYTES_PER_KB = 1024;

    /// <summary>
    /// 字节数/兆字节数
    /// </summary>
    private const int BYTES_PER_MB = 1024 * BYTES_PER_KB;

    /// <summary>
    /// 字节数/千兆字节数
    /// </summary>
    private const int BYTES_PER_GB = 1024 * BYTES_PER_MB;

    /// <summary>
    /// 8字节对齐
    /// </summary>
    public const int Alignment16B = 16;
    /// <summary>
    /// 32字节对齐
    /// </summary>
    public const int Alignment32B = 32;
    /// <summary>
    /// 64字节对齐
    /// </summary>
    public const int Alignment64B = 64;
    /// <summary>
    /// 512字节对齐
    /// </summary>
    public const int Alignment512B = 512;

    /// <summary>
    /// 4K字节对齐
    /// </summary>
    public const int Alignment4K = 4 * BYTES_PER_KB;

    /// <summary>
    /// 8K字节对齐
    /// </summary>
    public const int Alignment8K = 8 * BYTES_PER_KB;

    /// <summary>
    /// 16K字节对齐
    /// </summary>
    public const int Alignment16K = 16 * BYTES_PER_KB;

    /// <summary>
    /// 32K字节对齐
    /// </summary>
    public const int Alignment32K = 32 * BYTES_PER_KB;

    /// <summary>
    /// 64K字节对齐
    /// </summary>
    public const int Alignment64K = 64 * BYTES_PER_KB;

    /// <summary>
    /// 128K字节对齐
    /// </summary>
    public const int Alignment128K = 128 * BYTES_PER_KB;

    /// <summary>
    /// 256K字节对齐
    /// </summary>
    public const int Alignment256K = 256 * BYTES_PER_KB;

    /// <summary>
    /// 512K字节对齐
    /// </summary>
    public const int Alignment512K = 512 * BYTES_PER_KB;

    /// <summary>
    /// 1M字节对齐
    /// </summary>
    public const int Alignment1M = 1 * BYTES_PER_MB;

    /// <summary>
    /// 2M字节对齐
    /// </summary>
    public const int Alignment2M = 2 * BYTES_PER_MB;

    /// <summary>
    /// 4M字节对齐
    /// </summary>
    public const int Alignment4M = 4 * BYTES_PER_MB;

    /// <summary>
    /// 8M字节对齐
    /// </summary>
    public const int Alignment8M = 8 * BYTES_PER_MB;

    /// <summary>
    /// 16M字节对齐
    /// </summary>
    public const int Alignment16M = 16 * BYTES_PER_MB;

    /// <summary>
    /// 32M字节对齐
    /// </summary>
    public const int Alignment32M = 32 * BYTES_PER_MB;

    /// <summary>
    /// 64M字节对齐
    /// </summary>
    public const int Alignment64M = 64 * BYTES_PER_MB;

    /// <summary>
    /// 128M字节对齐
    /// </summary>
    public const int Alignment128M = 128 * BYTES_PER_MB;

    /// <summary>
    /// 256M字节对齐
    /// </summary>
    public const int Alignment256M = 256 * BYTES_PER_MB;

    /// <summary>
    /// 512M字节对齐
    /// </summary>
    public const int Alignment512M = 512 * BYTES_PER_MB;

    /// <summary>
    /// 1G字节对齐
    /// </summary>
    public const long Alignment1G = 1L * BYTES_PER_GB;

    /// <summary>
    /// 2G字节对齐
    /// </summary>
    public const long Alignment2G = 2L * BYTES_PER_GB;

    /// <summary>
    /// 4G字节对齐
    /// </summary>
    public const long Alignment4G = 4L * BYTES_PER_GB;

    /// <summary>
    /// 8G字节对齐
    /// </summary>
    public const long Alignment8G = 8L * BYTES_PER_GB;

    /// <summary>
    /// 16G字节对齐
    /// </summary>
    public const long Alignment16G = 16L * BYTES_PER_GB;

    /// <summary>
    /// 32G字节对齐
    /// </summary>
    public const long Alignment32G = 32L * BYTES_PER_GB;

    /// <summary>
    /// 64G字节对齐
    /// </summary>
    public const long Alignment64G = 64L * BYTES_PER_GB;

    /// <summary>
    /// 128G字节对齐
    /// </summary>
    public const long Alignment128G = 128L * BYTES_PER_GB;

    /// <summary>
    /// 256G字节对齐
    /// </summary>
    public const long Alignment256G = 256L * BYTES_PER_GB;

    /// <summary>
    /// 512G字节对齐
    /// </summary>
    public const long Alignment512G = 512L * BYTES_PER_GB;

    /// <summary>
    /// 1T字节对齐
    /// </summary>
    public const long Alignment1T = 1L * BYTES_PER_GB * 1024;

    /// <summary>
    /// 2T字节对齐
    /// </summary>
    public const long Alignment2T = 2L * BYTES_PER_GB * 1024;
}