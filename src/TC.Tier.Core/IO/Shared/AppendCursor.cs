namespace TC.Tier.Core.IO.Shared;

/// <summary>
/// 文件级追加预留计数盒（第九轮评审定案：游标两职责拆分）。
/// <para>★ <see cref="IFileHandle.Append"/> 的原子预留点归属<b>文件</b>（"下次追加落哪"= 文件末端状态，
///   任何句柄来追加都从同一末端推进）；<see cref="IFileHandle.Position"/>/<see cref="IFileHandle.Seek"/>
///   保留句柄级（会话书签——两个续读者各记各的，文件级反而互踩）。</para>
/// <para>★ fs 内 per-path 字典托管；句柄 open 时解析并缓存盒引用（追加热路径零字典查找）。</para>
/// <para>★ 协调边界：同一 fs 实例内经它打开的全部句柄（含不同打开语义的实例）共享同盒——
///   跨实例并发 Append 无覆写；绕过本层/异实例不参与（与 FileSharing/范围锁同一套边界哲学）。</para>
/// <para>★ 复位钩子：SetLength（权威复位到新长度）/ Delete / Move 源路径（盒摘除，下次解析按新 Length 重建）。
///   显式 Write 越过预留末端增长与 Append 混用是调用方纪律错误（追加式文件只经 Append 增长）。</para>
/// </summary>
internal sealed class AppendCursor
{
    /// <summary>下一个追加落点（Interlocked.Add 原子推进）。</summary>
    public long Value;
}
