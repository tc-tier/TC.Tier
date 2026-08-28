using TC.Tier.Products.Tests.Wal;

namespace TC.Tier.Products.Tests;

/// <summary>TierWAL 测试工厂——默认 mem 介质卷 + Options 配置链。</summary>
internal static class WalTestFactory
{
    public static Task<TierWal> StartAsync(TestVolume vol, Func<TierWalOptions, TierWalOptions>? configure = null)
    {
        var options = configure?.Invoke(TierWalOptions.Default) ?? TierWalOptions.Default;
        return options.Builder(vol.Fs).StartAsync();
    }

    /// <summary>带 builder 注入面（如 WithSnapshotPersistence）的启动。</summary>
    public static Task<TierWal> StartAsync(TestVolume vol, Func<TierWalOptions, TierWalOptions>? configure,
        Action<TierWalBuilder> builder)
    {
        var options = configure?.Invoke(TierWalOptions.Default) ?? TierWalOptions.Default;
        var b = options.Builder(vol.Fs);
        builder(b);
        return b.StartAsync();
    }

    /// <summary>单条提交形态（三维度全 0：条数立即 + 时间禁用——对齐 EntryLogTests 单条强制惯例）。</summary>
    public static TierWalOptions SingleForce(TierWalOptions options) => options
        .WithCommitInterval(TimeSpan.FromMilliseconds(-1))
        .WithMaxUnflushedBytes(long.MaxValue)
        .WithMaxUnflushedCount(0);

    /// <summary>组提交形态（时间禁用——仅显式 CommitAsync 推进水位；测试确定性）。</summary>
    public static TierWalOptions ManualCommit(TierWalOptions options) => options
        .WithCommitInterval(TimeSpan.FromMilliseconds(-1))
        .WithMaxUnflushedBytes(long.MaxValue)
        .WithMaxUnflushedCount(int.MaxValue);

    public static byte[] Entry(int i) => System.Text.Encoding.UTF8.GetBytes($"entry-{i}");
}
