# .NET 10 Upgrade, Native AOT Host & Performance — Task Checklist

Plan of record: `C:\Users\benpa\.claude\plans\i-want-to-update-wobbly-sundae.md`
Convention: each phase ends with a green build, full `dotnet test`, and Opus + Sonnet code review (findings fixed before next phase). Fable performs the final end-to-end check and review.

## Phase 0 — Baseline (no code changes)
- [x] `scripts/measure-startup.ps1` (time-to-listening + time-to-indexed, reusable)
- [x] Baseline cold `codecontext --version` (10×, min/avg) on net9.0 publish
- [x] Baseline time-to-ready on this repo
- [x] Baseline median query latency: `/api/context/complete?identifier=<hot>&depth=2&includeTests=true` ×20
- [x] Record numbers below (note: VS Build Tools install running concurrently — rerun if noisy)

Baseline captured on net9.0 JIT single-file self-contained publish (win-x64, `scripts/verify-publish.ps1`,
release version 0.2.2). SDK actually installed is 10.0.302 (not 10.0.100 as briefed); it publishes net9.0
via the downloaded 9.0 runtime pack. Timing caveats: (1) VS Build Tools 2022 install was running
concurrently, so absolute numbers are noisy — treat as ballpark. (2) A codecontext instance already owns
the real repo path (idempotent `start` exits 0), so startup + query latency were measured against a temp
copy of the repo source tree (`%TEMP%\cc-repo-copy`, 199 files / 119 `.cs`, heavy/ignored dirs excluded),
which indexes the same source. Query target `ContextService`, depth=2, includeTests=true, 20 runs after 1
warm-up. Cold `--version`: 10 runs `61.5,62.4,62.5,63.2,63.5,66.8,67.9,79,80.6,84.4` ms.

## Phase 1 — TFM / SDK / package / CI bump (JIT)
- [x] `global.json` (10.0.100, rollForward latestFeature) — resolves to installed SDK 10.0.302
- [x] All csproj → net10.0 (Api, Core, Mcp, Parser.Protocol, CSharp.Worker, Core.Tests, FakeWorker; roslyn-aot-test left on net9.0 per plan — deleted in Phase 4)
- [x] Remove all `Microsoft.Net.Compilers.Toolset` pins (Api, Core, CSharp.Worker, Core.Tests)
- [x] Fix `net9.0` hardcoded worker paths in CodeContext.Api.csproj (×3: build + publish item paths); also fixed stale `.vscode/launch.json` path
- [x] Packages landed: Microsoft.Extensions.Hosting/Logging → **10.0.10**; Microsoft.AspNetCore.OpenApi → **10.0.10**; Microsoft.AspNetCore.Mvc.Testing → **10.0.10**; Microsoft.NET.Test.Sdk → **17.14.1**; Microsoft.CodeAnalysis.CSharp (worker) → **5.0.0**; kept xunit 2.9.2 / NSubstitute 5.3.0 / coverlet 6.0.2 / xunit.runner.visualstudio 2.8.2
- [x] System.CommandLine → **2.0.10** stable (fixed all 10 option ctors: name no longer repeated in aliases, e.g. `new("--path","-p")`, `new("--port")`); GA `DefaultValueFactory`/`SetAction`/`GetValue`/`Parse().InvokeAsync()` compiled unchanged; smoked start/stop/list/status + --help
- [x] ModelContextProtocol → **1.4.1** stable (kept reflection `.WithTools<CodeContextTools>()`; `[McpServerTool]`/`[McpServerToolType]` unchanged — zero code changes needed)
- [x] Extra: pinned **Microsoft.OpenApi 2.11.0** to clear NU1903 (AspNetCore.OpenApi 10.0.10 pulls vulnerable 2.0.0) and migrated the 3 OpenAPI transformers to the Microsoft.OpenApi 2.0 API (namespace `Microsoft.OpenApi`, `OperationType`→`HttpMethod`, `JsonSchemaType` enum, `OpenApiSchemaReference`, read-only `IOpenApiParameter`); removed deprecated `WithOpenApi()` (ASPDEPR002)
- [x] CI: 9.0.x → 10.0.x (release.yml ×3; auto-tag.yml ×2 — plan said 1, but there are genuinely two setup-dotnet steps)
- [x] Capture MCP `tools/list` snapshot fixture (for Phase 3b parity) → `tests/CodeContext.Core.Tests/Fixtures/mcp-tools-list.snapshot.json` (3 tools: get_multi_context, get_context, get_status)
- [x] Verify: build warnings-clean (0W/0E); tests 396 passed + 8 ExternalTooling passed; verify-publish win-x64 green; MCP stdio smoke (initialize+tools/list+tools/call get_status) green; CLI smoke green
- [x] Opus + Sonnet review; findings fixed
  - Fixed: `JsonSchemaType` flags-equality bug in `CodeNodeArraySchemaTransformer` (both reviewers flagged; Sonnet verified nullable lists emit `Null|Array` empirically) — now a bitwise flags test.
  - Dismissed with evidence: MCP tool-name parity across SDK 0.3.0-preview.2 → 1.4.1 — probed the pre-upgrade binary's `tools/list` over stdio; names were already `get_context`/`get_multi_context`/`get_status`. Snapshot fixture is a valid golden.
  - Noted for Phase 3d+: MCP-mode stdout logging (pre-existing; observed on the old binary during the parity probe).

## Phase 2 — Remove in-process parser seam
- [x] Delete `ILanguageParser` + `IParserDiagnostics` (all implementers are test fakes — live-graph verified)
- [x] GraphUpdateService: delete `ProcessFileChangeOldWayAsync`, `UpdateGraphAsync`, `HandleFileDeletedAsync`, `ComputeFileHash`, `InProcessParserOutcomes`, `_parsers`; collapse fallback branches in `ProcessFileChangesAsync`/`PerformInitialScanCoreAsync`/`PerformResumableScanCoreAsync` (scans only enumerate worker-owned extensions, so the "other files" branches were dead; also dropped the now-unused `IParserSessionRegistry` ctor param and the `!_parsers.Any()` guard in `RunReconciliationAsync`)
- [x] StatusService: remove `_parsers` loop + `DeriveParserName` (parser health now sourced entirely from worker session reports)
- [x] FileMetadata: remove `FileHash` (+ removed dead `file_hash` field from the never-emitted `FileMetadataDto`; no REST endpoint constructs it)
- [x] DI: remove `IEnumerable<ILanguageParser>` plumbing (Api ProgramHelpers) — was comment-only; no registration existed
- [x] Tests: deleted legacy-only ScanResilienceTests (6) + GraphUpdateSessionReportTests (2) + 2 in-process-parser StatusService tests; surviving behaviors already covered by worker fixtures (GraphUpdateServiceTests/LanguageWorkerServiceTests); mechanical CSharpWorkerTestSupport/StatusServiceScanStateTests + repo-test updates
- [x] CLAUDE.md: rewrite parser-extensibility + fallback sections (workers only)
- [x] Verify: full suite (386) + ExternalTooling (8) green; `/api/status` byte-identical (normalized diff); verify-publish win-x64 edge kinds green
- [x] Opus + Sonnet review; findings fixed
  - Real finding (both reviewers): multi-worker scan-failure resilience was implemented but untested on the worker path. Added `tests/CodeContext.Core.Tests/Services/WorkerScanResilienceTests.cs` (3 tests, real C# worker + real FakeWorker in `crash-on-index` mode, default suite / no ExternalTooling trait): `FullScan_OneWorkerFails_SurfacesFailureAndStillAttemptsEveryWorker` (both workers attempted, failing file → Failed+message, first failure rethrown), `FullScan_PrunesMissingFiles_EvenWhenAWorkerFails` (unconditional prune of stale metadata still runs despite the failure), `ChangeBatch_HealthyWorkerCommits_WhenASiblingWorkerFails` (non-reconciliation-wrapped change path: healthy worker's batch commits + nodes queryable while the sibling batch fails and its file → Failed). Each was sanity-checked by temporarily breaking the production isolation/rethrow/prune (fail-fast, swallowed rethrow) and confirming the matching test failed, then restored.
  - Documented (test comments + class remarks): the full-scan path is atomic — a sibling failure rethrows and rolls the staged generation back, so healthy graph facts do not commit on a *full scan*; the "healthy results survive a sibling failure" property is genuine only on the change-batch path, which is where the test asserts queryable nodes.
  - Low finding: scrubbed stale `ILanguageParser`/`CSharpParser`/`TypeScriptParser` references from living docs — `codecontext-prd.md` §3.2 (rewritten as the language-worker extensibility contract), `docs/language-worker-architecture-plan.md` (table row marked removed), `typescript-feature.md` (superseded-by-worker note), `docs/post-phase-5-hardening-plan.md` (historical note), and `test-api-endpoints.sh` (dead identifiers → real present symbols `GraphUpdateService`/`ILanguageWorkerService`/`ProcessFileChangesAsync`).
  - Reviewers confirmed production behavior is unchanged; the coverage gap predated Phase 2 (the deleted legacy ScanResilienceTests only covered the removed in-process path).

## Phase 3 — AOT-safety refactors (still JIT)
- [x] Enable Trim/AOT/SingleFile analyzers + RequestDelegateGenerator (Api) + Trim/AOT/IsAotCompatible (Mcp); burned warnings to zero. Only IL2026/IL3050 surfaced (20, all from reflection JSON of anonymous objects + the CompactNativeTree JsonNode path); **no** RDG warnings (endpoint lambdas already source-generatable).
- [x] 3a. Typed DTOs replace anonymous JSON — Mcp tools (new `McpJsonContext`, verbatim naming + Never null-ignore to match the old default-options wire shape) + REST endpoints (`ErrorDtos.cs` in `CodeContextJsonContext`, camelCase + WhenWritingNull) via `Results.Json(dto, JsonTypeInfo, statusCode)`; CompactNativeTree de-reflected (`JsonNode.ToJsonString`+`JsonDocument.Parse`, `IList<JsonNode?>.Add`); `CountResponseDto` dropped its never-populated `Dictionary<string,object> _query_stats`. `ErrorContractTests` (8) lock byte-identity, written first.
- [x] 3b. `McpToolCatalog`: explicit `WithListToolsHandler`/`WithCallToolHandler` (delegate `McpRequestHandler<TParams,TResult>` = `(RequestContext<T>, CancellationToken) => ValueTask<TResult>`); three `Tool`s with InputSchema parsed from const JSON literals + `execution.taskSupport=optional` (ToolExecution is `[Experimental MCPEXP001]` — suppressed narrowly to match the contract). `CallAsync` dispatches by name, args from `ctx.Params.Arguments`, services from `ctx.Services`, delegates to the unchanged `CodeContextTools` bodies. `[McpServerTool]`/`[McpServerToolType]` removed. `McpToolCatalogTests` asserts semantic parity vs the golden snapshot.
- [x] 3c. OpenAPI dev-only: `ENABLE_OPENAPI` DefineConstant + `Microsoft.AspNetCore.OpenApi`/`Microsoft.OpenApi` PackageReferences all Debug-only; three transformers + AddOpenApi/MapOpenApi moved to `OpenApiSupport.cs` (whole file `#if ENABLE_OPENAPI`); call sites guarded by `#if` + runtime `IsDevelopment()`. Release output carries neither OpenApi DLL.
- [x] 3d. `WebApplication.CreateBuilder` → `CreateSlimBuilder` (REST); throwaway `LoggerFactory` deleted, current-dir line moved to `app.Logger` (REST only).
- [x] 3d+. MCP mode: `builder.Logging.ClearProviders()` + `AddConsole(LogToStandardErrorThreshold=Trace)` so stdout is JSON-RPC-only. Verified live: stdio round-trip (initialize + tools/list + tools/call get_status + get_context) returns 4 valid JSON responses on stdout with **0** `info:` lines; all logs on stderr.
- [x] Verify: 0 warnings Debug **and** Release (full solution); `dotnet test` 398 + ExternalTooling 8 green; snapshot parity green; Debug/Development serves `/openapi/v1.json` (200) + `/api/schema` (302), Release 404s both; REST smoke — `/api/status` 200, `/api/context/complete?identifier=Widget` compact 200, `?view=bogus` 400 byte-identical error; `scripts/verify-publish.ps1 -RuntimeIdentifier win-x64` green (JIT publish unchanged).
- [x] Opus + Sonnet review; findings fixed
  - **Finding 1 (MCP arg binding strictness).** The manual `McpToolCatalog` handler now reproduces the
    pre-upgrade SDK's `tools/call` taxonomy, established empirically by probing the old self-contained
    binary (`out/smoke-win-x64/codecontext.exe`) over stdio. Per-case decisions:
    (a) missing `identifier` → in-band `isError` result (old = isError behind a generic message; we
    surface `"identifier is required."`); (b) missing/non-array `identifiers` → in-band `isError`
    `"identifiers is required."` (was a *silent empty success* — the bug; old = isError), while an
    explicit empty array still binds to a successful `[]`; (c) wrong-kind optional scalar (`depth="5"`)
    → falls back to the default and succeeds (matches the old success taxonomy; numeric-string coercion
    is intentionally not replicated); (d) out-of-range `view="bogus"` → in-band `isError`
    `"view must be 'Compact' or 'Full'."` (was silent coercion to Compact; old = isError); (e) unknown
    tool → JSON-RPC **protocol** error via `McpProtocolException(McpErrorCode.InvalidParams)` = `-32602`,
    byte-identical message `"Unknown tool: '<name>'"` (thrown, not folded into `isError`). New
    `McpToolCatalogCallTests` (10) cover happy dispatch of all three tools through a DI scope, cases
    a–e, and tool-body-exception → `isError`.
  - **Finding 2 (restore determinism).** Both OpenAPI `PackageReference`s (`Microsoft.AspNetCore.OpenApi`
    + the `Microsoft.OpenApi` 2.11.0 CVE pin) made unconditional; `ENABLE_OPENAPI` stays Debug-only.
    Empirically verified in a fresh HEAD worktree: config-independent `dotnet restore` then no-restore
    `build -c Release` → 0 warnings, no NU1903; no-restore `build -c Debug` → 0 warnings with
    `OpenApiSupport` compiled into the Debug assembly and absent from the Release assembly.
  - **Finding 3 (REST error contract).** `CodeContextEndpointTests` gained full-shape (byte-identity)
    live assertions driven through the real ASP.NET host for `CONTEXT_ERROR` (upgraded from a loose
    `Assert.Contains`), `MULTI_CONTEXT_ERROR`, and the single-file `REFRESH_ERROR` variant.
  - **Finding 4 (cosmetic).** Phase 3a text corrected: `ErrorContractTests` is 8, not 10.
  - **Accepted debt.** The endpoint tests use `WebApplication.CreateBuilder`/`UseTestServer`, not the
    production slim builder (`CreateSlimBuilder`), so the slim-builder wiring is not covered by these
    tests — it is validated via `scripts/verify-publish.ps1` against the packaged host. Remaining
    REST/MCP error variants (e.g. `SCAN_IN_PROGRESS` 409, `SHUTTING_DOWN` 503, syntax-tree error codes,
    the full-view `relation` 400) have DTO-level golden coverage in `ErrorContractTests` but no live
    end-to-end assertion; the three above lock the pattern.
  - Verification: `dotnet build` Debug **and** Release 0 warnings; full suite 410 + ExternalTooling 8
    green; MCP stdio round-trip clean (initialize + tools/list + `get_status` + `no_such_tool` → -32602,
    0 stdout pollution).

## Phase 4 — Host AOT + worker R2R + publish/CI
- [x] Host: PublishAot=true, OptimizationPreference=Speed, InvariantGlobalization, StripSymbols non-Windows. Culture audit of Api+Core first: only `StatusService` memory-MB `ToString("F1")` was implicit-culture (all other user-facing formats use round-trip `"O"` or explicit `CultureInfo.InvariantCulture`); pinned it to InvariantCulture. Removed stale "Disable AOT for testing" comment + commented-out AOT lines. Worker publish stays PublishAot=false via existing ProjectReference AdditionalProperties + PublishCSharpWorkerForHost RemoveProperties/Properties (verified).
- [x] Worker: PublishReadyToRun (RID-conditioned), explicit TieredCompilation/TieredPGO, ConcurrentGarbageCollection=true + ServerGarbageCollection=false (workstation GC, per-repo memory; DATAS deferred to benchmarks). R2R verified live: `workers/csharp/Microsoft.CodeAnalysis.CSharp.dll` 6.52MB (non-R2R IL) → 17.67MB (R2R, 2.7×).
- [x] verify-publish.ps1: dropped `-p:PublishSingleFile`/`-p:IncludeNativeLibrariesForSelfExtract`; added no-managed-DLL-in-publish-root assertion (workers/** exempt). Kept all live assertions (boot, indexing ready, contractVersion 1, 12 edge kinds, skill hash) — all green on the AOT publish.
- [x] Delete roslyn-aot-test/; rewrite AOT_COMPATIBILITY.md (final architecture: host Native AOT, worker JIT+R2R and why; AOT-clean requirements; InvariantGlobalization; VS 2026 C++ toolchain prereq).
- [x] Prereq: VS 2026 Community C++ (VC.Tools.x86.x64) confirmed via vswhere. Local publish fix: VS 2026 `vcvarsall.bat` invokes bare `vswhere.exe`, so the VS **Installer dir must be on PATH** for `dotnet publish -r` locally (otherwise ILC's linker-path property is corrupted by vswhere's "not recognized" stderr → MSB3073 link error). CI `windows-latest` already has it on PATH; no repo change needed. Documented in the report.
- [x] Verify: `scripts/verify-publish.ps1 -RuntimeIdentifier win-x64` green (AOT, incl. new no-DLL assertion + all 12 edge kinds live); MCP stdio smoke on the **published AOT binary** green (initialize→serverInfo, tools/list→3 tools, get_status→success, bad tool→ -32602 "Unknown tool", clean JSON-only stdout — Phase 3 taxonomy); CLI smoke green (--version 0.2.20, list [], start --detach JSON, status, stop round trip); cold-start 3.6× faster vs baseline. (4-RID CI branch run: deferred to Phase 7 dry-run.)
- [ ] Opus + Sonnet review; findings fixed

## Phase 5 — Adjacency + file-path indexes (TDD)
- [ ] GraphAdjacency (FrozenDictionary source/target/filePath) + version-stamped `GetAdjacency()` on InMemoryDatabase
- [ ] Commit 1: repos consume index internally, signatures unchanged
- [ ] Commit 2 (optional): `Task<IReadOnlyList<T>>` reads without copies
- [ ] TDD: invalidation (upsert/delete/commit/prune/rollback), scan-vs-index equivalence, path-case semantics
- [ ] Verify: suite + ExternalTooling; live-index byte-compare of fixture /api/context; query-latency before/after
- [ ] Opus + Sonnet review; findings fixed

## Phase 6 — Allocation passes (one reviewed commit each)
- [ ] 6a. HeaderFraming: pooled buffered reads, Span header parse, ArrayPool payloads (tests first)
- [ ] 6b. RepositoryFileSelector: FileSystemEnumerable attributes, span segment walk, SearchValues
- [ ] 6c. ContextService: GeneratedRegex, no ToLower ranking allocs, cached test-name patterns (byte-identical fixtures)
- [ ] 6d. AnalysisDeltaApplier metadata reuse; GraphState rebuild size hints
- [ ] 6e. Cache worker supported-extensions set
- [ ] Verify: suite; dotnet-counters alloc rate before/after rescan
- [ ] Opus + Sonnet review; findings fixed

## Phase 7 — Wrap-up
- [ ] Final benchmark table (below)
- [ ] AOT_COMPATIBILITY.md + CLAUDE.md publish notes (additive)
- [ ] 4-RID CI release dry-run on branch
- [ ] Fable end-to-end check + final review of cumulative diff

## Benchmarks

| Metric | net9 JIT baseline | Post-AOT (Ph4) | Post-perf (Ph6) |
|---|---|---|---|
| Cold `--version` (min/avg ms) | 61.5 / 69.2 | **15.7 / 19.2** | |
| Time-to-listening (ms) | 1198 | **556** (516–580, n=3) | |
| Time-to-indexed, this repo (ms) | 7118 | **5688** (5551–5823, n=3) | |
| Query latency p50, depth=2+tests (ms) | 228.3 | **293** (287–316, 2 runs) ⚠️ | |
| Alloc rate during rescan (MB/s) | (Phase 6) | | |
| Publish size, win-x64 (MB) | 215.9 | **165.2** shippable / 29.2 host | |

Post-AOT conditions (win-x64, SDK 10.0.302, release version 0.2.20, VS 2026 C++ link toolchain):
- **Cold `--version`, startup, query** measured against the SAME temp corpus Phase 0 used
  (`%TEMP%\cc-repo-copy`, still present, 199 files / 119 `.cs`), query target `ContextService`,
  depth=2, includeTests=true, 20 runs after 1 warm-up (median of the sorted 20).
- **Cold start is the headline win**: `--version` 3.6× faster (69.2→19.2ms avg), time-to-listening
  2.2× faster (1198→556ms), time-to-indexed 1.25× faster (7118→5688ms). These are the per-repo-spawn
  costs AOT targets.
- **Query p50 regressed ~28%** (228→293ms) — the expected AOT-vs-warmed-JIT tradeoff: after warm-up a
  JIT process re-optimizes the hot O(E) traversal loop with tiered/dynamic PGO, which a
  compile-once AOT image cannot. This CPU-bound traversal path is exactly what Phase 5's adjacency
  indexes replace (O(E)→O(degree)); re-measure there. Stable across two runs (287–316ms spread).
- **Publish size**: 215.9MB baseline was single-file JIT self-contained *including* workers. AOT
  win-x64 publish dir is 290.3MB *with* a 125.1MB standalone debug PDB (symbols stripped only on
  non-Windows); excluding that separable debug artifact the shippable payload is **165.2MB**
  (host native binary 29.2MB + workers/csharp 32.7MB self-contained JIT+R2R + workers/typescript
  103.3MB Node runtime/deps + protocol + skill). Worker size grew from R2R (Roslyn DLL +11MB).
  Zero managed DLLs sit next to the host binary (assertion enforced).
