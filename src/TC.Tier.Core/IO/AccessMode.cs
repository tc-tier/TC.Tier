namespace TC.Tier.Core.IO;

/// <summary>
/// 访问三态（medium-protocol-and-parity-design §2.5/§5.2）——一个三值概念，三个平面：
/// 空间（<c>FileSystemOptions.Access</c>）、文件（<c>FileOpenOptions.Access</c>）、映射（Map API）。
/// <para>★ 空间平面即总上包络：句柄/映射构造期校验 <c>⊑ fs.Access</c>，越包络即抛、不构造。</para>
/// <para>★ 本枚举为目标词汇先行落位；P3 完成 <c>AccessMode</c> 改名与 <c>AccessMode</c> 并入。</para>
/// </summary>
public enum AccessMode
{
    /// <summary>只读（spec: <c>access=ro</c>）——fs 平面写拒绝，读全通。</summary>
    Read,

    /// <summary>只写（spec: <c>access=wo</c>）——fs 平面读拒绝，纯摄入形态。</summary>
    Write,

    /// <summary>读写（spec: <c>access=rw</c>，缺省）——全通。</summary>
    ReadWrite,
}
