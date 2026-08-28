namespace TC.Tier.Core.Tests.NativeInterop;

/// <summary>
/// IS-01：SparseFallback 稀疏标记验证——Windows 降级路径必须产生稀疏文件
/// （非稀疏 SetEndOfFile 扩展 = 即时簇分配，RM-41 同根因；Raw 侧已修 77e09cc0，Disk 侧本测试锁定）。
/// </summary>
public sealed class SparseFallbackTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs) TestTempDir.TryCleanup(dir);
    }

    private string NewPath()
    {
        var dir = TestTempDir.Create("tc-sparse-fallback");
        _dirs.Add(dir);
        return Path.Combine(dir, "fallback.bin");
    }

    /// <summary>
    /// 降级路径：SetLength 前先标记稀疏——逻辑大小成立、物理分配≈0。
    /// Windows：SetSparse 生效（未标记则 NTFS 即时分配满 64MB）；Linux/macOS：SetLength 天然稀疏，同断言成立。
    /// </summary>
    [Fact]
    public void SparseFallback_MarksSparse_NoPhysicalAllocation()
    {
        var path = NewPath();
        using var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

        var result = FileNative.SparseFallback(handle, 64L << 20, null);

        result.Should().Be(PreallocateResult.SparseFallback);
        RandomAccess.GetLength(handle).Should().Be(64L << 20);
        var allocated = FileNative.GetFileAllocatedDiskSize(handle);
        allocated.Should().BeLessThan(1L << 20,
            $"稀疏标记后不应物理分配（实际 {allocated} 字节）");
    }
}
