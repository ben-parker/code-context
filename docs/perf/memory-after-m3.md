# Memory after M1 to M3: the M4 gate

Item M4 of [../MEMORY_FOOTPRINT_PLAN.md](../MEMORY_FOOTPRINT_PLAN.md): after M1, M1b, M2a-c
and M3, is the mapped shard (phase 8 of
[../PERSISTENT_FACT_CACHE_PLAN.md](../PERSISTENT_FACT_CACHE_PLAN.md)) still worth building?
Measurement only; no product code changed. Taken 2026-11-19 on this repository and on
jellyfin, Release JIT build of `claude/confident-cerf-5c3o1u` at `35a9e4a` (the branch has
since merged `9e2f4f0`: snapshot key creation, a null JSON-RPC result, every wake must accept
its saved state; not re-measured).

**Caveats.** Linux, 4 vCPU, 16 GB, .NET 10.0.15, Node 22.23.5. A dev Release build, not the
release recipe (AOT host, ReadyToRun worker): M2 found the AOT host 40 to 55 MB smaller in
RSS. This VM reports a 260 MB L3 cache, which inflates .NET gen0 budgets, so absolute
figures overstate a desktop. One machine. A reviewer ran test suites in another checkout
during part of the session: edit timings taken under that load are marked or dropped;
memory readings were not affected beyond noise.

## Answer in one table

RSS MB, cached start, ranges over the runs listed under Method. "Graph" is the host's live
heap reachable from its shards (gcdump, below), the part a mapped shard would take off the
heap.

| Repository | State | Host | C# worker | TypeScript worker | Total | Graph in host heap |
|---|---|---|---|---|---|---|
| code-context, plan baseline (M0, `575cd05`) | idle, 5 s after ready | 254 (true idle 340-360) | 265 | 249 | 767 | 107 MB live, nearly all graph |
| code-context, now | idle | 124-132 | 159-160 | 210-213 | 495-503 (533 once: TypeScript at 250) | 36 MB |
| code-context, now | after 60 queries | 132-136 | 159 | 212-213 | 504-508 | 36 MB (+adjacency) |
| code-context, now | hibernated | 131-138 (146 in the outlier run) | - | - | 131-138 | 36 MB |
| code-context, now | woken after two edits | 135-138 | 257-262 | 212-213 | 608-610 | |
| jellyfin, Windows baseline (phase 11, v0.11) | 5 s after ready, working set | 448 | 401 | - | 849 | not measured |
| jellyfin, now | idle | 171-173 | 322-324 | not started | 493-497 | 84 MB |
| jellyfin, now | after 60 queries | 188-192 | 322-324 | - | 512-514 | 95 MB |
| jellyfin, now | hibernated after edits | 197-208 | - | - | 197-208 | 96 MB |
| jellyfin, now | woken after a declaration edit | 204-212 | 457-504 | - | 661-716 | |

The host's graph is no longer the largest cost while workers run: on jellyfin the C# worker
is twice the host, on this repository each worker is larger than the host. When every
worker is hibernated the graph is about half of the only process left (84 to 96 MB of 200 MB
on jellyfin, 36 of 133 MB here).

## Method

- `scripts/bench/measure-memory.sh` (fixed in this item, see below). This repository: cached
  `--queries --heap` x3, cached `--heap` x1, `--no-cache --queries --heap` x3,
  `--hibernate 30` x3, `--hibernate 120 --settle 60` x2, `--hibernate 120` x2 (with the
  woken-phase fix). Jellyfin: cached `--queries --heap --top 30` x3, `--no-cache --queries
  --heap` x1, `--hibernate 120 --probe-cs MediaBrowser.Controller/Entities/BaseItem.cs
  --probe-ts none --probe-query BaseItem` x2.
- Jellyfin edits and start times: a throwaway Python driver. First start with no cache
  directory, stop, cached start, then a body edit (`_ = CanDelete();` at the top of
  `BaseItem.GetDefaultPrimaryImageAspectRatio`, root project, 28 transitive dependents;
  answer = status ready with one more edge) and a declaration edit (a new public method in
  `Emby.Server.Implementations.ApplicationHost`; answer = `query ApplicationHost.<name>`
  resolves), each twice awake, then once each after the C# worker hibernated
  (`CodeContext:WorkerHibernateSeconds=120`). One run.
- Graph share: `dotnet-gcdump` of the host 50 s after a cached start, after the 60-query
  mix, and hibernated after an edit; a throwaway TraceEvent script sums the bytes reachable
  from every `InMemoryDatabase+GraphState`. The same on the C# worker from its per-file fact
  records (`WalkedFile`, `FileFacts`), not entering Roslyn objects. The host's floor is a
  host started on a directory with one text file.
- Jellyfin: `github.com/jellyfin/jellyfin` at `df62c85` (depth 1), `dotnet restore
  Jellyfin.slnx` with the installed SDK 10.0.404 (its `global.json` asks for 10.0.0 with
  `latestMinor`): every project restored. 2,253 C# files, no TypeScript or JavaScript.
  26,363 nodes, 297,499 edges (the phase 0 Windows clone at `06695fb494`: 26,301 and
  296,362). This repository: 274 files, 9,984 nodes, 103,561 edges.
- RSS from `/proc/<pid>/smaps_rollup`. Settling as in the script's help: snapshot write,
  20 s, then stable RSS.

## Figures

### Starts and snapshot

| | code-context | jellyfin |
|---|---|---|
| First scan, no cache directory, to ready (s) | 17-23 (`--no-cache`) | 35.2 (35 `--no-cache`) |
| Cached start to ready (s) | 5-8 | 7.1-8 |
| Snapshot file (MB) | 9.21 | 24.70 |
| C# worker state file (MB) | 6.24 | 18.84 |
| Write after a cached start | yes (the TypeScript worker re-walks) | none (nothing changed, no TypeScript worker) |

### No-cache start, idle RSS MB

| | Host | C# worker | TypeScript | Total | Host live | Worker live |
|---|---|---|---|---|---|---|
| code-context x3 | 122-124 | 266-272 | 213 | 602-609 | 40-43 MB | 97 MB |
| jellyfin x1 | 189 | 535 | - | 724 | 106 MB | 276 MB |

### Edit to answer (s)

| Repository | Edit | Awake, first after start | Awake, again | After hibernation (includes the wake) |
|---|---|---|---|---|
| code-context | C# class appended to `Core/CodeGraph.cs` | 10.6 (two quiet runs) | | 12.7-13.9 |
| code-context | TypeScript class appended | 1.3 | | 2.4 |
| jellyfin | body, `BaseItem` method | 3.4 | 1.1 | 23.5 |
| jellyfin | declaration, `ApplicationHost` method | 11.2 | 3.5 | 15.5 |
| jellyfin | class appended to `BaseItem.cs` (script probe) | 19.1-20.6 | | 24.1-24.7 |

Under the reviewer's load the code-context C# probe took 15 to 26 s awake and 21 to 27 s
after hibernation. A query while hibernated took 262-331 ms through the CLI (process start
included), answered without starting a worker and without a cache label.

### Host live heap by type

Cached, idle, MB (jellyfin; this repository in brackets). The same in every run.

| Type | jellyfin | code-context | What it is |
|---|---|---|---|
| `String` | 19.6 | (7.4) | node ids, names, signatures, paths; edge endpoints shared with node ids |
| `CodeEdge` | 15.9 | (5.5) | one per edge, 56 B |
| `EdgeId` | 15.9 | (5.5) | derived edge id parts (M1b), 56 B |
| `ConcurrentDictionary` node, edge table | 13.6 | (4.7) | 48 B per edge |
| `CodeEdge[]` | 7.8 | (2.7) | adjacency runs, built on first query or commit |
| `String[]` | 6.5 | (3.8) | shared metadata values (M1) |
| `CodeNode` | 3.6 | (1.4) | |
| edge table buckets | 2.5 | (0.9) | |
| `FactMetadata` | 2.2 | (1.3) | |
| node and identifier table nodes | 2.4 | (1.0) | |
| Total live | 96.0 | (38.1-38.6) | |

The host's floor on an empty directory: 72.6 MB RSS, 10.9 MB anonymous, 1.3 MB live heap.

Reachable from the shards (the graph), MB:

| Reading | Host live heap | Graph | Of which adjacency |
|---|---|---|---|
| code-context, idle (after the start's snapshot write) | 47.9 | 36.3 | 3.5 |
| jellyfin, idle | 91.8 | 84.4 | 0 |
| jellyfin, after the 60 queries | 101.4 | 95.3 | 10.9 |
| jellyfin, hibernated after an edit | 99.8 | 95.5 | 10.9 |

Per 100k edges the host now holds 28 to 35 MB of graph (jellyfin 28.4 idle, 32 with
adjacency; this repository 35), against 111 MB at the plan's baseline (108 MB for 97,202
edges): about 3.5 times denser. Host RSS grows about 24 MB per 100k edges between the two
repositories (124-128 to 171-173 MB for 194k more edges). At that rate a repository of a
million edges holds about 300 MB of graph in the host.

C# worker live heap: 51.5 MB here, 159.6 MB on jellyfin (276 with `--no-cache`). Its own copy
of the facts (`WalkedFile`, `FileFacts`) is 13.6 and 38.6 MB of that; on jellyfin
`EdgeFact[]` is 18.9 MB for 297,499 edges, about twice what one array needs (two lists per
file, or list slack; not separated). The rest is Roslyn: syntax trees, the token cache and
symbols. Its anonymous memory (254 MB on jellyfin) is that heap, the reference images (28 MB
here per M2, not measured on jellyfin) and the gen0 allowance.

## What a mapped shard would still save

Phase 8 serves loaded and compacted shards from the snapshot file. It would take off the
heap everything in the graph rows above: `CodeNode`, `CodeEdge`, `EdgeId`, their table
entries and buckets, the id, name and path strings, the metadata sets and the adjacency
runs (the file has by-source and by-target runs). What would remain:

- the overlay: a heap shard of the facts committed since the file was written, plus two bit
  sets. Small after an edit (one file's facts). After a TypeScript worker re-walks (every
  cached start on this repository) the overlay is that worker's whole shard until
  compaction swaps a new file in; small here (12 files).
- the low-cardinality string cache and the reader's sharing tables (types, languages,
  metadata sets), a few MB at most;
- per-request decoding, which is garbage, not live memory;
- touched pages of the file, counted in RSS as file-backed, shared page cache: up to 9 and
  25 MB.

Estimate, RSS MB (graph removed, file pages added back as a range):

| Repository | State | Total now | Saving | Share of total |
|---|---|---|---|---|
| code-context | awake, idle | 495-503 | 25-35 | 5-7% |
| code-context | hibernated | 131-138 | 25-35 | 19-26% |
| jellyfin | awake, idle | 493-497 | 60-85 | 12-17% |
| jellyfin | awake, after queries | 512-514 | 70-95 | 14-18% |
| jellyfin | hibernated | 197-208 | 70-95 | 35-46% |

Against that saving phase 8 carries its risk list: a lease on every read (the reproduced
fatal access violation on a read concurrent with an unmap), per-record checks at read time,
an overlay that reproduces the scoped commit rule for rule, compaction and swap, and a
query p95 budget to meet. The heap representation it replaces is now about 160 bytes per
edge (`CodeEdge` + `EdgeId` + table node); a struct-of-arrays edge table on the heap would
reach perhaps 40 bytes per edge and take about 35 MB off jellyfin with none of those risks,
if the host's graph ever becomes the cost that matters.

## Other findings

1. **The TypeScript worker with no claimed file does not start.** On jellyfin its session
   stays `notNeeded` and no process exists (`LanguageWorkerService` skips a first index with
   no files and no loaded facts). Nothing to do.
2. **The TypeScript worker is the largest awake process on this repository** (210-213 MB for
   12 files, M2's floor) and hibernation returns it only after 900 s. Its wake costs 2.4 s.
   A shorter default for workers without `persistentState` (for example 120 s) would return
   about 210 MB for most of an idle session at almost no cost. Cheapest real saving left.
3. **The C# worker after an edit.** Idle 322 MB on jellyfin, 457-504 MB after one
   declaration edit or a wake with one; 159 to 257-262 MB here. The first declaration edit
   binds the dependents' compilations, which then stay. Hibernation returns it all; nothing
   else does. Phase 7 (dormant units) would shrink the idle figure and the wake, not the
   after-edit one.
4. **C# worker fact copy**: `EdgeFact[]` at about twice the needed size (above), about 9 MB
   on jellyfin. Cheap to check (trim, or share one list between walk and bucket).
5. **`ArrayPool<byte>.Shared` holds 7 to 11 MB in the host** after a burst (8.8 MB of
   `Byte[]` 50 s after a cached start here, 11.2 MB after a jellyfin `--no-cache` start;
   1.0-1.4 MB in other readings): pool partitions and a thread-local array; which path
   rented them was not traced. Bounded; the pool trims under memory pressure. Not worth an
   item.
6. **The host gains 25 to 35 MB when hibernated after edits** on jellyfin (anonymous 98 to
   120-128). The live heap grows 8 MB: the adjacency (11 MB) built by the commit or the
   snapshot write stays while hibernated; the rest is not live. The quiet-time trim had
   run. Small; noted.
7. **Garbage not returned**: none found beyond 5 and 6. Host anonymous memory is within
   about 5 to 30 MB of its live heap in every settled reading.
8. **Measurement script fixes** (`scripts/bench/measure-memory.sh`): `--probe-ts none` for a
   repository without TypeScript; `--probe-query` (the hibernated query was hard-coded to
   `ContextService`, which fails on any other repository); the woken reading now waits for
   the snapshot write that follows the wake edits, as the idle reading does (before: host
   231-236 MB, C# 418-429 MB, read before the write and the trim; after: 135-138 and
   257-262); `--help` printed a truncated text. With `--hibernate 30` the workers hibernate
   before the idle reading, so "awake" edits are wakes: use 120 s or more.
9. One unexplained outlier in 9 idle readings here: host 220 MB, C# worker 396 MB (run
   `--hibernate 120 --settle 60` #2; the other runs 124-132 and 159-160). Not reproduced.

## Recommendation on M4

Stop the memory work here; do not build phase 8 now. Go to M5 and the feature backlog.

- The gate's condition is not met. The host's graph is no longer the largest remaining cost:
  awake, the workers are 65 to 75% of the total (jellyfin C# worker 322 of 495 MB; this
  repository 370 of 497 MB in two workers), and the graph is 7 to 17% of the total.
- The saving is modest in absolute terms: 25 to 35 MB here and 60 to 95 MB on jellyfin. The
  totals are now about 500 MB awake and 130 to 210 MB hibernated, against 1.0 to 2.0 GB
  that motivated the plan.
- Phase 8 is the riskiest item left (a crash on read during unmap, read-time validation,
  overlay equivalence, compaction), and its saving scales at about 30 MB per 100k edges.
  Revisit when a repository of over a million edges is a target, or when hibernated host
  memory is the complaint; consider a compact heap edge table first.
- Cheaper items, in order: a shorter hibernation default for workers without
  `persistentState` (about 210 MB here for most of an idle session); the C# worker's
  `EdgeFact` copy (about 9 MB on jellyfin); phase 7 only if wake time is the complaint.
- For M5: a daemonless `query` needs to read a snapshot, not to keep it. Reading in place
  from a one-shot process needs the authenticated reader and per-record checks, but no
  overlay, compaction, swap or lease against a concurrent unmap. M5 can take that read-only
  half of phase 8, or materialize: on jellyfin the host's whole graph is 84 MB and a cached
  start, which also verifies the C# workspace, is ready in 7 to 8 s. Measure the
  materializing load alone first.

## Not done or not verified

- Windows, macOS, a machine with a normal L3, the AOT host and ReadyToRun worker.
- The mapped-shard saving is an estimate from heap reachability, not a prototype.
- Edit timings are single runs on jellyfin; the body edit was not timed on this repository.
- The branch head `9e2f4f0` (merged after these runs) was not re-measured.
- Long sessions (hours of edits) and repositories larger than jellyfin.
