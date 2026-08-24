using System.IO.Hashing;
using System.Runtime.InteropServices;
using TC.Tier.Contracts.Layout;
using TC.Tier.Contracts.Structures;

namespace TC.Tier.Runtime.Structures.ProbingIndex;

/// <summary>
/// 索引结构主存储帧格式——两族索引（探测/比较）共用的三段式帧格式
/// （设计稿 V2/index-persistence-evolution-design.md）。
/// <para>★ 自校验三段式（段表同款理由——写入的是外部存储就不完全相信，只认自己的写入格式）：</para>
/// <list type="bullet">
/// <item><description><b>头（先行校验，快速失败）</b>：magic/version/flags/kind + 体长——不符立即判无效，不读体；</description></item>
/// <item><description><b>体（自定界）</b>：结构内容由族自己的几何块推出边界（Hash=桶区/溢出池；BTree/SkipList=32B 几何，节点在引擎内）；</description></item>
/// <item><description><b>尾（总验收）</b>：footerMagic + 水位 W + CRC64（覆盖头+体+尾前缀）——有效性最终裁决。</description></item>
/// </list>
/// <para>★ 帧格式知识归 codec（<see cref="IProbingIndexCodec"/> 实现——子类=格式定义者），
///   本类型只承载结构定义与共享常量；源生成器（[BinaryLayout]）生成
///   ProbingIndexHeaderCodec/ProbingIndexFooterCodec。</para>
/// </summary>
public static class ProbingIndexFormat
{
    /// <summary>体几何块尺寸（族几何块统一 32B——Hash/BTree/SkipList 三家一致）。</summary>
    public const int GeometrySize = 32;

    // === 族别（Kind 字段值——体几何由族自描述）===
    public const ushort KindProbing = 0;
    public const ushort KindSorted = 1;

    /// <summary>
    /// 结构帧 header（20B）：规范字段 + kind + 体长（写头时已知——帧长可推导的格式事实）。
    /// </summary>
    [BinaryLayout(Features = BinaryLayoutFeatures.All)]
    [StructLayout(LayoutKind.Explicit, Size = HeaderSize)]
    public struct ProbingIndexHeader
    {
        public const uint Magic = RecordMagic.ProbingIndexHeader; // "IXHD"
        public const ushort CurrentVersion = (ushort)((1 << 8) | 0);   // major=1, minor=0

        public const ushort DefaultFlags = RecordFlags.FLAG_CRC64
                                         | RecordFlags.FLAG_FOOTER_MAGIC;

        private const int HeaderSize = 20;

        [FieldOffset(0), ValidEquals(Magic)] public uint MagicValue;

        [FieldOffset(4), ValidEquals(CurrentVersion)]
        public ushort Version;

        [FieldOffset(6), ValidEquals(DefaultFlags)]
        public ushort Flags;

        /// <summary>族别（KindProbing/KindSorted）——体几何解释权。</summary>
        [FieldOffset(8)] public ushort Kind;

        [FieldOffset(10)] public ushort Reserved;

        /// <summary>体长（几何块 + 结构内容——不含头尾；读侧定界）。</summary>
        [FieldOffset(12)] public long BodyLength;
    }

    /// <summary>
    /// 结构帧 footer（32B）：W + CRC64 总验收（CRC 覆盖 Header + Body + Footer 前 24B）。
    /// </summary>
    [BinaryLayout(Features = BinaryLayoutFeatures.All)]
    [StructLayout(LayoutKind.Explicit, Size = FooterSize)]
    public struct ProbingIndexFooter
    {
        public const uint FooterMagic = RecordMagic.ProbingIndexFooter; // "IXFT"

        private const int FooterSize = 32;

        [FieldOffset(0), ValidEquals(FooterMagic)] public uint Magic;

        [FieldOffset(4)] public uint Reserved;

        /// <summary>水位 W：帧内容 = record 流 [?, W) 的折叠；重放只需 (W, End)。</summary>
        [FieldOffset(8)] public LogicalAddress Watermark;

        /// <summary>CRC64（覆盖 Header + Body + Footer 前 24B）。</summary>
        [FieldOffset(24)] public ulong Crc;
    }
}
