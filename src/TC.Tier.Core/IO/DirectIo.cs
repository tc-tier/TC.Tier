namespace TC.Tier.Core.IO;

/// <summary>
/// DIO（无缓冲直接 IO）的缓冲对齐地板——预分配对齐内存（页池/帧池）与句柄校验共用的单真相。
/// <para>★ 与 <see cref="Disk.DiskFileHandle"/> 的 <c>_requiredAlignment</c> 同式：
/// Windows = max(扇区, <see cref="Environment.SystemPageSize"/>)（Win DIO 缓冲地址须系统页对齐）；
/// Linux = 扇区（O_DIRECT 逻辑块粒度）。</para>
/// <para>★ 消费纪律：凡打算作为 DIO 写/读缓冲的长命内存（Ring 页池/Log 帧池），
/// 租用对齐必须取本地板而非卷扇区——按扇区租用令 Windows 下缓冲地址 7/8 概率失配
/// （卷扇区 512 时），DIO flush 随机抛 <see cref="FileIOException"/> 对齐错。</para>
/// </summary>
public static class DirectIo
{
    /// <summary>DIO 缓冲对齐地板（给定卷扇区大小）。非对齐模式句柄不校验，本值为安全上界。</summary>
    public static int BufferAlignmentFloor(int volumeSectorSize)
        => OperatingSystem.IsWindows()
            ? Math.Max(volumeSectorSize, Environment.SystemPageSize)
            : Math.Max(volumeSectorSize, 1);
}
