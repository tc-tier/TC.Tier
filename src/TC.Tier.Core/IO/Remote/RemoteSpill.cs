namespace TC.Tier.Core.IO.Remote;

/// <summary>
/// staging 超限 spill 位置（G7 收编——medium-protocol-and-parity-design §5.7）：
/// SpillDirectory（磁盘路径）+ SpillToMemory（布尔）互斥二态 → 单一概念两形态。
/// <para>★ 形态：磁盘目录（<see cref="ToDisk"/>，per-fs 子目录自举）/ 内存私有卷（<see cref="ToMemory"/>，
///   嵌入式无盘部署）；null = 不配置（超限 DiskFull 既有语义）。</para>
/// <para>★ spec 对应：spill=local:///var/tmp / spill=memory:（工厂构建器翻译岗）。</para>
/// </summary>
public sealed record RemoteSpill
{
    /// <summary>磁盘目录（ToDisk 形态非空；ToMemory 形态为 null）。</summary>
    public string? Directory { get; }

    /// <summary>内存私有卷形态判据（ToMemory 工厂的形态）。</summary>
    public bool IsMemory { get; }

    private RemoteSpill(string? directory, bool isMemory)
    {
        Directory = directory;
        IsMemory = isMemory;
    }

    /// <summary>磁盘目录形态（staging 超限落盘）。</summary>
    public static RemoteSpill ToDisk(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("spill 目录不可为空。", nameof(directory));
        return new RemoteSpill(directory, false);
    }

    /// <summary>内存私有卷形态（无盘部署——fs 级 MemoryFileSystem 自举）。</summary>
    public static RemoteSpill ToMemory() => new(null, true);
}
