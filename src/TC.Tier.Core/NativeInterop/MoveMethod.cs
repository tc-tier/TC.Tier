namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// SetFilePointer 移动方法枚举（Begin/Current/End）。
/// </summary>
internal enum MoveMethod : uint
{
    /// <summary>
    /// 从文件开头开始移动。
    /// </summary>
    Begin = 0,
    /// <summary>
    /// 从当前文件指针位置开始移动。
    /// </summary>
    Current = 1,
    /// <summary>
    /// 从文件末尾开始移动。
    /// </summary>
    End = 2
}