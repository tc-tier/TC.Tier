namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// 文件系统对扩展属性（xattr/ADS）的支持程度——平台中性的底层探测结果。
/// <para>★ 归属 NativeInterop 层（internal）：描述底层文件系统是否支持把段元信息附着到段文件本身
///   （Linux xattr / Windows NTFS ADS / macOS xattr），由 <see cref="FileNative"/> 探测得出。
///   **不公开**——上层 Device 层消费时据此选择 meta 写入策略（xattr 主路径 vs 异步边车回退）。</para>
/// <para>★ 二态语义（简化版，对比 <see cref="UnbufferedIoSupport"/> 的四态）：</para>
/// <list type="bullet">
/// <item><see cref="Supported"/>：文件系统支持 xattr/ADS，写入 + 读回校验成功——主路径走 per-segment
///   扩展属性（与段文件同 inode，天然一致，段级原子）。</item>
/// <item><see cref="Unsupported"/>：文件系统不支持（FAT32、某些网络盘、容器 overlayfs 限制），
///   或探测时异常——回退异步边车后台写（device 级集中文件，崩溃容忍）。</item>
/// <item><see cref="NotProbed"/>：尚未探测（理论不应出现，Initialize 必探测一次）。</item>
/// </list>
/// <para>★ 探测策略与 <see cref="UnbufferedIoSupport"/> 一致：try/catch 包裹，失败保守降级（安全侧
///   倾斜，不支持时走异步边车更稳，避免主路径写 xattr 反复失败拖慢）。</para>
/// </summary>
public enum FileMetaSupport
{
    /// <summary>尚未探测（理论不应出现，Initialize 必探测一次并缓存结果）。</summary>
    NotProbed,

    /// <summary>文件系统支持 xattr/ADS——主路径走 per-segment 扩展属性（段前写 + 段满写真实偏移）。</summary>
    Supported,

    /// <summary>文件系统不支持——回退异步边车后台写（device 级集中文件，自适应频率）。</summary>
    Unsupported,
}
