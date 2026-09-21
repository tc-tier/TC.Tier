using TC.Tier.Core.Execution;
using TC.Tier.Runtime.Tests;

namespace TC.Tier.Products.Tests.Blob;

/// <summary>TierBlob 测试工厂——mem 介质卷 + Options 配置链（对齐 TierTimeSeriesTestFactory 形态）。</summary>
internal static class TierBlobTestFactory
{
    /// <summary>测试缺省：小段几何（表引擎 1MB / 数据引擎 8MB 跨段扩容路径覆盖）+ 容量不限。</summary>
    public static TierBlobOptions DefaultOptions => new()
    {
        BlobName = "tc",
        SegmentGrowthLimit = 8L << 20,
        TableSegmentGrowthLimit = 1L << 20,
        SessionBufferSize = 64 << 10,
        TableSessionBufferSize = 8 << 10,
    };

    /// <summary>工厂缺省注入共享调度器（#505——configure with 覆盖 = 测试主权；Shared 契约 = 不 Dispose）。</summary>
    private static TierBlobOptions BaseOptions => DefaultOptions with { WorkerScheduler = IsolatedTaskScheduler.Shared };

    public static Task<TierBlob> StartAsync(TestVolume vol,
        Func<TierBlobOptions, TierBlobOptions>? configure = null,
        Action<TierBlobBuilder>? build = null)
    {
        var options = configure?.Invoke(BaseOptions) ?? BaseOptions;
        var b = new TierBlobBuilder(vol.Fs, options);
        build?.Invoke(b);
        return b.StartAsync();
    }

    /// <summary>合成对象数据：fill 字节填充。</summary>
    public static byte[] MakeData(int size, byte fill)
    {
        var b = new byte[size];
        Array.Fill(b, fill);
        return b;
    }

    /// <summary>对象帧总长（扇区整倍数——BlobObjectTable.FrameLengthOf 同式，测试断言用）。</summary>
    public static long FrameLengthOf(int userLength, int sectorSize)
        => (BlobObjectTable.FrameHeaderSize + userLength + BlobObjectTable.FrameFooterSize
            + sectorSize - 1) / sectorSize * sectorSize;
}
