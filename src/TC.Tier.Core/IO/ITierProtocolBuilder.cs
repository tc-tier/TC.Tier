namespace TC.Tier.Core.IO;

/// <summary>
/// network 协议构建器（二级注册表的开放轴）——第三方 <c>IObjectStore</c> 实现注册
/// <c>TierFs.RegisterProtocol("cos", builder)</c> 即接入完整远程栈（扩展永不进 scheme 顶层）。
/// </summary>
public interface ITierProtocolBuilder
{
    /// <summary>按解析后的 spec + 用户 options 构建网络文件系统（协议私有的端点/凭证/寻址映射在此完成；
    /// 优先级：spec 显式胜出 → options 同名值 → 类型缺省——合流规则见 TierFs.New(string,FileSystemOptions,ILogger?)）。</summary>
    IFileSystem Build(TierSpec spec, FileSystemOptions? options, TierFsVerb verb, ILogger? logger);
}