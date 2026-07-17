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
- [x] Opus + Sonnet review; findings fixed
  - **Finding 1 (HIGH, both) — PDBs shipped in the release zip.** Sonnet's empirical evidence:
    `codecontext.pdb` ~124.5MB (native AOT PDB; StripSymbols is deliberately non-Windows-only)
    plus three managed PDBs (Core/Mcp/Parser.Protocol) leaking into the publish root, and
    release.yml zips the publish dir verbatim while verify-publish's guard was `*.dll`-only — so
    the shipped win-x64 zip would have been ~290MB, larger than the 215.9MB pre-AOT baseline.
    Fixed at the single source of truth: `verify-publish.ps1` now removes all `*.pdb` from the
    publish ROOT after publish/before assertions (workers/** untouched; `-KeepSymbols` switch for
    local native debugging), and the stray-file guard now covers `*.dll` **and** `*.pdb`. release.yml
    needed no change — the script cleans the exact directory CI zips. Re-ran verify-publish win-x64
    green; the shipped payload is now the real **165.2MB** (host 29.2MB; the table's figure is now
    what the pipeline actually produces, footnote corrected from "conceptually excluded" to "actively
    pruned").
  - **Finding 2 (LOW, both) — ordinal comparer.** `ContextService.cs:1025` `ThenBy(item => item.Node.Identifier)`
    now passes `StringComparer.Ordinal`, matching line 1036 and every other sort in the file. Suite
    green, no output shift (invariant mode already made the default ordinal-equivalent).
  - **Finding 3 (LOW, Opus) — test invariant mode.** Added `<InvariantGlobalization>true</InvariantGlobalization>`
    to `CodeContext.Core.Tests.csproj` so the suite runs under the same globalization mode as the
    published host. Full suite stayed green.
  - **Finding 4 (MEDIUM, both) — docs.** AOT_COMPATIBILITY.md gained the VS 2026 vswhere/PATH gotcha
    (bare `vswhere.exe` in `vcvarsall.bat` → MSB3073 at the ILC link step if the VS Installer dir is
    off PATH). CLAUDE.md's stale publish section rewritten (host = Native AOT, worker = self-contained
    JIT+R2R; correct command is verify-publish or plain `dotnet publish -r <rid>` with no
    PublishSingleFile flags; removed the "Disable AOT for testing" note). codecontext-prd.md's stale
    PublishAot+PublishSingleFile sketch corrected (single-line: drop the meaningless PublishSingleFile).
  - **Gate:** dotnet build Debug + Release 0 warnings (`-warnaserror`); full suite 410 + ExternalTooling 8
    green; verify-publish win-x64 green with the new PDB cleanup + extended assertion (0 root DLLs, 0
    root PDBs, 2 worker PDBs kept, 165.2MB payload).

## Phase 5 — Adjacency + file-path indexes (TDD)
- [x] GraphAdjacency (FrozenDictionary EdgesBySource/EdgesByTarget/NodesByFilePath + cached Nodes/Edges snapshots) + version-stamped `GetAdjacency()` on InMemoryDatabase (modeled exactly on the `GetStatistics()` benign-race cache; reads the committed `_state` so reconciliation-staged generations stay invisible until commit). Extracted the file-path normalize/match into a shared `FilePathMatcher` (used by both ContextService and the index) so they cannot drift — **finding: the real semantics are `OrdinalIgnoreCase` on every platform (NOT the store's per-OS `PathComparer`); rooted=exact, relative=exact-or-suffix**. ContextService delegates to it (behavior-preserving).
- [x] Commit A (2b91116): InMemoryEdgeRepository.GetBySourceIdAsync/GetByTargetIdAsync/GetAllAsync + InMemoryNodeRepository.GetAllAsync consume the index internally, still return fresh `List<>`; signatures/mocks unchanged, full suite passed untouched. Removes the per-hop O(E) edge scans (→ O(degree)).
- [~] Commit B (optional `Task<IReadOnlyList<T>>` copy-free reads): **DEFERRED.** The return-type change ripples across both repo interfaces + both InMemory impls + ContextService (`directEdges` is a `List` with `.AddRange`) + StatusService + **19 mock-based test files** (well over the ~15-file threshold), for a marginal gain (Commit A already killed the O(E) scans; the residual is an O(degree) list copy). `NodesByFilePath` production consumption in ContextService is deferred for the same reason (needs a new interface method → same 19-file mock ripple); the index is built + equivalence-tested (`FindNodesByPath`) and ready to wire in that future commit. **Wrap-before-return caution for Commit B:** when it wires copy-free `IReadOnlyList<T>` reads, the cached `CodeEdge[]`/`CodeNode[]` arrays must be wrapped (`Array.AsReadOnly`/`ReadOnlyCollection<T>`) before crossing the repository boundary — a bare `IReadOnlyList<T>` backed by an array can be cast back to the mutable array by any caller in the same assembly graph, which would corrupt the shared frozen snapshot and the static `Empty` singletons. (`GetEdgesBySource`/`GetEdgesByTarget` already return `IReadOnlyList<CodeEdge>` as of the Phase 5 review; the wrap is what makes that a real, not nominal, guarantee once the arrays are handed out uncopied.)
- [x] TDD (11 tests, `tests/CodeContext.Core.Tests/Repositories/GraphAdjacencyTests.cs`): invalidation on upsert/node-delete/edge-delete-by-node/generation-commit(scope replacement)/prune/reconcile-commit/rollback; version-stamped caching (same instance when version unchanged); scan-vs-index set-equivalence on a seeded (20260717) random graph (200 nodes / 600 edges) for both edge directions; NodesByFilePath set-equal to a brute-force `FilePathMatcher.Matches` filter incl. rooted/relative/case-variant/backslash/suffix paths; concurrent reader/generation-swap smoke (readers only ever see a complete snapshot). Verified the invalidation tests fail (6/11) when the version check is bypassed.
- [x] Verify: full suite **421** + ExternalTooling **8** green; 0 warnings Debug **and** Release (`-warnaserror`); **live-index byte-compare IDENTICAL** — pre-change (f4767c4) vs post-change AOT publishes both indexing the Phase-0 corpus (`%TEMP%\cc-repo-copy`, 1703 nodes / 6701 edges), `/api/context/complete?identifier=ContextService&depth=2&includeTests=true` → **19439 bytes byte-for-byte, no normalization needed, zero ordering drift**; query p50 **191.6ms** on the AOT publish (see table); `scripts/verify-publish.ps1 -RuntimeIdentifier win-x64` green (0.2.26; all 12 edge kinds, contractVersion 1, no stray DLL/PDB, skill hash).
- [x] Opus + Sonnet review; findings fixed
  - **Finding 1 (consensus, Sonnet MEDIUM / Opus LOW) — two-field cache publication race.** Both
    `GetAdjacency()` and the pre-existing `GetStatistics()` published `{payload, version}` as two
    independent fields (plain payload write + `Interlocked`-stamped version), so a reader could latch an
    OLD payload, pause while a rebuilder published a NEW payload and stamped the version the reader had
    captured, then resume, match the stamp, and return the OLD payload under a current version
    (internally-consistent-but-stale). Fixed BOTH caches identically: a single immutable holder
    (`record CachedStatistics(long, GraphStatistics)` / `record CachedAdjacency(long, GraphAdjacency)`)
    stored in one `volatile` reference. Reader takes one reference read (atomic pair), compares
    `c.Version == version`; writer publishes `new(version, payload)` via the volatile write with no
    separate stamp field. Deleted the `_cachedStatisticsVersion`/`_cachedAdjacencyVersion` fields.
    Corrected the doc comments: the only remaining benign race is a duplicate rebuild —
    staleness-under-matching-version is eliminated. The race is timing-dependent and not reliably
    unit-testable, so no flaky test was added; instead the existing 11 `GraphAdjacencyTests` stay green
    and the implementer's mutation sanity was re-run (bypass the version check → 6/11 invalidation tests
    fail → restore → 11/11 green).
  - **Finding 2 (both, LOW/defensive) — shared cached array returned by reference.** `GetEdgesBySource`
    / `GetEdgesByTarget` now return `IReadOnlyList<CodeEdge>` (was `CodeEdge[]`), so a future in-place
    `.Sort()`/`Array.Clear()` cannot corrupt the shared frozen snapshot or the static `Empty`
    singletons. Arrays satisfy `IReadOnlyList` with zero allocation; consumers' `IEnumerable`/`new
    List<>(...)` usages compiled unchanged (one test `.Length`→`.Count`). `FindNodesByPath` already
    returns a freshly built `List` so needed no change. Recorded the Commit-B wrap-before-return caution
    in the Phase 5 Commit B note above (a bare `IReadOnlyList` over an array is castable back to the
    mutable array by a same-assembly caller once arrays are handed out uncopied).
  - **Doc/checklist notes (Opus F2/F4):** (a) `NodesByFilePath` is built on every adjacency rebuild but
    has no wired consumer yet — accepted forward-wiring cost, paid now so the index is ready for Commit
    B. (b) `GraphAdjacency.FindNodesByPath`'s relative-path lookup is O(distinct-file-paths) (it scans
    the key set for suffix matches), not O(1) — note for when it is wired into ContextService.
  - Reviewers confirmed byte-identical read behavior (the atomic holder is a pure publication fix, no
    payload change), ARM-safe publication (single `volatile` reference read/write replaces the
    two-field read-then-stamp), and the verbatim `FilePathMatcher` extraction from Phase 5.
  - **Gate:** `dotnet build` Debug **and** Release 0 warnings (`-warnaserror`); full suite **421** +
    ExternalTooling **8** green; no test-duration anomalies (GraphAdjacencyTests ~2s, full suite ~7s).

## Phase 6 — Allocation passes (one reviewed commit each)
- [x] 6a. HeaderFraming: pooled buffered reads, Span header parse, ArrayPool payloads (tests first) — new stateful `FrameReader` (ArrayPool buffer, `\r\n\r\n` scan with carry-over for pipelined frames, `Utf8Parser` span Content-Length parse, pooled `FrameLease` payload returned to the pool right after the synchronous deserialize). `JsonRpcConnection` owns one reader per connection. 10 new framing tests first (split-across-read-boundaries, pipelined-in-one-buffer, tiny-buffer boundary, interleaved partial reads, oversize/EOF/dup/missing/zero-length). Added `InternalsVisibleTo` for the test-only small-buffer ctor. (f8af38a)
- [x] 6b. RepositoryFileSelector: `FileSystemEnumerable` carries attributes (kills the per-entry `File.GetAttributes` syscall; `AttributesToSkip=0` keeps Hidden visible, System/ReparsePoint filtered as before), span segment walk replacing the Split + O(depth²) `string.Join` loop (mandatory-name membership via the set's `GetAlternateLookup<ReadOnlySpan<char>>`), extension match via `FrozenSet` span alt-lookup (no `Path.GetExtension` substring). 6 new tests first (hidden dotdirs, mandatory-at-depth, deep paths, nested negation, platform case semantics, best-effort symlink skip). (0f2b97a)
- [x] 6c. ContextService (behavior-preserving): `[GeneratedRegex]` for `NormalizeCompactSignature`; `TypeRank`/`RelationshipRank` via OrdinalIgnoreCase `FrozenDictionary` (no per-node/edge ToLower/ToUpper alloc, same ranks); `IsTestMethodForTarget` ~15 interpolated patterns built once per call, not per candidate. (4cd1322)
- [x] 6d. AnalysisDeltaApplier `BuildOwnedMetadata` transfers the freshly-deserialized node/edge metadata dict in place (ownership keys stamped last so they still win) instead of cloning; `GraphState` rebuild paths (`BuildNextState`, `PruneFilesNotPresent`) presize via a ctor with `concurrencyLevel:1` (single writer) + capacity from prior counts. (b09af3a)
- [x] 6e. Cache worker supported-extensions set — `GetAllSupportedExtensions` memoizes its `Distinct().ToList()` (the worker catalog / extension map is fixed at LanguageWorkerService construction; verified), consumed read-only. (38c69cd)
- [x] Verify: full suite **437** + ExternalTooling **8** green; 0 warnings Debug **and** Release (`-warnaserror`); ContextService byte-identity E2E IDENTICAL (see below); `dotnet-counters` alloc before/after (below).
  - **Byte-identity E2E (6c gate):** pre-change (85d0cdd) vs post-change (Phase 6 HEAD) JIT hosts, both indexing the Phase-0 corpus (`%TEMP%\cc-repo-copy`), `identifier=ContextService&depth=2&includeTests=true` → **19439 bytes byte-for-byte identical**; second shape `identifier=BuildRelationshipsAsync&relation=CALLS` (method-level + relation filter) → **4890 bytes identical**.
  - **Alloc measurement:** `dotnet-counters` (`System.Runtime`, 1s interval, 19-sample window) against the JIT host during 5 forced full rescans of `%TEMP%\cc-repo-copy` (119 `.cs`). Measured JIT (`dotnet build` host) for EventPipe support; the published host is AOT. Two runs each, stable: `dotnet.gc.heap.total_allocated` summed over the window **85d0cdd 184.6 / 185.5 MB → Phase 6 140.4 / 141.1 MB (~24% fewer bytes)**; avg allocation rate **9.75 → 7.4 MB/s**; gen2 collections over the window **11 → 5**.
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
| Query latency p50, depth=2+tests (ms) | 228.3 | **293** (287–316, 2 runs) ⚠️ | **191.6** (185.8–210.9, 20 runs) |
| Alloc rate during rescan (MB/s) | 9.75 (JIT, pre-6a) | | **7.4** (JIT) |
| Alloc/5-rescan workload (MB) | 185.0 (JIT, pre-6a) | | **140.8** (JIT) |
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
- **Publish size**: 215.9MB baseline was single-file JIT self-contained *including* workers.
  The raw AOT win-x64 publish dir would be 290.3MB *with* the 125.1MB standalone native debug
  PDB Windows emits next to the host (symbols are stripped only on non-Windows RIDs). Debug
  symbols are not part of the shipped payload, so `verify-publish.ps1` now removes every `*.pdb`
  from the publish root before packaging (worker symbols under `workers/**` are kept — already
  counted in the worker payload). The **shipped** win-x64 payload — exactly what release.yml
  zips — is **165.2MB** (host native binary 29.2MB + workers/csharp 32.7MB self-contained
  JIT+R2R + workers/typescript 103.3MB Node runtime/deps + protocol + skill), measured on the
  Phase-4-review publish (release version 0.2.23). Worker size grew from R2R (Roslyn DLL +11MB).
  Zero managed DLLs *and* zero PDBs sit next to the host binary (assertion enforced).

Post-perf (Ph5) conditions (win-x64 AOT, release version 0.2.26, same corpus/query/protocol as
Phase 0/4: `%TEMP%\cc-repo-copy`, `identifier=ContextService&depth=2&includeTests=true`, median of
20 runs after 1 warm-up):
- **Query p50 191.6ms** — the adjacency indexes turned every `ContextService` hop's edge lookup
  from a full O(E) scan of the ~6.7k-edge table into an O(degree) `FrozenDictionary` lookup. This
  is **34.7% below the 293ms post-AOT number** (recovering the AOT-vs-warmed-JIT regression) **and
  16.1% below the 228.3ms warmed-JIT baseline** — the compile-once AOT image now beats warmed JIT
  because the structural win dwarfs what tiered/dynamic PGO recovered on the old O(E) loop. Tight
  spread (185.8–210.9ms over 20 runs).
- **Output unchanged**: the compact `/api/context/complete` response is byte-for-byte identical
  (19439 bytes) between the pre-change and post-change AOT builds indexing the same corpus — the
  index preserves node/edge enumeration order, so results are set- AND order-identical.
- Commit A was the only consumed change; the copy-free `IReadOnlyList` read path (Commit B) was
  deferred, so this number reflects O(E)→O(degree) alone, with per-hop `List` copies still present.
