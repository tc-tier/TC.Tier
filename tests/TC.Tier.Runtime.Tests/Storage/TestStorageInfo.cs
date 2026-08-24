using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 测试用 <see cref="IStorageInfo"/> 简单实现——多段模式磁盘配置。
/// <para>供 EngineMeta/Compactor 单元测试注入，无需造完整引擎。其余成员用默认值。</para>
/// </summary>
internal sealed class TestStorageInfo : IStorageInfo
{
    public string EngineName { get; }
    public long SegmentGrowthLimit { get; } = 1 << 20;
    public uint SectorSize { get; } = 512;
    public bool EnableSegmentation { get; } = true;
    public bool PreallocateFile { get; }

    internal TestStorageInfo(string dataPath, string deviceName)
    {
        EngineName = deviceName;
    }

    public string SegmentFileName(int segId) => $"{EngineName}/{EngineName}.{segId}";
}
