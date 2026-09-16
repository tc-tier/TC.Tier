namespace TC.Tier.Products.Queue;

/// <summary>
/// TierQueue 恢复 hints（P0：组状态/水位全自恢复，无注入项——预留对齐产品 hints 惯例，
/// 形态同 <c>WalRecoveryHints</c>：外部主动注入的已知水位，通常无需提供）。
/// </summary>
public readonly struct TierQueueRecoveryHints;
