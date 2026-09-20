# TC.Tier.CodeGen.Abstractions 协调文档

> 五层架构最底层。定义源生成器识别的**特性形状**，**并文档化源生成器的核心用法**——零依赖、零逻辑、零 abstract。
> 源生成器（`TC.Tier.CodeGen`，独立项目）靠 FQN 识别这里的特性；本文件讲"特性怎么标、生成什么、注意什么"。

---

## 1. 这个项目是什么

- **项目类型**：普通 `net8.0` 类库（不是 analyzer、不是生成器）。
- **依赖**：**无**（零 ProjectReference、零 PackageReference）——这是它在依赖链最底端的根本原因。
- **namespace**：`TC.Tier.CodeGen`（⚠️ 特殊：项目名是 `...CodeGen.Abstractions`，但 namespace 用 `TC.Tier.CodeGen`——为对齐源生成器的 FQN 识别，消费方 `global using TC.Tier.CodeGen;` 即用）。
- **内容**：21 个文件——生成器识别的全部标注/契约（`[BinaryLayout]` + `[Valid*]` 校验族 + RingKey/KvStore/WireMessage/WireArray/ConstantRegistry/协议注册桥/命令壳族），是源生成器 `TC.Tier.CodeGen` 识别标注的"形状定义"。

---

## 2. 文件清单（按生成器分组）

| 文件 | 内容 |
|------|------|
| **BinaryLayout 族** | |
| `BinaryLayoutAttribute.cs` | `[BinaryLayout(Features = BinaryLayoutFeatures.*, Endianness = LayoutEndianness.*)]`——标记 `[StructLayout]` struct 触发生成 `XxxCodec` |
| `BinaryLayoutFeatures.cs` | 生成开关 flags：`StructSize` / `FieldConstants` / `FieldReaders` / `FieldWriters` |
| `LayoutEndianness.cs` | 字节序：`LittleEndian`（缺省铁律）/ `BigEndian`（网络字节序协议——DNS 等，#498 扩展） |
| `LayoutValidationAttributes.cs` | `[ValidEquals(const)]`（== 常量，Magic/Version/Flags）——字段校验族基类 |
| `ValidHasFlagsAttribute.cs` | `[ValidHasFlags(mask)]`——位掩码包含基线位 |
| `ValidNonDefaultAttribute.cs` | `[ValidNonDefault]`——字段非默认零值 |
| `ValidRangeAttribute.cs` | `[ValidRange(min,max)]`——字段在区间内（PayloadLength 等） |
| **RingKey / KvStore 族** | |
| `RingKeyAttribute.cs` | `[assembly: RingKey(typeof(TKey))]`——封闭 Ring/索引薄类源生成入口 |
| `KvStoreAttribute.cs` | `[assembly: KvStore(...)]`——KV 组合存储源生成入口 |
| **Wire 格式族** | |
| `WireMessageAttribute.cs` | `[WireMessage]`——RPC 消息族线格式标注 |
| `WireArrayAttribute.cs` | `[WireArray]`——变长集合字段标注（[Count][item×N]） |
| **协议注册桥** | |
| `ProtocolRegistrationAttributes.cs` | 协议程序集（如 S3）自动注册 TierFs 介质协议的标注 |
| **命令壳族（CommandShell）** | |
| `CommandGroupAttribute.cs` | `[CommandGroup]`——命令组（嵌套命名空间） |
| `CommandAttribute.cs` | `[Command]`——命令方法标注 |
| `CommandArgAttribute.cs` / `CommandOptionAttribute.cs` / `CommandBodyAttribute.cs` | 位置参数 / 具名选项 / 管道 body 标注 |
| `CommandErrorAttribute.cs` | `[CommandError]`——异常 → 退出码/状态码映射 |
| `CommandResults.cs` | `ICommandResults`——结果渲染器契约 |
| `CommandHttp.cs` | `CommandHttpResponse`——HTTP 结果纯数据载体（Status/ContentType/Body） |
| **杂项** | |
| `ConstantRegistryAttribute.cs` | `[ConstantRegistry]`——常量表注册（重复值编译期 Error） |
| `IsExternalInit.cs` | record/init 支持（netstandard/net8.0 消费面） |

---

## 3. 如何使用源生成器（核心用法）

### 3.1 标记 → 生成什么

在 `[StructLayout(LayoutKind.Explicit, Size = N)]` struct 上标 `[BinaryLayout]`，源生成器（`TC.Tier.CodeGen` 的 `IIncrementalGenerator`）产出 `XxxCodec`，按 `BinaryLayoutFeatures` 可选生成：
- `StructSize`——`XxxCodec.StructSize` 常量；
- `FieldConstants`——每个字段的 `Offset_*` / `Size_*` 常量；
- `FieldReaders` / `FieldWriters`——按偏移零拷贝读写的 `Read` / `Write`；
- 校验——`Validate`（综合字段校验特性）；
- **默认值——`Create()`**（2026-08-24 新增）：`[ValidEquals(const)]` 字段自动填常量（默认值 = 约束常量，无需独立 DefaultValue 特性——双声明重复）。写侧范式：`var h = XxxCodec.Create(); h.变化字段 = ...; XxxCodec.Write(dest, in h)`——规范字段（Magic/Version/Flags）零手填。
- **字节序**（2026-09-20 新增——#498）：`Endianness = LayoutEndianness.BigEndian` 生成物逐字段大端（`BinaryPrimitives.*BigEndian`）——网络字节序协议（DNS RFC 1035 等）声明即用；缺省恒小端（既有铁律不变）。

> 这**不是反射/runtime 校验**——是编译期源生成，零运行时开销、AOT 友好。

### 3.2 字段校验特性（故意不用 `System.ComponentModel.DataAnnotations`）

struct 不可 null、`Required` 对值类型语义不对，且 DataAnnotations 面向反射式运行时校验——故自造校验特性（见 `LayoutValidationAttributes.cs`）：

| 特性 | 语义 | 典型用途 |
|------|------|----------|
| `[ValidEquals(const)]` | 字段 == 常量（**默认值同此常量——生成 `Create()`**） | Magic / Version / Flags |
| `[ValidRange(min,max)]` | 字段在区间内 | PayloadLength |
| `[ValidHasFlags(mask)]` | 位掩码包含基线位（运行时可叠加动态位） | Flags 位基线 |
| `[ValidNonDefault]` | 字段非默认零值 | PageId 等必填 |

> ★ `Write(..., validate: true)` 语义 = **防御性补全**（非抛异常）：`[ValidEquals]` 字段不信任入参、强制写常量——调用方可传 `default`（如 `MetaPolicy.WriteHeader(default)`），布局层保证规范字段合法。`Validate(in h)` 保留纯检查语义（读侧/外部验证）。

### 3.3 诊断

- **TCSG001**：`[StructLayout].Size` 与字段偏移和不符。
- **TCSG002**：嵌套 struct 字段未标 `[BinaryLayout]`。

### 3.4 支持的字段类型

基元 + 同底层 enum + 嵌套 `[BinaryLayout]` struct。

### 3.5 范式

```csharp
[BinaryLayout(Features = BinaryLayoutFeatures.StructSize | BinaryLayoutFeatures.FieldConstants)]
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct MyHeader
{
    [FieldOffset(0)] [ValidEquals(MyHeader.Magic)]  public uint   MagicValue;
    [FieldOffset(4)] [ValidRange(0, MaxEntrySize)]  public ushort PayloadLength;
    [FieldOffset(6)] [ValidHasFlags(DefaultFlags)]   public ushort Flags;
    [FieldOffset(8)] [ValidNonDefault]               public ulong  PageId;
}
// 编译期生成 MyHeaderCodec（Write/Read/Validate/StructSize/Offset_*/Size_*）
```

### 3.6 ⚠️ 注意事项

- 标记点的 struct **必须** 同时有 `[StructLayout(LayoutKind.Explicit, Size = N)]`——`Size` 必须准确（否则 TCSG001）。
- 嵌套 struct 字段必须也标 `[BinaryLayout]`（否则 TCSG002）。
- **谁会标记**：当前标记点分布在 `TC.Tier.Contracts`（`LogicalAddress` / `Crc32Footer` / `Crc64Footer` / `RecordFlags`）与 `TC.Tier.Core`（`TierVolumeFrameLayouts`——TierVolume 帧布局族）。新增布局 struct 就近放所属程序集并标记即可。
- **验证生成生效**：编译后查 `obj/.../generated/TC.Tier.CodeGen/TC.Tier.CodeGen.BinaryLayoutGenerator/*.g.cs` 有产物；缺失说明消费项目的 Analyzer 引用（`TC.Tier.CodeGen` 以 `OutputItemType=Analyzer` 引用）未配置。

---

## 4. 为什么独立成项目（铁律）

1. **生成器零引用**：`TC.Tier.CodeGen` 生成器项目不引用任何项目（否则循环依赖），只靠 FQN 识别特性。特性若放在某个被引用的项目里，生成器就得引用它 → 破坏"生成器纯 FQN"原则。
2. **Contracts 零业务依赖**：Contracts 层需要用 `[BinaryLayout]`（标 `LogicalAddress`/`CrcFooter`/`RecordFlags`），但不能引 Core。特性独立成最底层项目后，Contracts 只引本抽象层 → 依赖链无环。
3. **namespace 与生成器 FQN 对齐**：生成器 `BinaryLayoutGenerator.cs` 内 `CodeGenNamespace = "TC.Tier.CodeGen"`。本项目 namespace 必须与之**完全一致**，否则生成器识别不到特性。

---

## 5. 依赖链位置

```
TC.Tier.CodeGen.Abstractions   ← 你在这里（零依赖，最底端）
        ↑
TC.Tier.Contracts              （标 [BinaryLayout] 于 LogicalAddress/CrcFooter/RecordFlags）
        ↑
TC.Tier.Core → TC.Tier.Runtime → TC.Tier.Products
```

- 谁引用它：`Contracts`（直接）、`Core`/`Runtime`/`Products`/Tests/Benchmarks（经 transitive，并以 `global using TC.Tier.CodeGen;` 兜底）。
- 改它 = 动整条链的地基，必须全解重新编译，并确认源生成器仍触发（`generated/*.g.cs` 有产物）。
