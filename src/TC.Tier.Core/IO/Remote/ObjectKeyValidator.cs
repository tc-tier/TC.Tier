using System.Text;

namespace TC.Tier.Core.IO.Remote;

/// <summary>对象键共享校验规则（契约冻结项 §9.5——桥与实现同一实现）。</summary>
public static class ObjectKeyValidator
{
    /// <summary>S3 对象键字节上限（UTF-8）。</summary>
    public const int MaxKeyBytes = 1024;

    /// <summary>校验对象键——非法抛 <see cref="ArgumentException"/>（空键/超长/含 '\0' 或 CR/LF）。</summary>
    public static void Validate(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("对象键不能为空。", nameof(key));
        if (key.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0)
            throw new ArgumentException($"对象键含非法控制字符（\\0/CR/LF——破坏签名 canonical request）: {key}", nameof(key));
        var bytes = Encoding.UTF8.GetByteCount(key);
        if (bytes > MaxKeyBytes)
            throw new ArgumentException($"对象键超长（{bytes} > {MaxKeyBytes} 字节，S3 上限）: {key}", nameof(key));
    }
}
