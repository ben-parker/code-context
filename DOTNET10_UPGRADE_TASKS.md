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
- [ ] `global.json` (10.0.100, rollForward latestFeature)
- [ ] All csproj → net10.0
- [ ] Remove all `Microsoft.Net.Compilers.Toolset` pins
- [ ] Fix `net9.0` hardcoded worker paths in CodeContext.Api.csproj
- [ ] Packages: M.E.Hosting/Logging → 10.0.x stable; AspNetCore.OpenApi → 10.0.x; Mvc.Testing → 10.0.x; Test.Sdk → 17.14.x; Roslyn → 5.0.x (worker)
- [ ] System.CommandLine → 2.0.0 stable (fix alias ctors; smoke 4 subcommands)
- [ ] ModelContextProtocol → current stable (keep reflection WithTools; mechanical renames only)
- [ ] CI: 9.0.x → 10.0.x (release.yml ×3, auto-tag.yml ×1)
- [ ] Capture MCP `tools/list` snapshot fixture (for Phase 3b parity)
- [ ] Verify: build warnings-clean, full tests + ExternalTooling, verify-publish win-x64, MCP smoke, CLI pass
- [ ] Opus + Sonnet review; findings fixed

## Phase 2 — Remove in-process parser seam
- [ ] Delete `ILanguageParser` + `IParserDiagnostics` (all implementers are test fakes — live-graph verified)
- [ ] GraphUpdateService: delete `ProcessFileChangeOldWayAsync`, `UpdateGraphAsync`, `HandleFileDeletedAsync`, `ComputeFileHash`, `InProcessParserOutcomes`, `_parsers`; collapse fallback branches in `ProcessFileChangesAsync`/`PerformInitialScanCoreAsync`/`PerformResumableScanCoreAsync`
- [ ] StatusService: remove `_parsers` loop + `DeriveParserName`
- [ ] FileMetadata: remove `FileHash` (+ JSON context surface; confirm no REST DTO exposure)
- [ ] DI: remove `IEnumerable<ILanguageParser>` plumbing (Api ProgramHelpers)
- [ ] Tests: port surviving behaviors (scan-state aggregation, session reporting) to worker fixtures; delete legacy-only tests; mechanical CSharpWorkerTestSupport/StatusServiceScanStateTests updates
- [ ] CLAUDE.md: rewrite parser-extensibility + fallback sections (workers only)
- [ ] Verify: full suite + ExternalTooling; `/api/status` byte-identical; verify-publish edge kinds green
- [ ] Opus + Sonnet review; findings fixed

## Phase 3 — AOT-safety refactors (still JIT)
- [ ] Enable Trim/AOT/SingleFile analyzers + RequestDelegateGenerator; burn warnings to zero
- [ ] 3a. Typed DTOs replace anonymous JSON (Mcp tools + REST endpoints + CountResponseDto); contract tests first
- [ ] 3b. McpToolCatalog: explicit ListTools/CallTool handlers; schema literals; tools/list snapshot parity
- [ ] 3c. OpenAPI dev-only: Debug-conditioned package + `ENABLE_OPENAPI` + OpenApiSupport.cs
- [ ] 3d. CreateSlimBuilder; delete throwaway LoggerFactory; MCP mode unchanged
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
