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

/// <summary>测试用未注册类型（不进 TestJsonContext——GetTypeInfo 查表未命中面）。</summary>
public sealed class UnregisteredBody
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

                [Command("raw", Description = "未注册 body 类型")]
                public string Raw([CommandBody] UnregisteredBody raw) => raw.Name;

                [Command("maybe", Description = "可空 body")]
                public string Maybe([CommandBody] RouteSpec? maybe) => maybe?.Name ?? "none";

                [Command("stat", Description = "统计")]
                public static long Stat([CommandOption(LongName = "limit")] long limit = 10) => limit;

                [Command("set", Description = "写值")]
                public string Set(
                    [CommandArg(0)] string key,
                    [CommandArg(1)] string pos,
                    [CommandOption(LongName = "ttl")] int? ttl = null)
                    => $"{key}/{pos}/{ttl?.ToString() ?? "none"}";

                [Command("put", Description = "可选位置参数（#454 残项——带缺省值位置参数的旗标声明）")]
                public string Put(
                    [CommandArg(0)] string ns,
                    [CommandArg(1)] string jobId = "",
                    [CommandOption(LongName = "ttl")] int? ttl = null)
                    => $"{ns}/{jobId}/{ttl?.ToString() ?? "none"}";

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

    private static readonly ImmutableArray<ISourceGenerator> s_stjGenerators = LoadStjGenerators();

    /// <summary>STJ 源生成器（targeting pack 分析器装配——真实消费工程 SDK 编译同款）：
    /// 生成物 JsonContext 的 Default/元数据成员由其落地，harness 内存编译必须同链装配。</summary>
    private static ImmutableArray<ISourceGenerator> LoadStjGenerators()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var dotnetRoot = Path.GetFullPath(Path.Combine(runtimeDir, "..", "..", ".."));
        var packsDir = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        if (!Directory.Exists(packsDir))
            throw new InvalidOperationException("未找到 Microsoft.NETCore.App.Ref packs——STJ 生成器装配面缺失");
        var genPath = Directory.EnumerateDirectories(packsDir)
            .Select(v => Path.Combine(v, "analyzers", "dotnet", "cs", "System.Text.Json.SourceGeneration.dll"))
            .FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("targeting pack 内未找到 System.Text.Json.SourceGeneration.dll");
        var fileRef = new Microsoft.CodeAnalysis.Diagnostics.AnalyzerFileReference(genPath, new TestAnalyzerAssemblyLoader());
        return fileRef.GetGenerators(LanguageNames.CSharp);
    }

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
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Text.Encodings.Web.dll")),
                MetadataReference.CreateFromFile(typeof(CommandGroupAttribute).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(CommandSourceGenTests).Assembly.Location),
                // 生成物消费的框架面（Uri/集合）——真实消费方项目引用全集，此处对齐
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Private.Uri.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Console.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Linq.dll")),
            ],
            // 对齐真实消费面（NRT 启用）——可空 body 声明的注解依赖编译期 Nullable 上下文
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        var driver = CSharpGeneratorDriver.Create(new CommandGenerator());
        driver.RunGeneratorsAndUpdateCompilation(comp, out var output, out var diagnostics);
        // 阶段二：STJ 源生成器消费命令生成器产物（JsonContext partial）——与真实消费工程多代生成一致；
        // 生成物引用 JsonContext.Default，未装配 STJ 面 = 生成物编译必然失败
        if (!s_stjGenerators.IsEmpty)
        {
            CSharpGeneratorDriver.Create(s_stjGenerators)
                .RunGeneratorsAndUpdateCompilation(output, out var stjOutput, out _);
            output = stjOutput;
        }
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

    private static object InvokeRun(System.Reflection.Assembly asm, System.Text.Json.Serialization.JsonSerializerContext? json, params string[] args)
    {
        var cliType = asm.GetType("Sample.TrafficCommandsCli")!;
        var service = System.Activator.CreateInstance(asm.GetType("Sample.TrafficCommands")!)!;
        var stdout = new System.IO.StringWriter();
        var stderr = new System.IO.StringWriter();
        var run = cliType.GetMethod("Run")!;
        var parameters = run.GetParameters();
        var inputs = new object?[]
        {
            args, service, stdout, stderr, json, default(System.Threading.CancellationToken), null,
        };
        var actual = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++) actual[i] = inputs[i];
        var rc = (int)run.Invoke(null, actual)!;
        return (rc, stdout.ToString(), stderr.ToString())!;
    }

    private static (int Rc, string Out, string Err) RunCli(System.Reflection.Assembly asm, params string[] args)
    {
        dynamic boxed = InvokeRun(asm, TestJsonContext.Default, args);
        return ((int)boxed.Item1, (string)boxed.Item2, (string)boxed.Item3);
    }

    private static (int Rc, string Out, string Err) RunCliNoJson(System.Reflection.Assembly asm, params string[] args)
    {
        dynamic boxed = InvokeRun(asm, null, args);
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
    public void Cli_OptionalPositional_DefaultsAndExtraTokens()
    {
        var (asm, _) = EmitAssembly(FamilySample);

        // 缺必选位置参数 → 语法错误（缺省值只豁免可选槽）
        RunCli(asm, "put").Rc.Should().Be(2);
        // 可选位置参数缺席 → 默认值；在场 → 绑定
        var a = RunCli(asm, "put", "n");
        a.Rc.Should().Be(0, a.Err);
        a.Out.Trim().Should().Be("n//none");
        var b = RunCli(asm, "put", "n", "j1");
        b.Rc.Should().Be(0, b.Err);
        b.Out.Trim().Should().Be("n/j1/none");
        var c = RunCli(asm, "put", "n", "j1", "--ttl", "5");
        c.Out.Trim().Should().Be("n/j1/5");
        // 多余令牌：可选槽消耗后旗标已置位——落"多余的参数"而非重复吸收
        RunCli(asm, "put", "n", "j1", "extra").Rc.Should().Be(2);

        // HTTP 面：可选位置参数 query 回落
        var (rcH, stH, bodyH) = InvokeHttp(asm, "POST", "/traffic/put", "ns=n&jobId=j1", Array.Empty<byte>());
        rcH.Should().Be(0);
        stH.Should().Be(200);
        bodyH.Should().Contain("n/j1/none");
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
        var (rc, _, stderr) = RunCliNoJson(asm, "spec", "High");
        rc.Should().Be(2);
        stderr.ToString().Should().Contain("body");
    }

    [Fact]
    public void Cli_BodyEmptyStdin_JsonException_Exit2()
    {
        var (asm, _) = EmitAssembly(FamilySample);
        // json 注入但 stdin 空（测试宿主 EOF）——空输入走 JsonException 语法错误路径
        var (rc, _, stderr) = RunCli(asm, "spec", "High");
        rc.Should().Be(2);
        stderr.ToString().Should().Contain("解析失败");
    }

    [Fact]
    public void Cli_BodyTypeNotRegisteredInContext_Exit2()
    {
        var (asm, _) = EmitAssembly(FamilySample);
        // 查表未命中在 stdin 读取前拦截——不经输入流即可观测
        var (rc, _, stderr) = RunCli(asm, "raw");
        rc.Should().Be(2);
        stderr.ToString().Should().Contain("未注册");
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
    public void Http_BodyTypeNotRegisteredInContext_BadRequest()
    {
        var (asm, _) = EmitAssembly(FamilySample);
        var (rc, status, body) = InvokeHttp(asm, "POST", "/traffic/raw", null,
            System.Text.Encoding.UTF8.GetBytes("""{"Name":"n1"}"""));
        rc.Should().Be(0);
        status.Should().Be(400);
        body.Should().Contain("未注册");
    }

    [Fact]
    public void Http_JsonNullBody_NonNullableBadRequest_NullableBindsNull()
    {
        var (asm, _) = EmitAssembly(FamilySample);

        // 非可空声明 body：JSON null → 400 回执（不裸 null 绑定进命令面）
        var (rc, status, body) = InvokeHttp(asm, "POST", "/traffic/spec", "level=Low",
            System.Text.Encoding.UTF8.GetBytes("null"));
        rc.Should().Be(0);
        status.Should().Be(400);
        body.Should().Contain("body 不能为 null");

        // 可空声明 body（RouteSpec?）：JSON null 放行 → 命令收到 null
        var (rc2, status2, body2) = InvokeHttp(asm, "POST", "/traffic/maybe", null,
            System.Text.Encoding.UTF8.GetBytes("null"));
        rc2.Should().Be(0);
        status2.Should().Be(200);
        body2.Should().Contain("none");

        // 可空声明 + 正常载荷照常绑定
        var (rc3, status3, body3) = InvokeHttp(asm, "POST", "/traffic/maybe", null,
            System.Text.Encoding.UTF8.GetBytes("""{"Name":"m1"}"""));
        rc3.Should().Be(0);
        status3.Should().Be(200);
        body3.Should().Contain("m1");
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
    [InlineData("unlinked-group-no-ctor")]
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
            "unlinked-group-no-ctor" => """
                using TC.Tier.CodeGen;
                namespace Sample;
                [CommandGroup("app")]
                public sealed class A
                {
                    [CommandGroup("sub")]
                    public sealed class Sub
                    {
                        public Sub(string p) { }
                        [Command("go")] public string Go([CommandArg(0)] string m) => m;
                    }
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

    [Fact]
    public void Diagnostics_UnlinkedGroupNoCtor_ExactTCSG061()
    {
        const string source = """
            using TC.Tier.CodeGen;
            namespace Sample;
            [CommandGroup("app")]
            public sealed class A
            {
                [Command("x")] public void X() { }

                [CommandGroup("sub")]
                public sealed class Sub
                {
                    private readonly string _p;
                    public Sub(string p) => _p = p;
                    [Command("go")] public string Go([CommandArg(0)] string m) => _p + m;
                }
            }
            """;
        var (genDiags, comp) = Run(source);
        Tcsg(genDiags).Should().ContainSingle(d => d.Id == "TCSG061",
            "全链无同型成员且无可访问无参构造必须精确报 TCSG061，位置在组声明处");
        comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error
                && d.Id.StartsWith("CS", System.StringComparison.Ordinal))
            .Should().BeEmpty("获取表达式已落占位——生成物不再产生 CS7036 形态的生成文件错误");
    }

    [Fact]
    public void NestedGroup_ParameterlessCtorFallback_StillWorks()
    {
        const string source = """
            using TC.Tier.CodeGen;
            namespace Sample;
            [CommandGroup("app")]
            public sealed class TrafficCommands
            {
                [CommandGroup("util")]
                public sealed class Util
                {
                    [Command("ping")] public string Ping() => "pong";
                }
            }
            """;
        var (asm, diags) = EmitAssembly(source);
        diags.Should().BeEmpty("无参构造回落是文档契约（服务注入与嵌套深度解耦）——不报诊断");
        var r = RunCli(asm, "util", "ping");
        r.Rc.Should().Be(0, r.Err);
        r.Out.Trim().Should().Be("pong");
    }

    // ══════════ 命令 I/O JSON 契约（#484——回执序列化编译期闭合）══════════

    [Fact]
    public void JsonContext_RegistersAllReturnAndBodyTypes()
    {
        var (_, comp) = Run(FamilySample);
        var tree = comp.SyntaxTrees.Single(t =>
            t.FilePath.EndsWith("TrafficCommandsJsonResults.g.cs", System.StringComparison.Ordinal));
        var text = tree.ToString();
        text.Should().Contain("class TrafficCommandsJsonContext", "生成 context 类在位");
        text.Should().Contain("class TrafficCommandsJsonResults", "默认渲染器在位");
        text.Should().Contain("readonly record struct TrafficCommandsJsonErrorEnvelope", "错误信封在位");
        // 返回类型 + body 类型全量登记（含未进消费方 TestJsonContext 的 UnregisteredBody）
        const string attr = "[global::System.Text.Json.Serialization.JsonSerializable(typeof(";
        text.Should().Contain(attr + "global::TC.Tier.CodeGen.Tests.UnregisteredBody))]");
        text.Should().Contain(attr + "global::TC.Tier.CodeGen.Tests.RouteSpec))]");
        text.Should().Contain(attr + "global::Sample.TrafficCommandsJsonErrorEnvelope))]");
    }

    [Fact]
    public void Http_DefaultResultsAndJson_ZeroWiringReceipt()
    {
        var (asm, diags) = EmitAssembly(FamilySample);
        diags.Should().BeEmpty();

        // results/json 双缺省：生成 context 反序列化 body + 序列化回执（#484 零接线面）
        var (rc, status, body) = InvokeHttpDefaults(asm, "POST", "/traffic/spec", "level=High",
            System.Text.Encoding.UTF8.GetBytes("""{"Name":"n1"}"""));
        rc.Should().Be(0);
        status.Should().Be(200);
        body.Should().Contain("spec High n1");

        // 用法错误 → 生成信封（Code/Message JSON；非 ASCII 由 STJ 默认编码器转义）
        var (rcErr, statusErr, bodyErr) = InvokeHttpDefaults(asm, "POST", "/traffic/set", "key=k1",
            Array.Empty<byte>());
        statusErr.Should().Be(400);
        bodyErr.Should().StartWith("{\"Code\":400").And.Contain("\"Message\":\"");
    }

    [Fact]
    public void Cli_DefaultJsonContext_AutoRegistersBodyType()
    {
        var (asm, diags) = EmitAssembly(FamilySample);
        diags.Should().BeEmpty();

        // json 缺省 = 生成 JsonContext——UnregisteredBody（不进消费方 TestJsonContext）由生成器
        // 自动登记：登记拦截不再触发，stdin 空载荷走到 JSON 语法错误（#484 旗舰面）
        var (rc, _, stderr) = RunCliNoJson(asm, "raw");
        stderr.ToString().Should().NotContain("未注册", "生成 context 已自动登记全部 body 类型");
        rc.Should().Be(2, "stdin 空载荷 = JSON 语法错误路径");
        stderr.ToString().Should().Contain("解析失败");
    }

    [Fact]
    public void Cli_Http_DefaultResults_NewCommandFamilyZeroRegistration()
    {
        const string source = """
            using TC.Tier.CodeGen;
            namespace Sample;
            public sealed class Receipt { public string Name { get; set; } = ""; }
            [CommandGroup("app")]
            public sealed class TrafficCommands
            {
                [Command("issue")] public Receipt Issue([CommandArg(0)] string name) => new Receipt { Name = name };
            }
            """;
        var (asm, diags) = EmitAssembly(source);
        diags.Should().BeEmpty();

        // 新命令族零登记：CLI stdout 走生成 JsonResults（源生成序列化），HTTP 双缺省同路径
        var cli = RunCliNoJson(asm, "issue", "k1");
        cli.Rc.Should().Be(0, cli.Err);
        cli.Out.Trim().Should().Be("{\"Name\":\"k1\"}");

        var http = InvokeHttpDefaults(asm, "POST", "/app/issue/k1", null, Array.Empty<byte>());
        http.Status.Should().Be(200);
        http.Body.Should().Be("{\"Name\":\"k1\"}");
    }

    /// <summary>HTTP 调用（results/json 双缺省——生成 JsonResults/JsonContext 消费面）。</summary>
    private static (int Rc, int? Status, string Body) InvokeHttpDefaults(
        System.Reflection.Assembly asm, string method, string path, string? query, byte[] body)
    {
        var httpType = asm.GetType("Sample.TrafficCommandsHttp")!;
        var handle = httpType.GetMethod("TryHandleAsync")!;
        var request = new CommandHttpRequest
        {
            Method = method, EncodedPath = path, Query = query, Body = body,
        };
        var service = System.Activator.CreateInstance(asm.GetType("Sample.TrafficCommands")!)!;
        var raw = handle.Invoke(null,
            new object?[] { request, service, null, null, default(System.Threading.CancellationToken) })!;
        var task = (System.Threading.Tasks.ValueTask<CommandHttpResponse?>)raw;
        var response = task.AsTask().GetAwaiter().GetResult();
        return response is null
            ? (404, null, string.Empty)
            : (0, response.Value.Status, System.Text.Encoding.UTF8.GetString(response.Value.Body));
    }
}

/// <summary>分析器装配加载器（harness 装配 targeting pack 的 STJ 源生成器——最小实现）。</summary>
internal sealed class TestAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
{
    public void AddDependencyLocation(string fullPath) { }

    public System.Reflection.Assembly LoadFromPath(string fullPath)
        => System.Reflection.Assembly.LoadFrom(fullPath);
}