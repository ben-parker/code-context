# Memory footprint plan (0.12) and Graphify-inspired backlog

Status: agreed by Ben on 2026-11-18. Work starts once the caller-comparison bug fixes
(family traversal in `ContextService`, generated-file handling in the C# worker) are
committed. This document is the durable state for a multi-session effort: update the
status table and the log at the bottom at the end of every work item.

Related: [PERSISTENT_FACT_CACHE_PLAN.md](PERSISTENT_FACT_CACHE_PLAN.md) (phases 7, 8, 9
and 10 are designed there and not started), [perf/persistent-fact-cache.md](perf/persistent-fact-cache.md).

## Why

A comparison against Graphify 0.9.112 (`--code-only`) on code-context, jellyfin and
diablo2-draft found Code Context far more accurate on "who calls this?" (about 99% of
real callers against about 61%) and far heavier: 1.0 to 2.0 GB resident per repository
against a 113 to 274 MB peak for a batch process that then exits.

Measured on this repository (9,491 nodes, 97,202 edges), Linux, Release JIT build of
`main` at `575cd05`, not the AOT release recipe:

| Process | Uncached start | Cached start | Live managed heap, cached |
|---|---|---|---|
| Host | 296 MB | 256 MB, 338 MB after 60 queries | 108 MB |
| C# worker | 537 MB | 264 MB | 51 MB |
| TypeScript worker (12 files) | 212 MB | 250 MB | not measured |

Findings that shape the plan:

- The graph is already on disk: the snapshot is 8.3 MB. The host holds it again as
  108 MB of live objects, about 13 times larger.
- Every node and edge owns a `Dictionary<string,string>` (109,162 of them, about 31 MB
  with their entry and bucket arrays, 29% of the live heap). On nodes it holds only
  `parserId` and `workspaceId`.
- Edge id strings are unique per edge and long; shard tables are `ConcurrentDictionary`.
- Workers are 60 to 70% of the total. The TypeScript worker sits idle after its scan.
- Live objects are about 60% of the host's anonymous memory and about 25% of the idle
  C# worker's. `DOTNET_GCConserveMemory=9` with non-concurrent GC changed little
  (worker 537 to 430 MB uncached, host unchanged). The remainder is not traced.
- No graph database: it would add a native dependency to an AOT host, slow each hop,
  and leave the workers, the larger share, untouched. Decision 1 of the cache proposal
  stands.

## Rules for every item

- Follow `AGENTS.md`: test first, invariants, gates (`dotnet test`, ExternalTooling for
  TypeScript worker changes, SDK suites for protocol changes, `git diff --check`).
- Answers must stay byte-identical unless the item says otherwise. No language
  toolchain enters the host.
- Each item reports memory before and after with the M0 script, on this repository at
  minimum, and records the figures in the log below.
- One item per branch commit series on the session branch; an independent review
  before an item is marked done.

## Memory items

| Id | Item | Status |
|---|---|---|
| M0 | Repeatable memory measurement script | done, `6036848` |
| M1 | Compact host heap representation | not started |
| M2 | Trace the non-live resident memory | not started |
| M3 | Worker hibernation, both workers | not started |
| M4 | Gate: mapped shard (cache plan phase 8) or stop | not started |
| M5 | Daemonless `query` from the snapshot | not started |

### M0: measurement script

`scripts/bench/measure-memory.sh` (Linux; a PowerShell twin only if cheap): starts an
instance on a given path, cached or `--no-cache`, waits for `indexing.indexed`, waits
5 s, prints per process RSS, anonymous and file-backed PSS from
`/proc/<pid>/smaps_rollup`, optionally runs a fixed query mix and prints again, and
optionally reports the live managed heap of host and C# worker with `dotnet-gcdump`
aggregated by type. It stops the instance and leaves the repository as it found it
(no `.codecontext` left behind when it created one). Output is one compact table.

Acceptance: two consecutive runs on this repository agree within 5% on every figure.

### M1: compact host heap representation

Goal: the host's live heap for this repository drops from 108 MB to 60 MB or less on a
cached start, with identical answers.

Steps, each separately measurable, smallest first:

1. Share metadata dictionaries. One instance per distinct content, per shard, for
   facts loaded from a snapshot (`SnapshotLoader`, `SnapshotShardReader`: the format
   already stores shared metadata sets and the loader expands them per fact) and for
   facts arriving in worker deltas (the delta apply path). Instances must be
   immutable to callers (`IReadOnlyDictionary`); check nothing mutates them.
2. Intern strings that repeat across facts on the delta path as the loader already
   does on its path: file paths, type, language, visibility, namespace, ids used as
   edge endpoints.
3. Find what the 60-query mix adds: the host goes from 254 to 440 MB resident (anonymous
   182 to 362 MB) while the workers do not move. Take a `--heap` reading after the queries
   and say how much is `GraphAdjacency`, how much is retained per-request allocation, and
   how much is not live. Fix what is cheap; report the rest.
4. Measure what the edge id strings and the `ConcurrentDictionary` tables cost after 1
   and 2, and what `GraphAdjacency` adds after queries. Propose, do not implement, the
   next cut (derived edge ids, frozen tables) with figures; the coordinator decides.

Out of scope: format changes, mapping, changes to public DTOs.

Tests: a store test that facts with equal metadata share one instance after a load and
after a commit; the existing snapshot round-trip, convergence and byte-identical
response tests stay green.

### M2: trace the non-live memory

Investigation, read-only apart from throwaway instrumentation. Questions: what the
roughly 150 MB of anonymous memory outside the live heap is in the idle C# worker after
a cached start, and the roughly 70 MB in the host; whether it is GC-committed free
space left by the start-up spike, native allocations (Roslyn metadata images, JIT
code), or something else; which setting or code change returns it (candidates:
`GCHeapAffinitizeMask`/heap count, `DOTNET_GCgen0size`, `System.GC.RetainVM`, an
explicit compacting collection after the first commit, `TieredCompilation` settings,
streaming instead of buffering large deltas and the state file). Compare against the
published R2R worker and AOT host if they can be built here.

Deliverable: a short section in `docs/perf/` with figures and a recommended change,
which becomes its own small item.

### M3: worker hibernation, both workers

Cache plan phase 9 as designed (section 3.9 and the phase entry), extended to workers
without `persistentState`: an idle TypeScript worker exits, the host keeps serving its
facts from the store, and the next change to a file it claims starts it and it
re-walks and replaces, as on any start. Phase 9's "out of scope: hibernating workers
without the capability" is lifted; its tests and acceptance stay, with twins for the
TypeScript worker. The carried supervisor race items in section 9 of the cache plan
are looked at here as that plan says.

Propose a default idle time with a measurement of wake cost (C# verification, and
TypeScript re-walk, about 2 s on this repository).

Acceptance: on this repository, idle total resident drops from about 770 MB to the
host alone; an edit after hibernation yields the corrected graph; a query in between
answers with no worker process; status shows `hibernated`, not failed.

Dependency: cache plan phase 7 (dormant units) is not required and is not scheduled;
reconsider it only if wake time is the complaint.

### M4: gate

After M1 to M3, measure again on this repository and on jellyfin. Mapped shard (cache
plan phase 8) goes ahead only if the host's graph memory is still the largest
remaining cost. Its carried items (lease per read, the reproduced crash on a read
concurrent with unmap, per-record checks) are the first steps if it does.

### M5: daemonless query

`codecontext query` answers from the newest authenticated snapshot when no instance is
running and the caller asks for it (flag to be designed), labelled as unverified
cache, without starting workers. Needs a staleness statement that is honest: at least
snapshot time and whether any indexed file's size or mtime moved. Depends on M4's
outcome for whether it reads a mapped or a materialized shard.

## Feature backlog (after the memory items)

| Id | Item | Notes |
|---|---|---|
| F1 | Whole-graph queries: shortest path, transitive `affected`, hubs | Over existing adjacency; REST, MCP and CLI together; contract and skill updates |
| F2 | Diff and PR impact | `affected` seeded from a git diff's changed symbols |
| F3 | Loud incompleteness | `query` says when a project is unrestored or has many unresolved names; `complete` with hundreds of unresolved names is not `complete` (jellyfin lost 29% of edges before `dotnet restore`; diablo2-draft ModBuilder reported `complete` with 292 unresolved names) |
| F4 | Labelled possible callers | Unresolved same-name calls shown as `possible`, never mixed with bound edges |
| F5 | Generic tree-sitter worker | Out of process, through the worker protocol, edges labelled inferred; breadth for languages with no semantic worker |
| F6 | Communities and overview report | Lowest priority |

## Log

| Date | Item | Result |
|---|---|---|
| 2026-11-18 | Baseline | Table in "Why" above |
| 2026-11-18 | M0 | `scripts/bench/measure-memory.sh` (+ `gcdump-types.cs`, `queries-<repo>.txt`). Cached baseline, this repo, RSS MB: idle host 254, csharp 265, typescript 249, total 767; after 60 queries host 440, total 915. Live heap: host 107.3 MB (String 53.5, `Entry<string,string>[]` 18.5, `Dictionary<string,string>` 8.3, `Int32[]` 5.9, `CodeEdge` 5.3), csharp 49.6 MB. No-cache: host 313, csharp 551, typescript 218; live heap host 183, csharp 122. Consecutive runs within 5%; outliers seen: typescript after queries once 249 instead of 212, one first run host 289 idle |
