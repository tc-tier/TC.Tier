namespace TC.Tier.Core.Shared;

/// <summary>
/// 资源所有权模式——区分"我释放"与"外部管，只跟踪诊断"。
/// <para>对齐 lease 范式（Active vs Referenced）：<see cref="Owned"/> 资源由 ResourceGroup Dispose 释放；
///   <see cref="Referenced"/> 资源是外部注入（调用方自管），进组只为诊断/泄漏可见，Dispose 时跳过。</para>
/// <para>★ 典型场景：注入引擎（<c>_ownsEngine=false</c>）用 <see cref="Referenced"/>——
///   进组泄漏可见，但不被 Dispose（避免双释放，引擎调用方还在用）。</para>
/// </summary>
public enum ResourceOwnership
{
    /// <summary>Owned：ResourceGroup 在 Dispose 时释放此资源。</summary>
    Owned,

    /// <summary>Referenced：外部注入，调用方自管。ResourceGroup 只跟踪诊断（泄漏可见），Dispose 时跳过。</summary>
    Referenced,
}