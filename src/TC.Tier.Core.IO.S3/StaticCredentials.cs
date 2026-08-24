namespace TC.Tier.Core.IO.S3;

/// <summary>静态凭证（部署期固定密钥——MinIO 自建/专有云典型）。</summary>
public sealed class StaticCredentials(S3Credentials credentials) : ICredentialProvider
{
    /// <summary>便捷构造：静态 AccessKey/SecretKey。</summary>
    public StaticCredentials(string accessKeyId, string secretAccessKey)
        : this(new S3Credentials(accessKeyId, secretAccessKey, null)) { }

    ValueTask<S3Credentials> ICredentialProvider.GetCredentialsAsync(CancellationToken ct)
        => ValueTask.FromResult(credentials);
}