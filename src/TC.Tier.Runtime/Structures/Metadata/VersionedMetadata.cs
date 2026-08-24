namespace TC.Tier.Runtime.Structures.Metadata;

/// <summary>
/// 版本化元数据（VersionedMetadata）——小数据元数据（meta 结构体 + cursor）的版本链存储。
/// 内存工作副本（运行时同步零 IO）+ 磁盘版本链（事务回滚）+ 多版本保留（Abort 零 IO）。
/// <para>★ 生命周期：继承 <see cref="MetadataBase"/>（实现 <see cref="ILifecycle{THints}"/>）——<see cref="MetadataBase"/>.Initialize 同步 void 启动后台恢复后立即返回，调用方用 IsReady/WaitForReady/事件观测等待。详见 src/TC.Tier.Core/docs/lifecycle.md。</para>
/// </summary>
public sealed partial class VersionedMetadata : MetadataBase
{
    private readonly VersionedMetadataSettings _settings;

    /// <summary>
    /// 初始化一个新的<see cref="VersionedMetadata"/>实例。
    /// </summary>
    /// <param name="fileSystem">组合根文件系统（TierFs 构造的 IFileSystem）。</param>
    /// <param name="settings">版本化元数据设置。</param>
    /// <param name="recovery">可选的恢复算法实例。</param>
    /// <param name="metaPolicyFactory">可选的 meta 策略工厂。</param>
    /// <param name="metaTransport">可选的 meta 传输（Transport 模式用）。</param>
    /// <param name="epoch">可选的 LightEpoch 实例。</param>
    /// <param name="persistencePolicy">可选的持久化策略实例。</param>
    public VersionedMetadata(
        IFileSystem fileSystem,
        VersionedMetadataSettings settings,
        IRecovery<MetadataRecoveryHints>? recovery = null,
        MetaPolicyFactory<MetadataMetaHeader, MetadataMetaPayload>? metaPolicyFactory = null,
        IMetaTransport? metaTransport = null,
        LightEpoch? epoch = null,
        IPersistencePolicy? persistencePolicy = null)
        : base(new Codec(), fileSystem, settings, recovery, metaPolicyFactory, metaTransport, epoch, persistencePolicy)
    {
        _settings = settings;
    }

}