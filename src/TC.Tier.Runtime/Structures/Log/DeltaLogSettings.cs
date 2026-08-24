namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// <see cref="DeltaLog"/> 专属配置——临时 checkpoint delta 文件场景。
/// <para>无额外专属配置（嵌入式 meta + 临时文件，基类字段够用）。参见 DeltaLog.md §1。</para>
/// </summary>
public sealed class DeltaLogSettings : LogSettings
{
    /// <summary>meta 模式缺省 Transport（嵌入式 meta——DeltaLog 临时 delta 文件场景的历史语义）。
    /// <para>派生构造里赋基类 init 属性（C# 允许派生 ctor 赋 init）；调用方初始化器仍可覆盖。</para></summary>
    public DeltaLogSettings() => MetaPolicyKind = MetaPolicyKind.Transport;

    /// <summary>完整构造——注入主引擎选项（Kind 缺省 Transport 同参数less 版）。</summary>
    public DeltaLogSettings(StorageEngineOptions mainEngine) : base(mainEngine)
        => MetaPolicyKind = MetaPolicyKind.Transport;
}
