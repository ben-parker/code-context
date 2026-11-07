# Persistent fact cache and worker hibernation

Status: direction agreed by Ben on 2026-11-04. Detailed design and phase plan to be
produced by the implementing session before any code changes. Builds on the project
awareness work (`docs/proposals/csharp-project-awareness.md`), which must be on `main`
first: it supplies compact facts, per-project fingerprints, the edit classifier and the
`coverage` reporting channel this design relies on.

## Problem, measured

Release-recipe builds (R2R worker, Native AOT host), end to end through the host, on
this machine (16 logical cores):

| Repo | Cold start to `ready` | Host resident | Worker resident | Body edit |
|---|---|---|---|---|
| jellyfin, 366k lines, 43 projects, 2,251 files | 50-55 s | ~630 MB | ~1,000 MB | 1.2-1.4 s |
| d2-roguelike, 44k lines, 25 projects | ~5.5 s worker, 11 s cold `query` | — | 450-510 MB | 0.06 s |
| this repo, 39k lines | 5.5 s | — | 395 MB | 0.1 s |

Where the time goes: semantic binding. On a 316k-line corpus `GetSymbolInfo` over all
names took 28-32 s single-threaded; parsing, walking and serialization are a few
seconds. The derived graph (jellyfin: 26,292 nodes, 296,033 edges) loads from disk in
milliseconds. A first scan must bind every name once, and nothing stored can shorten
deriving a fact; it can only avoid re-deriving one.

Where the memory goes: two processes, two causes. The host holds the graph and its
adjacency indexes (derived data). The worker holds Roslyn compilations, loaded metadata
for every referenced assembly, syntax trees and bound-symbol caches; that working set is
what makes a body edit cost 1.3 s instead of a 50 s rebuild, and it does not compress.

## Goal

Behave like Visual Studio: keep compilations in memory while a repository is active,
persist the derived indices to disk, serve from them on start before semantics are warm,
and say so.

- Cold start on an unchanged repository: serve queries in about one second at any repo
  size, with no binding.
- Cold start after an edit: serve immediately from the snapshot; re-walk only the
  projects whose fingerprint moved, as ordinary deltas.
- Host resident memory for the graph: page cache, not heap. Resident grows with what
  queries touch, not with repository size.
- Worker memory released when idle, re-acquired on the next change.
- Results identical to a fresh scan. Staleness is always labelled, never silent.

## Decisions already made

1. **Container: a memory-mapped binary file, not a database.** CSR-style adjacency,
   interned id strings, fixed-width node and edge records, per-file buckets with their
   hashes, per-project fingerprints, a header with format version and the versions of
   the host and each worker that produced it. The host maps the file and reads
   adjacency in place, so a `usedBy` query faults in a few pages. The same file is the
   snapshot used for load-then-verify: one file, two jobs. Alternatives considered:
   SQLite (inspectable, AOT-compatible, but a native dependency, milliseconds per query,
   and a poor fit for mapping); Kuzu (archived October 2025, never had a .NET binding);
   DuckDB+PGQ (a query engine nobody needs). The host is Native AOT: no reflection-based
   libraries. Microsoft.Data.Sqlite remains an acceptable fallback container if the
   mapped format proves too costly to get right; the loader interface must not care.
2. **Load-then-verify.** On start the host maps the snapshot, so the graph is queryable
   before any worker starts. `coverage`, `/api/status` and `query` report `cached,
   verifying`. Each worker receives the fingerprints the snapshot was built from,
   compares them with the repository as it is now, and emits deltas only for files
   whose project fingerprint moved. Unchanged projects bind nothing. Compilations are
   built lazily, when a project fails verification or an edit arrives.
3. **Fingerprint, per project**, conservative by construction: worker version, evaluated
   project inputs, resolved reference set, ordered content hashes of its files, and the
   fingerprints of the projects it depends on (a declaration change upstream rebinds
   downstream). Any mismatch re-walks that project. Files in no project form their own
   unit. Stat first, hash content only when size or mtime moved.
4. **Snapshot lifecycle.** Written after each committed generation, to a temp file then
   atomically renamed, under `.codecontext/cache/<workspaceId>/` (already a mandatory
   exclusion, so the cache can never index itself). A torn, truncated or
   version-mismatched file is ignored and rebuilt, never trusted. Writing the snapshot
   must not block queries or the next commit.
5. **Worker hibernation.** After N idle minutes with no file or project-input changes
   (configurable; propose a default and justify it), the worker exits and the host keeps
   serving from the mapped snapshot. A change wakes it; it verifies and rebuilds only the
   compilations it needs. The instance idle timeout (default 120 minutes) is unchanged;
   hibernation is nested inside it. Status reports a hibernated worker distinctly from
   a failed one.
6. **Serve stale while verifying: yes, labelled.** That is what every IDE does; the
   coverage channel is the honesty mechanism.
7. **Per-workspace cache only.** No sharing across worktrees or clones in this work;
   sharing is an optimization with a key-collision risk and comes later if at all.
8. **Syntax-first startup** (declarations, `HAS_*`, inheritance by name from parsing
   alone, served before binding on a first-ever scan) is in scope as a final phase if
   the cache phases land cleanly, and may be dropped without affecting the rest.

## Invariants that must hold (see AGENTS.md)

- No Roslyn or language-toolchain code enters the host. The host gains a mapped store,
  a loader and the verification plumbing; fingerprints are computed by workers.
- Every node and edge id keeps its `<parserId>:<workspaceId>:` prefix. Generation
  commits stay atomic and stale generations are rejected. A cached generation and a
  live delta are the same kind of thing to the store.
- Nothing from a repository is executed. The cache reads the repository, never runs it.
- The same file-selection policy applies to verification as to scans.
- Protocol changes are additive and keep `ParserProtocol.Version` at 1 if at all
  possible; schema, DTOs, `ParserProtocolJsonContext`, bundled workers, manifests, SDK
  constants and protocol tests move together. The TypeScript worker must keep working
  with no snapshot support (it re-walks and the host diffs, as today) and may gain it
  later.
- `skill/SKILL.md`, `codecontext.openapi.json` and `docs/` are updated with the
  behavior; the packaged skill must match the source byte for byte.

## Where it plugs in

`InMemoryCodeGraphRepository` / `IGenerationalGraphStore` (a mapped generation behind
the same interface), `IndexCoordinator` startup (load, then verify, instead of scan),
`AnalysisDeltaApplier` (unchanged semantics), `LanguageWorkerService` and the
supervisor (hibernation lifecycle), the C# worker's `ProjectModel`, compact facts and
`EditClassifier` (fingerprints and partial re-walks), `/api/status` and `coverage`.

## Measurements to report, before and after

On jellyfin, d2-roguelike and this repo, with release-recipe builds and the harness in
`C:\Users\benpa\AppData\Local\Temp\ccbench\measure.py` (note: watcher updates do not
advance `operationId`; detect commits by node/edge count change):

- Cold start to first answered query, unchanged repository.
- Cold start with one file changed since the snapshot; time to the corrected answer.
- Host resident memory after start and after 100 mixed queries.
- Worker resident memory before and after hibernation; wake-up time to first delta.
- Edit latency, which must not regress.
- Convergence: incremental graph after a random edit walk equals a fresh scan, with
  snapshots written and reloaded between steps.

## Process

A coordinating session (Fable) produces the detailed design and a phase plan from this
brief, then drives it: Opus implementers via the implementation skill, Opus adversarial
reviewers on every phase, and a final adversarial audit by Fable.

After all code work is done and before anything is pushed, a dedicated pass over the
agent skill: `skill/SKILL.md`, its frontmatter `description` (what agents read to decide
whether to load the skill at all), and `skill/references/*.md`. Goals, in order: agents
know exactly when CodeContext answers better than the tools they were trained on (grep,
rg, `Select-String`, `Get-ChildItem`, file reads) and when it does not; the description
triggers on relationship, blast-radius and test-evidence questions and not on literal
string, config or filename searches; and the skill costs as few tokens as possible to
load, with anything only needed occasionally moved into `references/`. Keep the behavior
of this work (cached-while-verifying, hibernation, coverage lines) described there in
the fewest words that make an agent act correctly on them. The packaged copy must still
match byte for byte.

Then merge to `main`, bump `version.json` minor (project awareness ships as 0.10; this
ships as 0.11), push, and confirm the release workflow publishes. Commits carry no co-author trailer. Running
`verify-publish.ps1` locally needs `C:\Program Files (x86)\Microsoft Visual Studio\Installer`
on PATH for the AOT link and `-ReleaseVersion` equal to the nbgv version the built
executable reports.

The project-awareness session is profiling the jellyfin first scan (where ~50 s goes:
project evaluation, reference loading, binding per project, emission, host commit, and
whether the parallel scan is bound by the dependency chain). Its findings bear on the
syntax-first phase and on what verification must avoid; use them when they arrive. The
jellyfin clone is at `C:\Users\benpa\source\repos\jellyfin`, restored.
