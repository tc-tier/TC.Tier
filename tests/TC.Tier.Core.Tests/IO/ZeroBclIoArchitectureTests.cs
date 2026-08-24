namespace TC.Tier.Core.Tests.IO;

/// <summary>
/// 零 BCL IO 架构测试（raw-medium-and-conversion-design §11）——
/// "一个 IFileSystem 对象解决全部"的机械化锁死：TC.Tier.Core 的 IO 目录之外禁止 BCL 文件族 API。
/// <para>★ 白名单：NativeInterop/（IO 的 syscall 底座——FileNative/DiskNative/Kernel32/LibC 是
///   IO 实现的原生半边，"IO 实现内部自由"条款覆盖）；obj/bin 产物目录。</para>
/// <para>★ 生效范围随分期扩大（设计 §11）：P1 = Core 层；Raw 落地后扩至全仓库消费者。</para>
/// </summary>
public sealed class ZeroBclIoArchitectureTests
{
    [Fact]
    public void Core_OutsideIoDir_DoesNotUseBclFileApis()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(ZeroBclIoArchitectureTests).Assembly.Location)!;
        // tests/TC.Tier.Core.Tests/bin/Debug/net8.0（目录）→ 仓库根（上溯五级：net8.0→Debug→bin→工程→tests→根）
        var repoRoot = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."));
        var coreSrc = Path.Combine(repoRoot, "src", "TC.Tier.Core");
        Directory.Exists(coreSrc).Should().BeTrue($"测试定位源目录失败：{coreSrc}");

        var banned = new[]
        {
            "File.Open", "File.Read", "File.Write", "File.Delete", "File.Exists", "File.Move",
            "File.Create", "File.Copy", "File.OpenHandle", "File.AppendText", "File.Replace",
            "Directory.CreateDirectory", "Directory.Delete", "Directory.Exists", "Directory.Move",
            "Directory.GetFiles", "Directory.GetDirectories", "Directory.Enumerate",
            "new FileStream", "new StreamWriter", "new StreamReader",
        };

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(coreSrc, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/") || normalized.Contains("/bin/")) continue;
            if (normalized.Contains("/IO/")) continue;   // IO 实现（含 Image/管线）内部自由
            if (normalized.Contains("/NativeInterop/")) continue;   // IO 的 syscall 底座（白名单）

            var text = File.ReadAllText(file);
            foreach (var token in banned)
                if (text.Contains(token, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetRelativePath(repoRoot, file)}: 含禁用 BCL IO「{token}」");
        }

        offenders.Should().BeEmpty(
            "Core 的 IO 目录之外禁止 BCL 文件族——一切文件访问经 IFileSystem（零 BCL IO 闭环，设计 §11）");
    }
}
