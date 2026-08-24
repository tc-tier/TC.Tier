
namespace TC.Tier.Core.IO.Shared;

/// <summary>句柄的池挂载能力（internal——四介质句柄 + 测试装饰器实现；池经它驱动归还协议）。
/// <para>★ 句柄装饰器必须转发本协议（池以装饰器为池化对象，挂载/归还/真关闭整体路由内层）。</para></summary>
internal interface IPoolAttachable
{
    /// <summary>当前附件（null = 未挂载，Dispose 走真关闭）。</summary>
    HandlePoolAttachment? PoolAttachment { get; }

    /// <summary>挂载池（Acquire 流程调用；幂等）。返回附件。</summary>
    HandlePoolAttachment AttachPool(FileHandlePool pool);

    /// <summary>池专用真关闭（绕过归还分支——仅池内三出口调用）。</summary>
    void CloseUnderlying();
}