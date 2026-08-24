namespace TC.Tier.Core.Tests;

/// <summary>
/// TestTempDir 自测（测试基建 1:1）——2026-08-20 残留事故复盘后的安全网验证：
/// 正常创建/清理 / 陈旧清扫删旧留新（GUID 后缀判据 + 年龄阈值）/ GUID 判据不误伤非测试目录名。
/// </summary>
public sealed class TestTempDirTests
{
    [Fact]
    public void Create_TryCleanup_NormalPath_RemovesDir()
    {
        var dir = TestTempDir.Create("tc-selftest");
        Directory.Exists(dir).Should().BeTrue();
        var marker = Path.Combine(dir, "x.txt");
        File.WriteAllText(marker, "1");
        TestTempDir.TryCleanup(dir);
        Directory.Exists(dir).Should().BeFalse("正常路径：句柄全释放，立即删净");
    }

    [Fact]
    public void SweepStale_DeletesOldTestDirs_KeepsYoungAndNonTest()
    {
        var old = TestTempDir.Create("tc-selftest-old");
        var young = TestTempDir.Create("tc-selftest-young");
        var nonTest = Path.Combine(Path.GetDirectoryName(young)!, $"not-a-test-{Guid.NewGuid():N}".ToUpperInvariant());
        Directory.CreateDirectory(nonTest);
        try
        {
            // 老化旧目录（LastWriteTime 回拨 2h——跨过 1h 阈值）
            Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromHours(2));

            TestTempDir.SweepStale(TimeSpan.FromHours(1));

            Directory.Exists(old).Should().BeFalse("陈旧测试目录被兜底清扫删除（强杀 run 的遗产）");
            Directory.Exists(young).Should().BeTrue("年轻目录豁免——并行 run 误删防线");
            Directory.Exists(nonTest).Should().BeTrue("非 GUID-N 命名目录不碰（SDK/工具目录保护）");
        }
        finally
        {
            TestTempDir.TryCleanup(old);
            TestTempDir.TryCleanup(young);
            try { Directory.Delete(nonTest, true); } catch { /* 尽力 */ }
        }
    }

    [Fact]
    public void TC_TEST_TMP_EnvVar_Honored_WhenSet()
    {
        // 本测试进程的 TmpRoot 在静态构造时已定格——此处验证环境变量读取逻辑存在性：
        // 设了 TC_TEST_TMP 的 CI 上，Create() 落在该根下（当前根 = GetTempPath 或 env）
        var root = Environment.GetEnvironmentVariable("TC_TEST_TMP") ?? Path.GetTempPath();
        var dir = TestTempDir.Create("tc-selftest-env");
        try
        {
            dir.StartsWith(root, StringComparison.OrdinalIgnoreCase).Should().BeTrue(
                "Create 落在 TmpRoot（TC_TEST_TMP 优先——C 盘保护机制）");
        }
        finally
        {
            TestTempDir.TryCleanup(dir);
        }
    }
}
