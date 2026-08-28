namespace TC.Tier.Core.IO.Image;

/// <summary>采集流的逐帧压缩编码（流头 flags 低 2 位——brotli/zstd 预留，设计 §5.1/§10）。</summary>
public enum ImageCompression : byte
{
    /// <summary>不压缩（配合文件→文件零拷贝场景——CopyRange/reflink 快道，设计 §6.3）。</summary>
    None = 0,

    /// <summary>BCL ZLib（逐帧独立——坏一帧不毁全卷）。</summary>
    ZLib = 1,

    /// <summary>zstd 原生（RM-13——NativeInterop/ZstdCodec；逐帧独立）。运行库缺失环境
    /// <see cref="ImageOptions.Validate"/> 显式拒绝（诚实降级——不静默回退）。</summary>
    Zstd = 2,
}