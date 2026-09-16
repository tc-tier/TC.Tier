namespace TC.Tier.Products.Kv;

/// <summary>
/// 会话读一致性档位（tierkv-design.md §1——FASTER SessionConditions 档位全集对齐；D3：ReadMyWrites 缺省）。
/// </summary>
public enum KvSessionConditions
{
    /// <summary>
    /// 无保证：读恒走主索引看最新已应用值（他人并发覆盖立即可见）；写立即应用。零写集簿记开销。
    /// </summary>
    None,

    /// <summary>
    /// 读己之写（缺省，D3 裁定）：写立即应用 + 登记本地写集；读优先命中写集——
    /// 他人并发覆盖同 key 不影响本会话的后续读（本会话最后一次写胜出）。
    /// </summary>
    ReadMyWrites,

    /// <summary>
    /// 可串行化：ReadMyWrites 全集 + 同步临界区 EPVS 版本保护（会话写临界区在保护下执行——
    /// 版本过渡不可能跨越进行中的临界区；操作之间过渡可完成——async-first，无线程亲和契约）。
    /// W2 档位 = per-op epoch pin 串行化底线，版本链快照读 W5 深化。
    /// </summary>
    Serializable,
}
