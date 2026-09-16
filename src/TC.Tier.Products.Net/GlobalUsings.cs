// 项目级全局 using——组装层依赖方向：Core（并发原语/日志等）+ Core.Net（网络核心——
// spec-12 分层：身份居根、Wire/Transport/Channels/Raft/P2P/Hosting 各归其层，本项目只做组装）。
global using TC.Tier.Core.Primitives;
global using TC.Tier.Core.Net;
global using TC.Tier.Core.Net.Wire;
global using TC.Tier.Core.Net.Transport;
global using TC.Tier.Core.Net.Transport.InProcess;
global using TC.Tier.Core.Net.Transport.Tcp;
global using TC.Tier.Core.Net.Channels;
global using TC.Tier.Products.Wal;
// 子域互引用（2026-09-13 目录归类：Node/Host/Admin/RaftStore/Queue/Routing——文件夹=命名空间）
global using TC.Tier.Products.Net.Node;
global using TC.Tier.Products.Net.Host;
global using TC.Tier.Products.Net.Admin;
global using TC.Tier.Products.Net.RaftStore;
global using TC.Tier.Products.Net.Queue;
global using TC.Tier.Products.Net.Routing;
