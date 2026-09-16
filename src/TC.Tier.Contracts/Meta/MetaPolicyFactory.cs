namespace TC.Tier.Contracts.Meta;

/// <summary>
/// Meta 策略工厂委托（按设置构造）
/// </summary>
/// <typeparam name="TSetting">元数据设置类型。</typeparam>
/// <typeparam name="TMetaHeader">元数据头类型。</typeparam>
/// <typeparam name="TMetaPayload">元数据负载类型。</typeparam>
/// <param name="setting">元数据设置实例（TSetting，引用类型）——工厂据此装配策略，具体取舍由实现定义。</param>
/// <returns>基于该设置装配出的 <see cref="IMetaPolicy{TMetaHeader, TMetaPayload}"/> 策略实例。</returns>
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
/// <param name="policyKind">目标 meta 策略模式（Disabled/Managed/Transport，语义见 <see cref="MetaPolicyKind"/>）。</param>
/// <returns>与 <paramref name="policyKind"/> 对应的 meta 策略实例（如 Disabled = no-op 不持久化）。</returns>
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
/// <param name="transport">传输实例——meta block 的读写通道（介质与放置策略由实现决定，块格式与 CRC 由策略负责，契约见 <see cref="IMetaTransport"/>）。</param>
/// <returns>包装该传输实例的 meta 策略（Transport 模式）。</returns>
public delegate IMetaPolicy<TMetaHeader, TMetaPayload> TransportMetaFactory<in TMetaTransport, TMetaHeader, TMetaPayload>(
    TMetaTransport transport)
    where TMetaTransport : IMetaTransport
    where TMetaHeader : struct
    where TMetaPayload : struct;
