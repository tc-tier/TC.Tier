# TC.Tier

> A quick-to-start, composable high-performance storage runtime for .NET.

English · [中文](README.md)

## About

TC.Tier is a high-performance storage runtime kernel, **implemented from scratch in pure C#, with a composable architecture**. It is built entirely on modern data-structure papers, OS storage methodology, and layered engineering practices — no third-party kernel black boxes, fully controllable, auditable, and AOT-native compilable.

This is a personal-interest engineering project focused on the engineering delivery and technical validation of a low-level storage kernel. It is not a commercial product, nor a general-purpose open-source service project.

## Project Status (Important)

- ✅ **Core Runtime is complete** — memory layout, lock-free concurrency primitives, spin locks, 128-bit atomic operations, segmented storage, segment table management, WAL logging, DirectIO raw-device I/O, multi-backend storage abstraction, cross-platform serialization, and a SourceGenerator compile-time generation system. All have undergone extensive local stress testing, optimization, and stability verification.
- ⚠️ **The overall version is in Beta** — upper-layer standardized application products (KV, queue, time-series, etc.) are still being assembled and finalized; the on-disk persistent binary format may change. Do not use it for production environments, core business data, or important persistent data.
- All implementations are engineering validation in nature, tested only by local benchmarks and stress tests; production-grade stability across all scenarios and environments is not guaranteed.

## Open-Source Positioning

1. The code is fully open source under the MIT license — free to read, learn, reference, and modify.
2. No commercial use, no promotion, no traffic funneling, no private services.
3. All designs come from public academic papers, open-source methodologies, and standard engineering practices — no closed proprietary technology.

## Communication & Q&A Rules

1. The only communication channel: GitHub [Issues](https://github.com/tc-tier/TC.Tier/issues) / [Discussions](https://github.com/tc-tier/TC.Tier/discussions).
2. No WeChat, no email, no personal contact information — private consultations are not accepted.
3. Q&A is voluntary and not obligatory; response time is not guaranteed, and some questions may be ignored or closed.
4. Priority is given to source design, architecture, engineering implementation, and technical questions; beginner onboarding, deployment, production adaptation, and custom feature requests may go unanswered.

Genuine questions, suggestions, and criticism are all welcome — we just don't promise to reply.

## License & Risk Statement

- This project is licensed under the MIT License.
- Users bear all risks of use; the author assumes no responsibility for data loss, failures, compatibility, or stability.
- In Beta, no version compatibility, format compatibility, or feature stability is promised.

## Highlights

- **Pure managed C# + Unsafe self-built kernel** — zero native black-box dependencies
- **SourceGenerator compile-time serialization** — full NativeAOT support, zero reflection
- **16-byte unified logical address space** — segment-spanning, address-as-identity; equality and hashing compare the address itself
- **Composable storage model** — index (hash / B+ tree / skip list) · Ring · Log building blocks combined on demand
- **Explicit memory layout** — field alignment, unified cross-platform endianness
- **Lock-free concurrency** — spin RW locks, sharded locks, 128-bit atomic CAS, lock-free queues
- **Unified storage abstraction** — memory / file DirectIO / raw device / S3 object storage with seamless switching
- **Self-built segmented storage engine** — automatic compaction, WAL crash recovery, logical address resolution
- **Full benchmark suite** — reproducible, comparable, auditable

## Suitable Scenarios

- Learning and source reference for .NET low-level storage kernels
- Research on high-performance, low-GC, lock-free architecture engineering
- Embedded storage foundation for personal / experimental projects
- Technical reference for custom middleware and self-built components

Not suitable for any production business or persistent core data. Once the upper-layer standard products are finalized, the on-disk storage format will be frozen and iterated into a stable V1.0 release. For production use, wait for the v1.0 stable release, or complete your own fault-injection and stress-testing validation first.

## Quick Start

```csharp
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.IO;
using TC.Tier.Runtime.Storage;

// ① Pick a file system — switch by URI, code unchanged
using var vol = TierFs.New("memory:");

// ② Three-stage assembly: Options → Builder → StartAsync (includes recovery, async-first)
await using var engine = await new StorageEngineOptions("demo").Builder(vol).StartAsync();

// ③ Write & read back: AppendAsync sequential append (WAL semantics), WriteAsync in-place rewrite (KV semantics)
ReadOnlyMemory<byte> payload = "hello, tier!"u8;
var addr = await engine.AppendAsync(payload, CancellationToken.None);   // returns the starting logical address
await engine.WriteAsync(addr, payload, CancellationToken.None);         // in-place rewrite at the same address
var buf = new byte[payload.Length];
var n = await engine.ReadAsync(addr, buf, CancellationToken.None);      // read back
```

More examples of building custom storage models with indexes / Ring / Log: [usage docs](https://docs.mytzz.top/docs/src/TC.Tier.Runtime/docs/storage-engine.html).

## Installation

```bash
# Runtime package (currently beta; includes Core / Contracts dependencies)
dotnet add package TC.Tier.Runtime --prerelease
```

Stable packages (v1.0.x): `TC.Tier.Contracts`, `TC.Tier.Core`, `TC.Tier.CodeGen`, `TC.Tier.Core.IO.S3` (S3 network file system implementation).

## Performance

Measured on .NET 8 across Windows (i5-12400) and Linux (AMD 6900HX) — **for reference only**; your actual results depend on your hardware and workload. Full methodology and details in the [performance docs](https://docs.mytzz.top/docs/src/TC.Tier.Runtime/docs/perf/storage-engine-perf-baseline.html).

| Scenario | Result |
|---|---|
| Steady-state rewrite 64B (single thread) | 532 ns/op |
| Rewrite 64B · 4 threads | ~176 ns/op (~3.1× speedup) |
| Large rewrite 64KB | 11.5 GB/s |
| Point lookup (Hash index) | 158 ns; batch 94.7 ns (zero allocation) |
| Log recovery | 500k entries ~9 ms |
| Ring concurrent batch writes (8 writers) | 6.07M op/s (3.0× over single writer) |

---

## Architecture

```
Official products (planned): TierKV / TierWAL / TierBlob / TierQueue / TierTimeSeries
────────────────────────────────────────────────
Storage Runtime (composable kernel)
  Index (Hash/BTree/SkipList) · Ring · Log · Blob (metadata/mirror/snapshot)
────────────────────────────────────────────────
Storage Engine (Options → Builder → Start/StartAsync, 16B logical address space)
────────────────────────────────────────────────
File system layer (local:// / memory: / virtual:// / network:///s3)
```

Dependencies are one-way and acyclic: `TC.Tier.CodeGen.Abstractions` → `TC.Tier.Contracts` → `TC.Tier.Core` → `TC.Tier.Runtime` → `TC.Tier.Products`. The source generator (`TC.Tier.CodeGen`) cuts across — BinaryLayout / registration bridges are generated at compile time, zero runtime reflection.

- Engine guide: [storage-engine.md](https://docs.mytzz.top/docs/src/TC.Tier.Runtime/docs/storage-engine.html)
- Structures overview: [structures.md](https://docs.mytzz.top/docs/src/TC.Tier.Runtime/docs/structures.html)
- Lifecycle model: [lifecycle.md](https://docs.mytzz.top/docs/src/TC.Tier.Core/docs/lifecycle.html)

---

## Documentation

- **Online doc site**: [docs.mytzz.top](https://docs.mytzz.top/) — usage docs, performance reports, API reference, full-text search
- **Source repository**: [github.com/tc-tier/TC.Tier](https://github.com/tc-tier/TC.Tier) — code, Issues, Discussions

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) — build, test conventions, code standards (enforced at compile time: no reflection TCSG030 / no sync-over-async TCSG031).

## License

MIT — see [LICENSE](https://github.com/tc-tier/TC.Tier/blob/main/LICENSE).

Core implementation is entirely self-built; algorithm attribution and third-party dependency notes: [THIRD-PARTY-NOTICES](https://github.com/tc-tier/TC.Tier/blob/main/THIRD-PARTY-NOTICES).
