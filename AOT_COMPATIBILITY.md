# AOT & Startup Architecture

## Final decision (Phase 4, .NET 10)

CodeContext ships as **two processes with two different compilation strategies**:

| Process | Strategy | Why |
|---|---|---|
| **Host** (`codecontext`, `src/CodeContext.Api`) | **Native AOT** (`PublishAot=true`) | Spawned once per repository — cold start is on the critical path. AOT emits a single native binary with no JIT warm-up and no managed assemblies beside it. |
| **C# worker** (`src/CodeContext.CSharp.Worker`) | **Self-contained JIT + ReadyToRun** (`PublishReadyToRun=true`) | Hosts Roslyn, whose AOT support is officially unsupported upstream. The worker is long-lived (one process per session), so its cold start amortizes; R2R pre-JITs it (and Roslyn) to native images to shrink that one-time cost, and tiered JIT + PGO still recompile hot paths above the R2R baseline. |

The host **never references Roslyn** — the worker is a separate executable reached only over the parser protocol (JSON-RPC + Content-Length framing over stdio). That process boundary is what makes host AOT viable: the un-AOT-able compiler never links into the AOT image.

## What was required to make the host AOT-clean

Native AOT forbids reflection-based serialization, runtime code generation, and reflection-driven discovery. The following were done across Phases 2–4 to reach zero trim/AOT analyzer warnings before flipping `PublishAot=true`:

- **Source-generated JSON everywhere.** Host, protocol, and worker serialize exclusively through `System.Text.Json` source-generated contexts (`CodeContextJsonContext`, `McpJsonContext`, protocol contexts). No `JsonSerializer` overload takes a runtime `Type`.
- **Typed DTOs, no anonymous objects.** Every REST error/response and every MCP payload is a declared record registered in a source-gen context. `Dictionary<string,object>` shapes were replaced with concrete types.
- **Manual MCP tool catalog.** The MCP SDK's reflection-based `.WithTools<T>()` discovery was replaced with an explicit `McpToolCatalog` (`WithListToolsHandler`/`WithCallToolHandler`, tool `InputSchema` parsed from const JSON literals). Attribute-scanning discovery is gone.
- **OpenAPI compile-gated out of Release.** `Microsoft.AspNetCore.OpenApi` code compiles only under the Debug-only `ENABLE_OPENAPI` constant (transformers live in `OpenApiSupport.cs`, whole-file `#if`). Release/AOT binaries carry no OpenAPI code and none of its `Assembly.GetExecutingAssembly()` reflection. (The packages are still referenced unconditionally for restore determinism; nothing roots them in Release, so AOT trims them out — the `verify-publish.ps1` no-managed-DLL assertion is the permanent guard that they never leak into the shipped payload.)
- **Removed the in-process parser seam** (`ILanguageParser`), including a `GetType().Name` reflection path in `StatusService`. Workers are the only extension mechanism.
- **Slim host builder.** REST mode uses `WebApplication.CreateSlimBuilder` (localhost HTTP only); MCP mode keeps `Host.CreateApplicationBuilder` for stdio.

## InvariantGlobalization

The host sets `InvariantGlobalization=true`, dropping ICU/culture data from the AOT image. This is safe because the host does only ordinal / invariant string work: identity resolution and edge-kind matching are ordinal, and the sole user-facing numeric/date formats use round-trip (`"O"`) or explicit `CultureInfo.InvariantCulture`. The Api + Core sources were audited for culture-sensitive `ToLower`/`ToUpper`/`string.Compare`/`ToString(format)` before enabling it; the one implicit-culture format (`StatusService` memory MB) was pinned to `InvariantCulture`.

## Build configuration

Host (`src/CodeContext.Api/CodeContext.Api.csproj`):

```xml
<PublishAot>true</PublishAot>
<OptimizationPreference>Speed</OptimizationPreference>
<InvariantGlobalization>true</InvariantGlobalization>
<!-- StripSymbols only on non-Windows RIDs (Windows emits a separate .pdb). -->
```

Worker (`src/CodeContext.CSharp.Worker/CodeContext.CSharp.Worker.csproj`):

```xml
<PublishReadyToRun Condition="'$(RuntimeIdentifier)' != ''">true</PublishReadyToRun>
<TieredCompilation>true</TieredCompilation>
<TieredPGO>true</TieredPGO>
<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>
<ServerGarbageCollection>false</ServerGarbageCollection>  <!-- workstation GC: low per-repo memory -->
```

`PublishAot` only takes effect on `dotnet publish -r <rid>`; plain `dotnet build` / `dotnet test` stay JIT, so the test suite runs against the same IL the analyzers vet.

## Toolchain prerequisite

A local Native AOT publish needs the C++ link toolchain: **Visual Studio 2022+ with the "Desktop development with C++" workload** (VS 2026 verified for this repo; CI `windows-latest` already carries it). Without it, ILC fails at the link step.

VS 2026 gotcha (Windows local publish): its `vcvarsall.bat` invokes a bare `vswhere.exe`, so the VS Installer directory (`C:\Program Files (x86)\Microsoft Visual Studio\Installer`) must be on `PATH`. If it is not, `dotnet publish -r <rid>` fails at the ILC link step with `MSB3073` — vswhere's "not recognized" stderr corrupts the linker-path property ILC computes. Add that directory to `PATH` before publishing. CI `windows-latest` already has it on `PATH`, so no workflow change is needed.

## Publishing

```bash
# The one supported host publish (single native binary + self-contained JIT/R2R workers):
scripts/verify-publish.ps1 -RuntimeIdentifier win-x64 -ReleaseVersion <ver> -PublishDirectory out/win-x64
```

`verify-publish.ps1` is the AOT acceptance gate: it boots the published binary, waits for `indexing.status=ready`, asserts `contractVersion=1` and all 12 contracted graph edge kinds are emitted by the real workers, checks the packaged skill hash, and asserts **no managed `*.dll` sits next to the host binary** (workers ship their DLLs under `workers/csharp/**` and `workers/typescript/**`).
