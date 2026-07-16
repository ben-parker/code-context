# Worker-architecture migration checklist

Tracking file for implementing `docs/language-worker-architecture-plan.md`.
Working session started 2026-07-15. Phases 0-2 are implemented; later phases
build on their exit gates.

## Plan review (2026-07-15)

All ten "Important findings" in the plan were verified against the working tree:

1. ✅ Confirmed — `FileWatcherService.Debounce` replaces the single `Timer`, cancelling pending changes for unrelated paths; `ProcessChange` is `async void`.
2. ✅ Confirmed — endpoints use `Task.Run`, watcher callbacks and startup scan all enter `GraphUpdateService` concurrently with no synchronization around `_currentFileContents`/`_currentGraph`.
3. ✅ Confirmed — `/api/index/refresh` calls `PerformInitialScanAsync` without a progress reporter.
4. ✅ Confirmed — `PerformInitialScanAsync` clears the graph repository before rebuilding.
5. ✅ Confirmed — `StatusService.GetStatusAsync` materializes all nodes, edges and file metadata per call.
6. ✅ Confirmed — parser status hard-codes `CSharp`/`Python`, omits TypeScript.
7. ✅ Confirmed — the in-memory reconcile round-trips the whole graph through JSON, and **drops fields** (`Language`, `StartCol`/`EndCol`, `ReturnType`, `Parameters`, `Modifiers`, `Metrics`, node `Metadata`) on the way back in.
8. ✅ Confirmed — `InstanceRegistry` has atomic file replacement but no cross-process lock around load-modify-save; liveness check is PID + process-name heuristic only.
9. ✅ Confirmed — release verifies `kuzu_api.py` in every payload; `scripts/install.ps1` selects `win-arm64`, which the matrix never builds.
10. ✅ Confirmed — CI excludes tests by name substrings (`TypeScriptParser`, `CSnakes`, `Repositories.Kuzu`, `TypeScriptKuzu`).

## Phase 0 — stabilize the lifecycle baseline

- [x] `.gitattributes` for consistent line endings; stop tracking `roslyn-aot-test/output` AOT binaries; ignore `*.binlog` and `roslyn-aot-test/output/`.
- [x] Registry transactions: cross-process lock file around load-modify-save.
- [x] Instance identity: random `instanceId` + process start-time fingerprint in `InstanceRecord`; liveness/pruning validates identity, not just PID.
- [x] `stop` validates process identity before killing a PID; stale records are unregistered, not killed.
- [x] `/api/shutdown` requires the instance ID.
- [x] `--detach` passes the instance ID to the child so the printed record matches the registered one.
- [x] Parser status assembled from registered parsers (with a TypeScript availability probe), not hard-coded prose.
- [x] Full refresh reports `filesProcessed`/`filesTotal` (progress reporter threaded through `PerformInitialScanAsync`).
- [x] `/api/status` no longer materializes the full graph: maintained/cached counters on the in-memory store; single-pass file-metadata aggregation.
- [x] `scripts/install.ps1` falls back explicitly to `win-x64` on ARM64 (matrix has no `win-arm64`).
- [x] Release test gate uses an explicit `ExternalTooling` trait instead of name filters; quarantined tests documented in DEVELOPMENT.md.
- [x] Release payload verification no longer requires the Kuzu/Python payload (opt-in backend).

## Phase 1 — single coordinator and typed graph generations

- [x] Typed `TryCommitGenerationAsync(nodes, edges, scope)` on the in-memory store (`IGenerationalGraphStore`); JSON removed from the in-memory hot path (it remains only inside the Kuzu adapter, where it is a process boundary).
- [x] In-memory store swaps complete node/edge snapshots atomically for **C#-owned facts**; readers never observe a cleared/partial C# graph during full refresh. (TypeScript facts are still rebuilt per-file until the Phase 4 worker — see review follow-ups.)
- [x] Full and resumable scans no longer clear the graph up front; stale graph facts and file metadata are pruned against the current file set instead.
- [x] `IndexCoordinator` hosted service: bounded channel, sole writer-side entry point, ordered mutations.
- [x] Watcher posts raw change notifications; per-path coalescing with a quiet window + max-latency flush (no global debounce cancellation, no `async void`).
- [x] Batched change processing: one C# reparse per flushed batch instead of one per file event.
- [x] Startup scan, full refresh, and single-file refresh are coordinator commands (no endpoint-owned `Task.Run`).
- [x] Full refresh returns an `operationId` observable through `/api/status`.
- [x] Stale generations cannot commit (monotonic generation check in the store).
- [x] Idle shutdown takes a busy lease while indexing work is active.
- [x] Shutdown cancels queued/active indexing work.

## Phase 2 — protocol and process supervisor

- [x] `src/CodeContext.Parser.Protocol`: JSON-RPC 2.0 envelope, Content-Length framing (max-size and header guards), typed message DTOs (`initialize`, `workspace/open`, `workspace/index`, `workspace/applyChanges`, `shutdown`, `$/cancel`, `analysis/delta`), source-generated `ParserProtocolJsonContext` (AOT-compatible, no external deps).
- [x] Canonical contract `protocol/parser-protocol.schema.json` shipped with the Protocol project (copied to output; moves into the release layout in Phase 5).
- [x] `WorkerManifest` (`worker-manifest.json`): identity, launch command (resolved against the manifest directory, never the invoking cwd), args, protocol version range, languages/extensions/project markers; validation on load.
- [x] `JsonRpcConnection`: bidirectional endpoint — request correlation, inline notification dispatch (deltas complete before their request's response), thread-pool request handling (a long index cannot block `$/cancel`), cooperative cancellation with grace period, EOF fails pending requests with the fault reason.
- [x] `ParserProcessSupervisor`: spawn from launch spec, initialize handshake with protocol-version validation (incompatible ⇒ terminal `unavailable`), stderr pumped into host logs with parser prefix, crash detection with bounded restart budget (exhausted ⇒ `unavailable`), cooperative cancellation, graceful shutdown (shutdown request → stdin EOF → kill tree), hard-stop disposal via EOF.
- [x] `AnalysisDeltaApplier`: protocol deltas → domain nodes/edges with parser/workspace ownership metadata, committed atomically through `IGenerationalGraphStore` scoped to exactly the delta's declared files/workspace; per-workspace stale-generation rejection; store-generation race retry.
- [x] Per-parser session states (`notNeeded`/`starting`/`indexing`/`ready`/`unavailable`/`failed`/`stopped`) in `ParserSessionRegistry`, surfaced through `/api/status` as `parsers.sessions` (+ per-parser `status` override). Supervisors push full snapshots; in-process parsers (until Phases 3/4) report ready/failed outcomes.
- [x] `tests/CodeContext.FakeWorker`: protocol-conformant worker executable with selectable misbehaviors (`protocol-too-new`, `hang-on-initialize`, `malformed-output`, `crash-on-index`, `crash-once`, `stderr-flood`, `slow-index`).
- [x] Conformance tests: handshake incompatibility, malformed output, stderr volume (5000 lines, no deadlock), initialize timeout, crash → restart within budget, restart-budget exhaustion, stdin-EOF self-exit, cooperative cancellation (worker survives), graceful shutdown, plus framing/connection/manifest/applier unit tests.
- [x] Exit gate: `FullGenerationFlow_InitialAndIncremental_CommitThroughGenerationalStore` — a fake worker completes an initial and an incremental generation committed through the generational store; the worker's only I/O is stdio (no port, no instance registration).

## Review follow-ups deferred to later phases

Raised in the Phase 0/1 dual-model review (2026-07-15); status as of the Phase 2 pass:

- [x] A failed watcher-driven C# batch only marked per-file metadata `Failed`. Now `GraphUpdateService` reports per-parser `failed`/`ready` session states to `/api/status` (Phase 2 session registry).
- [x] Per-parser error detail now reaches `/api/status` via `parsers.sessions[].lastError` (retained across recovery so the most recent failure stays diagnosable). Per-file detail remains in file metadata (`filesByStatus` + `errorMessage`).
- [ ] Kuzu's JSON reconcile has no scope support, so a C# reparse on `--backend kuzu` still replaces the whole graph (clobbers TS facts). Scoped deltas now exist (`AnalysisDelta`/applier) but don't flow to Kuzu; fix in Phase 3 when real parser traffic moves onto the protocol, or drop when Kuzu becomes a projection consumer.
- [x] TS full refreshes now stream one persistent-worker workspace generation and commit it atomically (Phase 4).
- [ ] Pre-existing: a Kuzu resumable scan parses only changed files into `_currentFileContents`, so its whole-graph reconcile can drop facts from unchanged files after a restart. Irrelevant for the in-memory default; resolve with worker workspace sessions in Phase 3.

## Phase 3 — extract C# (2026-07-15)

- [x] `src/CodeContext.CSharp.Worker`: regular JIT executable owning Roslyn. Speaks the Phase 2 protocol over stdio (initialize/open/index/applyChanges/shutdown/$/cancel), exits on stdin EOF, opens no ports. `CSharpWorkspaceAnalyzer` keeps per-workspace syntax-tree state: reparse changed files only, recompile, emit the complete workspace as chunked `analysis/delta` notifications (`replacesWorkspace`), mirroring the whole-scope C# generation semantics the host already commits atomically.
- [x] Node/edge IDs are language + workspace namespaced (`csharp:<workspace>:<symbol display>`) per the protocol's ownership rule; ownership metadata (`parserId`/`workspaceId`) is stamped by the applier.
- [x] Host-side wiring: `WorkerCatalog` discovers manifests from deterministic precedence roots; `LanguageWorkerService` owns one supervisor + workspace generation counter per worker and re-opens the workspace (approved-file sync) before every mutation so a restarted worker self-heals; `IAnalysisDeltaSink` abstracts the commit path — `AnalysisDeltaApplier` (generational, atomic) by default, `JsonReconcileDeltaSink` for Kuzu (whole-graph JSON reconcile; the pre-existing opt-in clobber limitation, unchanged).
- [x] `GraphUpdateService`: all `CSharpParser`/`OfType` special cases removed; worker-owned extensions route through `ILanguageWorkerService` (batch → one applyChanges; scans → one workspace/index per worker, empty generation included so deleting the last C# file clears pathless facts); resumable scans feed the worker its complete owned file set (fixes the changed-subset reconcile hazard for C#); a failed worker no longer hides other parsers' scan results (failures aggregate, scan still errors).
- [x] Roslyn removed from Core/host: `CSharpParser` deleted, `Microsoft.CodeAnalysis.CSharp` package reference dropped, `ContextService` using-statement parsing is regex-only. Guarded by `CSharpWorkerProtocolFixtureTests.HostAssemblies_HaveNoRoslynDependency`.
- [x] Api build bundles the worker under `workers/csharp/` next to the host binary (the catalog's runtime layout).
- [x] `/api/status`: worker sessions (from the supervisor snapshots) now feed `parsers.available`/`enabled` in addition to `parsers.sessions`.
- [x] Tests migrated to protocol fixtures: `CSharpWorkerAnalyzerTests` (engine-level, ports the old CSharpParserTests), `CSharpWorkerProtocolFixtureTests` (real worker process end-to-end: initial index, incremental change, delete, stale generations, session states), `LanguageWorkerServiceTests` (routing/lifecycle against the fake worker), and the GraphUpdateService/Implements/Resumable suites now run C# through the real worker.
- [x] Exit gate checked: host has no Roslyn dependency; C# graph behavior passes through protocol fixtures; `/healthz` responds ~3s before the C# worker's first index completes (see `docs/phase3-benchmarks.md`).
- [x] Benchmarks + AOT status recorded in `docs/phase3-benchmarks.md`. Native AOT publish is blocked on this machine by a missing VS C++ toolchain; remaining trim warnings (OpenAPI + MCP anonymous-type JSON) are listed there for the release pass.

## Phase 3 dual-model review (Opus + Sonnet, 2026-07-15)

Both reviews ran against commit `716b053`; findings and dispositions:

- [x] **Critical (both models)** — a single failing non-worker file (e.g. an in-process parser whose tooling is missing) aborted the entire scan: the per-file exception escaped `Parallel.ForEachAsync`, skipping `PruneMissingFilesAsync`/`ReportComplete` and failing the instance-wide scan state even though the C# worker had already committed. Fixed: all scan/batch paths now attempt every file, collect failures, finish pruning/progress, and rethrow the first failure at the end (indexing still reports `error`, but completely). Regression-tested in `ScanResilienceTests`.
- [x] **Major (Sonnet)** — per-file `Ready` session reports were last-write-wins under parallel processing, so a real parse failure could be masked by a later success. Fixed: in-process parser outcomes aggregate per batch/scan (`InProcessParserOutcomes`, failure wins) and are reported once; `Unavailable` still reports immediately. Regression-tested in `ScanResilienceTests`.
- [x] **Minor (Sonnet)** — `JsonReconcileDeltaSink` leaked partially buffered chunks when a worker died mid-stream. Fixed: superseded generations evict pending buffers (mirrors `AnalysisDeltaApplier`).
- [x] Hardening from the same pass: worker spawn failures (missing runtime, e.g. no `node`) now surface an actionable message through the session's `lastError` instead of a bare Win32 error.
- [ ] **Informational (Opus)** — on the opt-in Kuzu backend, `approvedFiles` derives from `IFileMetadataRepository.GetAllAsync`, which is type-limited there and may under-report; would cause the worker to drop cached trees. Tracked as existing Kuzu debt (all Kuzu repo tests mock `IKuzuApi`); resolve when Kuzu becomes a projection consumer.
- Noted, no action needed (Opus): `FileHash` is no longer written for worker-owned files; change detection keys on `Status`/`LastModified` only, verified for both backends.

## Phase 4 — replace the TypeScript bridge (2026-07-15)

- [x] `src/CodeContext.TypeScript.Worker/typescript-worker.js`: persistent Node.js worker speaking the parser protocol over stdio (Content-Length framing, sequential request chain, out-of-band `$/cancel`, exit on stdin EOF). One `ts.LanguageService` + document registry per workspace: a one-file edit bumps one snapshot version — no process-per-file, no double parsing.
- [x] Project ownership & module resolution defined: the host's approved file set is the program root list; `<root>/tsconfig.json` compilerOptions apply when present, permissive defaults (allowJs, esnext) otherwise. Cross-file EXTENDS/IMPLEMENTS/CALLS and import targets resolve through the type checker to declarations inside the approved set; unresolved targets keep the bare name plus `unresolved` metadata.
- [x] Node IDs are `typescript:<workspace>:<relpath>#<qualifiedName>` (workspace/file scoped); stable parameter-shape discriminators prevent overload/accessor collisions. File-level IMPORTS edges resolve to the workspace-owned target module when it is in the project.
- [x] Incremental state: `applyChanges` bumps only touched language-service snapshots, then re-walks the cached workspace into one atomic replacement because a changed export/type can alter semantic edges in untouched dependents. Node is not respawned and unchanged SourceFiles are not reparsed. Language-service path canonicalization bug (TS-normalized names vs host names ⇒ stale snapshots) found and fixed during bring-up.
- [x] Old bridge deleted: `src/CodeContext.TypeScript.Parser` project, `Scripts/typescript-parser.js`, and its tests are gone; no in-process production parsers remain (ILanguageParser stays as a test/adapter seam).
- [x] Node availability is visible: a failed `node` spawn surfaces an actionable `lastError` on the `typescript` session in `/api/status` (supervisor restart budget → `unavailable`); C# indexing is unaffected (scan resilience from the Phase 3 review pass).
- [x] Api build bundles the worker under `workers/typescript/` (script + manifest + package.json + node_modules when installed).
- [x] Exit gate checked (`TypeScriptWorkerProtocolTests`, ExternalTooling): mixed C#/TS fixture reaches `ready` with both sessions `ready`; cross-file TS relationships use project semantics (EXTENDS/CALLS edges point at resolved node IDs in other files); a one-file edit reuses the same worker PID and updates facts without a workspace rebuild. Verified end-to-end against the detached host binary as well.

## Phase 4 dual-model review (Opus + Sonnet, 2026-07-15)

Both reviews ran against commit `24172ad`; findings and dispositions:

- [x] **Critical (Sonnet, verified empirically)** — `dotnet publish` never picked up the `AfterTargets="Build"` worker copy (publish selects its own file set), so released binaries would ship with **no workers at all**; additionally `.github/workflows/release.yml` still verified the deleted `typescript-parser.js`, guaranteeing a red release. Fixed: a shared `_CollectLanguageWorkerFiles` target (RID-aware for the C# worker) now feeds both an `AfterTargets="Build"` and an `AfterTargets="Publish"` copy; release workflow runs `npm ci` for the TS worker and verifies the actual `workers/{csharp,typescript}` payload. Verified with a real `dotnet publish -r win-x64` run.
- [x] **Major (Sonnet)** — the worker's new `HAS_METHOD`/`HAS_FIELD`/`HAS_PROPERTY` containment edges leaked into `ContextService` `uses`/`usedBy`, burying real call/inheritance relationships under member lists. Fixed: containment kinds are excluded from usage relationships (structure is already covered by `relatedItems`); regression-tested in `ContainmentEdgeFilteringTests`.
- [x] **Minor (both)** — a `renamed` change's `oldPath` was dropped from the delta's `replacesFiles` scope (latent: the watcher splits renames today), which would have left the old path's facts orphaned. Fixed in the worker.
- [x] **Minor (Opus)** — both workers declared `endIsInclusive: true` while emitting exclusive end positions; declarations corrected to `false`.
- [x] **Minor (Opus)** — same-line same-target calls collapsed into one edge ID; CALLS edge IDs now carry line *and* column.
- [x] **Minor (Opus)** — IMPORTS edges never carried the `unresolved` marker for external packages; now consistent with heritage/CALLS.
- [x] **Minor (Opus)** — stale-root closure: the language-service host pinned `getCurrentDirectory`/tsconfig to the initialize root even if `workspace/open` re-rooted; now re-roots and reloads options.
- [x] **Minor (Opus)** — chunked delta writes had no backpressure handling and `process.exit` on stdin-EOF could truncate a buffered final frame; writes are now awaited and the exit path drains the last frame.
- [x] **Minor (Sonnet)** — malformed `tsconfig.json` fell back to defaults silently; now emits a workspace diagnostic on the next delta.
- [x] **Minor (Sonnet)** — an unreadable file was indistinguishable from "no declarations"; now emits a per-file diagnostic while still replacing its facts.
- [x] Former parity limitations fixed in the Phase 5 review: signatures use structural body boundaries (object-type braces no longer truncate them), and declaration IDs include stable overload/accessor shape. Per-RID packaging is covered below.

## Phase 3/4 review before Phase 5 (2026-07-15)

- [x] **Critical** — both real workers omitted workspace ownership from fact IDs despite the protocol contract. IDs now include parser/workspace ownership, and the supervisor rejects node IDs, edge IDs, or edge sources outside that prefix before they reach the graph.
- [x] **Critical** — TypeScript `replacesFiles` updates left resolved edges in untouched dependents stale after an exported member/type changed. Updates now reuse persistent language-service state but atomically replace re-walked workspace facts; regression-tested with a changed base file and untouched dependent.
- [x] **Major** — `SpanSemantics` was validated but ignored, leaving C# and TypeScript graph coordinates in different bases. The supervisor now normalizes every stored node/call position to zero-based, end-exclusive coordinates.
- [x] **Major** — a valid request-level JSON-RPC error marked the whole worker session failed, contradicting the supervisor contract. Remote request errors now leave a healthy session ready; connection/protocol failures still fail it.
- [x] **Major race/lifecycle** — worker shutdown could overlap an index/native-tree request, and aliased DI registrations could dispose `LanguageWorkerService` more than once. Shutdown/disposal now serialize on each worker lock and disposal is idempotent.
- [x] **Minor** — malformed extension-conflict logging produced CA2017; corrected. Manifest IDs/extensions now receive stricter validation as arbitrary third-party workers become discoverable.

## Phase 5 — harden distribution and extension support (2026-07-15)

- [x] Deterministic manifest discovery: `CODECONTEXT_WORKERS_DIR` path-list roots, per-user `~/.codecontext/workers`, then bundled workers; compatible first match wins, incompatible higher-precedence manifests do not hide a compatible fallback, and repository-local code is never auto-executed.
- [x] Optional `syntaxTree/get` contract and `POST /api/syntax-tree`: C# returns bounded `roslyn-csharp-syntax-v1` trees; TS/JS returns bounded `typescript-compiler-syntax-v1` trees. Range/depth limits, capability checks, committed-file checks, and API error states are covered without changing normalized graph storage.
- [x] Skill updated in the same release with an explicit normalized-vs-native decision rule and a combined workflow; installers deploy the canonical skill to Codex and Claude Code.
- [x] Release payload includes `workers/`, `protocol/`, and `skill/`. The C# worker is published self-contained/multi-file per RID; the TypeScript worker includes a target-RID Node runtime and upstream license, so neither system `dotnet` nor `node` is required.
- [x] Upgrade-safe installers stage immutable version directories and atomically switch a stable launcher/current marker. Running instances keep their original host/worker payload; the old Windows in-place layout gets an actionable close-and-retry migration.
- [x] Package-ready helper SDKs: `CodeContext.Worker.Protocol` (.NET), `@codecontext/worker-sdk` (npm), and `codecontext-worker-sdk` (Python), with framing tests and release artifacts. Extension lifecycle/ownership guidance is in `docs/language-worker-sdk.md`.
- [x] Verification: 429 non-external tests, five real TypeScript protocol tests, npm/Python SDK tests, NuGet pack, a real self-contained win-x64 publish, and an installed-payload smoke from an unrelated working directory. The mixed fixture reached `ready` for C# + TypeScript and returned a native Roslyn tree; the release matrix repeats payload verification for all supported RIDs.

## Later phases

- [ ] Phase 6: Python worker; Kuzu revisited as optional projection only.
