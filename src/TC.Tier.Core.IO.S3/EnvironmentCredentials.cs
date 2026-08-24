namespace TC.Tier.Core.IO.S3;

/// <summary>环境变量凭证源（每次调用重读——外部 STS 刷新器改环境变量即生效）。</summary>
public sealed class EnvironmentCredentials : ICredentialProvider
{
    ValueTask<S3Credentials> ICredentialProvider.GetCredentialsAsync(CancellationToken ct)
        => ValueTask.FromResult(S3Credentials.FromEnvironment());
}