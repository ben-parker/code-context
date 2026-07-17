# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## ⭐ Core Principle: You Are Modifying Your Own Tool

The `codecontext` MCP tool, when connected, exposes syntax-tree and dependency-graph information about this very codebase.

- **The `codecontext` tool may be running on the same source code you are being asked to modify.**
- If the tool gives unexpected or incorrect results, it may be because **the tool itself has a bug** — that's likely what you're here to fix.
- Use the `code-context` **skill** (or the `codecontext` MCP tools when connected) as your **first step** for investigations: `codecontext.GetCompleteContext(identifier="MyClassName")` via MCP, or the skill's REST flow otherwise. Fall back to `Read`/`Grep` for implementation detail once you know which files matter, and use `codecontext.GetStatus()` to check indexing health if a search returns unexpectedly empty results.

## Commands

```bash
# Build the solution
dotnet build

# Run all tests
dotnet test

# Run a single test class or method (dotnet test --filter uses xUnit FullyQualifiedName)
dotnet test --filter "FullyQualifiedName~CSharpWorkerAnalyzerTests"
dotnet test --filter "FullyQualifiedName~CSharpWorkerProtocolFixtureTests.InitialIndex_CommitsCrossFileRelationshipsThroughProtocol"

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run the service (REST API mode), watching a given path.
# Backend defaults to inmemory (zero Python dependency); port auto-allocates from 7890.
dotnet run --project src/CodeContext.Api -- start --path /path/to/watch
dotnet run --project src/CodeContext.Api -- start --path /path/to/watch --backend kuzu   # opt-in Kuzu/Python backend

# Instance lifecycle (one instance per codebase, tracked in ~/.codecontext/instances.json)
codecontext start --detach --path /path/to/watch   # background start; prints {port,pid,...} JSON
codecontext list [--json]                          # running instances
codecontext status [--path DIR]                    # relay /api/status for the instance covering DIR
codecontext stop [--path DIR] [--all]              # graceful shutdown (POST /api/shutdown, then kill)
# Instances self-terminate after --idle-timeout minutes without API activity (default 120, 0 = never).

# Run as an MCP server (stdio transport) instead of REST
dotnet run --project src/CodeContext.Api -- start --path /path/to/watch --mcp

# Install the agent skill (agent-agnostic SKILL.md + Claude Code frontmatter)
skill/install-skill.ps1   # or skill/install-skill.sh

# Publish per-RID (the acceptance-gated path used by CI; boots + smoke-tests the packaged host)
scripts/verify-publish.ps1 -RuntimeIdentifier win-x64 -ReleaseVersion <ver> -PublishDirectory out/win-x64
# Or a plain publish (no PublishSingleFile flags — the AOT host is already a single native binary)
dotnet publish -c Release -r win-x64 -o ./publish
```

Note: the host publishes as **Native AOT** (`<PublishAot>true</PublishAot>`); language workers publish as **self-contained JIT + ReadyToRun**. See `AOT_COMPATIBILITY.md` for the full architecture and toolchain prerequisites. Several parts of the codebase (JSON source-gen context, `IsAotCompatible` on other projects) are written for AOT compatibility — don't reintroduce reflection-heavy patterns.

## Architecture Overview

CodeContext is a local background service (also usable as a one-shot CLI) that watches a source tree, parses it into a dependency graph, and exposes that graph over a REST API and/or an MCP server so LLM coding assistants can query code relationships instead of ingesting whole files. See `codecontext-prd.md` for the full product spec and API design — refer back to it whenever a change needs to fit the larger vision.

### Solution layout (`CodeContext.sln`)

- **`src/CodeContext.Api`** — Composition root. `Program.cs` wires the CLI (`System.CommandLine`): `start` (`--path`, `--port`, `--backend`, `--mcp`, `--detach`, `--idle-timeout`), plus `stop`/`list`/`status`, with handlers in `Commands/`. One binary runs either as an ASP.NET Minimal API host or as an MCP stdio server depending on `--mcp`. `ProgramHelpers.ConfigureCoreServices` wires up DI for both modes (worker catalog, services, repository factory per `--backend`, CSnakes/Python only when the backend is Kuzu, hosted `FileWatcherService`). `CodeContextEndpoints.cs` maps the REST routes; `Lifecycle/` holds the idle-shutdown machinery.
- **`src/CodeContext.Mcp`** — `CodeContextTools` exposes `GetContext`, `GetMultiContext`, `GetStatus` as `[McpServerTool]` methods, calling the same `IContextService`/`IStatusService` used by the REST layer. `ProgramHelpers.AddMcpServer` registers the stdio MCP server.
- **`src/CodeContext.Core`** — Domain models (`CodeGraph`, `CodeNode`, `CodeEdge`), the worker plumbing (`Workers/`: `WorkerCatalog` manifest discovery, `LanguageWorkerService` supervisor routing, `ParserProcessSupervisor`, `AnalysisDeltaApplier`/`IAnalysisDeltaSink`, `ParserSessionRegistry`), the repository abstractions (`Repositories/`), and the orchestration services (`Services/`). **No Roslyn** — the host never depends on a language toolchain. There is no in-process parser abstraction: all languages are parsed out-of-process by workers.
- **`src/CodeContext.Parser.Protocol`** — Canonical parser-worker protocol: JSON-RPC 2.0 + Content-Length framing over stdio, typed DTOs, source-generated JSON context, `WorkerManifest`. External workers implement `protocol/parser-protocol.schema.json`, not this assembly.
- **`src/CodeContext.CSharp.Worker`** — The C# language worker (regular JIT executable; Roslyn lives here). `CSharpWorkspaceAnalyzer` holds per-workspace syntax-tree state; deltas stream back as whole-workspace replacements with workspace-owned `csharp:` IDs. Releases publish it self-contained under `workers/csharp/`.
- **`src/CodeContext.Python.Kuzu`** — CSnakes-managed Python project. `kuzu_api.py` is the Python module that talks to the Kuzu graph database; CSnakes source-generates a `KuzuApi`/`IKuzuApi` C# class/interface from it at build time (see "Working with CSnakes" below).
- **`src/CodeContext.TypeScript.Worker`** — The TypeScript/JavaScript language worker: a persistent Node.js process (`typescript-worker.js`) speaking the parser protocol, holding one `ts.LanguageService` per workspace. Uses `<root>/tsconfig.json` compilerOptions; cross-file relationships resolve through the type checker and updates reuse unchanged snapshots while atomically replacing semantic facts. IDs include parser/workspace/file ownership. Development requires Node + `npm install`; releases bundle the target-RID Node runtime and dependencies under `workers/typescript/`.
- **`tests/CodeContext.Core.Tests`** — xUnit test suite covering parsers, both repository implementations (InMemory and Kuzu), and the context/graph-update services.

### Parser extensibility (language workers only)

Language support is added exclusively as an **out-of-process worker**: an independently-executable program that speaks the parser protocol (JSON-RPC 2.0 + Content-Length framing over stdio, `src/CodeContext.Parser.Protocol/protocol/parser-protocol.schema.json`) plus a `worker-manifest.json` discovered under `workers/<name>/` next to the host binary. There is no in-process `ILanguageParser` seam — the host never links a language toolchain.

Reference implementations: `src/CodeContext.CSharp.Worker` (Roslyn) and `src/CodeContext.TypeScript.Worker` (Node). `tests/CodeContext.FakeWorker` is the minimal protocol-conformant shim and the template for a new language worker; see `docs/language-worker-architecture-plan.md` for the manifest/protocol rules.

### Worker-owned routing

Every supported file extension is claimed by a discovered worker manifest and routes through `ILanguageWorkerService`: a change batch becomes one `workspace/applyChanges` request (scans become `workspace/index`), the worker reparses only the changed files against its cached compilation state, and streams back a complete workspace replacement as `analysis/delta` chunks. `AnalysisDeltaApplier` buffers the chunks and commits them atomically as a new **generation** through `IGenerationalGraphStore`: the commit replaces only that parser/workspace's facts (ownership via `parserId`/`workspaceId` metadata), readers keep seeing the previous complete snapshot until the swap, and stale generations are rejected. On the opt-in Kuzu backend, worker deltas fall back to `JsonReconcileDeltaSink` → `ICodeGraphRepository.ReconcileAndPruneAsync` (whole-graph JSON replacement — JSON is the process boundary to `kuzu_api.py`, and the clobber of other parsers' facts is the pre-existing Kuzu limitation).

Files whose extension has no worker are simply skipped (a `GraphUpdateService` debug log notes "No worker for extension"); scans enumerate only worker-owned extensions, so unroutable files never reach the writer in the first place.

### Index coordinator (single writer)

`IndexCoordinator` (hosted service in Core) is the **sole writer-side entry point**: the startup scan, `/api/index/refresh` (full and single-file), and `FileWatcherService` change notifications are all commands on one bounded channel processed by a single loop. Watcher events are coalesced per path over a quiet window (500ms, max 2s latency) and applied as one batch — one C# reparse per burst, and a change to one path can never cancel another path's pending work. `FileWatcherService` only produces raw notifications. Full rescans return an `operationId` (from `IScanStateService.OperationId`) observable via `/api/status`; `IdleShutdownService` holds off while `IIndexCoordinator.IsBusy`.

### Repository pattern: InMemory (default) vs. Kuzu (opt-in)

`IRepositoryFactory` abstracts node/edge/file-metadata/graph repositories; selection happens in `RepositoryServiceExtensions.AddCodeContextRepositories(BackendType)`, driven by `--backend` (or the `CODECONTEXT_BACKEND` env var) and defaulting to InMemory:
- **`InMemoryRepositoryFactory`** (`Repositories/InMemory/`) — **the default backend**. No persistence: every start performs a full rescan (accepted trade-off). Zero Python/CSnakes dependency at runtime.
- **`KuzuRepositoryFactory`** (`Repositories/Kuzu/`) — opt-in via `--backend kuzu`; delegates to `IKuzuApi` (the CSnakes-generated wrapper around `kuzu_api.py`). Python is provisioned only on this path (see the backend branch in `ProgramHelpers.ConfigureCoreServices`). Requires `InitializeAsync(rootPath)` before any repository is created (creates `.codecontext/codecontext.kuzu` under the watched root). Known debt: schema simplifications, a type-limited `GetAllAsync`, and all its tests mock `IKuzuApi` (see `KUZU_API_UPDATE_SUMMARY.md`).

When debugging "relationships are empty" type issues, check which factory is active and whether the issue reproduces with `InMemoryRepositoryFactory` before assuming the parser is at fault (see `API_ENHANCEMENT_TASKS.md` for the historical incident).

### Context assembly (`ContextService`)

`ContextService` is the engine behind both `/api/context/complete` and the MCP `GetContext` tool. Both surfaces default to the compact view: canonical identity resolution before exact-first name search, depth 1, tests/related items/metrics/content off, at most five ambiguous summaries, and at most ten entries per relationship list. Every target returns a canonical `identifier` that round-trips through the same parameter. Method `usedBy` unifies statically known interface, implementation, and override callers while `uses` remains scoped to the selected method body. `fileDependencies` and `fileDependents` aggregate semantic relationships crossing the selected source-file boundary. Test-file, test-method, relationship, and call-site caps are independent. The compact-only `relation` filter (CSV of edge kinds, e.g. `CALLS,MOCK_CALLS`) narrows `uses`/`usedBy` and makes their counts/truncation reflect the filtered totals. `directlyTested` is set when a test method statically calls the symbol or, for type targets, one of the type's members (`memberCall` evidence). `view=full` exposes parser/debug details.

### Configuration & path resolution

`CodeContextOptions` holds `RootPath`, `Backend`, `Port`, `IdleTimeoutMinutes`, and ignore patterns. What is actually implemented: CLI options (set in `ProgramHelpers.ConfigureCoreServices` via `services.Configure`), plus the `CODECONTEXT_BACKEND` env var as the `--backend` default. The PRD's fuller precedence chain (`.codecontext/config.json`, user config, etc. — `codecontext-prd.md` §10.4) is aspirational and not implemented. The Python home directory for CSnakes is resolved differently depending on whether the current directory looks like a dev build output path or a production deployment — see `ProgramHelpers.GetPythonHome`.

## Working with CSnakes

1. Put each Python library/dependency in its own project, and flag Python files as `AdditionalFiles` with `CopyToOutputDirectory` in the `.csproj` so they land next to the built assembly:
   ```xml
   <ItemGroup>
      <AdditionalFiles Include="TestFiles\hello_kuzu.py">
         <CopyToOutputDirectory>Always</CopyToOutputDirectory>
      </AdditionalFiles>
   </ItemGroup>
   ```
2. Use `uv` to manage Python dependencies (see `src/CodeContext.Python.Kuzu/pyproject.toml` / `uv.lock`), not `pip` directly.
3. The Python home passed to CSnakes must be the directory where the Python files live at runtime:
   ```csharp
   var home = GetPythonHome(); // directory containing the .py files
   builder.Services
      .WithPython()
      .WithHome(home)
      .WithVenv(Path.Join(home, ".venv"))
      .FromRedistributable(); // downloads Python 3.12 automatically
   ```
4. CSnakes source-generates a C# class + interface per Python file at build time (invisible in the project tree). `kuzu_api.py` → `KuzuApi`/`IKuzuApi`, with `snake_case` functions becoming `CamelCase` methods. If a `IKuzuApi` method you expect doesn't exist, check the corresponding Python function's name/signature first — the mapping is mechanical.

## Development Style

- **TDD**: write a failing test in `tests/CodeContext.Core.Tests/` first, confirm it fails, implement, confirm it passes, then run the full suite for regressions.
- Use xUnit + NSubstitute for mocking; don't write tests for third-party library behavior, only for how this codebase uses it.
- Domain models (`CodeNode`, `CodeEdge`, `CodeGraph`) should be rich enough to mutate their own state, but business logic belongs in `Services/`, not the models.
- Before starting any feature/bug fix/refactor, investigate all affected files (including tests, dependents, and callers) and agree on a plan before writing code — see `GEMINI.md` for the fuller planning/todo-tracking ritual this repo follows (present a plan, take clarifying questions, track work in a markdown checklist file in the repo root, get explicit approval before implementing).
- **Graph edge-kind contract**: `ContextService` consumes a fixed set of edge kinds, but those edges are produced independently by the language workers — assuming a kind no worker emits ships a silent bug (mocked tests won't catch it). Every edge kind consumed by `ContextService` (its `ContainmentEdgeKinds`/`MethodFamilyEdgeKinds`/`SemanticFileRelationshipKinds`/`FilterableRelationKinds` sets) must be covered by a worker contract test in `tests/CodeContext.Core.Tests/Workers/GraphContractTests.cs` — which runs the real Roslyn/Node workers and asserts each kind is actually emitted — or explicitly declared RESERVED (consumed defensively, no producer yet, e.g. `CONTAINS`) or SYNTHETIC (a filter alias that is never a stored edge type, e.g. `USES`). The completeness test reads those consumer sets directly (they are `internal`, exposed via `InternalsVisibleTo`), so adding a kind without a producer fails CI; `scripts/verify-publish.ps1` re-checks the same contract live against the packaged host.
