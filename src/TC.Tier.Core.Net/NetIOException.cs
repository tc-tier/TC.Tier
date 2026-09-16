using System.IO;

namespace TC.Tier.Core.Net;

/// <summary>
/// Core/Net 唯一的网络 IO 异常类型（Core.IO.FileIOException 同构先例——家族·网络层标准）：
/// 传输面失败（目标未连/握手失败/连接失败/会话 Reset/流拒绝）的统一出口——InProcess/TCP
/// 两介质同构，Socket 层错误不裸抛（<see cref="IOException"/> 是 BCL 底层类型，网络层契约
/// 面一等类型）。
/// <para>★ 铁律：不携带任何协议语义（raft/p2p 词汇）——协议语义由机制面在边界包装补充。</para>
/// <para>★ 派生自 <see cref="IOException"/>（BCL）——消费者可用单一 catch (IOException) 兜底；
///   底层原生错误（SocketException 等）以 <see cref="Exception.InnerException"/> 携带保留诊断。</para>
/// </summary>
public class NetIOException : IOException
{
    /// <summary>创建——成品消息（已含目标/端点上下文）。</summary>
    /// <param name="message">异常消息。</param>
    public NetIOException(string message) : base(message)
    {
    }

    /// <summary>创建——携带底层原生异常（SocketException 等——诊断保留）。</summary>
    /// <param name="message">异常消息。</param>
    /// <param name="inner">底层原生异常。</param>
    public NetIOException(string message, Exception inner) : base(message, inner)
    {
    }
}
