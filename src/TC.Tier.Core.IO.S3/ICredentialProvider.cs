namespace TC.Tier.Core.IO.S3;

/// <summary>
/// 凭证提供者抽象——静态/环境变量/配置文件/STS 多源（评审低 3）。每次签名前取当前凭证：
/// STS 过期 token 中途刷新 = 换一个返回新值的 provider 即达，客户端零改动。
/// </summary>
public interface ICredentialProvider
{
    /// <summary>取当前有效凭证（可异步——STS/远程凭证源）。</summary>
    ValueTask<S3Credentials> GetCredentialsAsync(CancellationToken ct = default);
}