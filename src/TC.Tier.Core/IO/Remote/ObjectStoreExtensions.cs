namespace TC.Tier.Core.IO.Remote;

/// <summary>
/// 同步便捷包装（低频路径专用——高频路径直用异步族）。
/// <para>★ 内部经 <see cref="SyncAsyncBridge"/>（独立池 + 有界等待）桥接，替代裸
/// <c>GetAwaiter().GetResult()</c>——推进不依赖公共池、等待有界（超时抛 <see cref="TimeoutException"/> 带现场）。
///   详见 docs/sync-async-bridge.md §9 P1。</para>
/// </summary>
public static class ObjectStoreExtensions
{
    // === 共享桥选项（record 不可变，静态复用免每调分配）===
    private static readonly SyncBridgeOptions SPutOpts = new() { Name = "objectstore-put" };
    private static readonly SyncBridgeOptions SGetOpts = new() { Name = "objectstore-get" };
    private static readonly SyncBridgeOptions SHeadOpts = new() { Name = "objector-head" };
    private static readonly SyncBridgeOptions SDeleteOpts = new() { Name = "objectstore-delete" };
    private static readonly SyncBridgeOptions SListOpts = new() { Name = "objectstore-list" };
    // 服务端拷贝大对象可能远超单对象常规时延——预算放宽到 60s
    private static readonly SyncBridgeOptions SCopyOpts = new() { Name = "objectstore-copy", TimeoutMs = 60_000 };

    /// <summary>PutAsync 同步包装（桥接，默认 15s 有界）。</summary>
    public static void Put(this IObjectStore store, string key, ReadOnlyMemory<byte> data,
                           ObjectMetadata? metadata = null, PutCondition? condition = null)
        => SyncAsyncBridge.Run(ct => store.PutAsync(key, data, metadata, condition, ct), SPutOpts);

    /// <summary>GetAsync 同步包装（桥接，默认 15s 有界）。</summary>
    public static int Get(this IObjectStore store, string key, long offset, Memory<byte> destination)
        => SyncAsyncBridge.Run(ct => store.GetAsync(key, offset, destination, ct), SGetOpts);

    /// <summary>HeadAsync 同步包装（桥接，默认 15s 有界）。</summary>
    public static ObjectInfo? Head(this IObjectStore store, string key)
        => SyncAsyncBridge.Run(ct => store.HeadAsync(key, ct), SHeadOpts);

    /// <summary>DeleteAsync 同步包装（桥接，默认 15s 有界）。</summary>
    public static void Delete(this IObjectStore store, string key, DeleteCondition? condition = null)
        => SyncAsyncBridge.Run(ct => store.DeleteAsync(key, condition, ct), SDeleteOpts);

    /// <summary>ListAsync 同步包装（桥接，默认 15s 有界）。</summary>
    public static IReadOnlyList<ObjectEntry> List(this IObjectStore store, string? prefix = null)
        => SyncAsyncBridge.Run(ct => store.ListAsync(prefix, ct), SListOpts);

    /// <summary>CopyAsync 同步包装（桥接，60s 预算——服务端拷贝大对象放宽）。</summary>
    public static void Copy(this IObjectStore store, string sourceKey, string destKey, CopyMetadata? metadata = null)
        => SyncAsyncBridge.Run(ct => store.CopyAsync(sourceKey, destKey, metadata, ct), SCopyOpts);

    /// <summary>CopyMetadataAsync 同步包装（桥接，60s 预算）。</summary>
    public static ObjectMetadata CopyMetadata(this IObjectStore store, string sourceKey, ObjectMetadata? replace = null)
        => SyncAsyncBridge.Run(ct => store.CopyMetadataAsync(sourceKey, replace, ct), SCopyOpts);

    /// <summary>CopyRangeAsync 同步包装（桥接，60s 预算——服务端拷贝放宽）。</summary>
    public static long CopyRange(this IObjectStore store, string sourceKey, string destKey,
                                 long sourceOffset, long length, CopyMetadata? metadata = null)
        => SyncAsyncBridge.Run(ct => store.CopyRangeAsync(sourceKey, destKey, sourceOffset, length, metadata, ct), SCopyOpts);
}
