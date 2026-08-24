namespace TC.Tier.Core.IO;

/// <summary>
/// 维护门闩的作用范围（<see cref="IFileSystem.EnterMaintenance"/> 的 scope 参数）。
/// </summary>
public enum MaintenanceScope
{
    /// <summary>
    /// 仅拒绝写操作——读继续放行（采集/备份期的常用档：源卷静默写、读不受影响）。
    /// </summary>
    WriteOperations,

    /// <summary>
    /// 拒绝全部操作（读写全拒）——完全隔离档（如还原目标卷、格式化窗口）。
    /// </summary>
    AllOperations,
}
