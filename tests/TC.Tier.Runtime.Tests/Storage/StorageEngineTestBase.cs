namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// StorageEngine 测试基类——数据模式生成工具（卷管理见 <see cref="TestVolume"/>）。
/// </summary>
public abstract class StorageEngineTestBase
{
    /// <summary>生成可识别的数据模式（seed + i）——便于读写比对定位差异字节。</summary>
    protected static byte[] MakePattern(int length, byte seed = 0xAB)
    {
        var buf = new byte[length];
        for (int i = 0; i < length; i++) buf[i] = (byte)((seed + i) & 0xFF);
        return buf;
    }

    /// <summary>生成递增填充的 byte[]（0,1,2,...255,0,1,...）——直观比对。</summary>
    protected static byte[] MakeSequential(int length)
    {
        var buf = new byte[length];
        for (int i = 0; i < length; i++) buf[i] = (byte)(i & 0xFF);
        return buf;
    }
}
