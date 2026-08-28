namespace TC.Tier.Core.IO;

/// <summary>
/// 介质本性（medium-protocol-and-parity-design §1.5/§2.1）——spec 协议头的值域，四类封闭（平权完备族）。
/// <para>★ 三层一套词汇：文档标准术语 = spec 协议头 = <c>VolumeInfo.Nature</c> 观测值。</para>
/// <para>★ 二级分类在 path 首段（virtual 的 <c>dev</c> 载体 / network 的协议名——开放注册），
///   本枚举只承载封闭的顶层；扩展永不进顶层。</para>
/// <para>★ 遗留 <c>FileSystemType</c> 枚举的分类学由此继承（G12——形态退场，概念留任）。</para>
/// </summary>
public enum StorageNature
{
    /// <summary>本地文件系统（<c>local://</c>）——OS 目录树，可视化、生态全兼容。</summary>
    Local,

    /// <summary>内存文件系统（<c>memory:</c>）——进程内私有卷，高性能运行时。</summary>
    Memory,

    /// <summary>虚拟文件系统（<c>virtual://</c>）——盘上自持 FS（.tier 文件 / 块设备），存档即活卷。</summary>
    Virtual,

    /// <summary>网络文件系统（<c>network:///协议/</c>…）——对象存储（协议 = 首段，s3 今天，cos/oss 开放）。</summary>
    Network,
}
