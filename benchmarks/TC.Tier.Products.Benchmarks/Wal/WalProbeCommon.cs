namespace TC.Tier.Products.Benchmarks.Wal;

/// <summary>
/// TierWAL 夹具基准共享件——分位数统计 + Wal 启动 + entry 生成 + 介质简写。
/// <para>★ 介质统一组合根 = BenchVolume（链接 Runtime.Benchmarks，单一真源）；介质简写
///   mem/memory/local/virtual → spec（TierFs 连接字符串）。</para>
/// </summary>
internal static class WalProbeCommon
{
    /// <summary>稳定性矩阵三介质（§4：网络文件系统排除——网络延迟主导，非本地存储稳定性考验）。</summary>
    public static readonly (string Name, string Spec)[] MatrixMedia =
    [
        ("memory", "memory:"),
        ("local", "local"),
        ("virtual", "virtual"),
    ];

    /// <summary>介质简写 → spec。</summary>
    public static string SpecOf(string media) => media switch
    {
        "mem" or "memory" => "memory:",
        "local" => "local",
        "virtual" => "virtual",
        _ => media,
    };

    /// <summary>
    /// IO 模式简写 → hints（FileOpenHints 正交可叠加 flags）：
    /// none=默认缓冲 / wt=WriteThrough 写透 / dio=NoBuffering 直 IO / dio+wt=直 IO+写透。
    /// </summary>
    public static FileOpenHints HintsOf(string name) => name.ToLowerInvariant() switch
    {
        "none" or "buffered" => FileOpenHints.None,
        "wt" or "writethrough" => FileOpenHints.WriteThrough,
        "dio" or "nobuffering" => FileOpenHints.NoBuffering,
        "dio+wt" or "dio-wt" or "nobuffering|writethrough" => FileOpenHints.NoBuffering | FileOpenHints.WriteThrough,
        _ => FileOpenHints.None,
    };

    /// <summary>hints 显示名。</summary>
    public static string HintsName(FileOpenHints h) => h switch
    {
        FileOpenHints.None => "buffered(默认)",
        FileOpenHints.WriteThrough => "WriteThrough",
        FileOpenHints.NoBuffering => "DIO",
        FileOpenHints.NoBuffering | FileOpenHints.WriteThrough => "DIO+WriteThrough",
        _ => h.ToString(),
    };

    /// <summary>hints 维度全组合（磁盘介质 IO 模式矩阵）。</summary>
    public static readonly (FileOpenHints Hints, string Name)[] IoModes =
    [
        (FileOpenHints.None, "buffered"),
        (FileOpenHints.WriteThrough, "WriteThrough"),
        (FileOpenHints.NoBuffering, "DIO"),
        (FileOpenHints.NoBuffering | FileOpenHints.WriteThrough, "DIO+WriteThrough"),
    ];

    /// <summary>组提交形态（三维度禁用——仅显式 CommitAsync 推进水位；对齐测试 ManualCommit）。</summary>
    public static TierWalOptions GroupCommit() => TierWalOptions.Default
        .WithCommitInterval(TimeSpan.FromMilliseconds(-1))
        .WithMaxUnflushedBytes(long.MaxValue)
        .WithMaxUnflushedCount(int.MaxValue);

    /// <summary>单条提交形态（三维度全 0——每次 Append 即触发提交）。</summary>
    public static TierWalOptions SingleForce() => TierWalOptions.Default
        .WithCommitInterval(TimeSpan.FromMilliseconds(-1))
        .WithMaxUnflushedBytes(long.MaxValue)
        .WithMaxUnflushedCount(0);

    /// <summary>定长 entry（低位字节可辨内容，高位零——64B = 真实 raft entry 负载近似）。</summary>
    public static byte[] Entry(int i, int size = 64)
    {
        var b = new byte[size];
        b[0] = (byte)i;
        b[1] = (byte)(i >> 8);
        b[2] = (byte)(i >> 16);
        b[3] = (byte)(i >> 24);
        return b;
    }

    /// <summary>启动 Wal（三段式：Options → Builder → StartAsync）。</summary>
    public static Task<TierWal> StartAsync(IFileSystem fs, TierWalOptions options)
        => options.Builder(fs).StartAsync();

    /// <summary>已排序数组取分位数（ms）。</summary>
    public static (double P50, double P90, double P99, double P999, double Max) Percentiles(double[] sorted)
    {
        static double At(double[] a, double q)
        {
            if (a.Length == 0) return 0;
            var idx = (int)(a.Length * q);
            return a[Math.Min(idx, a.Length - 1)];
        }
        return (At(sorted, 0.50), At(sorted, 0.90), At(sorted, 0.99), At(sorted, 0.999), sorted[^1]);
    }

    /// <summary>分位数字符串（统一输出格式）。</summary>
    public static string Format((double P50, double P90, double P99, double P999, double Max) p, string unit = "ms")
        => $"p50={p.P50:F3}{unit} p90={p.P90:F3}{unit} p99={p.P99:F3}{unit} p99.9={p.P999:F3}{unit} max={p.Max:F3}{unit}";

    /// <summary>探针统一环境头。</summary>
    public static void PrintHeader(string title)
    {
        Console.WriteLine($"=== {title} ===");
        Console.WriteLine($"环境：{Environment.ProcessorCount} 逻辑核，.NET {Environment.Version}，{DateTime.Now:yyyy-MM-dd HH:mm}");
        Console.WriteLine();
    }
}
