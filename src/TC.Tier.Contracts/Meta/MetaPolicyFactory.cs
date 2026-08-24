namespace TC.Tier.Contracts.Meta;

/// <summary>
/// Meta 策略工厂委托（按设置构造）
/// </summary>
/// <typeparam name="TSetting">元数据设置类型。</typeparam>
/// <typeparam name="TMetaHeader">元数据头类型。</typeparam>
/// <typeparam name="TMetaPayload">元数据负载类型。</typeparam>
public delegate IMetaPolicy<TMetaHeader, TMetaPayload> MetaPolicyFactory<in TSetting, TMetaHeader, TMetaPayload>(
    TSetting setting)
    where TSetting : class
    where TMetaHeader : struct
    where TMetaPayload : struct;

/// <summary>
/// Meta 策略工厂委托（按模式构造）
/// </summary>
/// <typeparam name="TMetaHeader">元数据头类型。</typeparam>
/// <typeparam name="TMetaPayload">元数据负载类型。</typeparam>
public delegate IMetaPolicy<TMetaHeader, TMetaPayload> MetaPolicyFactory<TMetaHeader, TMetaPayload>(
    MetaPolicyKind policyKind)
    where TMetaHeader : struct
    where TMetaPayload : struct;

/// <summary>
/// Meta 策略工厂委托（Transport 模式用——注入传输实例）
/// </summary>
/// <typeparam name="TMetaTransport">传输类型。</typeparam>
/// <typeparam name="TMetaHeader">元数据头类型。</typeparam>
/// <typeparam name="TMetaPayload">元数据负载类型。</typeparam>
public delegate IMetaPolicy<TMetaHeader, TMetaPayload> TransportMetaFactory<in TMetaTransport, TMetaHeader, TMetaPayload>(
    TMetaTransport transport)
    where TMetaTransport : IMetaTransport
    where TMetaHeader : struct
    where TMetaPayload : struct;
