using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Tests;

/// <summary>
/// 测试临时目录管理 —— 健壮创建/清理 + 双端兜底清扫（2026-08-20 残留事故复盘后补全）。
/// <para>★ 事故：注释承诺的 AssemblyCleanupFixture 兜底<b>从未实现</b>；删除重试仅 3×50ms 且静默放弃；
///   TierFsTests 等直接拼 Path.GetTempPath() 完全绕过本类（零清理零重定向）——三层叠加在 C 盘累积
///   29G/1691 目录（强杀 run 不走 Dispose 整批残留 + 磁盘满连锁）。</para>
/// <para>★ 修复：(1) 删除重试 10×200ms，失败<b>打日志</b>（残留不再隐形）；(2) ModuleInitializer 启动
///  清扫陈旧残留（被强杀 run 的 Dispose 不执行——GUID 后缀判据 + 1h 年龄阈值避开并行 run 误删）；
///   (3) ProcessExit 退出清扫本进程漏网目录（Dispose 时刻句柄未释放的，退出时必然已释放）。</para>
/// </summary>
internal static class TestTempDir
{
    /// <summary>
    /// 测试临时目录根。优先用环境变量 <c>TC_TEST_TMP</c>（避免占用系统盘 C:），回退 <see cref="Path.GetTempPath"/>。
    /// </summary>
    private static readonly string TmpRoot = Environment.GetEnvironmentVariable("TC_TEST_TMP")
        ?? Path.GetTempPath();

    /// <summary>本进程创建的目录登记（退出清扫用——Dispose 漏网目录在进程退出时句柄必然已释放）。</summary>
    private static readonly ConcurrentBag<string> CreatedDirs = new();

    /// <summary>陈旧残留判定年龄（并行 run 的活跃目录是年轻的——阈值内不误删）。</summary>
    private static readonly TimeSpan StaleAge = TimeSpan.FromHours(1);

    /// <summary>创建唯一的临时目录（GUID 防冲突），返回完整路径。</summary>
    public static string Create(string prefix = "tc-test")
    {
        var dir = Path.Combine(TmpRoot, $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        CreatedDirs.Add(dir);
        return dir;
    }

    /// <summary>
    /// 健壮删除临时目录：先 GC 回收延迟句柄，再重试删除（10×200ms——后台线程/终结器释放句柄有延迟）。
    /// 失败打日志（残留可见），由启动/退出双端兜底清扫收口。
    /// </summary>
    public static void TryCleanup(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        // GC 回收延迟释放的文件句柄（FASTER device finalizer / IOCP pending 回调）
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Exception? last = null;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;   // 文件句柄尚未释放，等待后重试
                Thread.Sleep(200);
            }
            catch { return; }   // 其它异常（目录已不存在等），放弃
        }
        Console.Error.WriteLine($"[TestTempDir] 清理失败（待兜底清扫）: {dir} — {last?.Message}");
    }

    // ═══════════════ 兜底清扫（注释承诺过的 AssemblyCleanupFixture——本类即其实体）═══════════════

    /// <summary>启动清扫（陈旧残留——被强杀 run 的遗产）+ 退出清扫（本进程漏网）。</summary>
    [ModuleInitializer]
    internal static void InitCleanupHooks()
    {
        try
        {
            SweepStale(StaleAge);
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => SweepOwn();
        }
        catch
        {
            // 清扫失败不阻断测试启动（TmpRoot 不可达等环境异常）
        }
    }

    /// <summary>清扫陈旧残留：GUID 后缀判据（全部测试目录形态 = prefix-<32hex>，不误伤
    /// SDK/其它工具目录）+ 年龄阈值（并行 run 的活跃目录年轻，不在阈值内）。</summary>
    internal static void SweepStale(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        foreach (var dir in Directory.EnumerateDirectories(TmpRoot))
        {
            try
            {
                var name = Path.GetFileName(dir);
                if (!IsTestDirName(name)) continue;
                if (Directory.GetLastWriteTimeUtc(dir) >= cutoff) continue;
                Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 句柄未释放/并行 run——下次启动再扫
            }
        }
    }

    private static bool IsTestDirName(string name)
    {
        // prefix-<32 位十六进制 GUID N 格式>（全部测试目录的命名形态——SDK/其它工具目录不匹配）
        var dash = name.LastIndexOf('-');
        if (dash <= 0 || name.Length - dash - 1 != 32) return false;
        foreach (var c in name.AsSpan(dash + 1))
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    private static void SweepOwn()
    {
        foreach (var dir in CreatedDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // 尽力——启动清扫兜底
            }
        }
    }
}
