using TC.Tier.Runtime.Structures.Log.Contracts;

namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// DeltaLog — KV 增量 checkpoint delta 流实现类。
/// <para>codec 绑定 DeltaLogCodec（不可替换）。其余参数全部可注入。</para>
/// </summary>
public sealed partial class DeltaLog : LogBase
{
    private readonly DeltaLogSettings _settings;

    /// <summary>
    /// 构造 DeltaLog 实例。
    /// </summary>
    /// <param name="fileSystem">文件系统接口。</param>
    /// <param name="settings">DeltaLog 配置项。</param>
    /// <param name="recovery">日志恢复接口。</param>
    /// <param name="cursorFactory">日志游标工厂方法。</param>
    /// <param name="metaPolicyFactory">元数据策略工厂方法。</param>
    /// <param name="metaTransport">元数据传输。</param>
    public DeltaLog(IFileSystem fileSystem,DeltaLogSettings settings,
        IRecovery<LogRecoveryHints>? recovery = null,
        LogCursorFactory<ILogCursor>? cursorFactory = null,
        MetaPolicyFactory<LogMetaHeader, LogMetaPayload>? metaPolicyFactory = null,
        IMetaTransport? metaTransport = null)
        : base(new Codec(),fileSystem, settings, recovery, cursorFactory, metaPolicyFactory, metaTransport)
    {
        _settings = settings;
    }

}