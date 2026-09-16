namespace TC.Tier.Core.Net.Tests.Fixtures;

/// <summary>
/// 冻结取证保持钩子（选举冻结长尾取证判例 2026-09-03——销案记录 §5 铺设路径的落地件）：
/// 测试失败快照抛出前，若 <c>TC_NET_FREEZE_HOLD</c> 环境变量非空，按其秒数有界保持
/// （冻结集群的 parked 任务/锁现场存活，供 dotnet-stack 活体抓栈）——默认零开销直通，
/// 常规跑不设置即无行为差异；仅取证跑显式设置。
/// </summary>
internal static class FreezeForensics
{
    /// <summary>失败现场保持（秒）——环境变量未设置/非法 = 0（直通）。</summary>
    public static int HoldSeconds
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("TC_NET_FREEZE_HOLD");
            return int.TryParse(raw, out var s) && s > 0 ? s : 0;
        }
    }

    /// <summary>有界保持（失败快照抛出前调用——冻结现场存活窗口）。
    /// ★ Thread.Sleep 而非 Task.Delay：保持链不得依赖 TimerQueue（2026-09-03 丢表项判例）。</summary>
    public static void Hold()
    {
        var seconds = HoldSeconds;
        if (seconds == 0) return;
        Console.WriteLine($"[FreezeForensics] 冻结现场保持 {seconds}s（TC_NET_FREEZE_HOLD）——此刻活体抓栈：dotnet-stack report -p <testhost>");
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
    }
}
