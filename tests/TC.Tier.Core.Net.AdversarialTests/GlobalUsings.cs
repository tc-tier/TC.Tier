// Core.Net 对抗性测试全局 using（spec-12 §1 五层塔 + 机制面——测试消费全部层）。
global using Xunit;
global using FluentAssertions;
global using TC.Tier.Core.Net;
global using TC.Tier.Core.Net.Wire;
global using TC.Tier.Core.Net.Transport;
global using TC.Tier.Core.Net.Transport.InProcess;
global using TC.Tier.Core.Net.Transport.Tcp;
global using TC.Tier.Core.Net.Channels;
global using TC.Tier.Core.Net.Raft;
global using TC.Tier.Core.Net.Tests.Fixtures;   // FsRaftStore（链接共享——单一真源）
