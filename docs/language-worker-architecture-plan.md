# CodeContext language-worker architecture plan

Status: accepted direction, revised against the working tree on 2026-07-15.

This document is the durable implementation plan for separating language analysis from the CodeContext host while preserving the new per-repository lifecycle and agent-skill workflow. It supersedes the earlier idea of loading parser packages directly into the Native AOT process.

## Outcome

CodeContext will use one lightweight host process per watched repository. That host owns the HTTP API, instance lifecycle, file-change coordination, normalized in-memory graph, and JSON responses. Language tooling runs in long-lived child processes behind a versioned stdio protocol.

```text
agent skill
    |
    v
global instance registry -> repository host (AOT target)
                                |
                 +--------------+----------------+
                 |              |                |
             C# worker      TS/JS worker     Python worker
             Roslyn/JIT     Node + TS API    Python tooling
                 |              |                |
                 +-------- normalized deltas ----+
                                |
                                v
                    indexed in-memory GraphStore
                                |
                                v
                          REST and MCP reads
```

The runtime extension seam is an executable protocol, not a .NET assembly interface. A parser may be distributed in a NuGet, npm, PyPI, or release archive, but the host interacts with its executable and manifest only.

## Working-tree baseline

The changes made before this plan are valuable and should be retained as the baseline:

- In-memory storage is the default and matches the intended one-process/one-repository model.
- The global `InstanceRegistry`, automatic port selection, idempotent detached start, path-to-instance resolution, stop/list/status commands, and idle shutdown establish the correct outer lifecycle.
- HTTP liveness is decoupled from indexing readiness. Initial indexing running after the host binds is exactly what the worker architecture needs.
- `/api/status`, `/healthz`, `/api/shutdown`, guarded full refresh, and real scan state establish the control surface used by the agent skill.
- Resilient enumeration that skips reparse points and inaccessible directories should remain shared host behavior. Workers should receive an approved file/workspace set and should not each reinvent repository crawling.
- The skill's grep-versus-CodeContext cost model and its instruction to leave instances running are consistent with amortizing long-lived parser workers.
- The release/install work establishes install-once distribution and a natural location for bundled worker assets.

Some current mechanisms are transitional:

| Current mechanism | Disposition |
| --- | --- |
| `ILanguageParser` | Removed (July 2026, .NET 10 upgrade Phase 2). No production parser used it; the out-of-process worker protocol is now the sole extension seam. |
| Concrete parser registration in `ProgramHelpers` | Replace with manifest/client registration. |
| C# special cases and `OfType<CSharpParser>` in `GraphUpdateService` | Replace with worker capabilities and workspace sessions. |
| Node process per TypeScript file | Replace with a persistent project-aware TypeScript worker. |
| `IRepositoryFactory` on the in-memory hot path | Keep during migration; replace with typed read/write graph-store contracts. |
| Kuzu/CSnakes runtime branch | Accept as an interim opt-in; remove from the default host dependency and release payload before calling the host AOT-clean. |
| Global scan state | Preserve its public `scanning`/`ready`/`error` contract; make it coordinator-driven and add per-parser details. |
| Host instance registry | Keep for hosts only. Parser children must never register as independent CodeContext instances. |

## Important findings from the current implementation

These should be addressed as part of the migration rather than allowed to become worker-protocol bugs.

1. `FileWatcherService` has one debounce timer for the entire repository. A change to one path can cancel a pending change for another path, and the `async void` handler provides no ordering or backpressure.
2. Initial scan, full refresh, single-file refresh, and watcher callbacks can enter `GraphUpdateService` concurrently. Its current file-content dictionary, parser state, and full-graph reconciliation are not a safe concurrency boundary.
3. Full refresh calls `PerformInitialScanAsync` without the progress reporter, so the new `filesProcessed` and `filesTotal` fields remain zero during that operation.
4. A full refresh clears the live graph before rebuilding it. API readers can therefore see empty or partially rebuilt results. The last complete generation should remain queryable until the next generation commits.
5. `/api/status` currently reads and groups all nodes, edges, and file metadata. The agent skill polls this endpoint every one or two seconds during indexing, so status should use maintained counters and remain effectively O(1).
6. Parser status is hard-coded, reports Python as available, and omits the registered TypeScript parser. Worker handshake/session state should be its only source of truth.
7. The in-memory reconcile path serializes the complete graph to JSON and immediately deserializes it before clearing and repopulating dictionaries. JSON should not appear inside the in-memory repository contract.
8. `InstanceRegistry` uses atomic replacement but not a cross-process lock around its load-modify-save sequence. Concurrent registration or shutdown can lose another process's update. PID name checks also cannot reliably distinguish a reused PID from the original instance.
9. The release currently includes and verifies the Python/Kuzu payload even though Kuzu is future/opt-in. The Windows installer selects `win-arm64`, but the release matrix does not produce that RID.
10. The primary TypeScript parser tests are excluded from the release test gate. Worker extraction should replace broad exclusions with worker-specific unit, protocol-conformance, and packaging smoke tests.

## Lifecycle reconciliation

The detached repository host becomes the worker supervisor.

- `codecontext start --detach` continues to start exactly one registered process for the selected root.
- The host starts only the workers needed by files/project markers found under that root.
- Start HTTP first, then discover projects and warm workers in the background.
- Use one worker process per parser type per host initially. The protocol supports multiple logical `workspaceId` sessions so a monorepo can contain several `.csproj`, `tsconfig.json`, or Python package roots without immediately creating a process per project.
- Worker stdin/stdout are private protocol pipes; worker stderr is captured into the host log with parser/session prefixes.
- Graceful stop, explicit shutdown, and idle shutdown cancel the coordinator, request worker shutdown, close protocol pipes, and wait briefly before killing the process tree.
- Workers must exit when protocol stdin reaches EOF. This makes a host crash self-cleaning on every platform; a Windows Job Object or Unix process-group guard can be added as defense in depth.
- The registry contains host identity only. Worker PID, health, restart count, and version belong in `/api/status`.
- Idle shutdown must not fire while an index generation or API request is active. Background work should contribute a busy lease; status polling should not be required to keep a long scan alive.

Before depending on the registry as the supervisor authority, add:

- a cross-process lock for registry transactions;
- a random `instanceId` and process start-time fingerprint in each record;
- identity validation before `status`, graceful stop, or PID kill, preventing a stale record/port/PID from targeting an unrelated process;
- an optional local control token for `/api/shutdown`, or at minimum an instance-ID requirement.

## Parser protocol and normalized IR

Create a canonical JSON schema plus source-generated C# DTOs. External workers implement the schema, not a shared CLR assembly.

Transport: JSON-RPC 2.0 with `Content-Length` framing over stdin/stdout. This is portable, avoids worker ports and authentication, and supports streaming result chunks. Parsing cost dominates the local JSON framing cost; optimize the encoding only after measurement.

Required operations:

- `initialize`: protocol negotiation, host/root information, configuration, supported schema range;
- `openWorkspace`: logical workspace/project markers and approved roots;
- `indexWorkspace`: start or replace a complete workspace generation;
- `applyChanges`: ordered created/changed/deleted/renamed batches;
- `cancel`: cooperative request cancellation;
- `getNativeSyntaxTree`: optional language-native AST capability;
- `shutdown`.

The handshake returns parser ID/version, supported languages/extensions/project markers, and capabilities such as workspace analysis, incremental updates, semantic analysis, and native AST support.

Workers publish `AnalysisDelta` messages containing:

- `workspaceId`, monotonically increasing generation, and request ID;
- the files or workspace scope replaced by the delta;
- normalized nodes and edges;
- unresolved symbolic references for later linking;
- diagnostics and completeness information;
- parser ID/version used to produce the facts.

Stable IDs must include language and parser/workspace ownership. Source spans must define line/column base and end-position semantics. Relationship kinds are versioned strings rather than closed cross-language enums.

Do not force a universal detailed AST into `CodeNode`. Maintain two layers:

1. a deliberately narrow cross-language graph IR for merged queries;
2. optional parser-native syntax trees behind a common response envelope.

Cross-language links such as TypeScript clients to C# HTTP routes are separate linker/analyzer stages over the normalized IR. They are not expected to emerge automatically from either compiler.

## Coordinator and change pipeline

Introduce one `IndexCoordinator` hosted service as the sole writer-side entry point.

- `FileWatcherService` only produces raw change notifications.
- A bounded channel provides backpressure and per-path coalescing without losing unrelated paths.
- Initial scan, full refresh, single-file refresh, and watcher changes become coordinator commands instead of detached `Task.Run` calls from endpoints.
- Mutations are ordered within a logical parser workspace and may run concurrently across independent worker sessions.
- Changes arriving during initial indexing are versioned and replayed after the initial worker snapshot, preventing an older response from overwriting a newer file state.
- Full refresh builds a new graph generation while the previous complete snapshot remains readable, then atomically commits.
- Cancellation is linked to host shutdown. API refresh returns an operation/generation ID that can be observed through status.

Keep top-level `indexing.status` values stable for the skill:

- `scanning`: a generation is being built;
- `ready`: the latest requested generation committed successfully;
- `error`: relevant files could not be indexed completely.

Add per-parser/session states: `notNeeded`, `starting`, `indexing`, `ready`, `unavailable`, `failed`, and `stopped`. Empty results are conclusive only when the relevant session is `ready`.

## In-memory graph and API performance

Make the in-memory graph the primary domain store rather than an implementation hidden behind a persistence-shaped factory.

Suggested contracts:

- `IGraphReadStore`: ID/name/type/file lookups and indexed incoming/outgoing traversal;
- `IGraphWriteStore`: atomic `ApplyDelta` and generation commit;
- `IFileIndexStore`: hashes, ownership, and last completed parser generation;
- optional `IGraphProjection` for future persistence/export implementations.

Maintain indices for node ID, normalized name, file ownership, node type/language, outgoing edges, and incoming edges. Apply all related index changes atomically. API requests read one committed generation.

Maintain status counters when deltas commit instead of scanning the graph. Continue using source-generated `System.Text.Json` at REST/MCP boundaries and stream exceptionally large responses. Remove serialize-deserialize cycles from in-memory operations.

Kuzu should not shape these hot-path contracts. If revived, prefer a projection/persistence consumer of committed graph deltas or a separately packaged backend process. That allows the default host to remove CSnakes and Python references entirely.

## Packaging and install-once behavior

The release layout should become explicit:

```text
codecontext[.exe]
workers/
  csharp/worker-manifest.json + executable assets
  typescript/worker-manifest.json + script/runtime assets
protocol/parser-protocol.schema.json
skill/...
```

Resolve bundled workers relative to `AppContext.BaseDirectory`; do not depend on the invoking working directory. Manifests may also describe user-installed workers later.

For the C# worker, benchmark a self-contained ReadyToRun/JIT worker against framework-dependent distribution. Worker startup is amortized, so correctness and package size matter more than making Roslyn AOT. The host itself remains the Native AOT target.

For TypeScript, the first worker version may use `node` from the repository/user PATH, but `/api/status` and the skill must report an actionable `unavailable` state. If “install once” is intended to mean no language-runtime prerequisites, bundle a pinned Node runtime per RID in a later release and measure the archive cost.

Remove Kuzu/Python payload verification from the default release once Kuzu is separated. Publish it as an optional artifact when work resumes. Either add `win-arm64` to the matrix or make the installer fall back explicitly to the supported `win-x64` artifact.

Each release must run:

- host unit/integration tests on the in-memory path;
- protocol schema/fixture conformance tests;
- fake-worker crash, timeout, cancellation, stale-generation, and shutdown tests;
- C# worker tests;
- TypeScript worker tests with pinned Node/npm setup;
- a packaged smoke test that installs the archive, starts a detached mixed-language fixture, waits for `ready`, performs a query, and shuts it down.

## Agent skill changes

The current skill remains the primary client contract. Evolve it alongside the API:

- resolve/start the repository host exactly as it does now;
- wait for top-level indexing readiness and inspect the relevant parser session;
- treat `unavailable` or `failed` as an explicit limitation, never as “no references”;
- show worker prerequisite/remediation messages from status;
- continue leaving instances running for idle shutdown;
- use an operation ID when requesting full refresh and wait for that generation;
- update the supported-language list from runtime capabilities rather than hard-coded prose when practical.

Keep `skill/SKILL.md` agent-agnostic. Platform-specific installers/frontmatter should remain generated wrappers. Replace the `OWNER/code-context` placeholders after the remote is established.

## Implementation phases

### Phase 0: stabilize the lifecycle baseline

- Commit or otherwise isolate the current functional lifecycle/API/skill/release work before parser refactoring.
- Add `.gitattributes` for consistent text line endings and stop tracking generated AOT binaries/build outputs to make future reviews meaningful.
- Fix registry transaction/identity concerns, installer RID mismatch, dynamic parser status, full-refresh progress, and cheap status counters.
- Replace broad CI name filters with explicit external-tooling categories and document the quarantined tests.

Exit gate: two simultaneously launched repository instances remain isolated; concurrent registry updates are preserved; status/stop validate instance identity; filtered baseline tests and a detached in-memory smoke test pass.

### Phase 1: single coordinator and typed graph generations

- Add the bounded change channel and `IndexCoordinator`.
- Route startup scan, refresh, and watcher events through it.
- Introduce `AnalysisDelta` and atomic graph-generation commits while parsers are still in process.
- Replace the global debounce and remove endpoint-owned background tasks.
- Preserve the last complete snapshot during refresh.

Exit gate: burst/rename/delete tests lose no paths; stale generations cannot commit; reads remain consistent during full refresh; shutdown cancels all indexing work.

### Phase 2: protocol and process supervisor

- Add `CodeContext.Parser.Protocol`, the canonical JSON schema, manifests, framing, and source-generated contexts.
- Implement `ParserProcessSupervisor` and a fake worker executable.
- Feed worker/session state into `/api/status` and host logs.
- Test handshake incompatibility, malformed output, stderr volume, timeout, crash/restart, EOF exit, cancellation, and graceful shutdown.

Exit gate: a fake worker can complete initial and incremental generations without registering a new global instance or opening a port.

### Phase 3: extract C#

- Create `CodeContext.CSharp.Worker` as a regular .NET executable using Roslyn.
- Move project/solution discovery and incremental compilation state into the worker.
- Remove `CSharpParser`, Roslyn packages, and concrete C# branches from Core.
- Publish the host with Native AOT and record cold host startup, time-to-health, archive size, worker warm-up, first index, and incremental update benchmarks.

Exit gate: the host has no Roslyn dependency; existing C# graph behavior passes through protocol fixtures; host health is available before the C# worker is ready.

### Phase 4: replace the TypeScript bridge

- Convert the script to a persistent worker using `tsconfig.json`, a builder program/language service, and the TypeScript type checker.
- Eliminate process-per-file execution and double parsing in `ParseFiles`.
- Define JavaScript/TypeScript project ownership and module-resolution behavior.
- Make Node/tooling availability visible through parser status and the skill.

Exit gate: a mixed C#/TypeScript fixture reaches `ready`, cross-file TypeScript relationships use project semantics, and a one-file edit does not recreate or reparse the entire Node process/workspace unnecessarily.

### Phase 5: harden distribution and extension support

- Update release archives, installers, and skill installation for worker assets.
- Add manifest discovery with precedence rules and protocol compatibility ranges.
- Publish small SDKs/helpers for .NET, npm, and PyPI only after the protocol is proven.
- Add optional native AST API endpoints without changing the normalized graph contract.

Exit gate: install once, start from an arbitrary repository directory, index a mixed fixture, query through the skill workflow, idle-shutdown, and upgrade all work on every supported RID.

### Phase 6: Python parser and future persistence

- Add a Python worker starting with `ast`/`symtable`, module discovery, imports, and normalized symbols.
- Evaluate a semantic engine such as Pyright or Jedi behind the same protocol.
- Revisit Kuzu only as an optional projection/backend package or external process; do not reintroduce Python into the default host dependency graph.

Exit gate: Python support requires no Core/API reference to Python libraries, and a Python worker failure degrades only its own parser session.

## Architecture rules for future changes

1. The host may depend on protocol contracts, never on a language compiler/toolchain.
2. Only the repository host is globally registered and externally addressable.
3. Only the coordinator mutates indexing state or submits graph deltas.
4. API readers observe committed generations, never an in-progress rebuild.
5. Parser capabilities and health are discovered, never hard-coded.
6. JSON is a process/API boundary format, not an in-memory repository abstraction.
7. A language worker owns language-specific project discovery, compiler state, and semantics.
8. Cross-language relationships are explicit linker stages over normalized facts.
9. Default release/startup tests must prove zero Python/Kuzu activity and, after separation, zero dependency.
10. Any status relied upon by the agent skill is a compatibility contract and changes only with the skill in the same release.

