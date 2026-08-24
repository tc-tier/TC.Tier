namespace TC.Tier.Contracts.Common;

/// <summary>异步操作状态机（<see cref="IAsyncOperation.Status"/>）。
/// <para>★ 无 <c>Pending</c> 态——可见性原则（Core docs/sync-async-bridge.md §7）：发起线程在句柄对外可见前
///   同步置 <see cref="Running"/>，"已受理"不可观测为 Pending 之外的状态。</para>
/// <para>★ 从 Core.Primitives 迁入 Contracts——接口消费面补全（IAsyncOperation.Status）。</para></summary>
public enum AsyncOperationStatus
{
    /// <summary>已受理并在途（构造即此态——发起线程返回前同步置位）。</summary>
    Running = 0,

    /// <summary>成功终态。</summary>
    Succeeded = 1,

    /// <summary>失败终态（异常存 <see cref="IAsyncOperation.Exception"/>）。</summary>
    Failed = 2,

    /// <summary>取消终态（OCE 存 <see cref="IAsyncOperation.Exception"/>）。</summary>
    Canceled = 3,
}
