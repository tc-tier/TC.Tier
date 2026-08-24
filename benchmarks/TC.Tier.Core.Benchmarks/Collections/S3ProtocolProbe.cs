using TC.Tier.Core.IO;

namespace TC.Tier.Core.Benchmarks.Collections;

/// <summary>
/// ★ 外部协议注册桥验证探针（2026-08-24 用户裁定——源生成器解决，零反射）：
/// 纯 spec 消费者（不触碰任何 S3 类型）——生成器在消费方编译里生成 TierFsExternalProtocolRegistration
/// （引用程序集符号扫描——编译期，NativeAOT 安全）→ 消费方程序集加载即注册。
/// 用法：--s3-protocol-probe
/// 返回码：0 = 生成桥注册生效；3 = 注册缺失
/// </summary>
internal static class S3ProtocolProbe
{
    public static int Run(string[] args)
    {
        Console.WriteLine("S3ProtocolProbe：纯 spec 消费者（不触碰 S3 类型）——生成桥应自动注册");
        Environment.SetEnvironmentVariable("TC_TIER_S3_TEST", "AKID:SECRET");
        try
        {
            _ = TierFs.Open("network:///s3/127.0.0.1:9/bkt/pfx?tls=0&cred=env:TC_TIER_S3_TEST");
            Console.WriteLine("协议已注册（触碰加载生效）——构建进入后续阶段");
            return 0;
        }
        catch (FileIOException fioe)
        {
            if (fioe.Message.Contains("未注册"))
            {
                Console.WriteLine($"✗ 注册缺失：{fioe.Message}");
                return 3;
            }
            Console.WriteLine($"✓ 协议已注册——后续错误与协议无关：{fioe.Message[..Math.Min(80, fioe.Message.Length)]}");
            return 0;
        }
    }
}
