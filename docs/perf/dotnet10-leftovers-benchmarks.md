# .NET 10 leftovers — performance benchmark log

Running measurement record for the `perf/dotnet10-leftovers` effort. All numbers
gathered with `scripts/measure-worker-gc.ps1` unless noted.

## Methodology

- **Corpus**: `%TEMP%\cc-repo-copy` — the fixed 199-file / 119-`.cs` corpus used by every
  perf phase since the .NET 10 upgrade (`DOTNET10_UPGRADE_TASKS.md`).
- **Host**: Native AOT `dotnet publish -c Release -r win-x64 --self-contained` of
  `CodeContext.Api`; workers bundled (csharp = self-contained JIT+R2R).
- **Workload per run**: cold start (time-to-listening via `/healthz`, time-to-indexed via
  `/api/status`), then 5 forced full rescans (`POST /api/index/refresh`, timed to
  status-ready + operationId advance), then 20 incremental single-file touches
  (append/revert a trailing space on the median-size `.cs` file, timed from file write to
  the next `AnalysisDeltaApplier` "atomically committed generation" log line; host runs
  with `Logging__LogLevel__Default=Debug`).
- **Collection**: `dotnet-counters collect` (System.Runtime provider, 1 s refresh)
  attached to the csharp worker PID (from `/api/status` parser sessions) for the whole
  workload window. .NET 10 emits the OpenTelemetry-style `dotnet.*` counter names.
- **Runs**: 3 per configuration, medians reported. Machine otherwise idle.
- Worker env for config variants is injected via the host configuration section
  `CodeContext:WorkerEnvironment:csharp:*` (env-var form
  `CodeContext__WorkerEnvironment__csharp__KEY` — see `ParserWorkerOptions.FromConfiguration`).

## Phase 0 — worker GC A/B (July 17 2026, HEAD = de7c708, host 0.2.44)

Config A = shipped workstation + background GC. Config B = `DOTNET_gcServer=1` +
`DOTNET_GCDynamicAdaptationMode=1` (DATAS), same binaries.

Per-run:

| run | indexed ms | rescan med ms | touch med ms | alloc MB | peak heap MB | committed MB | peak WS MB | GC pause s | gen0 | gen2 |
|---|---|---|---|---|---|---|---|---|---|---|
| A-1 | 5646 | 3369 | 1260 | 2625 | 93.9 | 115.6 | 250.6 | 2.21 | 211 | 30 |
| A-2 | 5687 | 3236 | 1257 | 2625 | 104.0 | 120.5 | 256.0 | 2.16 | 213 | 32 |
| A-3 | 5513 | 3246 | 1266 | 2631 | 102.2 | 118.2 | 249.1 | 2.21 | 211 | 32 |
| B-1 | 5633 | 3326 | 1199 | 2260 | 152.1 | 201.9 | 347.1 | 0.44 | 25 | 14 |
| B-2 | 5600 | 3315 | 1204 | 2629 | 161.1 | 170.5 | 302.3 | 1.17 | 26 | 17 |
| B-3 | 5518 | 3201 | 1180 | 2226 | 168.5 | 192.6 | 337.3 | 0.44 | 29 | 15 |

Medians:

| metric | A workstation | B ServerGC+DATAS | delta |
|---|---|---|---|
| time-to-indexed (ms) | 5646 | 5600 | −0.8% |
| rescan batch med (ms) | 3246 | 3315 | +2.1% |
| incremental touch med (ms) | 1260 | 1199 | −4.8% |
| total allocated (MB) | 2625 | 2260 | −14% |
| peak GC heap (MB) | 102.2 | 161.1 | +58% |
| peak committed (MB) | 118.2 | 192.6 | +63% |
| peak working set (MB) | 250.6 | 337.3 | +35% |
| total GC pause (s) | 2.21 | 0.44 | −80% |

**Decision (pre-registered flip rule: flip only if batch time improves ≥10% AND peak WS
regresses <20%): keep workstation GC.** B's only batch win is 4.8% on incremental
touches, far under the bar, and its +35% working-set regression breaks the memory
ceiling that motivated workstation GC in the first place. The pause-time win is real but
irrelevant to a batch-pipeline worker with no latency SLO. Decision recorded in the
worker csproj comment.

**Phase 1 comparison baseline = config A medians above** (worker allocates ~2.6 GB over
the 5-rescan + 20-touch workload; touch med 1260 ms; rescan med 3246 ms).

## Phase 1 — Roslyn compilation reuse (July 17 2026, HEAD = de52436, workstation GC)

Persistent `CSharpCompilation` mutated in lockstep with the tree cache
(`Add/Replace/RemoveSyntaxTrees`) instead of `CSharpCompilation.Create` over all trees
per batch; shared static metadata references.

| run | indexed ms | rescan med ms | touch med ms | alloc MB | peak heap MB | peak WS MB | GC pause s | gen2 |
|---|---|---|---|---|---|---|---|---|
| P1-1 | 6634 | 3008 | 1230 | 2328 | 98.8 | 252.2 | 1.73 | 28 |
| P1-2 | 5575 | 3194 | 1223 | 2331 | 108.8 | 248.0 | 1.68 | 28 |
| P1-3 | 5655 | 3121 | 1228 | 2333 | 108.1 | 242.6 | 1.71 | 27 |

Medians vs Phase 0 config A:

| metric | Phase 0 (A) | post-P1 | delta |
|---|---|---|---|
| time-to-indexed (ms) | 5646 | 5655 | flat (expected — cold index builds the compilation either way) |
| rescan batch med (ms) | 3246 | 3121 | −3.9% |
| incremental touch med (ms) | 1260 | 1228 | −2.5% |
| total allocated (MB) | 2625 | 2331 | **−11.2%** |
| peak GC heap (MB) | 102.2 | 108.1 | +6% (retained binding state — accepted) |
| peak working set (MB) | 250.6 | 248.0 | flat |
| total GC pause (s) | 2.21 | 1.71 | −23% |

**Interpretation:** reuse removes the per-batch compilation
setup/declaration-table rebuild (the allocation win) but the incremental batch remains
dominated by the whole-workspace semantic re-walk + full re-emission that the v1
protocol mandates (`ReplacesWorkspace:true`). Note `workspace/index` (rescan) resets the
compilation by design, so rescans get only the shared-references win. The residual
~1.2 s touch batch on a 119-file corpus is the protocol-v2 target.

## Final (all phases, July 17 2026, HEAD = 663dbd2, workstation GC)

Adds: GraphState sharding + per-shard adjacency reuse (P2), NodesByFilePath index
consumption + copy-free `IReadOnlyList` repository reads (P3), traversal path-tracking
skip on the testing sweep (P4), watcher verdict memoization (P5).

| run | indexed ms | rescan med ms | touch med ms | alloc MB | peak heap MB | peak WS MB | GC pause s |
|---|---|---|---|---|---|---|---|
| final-1 | 6053 | 2566 | 1172 | 2329 | 99.0 | 243.2 | 1.58 |
| final-2 | 4826 | 2574 | 1164 | 2335 | 113.5 | 247.7 | 1.58 |
| final-3 | 4834 | 2812 | 1166 | 2336 | 109.0 | 247.1 | 1.61 |

### Summary table (medians)

| metric | baseline (Phase 0, config A) | post-P1 | final | final vs baseline |
|---|---|---|---|---|
| time-to-indexed (ms) | 5646 | 5655 | 4834 | **−14%** |
| rescan batch med (ms) | 3246 | 3121 | 2574 | **−21%** |
| incremental touch med (ms) | 1260 | 1228 | 1166 | **−7.5%** |
| worker total allocated (MB) | 2625 | 2331 | 2335 | **−11%** |
| worker peak working set (MB) | 250.6 | 248.0 | 247.1 | flat |
| worker GC pause (s) | 2.21 | 1.71 | 1.58 | −29% |
| **host query p50 (ms)** | **195.5** † | — | **13.5** | **−93% (14.5×)** |
| compact response (bytes) | 19457 | 19457 | 19457 | byte-identical (same SHA-256) |

† Baseline query p50 measured apples-to-apples: a fresh AOT publish of the pre-branch
develop tip (0.2.42+dee4d6e) driven by the identical script/corpus/query on the same
idle machine (historical record: 200.5–204.5 ms — consistent). Final host verified to
return the byte-identical 19457-byte compact response at 13.5 ms. The query win comes
from the host read path: no more `GetAllAsync` full-list copies inside the query loops,
path lookups served by the index, no per-hop edge-list copies, and no discarded
path-list allocations in the depth-5 testing sweep.

**Live E2E (final HEAD):** self-indexing this repository (real workers, both parsers
ready), `identifier=InMemoryDatabase&depth=2&includeTests=true` resolves with full
relationships (18805-byte compact response); a real file save produced an incremental
generation commit in 3.6 s end-to-end (watcher debounce + whole-workspace v1 re-analysis
on the larger repo — see the protocol-v2 recommendation for why this scales with
workspace size, not change size).

## Protocol v2 — go/no-go recommendation

**GO.** Replace v1 outright (greenfield; no back-compat shim), next effort.

Evidence decomposition of an incremental touch batch (119-file corpus, final HEAD):
- **Host commit + adjacency:** O(touched shard) after P2; host query path 13.5 ms —
  the host is no longer a meaningful cost in the maintenance loop.
- **Worker compilation setup:** eliminated by P1 (the −11% allocation drop); what
  remains of the ~1.17 s batch is dominated by the v1-mandated whole-workspace
  semantic re-walk (GetSemanticModel × all trees) and full re-emission (~8K facts,
  2000/chunk C#, 1000/chunk TS) plus host re-materialization of all of it.
- The cost is O(workspace), not O(change): the same touch on this repo (larger
  workspace) took 3.6 s. P1+P2 squeezed everything the v1 protocol allows; the
  remaining order-of-magnitude lives in the protocol.

Proposed v2 shape (the host side already exists):
1. Workers emit only changed files' facts with `replacesFiles: [paths]` /
   `replacesWorkspace: false` — both fields are already in the schema and validated;
   the store's file-scoped carry-over (`CommitScope.ReplacesFiles`, landed in P2)
   already applies exactly this delta shape atomically.
2. Cross-file staleness (the reason v1 re-emits everything): the worker computes a
   per-file fact-hash after each analysis; files whose emitted facts changed due to
   cross-file binding join the delta (semantic-dirty set via hash diff — sidesteps
   predicting Roslyn's dependency fan-out). The C# worker already re-walks the whole
   workspace internally, so the hash diff is cheap bookkeeping on top.
3. Symbol deletions within a still-present file are covered by per-file replacement;
   whole-file deletion is `replacesFiles` containing the deleted path with no facts.
4. Unify the C#/TS chunk-size asymmetry (2000 vs 1000) while touching the emit paths.

Remaining work for v2: both workers' emit paths, the per-file hash bookkeeping,
`AnalysisDeltaApplier` accepting mixed per-file deltas per generation, and protocol
version bump. The store, scoping, and atomicity story need no further changes.

## Protocol v2 — SHIPPED (July 17 2026, commits f5610ed / 8397398 / f2fba88)

Both workers now emit file-scoped incremental deltas on `workspace/applyChanges`
(`replacesWorkspace:false`, `replacesFiles` = hash-dirty ∪ removed, verbatim paths),
with per-file SHA-256 fact-hash diffing over the whole-workspace walk for cross-file
correctness, deterministic duplicate-id ownership (ordinal-min path — fixes the
partial-type winner-flip a reviewer proved end-to-end), commit-baseline-after-send in
both workers, and the contract documented as canonical in the schema +
`language-worker-sdk.md`. TS chunk size unified with C# at 2000. The host needed zero
changes — `CommitScope.ReplacesFiles` (landed with sharding) was already the exact
primitive. Suites: 505 default + 13 ExternalTooling, all green; dual whole-effort
review APPROVE.

### Measured outcome — honest assessment

| scenario | pre-v2 | v2 | delta |
|---|---|---|---|
| 119-file corpus: touch med (3 runs) | 1166 ms | 1170 ms | flat |
| 119-file corpus: worker alloc/workload | 2335 MB | 2222 MB | −5% |
| self repo (~250 files): warmed touch med (5-touch loop) | 1893 ms | 1921 ms | flat |
| 1500-file synthetic (~46K facts): warmed touch med (2×) | ~1681–1807 ms | ~1490–1695 ms | **≈ −11%** |
| 1500-file synthetic: cold index (2nd launch) | 6379 ms | 6623 ms | flat (v2 adds hashing) |

**The original premise was wrong about where the cost lives.** Whole-workspace
*emission* — the thing v2 eliminates — costs only tens of milliseconds even at ~46K
facts; the steady-state incremental batch is dominated by the watcher's 500 ms
debounce window plus the whole-workspace *semantic re-walk*, which v2 deliberately
keeps (the hash diff needs every file's current facts to catch cross-file changes).
Emission scales with fact count, so the win grows with repo size (~11% at 1500 files,
and it would dominate at true monorepo scale where pre-v2 serialized hundreds of MB
per save) — but at this project's target scale v2 is latency-neutral.

**What v2 actually bought:** (1) modest allocation/churn reduction now; (2) scaling
headroom — per-save wire+materialization cost is O(change), not O(workspace); and
most importantly (3) **the prerequisite for the real win**: with emission now
file-scoped and hash-bookkeeped, the next lever is scoping the *walk* itself —
re-analyzing only changed files plus their reverse-dependency closure (Roslyn/TS both
expose the needed reference info) and trusting stored hashes for everything else.
That is where the order-of-magnitude lives, and it was unreachable while the protocol
demanded whole-workspace re-emission. Recommended as the next perf effort; also
consider making the 500 ms debounce adaptive, since it is now the floor of every
incremental batch.
