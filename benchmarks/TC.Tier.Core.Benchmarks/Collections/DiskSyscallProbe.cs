using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;

namespace TC.Tier.Core.Benchmarks.Collections;

/// <summary>
/// Disk 同步写 syscall 计数探针（CORE-09 验收）——strace 统计每次 Write 的 syscall 数：
/// 配额关闭时每写应 = 1 syscall（pwrite64——fstat 已消除）；配额开启时 = 2（fstat + pwrite）。
/// 用法：strace -c -e trace=write,pwrite64,fstat,newfstatat dotnet run ... -- --disk-syscall-probe [writes]
/// </summary>
internal static class DiskSyscallProbe
{
    public static int Run(string[] args)
    {
        var writes = args.Length > 1 && int.TryParse(args[1], out var w) ? w : 1000;
        var dir = Path.Combine(Path.GetTempPath(), "tier-disk-syscall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var fs = DiskFileSystem.Open(dir);
            using var h = fs.Open("f.bin", new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.CreateNew });
            var buf = new byte[4096];
            for (var i = 0; i < writes; i++)
                h.Write(i * 4096L, buf);
            Console.WriteLine($"Disk 同步写完成：{writes} × 4KB（配额关闭——每写应仅 1 syscall = pwrite64）");
            return 0;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
