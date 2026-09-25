using System.Text;
using TC.Tier.Core.Execution;
using TC.Tier.Products.Collections;

namespace TC.Tier.Products.Tests.Collections;

/// <summary>TierHash 测试工厂——mem 介质卷 + Options 配置链（对齐 TierTimeSeriesTestFactory 形态）。</summary>
internal static class TierHashTestFactory
{
    /// <summary>测试缺省：小内存几何（64KB 页 × 128 页 = 8MB）——mem 卷物化 Allocate 跨度（OOM 教训）；
    /// TTL 默认关闭（后台循环零噪音——TTL 场景显式开启 + FakeTimeProvider 直调 RunRetentionAsync）。</summary>
    public static HashOptions DefaultOptions => new()
    {
        PageSize = 64 << 10,
        MemorySize = 8 << 20,
        DomainTtl = null,
    };

    public static Task<TierHash> StartAsync(TestVolume vol,
        Func<HashOptions, HashOptions>? configure = null,
        Action<TierHashBuilder>? build = null)
    {
        var options = configure?.Invoke(DefaultOptions) ?? DefaultOptions;
        var b = new TierHashBuilder(vol.Fs, options);
        // 工厂缺省注入共享调度器（#505——多实例一组引擎线程；Shared 契约 = 调用方不 Dispose）
        b.WithWorkerScheduler(IsolatedTaskScheduler.Shared);
        build?.Invoke(b);
        return b.StartAsync();
    }

    public static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    public static string Str(ReadOnlyMemory<byte> b) => Encoding.UTF8.GetString(b.Span);
}
