namespace TC.Tier.Products.Tests.Queue;

/// <summary>TierQueue 测试工厂——mem 介质卷 + Options 配置链（对齐 WalTestFactory 形态）。</summary>
internal static class TierQueueTestFactory
{
    /// <summary>测试缺省：小内存几何（64KB 页 × 128 页 = 8MB）——mem 卷物化 Allocate 跨度，
    /// 产品缺省 64MB 也会被并行套件放大（OOM 教训 2026-08-27）。</summary>
    public static TierQueueOptions DefaultOptions => new()
    {
        PageSize = 64 << 10,
        MemorySize = 8 << 20,
    };

    public static Task<TierQueue> StartAsync(TestVolume vol,
        Func<TierQueueOptions, TierQueueOptions>? configure = null,
        Action<TierQueueBuilder>? build = null)
    {
        var options = configure?.Invoke(DefaultOptions) ?? DefaultOptions;
        var b = new TierQueueBuilder(vol.Fs, options);
        build?.Invoke(b);
        return b.StartAsync();
    }

    public static byte[] Msg(int producer, int seq)
    {
        var buf = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf, producer);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(4), seq);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(8), 0x5A5A5A5A);
        return buf;
    }

    public static (int Producer, int Seq) ParseMsg(byte[] payload)
        => (System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4)));
}
