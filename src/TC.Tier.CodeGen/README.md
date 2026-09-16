# TC.Tier.CodeGen

TC.Tier 的**源生成器**（analyzer 包）——编译期生成，零运行时反射。安装后自动提供：

## 能力

- **BinaryLayout**：为标注类型生成高效的二进制序列化/反序列化代码（布局 + codec；字段偏移/
  读写/尺寸常量全部由生成物产出——手写字节序零残留）
- **WireMessage**：RPC 消息族线格式（tag 判别 + 公共前缀 + 变长成员；未知 tag 解码返回 false
  前向兼容）
- **WireArray/WireMember**：变长集合字段（[Count][item×N]，防御上限显式声明）
- **ConstantRegistry**：常量表注册（重复值编译期 Error + 区间助手——协议域五区制）
- **TierFs 协议注册桥**：引用协议程序集（如 S3）即自动注册 TierFs 介质协议，消费方零配置
- **命令壳（CommandShell，#435）**：`[CommandGroup]` 命令组一份定义生成 **CLI + HTTP 双执行面**
  （参数绑定/帮助文本/补全/退出码与状态码映射——零反射）；使用文档见 `docs/command-shell.md`
- **依赖锁分析器（E4）**：分层依赖单向铁律编译期封堵（如 Core.Net 禁引用产品层/机制面互禁）

## 安装

```bash
dotnet add package TC.Tier.CodeGen
```

包内 analyzer 在编译期自动生效（无需额外配置）；项目需配合 `TC.Tier.CodeGen.Abstractions` 中的标注特性使用。

## 文档

- 完整文档站：https://docs.mytzz.top/
- API 参考：https://docs.mytzz.top/api/
