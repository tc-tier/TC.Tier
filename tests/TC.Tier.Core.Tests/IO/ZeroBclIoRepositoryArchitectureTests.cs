namespace TC.Tier.Core.Tests.IO;

/// <summary>
/// 零 BCL IO 架构测试扩面（RM-15——raw-medium-and-conversion-design §11 "Raw 落地后覆盖全仓库消费者"）。
/// <para>★ 范围：src/ 全部工程（Core IO 白名单除外——IO 实现内部自由）。</para>
/// <para>★ 灰度策略：已知存量白名单（文件级，注释指向清偿路径）——违约集合**只减不增**
///   （新增违约即红；存量清偿后从白名单删除，最终收敛到空）。</para>
/// </summary>
public sealed class ZeroBclIoRepositoryArchitectureTests
{
    /// <summary>存量白名单（RM-15 建账 2026-08-19 → 2026-08-20 清零）。
    /// 原建账 9 文件（Runtime Storage 引擎面）已随 storage-engine-core-io-migration 全部清偿：
    /// SnapshotCheckpoint×2 / EngineMeta / IO/DiskFileHandle / StorageEngineFactory / DiskCompactor
    /// 随类系统坍缩删除；ScanCheckpoint / CompactorBase 改经 IFileSystem（含注释措辞去 BCL 化）。
    /// 现为空集——新增违约即红，永不回填。</summary>
    private static readonly string[] s_knownOffenders = Array.Empty<string>();

    /// <summary>IO 家族工程（IO 实现内部自由条款——与 Core/IO 目录同待遇）。</summary>
    private static readonly string[] s_ioFamilyProjects =
    {
        "TC.Tier.Core/IO", "TC.Tier.Core/NativeInterop", "TC.Tier.Core.IO.Net", "TC.Tier.Core.IO.S3",
    };

    [Fact]
    public void Repository_OutsideIoFamily_DoesNotUseBclFileApis()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(ZeroBclIoRepositoryArchitectureTests).Assembly.Location)!;
        var repoRoot = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."));
        var srcRoot = Path.Combine(repoRoot, "src");
        Directory.Exists(srcRoot).Should().BeTrue($"测试定位源目录失败：{srcRoot}");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = file.Replace('\\', '/');
            if (normalized.Contains("/obj/") || normalized.Contains("/bin/")) continue;
            var rel = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
            if (s_ioFamilyProjects.Any(p => normalized.Contains($"/{p}/")
                    || normalized.EndsWith($"/{p}.cs", StringComparison.Ordinal)))
                continue;   // IO 家族（实现内部自由）
            if (s_knownOffenders.Contains(rel))
                continue;   // 存量白名单（只减不增）

            var text = File.ReadAllText(file);
            foreach (var token in s_banned)
                if (text.Contains(token, StringComparison.Ordinal))
                    offenders.Add($"{rel}: 含禁用 BCL IO「{token}」");
        }

        offenders.Should().BeEmpty(
            "IO 家族与存量白名单之外禁止 BCL 文件族——一切文件访问经 IFileSystem（零 BCL IO 闭环，设计 §11；"
            + "新增违约即红；存量清偿 = 从 s_knownOffenders 删除对应条目）");
    }

    /// <summary>禁用词表（两测试共享——白名单诚实性检查用同表防口径漂移）。</summary>
    private static readonly string[] s_banned =
    {
        "File.Open", "File.Read", "File.Write", "File.Delete", "File.Exists", "File.Move",
        "File.Create", "File.Copy", "File.OpenHandle", "File.ReadAllText", "File.WriteAllText",
        "File.AppendAllText", "File.Replace",
        "Directory.CreateDirectory", "Directory.Delete", "Directory.Exists", "Directory.Move",
        "Directory.GetFiles", "Directory.GetDirectories", "Directory.EnumerateFiles",
        "new FileStream", "new StreamWriter", "new StreamReader",
    };

    [Fact]
    public void KnownOffenders_StillExist_WhiteListStaysHonest()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(ZeroBclIoRepositoryArchitectureTests).Assembly.Location)!;
        var repoRoot = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."));
        foreach (var rel in s_knownOffenders)
        {
            var path = Path.Combine(repoRoot, rel);
            if (!File.Exists(path)) continue;   // 文件被删除/改名——白名单条目应同步清理（人工动作）
            var text = File.ReadAllText(path);
            var stillOffending = s_banned.Any(t => text.Contains(t, StringComparison.Ordinal));
            stillOffending.Should().BeTrue(
                $"{rel} 在白名单中但已无违约 BCL IO——请从 s_knownOffenders 删除（白名单诚实性：条目与实况一致）");
        }
    }
}
