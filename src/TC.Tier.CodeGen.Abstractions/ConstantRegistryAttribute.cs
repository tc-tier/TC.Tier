namespace TC.Tier.CodeGen;

/// <summary>
/// 标记常量类为常量注册表——ConstantRegistryGenerator 编译期防冲突 + 区间助手生成（spec-12 §10 E3）。
/// <para>★ 重复值检测（恒生效）：类内全部整型 <c>const</c> 字段的<b>数值</b>两两相异——
///   重复即 TCSG040 Error（FrameKind/ProtocolId/Channel/特性位常量防冲突，声明即锁死）。</para>
/// <para>★ 区间助手（可选，<see cref="Zones"/> 声明）：生成 <c>static bool 谓词(value)</c>
///   注入本类（类必须声明 <c>partial</c>）——谓词参数类型 = 类内整型 const 的统一类型
///   （混合整型类型报 TCSG044）。</para>
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ConstantRegistryAttribute : System.Attribute
{
    /// <summary>
    /// 区间断言声明（每条 = <c>"谓词名:区间[,区间]*"</c>；区间 = <c>0xLL-0xHH</c> 含两端或单值 <c>0xVV</c>，
    /// 十六进制 0x 前缀可省；谓词名须为合法 C# 标识符）。例（ProtocolId 五区制，spec-12 §3.5）：
    /// <c>[ConstantRegistry(Zones = new[] { "IsCore:0x00-0x4F", "IsUserRegistrable:0x60-0xAF", "IsForbidden:0x50-0x5F,0xB0-0xFF" })]</c>。
    /// </summary>
    public string[]? Zones { get; set; }
}
