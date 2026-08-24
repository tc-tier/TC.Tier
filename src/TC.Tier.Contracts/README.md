# TC.Tier.Contracts

TC.Tier 的**契约层**——逻辑地址、生命周期/恢复接口、存储引擎 IO 契约、结构层接口。零依赖（不引用任何实现），是 TC.Tier 各层的公共类型基础。

## 包含

- **LogicalAddress**（16B 全局统一逻辑地址：SegId + ABA 防护 + Offset）
- **ILifecycle / IRecovery**（生命周期观测与恢复算法契约）
- **IStorageEngine / IStorageInfo**（存储引擎 IO 与信息契约）
- **结构层接口**（IMetaPolicy / ITransactionParticipant / ISequentialReader 等）
- **AsyncOperation 后台操作句柄契约**（事件/取消/进度/等待）

## 依赖

无（零依赖包）。

## 文档

- 完整文档站：https://docs.mytzz.top/
- API 参考：https://docs.mytzz.top/api/
