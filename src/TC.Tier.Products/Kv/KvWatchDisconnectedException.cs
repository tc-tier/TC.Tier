namespace TC.Tier.Products.Kv;

/// <summary>Watch 订阅断连（慢订阅——etcd 对齐）：订阅者事件通道写满，写路径不被反压，
/// 断连是唯一出路。经枚举流抛出——与干净收束（存储关闭 <c>CompleteAll</c> 后枚举正常结束）
/// 两态可辨：捕获本异常 = 凭已见最大事件地址重订阅续传；枚举正常结束 = 存储已终，不得重订。</summary>
public sealed class KvWatchDisconnectedException()
    : InvalidOperationException("Watch 订阅已断连（慢订阅者事件通道写满）——凭已见最大事件地址重订阅续传");
