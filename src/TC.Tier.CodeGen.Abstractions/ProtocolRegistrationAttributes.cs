namespace TC.Tier.CodeGen;

/// <summary>
/// 介质 options 注册标注（spec-typed-frontend-and-generator-design §3）——贴在 options 子类上，
/// 重载生成器据此发射 <c>TierFs.New/Open(spec, TOptions, logger)</c> 类型化重载族。
/// <para>★ string 形态（硬约束：Core 引用本程序集，标注不可反引 Core 的 StorageNature 枚举）——
///   生成器对未知值<b>编译期报错</b>（拼错即炸，fail-fast）。</para>
/// </summary>
/// <param name="nature">
/// 本性四类（小写协议头）："local" / "memory" / "virtual" / "network"——生成器校验已知集。
/// </param>
[System.AttributeUsage(System.AttributeTargets.Class, Inherited = false)]
public sealed class MediumOptionsAttribute(string nature) : System.Attribute
{
    /// <summary>本性（协议头字符串——生成器校验已知集）。</summary>
    public string Nature { get; } = nature;

    /// <summary>动词集（缺省 "New,Open"；virtual 两簇 options 用 "New" / "Open" 区分）。</summary>
    public string Verbs { get; init; } = "New,Open";

    /// <summary>生成重载的参数类型显示名（诊断友好——缺省取类名）。</summary>
    public string? OptionsTypeName { get; init; }
}

/// <summary>
/// network 协议注册标注（§3）——贴在协议构建器上，生成器发射 ModuleInitializer 注册代码
/// （替代手写 TierFsS3ModuleInitializer——形态相同但自维护；加协议 = 实现接口 + 标注）。
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Class, Inherited = false)]
public sealed class NetworkProtocolAttribute(string protocol) : System.Attribute
{
    /// <summary>协议注册键（与 spec path 首段一致——如 "s3"）。</summary>
    public string Protocol { get; } = protocol;
}

/// <summary>
/// ★ 程序集级协议导出标记（2026-08-24 用户裁定——Tier 通用协议导出机制）：
/// 协议程序集（TC.Tier.Core.IO.S3 或外部第三方）在<b>程序集</b>上标注本特性 = 声明
/// "本程序集导出网络协议"——TierFsGenerator 在<b>消费方编译</b>里只深入<b>带此标记</b>的
/// 引用程序集找 <see cref="NetworkProtocolAttribute"/> 类型，生成注册桥。
/// <para>★ 关注点分离：类型级 <see cref="NetworkProtocolAttribute"/> 标注协议<b>身份</b>
///   （协议键 + 实现类——协议程序集自身编译时本地 ModuleInitializer 注册）；
///   程序集级本特性声明导出<b>意图</b>（供外部桥精确扫描——零误扫非协议程序集）。</para>
/// <para>★ 无参标记——语义即"本程序集含 [NetworkProtocol] 导出"。第三方协议程序集加一行
///   <c>[assembly: TierProtocolExported]</c> 即被消费方自动发现（零反射，NativeAOT 安全）。</para>
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Assembly, Inherited = false)]
public sealed class TierProtocolExportedAttribute : System.Attribute
{
}

/// <summary>
/// spec 参数标注（§3）——贴在 TierSpec 的参数属性上，DSL 生成器据此派生 builder 方法集
/// （方法集 = 参数表——单一事实源的机械执行；参数 × 介质归属与可重复性在此声明）。
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Property, Inherited = false)]
public sealed class SpecParamAttribute : System.Attribute
{
    /// <summary>参数 × 介质归属（"all" 缺省 / "network" / "virtual"）。</summary>
    public string Media { get; init; } = "all";

    /// <summary>可重复参数（如 virtual 的 member）。</summary>
    public bool Repeatable { get; init; }
}
