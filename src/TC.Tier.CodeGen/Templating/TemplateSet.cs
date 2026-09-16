using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TC.Tier.CodeGen.Templating;

/// <summary>
/// 内嵌模板资源加载器（Templates/**/*.sbn 以 EmbeddedResource 打进 analyzer dll）。
/// <para>★ 键 = 资源路径去 "TC.Tier.CodeGen.Templates." 前缀、去 .sbn 后缀、'.'→'/'（如 "BinaryLayout/CodecClass"）。</para>
/// <para>★ Lazy 一次性加载为只读字典——Roslyn 生成器跨编译并发运行，静态状态必须不可变。</para>
/// <para>★ 零 BCL 文件族（仓库架构纪律）：资源流经 MemoryStream 读入后 UTF8 解码
///   （ZeroBclIoRepositoryArchitectureTests 的词表禁止 BCL 流式读取器构造）。</para>
/// </summary>
internal static class TemplateSet
{
    private const string TemplatesPrefix = ".Templates.";

    private static readonly System.Lazy<IReadOnlyDictionary<string, string>> Templates =
        new(Build);

    private static Dictionary<string, string> Build()
    {
        var assembly = typeof(TemplateSet).Assembly;
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            var idx = resourceName.IndexOf(TemplatesPrefix, StringComparison.Ordinal);
            if (idx < 0 || !resourceName.EndsWith(".sbn", StringComparison.Ordinal)) continue;
            var stemStart = idx + TemplatesPrefix.Length;
            var key = resourceName.Substring(stemStart, resourceName.Length - 4 - stemStart).Replace('.', '/');
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new System.InvalidOperationException($"内嵌模板资源缺失：{resourceName}");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var text = Encoding.UTF8.GetString(buffer.ToArray());
            if (text.Length > 0 && text[0] == '\uFEFF')
                text = text.Substring(1);   // 编辑器误加 BOM 防护——BOM 会破坏模板首行
            // 行尾归一（autocrlf 检出 CRLF 会混入生成输出——golden 逐字节比对要求 LF 跨机器稳定）
            text = text.Replace("\r\n", "\n");
            map[key] = text;
        }
        return map;
    }

    /// <summary>取模板文本；缺失即抛——fail-fast，生成器内部缺陷立刻暴露。</summary>
    /// <param name="name">模板键（资源路径去 "TC.Tier.CodeGen.Templates." 前缀、去 .sbn 后缀、'.'→'/'）。</param>
    /// <returns>模板文本（BOM 去除、行尾已归一为 LF）。</returns>
    public static string Get(string name) =>
        Templates.Value.TryGetValue(name, out var template)
            ? template
            : throw new System.InvalidOperationException(
                $"模板 '{name}' 不存在（已加载：{string.Join(", ", Templates.Value.Keys)}）。");
}
