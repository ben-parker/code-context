# Persistent fact cache: performance record

Before and after numbers for the persistent fact cache and worker hibernation
(`docs/PERSISTENT_FACT_CACHE_PLAN.md`, section 6 defines each number). The section "Phase
11" holds the after numbers. Every figure after it was taken with
`scripts/bench/measure-cache.py` as it stood on 2026-11-05, before its review.

Those later tables are a first reading. The review found limitations in how several of them
were taken (see "Limitations of these baseline numbers") and the harness has been fixed
since. Phase 11 therefore re-took v0.10.7 and the branch build back to back, on the same day
and the same commits, with the fixed harness, and compares those two columns; the "Results"
figures are not the phase 11 baseline.

The comparison baseline for phase 11 is **v0.10.7**: it contains `main`'s enumeration
patch (a1f62fc) and largest-files-first walk (f4b16d8), so a 0.11 figure against it
shows what the cache bought, not what those patches bought. v0.10.0 is kept for history.
v0.10.4 (enumeration patch only) is recorded for jellyfin cold start.

## Phase 11: v0.10.7 against the branch

Taken 2026-11-06, 16:22-18:11, with the hardened harness (carried items 1-4, 8 and the
cached-or-uncached assertion of 5; see "Harness as used" below). These tables replace the
"Results" section below as the comparison; that section stays for history.

Each cell is the median of 5 timed runs after 1 discarded warm-up, then the min-max range.
Edit rows pool 3 timed rounds of each of the 5 runs (15 values) after one priming round.
The run order per repository was all of v0.10.7, then all of the branch, back to back; runs
were not interleaved one by one. Order of repositories: bench worktree, d2-roguelike,
jellyfin. Same commit and same `git status` hash in every run of both builds of a
repository: jellyfin 06695fb494, d2-roguelike 8d4f6f9374 (with the other session's
uncommitted edits, none in an edit target), bench worktree 15bfde8e8c. Node and edge counts
equal on both builds: 26301/296362, 3818/27594, 6005/52026.

| Label | Build | Reported version |
|---|---|---|
| v0.10.7 | release asset, as in "Builds" below | `0.10.7+f4b16d8079` |
| branch | `dotnet publish` of `src/CodeContext.Api` at ce69208, `-c Release -r win-x64 --self-contained` (Native AOT host, ReadyToRun worker), under `%TEMP%\ccbench\pfc\branch-ce69208` | `0.10.32+ce69208801` |

The branch build is the publish step of `scripts/verify-publish.ps1`, not the whole script:
the script's checks were not run. Its bundled `node.exe` was copied from the v0.10.7 asset
(22.15.0, the version `prepare-node-worker-runtime.ps1` pins).

On the branch every unchanged cold start of the timed runs was checked cached by
`check_cached_start` (the cache seen `verifying` or `verified`, no `ignored`, verification
`csharp: baseline` at ready; `typescript: replaced` where that worker runs) and its first
answer carried the `cache` line. The `cold.cached` field of those runs recorded the row's
expectation, not an observation. Every `--no-cache` start showed no cache state at ready. On v0.10.7 `--no-cache` does not exist: its cold rows
are the comparison for both.

Machine load. Bench worktree, branch block: 3 runs were discarded for CPU outside the harness
averaging 11.3-12.7 percent of all logical processors (runs started 16:43-16:50). The 5 kept
runs of that block had means of 3.1-7.9 percent and peaks of up to 36 percent, so its ranges
are the least clean of the six blocks. Cause not found: a 5 s sample of all processes at
about 16:51 showed none above 1 percent of the machine. All other blocks: means of 1.6-2.8
percent, one run 5.6 percent, no discards. The gate is still the mean of a whole run (carried
item 7 was not in scope), so a short burst can sit inside a kept run. Nobody else's build or
test ran during any kept run (`disturbance.processes` empty).

### jellyfin

| jellyfin | v0.10.7 | branch |
|---|---:|---:|
| Cold start, unchanged: spawn to first answer (s) | 18.2 (18.2-18.4) | 1.14 (1.10-1.20) |
| Cold start, unchanged: spawn to `ready` (s) | 17.7 (17.7-17.9) | 5.04 (5.00-5.11) |
| Cold start, `--no-cache`: to first answer (s) | - | 18.2 (18.1-18.6) |
| Cold start, `--no-cache`: to `ready` (s) | - | 17.7 (17.7-18.0) |
| Cold start, cache directory absent: to `ready` (s) | - | 17.8 (17.5-18.1) |
| Cold start, body file changed: to answer (s) | 18.4 (18.2-18.9) | 1.15 (1.14-1.22) |
| Cold start, body file changed: to corrected (s) | 17.8 (17.7-18.3) | 12.1 (12.0-13.1) |
| Cold start, top file changed: to answer (s) | 18.2 (18.1-18.4) | 1.15 (1.12-1.19) |
| Cold start, top file changed: to corrected (s) | 17.7 (17.6-17.8) | 8.48 (8.35-8.54) |
| Warm body edit, apply (s) | 0.96 (0.94-1.03) | 0.93 (0.90-1.07) |
| Warm body edit, revert (s) | 0.95 (0.93-0.99) | 0.94 (0.89-1.02) |
| Warm top-level edit, apply (s) | 1.90 (1.75-1.96) | 1.79 (1.74-1.83) |
| Warm top-level edit, revert (s) | 1.80 (1.76-1.88) | 1.78 (1.71-1.86) |
| First edit after cold start, leaf, apply (s) | 0.89 (0.89-0.90) | 1.27 (1.26-1.29) |
| First edit after cold start, root, apply (s) | 1.24 (1.23-1.27) | 1.73 (1.72-1.78) |
| Host WS 5 s after ready (MB) | 769 (748-784) | 448 (444-458) |
| Host private 5 s after ready (MB) | 862 (839-995) | 469 (466-475) |
| Host WS after 100 queries (MB) | 1105 (931-1118) | 586 (562-722) |
| Host private after 100 queries (MB) | 1206 (1134-1309) | 636 (609-768) |
| C# worker WS 5 s after ready (MB) | 837 (821-841) | 401 (397-401) |
| C# worker private 5 s after ready (MB) | 765 (749-769) | 363 (359-363) |
| C# worker WS after all edits (MB) | 1027 (998-1048) | 794 (765-799) |
| C# worker private after all edits (MB) | 946 (915-970) | 716 (683-722) |
| Host WS, `--no-cache`, 5 s after ready (MB) | - | 760 (757-791) |
| C# worker WS, `--no-cache`, 5 s after ready (MB) | - | 848 (826-862) |
| 100-query mix total (s) | 2.38 (2.33-2.48) | 0.81 (0.74-0.82) |
| 100-query mix p50 (ms) | 5.93 (5.54-6.14) | 2.82 (2.60-2.92) |
| 100-query mix p95 (ms) | 78.0 (74.8-78.8) | 20.3 (18.6-21.9) |
| Snapshot size after first scan (MB) | - | 23.6 (23.6-23.6) |
| Worker state size (MB) | - | 17.8 (17.8-17.8) |
| Snapshot write, first scan (ms) | - | 1726 (1653-1751) |
| Snapshot capture, first scan (ms) | - | 179 (172-180) |
| Snapshot write, after edits (ms) | - | 1460 (1371-1548) |

### d2-roguelike

| d2-roguelike | v0.10.7 | branch |
|---|---:|---:|
| Cold start, unchanged: spawn to first answer (s) | 3.50 (3.47-3.55) | 0.62 (0.61-0.62) |
| Cold start, unchanged: spawn to `ready` (s) | 3.33 (3.30-3.41) | 2.20 (2.20-2.20) |
| Cold start, `--no-cache`: to first answer (s) | - | 3.53 (3.33-3.59) |
| Cold start, `--no-cache`: to `ready` (s) | - | 3.32 (3.31-3.41) |
| Cold start, cache directory absent: to `ready` (s) | - | 3.35 (3.32-3.42) |
| Cold start, body file changed: to answer (s) | 3.52 (3.49-3.55) | 0.62 (0.61-0.71) |
| Cold start, body file changed: to corrected (s) | 3.36 (3.34-3.36) | 3.04 (3.02-3.15) |
| Cold start, top file changed: to answer (s) | 3.51 (3.48-3.54) | 0.62 (0.61-0.67) |
| Cold start, top file changed: to corrected (s) | 3.36 (3.28-3.40) | 3.01 (3.00-3.08) |
| Warm body edit, apply (s) | 0.59 (0.58-0.61) | 0.59 (0.57-0.61) |
| Warm body edit, revert (s) | 0.58 (0.56-0.59) | 0.58 (0.57-0.59) |
| Warm top-level edit, apply (s) | 0.83 (0.79-0.84) | 0.81 (0.78-0.84) |
| Warm top-level edit, revert (s) | 0.82 (0.78-0.84) | 0.82 (0.80-0.83) |
| First edit after cold start, leaf, apply (s) | 0.68 (0.67-0.69) | 1.03 (1.02-1.04) |
| First edit after cold start, root, apply (s) | 0.67 (0.67-0.68) | 1.00 (0.99-1.01) |
| Host WS 5 s after ready (MB) | 120 (110-126) | 71.4 (70.9-71.9) |
| Host private 5 s after ready (MB) | 118 (108-123) | 70.7 (70.3-71.6) |
| Host WS after 100 queries (MB) | 160 (127-166) | 114 (110-115) |
| Host private after 100 queries (MB) | 158 (130-162) | 115 (112-117) |
| C# worker WS 5 s after ready (MB) | 252 (249-255) | 152 (151-152) |
| C# worker private 5 s after ready (MB) | 201 (197-204) | 117 (117-117) |
| C# worker WS after all edits (MB) | 320 (311-338) | 299 (295-303) |
| C# worker private after all edits (MB) | 242 (232-261) | 217 (214-222) |
| Host WS, `--no-cache`, 5 s after ready (MB) | - | 114 (102-119) |
| C# worker WS, `--no-cache`, 5 s after ready (MB) | - | 250 (248-254) |
| 100-query mix total (s) | 0.23 (0.22-0.26) | 0.14 (0.13-0.14) |
| 100-query mix p50 (ms) | 1.08 (1.05-1.18) | 0.80 (0.78-0.84) |
| 100-query mix p95 (ms) | 6.40 (6.07-6.61) | 2.67 (2.38-2.85) |
| Snapshot size after first scan (MB) | - | 2.43 (2.43-2.43) |
| Worker state size (MB) | - | 1.64 (1.64-1.64) |
| Snapshot write, first scan (ms) | - | 116 (114-133) |
| Snapshot capture, first scan (ms) | - | 23.0 (22.0-24.0) |
| Snapshot write, after edits (ms) | - | 110 (100-113) |

### code-context (bench worktree)

| code-context (bench worktree) | v0.10.7 | branch |
|---|---:|---:|
| Cold start, unchanged: spawn to first answer (s) | 4.86 (4.81-5.14) | 0.65 (0.63-0.70) |
| Cold start, unchanged: spawn to `ready` (s) | 4.77 (4.74-4.89) | 2.50 (2.42-2.59) |
| Cold start, `--no-cache`: to first answer (s) | - | 5.13 (4.83-5.45) |
| Cold start, `--no-cache`: to `ready` (s) | - | 4.96 (4.75-5.19) |
| Cold start, cache directory absent: to `ready` (s) | - | 4.93 (4.84-5.10) |
| Cold start, body file changed: to answer (s) | 5.04 (4.83-5.12) | 0.64 (0.63-0.75) |
| Cold start, body file changed: to corrected (s) | 4.81 (4.75-4.89) | 4.27 (4.12-4.50) |
| Cold start, top file changed: to answer (s) | 5.03 (4.98-5.09) | 0.64 (0.63-0.71) |
| Cold start, top file changed: to corrected (s) | 4.90 (4.79-4.92) | 4.20 (4.03-4.47) |
| Warm body edit, apply (s) | 0.71 (0.70-0.76) | 0.73 (0.70-0.78) |
| Warm body edit, revert (s) | 0.70 (0.69-0.72) | 0.71 (0.68-0.75) |
| Warm top-level edit, apply (s) | 1.13 (1.07-1.17) | 1.16 (1.11-1.20) |
| Warm top-level edit, revert (s) | 1.13 (1.08-1.20) | 1.17 (1.13-1.20) |
| First edit after cold start, leaf, apply (s) | 0.79 (0.78-0.80) | 1.25 (1.21-1.26) |
| First edit after cold start, root, apply (s) | 1.00 (0.99-1.01) | 1.41 (1.41-1.42) |
| Host WS 5 s after ready (MB) | 171 (164-173) | 114 (110-116) |
| Host private 5 s after ready (MB) | 179 (172-182) | 126 (118-129) |
| Host WS after 100 queries (MB) | 289 (279-295) | 176 (170-228) |
| Host private after 100 queries (MB) | 309 (299-342) | 184 (178-237) |
| C# worker WS 5 s after ready (MB) | 359 (356-362) | 186 (185-186) |
| C# worker private 5 s after ready (MB) | 307 (305-311) | 152 (151-152) |
| C# worker WS after all edits (MB) | 456 (435-460) | 390 (378-413) |
| C# worker private after all edits (MB) | 379 (355-382) | 309 (295-332) |
| Host WS, `--no-cache`, 5 s after ready (MB) | - | 173 (171-177) |
| C# worker WS, `--no-cache`, 5 s after ready (MB) | - | 365 (358-370) |
| 100-query mix total (s) | 1.16 (1.15-1.16) | 0.31 (0.29-0.33) |
| 100-query mix p50 (ms) | 2.43 (2.01-2.54) | 1.89 (1.73-2.01) |
| 100-query mix p95 (ms) | 45.1 (44.0-46.9) | 5.85 (5.27-7.43) |
| Snapshot size after first scan (MB) | - | 4.85 (4.85-4.85) |
| Worker state size (MB) | - | 3.26 (3.26-3.26) |
| Snapshot write, first scan (ms) | - | 265 (259-286) |
| Snapshot capture, first scan (ms) | - | 40.0 (38.0-42.0) |
| Snapshot write, after edits (ms) | - | 233 (211-263) |

### Reading these numbers

- Cached cold start, first answer: jellyfin 18.2 s to 1.14 s, d2-roguelike 3.50 s to 0.62 s,
  code-context 4.86 s to 0.65 s. To `ready`: 17.7 s to 5.04 s, 3.33 s to 2.20 s, 4.77 s to
  2.50 s.
- `--no-cache` and a start with no cache directory equal v0.10.7 within range. The cache adds
  no measurable cost to a start that scans everything.
- With one file changed while stopped, the first answer is as fast as unchanged and carries
  the `cache` line; the corrected graph comes earlier than on v0.10.7, but much later than an
  unchanged `ready` (jellyfin body 12.1 s, top-level 8.48 s, against 5.04 s).
- Warm edits, the phase 6 acceptance item, are within noise. Medians against the rule (10
  percent plus 0.05 s of v0.10.7), branch minus v0.10.7, apply and revert:

  | Repository | body apply | body revert | top apply | top revert | Rule |
  |---|---:|---:|---:|---:|---|
  | jellyfin | -0.033 | -0.012 | -0.113 | -0.014 | met |
  | d2-roguelike | 0.000 | -0.003 | -0.019 | -0.001 | met |
  | code-context | +0.017 | +0.012 | +0.028 | +0.039 | met |

  These follow a cached start. After an uncached start there is only the warm-up run of each
  repository (3 values each, not timed figures): body apply 0.95-0.97, 0.58-0.60, 0.70-0.78 s;
  top-level apply 1.77-1.94, 0.79-0.84, 1.10-1.15 s; no difference visible.
- **Regression: the first edit after a cached start is slower.** Leaf: jellyfin 0.89 s to
  1.27 s, d2-roguelike 0.68 s to 1.03 s, code-context 0.79 s to 1.25 s. Root: 1.24 s to
  1.73 s, 0.67 s to 1.00 s, 1.00 s to 1.41 s. That is +0.33 to +0.50 s. It was not seen after
  an uncached start: the evidence is 3 untimed warm-up leaf values (branch, one per
  repository: 0.90, 0.67, 0.78 s), each after the run that wrote the snapshot, leaf only. It
  may come with `baseline`, work the worker put off at start; the cause was not traced. The
  revert of that same edit is at v0.10.7 speed. Later first edits of a kind pay less: the
  priming rounds, medians, body apply jellyfin 1.02 s to 1.27 s, d2-roguelike 0.63 s to 0.65 s,
  code-context 0.91 s to 1.02 s; top-level apply 1.92 s to 2.29 s, 0.82 s to 0.91 s, 1.18 s to
  1.35 s. From the round after that, edits are at warm speed. No section 6 rule covers this row
  directly. Accepted for 0.11 by the coordinator: about 0.4 s, paid about once per kind of
  edit, against 3 to 17 s saved on every start. Likely the cost section 3.7 ("The first edit
  after a verification") and R8 of the plan describe (no old tree to compare after a restore),
  but not proven. Tracing it moves to 0.12 with phase 10.
- Memory after a cached start is lower, which the plan did not expect without phase 8. jellyfin
  5 s after ready: host working set 769 to 448 MB, worker 837 to 401 MB. A `--no-cache` start
  reads as v0.10.7 (host 760, worker 848 MB), so this comes from the cached start, probably
  because nothing has yet been built that a full scan builds. Use brings part of it back: worker
  after all edits 1027 to 794 MB, host after queries 1105 to 586 MB. How far it climbs in a
  long session was not measured.
- The query mix is faster: code-context p95 45.1 to 5.85 ms, jellyfin 78.0 to 20.3 ms,
  d2-roguelike 6.40 to 2.67 ms. Two one-off observations, not in `results11` and not
  reproducible from it: a `--no-cache` branch instance was also faster (7.47 against 47.6 ms),
  and all 100 code-context response bodies were byte-identical between v0.10.7 and that branch
  instance (845,889 bytes). Cause not investigated; jellyfin and d2-roguelike answers were not
  compared.
- Reading and hashing the state file on a full index: the harness does not show it apart.
  What it shows is an upper bound for all of the worker's baseline work on a cached start, the
  time from session `indexing` to `generation 1 committed`: jellyfin 1.18-1.28 s (17.8 MB state),
  d2-roguelike 0.15-0.20 s (1.6 MB), code-context 0.26 s (3.3 MB). The same span on a
  `--no-cache` start is 14.5-14.8 s, 1.33-1.43 s, 2.7-3.1 s.
- Snapshots: 23.6 MB and 17.8 MB of worker state on jellyfin, written in 1.7 s after a first
  scan and 1.5 s after edits, off the commit path. The harness's own wait for a stable snapshot
  before a stop came to about 20 s on every repository, most of it its 15 s check.

### Harness as used

Fixed for this phase: identity by parent process, a stop that survives a failed `list --json`,
temporary files removed on every failure path, the moved-aside manifest removed when the
rename fails, `wait_counts_change` failing at once when the instance dies, `ignored` fatal on a
start expected cached, an uncached-start check, `cold.cached` recorded, `--warmup` at least 1,
and hibernation no longer started with `--idle-timeout 0`. The timed runs above used the first
version of the uncached-start check (a null cache at first status), which cached starts also
show, so it did not tell the two apart. After review it was replaced by a check at `ready`
(no `verifying` or `ignored` state seen, no cache line, no verification outcome), and
`cold.cached` now records what was observed, not what the row expected. Neither change was
in force for the timed runs; the new check was run once, untimed, on the bench worktree.
Added: the row `cold_nocache` (`start --detach --no-cache`, then `query`, clock from the first
of the two); `.codecontext/cache` is now guarded on every run of a cache build, not only with
the snapshot row, and `.codecontext` is removed when the harness's runs created it.

Not fixed (carried items out of this phase's scope): 6, the clocks start before the temporary
file is written, on both builds alike; 7, disturbance gating by mean; 9, no `--check-config`
mode; 10 was covered only by one untimed run of every row on the bench worktree, both builds.

`scripts/bench/jellyfin.json`: the top-level symbol is now
`Emby.Server.Implementations.ApplicationHost`. `ApplicationHost` alone is also a property of
`CollectionFolder`, so `query` answered `matches 2/2` and the harness's exact-target check,
newer than the phase 0 runs, stopped the first jellyfin attempt on both builds in the row
`cold_changed`. Those two attempts are not used. Both cleaned up as designed: the file was
reverted, the instance stopped, `.codecontext` removed.

After all runs: `git status` of jellyfin, d2-roguelike and the bench worktree is identical to
before; none holds `.codecontext` or a `.ccbench-tmp` file; `list --json` is empty.

Not run: hibernation and wake rows (deferred to 0.12), `--require-hibernation`, cold OS file
cache, Linux and macOS, run-by-run interleaving, and the `verify-publish.ps1` step the phase 11
entry names.

## Machine

- CPU: AMD Ryzen 7 3800X, 8 cores, 16 logical processors.
- RAM: 31.9 GB.
- OS: Windows 10 Pro 10.0.19045, build 19045.6466.
- Disk: one NVMe SSD (WD WDS100T3X0C, 1 TB) holding the system, the repositories and
  `%TEMP%`.
- Python 3.13.3 runs the harness.
- Runs taken on 2026-11-05: v0.10.0 08:41-11:28, v0.10.7 and v0.10.4 14:36-15:00.
- Other agents ran builds and `dotnet test` during the v0.10.0 session. The harness
  waits until no `dotnet`, `testhost`, `vstest`, `MSBuild`, `VBCSCompiler`,
  `CodeContext.Core.Tests` or `CodeContext.FakeWorker` process exists before each run,
  samples the process list every 2 s during the run, and discards and repeats any run
  during which one appeared. Discarded: v0.10.0 d2-roguelike 1, code-context 1; one
  v0.10.0 jellyfin warm-up was cut off by a supervisor time limit and the series rerun
  from the start. v0.10.7 and v0.10.4: none.

## Builds

| Label | Build | Reported version |
|---|---|---|
| v0.10.0 | release asset `codecontext-v0.10.0-win-x64.zip`, tag v0.10.0 | `0.10.0+15bfde8e8c` |
| v0.10.4 | release asset `codecontext-v0.10.4-win-x64.zip`, first tag containing a1f62fc | `0.10.4+a1f62fc109` |
| v0.10.7 | release asset `codecontext-v0.10.7-win-x64.zip`, first tag containing f4b16d8 | `0.10.7+f4b16d8079` |

Each is unpacked outside the repository, under `%TEMP%\ccbench\pfc\<label>\`, and
called by full path. They are the release recipe by construction (Native AOT host,
ReadyToRun C# worker, bundled Node runtime for the TypeScript worker).

## Repositories and edit targets

| Repository | Where | Files indexed | Nodes, edges |
|---|---|---|---|
| jellyfin | `C:\Users\benpa\source\repos\jellyfin` at 06695fb494, restored, in place | 2251 | 26301, 296362 |
| d2-roguelike | `C:\Users\benpa\source\repos\d2-roguelike` at 8d4f6f9, in place | 114 | 3818, 27594 |
| code-context | worktree `.claude/worktrees/pfc-bench` at 15bfde8 (v0.10.0), restored once | 173 | 6005, 52026 |

Graph counts are identical across the three builds. Another session kept uncommitted
edits in other d2-roguelike files throughout; those files were avoided.

Edit targets live in `scripts/bench/<repo>.json`. The body edit inserts one call
statement after the opening brace of the file's first public block-bodied method (+1
edge; +2 on code-context, a `CALLS` and a `REFERENCES`); the top-level edit adds an empty
public method after the class's opening brace (+1 node, +1 edge). Dependents are
counted over `ProjectReference`, transitive, tests included.

| Repository | Role | File | Project, dependents |
|---|---|---|---|
| jellyfin | body, root | `MediaBrowser.Controller/Entities/BaseItem.cs` | MediaBrowser.Controller, 28 (15 direct) |
| jellyfin | top-level | `Emby.Server.Implementations/ApplicationHost.cs` | Emby.Server.Implementations, 4 |
| jellyfin | body, leaf side | `Jellyfin.Api/Helpers/HlsCodecStringHelpers.cs` | Jellyfin.Api, 6 |
| d2-roguelike | body, root | `game/rules/D2Roguelike.Rules/RunState.cs` | D2Roguelike.Rules, 4 |
| d2-roguelike | top-level, root | `game/rules/D2Roguelike.Rules/Targeting.cs` | D2Roguelike.Rules, 4 |
| d2-roguelike | body, leaf | `game/scripts/Unit3D.cs` | D2Roguelike (Godot game), 0 |
| code-context | body, root | `src/CodeContext.Core/Services/ContextService.cs` | CodeContext.Core, 3 |
| code-context | top-level, middle | `src/CodeContext.CSharp.Worker/CSharpWorkspaceAnalyzer.cs` | CodeContext.CSharp.Worker, 2 |
| code-context | body, leaf side | `src/CodeContext.Api/Commands/QueryCommandHandler.cs` | CodeContext.Api, 1 (tests) |

Jellyfin.Api is not a strict leaf: Emby.Server.Implementations references it, so its
closure is Emby.Server.Implementations, Jellyfin.Server and four test projects. It is
kept as the leaf side against MediaBrowser.Controller's 28 and because the plan's
figures used it. The strict leaves there are Jellyfin.Server (89 files, only tests
above it) and the fuzz projects. d2-roguelike has two levels, so its top-level edit is
in the root project too. code-context's Parser.Protocol has more dependents (6) than
Core but is 8 files of contracts with few method bodies, so Core is the root target.

## Method

Commands, from the worktree root:

```powershell
$p = "$env:TEMP\ccbench\pfc"
# v0.10.0 and v0.10.7, full set, per repository:
python scripts\bench\measure-cache.py --exe $p\v0.10.7\codecontext.exe --label v0.10.7 `
  --repo C:\Users\benpa\source\repos\jellyfin --config scripts\bench\jellyfin.json `
  --runs 5 --warmup 1 --max-retries 10 --out $p\results107\jellyfin.jsonl
#   d2-roguelike: --repo C:\Users\benpa\source\repos\d2-roguelike --config scripts\bench\d2-roguelike.json
#   code-context: --repo C:\Users\benpa\source\repos\code-context\.claude\worktrees\pfc-bench `
#                 --config scripts\bench\code-context.json
# v0.10.4, jellyfin cold start only:
python scripts\bench\measure-cache.py --exe $p\v0.10.4\codecontext.exe --label v0.10.4 `
  --repo C:\Users\benpa\source\repos\jellyfin --config scripts\bench\jellyfin.json `
  --runs 5 --warmup 1 --rows cold --out $p\results104\jellyfin.jsonl
```

One run is, in order:

1. Cold start. No instance runs. Spawn `codecontext query <coldSymbol> --path <repo>`
   and start a clock. Poll `codecontext list --json` every 100 ms until the root is
   registered, then `/api/status` every 50 ms. Recorded: time to a `query` exit with
   code 0 (`query` waits at most 30 s for readiness and exits 3; the harness then spawns
   it again, as an agent would, and records each attempt); whether stdout began with a
   `cache` line; time to `indexing.status == "ready"`; host file enumeration three ways:
   until the `csharp` session leaves `notNeeded`, until it shows a pid, and the OS
   creation time of the worker process minus that of the host process.
2. 5 s after `ready`: working set and private bytes of the host and each worker
   (Win32 `GetProcessMemoryInfo`, `WorkingSetSize` and `PrivateUsage`, which are what
   `Get-Process` reports as `WorkingSet64` and `PrivateMemorySize64`; cross-checked).
3. First edit after the cold start, leaf-side body file: apply, then revert.
4. 100 queries from `scripts/bench/queries-<repo>.txt`, in order, over one keep-alive
   connection to `GET /api/context/complete` (40 symbols at depth 1, 20 with
   `includeTests=true`, 20 at depth 3, 10 file paths, 10 substrings). Total, p50 and p95;
   memory after the last. Every query returned at least one match with the expected
   `matchMode` on every build.
5. Warm edits: 3 rounds of apply and revert of the body edit, worker memory (after 6
   edits), 3 rounds of the top-level edit, memory again. Each edit is timed from the file
   write to `/api/status` showing `ready` with a node or edge count different from
   before (watcher commits do not advance `indexing.operationId`), polled every 50 ms,
   so figures are 50 ms coarse. The earlier `%TEMP%\ccbench\measure.py` polled every
   200 ms.
6. Stop by `--path`. A second unchanged cold start, then the first edit in the root
   body file. Stop.
7. Cold start with one file changed: write the body edit while stopped, spawn `query`
   for the edited method, time to its answer and to `ready` with counts different from
   the unchanged graph (the corrected answer). Revert, stop. Again with the top-level
   edit, querying the edited class.

Each cell is the median of 5 timed runs after 1 discarded warm-up, then the min-max
range. Edit rows pool the 3 rounds of every run (15 values).

## Results

### jellyfin

| jellyfin | v0.10.0 | v0.10.7 | v0.10.4 |
|---|---:|---:|---:|
| Cold start: spawn `query` to exit 0 (s) | 50.1 (47.8-50.5) | 20.1 (19.8-20.9) | 34.9 (34.6-36.4) |
| Cold start: first `query` process exit (s) | 30.7 (30.6-30.7) † | 20.1 (19.8-20.9) | 30.6 (30.6-30.7) † |
| Cold start: spawn to `status == ready` (s) | 49.5 (47.3-49.9) | 19.5 (19.1-20.3) | 34.2 (34.0-35.7) |
| Host enumeration: spawn to csharp `starting` (s) | 12.1 (11.9-12.2) | 0.26 (0.25-0.35) ‡ | 0.31 (0.26-0.40) ‡ |
| Host enumeration: spawn to csharp pid in status (s) | 12.2 (12.0-12.4) | 0.43 (0.39-0.51) ‡ | 0.47 (0.39-0.50) ‡ |
| Host enumeration: host to worker process, OS (s) | 11.9 (11.8-12.1) | 0.21 (0.20-0.22) | 0.23 (0.20-0.25) |
| Host WS 5 s after ready (MB) | 763 (757-777) | 775 (741-781) | - |
| Host private 5 s after ready (MB) | 942 (858-963) | 872 (845-927) | - |
| Host WS after 100 queries (MB) | 1264 (1216-1300) | 1098 (935-1219) | - |
| Host private after 100 queries (MB) | 1429 (1281-1446) | 1294 (1137-1329) | - |
| 100-query mix total (s) | 2.70 (2.55-3.35) | 2.84 (2.67-2.98) | - |
| 100-query mix p50 (ms) | 8.90 (6.69-14.3) | 9.02 (8.43-10.1) | - |
| 100-query mix p95 (ms) | 83.7 (79.5-103) | 86.4 (85.7-98.6) | - |
| C# worker WS 5 s after ready (MB) | 971 (957-1003) | 841 (829-860) | - |
| C# worker private 5 s after ready (MB) | 888 (877-921) | 769 (756-788) | - |
| C# worker WS after 8 commits (6 body edits) (MB) | 927 (869-955) | 891 (883-918) | - |
| C# worker private after 8 commits (6 body edits) (MB) | 840 (783-870) | 813 (806-841) | - |
| C# worker WS after 14 commits (all 12 edits) (MB) | 1019 (975-1041) | 1013 (997-1022) | - |
| C# worker private after 14 commits (all 12 edits) (MB) | 931 (888-955) | 933 (916-944) | - |
| Warm body edit, apply (s) | 1.12 (1.06-1.17) | 1.09 (1.06-1.18) | - |
| Warm body edit, revert (s) | 1.11 (1.06-1.19) | 1.06 (1.01-1.12) | - |
| Warm top-level edit, apply (s) | 3.85 (3.74-4.22) | 2.10 (1.99-2.21) | - |
| Warm top-level edit, revert (s) | 3.85 (3.68-4.05) | 2.05 (1.94-2.14) | - |
| First edit after cold start, leaf, apply (s) | 1.07 (1.01-1.09) | 0.97 (0.96-1.02) | - |
| First edit after cold start, root, apply (s) | 1.17 (1.17-1.37) | 1.38 (1.33-1.38) | - |
| Cold start, body file changed: to answer (s) | 49.0 (48.4-50.3) | 20.3 (20.0-20.6) | - |
| Cold start, body file changed: to corrected (s) | 48.3 (47.9-49.5) | 19.6 (19.4-20.0) | - |
| Cold start, top file changed: to answer (s) | 48.9 (48.2-49.9) | 19.8 (19.5-20.0) | - |
| Cold start, top file changed: to corrected (s) | 48.7 (48.1-49.7) | 19.6 (19.3-19.8) | - |

† Exit code 3, `query`'s 30 s readiness timeout, in every run; the answer came from the
second `query`. On v0.10.7 the first `query` answers.

‡ At most the time of the first status (about 0.25 s after the spawn): in some or all runs
the first status already showed the session `starting`, so the cell is the polling floor,
not enumeration. Use the OS row. On 0.11, where a snapshot is served before enumeration,
this column means "host start to worker process", not enumeration.

jellyfin has no TypeScript worker session.

### d2-roguelike

| d2-roguelike | v0.10.0 | v0.10.7 |
|---|---:|---:|
| Cold start: spawn `query` to exit 0 (s) | 6.21 (5.79-9.88) | 3.89 (3.82-3.91) |
| Cold start: spawn to `status == ready` (s) | 6.05 (5.66-9.55) | 3.68 (3.56-3.71) |
| Host enumeration: spawn to csharp `starting` (s) | 1.84 (1.74-3.77) | 1.52 (1.40-1.56) |
| Host enumeration: spawn to csharp pid in status (s) | 2.00 (1.84-3.97) | 1.62 (1.56-1.71) |
| Host enumeration: host to worker process, OS (s) | 1.75 (1.57-3.43) | 1.38 (1.33-1.42) |
| Host WS 5 s after ready (MB) | 119 (93.7-123) | 114 (112-118) |
| Host private 5 s after ready (MB) | 116 (97.6-120) | 111 (109-115) |
| Host WS after 100 queries (MB) | 157 (138-165) | 161 (141-164) |
| Host private after 100 queries (MB) | 152 (141-160) | 156 (138-160) |
| 100-query mix total (s) | 0.28 (0.25-0.34) | 0.26 (0.24-0.26) |
| 100-query mix p50 (ms) | 1.55 (1.39-1.87) | 1.44 (1.25-1.48) |
| 100-query mix p95 (ms) | 7.75 (7.06-9.22) | 7.00 (6.35-7.38) |
| C# worker WS 5 s after ready (MB) | 277 (272-283) | 251 (248-252) |
| C# worker private 5 s after ready (MB) | 215 (213-218) | 199 (195-200) |
| C# worker WS after 8 commits (6 body edits) (MB) | 300 (282-302) | 281 (279-282) |
| C# worker private after 8 commits (6 body edits) (MB) | 226 (214-235) | 218 (216-219) |
| C# worker WS after 14 commits (all 12 edits) (MB) | 324 (310-331) | 305 (304-318) |
| C# worker private after 14 commits (all 12 edits) (MB) | 250 (237-255) | 237 (235-250) |
| Warm body edit, apply (s) | 0.67 (0.62-0.73) | 0.67 (0.62-0.68) |
| Warm body edit, revert (s) | 0.67 (0.62-0.68) | 0.62 (0.62-0.68) |
| Warm top-level edit, apply (s) | 1.09 (1.03-1.29) | 0.93 (0.83-0.98) |
| Warm top-level edit, revert (s) | 1.09 (1.03-1.32) | 0.88 (0.88-1.00) |
| First edit after cold start, leaf, apply (s) | 0.73 (0.72-0.78) | 0.72 (0.72-0.72) |
| First edit after cold start, root, apply (s) | 0.73 (0.72-0.83) | 0.72 (0.67-0.73) |
| Cold start, body file changed: to answer (s) | 5.73 (5.22-6.47) | 3.93 (3.80-4.14) |
| Cold start, body file changed: to corrected (s) | 5.62 (4.94-6.35) | 3.69 (3.61-4.03) |
| Cold start, top file changed: to answer (s) | 5.66 (5.23-6.18) | 3.83 (3.82-3.88) |
| Cold start, top file changed: to corrected (s) | 5.43 (5.06-5.87) | 3.66 (3.62-3.77) |
| TypeScript worker WS 5 s after ready (MB) | 145 (144-146) | 148 (146-180) |

The first `query` answered in every run on both builds. The 9.88 s v0.10.0 maximum is
the fifth timed run, taken after a discarded run and a 19-minute wait for an idle
machine; the other four are 5.79-6.50 s. It is probably a disturbed run: the harness
then detected only build and test processes by name, not other CodeContext instances
or system CPU, and its enumeration figures (3.43-3.97 s against about 1.8 s) are high
too. It is kept because it was recorded; the median is barely affected.

### code-context

| code-context | v0.10.0 | v0.10.7 |
|---|---:|---:|
| Cold start: spawn `query` to exit 0 (s) | 11.5 (11.2-11.8) | 5.43 (5.41-5.49) |
| Cold start: spawn to `status == ready` (s) | 11.4 (11.0-11.6) | 5.29 (5.22-5.31) |
| Host enumeration: spawn to csharp `starting` (s) | 1.61 (1.51-1.89) | 0.25 (0.18-0.26) ‡ |
| Host enumeration: spawn to csharp pid in status (s) | 1.72 (1.61-2.04) | 0.35 (0.32-0.39) ‡ |
| Host enumeration: host to worker process, OS (s) | 1.53 (1.35-1.75) | 0.10 (0.09-0.11) |
| Host WS 5 s after ready (MB) | 168 (167-172) | 173 (169-175) |
| Host private 5 s after ready (MB) | 184 (178-185) | 181 (176-184) |
| Host WS after 100 queries (MB) | 285 (273-303) | 279 (272-284) |
| Host private after 100 queries (MB) | 303 (291-323) | 297 (291-330) |
| 100-query mix total (s) | 1.41 (1.25-1.64) | 1.25 (1.24-1.42) |
| 100-query mix p50 (ms) | 3.53 (2.63-4.33) | 2.85 (2.66-3.13) |
| 100-query mix p95 (ms) | 57.8 (47.7-62.7) | 51.5 (49.4-56.1) |
| C# worker WS 5 s after ready (MB) | 390 (386-394) | 359 (354-360) |
| C# worker private 5 s after ready (MB) | 324 (320-328) | 308 (303-309) |
| C# worker WS after 8 commits (6 body edits) (MB) | 425 (393-433) | 392 (390-395) |
| C# worker private after 8 commits (6 body edits) (MB) | 351 (320-360) | 327 (325-331) |
| C# worker WS after 14 commits (all 12 edits) (MB) | 435 (420-458) | 423 (406-472) |
| C# worker private after 14 commits (all 12 edits) (MB) | 354 (339-378) | 350 (334-401) |
| Warm body edit, apply (s) | 0.89 (0.83-1.00) | 0.84 (0.73-0.99) |
| Warm body edit, revert (s) | 0.89 (0.84-1.05) | 0.79 (0.73-0.89) |
| Warm top-level edit, apply (s) | 1.76 (1.61-1.97) | 1.25 (1.19-1.35) |
| Warm top-level edit, revert (s) | 1.76 (1.61-1.99) | 1.25 (1.20-1.30) |
| First edit after cold start, leaf, apply (s) | 0.94 (0.94-1.04) | 0.83 (0.83-0.84) |
| First edit after cold start, root, apply (s) | 1.46 (1.40-1.56) | 1.04 (1.04-1.09) |
| Cold start, body file changed: to answer (s) | 11.2 (11.2-12.0) | 5.47 (5.38-5.72) |
| Cold start, body file changed: to corrected (s) | 11.0 (10.9-11.9) | 5.30 (5.20-5.45) |
| Cold start, top file changed: to answer (s) | 11.6 (10.7-13.1) | 5.42 (5.41-5.47) |
| Cold start, top file changed: to corrected (s) | 11.5 (10.6-13.0) | 5.15 (5.14-5.31) |
| TypeScript worker WS 5 s after ready (MB) | 181 (180-181) | 180 (180-181) |

The first `query` answered in every run on both builds. ‡ as for jellyfin: on v0.10.7
the first status showed `starting` in every run.

### Reading the baselines

- Host enumeration on jellyfin went from 11.9 s to 0.23 s with the enumeration patch
  (v0.10.4), and cold start to `ready` from 49.5 s to 34.2 s; the largest-files-first
  walk (v0.10.7) took it to 19.5 s with enumeration unchanged. On d2-roguelike,
  enumeration is still 1.38 s on v0.10.7, where it is 0.10 s on code-context.
- Warm body edits did not move between v0.10.0 and v0.10.7 (jellyfin 1.12 s and
  1.09 s). Top-level edits got faster: jellyfin 3.85 s to 2.10 s.
- The 0.11 acceptance rule (phase 11: warm edit medians within 10 percent plus 0.05 s
  of the baseline) applies to v0.10.7 as re-taken in phase 11 with the fixed harness,
  not to the cells here. For orientation, the v0.10.7 column reads: jellyfin body
  1.09 s, top-level 2.10 s; d2-roguelike 0.67 s, 0.93 s; code-context 0.84 s, 1.25 s.

## Limitations of these baseline numbers

Found by the review of the phase 0 harness and fixed in the harness since; they apply to
every recorded cell above. Neither reviewer found a recorded cell that differs from the
raw results.

- Warm edits include the first edit of their kind (round 0). After the leaf first edit,
  round 0 of the body edit is the first edit in that project in that process, and round
  0 of the top-level edit is the first top-level edit. Medians of the timed runs, apply:

  | Repository, build | body round 0 | body rounds 1-2 | top round 0 | top rounds 1-2 |
  |---|---:|---:|---:|---:|
  | code-context v0.10.0 | 0.993 | 0.887 | 1.814 | 1.710 |
  | code-context v0.10.7 | 0.989 | 0.812 | 1.350 | 1.244 |
  | d2-roguelike v0.10.0 | 0.724 | 0.648 | 1.133 | 1.087 |
  | d2-roguelike v0.10.7 | 0.673 | 0.648 | 0.931 | 0.881 |
  | jellyfin v0.10.0 | 1.122 | 1.115 | 3.850 | 3.867 |
  | jellyfin v0.10.7 | 1.126 | 1.078 | 2.152 | 2.098 |

  The fixed harness runs one priming round per edit kind first, records it as `prime`,
  and times the following rounds only.
- Edit detection polled every 52 ms (a 50 ms sleep plus the status call), so near
  deterministic latencies sit on 52 ms steps: the d2-roguelike body apply values are
  exactly 0.620, 0.672, 0.724, 0.778 and 0.832 s. Phase 11's rule (10 percent plus
  0.05 s, about 0.12 s on d2-roguelike) is about two steps wide. The fixed harness sleeps
  5 ms between polls (sleep plus status call came to about 20 ms per poll on
  code-context in an untimed check run, against 52 ms before) and
  records each detection as a bracket [last negative poll sent, first positive poll
  answered], reporting its midpoint.
- Memory after the queries, after the body edits and after all edits was sampled at
  once, with no settle, unlike "5 s after ready". The rows "after 6 body edits" and
  "after all 12 edits" are after 8 and 14 commits (the two commits of the leaf first
  edit come first); they are labelled so above. The fixed harness samples immediately and
  again after the same 5 s, and records the commit count beside each sample.
- The root first edit ran the instant its cold start returned, while the leaf first
  edit ran after a 5 s settle. The fixed harness settles 5 s before both.
- Query latency included client-side JSON parsing of the response; the fixed harness
  stops the clock when the body has been read.
- "Spawn `query` to exit 0" was detected by polling the process once per loop iteration,
  up to 0.10-0.15 s late, which is about 15 percent of a "< 1 s" goal. The fixed harness
  takes the time in the thread that waits on the process.
- The enumeration rows "to csharp `starting`" and "to csharp pid in status" cannot be
  earlier than the first status (about 0.25 s). On v0.10.7 for jellyfin and code-context,
  and in some v0.10.4 runs, they are that floor (cells marked ‡). The OS row is the only
  one that measures host start to worker process. The fixed harness records
  `firstStatus_s` beside them, leaves `sessionStarting_s` empty when the first status
  already shows a started session, and does not count `hibernated` as starting.

Other changes in the fixed harness, which can move figures slightly against these: a
file is now replaced atomically (temporary file in the same directory, then rename),
so the watcher sees a rename where it saw an in-place write; a run counts as disturbed
also when a CodeContext host or worker that is not the harness's runs, or when system
CPU outside the harness's own processes averages above 10 percent of all logical
processors (`--cpu-threshold`); disturbed runs are written to the output with
`"discarded": true` and the reason.

## Rows not applicable before the cache

| Number (section 6) | v0.10.0, v0.10.7 | Nearest comparable figure |
|---|---|---|
| Cold start from a snapshot, `cache` line | no `cache` line in any run; `indexing.cache` absent | cold start to `ready` |
| Worker resident after hibernation | no `--worker-idle-timeout` option | host memory 5 s after `ready` |
| Wake-up time to first delta | no hibernation | warm body edit, apply |
| Snapshot size and write time | no `indexing.cache` | none |
| Convergence | the test tiers of phases 6, 8 and 9 do not exist yet | none |

Two rows need no cache to run, so they are measured; they give the "before" the plan
predicts:

- Cold start with one file changed: the answer and the corrected answer both arrive
  with `ready`, because these builds answer only after a full scan. Both equal the
  unchanged cold start within its range.
- First edit after a cold start: the worker holds a full compilation after every
  start, so the first edit costs about what a warm edit costs. Exception: the root body
  edit on code-context (1.46 s against 0.89 s warm on v0.10.0, 1.04 s against 0.84 s on
  v0.10.7) and on jellyfin v0.10.7 (1.38 s against 1.09 s).

## What was not measured

- Hibernation, wake and snapshot rows: the harness runs them only when `start --help`
  lists `--no-cache` (the cache) or `--worker-idle-timeout` (hibernation); presence of
  `indexing.cache` in status is not taken as the capability, because it is absent until
  something is loaded or written. The snapshot rows ran in phase 11; hibernation and wake
  have never run and moved to 0.12. `--require-cache` makes a missing capability, a cold
  start that should have loaded a snapshot and did not, or a snapshot that is not written
  and verified in time fatal instead of "not applicable"; `--require-hibernation` does the
  same for a worker that is still running while `hibernated`. The snapshot row's "after the first scan" figure is taken on a
  dedicated start with `.codecontext/cache` absent: the harness deletes only a cache
  directory created during the same invocation; an existing one is moved aside to
  `.codecontext/cache.ccbench-aside` and back only with `--fresh-cache-ok`, otherwise the
  harness refuses to start. Before every stop that precedes a cold start, a build with
  the cache waits until the snapshot holds the current graph (a newer, verified
  sequence and no further write for 15 s).
- Convergence: a test result, not a timing; it belongs to phases 6, 8 and 9.
- v0.10.4: only jellyfin cold start and enumeration were taken (7 min); memory, the
  query mix and edits were not.
- v0.10.8: not measured; it changes only a coverage label.
- No run was taken with a cold OS file cache.

## Running the harness on shared repositories

jellyfin and d2-roguelike are measured in place, and other sessions work in them.

- An edit target must be clean in git (or named with `--allow-dirty-target <file>`) and
  must not already hold the probe statement or `ProbeMethodForMeasurement`; otherwise
  the harness refuses to start. git runs with `GIT_OPTIONAL_LOCKS=0`, so the harness
  never takes `index.lock`.
- How long a file stays edited: a warm or first edit, from the write until its commit is
  seen (about 0.6-2.2 s), then it is reverted at once. Cold start with a file changed:
  from just before `query` is spawned until both its answer and the corrected graph are
  seen, a full cold start (about 4 s on d2-roguelike, 20 s on jellyfin v0.10.7, 50 s on
  v0.10.0). Hibernation: from the write until the wake commits and the session is
  `ready`. During those windows another session reading the file sees the edit.
- The harness stops only an instance it can prove it started (parent process one it
  spawned for that start, exact root, created after that spawn; then pid and creation
  time), through that instance's `/api/shutdown?instanceId=`, also when `list --json`
  fails or has lost the record. An instance it cannot prove is logged and left running;
  one it cannot stop is logged with pid, port and instance id.
- On a build with the cache, `.codecontext/cache` in the measured repository belongs to
  the harness for the invocation. It refuses an existing one without `--fresh-cache-ok`,
  and removes what it created, `.codecontext` included, at the end.

Recovering after a hard kill (time limit, closed terminal):

1. Recovery copies. Before its first write to a file the harness saves the original
   bytes to `%TEMP%\ccbench\recover\<hash>.orig` with a manifest `<hash>.json` naming
   the file, and deletes both when the file is back. If a run was killed, the next run
   refuses to start and prints the `Copy-Item` command. Check the file (`git diff`)
   before copying the original back, then delete both recovery files. A killed run can
   also leave `.<file>.ccbench-tmp` next to a target; the next run deletes it.
2. A detached instance left running. Run `<exe> list --json` with the same executable,
   and only if the record's `rootPath` is exactly the measured root and its `startedAt`
   is from the killed run, stop it with `<exe> stop --path <root>`. Never use
   `stop --all`, and never stop by `--path` without that check: `stop --path` resolves
   the closest same-or-ancestor record, which for the code-context bench worktree is the
   main checkout's instance when the bench instance is gone.
3. A cache directory moved aside (`--fresh-cache-ok`). `.codecontext/cache.ccbench-aside`
   and a manifest `%TEMP%\ccbench\recover\<hash>.cache.json` remain; delete the
   harness's `.codecontext/cache`, rename the aside directory back and delete the
   manifest.

## Notes for later phases

- `query` waits 30 s for readiness, then exits 3 with a message naming
  `codecontext status`. On v0.10.0 every jellyfin cold `query` timed out once. The
  harness re-spawns on exit 3; a cached build should answer from the first `query`.
- Before the C# worker exists, `/api/status` lists the `csharp` session with state
  `notNeeded` and no pid (v0.10.0, v0.10.4, and d2-roguelike on v0.10.7). It becomes
  `starting` with no pid when enumeration has routed files to it, then `ready` with a
  pid and the message `initialized CSharp <version> (protocol v1)` about 0.1-0.2 s
  later, then `indexing` with `generation 1 (<n> files)`, then `ready` with
  `generation 1 committed`. On v0.10.7 for jellyfin and code-context the first status
  already shows `starting`.
- `indexing.status` turns `ready` only after every worker has committed; on
  code-context the TypeScript worker commits last. `query` polls readiness every 250 ms
  and then prints its answer, so its exit trails `ready`: by 0.12-0.34 s on
  code-context and d2-roguelike, and by 0.46-0.87 s on jellyfin v0.10.0 and 0.55-0.69 s
  on v0.10.7.
- No match is HTTP 200 with `totalMatches: 0`, not 404. An identifier with `/` or `\`
  or a worker-owned extension is a file path (`matchMode: filePath`); a name with no
  exact symbol falls back to substring matching when `exact` is absent.
- Commit detection must use node and edge counts: watcher commits leave
  `indexing.operationId` at 1 on these builds.
