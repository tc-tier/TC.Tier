namespace TC.Tier.Core.IO.Mem;

/// <summary>
/// 打开登记项（advisory 共享检查）——<b>命名类型防位错</b>：位置元组两个 <c>bool</c> 传反编译不报错、
/// 静默破坏共享检查；命名 record 的值相等性支撑 <c>Remove(entry)</c> 注销路径。
/// </summary>
/// <param name="Sharing">登记句柄声明的共享模式。</param>
/// <param name="NeedsRead">登记句柄是否需要读权限。</param>
/// <param name="NeedsWrite">登记句柄是否需要写权限。</param>
internal readonly record struct OpenRegistryEntry(FileSharing Sharing, bool NeedsRead, bool NeedsWrite);
