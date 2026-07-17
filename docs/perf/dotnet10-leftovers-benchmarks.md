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
