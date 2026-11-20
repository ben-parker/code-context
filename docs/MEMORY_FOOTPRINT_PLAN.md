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
| M1 | Compact host heap representation, steps 1 to 3 | done (`3a8b0d4`, `4c1623b`), reviewed |
| M1b | Derived edge ids (about -44 MB live); frozen tables skipped (3-4 MB for a second shard type) | done (`79f6304`, `5c372a7`, `dc2c654`, review fix `6709999`), reviewed |
| M2 | Trace the non-live resident memory | done, [perf/memory-non-live.md](perf/memory-non-live.md) |
| M2a | Quiet-time trim (aggressive compacting GC after bursts), host and C# worker | done (`23bee8a`), reviewed |
| M2b | Snapshot writer: no re-decode of snapshot-backed shards in `CheckPersistable` | done (`cb5de1e`), reviewed |
| M2c | C# worker: fallback runtime references only when a file falls back (-57 MB) | done (`fb0e42f`), reviewed |
| M2d | C# worker: reference assemblies held as metadata only, read through a stream closed before use so no handle stays on the file (jellyfin: worker anonymous memory 417 to 368 MB; the further modules of an assembly of several modules are read the same way, through the guard, and reported as inputs, since handing such an assembly over as a file left a handle on its modules that blocked deleting them on Windows); run on Windows in pull-request CI | done in the final pass (`70b5dcb`) |
| M2e | Query path allocation (72 MB per 60 queries in three `ContextService` methods) | done in the final pass (`c56facb`: 71.0 to 26.9 MB per 60 queries) |
| M0b | Measurement script waits for the post-start snapshot write and V8 shrink before reading | done (`4cd7ffb`) |
| M3 | Worker hibernation, both workers | done (`1d30ab1`..`8bd1ead`, follow-ups `e4f7bc2`..`ee66822`), reviewed (follow-ups too; review fixes `d714491`, `e5c38e2`, `e21b675`) |
| M4 | Gate: mapped shard (cache plan phase 8) or stop | gate measured (`docs/perf/memory-after-m3.md`, `95190ce`): mapped shard not built; its index sections removed in format 3 (`a937f71`); see log |
| M3c | Hibernation time by wake cost; C# worker shares a walk's edge list with its bucket | done (`f2dfa10`, `3881e34`, merge `1ed0b2c`), reviewed in the final pass |
| M5 | Daemonless `query` from the snapshot | done (`0797508`..`25a6d2c`), reviewed, fixes `b2d1aa9`..`650e884`; opt-in (`query --snapshot`); run on Windows and macOS in pull-request CI, fixes `c575d72`, `d95f421` (see log); one limit left on each of those platforms; reported inputs are dated against when their worker began reading them, not the capture (review fix, see log) |
| M5f | Before the one-shot path may become a default on Windows or macOS: each reported input carries an identity a copy cannot forge (Windows: the NTFS update sequence number, `FSCTL_READ_FILE_USN_DATA`, one handle and one call per input, needs the volume's change journal; or size plus a content hash, a read of every input, megabytes for a referenced assembly; or, cheaper and partial, the size and last write time recorded and compared for equality, which catches a copy whose source differs in either. macOS: the status change time read with `lstat`, one call per entry as on Linux, but a hand-laid `struct stat` per architecture) | open |

Order: final review pass (closing step 2) is done (2026-11-19, see log); the feature backlog is next.

M1 review findings carried into M1b: build the metadata `string[]` directly on the commit path (5-10% slower large commits measured); a duplicate metadata key must not drop the request; the snapshot reader's sharing tables need a bound before readers are kept alive (M4).

Known in this container, present before this work (`42d2d03`): 7 tests failed in the full
suite (`SnapshotKeyTests.FileThatIsNotAKey_IsRefused`, 3 `InitCommandHandlerTests`,
1 `QueryCommandHandlerTests`, 2 `DetachedStartOrchestratorTests`). The first was the umask
(fixed in the test, see log); the other six used Windows paths as their root, which off
Windows are relative file names, and now spell the root as the platform does (`72050b4`).

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

### M1b: derived edge ids, and the next density step

As built: see the log. Not built, and the next step if the host's graph has to shrink again (measured in the
final review on a synthetic shard of 200,000 nodes and 800,000 edges, and by counting fields):

An edge costs 168 bytes before its strings and metadata: `CodeEdge` 56, `EdgeId` 56, a `ConcurrentDictionary`
node 48 and its bucket slot 8. Two changes take about 65 of them. (1) Plain `Dictionary` tables for a committed
shard, about 24 bytes per edge and 20 per node. The tables are concurrent only for the legacy in-place
mutators (`UpsertNode`, `UpsertEdge`, `RemoveNode`, `RemoveEdgesTouchingNode`, `ReplaceAll`, `AsHeapShard`),
which no production path calls; they go first, or become copy-on-write. This is also what makes a one-file
commit cheap: today it copies the whole shard into new tables, 0.9 s and 76 MB allocated for that shard, where a
`Dictionary` can be cloned without rehashing. (2) Folding `EdgeId` into `CodeEdge` (the two pieces, the hash
and the canonical flag beside the endpoint ids it already holds), about 40 bytes per edge; the table key is
then the edge or a string, and the snapshot reader and writer, which build and read the parts, change with
it. Edge metadata is the other large share and has no cheap cut: line and column make it nearly unique per
edge (40,721 instances for 90,263 edges on this repository).

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

As built, where it differs from cache plan 3.9: the setting is
`CodeContext:WorkerHibernateSeconds` (default 900 s, 0 off; no CLI flag, no
`WorkerIdleMinutes`). `WorkerHibernation` arms a timer at the end of each worker operation
and runs the check as a coordinator maintenance command; the worker service decides under the
worker's lock. A `persistentState` worker hibernates only when its state is recorded with its
current shard, the file exists and the written snapshot matches the store, so with
`--no-cache` or a read-only instance it stays up; other workers always may. The first
operation on a hibernated worker re-indexes it (baseline where there is one); a replacement
subsumes the pending batch, an accepted baseline is followed by the batch's content changes as
touches. A syntax tree wakes through a coordinator mutation command (busy while it runs).
Waking shows as `starting` with the message `waking from hibernation`; there is no `waking`
cache label (answers while hibernated are current, not cached). Not built: crash restart from
the last capture. The supervisor race of section 9 was not reproduced; hibernation found that
the host's JSON-RPC reader refused the `"result": null` every worker answers to `shutdown`
(masked by stop and hibernate, both of which fell back to EOF); the reader now accepts it.

Follow-ups after the M3 review: a failed session (a request answered `complete: false`, the
process still up) does not hibernate, so status keeps `failed` and its error; a wake whose
index fails leaves the worker not hibernated but with its wake incomplete, so the next
operation re-indexes, and a wake records its baseline answer as an index does. A hibernation
waits 1 s for the `shutdown` answer and 1 s for the exit after EOF, then kills
(`HibernateStopTimeout`; a stop keeps its 5 s waits). Host memory after hibernating: the
excess was not hibernation's own but replaced shards kept alive by the start-up step's
loaded view and by the composite adjacency cache, plus glibc arena memory; both holders
are dropped, the quiet-time trim calls `malloc_trim(0)` on Linux (host only), and a
hibernation requests a trim although it allocates little. This repository, host RSS MB,
idle then two awake edits and a hibernation: 134 to 165 before, 125 to 132 after. The
convergence walk with hibernate and wake before every step is
`CSharpWorker_HibernateAndWakeBetweenEveryStep_EqualsAFreshScan` (real C# worker, 16
steps, about 24 s).

### M4: gate

After M1 to M3, measure again on this repository and on jellyfin. Mapped shard (cache
plan phase 8) goes ahead only if the host's graph memory is still the largest
remaining cost. Its carried items (lease per read, the reproduced crash on a read
concurrent with unmap, per-record checks) are the first steps if it does.

Final review, after the gate decided against it: the snapshot sections only a mapped shard
would read (hashes, adjacency runs, name table, cross-shard edges, histogram; 15-17% of a
file) and the reader code and tests for them were removed with format version 3
(`docs/perf/memory-after-m3.md`, "Snapshot format 3"). Building the mapped shard later starts
by restoring them from history under a new format version.

### M5: daemonless query

`codecontext query` answers from the newest authenticated snapshot when no instance is
running and the caller asks for it (flag to be designed), labelled as unverified
cache, without starting workers. Needs a staleness statement that is honest: at least
snapshot time and whether any indexed file's size or mtime moved. Depends on M4's
outcome for whether it reads a mapped or a materialized shard.

As built. With `--snapshot` (or `CodeContext:QueryFromSnapshot=true`; `--start` overrides it) and no
instance registered for the path, `query` opens the newest snapshot that authenticates (`OneShotSnapshot`:
same key, checks, refusal budget and candidate loop as a host's load; no key, directory, lock,
registration, write or worker), refuses one written while its host was unverified or holding a shard
whose worker reported no inputs, stat-checks it, materializes it with `SnapshotLoader` and answers
through `ContextService` with the endpoint's defaults and ASP.NET's own JSON options, labelled `cache
snapshot`. Anything else falls back to today's start, with one stderr line saying why. Without the flag
or setting, `query` starts an instance as before M5. Measured 2026-11-19, Linux, 4 vCPU; this repository
(clone at `6f37e17`, 355 checked entries, 9.3 MB snapshot) and jellyfin (2,253 files, 2,838 checked
entries, 24.7 MB snapshot); wall time of the CLI process and its peak RSS; one-shot over 5 runs.

| | JIT build, code-context | JIT, jellyfin | AOT host, code-context | AOT, jellyfin |
|---|---|---|---|---|
| One-shot answer, nothing left running | 1.19-1.31 s, 189-222 MB | 2.07-2.23 s, 274-293 MB | 0.37-0.41 s, 105-114 MB | 1.06-1.32 s, 195-197 MB |
| Today's cold `query` (`--start`) | 1.60 s; leaves 646 MB (host 257, C# 164, TS 225) | 2.95 s; leaves 790 MB (312, 478) | 0.71 s; leaves 577 MB | 1.47 s; leaves 645 MB |
| Query to a running instance | 0.28-0.35 s | 0.28-0.35 s | 0.03 s | 0.05-0.07 s |

Decisions:

1. Freshness, option (a), by what the workers say they read. The snapshot holds no per-file size, time
   or hash (they are in the C# worker's state file), so the check is by time against the capture, of
   exactly: every directory, claimed file and `.gitignore` the scan's own selection walk includes, plus
   each shard's `inputs` as its worker reported them (protocol `analysis/delta.inputs`, additive and
   optional, version still 1; stored in snapshot format 2): files that must still exist and be no newer,
   paths that must still be absent. The C# worker reports what it checks itself before reusing what it
   read (project model stamps, `Directory.Build.*`, `Directory.Packages.props`, `global.json` and `.git`
   probes up to the repository top, restore data where the project puts it, listed directories, every
   referenced assembly); the TypeScript worker its configs with `extends` and `references`, what module
   resolution looked at (TypeScript's failed and affecting locations), the program's library and
   declaration files under `node_modules`, and the directories it listed. A time after the capture less
   2 s counts. The time is the status change time on Linux (`statx`), which no user tool sets back; on
   Windows the file system's change time read from the entry itself (a directory's times in its parent's
   listing are not kept current); the later of write and creation time on macOS; a time ahead of the clock counts as a change but never
   makes a host rewrite. The root's time after `.codecontext/` was made is recorded, so making it is no
   change. Stat pass: 41-47 ms warm on jellyfin (89-233 ms inside a cold one-shot, 4,236 entries with the
   inputs), 0.74-0.78 s on a synthetic tree of 100,000 files in 5,000 folders, as before. Not seen: on
   Windows, a file replaced by a copy of the same size with older times (a copy carries its source's change
   and last write times over); on macOS, content rewritten in place with an older time set afterwards (both
   tested as such, see M5f); an edit landing
   between a worker's read and the capture when the host then stops before its watcher reports it; a
   change of configured ignore patterns or of the installed workers.
2. Opt-in (`--snapshot`, or the setting), after the review found stale answers labelled current under
   the first check. One-shot costs 0.35-1 s more per query than a resident AOT instance, but leaves
   nothing behind (against 580-790 MB and the verification's CPU), and the first edit makes the next
   query fall back and start an instance.
3. Materializing load kept. Phases (JIT harness, cold/warm): authenticate 27/9-13 ms here,
   66/22 ms on jellyfin; materialize 815/348-382 and 1,526/718-837 ms; first query 172/89-138 and
   845/318-452 ms (adjacency build). The AOT figures above are within the comparison's 110-270 MB
   peak; a read-only mapped reader would cut most of the materialize time, not worth phase 8's
   reader now.

Also: after a verification the writer rewrites, in the background, a loaded snapshot older than some
input (a checkout and back), else such a repository never answers from the snapshot again; never before
the scan verified, and the check no longer runs before the scan (a cached start of the 100,000-file tree
went from 3.24-3.28 s to 2.50-2.79 s). A snapshot captured while no scan had verified the store is
flagged and rewritten once one has. A shard of a worker that sends fingerprints but was written without
its state is refused. A running instance now also follows `tsconfig.json`/`jsconfig.json` edits (project
inputs of the TypeScript worker, which re-reads its configs); other config names reach it at its next
start. Not built: a stale answer with a count (option b).

## Closing steps and review process (agreed 2026-11-19)

1. Finish M3c (branch `m3c`, worktree `/home/user/cc-m3c`) and M5, each with an independent review.
2. Final adversarial and simplification pass over every commit on this branch since the plan started: correctness, allocation efficiency, hot paths, abstraction and re-use. Done by the strongest available review agents, one per area (host store and edge ids; snapshot writer, loader and key; trim and hibernation; C# worker; CLI and daemonless query). The coordinator verifies each finding; the same agent then fixes the agreed ones test-first, with before and after numbers for any allocation or hot-path claim. Close with the full suite, `Category=ExternalTooling` and both SDK suites.
3. Then the feature backlog, one feature at a time: implementers and reviewers as for the memory items, and for each feature a final comprehensive review by the strongest available review agent, which also fixes what the coordinator agrees with.

## Feature backlog (after the memory items)

| Id | Item | Notes |
|---|---|---|
| F1 | Whole-graph queries: shortest path, transitive `affected`, hubs | Over existing adjacency; REST, MCP and CLI together; contract and skill updates. Done (`40b854a`..`F1 final review` in the log; see "F1 as built") |
| F2 | Diff and PR impact | `affected` seeded from a git diff's changed symbols |
| F3 | Loud incompleteness | `query` says when a project is unrestored or has many unresolved names; `complete` with hundreds of unresolved names is not `complete` (jellyfin lost 29% of edges before `dotnet restore`; diablo2-draft ModBuilder reported `complete` with 292 unresolved names) |
| F4 | Labelled possible callers | Unresolved same-name calls shown as `possible`, never mixed with bound edges |
| F5 | Generic tree-sitter worker | Out of process, through the worker protocol, edges labelled inferred; breadth for languages with no semantic worker |
| F6 | Communities and overview report | Lowest priority |

### F1 as built

Surface. `GET /api/graph/path?from&to[&maxDepth=10][&relation][&includeTests]`,
`GET /api/graph/affected?identifier[&maxDepth=5][&relation][&includeTests][&limit=20]`,
`GET /api/graph/hubs[?limit=20][&relation][&type][&within][&includeTests][&includeExternal]`; MCP `get_path`,
`get_affected`, `get_hubs` with the same names; `codecontext query path <from> <to>`, `query affected <id>`,
`query hubs` (`--depth` is maxDepth, `--within` and `--type` are hubs only). `maxDepth` 1-20, `limit` 1-100.
`status` lists the three endpoints. All go through `ContextService` (partial `ContextService.Graph.cs`, interface
`IGraphQueryService`, the same singleton registered for both interfaces; no resolver interface, no `EdgeCount`);
`query --snapshot` answers them through the same service (`OneShotSnapshot.Graph`). Responses reuse
`CompactCodeNode` (subclasses add hop, relation, via, dependents), canonical identifiers, `cache` and `coverage`.

Decisions where the backlog was silent:

1. Identifiers resolve exactly (canonical identifier, exact or qualified name, file path), never by substring: a
   traversal from a guessed symbol is worse than a miss. Ambiguous or missing endpoints traverse nothing and exit 2.
   A type stands for itself and its members (containment, transitively); a file for its symbols.
2. Hops: usage edges forward (containment and method-family kinds are not usage, as in `uses`); dispatch from a
   member reached by a call, or started from, to its implementations and overrides, downward only, free (0 hops), not after a call
   marked `dispatch: static`, not through a library member (as `usedBy` stops families there); containment only at
   an endpoint, never mid-path (a use of a type does not reach what its members call). `relatedItems` proximity is
   never used. A relation filter selects usage kinds; dispatch is followed whatever it says.
3. Path: one shortest path, counting relationship hops; ties broken by canonical edge order, so answers are
   deterministic. Test files are not passed through unless `includeTests` (endpoints may be tests). No path: the
   explanation names the request's own filter when dropping it finds one (`excludedBy`), else the depth limit when
   something lies beyond it (`depthLimited` is exact: some node unreached within the limit is reachable), else a
   path the other way round, else what the graph does not record. The filter is read from the path found with
   every filter dropped (one search; none further when it finds nothing): a hop of an excluded kind blames
   `relation`, a symbol in a test file `tests`. Only a path that needed both gone has each dropped on its own, and
   then the text says whether one alone is enough, either is, or both must go. With both filters given and each
   hiding a different path, the one the nearest path needs is named.
4. Affected: impact moves from a symbol to each source of a usage edge into it; without a relation filter, to each
   implementation or override of it (it must stay compatible with a changed declaration); and to each source of a
   non-static usage edge into a declaration it implements or overrides, at any height (`through` names it). Not to
   siblings implementing the same declaration, the declaration itself, containers or neighbours. Seeds (a type's
   members included) are never listed. Unless `includeTests`, test-file symbols are counted (`testsHidden`), not
   listed, and not moved on from, as a path does not pass through them.
5. Hubs: distinct other symbols with a counted usage edge into the symbol, then their distinct files; no dispatch
   or member aggregation (a type's count is uses of the type). Test files are neither ranked nor counted unless
   `includeTests`; library symbols are ranked only with `includeExternal`, never under `within`. `within` is a
   root-relative directory or file, matched on whole segments; one outside the root is refused. Computed per call,
   no degree cache.
6. One generation: a query takes the composite adjacency of the published map once and reads only it: the walks read
   its shards, and the repository reads it makes (names, identifiers, files, copies of a declaration) see it pinned
   for the query's async flow (`InMemoryDatabase.PinPublished`), so the answer holds one map by construction. The
   version check alone was not enough: a commit publishes its map before it bumps the version, and two readers
   building the composite in that window could cache the new map under the old version, so a query walked one map,
   resolved through the other and passed the check (review finding on PR 9; reproduced by
   `ACommitPublishedButNotYetCounted_IsNeverMixedIntoAnAnswer`). Context queries (single, multi, compact and full,
   over REST, MCP, the CLI and `query --snapshot`) take the same pin as the first statement of each public method of
   `ContextService` (`OneGeneration`); a query called by another keeps the caller's pin, so the answers of one multi
   query share a generation, and a graph query inside a pin answers from it once, without the retry.
   `ContextGenerationTests` calls every method of `IContextService` and `IGraphQueryService` and fails if one reads
   the store outside a pin. Taking the pin is the read that chooses the facts, so a context query reads its "before"
   cache notice just ahead of it, and a multi query labels each answer its own notices left bare with the notice read
   before its pin (a graph query retries when a commit lands after its pin, so its notice is never too late). The pin
   costs one execution-context change per query (72 B) and ends with it. Status serves counts, no facts, and takes no
   pin: its graph counts come from one store call, but its file counts come from the file metadata (outside the
   generations) and its coverage from the annotations, so its parts can be read moments apart. The syntax-tree endpoint
   asks a worker, and the one-shot store is published once before any query. Known, label only: coverage comes from
   the annotations, read when the answer needs it and not pinned, so an answer's coverage label can be newer than its
   facts (by every commit that lands while the pin is held). A graph query's answer is still kept only if the
   mutation version did not move: a commit makes the query read again, so it answers from the newest generation when
   it can, and a legacy in-place write (tests only; it changes a shard where it stands) is read again once counted.
   After 3 changed attempts the query fails with `GRAPH_CHANGING` (REST 503). A walk looks at the request's
   cancellation token once per 1,024 symbols visited, so it stops when the caller goes away. The CLI waits 60 s for a
   graph query (5 s for `query`), then names the query and how to narrow it.
7. Refusals: an option a query does not take is an error naming the query it belongs to (REST: checked before
   binding, 400 `INVALID_GRAPH_QUERY`; MCP: unknown names, wrong JSON kinds and missing required arguments are
   `isError`, unlike the lenient context tools; CLI: an option the subcommand does not read, exit 1). `query -- path`
   queries a symbol named like a subcommand.
8. The test-file rule is one function, `RepositoryPaths.IsTestFile`, used by the graph queries and by `--tests`
   evidence, test-file discovery and candidate ranking. It judges the root-relative path (those matched the absolute
   path before: a checkout under a folder named `tests` made every file a test) by the names test runners discover
   tests with, not by substring: each directory name, and the file name without its extension, is split at `.`, `-`
   and `_`, and the file is a test when a piece is `test`, `tests`, `spec` or `specs` in any case (`tests/`,
   `__tests__/`, `Widget.Tests/`, `widget.test.ts`, `widget.spec.ts`, `widget_test.go`, `test_widget.py`,
   `widget_spec.rb`), ends in camel-case `Test` or `Tests` (`WidgetTests.cs`, `WidgetTest.java`,
   `Widget.UnitTests/`), or the file is pytest's `conftest`. Also, after the final review: a whole directory name
   `testdata`, `unittests` or `__mocks__` in any case (Go fixtures, lowercase C++ `unittests/`, Jest manual mocks;
   `testdatabase/`, `testdata.json`, `mocks/` are not), and Cypress's `*.cy.*`, `cy` in any case between two dots of
   the file name and not at its start (`login.cy.ts`; `cy.ts`, `cy/`, `login-cy.ts`, `.cy.ts` are not). Covered: .NET (xunit/NUnit/MSTest project and class
   names), Jest/Vitest/Jasmine/Mocha, Cypress, Go, pytest/unittest, RSpec/minitest, Maven and Gradle layouts. Left out
   on purpose: a camel-case `Spec` suffix and `Test` prefix, which production code uses (`WorkerLaunchSpec.cs`,
   `TestConnection.cs`); `testing/` (it would catch `ab-testing/`), `fixtures/`, `*IT.java`. This changes
   existing `--tests` answers: a file that only contained the letters (`WorkerLaunchSpec.cs`, `Inspector.cs`,
   `Latest.ts`) is no longer test evidence, a candidate test file, or ranked after production code, and with the
   graph queries it is ranked by `hubs` and passed through by `path`. On this repository one file changes side
   (`src/CodeContext.Core/Workers/WorkerLaunchSpec.cs`).

9. One declaration, several nodes. Candidates that are the same source declaration (same file, span, name, type and
   signature; library symbols have no file and never qualify) are one endpoint, not an ambiguity: every copy and its
   members are seeds (`affected`) or starts and goals (`path`), also when a canonical identifier names one copy (the
   others are found by name), and the endpoint lists them (`sameDeclaration`, a hint, a `sameDeclaration` line in
   CLI text). Declarations that differ in any of those stay ambiguous. The bundled workers do not produce this
   today: the C# worker gives a file shared by projects to one owning project, and the TypeScript worker keys a
   symbol by file, so it is exercised on hand-built graphs only; it is there for a worker or workspace layout that
   reports a file once per project.

## Log

| Date | Item | Result |
|---|---|---|
| 2026-11-18 | Baseline | Table in "Why" above |
| 2026-11-18 | M0 | `scripts/bench/measure-memory.sh` (+ `gcdump-types.cs`, `queries-<repo>.txt`). Cached baseline, this repo, RSS MB: idle host 254, csharp 265, typescript 249, total 767; after 60 queries host 440, total 915. Live heap: host 107.3 MB (String 53.5, `Entry<string,string>[]` 18.5, `Dictionary<string,string>` 8.3, `Int32[]` 5.9, `CodeEdge` 5.3), csharp 49.6 MB. No-cache: host 313, csharp 551, typescript 218; live heap host 183, csharp 122. Consecutive runs within 5%; outliers seen: typescript after queries once 249 instead of 212, one first run host 289 idle |
| 2026-11-18 | M1 steps 1-3 | Host live heap, cached: 107.5 to 79.8 MB; no-cache 173.6 to 89.6 MB. Target of 60 MB not met: edge id strings are 46.4 MB (495 bytes each, source + middle + target + tail), hence M1b. Query growth is uncollected garbage (72 MB allocated per 60 queries, live +4.3 MB for `GraphAdjacency`), not live memory. Idle RSS varies about 25 MB between runs |
| 2026-11-18 | M2 | Non-live memory is mostly uncollected gen0/LOH garbage from bursts (snapshot load 231 MB allocated, snapshot write 218 MB, queries). `GC.Collect(2, Aggressive, blocking, compacting)` with LOH compaction: host anon 265 to 134 MB, worker 200 to 149 (uncached 463 to 214); pause 80-240 ms. Worker native: 85 MB of copied PE images, 57 of it for an eagerly built fallback compilation. TypeScript worker floor about 180-210 MB while alive; only hibernation recovers it. The container's 260 MB L3 inflates gen0 budgets, so absolute figures here overstate a desktop. M0 read too early (snapshot write lands 10 s after ready): true idle host is 340-360 MB RSS |
| 2026-11-18 | M1 review | No claim broken; four low findings, carried into M1b |
| 2026-11-18 | M0b | Script waits for a new `cache.snapshotSequence`, a 20 s settle and stable RSS. Idle host range over 3 runs 79 MB to 34 MB |
| 2026-11-18 | M2b | The trace's attribution was wrong: no re-decode. Per write: adjacency 12 MB, check 4 MB, building the file 116 MB. Check skipped for a shard already checked at its version; file build copies less. Allocation per write, cached start: 139-146 to 62 MB; after an uncached start 186 to 158 MB. File bytes unchanged |
| 2026-11-18 | M2a | `QuietTimeTrim` (`src/CodeContext.Core/Memory/`, also compiled into the worker): 15 s after the last activity, at least 30 s apart, only after 32 MB allocated; `CodeContext:MemoryTrimSeconds` (0 = off). RSS/anon MB off to on: host cached idle 254-265/180-191 to 167/92, after queries 264-274 to 175-177; host uncached idle 280 to 161-164; csharp cached idle 252/186 to 204/138, uncached 510-547 to 313-320. Pause host 34-106 ms, worker 66-212 ms. Next-edit latency unchanged (651-879 ms on, 638-920 ms off). Not run: AOT publish, Windows, normal L3 |
| 2026-11-18 | M2c | Fallback runtime references load with the fallback's first file. Worker anon, cached, tree with no loose C# file: 186 to 130 MB; answers identical. No saving on this repository while `scripts/bench/gcdump-types.cs` is in no project. Not lazy: a project with an unresolvable framework; references stay once loaded |
| 2026-11-19 | M2a/M2b/M2c review | No correctness bug; snapshot bytes identical by SHA-256. Low findings: test hosts ran the real trim (fixed, `5c372a7`: test assembly sets `CodeContext__MemoryTrimSeconds=0`); some background work is not counted as activity (latency only, accepted); allocation outside activity never triggers a trim (accepted) |
| 2026-11-19 | M1b | `gcdump-types.cs` renamed `.csx` (`3c7dbaf`): C# worker cached idle RSS/anon 215/150 to 157/92 MB on this repository. Derived edge ids (`dc2c654`): an id is held as parts only when it is exactly source + piece + target + piece, otherwise as the string sent; all 101,934 edges here decompose. Host live heap cached 80.6 to 40.0 MB, no-cache 80.3 to 41.3; idle RSS cached 177 to 135, no-cache 171 to 131. Answers and snapshot bytes identical; queries p50/p95 unchanged or slightly better. Repeated metadata key keeps last value (`79f6304`). Cost: synthetic 200k-node/800k-edge commit 1.4 s before M1, 2.7 s after M1, 2.55 s now (endpoint interning about 0.45 s, metadata sharing about 0.5 s, id splitting). Open: snapshot reader sharing tables still unbounded (bound before M4). Not run: ExternalTooling, SDK suites, Windows, AOT, other repositories |
| 2026-11-19 | M1b review | Bytes, JSON output and id-shape independence hold; 200k-id randomized check of hash, equality, order and prefix passed. One latent bug: two keys with the same text and different splits compared unequal (only reachable through a non-canonical split from the snapshot reader); fixed in `6709999`. Notes, no action: edge tables use a seeded custom hash, not the runtime's string hash; no composed-host test runs on the real 15 s trim default; `SnapshotKeyTests.ConcurrentFirstUse_AgreesOnOneKey` failed once under a parallel run |
| 2026-11-19 | M3 | `WorkerHibernation` arms a timer after each worker operation and queues a maintenance command on `IndexCoordinator`; `LanguageWorkerService.TryHibernateAsync` re-checks under the worker's lock. A `persistentState` worker stops only when its saved state matches the written snapshot (so not with `--no-cache` or read-only); others always may. Next operation, refresh or syntax-tree request wakes it. Status `hibernated`; no cache label on queries. `CodeContext:WorkerHibernateSeconds`, default 900, 0 = off. No protocol or DTO change. This repository, one run, RSS MB host/C#/TS/total: awake 134/158/210/503, hibernated 163/-/-/163, after wake 175/258/212/645. Edit to answer awake vs hibernated: C# 11.2 vs 13.0 s, TypeScript 1.2 vs 2.4 s. Open: host 29 MB above its idle reading when hibernated (trim after exit not checked); host reader rejects `"result": null` on `shutdown`; restart of a crashed worker from saved state not built; no CLI flag; convergence run with hibernate and wake not added; not measured on jellyfin |
| 2026-11-19 | M3 review | Design holds: 10 real-worker scenarios (project inputs, rename, delete, move between projects, restored or backwards mtime, coalesced batches, three cycles) equal a fresh scan; the C# worker restores a walk only on a SHA-256 match. Found: a failed session with a live process hibernated and showed `hibernated` (fixed `9304c0b`); a worker ignoring `shutdown` held the coordinator loop about 12 s (now about 2 s, `7d61015`); wake-failed state inconsistent (`0317f08`); settings untested (`e59f080`). The 11.2 s C# edit is expected: first declaration edit in Core after a cached start or a wake binds four cold compilations (warm: 3.5 s; body edit 1.1 s). Not checked: Windows, `stop` racing a hibernate, a real slow third-party worker |
| 2026-11-19 | M3 follow-ups | Host +30 MB after edits was not hibernation: `SnapshotStartup` kept the loaded view for the host's life and the composite adjacency cache kept the previous shard map until the next query (live heap 42 to 61 MB); both released now (`e4f7bc2`). Quiet-time trim calls `malloc_trim(0)` on Linux glibc in the host, and hibernation requests a trim. Host RSS idle to hibernated after two edits: 134 to 165 before, 125-133 to 129-132 after. Host reader now accepts `"result": null` (`c3fcf2c`; the schema was right). Convergence test: 16-step edit walk with hibernate and wake at every step, 5 seeds, no bug (`045ecda`). Open: restart of a crashed worker from saved state; no CLI flag; TypeScript not in the convergence walk; `SnapshotKeyTests.ConcurrentFirstUse_AgreesOnOneKey` flakes under the full run |
| 2026-11-19 | M3 follow-ups review | Found a race older than this plan: two first users of `~/.codecontext/cache.key` could each keep a different key (`File.Move(overwrite: false)` is lstat then rename on Linux), so the loser's snapshots failed authentication and were deleted; 239 of 2000 rounds with 8 threads. Key is now created with `CreateNew`; readers wait up to 2 s for a short file (`d714491`); 0 of 2000 after. `FileThatIsNotAKey_IsRefused` failed here because of the umask, fixed in the test: 6 known failures remain. Reader tolerates a null result beside an error or on a request again (`e5c38e2`). Convergence test requires every wake to accept saved state and compares against a new worker process (`e21b675`). AOT publish of the host compiled clean with `malloc_trim`; not run. Open: a creator crashing between create and write leaves an empty key that is refused until deleted |
| 2026-11-19 | M4 gate | Measured at `35a9e4a`, RSS MB, cached start. This repository (104k edges): awake 495-503 total (host 124-132, C# 159-160, TS 210-213), hibernated 131-138, woken 608-610; baseline was 767 with a host of 254-360. jellyfin (297k edges, no TypeScript): awake 493-497 (host 171-173, C# 322-324), hibernated 197-208, woken after a declaration edit 661-716; first scan 35 s, cached start 7-8 s, snapshot 24.7 MB. Host graph 28-35 MB per 100k edges (111 at baseline). Mapped shard would save an estimated 25-35 MB here and 60-95 MB on jellyfin (5-18% awake, 19-46% of the hibernated host). Decision: not built now; workers are 65-75% of the awake total and phase 8 is the riskiest item left. If the host graph matters later, try a compact heap edge table first (about 160 B per edge now). Wake cost on jellyfin: body edit 23.5 s hibernated against 3.4 s awake (first) and 1.1 s (second); declaration 15.5 against 11.2. Cheaper options: shorter hibernation for workers without saved state (TS: 210 MB, wakes in 2.4 s); C# worker `EdgeFact[]` about twice needed (about 9 MB on jellyfin); C# worker stays at 457-504 MB after a declaration edit until it hibernates. Not measured: Windows, AOT, normal L3 |
| 2026-11-20 | M5 | With no instance running, `query` answers in process from a snapshot that authenticates and that no selected file, folder, `.gitignore` or `obj/project.assets.json` is newer than (capture time, 2 s slack); otherwise it starts an instance as before; `--start` forces that. Label `cache snapshot`. Existing materializing loader kept. Wall time and peak RSS, one-shot: AOT 0.37-0.41 s / 105-114 MB here, 1.06-1.32 s / 195-197 MB on jellyfin; JIT 1.2-1.3 s / 189-222 MB and 2.1-2.2 s / 274-293 MB. Today's cold query leaves 577-790 MB running. Stat pass 13-25 ms here, 37-227 ms jellyfin, 0.7-1.5 s for 100k files. Host now rewrites a snapshot older than its files after a verified scan. Blind spots: a file rewritten with an older time set afterwards; an edit between a worker's read and the capture when the host stops before the watcher reports it; a TypeScript-only snapshot written after a failed verification. The 6 known test failures are Windows-path fixtures run on Linux. Not run: Windows, macOS (since run in pull-request CI, see the 2026-11-20 CI entry) |
| 2026-11-20 | M5 review and fixes | Review got wrong answers labelled current: TypeScript config edits, C# inputs above the indexed root or under custom restore paths, times set back by `tar`/`rsync`/`cp -p`, snapshots written while unverified. Fixes: in-process path is opt-in (`query --snapshot`, `CodeContext:QueryFromSnapshot`); workers report the files they read and the paths they found absent (optional `inputs` on `analysis/delta`, protocol still 1), the snapshot records them (format 2; a version 1 file is refused and rebuilt) and the check stats exactly those; a shard without inputs is never answered in process; Linux also compares ctime (`statx`); unverified snapshots are flagged and refused; a running instance now follows `tsconfig.json`/`jsconfig.json` edits. Stat walk moved off the start-up path (100k-file cached start 3.2-3.3 to 2.5-2.8 s). Open, hence opt-in: in-place rewrite with an older time on Windows/macOS; an edit between a worker's read and the save when the host stops first; ignore-pattern or worker-set changes; TypeScript failed type-reference lookups; a build's `obj/` changes cause one fallback. Nothing run on Windows or macOS (since run in pull-request CI, see the 2026-11-20 CI entry) |
| 2026-11-20 | M3c | A worker whose last index from its files took under 5 s hibernates after 120 s idle, any other (including one that only restored state) after 900 s; a configured value applies to all. This repository, 10 quiet minutes: mean total RSS 617 to 450 MB (TypeScript hibernates at 2 min; edit after wake 2.4 s). C# worker held each edge twice (bucket copied its walk's list): jellyfin `EdgeFact[]` 18.8 to 13.5 MB, answers identical. Worker growth after a declaration edit on jellyfin (317 to 427 MB) is live bound compilations of dependents (+65 MB heap), not garbage; no fix short of dropping compilations. Single runs |
| 2026-11-19 | Final pass: host store (merge `65a27fe`) | Edge id keys use the runtime string hash (40,000 ids crafted to collide: commit 84.8 s to 0.24 s). Adjacency built in two passes over plain dictionaries (first read after a one-file commit on 800k edges: 2.5-2.9 s / 199 MB to 0.86-1.02 s / 92 MB). Query path allocation 71.0 to 26.9 MB per 60 queries (M2e). Foreign-shard sweep takes the delta's lists (full commit 310 to 244 MB). Endpoints in other files reuse the stored id instance. 110 queries byte-identical before and after. Left: store drift after file-scoped commits (+8.2 MB over a fresh commit), one-file commit still 0.9 s / 76 MB on 800k edges, next density step (see M1b) |
| 2026-11-19 | Final pass: snapshots and snapshot query (merge `e46cc93`) | Watcher: a directory created, moved in, renamed or removed is reconciled as a subtree (files arriving that way were missed before). One-shot query refuses when the worker set differs or the capture time is ahead of now; every included directory's `.gitignore` is checked. Loader and writer allocate less. Format 3: the ten sections only a mapped shard would read are gone (file -16.9% here, -18.6% jellyfin; allocation per write 40.2 to 29.4 MB and 108.8 to 72.1 MB); a format 2 file is deleted and rebuilt. Left: a removed path with no indexed extension costs a pass over the file table; a worker binary rewritten in place is not seen. Before `query --snapshot` could be the Linux default: a test of the inotify watch limit and one run on an AOT build with the real workers; clock set back and foreign-clock file systems documented; Windows and macOS stay opt-in. The default is the owner's decision |
| 2026-11-19 | Final pass: trim and hibernation (merge `56ebc9c`) | Host drops absent inputs below a listed absent path (2,000-file TypeScript project: kept 97,287 to 2,639, snapshot inputs 14.2 to 0.36 MB) and reads inputs from the first chunk only. Host refuses reported paths the workers' guard would refuse, as a report arrives and at the freshness check, and looks at no path through a link the repository holds; such a shard falls back from the one-shot path (so does pnpm-style `node_modules`). Idle worker waiting on a snapshot is looked at again after min(idle, 30 s). Scan and wake share one index path. A composed-host test holds the real defaults. Schema and SDK guide state the inputs limit, the collapse and idle stops. Left: host code that opens a node's `filePath` was not examined for the same exposure; nothing run on Windows |
| 2026-11-19 | Final pass: bundled workers (merge `5505abd`) | Both workers keep guard-refused paths out of inputs. C# `Inputs()` no longer loads runtime references (one-project repository: worker 77-78 to 20 MB). TypeScript inputs de-duplicated and collapsed (delta 2.97 to 1.09 MB on a 132-package project). M2d: references held as metadata only (jellyfin anonymous 417 to 368 MB, facts identical). Discovery stops one directory below `obj`, so edit, build, `query --snapshot` answers from the snapshot. Buffered fact hasher (jellyfin body edit emission 42-48 to 19-20 ms). Bound state released after 60 s quiet (jellyfin after first scan: RSS 487 to 378 MB, heap 302 to 201 MB; no cost seen on the next edit). Left: incremental `SettleOwnership` (about 45 ms per edit on jellyfin), inputs sent only when changed, guard cost of `Inputs()` on Windows unmeasured; nothing run on Windows |
| 2026-11-19 | F1 | Whole-graph queries (path, affected, hubs) over REST, MCP, the CLI and `query --snapshot`, through `ContextService`; see "F1 as built". Tests: hand-built graphs per query, a slow reference compared on 12 generated two-shard graphs per query (it found a real bug in the first path search: a free dispatch hop could not improve a state already queued one hop deeper), concurrent commits (no answer mixes generations; the test fails without the generation check), allocation (multi-shard lookups allocate nothing; hubs over 80,000 edges allocates 74 KB). Memory, `measure-memory.sh --queries`, this repository, Release JIT, baseline `a62fcbc` against F1, two runs each, RSS MB: idle host 128.5/128.5 vs 128.5/129.2, idle total 497.9/497.2 vs 497.5/498.2; after the 60-query mix host 171.6/169.6 vs 170.1/170.4. Graph queries on this repository (Release, median of 5): hubs 33-42 ms (9 ms under a `within` and `type`), affected 3-21 ms (21 ms for `CodeNode` at depth 20 with tests), path 6 ms (`McpToolCatalog.CallAsync` to `InMemoryDatabase.GetAdjacency`; the two other path queries named an ambiguous `Program.Main` and searched nothing, 2 ms); host RSS 127.6 MB before a 45-query graph mix, 142.3 MB 25 s after (garbage, as for context queries). Changed beside F1: the test-file rule is root-relative for `--tests` too; the shared `--depth`/`--limit` validators no longer crash on a non-number. Not run: Windows, AOT publish, jellyfin |
| 2026-11-20 | F1 final review | Decisions applied: test files by naming convention (item 8; here only `WorkerLaunchSpec.cs` changes side; the rule costs 27 ns a call against 84-94 ns for the substring rule), copies of one declaration searched together (item 9; not producible with the bundled workers, hand-built graphs only), cancellation inside the walks. Defects fixed: an MCP argument in the wrong letter case was said to belong to another query; "without the relation filter and with test files included" was also said when either alone was enough; the CLI's 5 s request timeout cut off graph queries on large graphs (now 60 s, with a message). Synthetic two-shard graph, 150,000 symbols and 500,000 edges, Release JIT, best of 7, before to after: runs vary by about 15% here: hubs 728-842 to 581-854 ms, 4.0 MB (ordering all 117,972 hubs was about 140 ms of it; now the leading 20 are selected); affected with 95,160 dependents 448-477 to 267-359 ms and 17.3 to 15.7 MB, 144,106 dependents at depth 20 899-957 to 608-861 ms and 25.3 to 23.1 MB (nodes carried instead of looked up again, no full sort); a path search that finds nothing 592-642 to 550-596 ms and 32.8 to 20.2 MB (one state table instead of two); edge kinds by ordinal switch, 82 to 5 ms per 500,000 edges, shared with context queries. Per edge the walks cost about 1.3-1.7 us, mostly the string-keyed node lookup (0.7-0.9 us cold); nothing short of integer-indexed adjacency changes that. 600,000 symbols and 2,000,000 edges: hubs 2.7-3.3 s, affected at depth 20 3.1 s and 88-90 MB, a path that does not exist 3.8-6.4 s and 109-195 MB (was 6.3 s and 219 MB with both filters). AOT host with both real workers (`dotnet publish -r linux-x64 --self-contained`, no trim or AOT warning): the three queries answered over REST, CLI, MCP stdio and `--snapshot` (one-shot hubs 0.32 s); a stale snapshot (one file touched) and an absent one each said why and started an instance. C# and TypeScript in one instance (two projects sharing a file, two tsconfigs sharing a module): one node per shared declaration, dependents of both projects listed. Cross-checked with `rg`: `IScanProgressReporter` affected in 6 files, `IGraphShard` used in 8, both exact. Gates: 2342 passed, the 6 known failures, 38 skipped; ExternalTooling 74 and 2 skipped; both SDK suites; idle RSS host 128.8/128.8/129.4 MB, total 500.4/501.2 MB (one run 539 with the TypeScript worker at 253, the outlier M0 records). Not run: Windows, macOS, a real repository above 300,000 edges, a client disconnect against a live instance (the walks' cancellation is tested directly). Known limits of the test-file rule: `testing/`, `testdata/`, `unittests/`, `__mocks__/`, `*.cy.ts` and a camel-case `Test` prefix (`TestHelpers.cs` outside a test directory) are not recognized |
| 2026-11-20 | Pull-request CI on Windows, macOS, Linux | The first runs of this work off Linux (`.github/workflows/pr-test.yml`). Windows found: the freshness walk took each directory's time from its parent's listing, which NTFS does not keep current as entries come and go, so a deleted file or an added unclaimed one went unseen and a one-shot query could answer stale (fixed `d95f421`: Windows reads the entry's own `ChangeTime`); a multi-module assembly outside the repository was handed to Roslyn as a file, which kept handles on its modules and blocked deleting them (fixed `c575d72`: modules read into memory through the guard and reported as inputs); a test joined the check's `/`-separated path onto a Windows root. macOS found a test pinning the file where the copy replaced it and its folder changed first. Left, tested as limits: on Windows a same-size copy with older times is not seen (the copy carries the source's change and last write times); on macOS content rewritten in place with its old modification time put back. A cached restart is not affected the same way: the C# worker compares source files by content and project files and referenced assemblies by last write time for equality, so only an edit that puts an exact old time back on one of those is missed there. M5f before any default |
| 2026-11-20 | M5 re-review (two findings) | Both real. (1) A module of a multi-module assembly that was missing, unreadable or malformed was left out of the C# worker's inputs (`c575d72` reported only modules read), so restoring it went unseen; now every module the manifest names beside it is reported, missing ones as absent (`911dd35`). (2) Older than this pull request: the one-shot check compared reported inputs with the capture time, but facts are as old as the worker's read of them, and nothing makes a worker read again a file outside the watched root (a `Directory.Build.props` above it, the SDK, package caches) or under a mandatory exclusion; an edit between the read and the capture (the quiet period, a hibernated worker whose shard is carried into a later snapshot, another worker's commit) passed as older than the snapshot. The host now records, per shard, when it asked the worker for the last index or project reread (`ShardAnnotation.InputsReadUtcTicks`, shard meta key `inputsReadUtcTicks`, no format version change, no protocol change) and dates reported files against that; directories the walk itself dates (the root among them) stay with the walk. A snapshot without the key is not answered from and a host rewrites it once verified; an unchanged restart still writes nothing (the older time is only more cautious) |
| 2026-11-20 | Stop and the loaded-snapshot check (ported from `claude/graph-queries` `0b0a4d0`, as reconciled in `85d0aa5`) | A host whose loaded snapshot was older than its files rewrote the store once verified, but a stop did not wait for either source of that decision: the mutation observer (which runs after the scan's phase already reads Ready) and the background check against file times (which gave up once stopping). Seen as an intermittent Ubuntu failure of `TouchedWithTheSameContent_TheVerifiedStoreIsWrittenAgain`. The stop now applies the verified-scan step itself and waits for the tracked check before its final capture; the check records its answer while stopping. One change to the port: the stop waits for the check only when nothing else would write the store, since in the reconciled form a slow check spent the stop's whole budget and a changed store went unwritten (test `AStopWithAChangedStore_DoesNotWaitForTheCheckOfTheLoadedSnapshot` fails on the reconciled form, passes here). The observer window has its own deterministic test (`...AStopBeforeTheObserverSeesTheScanEnd_StillWritesTheStore`, the observer withheld) |
| 2026-11-20 | F1 pull-request review and CI | Review finding, real: a commit sets the published map, then bumps the version; in between, two readers building the composite adjacency could leave the new map cached under the old version, and a graph query that walked the old map then resolved its identifier through the new one passed its version check (a symbol whose id changed got no dependents). Reproduced deterministically with two store test seams (after publish, on a composite cache miss); fixed by pinning the query's one composite for its repository reads (no copy, no per-lookup allocation; the pin is dropped when the query ends). The opposite order, notify then swap, would be worse (a composite of the old map cached under the new version until the next commit), so the order stays. Not changed, pre-existing: context queries make several repository reads with no generation check, so a commit between two reads can mix generations in one answer; coverage labels come from the annotations, published just before the map. First CI run of this branch: Ubuntu failed `TouchedWithTheSameContent_TheVerifiedStoreIsWrittenAgain` (base code): a stop right after a verifying scan wrote nothing when the mutation observer had not yet seen the scan end or the background check of the loaded snapshot was still running; the stop now applies that step and waits for the check (test holds the check until the stop has begun; since ported to the base as `5982be7`, which waits only when nothing else would write the store) |
| 2026-11-20 | Context queries in one generation | Context answers made several store reads with no generation protection, so a commit between two of them mixed generations (target resolved in one, its users read in the next); every public query of `ContextService` now pins the published map for its duration, and a multi query's answers share one pin. Tests force a commit between two reads of a compact, a full and a multi answer (each failed before), and fail if any query method reads outside a pin. Per query: 72 B more (4,064 against 3,992 B for a depth-0 compact query); `QueryAllocationTests` unchanged |
| 2026-11-20 | Composite cache kept no replaced generation | A reader that built the composite adjacency and passed its version check could store it after a commit had cleared the cache, keeping the replaced generation's shards alive until the next read; the reader now takes its entry back when the version moved. Test: a commit between the check and the store (failed before) |
| 2026-11-20 | Cache label before the pin | Review of "Context queries in one generation", real: the pin was taken before the "before" cache notice, so a scan that committed and was reported between the two left an answer from the loaded snapshot's pinned facts unlabelled (both notices empty), and in a multi query every answer after the commit read its notices inside the multi query's pin. The notice is now read just before the pin, and a multi query labels each answer its notices left bare with its own. Tests: the scan commits on the first notice (single, compact and full) or the second (multi), each failed before. No allocation change (72 B per query for the pin, multi queries included) |
| 2026-11-20 | "One generation" as documented | "F1 as built" item 6 named `InMemoryDatabase.Pin` (now `PinPublished`), said status reads its counts in one store call (graph counts only; file counts come from the file metadata and coverage from the annotations) and that a coverage label can be one commit newer than its facts (any number: it is read unpinned while the pin is held). Corrected, and the skill now says an answer's facts are one state and only its `coverage` line can be later. Entry points checked for a store read without the pin: REST context, multi, graph; MCP `get_context`, `get_multi_context`, graph tools; CLI and `query --snapshot` (all through `ContextService`); status and the syntax-tree endpoint read no facts |
