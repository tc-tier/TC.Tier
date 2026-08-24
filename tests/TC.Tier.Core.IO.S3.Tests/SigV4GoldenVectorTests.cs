using TC.Tier.Core.IO.S3;

namespace TC.Tier.Core.IO.S3.Tests;

/// <summary>
/// SigV4 黄金向量——AWS 官方文档示例（签名正确性的唯一可信离线验证）。
/// <para>★ 向量来源：AWS General Reference「Signature Version 4 signing process」完整示例
///   （ListUsers 请求，密钥 wJalrXUtnFEMI/…EXAMPLEKEY，scope 20150830/us-east-1/iam/aws4_request，
///   期望签名 5d672d79…——官方向量，多方独立复现）。</para>
/// <para>★ 在线终验：MinIO 真协议（S3ObjectStoreContractTests）——独立实现的司法鉴定。</para>
/// </summary>
public class SigV4GoldenVectorTests
{
    private const string Secret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private const string Date = "20150830";
    private const string Region = "us-east-1";
    private const string Service = "iam";
    private const string AmzDate = "20150830T123600Z";
    private const string ExpectedSignature = "5d672d79c15b13162d9279b0855cfba6789a8edb4c82c400e06b5924a6f2b5d7";

    /// <summary>官方示例 canonical request（文档逐字节）。</summary>
    private const string ExpectedCanonicalRequest =
        """
        GET
        /
        Action=ListUsers&Version=2010-05-08
        content-type:application/x-www-form-urlencoded; charset=utf-8
        host:iam.amazonaws.com
        x-amz-date:20150830T123600Z

        content-type;host;x-amz-date
        e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
        """;

    [Fact]
    public void CanonicalRequest_MatchesOfficialExample_ByteForByte()
    {
        var headers = new List<(string Name, string Value)>
        {
            ("content-type", "application/x-www-form-urlencoded; charset=utf-8"),
            ("host", "iam.amazonaws.com"),
            ("x-amz-date", AmzDate),
        };
        var canonical = SigV4.BuildCanonicalRequest("GET", "/", "Action=ListUsers&Version=2010-05-08",
            headers, SigV4.EmptyPayloadHash);
        canonical.Should().Be(ExpectedCanonicalRequest.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void EndToEnd_Signature_MatchesOfficialVector()
    {
        var headers = new List<(string Name, string Value)>
        {
            ("content-type", "application/x-www-form-urlencoded; charset=utf-8"),
            ("host", "iam.amazonaws.com"),
            ("x-amz-date", AmzDate),
        };
        var canonical = SigV4.BuildCanonicalRequest("GET", "/", "Action=ListUsers&Version=2010-05-08",
            headers, SigV4.EmptyPayloadHash);
        var canonicalHash = SigV4.Sha256Hex(System.Text.Encoding.ASCII.GetBytes(canonical));
        var stringToSign = SigV4.BuildStringToSign(AmzDate, $"{Date}/{Region}/{Service}/aws4_request", canonicalHash);
        var signingKey = SigV4.DeriveSigningKey(Secret, Date, Region, Service);
        var signature = SigV4.ComputeSignature(signingKey, stringToSign);

        signature.Should().Be(ExpectedSignature);
    }

    // ═══════════════ 编码器单元（RFC 3986 + AWS 大写 hex 规范）═══════════════

    [Theory]
    [InlineData("a", "a")]
    [InlineData("A-Z0-9a-z_.~-", "A-Z0-9a-z_.~-")]            // unreserved 直通
    [InlineData("a b", "a%20b")]                              // 空格 %20（非 '+'——AWS 规范）
    [InlineData("键", "%E9%94%AE")]                            // UTF-8 三字节逐字节编码
    [InlineData("k=v&q", "k%3Dv%26q")]                        // 保留字符编码
    public void UriEncode_EncodesPerRfc3986(string input, string expected)
        => SigV4.UriEncode(input).Should().Be(expected);

    [Fact]
    public void UriEncode_PathMode_KeepsSlash()
        => SigV4.UriEncode("dir/na me/x", encodeSlash: false).Should().Be("dir/na%20me/x");

    [Fact]
    public void UriEncode_QueryMode_EncodesSlash()
        => SigV4.UriEncode("a/b", encodeSlash: true).Should().Be("a%2Fb");

    [Fact]
    public void CanonicalQueryString_SortsByKeyName_Ordinal()
    {
        var query = new List<(string, string)> { ("Version", "2010-05-08"), ("Action", "ListUsers") };
        SigV4.CanonicalQueryString(query).Should().Be("Action=ListUsers&Version=2010-05-08");
    }

    [Fact]
    public void CanonicalQueryString_EncodesKeysAndValues()
    {
        var query = new List<(string, string)> { ("pre fix", "a b"), ("list-type", "2") };
        SigV4.CanonicalQueryString(query).Should().Be("list-type=2&pre%20fix=a%20b");
    }

    [Fact]
    public void EmptyPayloadHash_IsSha256OfEmpty()
        => SigV4.Sha256Hex(ReadOnlySpan<byte>.Empty).Should().Be(SigV4.EmptyPayloadHash);

    [Fact]
    public void AmzDate_FormatIsIsoBasic()
        => SigV4.AmzDate(new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero)).Should().Be("20150830T123600Z");
}
