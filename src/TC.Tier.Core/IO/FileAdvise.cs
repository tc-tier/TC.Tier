namespace TC.Tier.Core.IO;

/// <summary>访问提示（posix_fadvise 族语义）——告诉内核接下来的访问模式，供预取/回收决策。</summary>
public enum FileAdvise
{
    /// <summary>即将访问——预读入页缓存。</summary>
    WillNeed,

    /// <summary>不再访问——可回收页缓存。</summary>
    DontNeed,

    /// <summary>顺序访问——激进预读。</summary>
    Sequential,

    /// <summary>随机访问——禁用预读。</summary>
    Random,
}