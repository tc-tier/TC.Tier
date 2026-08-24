namespace TC.Tier.Core.IO;

/// <summary>
/// 文件打开语义——Access × Mode × Sharing × Hints 四要素 + 预分配，一次表达完整打开意图
/// （BCL <c>File.Open(path, FileMode, FileAccess, FileShare)</c> 三要素一一对应，无损表达）。
/// <para>★ 值相等（sealed record，全部成员含 <see cref="PreallocateSize"/> 参与）——供消费者比较/测试便利；
///   池缓存的 key 正确性另由专用 HandleCacheKey 承担（预分配不进池 key，两者各自场景互不依赖）。</para>
/// <para>★ 组合合法性构造时校验，非法抛 <see cref="ArgumentException"/>。</para>
/// </summary>
public sealed record FileOpenOptions
{
    /// <summary>访问权限轴。</summary>
    public AccessMode Access { get; init; }

    /// <summary>存在性处置轴。</summary>
    public FileOpenMode Mode { get; init; }

    /// <summary>共享约束轴。</summary>
    public FileSharing Sharing { get; init; }

    /// <summary>缓存策略提示轴。</summary>
    public FileOpenHints Hints { get; init; }

    /// <summary>预分配大小——&gt;0 时 open 即幂等预分配（两步舞收拢；预分配是创建期动作，不参与池 key）。</summary>
    public long PreallocateSize { get; init; }

    /// <summary>组合合法性校验——非法抛 <see cref="ArgumentException"/>（Append 须写权限；写模式须写权限；Append 与 Truncate/CreateNew 互斥由枚举单值天然保证）。</summary>
    public void Validate(string paramName = "options")
    {
        var needsWrite = Mode is FileOpenMode.OpenOrCreate or FileOpenMode.CreateNew
            or FileOpenMode.Truncate or FileOpenMode.Append;
        if (needsWrite && Access == AccessMode.Read)
            throw new ArgumentException(
                $"FileOpenMode.{Mode} requires write access, got Access={Access}.", paramName);

        if (PreallocateSize < 0)
            throw new ArgumentException($"PreallocateSize must be >= 0, got {PreallocateSize}.", paramName);
    }
}
