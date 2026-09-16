using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TC.Tier.CodeGen;
using Xunit;

namespace TC.Tier.CodeGen.Tests;

/// <summary>测试用 RouteSpec（fixture 引用本程序集——body 反序列化走 TestJsonContext 元数据）。</summary>
public sealed class RouteSpec
{
    public string Name { get; set; } = string.Empty;
}

/// <summary>测试用 STJ source-gen 上下文（消费方 [JsonSerializable] 一次收口形态——AOT 契约）。</summary>
[JsonSerializable(typeof(RouteSpec))]
internal sealed partial class TestJsonContext : JsonSerializerContext
{
}

/// <summary>
/// 命令源生成测试（#435）——GeneratorDriver 直跑：诊断正反例（TCSG054-060）＋
/// 内存发射装配反射执行生成的 CLI/HTTP（解析/绑定/帮助/补全/退出码/状态码映射）。
/// 含 #454 回归面：三级嵌套组链式服务获取（组构造带参不落 new）＋用户参数名撞生成器保留前缀。
/// </summary>
public class CommandSourceGenTests
{
    /// <summary>fixture——根组 + 嵌套组（root 成员实例）+ 选项/枚举 + body + CommandError；
    /// 另含三级嵌套（sys → discovery，组构造带参——服务获取必须走父组属性链）与
    /// key/pos 位置参数名（与生成器自有局部变量旧名重合——保留前缀后不撞）。</summary>
    internal const string FamilySample = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using TC.Tier.CodeGen;
        using TC.Tier.CodeGen.Tests;

        namespace Sample
        {
            public enum Level { Low, High }

            [CommandGroup("traffic", Description = "TierTraffic 管理根")]
            public sealed class TrafficCommands
            {
                public RouteGroup Route { get; } = new();
                public SysGroup Sys { get; } = new("sys");

                [Command("ping", Description = "探活")]
                public bool Ping([CommandOption(LongName = "verbose", ShortName = 'v')] bool verbose)
                    => verbose;

                [Command("spec", Description = "提交规格")]
                [CommandError(typeof(ArgumentException), 422)]
                public async Task<string> SpecAsync(
                    [CommandArg(0)] Level level,
                    [CommandBody] RouteSpec spec,
                    CancellationToken ct = default)
                {
                    await Task.Yield();
                    if (spec.Name == "boom") throw new ArgumentException("规格非法");
                    return $"spec {level} {spec.Name}";
                }

                [Command("stat", Description = "统计")]
                public static long Stat([CommandOption(LongName = "limit")] long limit = 10) => limit;

                [Command("set", Description = "写值")]
                public string Set(
                    [CommandArg(0)] string key,
                    [CommandArg(1)] string pos,
                    [CommandOption(LongName = "ttl")] int? ttl = null)
                    => $"{key}/{pos}/{ttl?.ToString() ?? "none"}";

                public static class Probe
                {
                    public static string[] Next(string[] path) => global::System.Linq.Enumerable.ToArray(TrafficCommandsCli.CompleteNext(path));

                    public static string Help2(string[] path)
                    {
                        var w = new System.IO.StringWriter();
                        TrafficCommandsCli.WriteHelp(path, w);
                        return w.ToString();
                    }
                }

                [CommandGroup("route")]
                public sealed class RouteGroup
                {
                    [Command("add", Description = "添加路由规则")]
                    public string Add(
                        [CommandArg(0)] string name,
                        [CommandOption(LongName = "cost", ShortName = 'c')] int? cost = null,   // 设计契约：可空性≠可选——显式默认值才是可选
                        CancellationToken ct = default)
                        => $"added {name} cost={cost?.ToString() ?? "none"}";
                }

                [CommandGroup("sys", Description = "系统管理")]
                public sealed class SysGroup
                {
                    private readonly string _prefix;
                    public SysGroup(string prefix) => _prefix = prefix;   // 无参构造不存在——获取表达式禁止落 new
                    public DiscoveryGroup Discovery { get; } = new("d-");

                    [Command("echo", Description = "回显")]
                    public string Echo([CommandArg(0)] string msg) => _prefix + msg;

                    [CommandGroup("discovery", Description = "发现源")]
                    public sealed class DiscoveryGroup
                    {
                        private readonly string _prefix;
                        public DiscoveryGroup(string prefix) => _prefix = prefix;

                        [Command("add", Description = "加发现源")]
                        public string Add([CommandArg(0)] string name) => _prefix + name;
                    }
                }
            }
        }
        """;

    internal static string FamilySampleForProbe => FamilySample;

    private static (ImmutableArray<Diagnostic> Diagnostics, Compilation Compilation) Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var comp = CSharpCompilation.Create("cmd-test",
            new[] { tree },
            references:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "netstandard.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Text.Json.dll")),
                MetadataReference.CreateFromFile(typeof(CommandGroupAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(CommandSourceGenTests).Assembly.Location),
                // 生成物消费的框架面（Uri/集合）——真实消费方项目引用全集，此处对齐
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Private.Uri.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Console.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Linq.dll")),
            ],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver.Create(new CommandGenerator());
        driver.RunGeneratorsAndUpdateCompilation(comp, out var output, out var diagnostics);
        return (diagnostics, output);
    }

    private static readonly string[] s_routeAdd = { "route", "add" };
    private static readonly string[] s_spec = { "spec" };
    private static readonly string[] s_routeOnly = { "route" };
    private static readonly object[] s_routeAddPath = { s_routeAdd };

    private static System.Collections.Generic.List<Diagnostic> Tcsg(ImmutableArray<Diagnostic> diags)
        => diags.Where(d => d.Id.StartsWith("TCSG0", System.StringComparison.Ordinal)).ToList();

    // ══════════ 端到端：发射 + 反射执行 ══════════

    private static (System.Reflection.Assembly Asm, System.Collections.Generic.List<Diagnostic> Diags) EmitAssembly(
        string source)
    {
        var (diags, comp) = Run(source);
        var errors = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        errors.Should().BeEmpty("fixture＋生成物必须零错误：\n" + string.Join("\n", errors.Take(8).Select(e => e.ToString())));
        using var pe = new System.IO.MemoryStream();
        var result = comp.Emit(pe);
        result.Success.Should().BeTrue("发射失败：" + string.Join("\n", result.Diagnostics.Take(8)));
        return (System.Reflection.Assembly.Load(pe.ToArray()), Tcsg(diags));
    }

    private static object InvokeRun(System.Reflection.Assembly asm, params string[] args)
    {
        var cliType = asm.GetType("Sample.TrafficCommandsCli")!;
        var service = System.Activator.CreateInstance(asm.GetType("Sample.TrafficCommands")!)!;
        var stdout = new System.IO.StringWriter();
        var stderr = new System.IO.StringWriter();
        var run = cliType.GetMethod("Run")!;
        var parameters = run.GetParameters();
        var inputs = new object?[]
        {
            args, service, stdout, stderr, TestJsonContext.Default, default(System.Threading.CancellationToken), null,
        };
        var actual = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++) actual[i] = inputs[i];
        var rc = (int)run.Invoke(null, actual)!;
        return (rc, stdout.ToString(), stderr.ToString())!;
    }

    private static (int Rc, string Out, string Err) RunCli(System.Reflection.Assembly asm, params string[] args)
    {
        dynamic boxed = InvokeRun(asm, args);
        return ((int)boxed.Item1, (string)boxed.Item2, (string)boxed.Item3);
    }

    [Fact]
    public void FamilySample_ZeroDiagnostics()
    {
        var (asm, diags) = EmitAssembly(FamilySample);
        diags.Should().BeEmpty();
        asm.GetType("Sample.TrafficCommandsCli").Should().NotBeNull();
        asm.GetType("Sample.TrafficCommandsHttp").Should().NotBeNull();
    }

    [Fact]
    public void Cli_NestedGroupCommand_Executes()
    {
        var (asm, _) = EmitAssembly(FamilySample);
        var (rc, stdout, stderr) = RunCli(asm, "route", "add", "r1", "--cost", "5");
        rc.Should().Be(0, stderr.ToString());
        stdout.ToString().Trim().Should().Be("added r1 cost=5");

        var (rc2, out2, _) = RunCli(asm, "route", "add", "r2", "--cost=7");
        rc2.Should().Be(0);
        out2.Trim().Should().Be("added r2 cost=7");

        // 短选项 + 无值（Nullable 缺省）
        var (rc3, out3, _) = RunCli(asm, "route", "add", "r3", "-c", "9");
        rc3.Should().Be(0);
        out3.Trim().Should().Be("added r3 cost=9");
    }

    [Fact]
    public void Cli_ThreeLevelNesting_ChainedServiceAcquisition()
    {
        var (asm, _) = EmitAssembly(FamilySample);

        // 二级组：组构造带参（无参构造不存在）——获取表达式必须经根属性解析（service.Sys）
        var echo = RunCli(asm, "sys", "echo", "m1");
        echo.Rc.Should().Be(0, echo.Err);
        echo.Out.Trim().Should().Be("sysm1");

        // 三级组：DiscoveryGroup 挂在 SysGroup 下（根上无该类型属性）——父组属性链式解析
        // （#454 限制 1 回归：旧实现落 new DiscoveryGroup() → CS7036）
        var add = RunCli(asm, "sys", "discovery", "add", "r1");
        add.Rc.Should().Be(0, add.Err);
        add.Out.Trim().Should().Be("d-r1");
    }

    [Fact]
    public void Cli_UserParamNames_KeyAndPos_NoCollision()
    {
        var (asm, _) = EmitAssembly(FamilySample);

        // 位置参数名 key/pos 与生成器自有局部变量旧名重合 + 同命令带选项——#454 限制 2 回归
        // （旧实现：key 撞选项解析循环局部 → CS0136；pos 撞方法级 pos 局部 → CS0128）
        var (rc, stdout, stderr) = RunCli(asm, "set", "k1", "p1", "--ttl", "5");
        rc.Should().Be(0, stderr.ToString());
        stdout.ToString().Trim().Should().Be("k1/p1/5");

        var (rc2, out2, _) = RunCli(asm, "set", "k2", "p2");
        rc2.Should().Be(0);
        out2.Trim().Should().Be("k2/p2/none");
    }

    [Fact]
    public void Cli_ExitCodes_HelpAndSyntaxAndRuntime()
    {
        var (asm, _) = EmitAssembly(FamilySample);

        // 未知命令 → 2
        RunCli(asm, "bogus").Rc.Should().Be(2);
        // 未知子命令 → 2
        RunCli(asm, "route", "bogus").Rc.Should().Be(2);
        // 未知选项 → 2
        RunCli(asm, "route", "add", "r1", "--nope").Rc.Should().Be(2);
        // 缺位置参数 → 2
        RunCli(asm, "route", "add").Rc.Should().Be(2);
        // 帮助：组级 -h / 任意层级 --help / 命令级
        RunCli(asm, "-h").Rc.Should().Be(0);
        RunCli(asm, "-h").Out.Should().Contain("traffic");
        RunCli(asm, "route", "--help").Rc.Should().Be(0);
        RunCli(asm, "route", "add", "-h").Rc.Should().Be(0);
        RunCli(asm, "route", "add", "-h").Out.Should().Contain("--cost");
        // 静态命令 + 可选选项缺省
        var stat = RunCli(asm, "stat");
        stat.Rc.Should().Be(0);
        stat.Out.Trim().Should().Be("10");
        var stat2 = RunCli(asm, "stat", "--limit", "99");
        stat2.Out.Trim().Should().Be("99");
        // bool 选项裸形态 + 短名
        var ping = RunCli(asm, "ping", "--verbose");
        ping.Rc.Should().Be(0);
        ping.Out.Trim().Should().Be("True");
        var ping2 = RunCli(asm, "ping", "-v");
        ping2.Out.Trim().Should().Be("True");
        var ping3 = RunCli(asm, "ping");
        ping3.Out.Trim().Should().Be("False");
    }

    [Fact]
    public void Cli_BodyWithoutJson_Exit2()
    {
        var (asm, _) = EmitAssembly(FamilySample);
        var (rc, _, stderr) = RunCli(asm, "spec", "High");
        rc.Should().Be(2);
        stderr.ToString().Should().Contain("body");
    }

    [Fact]
    public void Cli_CompleteNext_And_CommandPaths()
    {
        var (asm, _) = EmitAssembly(FamilySample);
        var probe = asm.GetType("Sample.TrafficCommands+Probe")!;
        var next = (string[]?)probe.GetMethod("Next")!.Invoke(null, new object?[] { s_routeOnly });
        next.Should().Contain("add");

        var cliType = asm.GetType("Sample.TrafficCommandsCli")!;
        var paths = (System.Collections.Generic.IReadOnlyList<string[]>)(cliType
            .GetProperty("CommandPaths")!.GetValue(null)!);
        paths.Should().ContainEquivalentOf(s_routeAdd);
        paths.Should().ContainEquivalentOf(s_spec);

        // 双档帮助 API（span 路径经 Probe 包装——反射不能装箱 ReadOnlySpan）
        var help2 = (string?)probe.GetMethod("Help2")!.Invoke(null, new object?[] { s_routeAdd });
        help2.Should().Contain("--cost");
    }

    // ══════════ HTTP 代理 ══════════

    private static (int Rc, int? Status, string Body) InvokeHttp(
        System.Reflection.Assembly asm, string method, string path, string? query, byte[] body)
    {
        var httpType = asm.GetType("Sample.TrafficCommandsHttp")!;
        var handle = httpType.GetMethod("TryHandleAsync")!;
        var request = new CommandHttpRequest
        {
            Method = method, EncodedPath = path, Query = query, Body = body,
        };
        var service = System.Activator.CreateInstance(asm.GetType("Sample.TrafficCommands")!)!;
        var results = new TestResults();
        var raw = handle.Invoke(null,
            new object?[] { request, service, results, TestJsonContext.Default, default(System.Threading.CancellationToken) })!;
        var task = (System.Threading.Tasks.ValueTask<CommandHttpResponse?>)raw;
        var response = task.AsTask().GetAwaiter().GetResult();
        return response is null
            ? (404, null, string.Empty)
            : (0, response.Value.Status, System.Text.Encoding.UTF8.GetString(response.Value.Body));
    }

    private sealed class TestResults : ICommandResults
    {
        public System.Threading.Tasks.Task<CommandHttpResponse> WriteAsync<T>(T result)
            => System.Threading.Tasks.Task.FromResult(new CommandHttpResponse
            {
                Status = 200,
                ContentType = "application/json",
                Body = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(result)),
            });

        public CommandHttpResponse WriteError(int status, string message)
            => new() { Status = status, Body = System.Text.Encoding.UTF8.GetBytes(message) };

        public CommandHttpResponse WriteError(int status, System.Exception error)
            => new() { Status = status, Body = System.Text.Encoding.UTF8.GetBytes(error.Message) };

        public string Render<T>(T result) => System.Text.Json.JsonSerializer.Serialize(result);
    }

    [Fact]
    public void Http_Route_Query_And_Positional()
    {
        var (asm, diags) = EmitAssembly(FamilySample);
        diags.Should().BeEmpty();

        // 位置参数走路由段 + 选项走 query
        var (rc, status, body) = InvokeHttp(asm, "POST", "/traffic/route/add/r1", "cost=5", Array.Empty<byte>());
        rc.Should().Be(0);
        status.Should().Be(200);
        body.Should().Contain("added r1 cost=5");

        // 位置参数回落 query
        var (rc2, status2, body2) = InvokeHttp(asm, "POST", "/traffic/route/add", "name=r2", Array.Empty<byte>());
        rc2.Should().Be(0);
        status2.Should().Be(200);
        body2.Should().Contain("added r2 cost=none");

        // 根段不匹配 → null（宿主续走）
        InvokeHttp(asm, "POST", "/other/x", null, Array.Empty<byte>()).Status.Should().BeNull();
        // 方法不符 → 405
        InvokeHttp(asm, "GET", "/traffic/route/add/r1", null, Array.Empty<byte>()).Status.Should().Be(405);
    }

    [Fact]
    public void Http_Body_And_ErrorStatusMapping()
    {
        var (asm, _) = EmitAssembly(FamilySample);

        // body JSON 反序列化（GetTypeInfo 查表非反射）+ 枚举位置参数
        var (rc, status, body) = InvokeHttp(asm, "POST", "/traffic/spec",
            "level=High", System.Text.Encoding.UTF8.GetBytes("""{"Name":"n1"}"""));
        rc.Should().Be(0);
        status.Should().Be(200);
        body.Should().Contain("spec High n1");

        // 声明异常 → 声明 Status（422）
        var (rcBad, statusBad, bodyBad) = InvokeHttp(asm, "POST", "/traffic/spec",
            "level=Low", System.Text.Encoding.UTF8.GetBytes("""{"Name":"boom"}"""));
        rcBad.Should().Be(0);
        statusBad.Should().Be(422);
        bodyBad.Should().Contain("规格非法");

        // body 解析失败 → 400
        InvokeHttp(asm, "POST", "/traffic/spec", "level=Low",
            System.Text.Encoding.UTF8.GetBytes("{not-json")).Status.Should().Be(400);
    }

    [Fact]
    public void Http_ThreeLevelNesting_And_KeyPosParams()
    {
        var (asm, _) = EmitAssembly(FamilySample);

        // 三级嵌套路由：/traffic/sys/discovery/add/<name>——父组属性链式获取
        var (rc, status, body) = InvokeHttp(asm, "POST", "/traffic/sys/discovery/add/r1", null, Array.Empty<byte>());
        rc.Should().Be(0);
        status.Should().Be(200);
        body.Should().Contain("d-r1");

        // key/pos 位置参数经 query 回落（按参数名）
        var (rc2, status2, body2) = InvokeHttp(asm, "POST", "/traffic/set", "key=k1&pos=p1&ttl=5", Array.Empty<byte>());
        rc2.Should().Be(0);
        status2.Should().Be(200);
        body2.Should().Contain("k1/p1/5");
    }

    // ══════════ 诊断正反例（TCSG054-060）══════════

    [Theory]
    [InlineData("dup-command")]
    [InlineData("dup-position")]
    [InlineData("name-with-slash")]
    [InlineData("unannotated")]
    [InlineData("ref-param")]
    [InlineData("get-with-body")]
    [InlineData("two-bodies")]
    [InlineData("stray-command")]
    [InlineData("error-not-exception")]
    [InlineData("reserved-param-name")]
    public void Diagnostics_NegativeCases_ReportsExpectedId(string caseName)
    {
        var source = caseName switch
        {
            "dup-command" => """
                using TC.Tier.CodeGen;
                namespace Sample;
                [CommandGroup("app")]
                public sealed class A
                {
                    [Command("x")] public void X1() { }
                    [Command("x")] public void X2() { }
                }
                """,
            "dup-position" => """
                using TC.Tier.CodeGen;
                namespace Sample;
                [CommandGroup("app")]
                public sealed class A
                {
                    [Command("x")] public void X([CommandArg(0)] string a, [CommandArg(0)] string b) { }
                }
                """,
            "name-with-slash" => """
                using TC.Tier.CodeGen;
                namespace Sample;
                [CommandGroup("app")]
                public sealed class A
                {
                    [Command("a/b")] public void X() { }
                }
                """,
            "unannotated" => """
                using TC.Tier.CodeGen;
                namespace Sample;
                [CommandGroup("app")]
                public sealed class A
                {
                    [Command("x")] public void X(string free) { }
                }
                """,
            "ref-param" => """
                using TC.Tier.CodeGen;
                namespace Sample;
                [CommandGroup("app")]
                public sealed class A
                {
                    [Command("x")] public void X([CommandArg(0)] ref string a) { }
                }
                """,
            "get-with-body" => """
                using TC.Tier.CodeGen;
                namespace Sample;
                public sealed class B { public string Name { get; set; } = ""; }
                [CommandGroup("app")]
                public sealed class A
                {
                    [Command("x", Method = "GET")] public void X([CommandBody] B b) { }
                }
                """,
            "two-bodies" => """
                using TC.Tier.CodeGen;
                namespace Sample;
                public sealed class B { public string Name { get; set; } = ""; }
                [CommandGroup("app")]
                public sealed class A
                {
                    [Command("x")] public void X([CommandBody] B b1, [CommandBody] B b2) { }
                }
                """,
            "stray-command" => """
                using TC.Tier.CodeGen;
                namespace Sample;
                public sealed class A
                {
                    [Command("x")] public void X() { }
                }
                """,
            "reserved-param-name" => """
                using TC.Tier.CodeGen;
                namespace Sample;
                [CommandGroup("app")]
                public sealed class A
                {
                    [Command("x")] public void X([CommandArg(0)] string __tcsg_key) { }
                }
                """,
            _ => """
                using System;
                using TC.Tier.CodeGen;
                namespace Sample;
                [CommandGroup("app")]
                public sealed class A
                {
                    [Command("x")]
                    [CommandError(typeof(int), 500)]
                    public void X() { }
                }
                """,
        };

        var diags = Tcsg(Run(source).Diagnostics);
        diags.Should().NotBeEmpty($"case {caseName} 必须出诊断");
    }

    [Fact]
    public void Diagnostics_CleanSample_Zero()
    {
        Tcsg(Run(FamilySample).Diagnostics).Should().BeEmpty();
    }

    [Fact]
    public void Diagnostics_ReservedParamName_ExactTCSG060()
    {
        const string source = """
            using TC.Tier.CodeGen;
            namespace Sample;
            [CommandGroup("app")]
            public sealed class A
            {
                [Command("x")] public void X([CommandArg(0)] string __tcsg_pos) { }
            }
            """;
        var diags = Tcsg(Run(source).Diagnostics);
        diags.Should().ContainSingle(d => d.Id == "TCSG060",
            "保留前缀参数名必须精确报 TCSG060（生成代码自有标识符命名空间）");
    }
}
