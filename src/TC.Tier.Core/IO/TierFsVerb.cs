namespace TC.Tier.Core.IO;

/// <summary>工厂动词（medium-protocol-and-parity-design §2.3）——New = 创建空镜像并打开（已存在即抛）；Open = 打开既有（不存在即抛）；OpenOrCreate = 懒初始化糖（bind-any——两态显式表达）。</summary>
public enum TierFsVerb
{
    /// <summary>创建空镜像并打开——已存在即抛 AlreadyExists（防误格式化/误覆盖）。</summary>
    New,

    /// <summary>打开既有镜像——不存在即抛 NotFound。</summary>
    Open,

    /// <summary>懒初始化糖（bind-any 终态）：不存在/未格式化则建，存在则开——显式表达"我接受两种状态"。</summary>
    OpenOrCreate,
}