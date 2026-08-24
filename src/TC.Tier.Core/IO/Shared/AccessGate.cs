namespace TC.Tier.Core.IO.Shared;

/// <summary>
/// 访问包络共享执法件（medium-protocol-and-parity-design §5.2 G2）——fs.Access 是全空间一切访问的
/// 总上包络，四介质同一套规则（能力单调性：子访问永不超出父访问）。
/// <para>★ 执法三平面：命名空间（fs 自身操作按 access 过滤）、数据（句柄构造期校验 ⊑）、映射（同）。</para>
/// <para>★ 越包络在**构造期**抛 <see cref="FileIOException"/>(AccessDenied)——fail-fast，句柄不构造
///   （错在构造期暴露，绝不延迟到第一次读写才炸）。</para>
/// </summary>
internal static class AccessGate
{
    /// <summary>ro（Read）：写族操作拒绝——命名空间写（建/删/移/整理）与维护性变异。</summary>
    public static void RejectWrite(AccessMode access, string op, string? path = null)
    {
        if (access == AccessMode.Read)
            throw Deny(access, op, path, "只读挂载拒绝写操作");
    }

    /// <summary>wo（Write）：读族操作拒绝——Enumerate/Stat（纯摄入形态防误读兜底；Exists 豁免：幂等创建的写路径支撑）。</summary>
    public static void RejectRead(AccessMode access, string op, string? path = null)
    {
        if (access == AccessMode.Write)
            throw Deny(access, op, path, "只写挂载拒绝读操作（纯摄入形态）");
    }

    /// <summary>句柄构造期包络校验：requested ⊑ fs.Access——越包络即抛、句柄不构造。</summary>
    public static void CheckHandleOpen(AccessMode fsAccess, AccessMode requested, string path)
    {
        if (!Within(fsAccess, requested))
            throw Deny(fsAccess, "Open", path,
                $"句柄请求 {requested} 越出挂载包络 {fsAccess}——构造期拒绝，句柄不构造");
    }

    /// <summary>映射构造期包络校验（映射无只写——Write 一并拒绝）。</summary>
    public static void CheckMapOpen(AccessMode fsAccess, AccessMode requested, string path)
    {
        if (requested == AccessMode.Write || !Within(fsAccess, requested))
            throw Deny(fsAccess, "Map", path,
                $"映射请求 {requested} 非法或越出挂络 {fsAccess}（映射无只写）");
    }

    /// <summary>包络关系：ReadWrite ⊇ 一切；否则要求同值（Read→Read / Write→Write）。</summary>
    private static bool Within(AccessMode fs, AccessMode requested)
        => fs == AccessMode.ReadWrite || fs == requested;

    private static FileIOException Deny(AccessMode access, string op, string? path, string reason)
        => new(IOError.AccessDenied, $"access={access}：{reason}（操作 {op}）。", path, $"access-gate:{op}");
}
