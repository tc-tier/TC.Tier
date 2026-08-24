namespace TC.Tier.Core.IO;

/// <summary>字节范围锁模式（fd 级协调原语，与卷锁分层）。</summary>
public enum FileLockMode
{
    /// <summary>共享锁——多持有者并存，与排他锁互斥。</summary>
    Shared,

    /// <summary>排他锁——独占，与一切锁互斥。</summary>
    Exclusive,
}