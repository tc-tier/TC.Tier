namespace TC.Tier.Core.IO.S3;

/// <summary>S3 凭证（静态密钥或 STS 会话 token）。</summary>
/// <param name="AccessKeyId">访问键 ID。</param>
/// <param name="SecretAccessKey">秘密键。</param>
/// <param name="SessionToken">会话 token（STS 临时凭证；静态密钥为 null）。</param>
public readonly record struct S3Credentials(string AccessKeyId, string SecretAccessKey, string? SessionToken)
{
    /// <summary>从 AWS 标准环境变量读取（AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY / AWS_SESSION_TOKEN）。</summary>
    public static S3Credentials FromEnvironment()
    {
        var key = Environment.GetEnvironmentVariable("AWS_ACCESS_KEY_ID");
        var secret = Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY");
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(secret))
            throw new InvalidOperationException(
                "AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY 环境变量未设置。");
        return new S3Credentials(key, secret, Environment.GetEnvironmentVariable("AWS_SESSION_TOKEN"));
    }
}