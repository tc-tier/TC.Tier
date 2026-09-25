using System.Text;
using TC.Tier.Core.Execution;
using TC.Tier.Products.Collections;

namespace TC.Tier.Products.Tests.Collections;

/// <summary>TierZSet 测试工厂——mem 介质卷 + Options 配置链（对齐 TierHashTestFactory 形态）。</summary>
internal static class TierZSetTestFactory
{
    /// <summary>测试缺省：小内存几何（64KB 页 × 128 页 = 8MB）；TTL 默认关闭。</summary>
    public static ZSetOptions DefaultOptions => new()
    {
        PageSize = 64 << 10,
        MemorySize = 8 << 20,
        DomainTtl = null,
    };

    public static Task<TierZSet> StartAsync(TestVolume vol,
        Func<ZSetOptions, ZSetOptions>? configure = null,
        Action<TierZSetBuilder>? build = null)
    {
        var options = configure?.Invoke(DefaultOptions) ?? DefaultOptions;
        var b = new TierZSetBuilder(vol.Fs, options);
        // 工厂缺省注入共享调度器（#505——多实例一组引擎线程；Shared 契约 = 调用方不 Dispose）
        b.WithWorkerScheduler(IsolatedTaskScheduler.Shared);
        build?.Invoke(b);
        return b.StartAsync();
    }

    public static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    public static string Str(ReadOnlyMemory<byte> b) => Encoding.UTF8.GetString(b.Span);
}
