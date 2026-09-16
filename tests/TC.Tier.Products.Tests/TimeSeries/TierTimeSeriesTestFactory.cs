namespace TC.Tier.Products.Tests.TimeSeries;

/// <summary>TierTimeSeries 测试工厂——mem 介质卷 + Options 配置链（对齐 TierQueueTestFactory 形态）。</summary>
internal static class TierTimeSeriesTestFactory
{
    /// <summary>测试缺省：小内存几何（64KB 页 × 128 页 = 8MB）——mem 卷物化 Allocate 跨度（OOM 教训）；
    /// retention 默认关闭（后台循环零噪音——TTL/字节上限场景显式开启）。</summary>
    public static TimeSeriesOptions DefaultOptions => new()
    {
        PageSize = 64 << 10,
        MemorySize = 8 << 20,
        RetentionTime = null,
    };

    public static Task<TierTimeSeries> StartAsync(TestVolume vol,
        Func<TimeSeriesOptions, TimeSeriesOptions>? configure = null,
        Action<TierTimeSeriesBuilder>? build = null)
    {
        var options = configure?.Invoke(DefaultOptions) ?? DefaultOptions;
        var b = new TierTimeSeriesBuilder(vol.Fs, options);
        build?.Invoke(b);
        return b.StartAsync();
    }

    /// <summary>样本值：8B double LE（Rollup 值编码契约）+ 校验尾。</summary>
    public static byte[] Val(double v)
    {
        var buf = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(buf, v);
        return buf;
    }

    public static double ParseVal(byte[] payload)
        => System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(payload);
}
