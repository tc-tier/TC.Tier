namespace TC.Tier.CodeGen;

/// <summary>
/// 请求体标注——复杂类型参数 = HTTP body JSON 反序列化、CLI 读 stdin 全量字节（#435 命令源生成）。
/// <para>★ 一个命令最多一个 body 参数（多个 = TCSG059）；GET 命令禁 body（TCSG058）。</para>
/// <para>★ AOT 契约：消费方以标准 <c>[JsonSerializable]</c> partial context 声明复杂类型清单，
///   生成的 CLI/HTTP 入口接收 <c>JsonSerializerContext</c>——生成代码 <c>GetTypeInfo(typeof(T))</c>
///   查表非反射（生成器间不互喂，context 由消费方手写一次收口）。</para>
/// </summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class CommandBodyAttribute : Attribute
{
}
