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
- [ ] Enable Trim/AOT/SingleFile analyzers + RequestDelegateGenerator; burn warnings to zero
- [ ] 3a. Typed DTOs replace anonymous JSON (Mcp tools + REST endpoints + CountResponseDto); contract tests first
- [ ] 3b. McpToolCatalog: explicit ListTools/CallTool handlers; schema literals; tools/list snapshot parity
- [ ] 3c. OpenAPI dev-only: Debug-conditioned package + `ENABLE_OPENAPI` + OpenApiSupport.cs
- [ ] 3d. CreateSlimBuilder; delete throwaway LoggerFactory; MCP mode unchanged
- [ ] 3d+. Pre-existing bug found during Phase 1 review: in `--mcp` mode, console `info:` logs go to **stdout**, interleaved with protocol JSON (verified against pre-upgrade binary too). Route logging to stderr (or off) when MCP transport is active.
- [ ] Verify: zero warnings; suite; snapshot parity; Debug /openapi vs Release 404; REST smoke
- [ ] Opus + Sonnet review; findings fixed

## Phase 4 — Host AOT + worker R2R + publish/CI
- [ ] Host: PublishAot=true, OptimizationPreference=Speed, InvariantGlobalization (audit culture use first), StripSymbols non-Windows
- [ ] Worker: PublishReadyToRun (RID-conditioned), TieredCompilation/TieredPGO explicit, workstation+concurrent GC
- [ ] verify-publish.ps1: drop SingleFile/IncludeNativeLibraries flags; add no-managed-host-DLLs assertion
- [ ] Delete roslyn-aot-test/; rewrite AOT_COMPATIBILITY.md
- [ ] Prereq: VS 2022 Build Tools C++ (installing — verify before local publish)
- [ ] Verify: verify-publish win-x64 local + 4 RIDs via CI branch; MCP smoke on published AOT binary; cold-start vs baseline
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
| Cold `--version` (min/avg ms) | 61.5 / 69.2 | | |
| Time-to-listening (ms) | 1198 | | |
| Time-to-indexed, this repo (ms) | 7118 | | |
| Query latency p50, depth=2+tests (ms) | 228.3 | | |
| Alloc rate during rescan (MB/s) | (Phase 6) | | |
| Publish size, win-x64 (MB) | 215.9 | | |
