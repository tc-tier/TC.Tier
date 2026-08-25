# TC.Tier

> An out-of-the-box, composable high-performance storage runtime for .NET.

English · [中文](README.md)

> ⚠️ **Project Statement**
>
> This project is a personal-interest, self-developed engineering validation implementation. The code is published for reference and learning purposes only.
>
> - Communication is limited to GitHub [Issues](https://github.com/tc-tier/TC.Tier/issues) / [Discussions](https://github.com/tc-tier/TC.Tier/discussions); no other private channels (WeChat, email, etc.) are provided.
> - Responses are voluntary and not obligatory; no response time is guaranteed, and some questions may go unanswered.
> - The project is overall in Beta; upper-layer products are not yet finalized, and the on-disk storage format may change. Do not use it with production data.
> - The implementation draws on public academic papers and modern compositional design ideas and has undergone extensive local stress testing; cross-environment production stability is not guaranteed.
> - MIT License. Users bear all risks of use.

TC.Tier offers storage at two levels:

- **Ready to use** — the `StorageEngine` provides in-place rewrite (KV semantics), sequential append (WAL semantics), Blob large-value separation, and mirror/snapshot out of the box.
- **Composable** — build your own storage model on a unified 16-byte logical address space, combining index (hash / B+ tree / skip list), Ring, Log, and other building blocks.

The project is evolving quickly: the runtime package is currently in beta and APIs may change. Bug reports, feature requests, and even criticism are all welcome via [Issues](https://github.com/tc-tier/TC.Tier/issues) or [Discussions](https://github.com/tc-tier/TC.Tier/discussions).

---

## Highlights

- **16-byte logical address space** — segment-spanning, reusable without truncation; equality and hashing compare the address itself
- **Two write models** — Mode A: pre-allocate + in-place rewrite (KV semantics); Mode B: sequential append (WAL semantics)
- **Pluggable indexes** — hash / B+ tree / skip list under one abstraction
- **Concurrency-friendly** — lock-free reads; non-overlapping regions can be written in parallel (~3.1× scaling on 4 threads in our measurements)
- **Native C#, zero reflection** — source generators replace reflection; hot paths use native memory; NativeAOT-compatible
- **One abstraction, four file systems** — local (`local://`, Direct IO), in-memory (`memory:`), virtual (`virtual://`, .raw-backed), network (`network:///s3`, S3 protocol contract); switch via the `TierFs` factory with no code changes
- **Background compaction** — whole-segment moves run in the background without blocking reads/writes, and resume automatically on failure

---

## Install

```bash
# Runtime package (currently beta; pulls in Core / Contracts)
dotnet add package TC.Tier.Runtime --prerelease
```

Stable packages (v1.0.x): `TC.Tier.Contracts`, `TC.Tier.Core`, `TC.Tier.CodeGen`, `TC.Tier.Core.IO.S3` (network file system on S3).

## Quick start

```csharp
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.IO;
using TC.Tier.Runtime.Storage;

// ① Pick a file system — swap the URI and the code stays the same
using var vol = TierFs.New("memory:");

// ② Three-step assembly: Options → Builder → StartAsync (includes recovery; async-first)
await using var engine = await new StorageEngineOptions("demo").Builder(vol).StartAsync();

// ③ Write & read back: AppendAsync is sequential (WAL semantics), WriteAsync rewrites in place (KV semantics)
ReadOnlyMemory<byte> payload = "hello, tier!"u8;
var addr = await engine.AppendAsync(payload, CancellationToken.None);   // returns the starting logical address
await engine.WriteAsync(addr, payload, CancellationToken.None);         // rewrite the same address in place
var buf = new byte[payload.Length];
var n = await engine.ReadAsync(addr, buf, CancellationToken.None);      // read back
```

More examples of composing index / Ring / Log into custom storage models: [usage docs](https://docs.mytzz.top/docs/src/TC.Tier.Runtime/docs/storage-engine.html).

---

## Performance

Measured locally on Windows with .NET 8 — **for reference only**; real numbers depend on your hardware and workload. Full methodology and details: [performance docs](https://docs.mytzz.top/docs/src/TC.Tier.Runtime/docs/perf/storage-engine-perf-baseline.html).

| Scenario | Result |
|---|---|
| Steady in-place rewrite, 64B (single thread) | 532 ns/op |
| Rewrite 64B, 4 threads | ~176 ns/op (~3.1× scaling) |
| Large rewrite 64KB | 11.5 GB/s |
| Point lookup (hash index) | 158 ns/op; ~94.7 ns/op batched (zero allocation) |
| Log recovery | ~9 ms per 500K records |
| Ring batched concurrent writes (8 writers) | 6.07M op/s (3.0× vs single writer) |

---

## Architecture

```
Official products (planned): TierKV / TierWAL / TierBlob / TierQueue / TierTimeSeries
────────────────────────────────────────────────
Storage Runtime (composable kernel)
  Index (Hash/BTree/SkipList) · Ring · Log · Blob (metadata/mirror/snapshot)
────────────────────────────────────────────────
Storage Engine (Options → Builder → Start/StartAsync; 16-byte logical address space)
────────────────────────────────────────────────
File system layer (local:// / memory: / virtual:// / network:///s3)
```

Acyclic dependency chain: `TC.Tier.CodeGen.Abstractions` → `TC.Tier.Contracts` → `TC.Tier.Core` → `TC.Tier.Runtime` → `TC.Tier.Products`. The source generator (`TC.Tier.CodeGen`) is cross-cutting — BinaryLayout and registration bridges are generated at compile time, with zero runtime reflection.

- Engine guide: [storage-engine.md](https://docs.mytzz.top/docs/src/TC.Tier.Runtime/docs/storage-engine.html)
- Structures overview: [structures.md](https://docs.mytzz.top/docs/src/TC.Tier.Runtime/docs/structures.html)
- Lifecycle model: [lifecycle.md](https://docs.mytzz.top/docs/src/TC.Tier.Core/docs/lifecycle.html)

---

## Documentation

Usage docs and performance reports live at [docs.mytzz.top](https://docs.mytzz.top/) — API reference plus full-text search.

## Feedback

- **Issues** — bug reports and feature requests: [tc-tier/TC.Tier/issues](https://github.com/tc-tier/TC.Tier/issues)
- **Discussions** — usage questions, architecture discussions, anything: [tc-tier/TC.Tier/discussions](https://github.com/tc-tier/TC.Tier/discussions)

The project is still young — questions, suggestions, and criticism are all genuinely welcome.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) — build & test conventions, code style (enforced at compile time: no reflection TCSG030 / no sync-over-async TCSG031).

## License

MIT — see [LICENSE](https://github.com/tc-tier/TC.Tier/blob/main/LICENSE).

The core implementation is self-contained; algorithm attribution and third-party notices: [THIRD-PARTY-NOTICES](https://github.com/tc-tier/TC.Tier/blob/main/THIRD-PARTY-NOTICES).
