# Phase 3 benchmarks and AOT status

Recorded 2026-07-15 on the development machine (Windows 10, Debug build, JIT host)
against this repository (124 `.cs` files + 8 TS/JS files, 132 indexed files,
~1,500 graph nodes). Numbers are single-run magnitudes, not averaged.

## Host + C# worker (out-of-process, protocol stdio)

| Metric | Value | Notes |
| --- | --- | --- |
| Detached start returns | ~1.4 s | `codecontext start --detach` prints the instance record |
| Time to `/healthz` | ~1.5 s | HTTP binds before any indexing (unchanged behavior) |
| Time to `ready` (first index) | ~4.7 s | includes C# worker spawn + handshake + full Roslyn index streamed over the protocol |
| C# worker warm-up (spawn → initialized) | ~1 s | amortized once per host lifetime; restarts budgeted by the supervisor |
| Incremental one-file change | ~2.0 s observed | includes the coordinator's 500 ms quiet window; worker reparses one file, recompiles, streams a replacement generation |

Host health is available roughly 3 seconds before the C# worker finishes its first
index — the exit-gate ordering (health before worker readiness) holds.

## Native AOT publish status

`dotnet publish -c Release -r win-x64 -p:PublishAot=true` for the host:

- Managed IL compilation completes; no Roslyn anywhere in the host closure
  (`CodeContext.Core`/`Parser.Protocol` assert this in
  `CSharpWorkerProtocolFixtureTests.HostAssemblies_HaveNoRoslynDependency`).
- The native link step fails **on this machine** because no Visual Studio C++
  toolchain is reachable (`vswhere.exe` not found for `Microsoft.NETCore.Native`).
  This is an environment gap, not a code regression.
- Remaining trim/AOT warnings to burn down before flipping `PublishAot` on for
  release: `WithOpenApi(...)`/`Results.Json(anonymous type)` in
  `CodeContextEndpoints`, and anonymous-type `JsonSerializer.Serialize` calls in
  `CodeContext.Mcp.CodeContextTools`. All are reflection-based JSON/OpenAPI usage
  that needs source-generated equivalents.
- The C# worker itself intentionally stays a JIT executable (Roslyn is not
  AOT-friendly; worker startup is amortized).

Re-run the AOT publish on a machine with the VS C++ build tools (or in CI) and
record binary size + cold start there before declaring the Phase 3 packaging
numbers final.
