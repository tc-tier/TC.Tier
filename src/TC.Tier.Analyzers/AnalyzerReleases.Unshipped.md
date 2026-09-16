## Unreleased

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
TCSG130 | TierCode | Warning | 禁用引用（tier_layer.forbidden_reference）——元数据引用/using 双形态
TCSG131 | TierCode | Warning | 零内部依赖违约（tier_layer.zero_internal_refs）
TCSG132 | TierCode | Warning | 引用白名单越界（tier_layer.allowed_reference）
TCSG133 | TierCode | Warning | 命名空间归属越界（tier_layer.namespace_prefix）
TCSG134 | TierCode | Warning | 裸线程原语（tier_forbidden.pack=bare_threads）
TCSG135 | TierCode | Warning | 热路径分配纪律（tier_forbidden.pack=hotpath_discipline）
TCSG136 | TierCode | Warning | 禁止运行时反射（tier_forbidden.pack=reflection）
TCSG137 | TierCode | Warning | 禁 sync-over-async（tier_forbidden.pack=sync_over_async）
TCSG138 | TierCode | Warning | 禁 fire-and-forget 丢弃（tier_forbidden.pack=fire_and_forget）
TCSG139 | TierCode | Error | 分析器配置非法（未知键/坏语法/依赖缺失——fail-fast）
