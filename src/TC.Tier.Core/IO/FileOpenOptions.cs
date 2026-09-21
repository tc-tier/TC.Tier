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

    /// <summary>
    /// Unix 文件权限位（创建期生效——#493 密钥 0600 场景）。null = 平台缺省（umask 决定），零行为变化。
    /// <para>★ 生效语义：本次打开<b>实际创建了文件</b>时应用——<see cref="FileOpenMode.CreateNew"/> 恒创建；
    ///   <see cref="FileOpenMode.OpenOrCreate"/>/<see cref="FileOpenMode.Append"/> 仅在文件不存在而由本次创建时
    ///   应用（并发抢先建的落败方不应用——文件归抢先方所有）。<see cref="FileOpenMode.OpenExisting"/>/
    ///   <see cref="FileOpenMode.Truncate"/> 不可能创建——显式传值在 <see cref="Validate"/> 拒绝（模式错配 fail-fast）。</para>
    /// <para>★ 平台语义：仅 Unix（Linux/macOS）Disk 支持（能力位 <see cref="FileSystemCapabilities.UnixPermissions"/>）；
    ///   未置位实现遇到非 null 值抛 <c>IOError.Unsupported</c>——安全权限请求<b>绝不静默忽略</b>。
    ///   应用时点 = 打开返回前（消费者写内容之前）——空文件窗口极短且先于任何敏感数据落盘。</para>
    /// <para>★ 非「文件打开」路径产出的 inode（UDS socket bind 产物等）不走本项——用
    ///   <see cref="IFileSystem.ApplyInodeMode"/>（path-based chmod 任意 inode，#508）。</para>
    /// </summary>
    public UnixFileMode? UnixPermissions { get; init; }

    /// <summary>组合合法性校验——非法抛 <see cref="ArgumentException"/>（Append 须写权限；写模式须写权限；Append 与 Truncate/CreateNew 互斥由枚举单值天然保证；Unix 权限位仅创建形态可指定）。</summary>
    /// <param name="paramName">参数名（默认 "options"）</param>
    public void Validate(string paramName = "options")
    {
        var needsWrite = Mode is FileOpenMode.OpenOrCreate or FileOpenMode.CreateNew
            or FileOpenMode.Truncate or FileOpenMode.Append;
        if (needsWrite && Access == AccessMode.Read)
            throw new ArgumentException(
                $"FileOpenMode.{Mode} requires write access, got Access={Access}.", paramName);

        if (PreallocateSize < 0)
            throw new ArgumentException($"PreallocateSize must be >= 0, got {PreallocateSize}.", paramName);

        if (UnixPermissions is not null
            && Mode is not (FileOpenMode.CreateNew or FileOpenMode.OpenOrCreate or FileOpenMode.Append))
            throw new ArgumentException(
                $"UnixPermissions requires a creation-capable FileOpenMode (CreateNew/OpenOrCreate/Append), got FileOpenMode.{Mode}.",
                paramName);
    }

    /// <summary>权限位介质支持守卫（各 IFileSystem.Open 入口调）——能力位未置位且显式请求权限 = 抛
    /// Unsupported（安全面绝不静默忽略；能力位契约的"无回退族"形态）。</summary>
    internal void EnsurePermissionsSupported(FileSystemCapabilities capabilities, string path, string operationName)
    {
        if (UnixPermissions is not null && (capabilities & FileSystemCapabilities.UnixPermissions) == 0)
            throw new FileIOException(IOError.Unsupported,
                $"Unix 文件权限位在本介质不支持（能力位 UnixPermissions 未置位），显式请求 UnixPermissions={UnixPermissions} 被拒绝——安全权限请求不降级: {path}",
                path, operationName);
    }
}
