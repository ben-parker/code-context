# Persistent fact cache and worker hibernation: design and phase plan

Status: design complete, no phase started. Targets 0.11. The coordinating session updates the
`Status:` line of each phase.

Brief: [proposals/persistent-fact-cache.md](proposals/persistent-fact-cache.md), direction and
eight decisions agreed 2026-11-04. Builds on
[proposals/csharp-project-awareness.md](proposals/csharp-project-awareness.md), released as
0.10.0 (main 15bfde8). `AGENTS.md` invariants are hard constraints; section 4 maps each one.

Numbers quoted as "measured" come from the brief and the jellyfin profiling session (2,251
files, 366k lines, 44 compilations, 26,301 nodes, 296,362 edges, Ryzen 3800X). Numbers marked
"est." are derived from them and are checked in the phase named beside them.

Profile figures used here, from the project-awareness session (real AOT host, instrumented
worker at ac0711a, 3 runs, 46.8-48.0 s to scan end): host file enumeration 0-11.6 s, during
which the worker is not started; worker start and open build 11.6-14.6 s; second build plus
walk 14.6-44.6 s; settle 0.2-0.3 s; emission 3.1-3.2 s; host commit 0.06 s after the last
delta. Worker alone: project model 0.45-0.6 s, stat of 2,251 files 65 ms, read plus SHA-256
of all 13 MB 201 ms, process start to first open 0.4-1.0 s. Worker working set after commit
1,002-1,003 MB; host 585-654 MB. The 11.6 s is `RepositoryFileSelector` running 161
`.gitignore` regexes, interpreted under Native AOT, on every path (about 18 us per rule per
path); a 0.10.x patch on `main` removes it (D25). Not measured by that session: cold disk, any repository but
jellyfin for the breakdown.

## 1. Goals and non-goals

Goals, each with the number that decides it:

1. Cold start on an unchanged repository answers the first query in under 1 s after the host
   process starts, at any repository size, with no binding, parsing or compilation. The answer
   is labelled `cache verifying` until the worker has checked the files (est. 1.2-2.2 s on
   jellyfin plus file enumeration, against about 50 s today). Enumeration is 11.6 s on
   jellyfin today and must fall to about 1 s (D25), or verification would take 13 s.
2. Cold start after edits answers immediately from the snapshot, labelled, and re-walks only
   projects whose fingerprint moved, as ordinary file-scoped deltas.
3. Host memory for the graph is file-backed pages, not heap: resident grows with what queries
   touch. Today the host holds 585-630 MB at ready on jellyfin.
4. An idle C# worker exits (about 1,000 MB on jellyfin, 395-510 MB on the small repositories)
   and the host keeps serving. The next change wakes it through the same verification path.
5. The graph after any sequence of edits, snapshots, restarts and hibernations equals a fresh
   scan of the same files. Staleness from the cache or from a waking worker is labelled.
6. Warm edit latency does not regress: body edit 1.3 s and top-level edit 3.9 s on jellyfin.

Non-goals:

- Sharing a cache between worktrees, clones or machines (brief decision 7).
- Faster first-ever scans. The batch barrier and TieredPGO are another session's topics. Only
  the optional final phase touches first-scan behavior.
- Persistent state in the TypeScript worker. It keeps working exactly as today and its cached
  facts are served labelled while it re-walks.
- A query engine, a database backend, or any new native dependency.
- The MimeTypes source-only package gap, and labelling the short staleness window of an
  ordinary warm edit (unchanged from 0.10.0).

## 2. Decisions

Source tags: [brief] already agreed, [coord] coordinator, [plan] made here. Each has its
reason and the alternative rejected.

**Container and store**

- D1 [brief]. The container is a memory-mapped binary file with CSR adjacency, interned
  strings and fixed-width records, and the same file is the startup snapshot. Rejected:
  SQLite (native dependency, milliseconds per query), Kuzu, DuckDB+PGQ.
- D2 [coord]. A shard of `InMemoryDatabase` is either a heap shard (today's `GraphState`) or a
  mapped shard: an immutable mapped base plus a small heap overlay. Commits against a mapped
  shard build a new overlay and never touch the mapping. The snapshot writer is the compactor.
  Readers always see one complete generation through the existing single-reference swap.
  Rejected: mutable mapped pages (torn reads, no crash safety).
- D3 [plan]. Delivery is staged behind one loader interface: a materializing loader first
  (file to heap shard, phases 2-7), in-place mapped reads later (phase 8). Reason: the
  load-then-verify contract with the worker is the larger correctness risk and should not wait
  for the store rewrite. Each stage is correct and measurable. Rejected: mapped store first.
- D4 [plan]. Every multi-valued read returns results in canonical order: ordinal by id
  (`StringComparer.Ordinal` over the UTF-16 form), shards concatenated in `ShardKey` order.
  Reason: heap and mapped shards must give byte-identical answers, including where
  `ContextService` breaks ties by input order, and base and overlay must merge without a sort.
  Rejected: comparing as sets and patching each order-sensitive call site, which leaves
  unknown leaks.
- D5 [coord]. One snapshot file with one section per (parserId, workspaceId) shard. Reason:
  cross-shard edges and global identifier uniqueness need all shards from one published map;
  per-shard files would need a manifest to pair them. Shard blobs are position-independent,
  so an unchanged shard can later be copied through byte for byte.
- D6 [plan]. The un-owned `Default` shard is never persisted, and a shard is persisted only if
  every node id, edge id and edge source id starts with `<parserId>:<escaped workspaceId>:`
  and every string survives a strict UTF-8 round trip. Reason: no worker can verify un-owned
  facts, and a lossy string would be a wrong answer. A shard that fails is rebuilt by a scan.

**Files and integrity**

- D7 [coord]. Snapshot names carry a sequence number. Write a temp file, flush, rename to a
  new unique final name, retire the older file after its mapping is released. Little-endian
  only, stated in the header. Mapped files are opened deny-write. No reflection, no native
  dependency.
- D8 [plan]. Integrity is HMAC-SHA256 over the whole file with a per-user key at
  `~/.codecontext/cache.key`, verified by a streaming read before mapping. Reason: the cache
  directory is inside the repository, so a cloned or unpacked repository can carry a crafted
  snapshot; a plain checksum would authenticate it. Rejected: SHA-256 footer (integrity
  without origin), CRC32 (needs a package).
- D9 [plan]. A snapshot is used only if format version, host informational version, root path
  and path case rule all match. Reason: host-side normalization (spans, ownership metadata,
  identifier derivation) may change between host builds. Coordinator decision 7 asks for the
  same. Cost: the first start after an upgrade is a full scan.
- D10 [plan]. The cache directory is used only if no path component from the root down to it
  is a reparse point or symlink, and it contains its own `.gitignore` with `*`. Reason: a
  repository-supplied link must not redirect writes or deletes, and `git status` stays clean.
- D11 [plan]. One writer per root: an exclusive `writer.lock` held for the process lifetime.
  An instance that cannot take it (an MCP host beside a REST host) reads snapshots and never
  writes, deletes, or hibernates its worker.

**Worker contract**

- D12 [coord]. Protocol changes are additive and `ParserProtocol.Version` stays 1. A worker
  without the new capability behaves exactly as today.
- D13 [coord]. Fingerprints are computed by the worker and opaque to the host, which stores
  them and hands them back.
- D14 [plan]. The C# worker restores from a worker-private state file in the cache directory
  (option b). The host names the file, records its id and SHA-256 in the authenticated
  snapshot, and passes them back in `baseline`. Any mismatch is a full re-walk.
  Reason, by the coordinator's criteria: (1) settlement in the worker is a pure function of
  pre-settlement per-file walk results, and the host graph has lost exactly that: which file
  emitted each copy of a shared id, which file walked each edge, per-file stub use with its
  assembly, per-file unresolved names. Restoring the worker's own records reproduces a
  never-restarted worker by construction. (2) No binding or parsing. (3) The settled graph
  has one home (host snapshot) and walk results have one home (state file); the pair is
  bound by id, hash and fingerprints. (4) Additive. (5) A serializer for four record types.
  Rejected: (a) reading the host shard through a shared reader, because the settled graph
  cannot give back partial-type copies, edge provenance or per-file stub liveness; (c) opaque
  blobs through the host, because 10-15 MB of state would cross a pipe whose frame limit is
  16 MB on every start and on every edit that changes it.
- D15 [plan]. The worker saves state on a new optional request, `workspace/saveState`, sent
  by the host at snapshot capture. Reason: a save is not a mutation, must not create a
  generation (`ValidateMutationResult` requires at least one delta for index and apply), and
  must be ordered after the last commit. Saving inside every mutation would add 10-15 MB of
  writing to a 1.3 s body edit. This departs from the coordinator's preference for optional
  fields only; `syntaxTree/get` is the precedent for a capability-gated method.
- D16 [brief, refined]. The fingerprint is per project and conservative. Dependents include
  the fingerprints of what they reference, so a change upstream re-walks downstream. An
  optional phase (10) narrows a provably body-only change to its file.
- D17 [coord]. Verification uses the host's approved file list, stats first and hashes only
  when size or mtime moved. Size, mtime and content hash live in the worker state file,
  because only the worker reads sources and the host has no use for them.
- D18 [plan]. A full refresh (`POST /api/index/refresh`, watcher overflow, `.gitignore`
  change) stays a full re-walk with no baseline. Reason: it is the recovery path.

**Lifecycle and surface**

- D19 [coord]. Snapshot writes are coalesced and off the commit path: 10 s quiet period, 120 s
  maximum delay, flush on graceful shutdown. Reason for the long quiet period: a stale
  snapshot costs only a partial re-walk at the next start, while each write is 24-28 MB (est.)
  plus 10-15 MB of worker state on jellyfin.
- D20 [plan]. Capture is a command on the `IndexCoordinator` channel. Reason: a scan stages
  several worker commits behind one reconciliation, so only the coordinator knows a point
  where published host facts and worker state agree. Cost: the coordinator waits for the
  worker's save (est. 100-250 ms on jellyfin, measured in phase 6).
- D21 [coord]. Hibernation is host-driven with a 30-minute default idle period (section 3.9).
- D22 [coord]. `indexing.status` keeps its three values. The cache is an additive
  `indexing.cache` object and an additive `cache` notice on context responses. `query`
  answers from an unverified cache immediately; `init --wait` still waits for `ready`.
  Rejected: a fourth status value, which existing pollers would not understand.
- D23 [plan]. A worker waking from hibernation is labelled like an unverified cache. Reason:
  its first delta can take seconds to tens of seconds.
- D24 [coord]. Syntax-first startup is the final, droppable phase.
- D25 [coord]. Verification depends on an ignore-rule fast path, and this work does not
  build it. Reason for the dependency: verification enumerates approved files through the
  selector, and that alone is 11.6 s on jellyfin, six times the rest of an unchanged
  verification. Rejected: verifying from the snapshot's file list plus stat and enumerating
  later, which would keep two definitions of "the files" alive during verification.
  Reason for not building it here (revised 2026-11-05, an hour after the plan was
  committed): the review session reported that the fix was approved as its own 0.10.x
  patch and is being built by the project-awareness session on a branch from `main`,
  together with skipping the second project build. Two sessions editing
  `RepositoryFileSelector` would conflict for no gain, so phase 0A is dropped and this
  branch merges `main` when the patch lands. The design assumes enumeration under about
  1 s on jellyfin (their estimate 0.9 s). If the patch has not landed by phase 11,
  enumeration is reported as measured, the goal-2 figure is reported with and without it,
  and the coordinator decides then whether to reinstate the phase.
  Landed 2026-11-05 as `main` a1f62fc (c45c02d selector, 464eda1 one build per first
  scan), merged into this branch as 0cf756d. Measured by that session on jellyfin, AOT
  host: enumeration 11.67-11.83 s to 0.50-0.55 s including host startup; cold start to
  `ready` 48.5-49.4 s to 32.9-33.9 s; graph identical. Verification may assume about
  0.5 s. The new selector agrees with the old one, not with Git, in at least one case
  (`x[!a]b` matches `x/b`); that has its own follow-up and is not changed here.
- D26 [coord]. TieredPGO stays on in the worker and the walk's batch barrier is not
  touched. Reason: PGO off made top-level edits about 17 percent slower in the profiling
  session's check, and once the cache exists a full first scan is rare while edits are
  constant. Both belong to the profiling session.
- D27 [coord]. The comparison baseline for phase 11 is the last release before the cache,
  v0.10.7 (`main` f4b16d8, merged here as 3f8b053), with v0.10.0 kept as a history column.
  Reason: `main` gained two worker patches while this plan was being written. Measured by
  the review session on jellyfin through the AOT host, same graph: cold start 33.5-35.8 s
  to 18.9-20.8 s; top-level edit 8.2-9.6 s to 4.0-5.3 s (BaseItem) and 3.6-4.5 s to
  2.0-2.3 s (ApplicationHost); body edit unchanged at 1.1-1.4 s; worker peak about 900 MB;
  host 600-690 MB. Comparing 0.11 with 0.10.0 would credit the cache with their gains.
  Consequences: every estimate in this document that was derived from a 27-29 s jellyfin
  walk is too high by about a factor of 2.5 to 3 (the walk is now about 10 s). That
  covers the first-edit rows of 3.7, the wake cost in 3.9 and R8. The rows are corrected
  below; the 30-minute hibernation default was justified by the old figures and is
  decided again in phase 9 from measured wake cost, with 15 minutes as the candidate.
  Constraints carried into phases 6, 7 and 10: `AskEveryInvokedName` is a test-only
  switch that must stay; nothing may depend on walk order (results are recorded in
  requested order, pinned by `CSharpWorkerWalkOrderTests`); a cancelled walk throws and
  records nothing; `CSharpWorkerFirstScanBuildTests` keeps its intent when phase 7
  replaces `IsFreshBuildOf`.

**Process**

- P1. Implementers run on Opus through the implementation skill; every phase gets two
  independent adversarial Opus reviews; the coordinator triages; a fresh-context audit
  closes the work. Set by the owner when the work was delegated.
- P2. Decided by Ben, 2026-11-05, stated in the coordinating session; replaces the review
  part of P1 from the first review gate on. Every phase gets one Opus reviewer and one
  Sonnet reviewer, independent and adversarial, with the same brief, diff and gates;
  neither sees the other's findings; both sets are triaged the same way. Reason: a usage
  limit stopped phase 0, this cuts Opus use per phase by about a third, and the two
  models find different things. The coordinator may add a second Opus reviewer to a
  high-risk phase. Chosen now: phase 2 (snapshot format and authenticated loader: a
  parsing mistake is a wrong answer or a crash), phase 6 (worker state and verification:
  a restore mistake is a silently wrong graph) and phase 8 (mapped shard and overlay:
  the commit rules must match the heap store rule for rule). Others are added only with
  a reason recorded here. Implementers stay on Opus; limits are waited out, not worked
  around by changing models or cutting gates.

**Coordinator review of the plan's own decisions, 2026-11-05**

Each [plan] decision was checked against the brief and `AGENTS.md`. All are accepted; four
carry a condition or a note because they touch something the brief wrote down.

- D8, HMAC with a per-user key: accepted. The brief says a file that cannot be trusted is
  rebuilt, never trusted, and a repository-supplied snapshot with a matching worker state
  file would otherwise verify as unchanged and serve invented facts indefinitely. Cost: a
  machine with no writable home runs without a cache, which is today's behavior.
- D14 and D17, worker-private state file: accepted. The brief lists "per-file buckets with
  their hashes" among the container's contents. File buckets and fingerprints are in the
  snapshot; the emitted-fact hashes and file stat data sit in the worker's state file,
  which the snapshot names and binds by id and SHA-256. Reason: the host has no use for
  them and the worker cannot be restored from the settled graph (D14). The cache directory
  still holds everything the brief lists, and one authenticated file decides whether the
  pair is used.
- D15, one new request: accepted. It is gated by an optional capability, the protocol
  version stays 1, and a worker that never declares the capability never sees it.
- D20, capture on the coordinator: accepted with a bound. The brief says writing the
  snapshot must not block queries or the next commit. The write does not. The capture
  holds the coordinator only for the worker's state save, after 10 s of quiet. Bound: 250 ms
  on jellyfin, measured in phase 6. Above that, the fallback of R7 is built in phase 6, not
  deferred.
- D3, staging: accepted. Phases 2-7 serve a loaded snapshot from the heap, so goal 3 is
  met only from phase 8 on. Phase 8 is mandatory, not optional.
- D4 and the two store fixes in phase 1: accepted. Result order becomes deterministic,
  which changes the order of some lists against 0.10.0 and nothing else; today's order is
  dictionary order and differs between runs.
- Phase 10 stays optional and phase 12 droppable. Both are decided after phase 9 from the
  measured first-edit cost.

## 3. Detailed design

### 3.1 What lives where

```
<root>/.codecontext/cache/
  .gitignore                                   "*"
  default/                                     the host's workspace id
    writer.lock                                exclusive, held by the writing instance
    graph.0000000042.5d0c9a1e.ccsnap           host snapshot, sequence 42
    worker-csharp.0000000042.5d0c9a1e.state    C# worker state paired with it
    graph.0000000043.91f0e2aa.tmp              a write in progress
~/.codecontext/cache.key                       32 random bytes, user-only
```

| Data | Owner | Home | Used for |
|---|---|---|---|
| Settled nodes and edges, indexes | host | `.ccsnap` shard blob | queries, startup |
| Coverage, fingerprints, state file id and hash | host stores, worker produces | `.ccsnap` shard meta | labels, `baseline` |
| Per-file walk results, emitted-fact hashes, file size/mtime/content hash | worker | `.state` | deltas after a restart |
| Roslyn compilations, trees | worker | memory only | binding |

### 3.2 Snapshot file format, version 1

All integers little-endian. Offsets in the file header and shard directory are absolute;
offsets inside a shard blob are relative to the blob start. Every blob and section starts on
an 8-byte boundary.

```
file := header(256) | host meta | shard directory | shard blob* | footer(40)
```

Header, 256 bytes:

| Offset | Type | Field |
|---|---|---|
| 0 | 8 bytes | magic `43 43 53 4E 41 50 0D 0A` ("CCSNAP\r\n") |
| 8 | u32 | formatVersion = 1 |
| 12 | u32 | endianTag = 0x01020304 |
| 16 | u32 | headerSize = 256 |
| 20 | u32 | flags; bit 0 = paths compared case-insensitively by the writer |
| 24 | u64 | fileLength, including the footer |
| 32 | u64 | snapshotSequence |
| 40 | u64 | storeGeneration at capture |
| 48 | i64 | createdUtcTicks |
| 56 | u64 | hostMetaOffset |
| 64 | u32 | hostMetaLength |
| 68 | u32 | shardCount |
| 72 | u64 | shardDirectoryOffset |
| 80 | 16 bytes | writer instance id |
| 96 | 160 bytes | reserved, zero |

Host meta: `u32 count`, then per entry `u16 keyLength, key, u32 valueLength, value` in UTF-8.
Keys: `hostVersion` (informational version), `rootPath`, `platform`.

Shard directory: `shardCount` entries of 64 bytes: `u64 blobOffset, u64 blobLength,
32 bytes blobSha256, u32 nodeCount, u32 edgeCount, u32 flags, u32 reserved`.

Footer, 40 bytes: magic `43 43 53 4E 45 4E 44 0A` ("CCSNEND\n"), then HMAC-SHA256 over bytes
`[0, fileLength - 32)`.

Shard blob: `shard header(128) | section table | sections`.

Shard header: `u32 magic "SHRD", u32 sectionCount, u32 nodeCount N, u32 edgeCount E,
u32 idCount S, u32 stringCount, u32 fileCount F, u32 nameCount, u32 foldedNameFirstRef,
u32 flags`, rest reserved. Section table entry, 24 bytes: `u32 kind, u32 elementSize,
u64 offset, u64 length`.

| Kind | Section | Content |
|---|---|---|
| 1 | ShardMeta | parserId, workspaceId, parserVersion, state file name, stateId, stateSha256 (key/value as host meta) |
| 2 | StringOffsets | `u32[stringCount + 1]` byte offsets into StringBytes |
| 3 | StringBytes | UTF-8, no terminators |
| 4 | Nodes | `NodeRecord[N]`, grouped by file bucket, id order inside a bucket |
| 5 | Edges | `EdgeRecord[E]`, sorted by (source sid, edge id) |
| 6 | IdTable | `[S] { u32 str, u32 nodeIndex or 0xFFFFFFFF }`, sorted by id; the index is the sid |
| 7 | IdHash | `u32[]` open addressing, value sid + 1, 0 empty |
| 8 | IdentifierHash | `u32[]`, value nodeIndex + 1, keyed by identifier |
| 9 | EdgeIdHash | `u32[]`, value edgeIndex + 1, keyed by the full edge id |
| 10 | BySourceOffsets | `u32[S + 1]` ranges into Edges |
| 11 | ByTargetOffsets | `u32[S + 1]` ranges into ByTargetEdges |
| 12 | ByTargetEdges | `u32[E]` edge indexes grouped by target sid, edge id order in a run |
| 13 | Files | `[F] { u32 pathStr, u32 normalizedKeyStr, u32 firstNode, u32 nodeCount, u32 flags, u32 reserved }` |
| 14 | NameTable | `[nameCount] { u32 foldedNameStr, u32 postingsStart, u32 postingsCount }`, sorted by UTF-8 bytes |
| 15 | NamePostings | `u32[]` node indexes, id order in a run |
| 16 | Metadata | `u32 setCount, u32[setCount + 1] offsets`, sets of `u32 pairCount, (u32 keyStr, u32 valueStr)*` |
| 17 | CrossShardEdges | `u32[]` indexes of edges with an endpoint that is no node of this shard |
| 18 | TypeHistogram | node kinds then edge kinds, each `u32 count, (u32 typeStr, u32 n)*` |
| 19 | Coverage | UTF-8 JSON array of `ProjectCoverage`, or empty |
| 20 | Fingerprints | UTF-8 JSON array exactly as the worker sent it, or empty |

Strings. Reference 0 is null; reference k is entry k - 1. The empty string is a real entry.
Strings are deduplicated ordinally per shard. Folded names (`Name.ToUpperInvariant()`) occupy
the contiguous reference range starting at `foldedNameFirstRef`, so a substring search scans
one byte range (est. 200 KB on jellyfin).

NodeRecord, 80 bytes: `u32 sid, identifierStr, nameStr, typeStr, languageStr, fileIndex;
i32 startLine, endLine, startCol, endCol; u32 namespaceStr, visibilityStr, signatureStr,
returnTypeStr, parametersStr, modifiersStr, metricsStr, metadataRef, flags, reserved`.
Flag bit 0 is `metadata.external == "true"`. A null `FilePath` and an empty one are separate
buckets because their `pathStr` differs.

EdgeRecord, 32 bytes: `u32 sourceSid, targetSid, typeStr, idA, idB; i32 line, column;
u32 metaAndForm`. A null source or target is 0xFFFFFFFF; null-source edges sort last, after
`BySourceOffsets[S]`. Low 2 bits of `metaAndForm` are the id form, the rest is a metadata set
reference (0 = none).

- Edge id form 0: `idA` is the whole id. Form 1: `id == sourceId + str(idA) + targetId +
  str(idB)`. The writer uses form 1 only when recomposition reproduces the id exactly. Both
  bundled workers and the fake worker qualify. Without this the 296k jellyfin edge ids alone
  would be about 59 MB.
- `line` and `column` hold the metadata values when the string is canonical decimal int32
  (`i.ToString(Invariant) == value`); otherwise the pair stays in the metadata set.
  `int.MinValue` means absent.
- Metadata sets exclude `parserId` and `workspaceId`: they equal the shard's and are added
  back on materialization. The writer refuses a shard where a fact disagrees.

Hashing. Hash sections use FNV-1a 64-bit over UTF-8 bytes, slot `(h ^ (h >> 32)) & (cap - 1)`,
linear probing, capacity the next power of two at or above `count / 0.75`. A lookup compares
the key bytes with the stored bytes.

Limits. Counts below 2^31 and StringBytes below 4 GiB per shard, or the shard is not
persisted. Size on jellyfin, est.: nodes 2.1 MB, edges 9.5 MB, strings 8-10 MB, indexes
4.5 MB, total 24-28 MB (measured in phase 2).

Load and validation order. A failure at any step ignores the file and tries the next older.

1. Parse the name; skip `.tmp`. Open with `FileAccess.Read, FileShare.Read`.
2. Length at least 296; header magic, endian tag, format version, header size; `fileLength`
   equals the real length.
3. Host meta: host version, root path (`PathComparer`), path case flag.
4. Streaming HMAC over the same handle with `FileOptions.SequentialScan`; compare the footer.
   No page enters the working set through a mapping. Est. 30-60 ms for 25 MB.
5. Map the same handle read-only. Check directory and section tables: bounds, no overlap,
   alignment, element sizes, lengths against counts, first and last entry of each offsets
   array.
6. Per shard: drop it if no registered worker has its parserId or the workspace id is not the
   host's.

Every accessor goes through one `MappedRegion.Slice(offset, length)` that checks bounds and
throws `SnapshotCorruptException`. No span or pointer leaves a read call. A corrupt value met
after authentication (a writer bug) fails the request with an error, drops the shard and
queues a full rescan. It never crashes and never answers from data that failed a check.

Format as built (phase 2). The text above was written before the code; where it is silent
or differs, this list is the format. A reader requires exactly this layout.

1. Layout is dense. Host meta starts at 256; the directory at the next 8-byte boundary
   after it; each blob at the boundary after the previous part; the footer at the boundary
   after the last blob. Inside a blob each section starts at the boundary after the
   previous one and the blob ends at the boundary after its last section. Padding is zero.
2. Section table: kinds 1 to 20, each once, in kind order. Element sizes: ShardMeta 1,
   StringOffsets 4, StringBytes 1, Nodes 80, Edges 32, IdTable 8, the three hashes 4,
   BySourceOffsets 4, ByTargetOffsets 4, ByTargetEdges 4, Files 24, NameTable 12,
   NamePostings 4, Metadata 4, CrossShardEdges 4, TypeHistogram 4, Coverage 1,
   Fingerprints 1.
3. Host meta keys are written in the order hostVersion, rootPath, platform, at most 1 MiB;
   `platform` is not checked on read. ShardMeta holds parserId and workspaceId, then
   `parserVersion`, `stateFile`, `stateId`, `stateSha256` when present. A key/value block
   must be consumed exactly, be valid UTF-8 and hold no duplicate key.
4. Shard header flags and reserved bytes are 0 and `foldedNameFirstRef` is 1. Directory
   flags and reserved are 0. The directory's node and edge counts equal the shard header's.
5. `blobSha256` exists for phase 8's byte-for-byte copy of an unchanged blob. The HMAC
   covers it; it is not verified at load.
6. Shards are written in ascending `ShardKey` order; a reader requires strictly ascending
   keys.
7. Strings: references 1 to nameCount are the folded names in NameTable order (UTF-8 byte
   order). Every other distinct string follows in ordinal UTF-16 order. The fold is
   `ToUpperInvariant` under the host's invariant globalization; the mode is not recorded,
   because a file from another host version is refused.
8. The id set is every node id, edge source and non-null edge target, sorted ordinally
   (UTF-16); an id's index is its sid. Ids are read by sid and found through IdHash, never
   by binary search.
9. Files: the null-path bucket first if there is one, then raw paths ordered
   OrdinalIgnoreCase then Ordinal. Each bucket is non-empty; node ranges are contiguous
   from 0 to N; nodes are in id order inside a bucket. `normalizedKeyStr` is
   `FilePathMatcher.Normalize(path)`, or 0 for a null or blank path. File flags are 0.
10. NameTable entry k names string `foldedNameFirstRef + k`; names strictly increase by
    UTF-8 bytes; postings runs are non-empty, contiguous from 0 and end at the postings
    length; a run holds node record indexes in node id order. A node with a null name has
    no entry.
11. Hashes: IdHash is keyed by every id (value sid + 1); IdentifierHash by every non-blank
    identifier (value node index + 1), its capacity from that count; EdgeIdHash by the
    full edge id (value edge index + 1). Capacity is the smallest power of two at or above
    4 x count / 3, and 0 for no keys. Insertion order: sid order, node record order, edge
    record order. A probe stops after `capacity` steps.
12. Edge records are sorted by (source sid, edge id). The writer persists no null source,
    so `BySourceOffsets[S] == E`; the format still represents one. Null-target edges
    follow `ByTargetOffsets[S]` in edge id order. Every run is in strictly increasing edge
    id order; one that is not is corrupt when read.
13. Edge id form 1 is used only when the target is non-null and the id starts with the
    source: `idA` runs to the first occurrence of the target at or after the source's
    length and `idB` is the rest, possibly the empty string. Otherwise form 0 with
    `idB = 0`.
14. `line` and `column` are held in the record when the value is canonical decimal int32
    and not "-2147483648"; otherwise the record holds `int.MinValue` and the pair stays in
    the metadata set.
15. Metadata section: `u32 setCount`, then `setCount + 1` u32 byte offsets from the
    section start (the first set starts at `4 + 4 x (setCount + 1)`, the last offset is the
    section length), then each set as `u32 pairCount, (keyStr, valueStr)*` with keys in
    ordinal order. `parserId`, `workspaceId` and record-held `line`/`column` are excluded.
    Sets are deduplicated and numbered from 1 in first use, nodes in record order and then
    edges. Reference 0 is the empty set. A null value is legal; a null key is not.
    Materialized dictionaries are ordinal and enumerate in key order; insertion order is
    not preserved (the store and `ContextService` read metadata by key only).
16. Node flags are exactly bit 0 = external, where external means metadata `external` is
    the ordinal string "true" (the store's prune rule). A record whose flag disagrees with
    its metadata is corrupt.
17. CrossShardEdges lists, in strictly ascending record order, the edges whose source or
    non-null target is no node of the shard. TypeHistogram writes node kinds then edge
    kinds, each in ordinal type order, skipping null and empty types.
18. Sections whose length no count fixes: IdentifierHash, CrossShardEdges, the null-target
    tail of ByTargetEdges, and the self-describing Metadata, ShardMeta, Coverage and
    Fingerprints. All other section lengths must match their counts exactly.
19. Validation order, replacing steps 1 to 5 above: (1) the name is
    `graph.<10 digits>.<8 lowercase hex>.ccsnap` and the header sequence equals the
    name's, so an older authentic file cannot be renamed to look newer; (2) on Unix,
    before hashing, on the open handle: refuse a file that grants group or other write,
    then set its mode to 0600, which only the owner (or root) can do, and refuse it if
    that fails; (3) at least 296 bytes and at most 1 GiB, before anything is hashed;
    (4) magic, endian tag, format version, header size, `fileLength`; (5) host meta at
    exactly 256: host version, root path by the path case rule, path case flag;
    (6) footer magic, then the HMAC, compared in constant time, with a 32-byte key;
    (7) map; dense directory and layout; key order; each shard's tables, name table and
    file buckets. Open costs O(shards + names + files), no per-node or per-edge work.
    Text from the file quoted in a refusal before authentication is cut to 200 characters
    with control characters escaped.
20. Persistability, checked by the writer per shard and again by the loader on every
    shard it materializes: not the `Default` shard; at least one fact; every node id, edge
    id and edge source carries `<parserId>:<Uri.EscapeDataString(workspaceId)>:`; targets
    may be anything, including null; node identifiers are non-null; metadata is a
    `Dictionary` with the default or ordinal comparer whose `parserId` and `workspaceId`
    equal the shard key; every non-blank identifier resolves to its own node through the
    shard's identifier map; every string is well-formed UTF-16. The writer skips a shard
    that fails and writes the others; the loader refuses the whole file. This replaces the
    one-line rule in D6.
21. The loader refuses the whole file for any structural failure in any shard, for a node
    id, edge id or non-blank identifier that appears twice (in a shard or across shards),
    and for a shard failing rule 20. It publishes nothing from a refused file.
22. Limits: per shard, counts below 2^30 and each section and the blob at most
    `Array.MaxLength` bytes (about 2 GiB), otherwise the shard is skipped; a file at most
    1 GiB, otherwise nothing is written and the result says why. The 2^31 and 4 GiB
    figures above are superseded. A big-endian process refuses to read, and the writer
    throws `PlatformNotSupportedException`.
23. Files are created 0600 on Unix. The key is 32 random bytes, created 0600 at creation
    (its directory 0700 if missing) by writing a temp file and moving it into place
    without overwrite, so concurrent creators agree; a key file that grants group or
    other any access is refused on Unix. On Windows the profile's ACL applies to the key
    and deny-write sharing to an open snapshot.

Measured on the generated shard (26,000 nodes, 298,792 edges; Release, median of 5, file
in the OS cache): 22,143,416 bytes (21.1 MiB; the estimate was 24 to 28 MB); write 850
ms; open, authenticate, map, validate and materialize to heap shards 605 ms; open without
materializing 14.8 ms, of which the streaming HMAC is 14.1 ms. Largest sections: Edges
9.56 MB (43 percent), StringBytes 5.89 MB (27 percent), EdgeIdHash 2.10 MB, Nodes 2.08
MB, ByTargetEdges 1.20 MB. The writer allocates about 448 MiB in total for that file
(peak about 191 MiB above baseline); it runs off the commit path. Cold-disk times were
not measured.

### 3.3 The shard read interface

`IGraphShard` (internal, `CodeContext.Core.Repositories.InMemory`) is implemented by
`GraphState` (heap) and `MappedShard`. `InMemoryDatabase._shards` becomes
`ImmutableSortedDictionary<ShardKey, IGraphShard>`.

Read members: `NodeCount`, `EdgeCount`, `TryGetNode(id)`, `TryGetNodeIdByIdentifier`,
`TryGetEdge(id)`, `FindNodesByName(name, exact)`, `FindNodesByPath(requestedPath)`,
`GetEdgesBySource(id)`, `GetEdgesByTarget(id)`, `GetAllNodes()`, `GetAllEdges()`,
`GetFilePaths()`, `AddStatistics(...)`, `GetCrossShardEdgeIds()`.

Commit members, called under `_commitLock` and returning a new shard or null for "unchanged":
`ApplyScoped(nodes, edges, replaceFiles, foreignShards, out removedIds,
out removedNotReestablished)`, `WithoutForeignConflicts(...)`,
`CollectNodesOutside(keepPaths, into)`, `WithoutNodes(removedIds)`.

How each read path of today is served by a mapped shard:

| Read path | Today | Mapped shard | Index section |
|---|---|---|---|
| Node by id | dictionary | IdHash, then one NodeRecord | IdHash |
| Node by identifier | dictionary | IdentifierHash | IdentifierHash |
| Name, exact | scan of all nodes | binary search in NameTable, postings | NameTable, NamePostings |
| Name, substring | scan of all nodes | scan of the folded-name byte range, postings | same |
| By file path | frozen dictionary, key scan for relative | scan of Files keys (decoded once per mapping, est. 200 KB heap), node range | Files |
| Edges by source | frozen dictionary | BySourceOffsets range of Edges | BySourceOffsets |
| Edges by target | frozen dictionary | ByTargetOffsets range of ByTargetEdges | ByTargetOffsets, ByTargetEdges |
| Edge by id | dictionary | EdgeIdHash | EdgeIdHash |
| Statistics | cached pass | TypeHistogram minus hidden plus overlay, cached per shard instance | TypeHistogram |
| Cross-shard sweep | cached id set | CrossShardEdges plus the overlay's own | CrossShardEdges |
| Prune | scan of nodes | scan of Files, then node ranges | Files |
| Enumerate all | cached list | materializes every record; not in place | none |

Name and path indexes produce candidates only. Each candidate is materialized and checked
with the same predicate the heap path uses, so a fold subtlety can cost time, not
correctness. The index must still return a superset; a property test pins
`Equals(a, b, OrdinalIgnoreCase)` implies `a.ToUpperInvariant() == b.ToUpperInvariant()`.

What is not in place, and its cost:

- `GetAllNodes` and `GetAllEdges` materialize the whole shard per call: on jellyfin about
  26 MB of transient garbage for nodes, and they touch the whole node section and string heap
  (est. 10-12 MB resident). Production callers after phase 1: `GetRelatedItemsAsync`, which
  only runs with `includeRelated=true` (off by default, not exposed by `query`), and the test
  oracles. `FindTestFilesAsync`, which runs on every `--tests` query, moves to
  `GetFilePaths()` plus per-file lookups.
- Materialization allocates. A `CodeNode` is about 15 string decodes, a `CodeEdge` an id
  concatenation and a small dictionary. Strings from low-cardinality columns (type, language,
  visibility, modifiers, edge kind, file path, namespace) are decoded once per mapping and
  cached; ids, names and signatures are decoded per use. Budget: the 100-query mix has p95 at
  most twice the heap p95 or 50 ms, whichever is larger. If phase 8 misses it, a lazy edge id
  and a struct-backed edge metadata dictionary are the allowed fixes.
- Legacy in-place mutations (`UpsertNode`, `RemoveNode`, `UpsertEdge`,
  `RemoveEdgesTouchingNode`) have no production caller. On a mapped shard they first
  materialize it to a heap shard.

### 3.4 Mapped shard, overlay and compaction

`MappedShard = (Base, Overlay)`, both immutable. `Overlay` holds a heap `GraphState` with
the facts committed since the base was written, a `HiddenNodes` bit set over base nodes and
a `DeadEdges` bit set over base edges (3.3 KB and 37 KB on jellyfin, copied per commit).

Effective state: base nodes not hidden, plus overlay nodes; base edges not dead, plus overlay
edges. Results merge base runs and overlay lists by id; both are already in canonical order.

Scoped commit with `replaceFiles` F, delta nodes and delta edges, reproducing
`BuildScopedState` rule for rule:

1. Removed ids: every non-hidden base node in a Files bucket whose raw path is in F
   (`PathComparer`, never a null or empty path) is hidden; every overlay node with a path in
   F is dropped. Both go into `removedIds`.
2. Each delta node, in order: look its identifier up among identifiers registered by this
   delta so far, then among carried nodes (overlay first, then IdentifierHash for a
   non-hidden base node), then in each foreign shard. A hit with a different id throws
   `InvalidDataException` before anything is published. Then the node enters the overlay,
   and a non-hidden base node with the same id is hidden.
3. For each removed id: every base edge in its by-source run dies, and overlay edges sourced
   there are dropped. This holds even when the delta re-supplies the node, as today.
4. For each removed id that no node carries after step 2: every base edge in its by-target
   run dies, and overlay edges targeting it are dropped.
5. Each delta edge enters the overlay; a live base edge with the same id (EdgeIdHash) dies.
6. Foreign shards are swept through `WithoutForeignConflicts` exactly as
   `RebuildForeignShard` does: dangling edges from CrossShardEdges and the overlay, delta id
   collisions through IdHash and EdgeIdHash, and the evicted node's own edges.

A commit with `replaceFiles == null` returns a heap shard and drops the mapping reference.
Prune hides nodes of buckets whose path is outside the keep set, except nodes with the
external flag, and kills their edges in both directions.

Two store fixes land in phase 1 so that both shard kinds follow one definition:

- A shard's identifier map is `{ node.Identifier -> node.Id }` over its final nodes. Today a
  delta node that overwrites a carried node with a different identifier leaves the old
  identifier resolving for one generation; `UpsertNode` already cleans this up and
  `BuildScopedState` will too.
- A scoped commit with no files, no nodes and no edges returns the same shard reference and
  only advances the generation. An unchanged verification then costs O(1) instead of a
  rebuild of the shard, and it leaves the snapshot clean.

Compaction and swap. The writer serializes a captured shard (heap, or base plus overlay)
through the read interface into a new file, authenticates and maps it, then calls
`InMemoryDatabase.TryReplaceShard(key, expected, replacement)` under `_commitLock`. The swap
happens only if the shard is still the captured reference and no reconciliation is staged;
otherwise the file stays valid on disk for the next start and the next cycle tries again.
The published map changes by the same single volatile write as a commit.

Mapping lifetime. Each read call holds the view's `SafeHandle` reference for its duration.
Retiring a snapshot disposes the view, so the unmap happens when the last reader releases.
A reader that finds the handle closed re-latches `_shards` and retries. The file is deleted
after the unmap; a failed delete is retried at the next write and at the next start.

### 3.5 Snapshot lifecycle

Trigger. After every mutation command the coordinator tells `SnapshotService` that the
store may have changed. The service is dirty when a shard reference, or a shard's coverage
or fingerprints, differs from the last capture. It enqueues one `SnapshotCommand` after
10 s without a further commit, or 120 s after it first became dirty. Both are options.

Capture, inside the coordinator loop, so no scan, batch or reconciliation is in flight:

1. For each worker that declared `persistentState`, has indexed and has a live process:
   `workspace/saveState` with a new state path and id. A failure, or a worker without the
   capability, leaves that shard without state: its next start is a full re-walk. A
   hibernated worker keeps its previous state file if its shard is still the reference last
   captured.
2. Latch the published shard map, the store generation, and each shard's coverage,
   fingerprints and parser version.
3. Hand the capture to one background task and return. The coordinator is held for step 1
   only.

Write, in the background, one at a time: serialize to `graph.<seq>.<id>.tmp` in chunks of
1 MiB (few watcher events), `Flush(true)`, close, rename to `.ccsnap`, authenticate and map
it, swap shards (phase 8), retire the previous snapshot and its state files. A legacy
in-place write during serialization is detected by `GraphState.Version` and discards the
file.

| Situation | Behavior |
|---|---|
| Rapid edits | The quiet period restarts on each commit, up to 120 s. A write in flight finishes from its immutable capture; the service stays dirty and writes again. |
| Graceful shutdown (`/api/shutdown`, idle timeout, Ctrl+C) | `SnapshotService.StopAsync` runs before the coordinator and worker service stop: it enqueues a final capture when dirty and waits up to 10 s. |
| Crash or kill | The last completed snapshot remains. The next start verifies against the files and re-walks what moved. |
| Crash mid-write | Only a `.tmp` exists; it is deleted at the next start. |
| Crash after rename, before retire | Two valid snapshots; the newest wins and the older is deleted. |
| Worker state saved, host write failed | The older pair stays valid; the newer state file is deleted at cleanup. |
| Write error (disk full, read-only root) | Logged, exposed as `indexing.cache.lastWriteError`, retried after the next commit. Serving is unaffected. |
| Second instance on the root | No lock: it loads, never writes or deletes. A snapshot it has mapped cannot be deleted by the writer on Windows; the delete is retried later. If a file vanishes between selection and open, it retries selection, then scans. |

Startup. The first synchronous step of `IndexCoordinator.ExecuteAsync`: take the lock,
delete `.tmp` files when holding it, pick the newest valid snapshot, publish its shards
through `InMemoryDatabase.PublishLoadedShards` (allowed only into an empty store), restore
coverage into `AnalysisCoverageRegistry`, set the cache state to `verifying`, and delete
older snapshots and unreferenced state files. Then the startup scan runs as a verification
scan: the full-scan flow of `PerformInitialScanAsync` (every worker gets a generation, even
with no files), inside one reconciliation as today. A query that arrives before this step
sees `status: scanning` with no cache and waits one poll.

The watcher keeps `.gitignore` files as control events except below a mandatory exclusion,
where they cannot change selection. Without that, creating the cache's own `.gitignore`
would trigger a full rescan.

### 3.6 Protocol additions

All optional. A host that does not see `capabilities.persistentState: true` sends none of
them.

`initialize` result:

```json
"capabilities": { "workspaceAnalysis": true, "incrementalUpdates": true,
  "semanticAnalysis": true, "nativeSyntaxTree": true, "persistentState": true }
```

`workspace/index` params, optional `baseline`:

```json
{ "workspaceId": "default", "generation": 1, "files": ["C:\\repo\\src\\A.cs"],
  "baseline": {
    "statePath": "C:\\repo\\.codecontext\\cache\\default\\worker-csharp.0000000042.5d0c9a1e.state",
    "stateId": "5d0c9a1e7b3f4c0e9a6d2f8b1c4e7a90",
    "stateSha256": "9b1f...64 hex",
    "fingerprints": [ { "unit": "3fa9c1d2", "value": "c2a4...64 hex" } ] } }
```

`workspace/index` result, optional `baseline` and `baselineReason`:

```json
{ "workspaceId": "default", "generation": 1, "deltasEmitted": 1, "complete": true,
  "baseline": "accepted" }
{ "workspaceId": "default", "generation": 1, "deltasEmitted": 14, "complete": true,
  "baseline": "rejected", "baselineReason": "state file is missing" }
```

A worker that accepts a baseline answers with `replacesWorkspace: false` and only the files
whose facts differ from what the baseline holds, plus removed files. A worker that rejects
it answers exactly as for a request without one: `replacesWorkspace: true`. Progress is
reported over the approved file list either way, ending at `filesTotal`.

`analysis/delta`, optional `fingerprints`, first chunk only, beside `coverage`:

```json
{ "parserId": "csharp", "replacesWorkspace": false, "replacesFiles": [], "nodes": [],
  "edges": [], "coverage": [], "isLastForRequest": true,
  "fingerprints": [ { "unit": "3fa9c1d2", "value": "c2a4..." },
                    { "unit": "<workspace>", "value": "07be..." } ] }
```

A worker with the capability sends `fingerprints` on every request. The applier keeps the
last set per shard and replaces it when the request commits; a commit that carries none
clears it, and no baseline is offered from a snapshot of that generation.

New request `workspace/saveState`, host to worker:

```json
{ "workspaceId": "default",
  "statePath": "C:\\repo\\.codecontext\\cache\\default\\worker-csharp.0000000043.91f0e2aa.state",
  "stateId": "91f0e2aa..." }
```

Result: `{ "workspaceId": "default", "stateId": "91f0e2aa...", "saved": true,
"bytes": 13400211, "sha256": "..." }`. The worker writes a temp file beside the path, flushes
and renames it. It answers when the file is durable. `saved: false` or an error means no
state for this capture.

The host offers a baseline only when all hold: the capability is declared; the shard came
from the authenticated snapshot, or is still the reference last captured (wake, crash
restart); the shard's recorded parser version equals `InitializeResult.ParserVersion`; a
state id and hash are recorded; this is the first index of this worker process.

Sequences. H is the host, W the C# worker.

Cold start, nothing changed:

1. H authenticates and maps `graph.42`, publishes shards and coverage. Cache state
   `verifying`. Queries are answered from here on, labelled.
2. H enumerates approved files with the selector, starts W, `initialize`, `workspace/open`.
   W records the list and builds nothing.
3. H sends `workspace/index` with `baseline`. W checks the state file (hash, id, worker
   version, root, fingerprints equal to the baseline's), loads the project model, stats the
   files, recomputes fingerprints: all equal.
4. W sends progress to n/n and one empty delta with coverage and fingerprints. H commits a
   no-op generation, the reconciliation commits, status is `ready`, cache state `verified`.
   Nothing is written: the snapshot is clean.

Cold start, files changed while down: steps 1-3 as above, then W finds units whose
fingerprint moved, restores walk results for the rest, builds only the compilations those
units and their references need, walks their files, settles, and sends the buckets whose
hash differs from the restored emitted-fact hashes, plus removed files. H commits the
file-scoped delta. Answers before the commit are stale and labelled; after it the label
goes. The snapshot is dirty and is rewritten after the quiet period.

Worker without the capability (TypeScript, third-party): step 1 as above. H sends
`workspace/index` as today. W walks everything and answers `replacesWorkspace: true`. H
commits the replacement. The cached facts were served, labelled, for the whole walk.

Corrupt or foreign snapshot: validation fails at the step that catches it (3.2), the file is
ignored and, when the instance holds the lock, deleted. With no older valid file the start
is today's: no cache object beyond `state: ignored` with the reason, a full scan, and a new
snapshot afterwards.

### 3.7 C# worker: what a fresh process must know

After a snapshot load the worker has no retained facts. To emit what a never-restarted
worker would emit it needs, for every file of an unchanged project:

| Needed | Why | Where it comes from |
|---|---|---|
| Per-file emitted-fact hash (`_factHashes`), including `external:<ns>` buckets and files of changed projects | the diff decides what to send and what to remove | state file |
| Pre-settlement walked nodes, including copies of ids another file owns | partial types: when the owning file goes, the next file's copy, with its own span, becomes the node | state file |
| Pre-settlement walked edges, by walking file | an edge moves to the bucket of its source node's owner; re-emitting one file must not drop edges another file contributed | state file |
| Per-file stub use: stub nodes with assembly, containment edges | a stub leaves when its last user stops; the assembly is chosen across all users | state file |
| Unqualified identifiers | qualification on collision is recomputed over all files; owners re-emit together | walked nodes hold the unqualified form |
| Per-file unresolved-name counts and a contains-directives flag | coverage entries for projects that bind nothing this run | state file |
| Owner of each file, unit ids and qualifiers | id prefixes, qualification | recomputed from the project model |
| Size, mtime, content SHA-256 per file | verification without reading unchanged files | state file |
| Unit fingerprints | pairing check, and which units moved | state file and `baseline` |

`_buckets`, `_sharedIds` and `_contestedIdentifiers` are not stored: `SettleOwnership` is a
pure function of `_walked` and rebuilds them. The first settlement after a restore treats
every file as just walked, so every bucket is rebuilt and hashed once, and the diff against
the restored `_factHashes` sends only real differences. If restored state ever disagreed
with the host, that diff would repair it.

State file. Worker-private format, versioned by the worker: header (magic, format version,
worker version, state id, workspace id, root path), string table, unit table (key,
fingerprint), file table (path, size, mtime ticks, racy flag, content hash, directives
flag, offset of its facts), emitted-fact hashes, then per-file facts. The host checks only
the whole-file SHA-256 it recorded. The worker reads the tables at verification and leaves
the per-file facts as bytes until the first settlement needs them. Est. 10-15 MB on
jellyfin.

File hashes. The content hash is over the raw bytes; the text handed to Roslyn is decoded
from the same bytes exactly as `File.ReadAllText` decodes it, so facts do not change. Size
and mtime are read before the bytes. An entry whose mtime is within 2 s of the read is
marked racy and is always re-hashed at verification, because a later write could share the
timestamp.

Fingerprint.

```
fingerprint(U) = SHA-256( "ccfp1", workerVersion, unitKey, Signature(U),
    for each file U compiles, by path:   path, contentSha256, ownerUnitKey,
    for each unit U references, by key:  key, fingerprint(R) )
```

`Signature` is the existing method: constants, language version, assembly name, project
name, global usings, `InternalsVisibleTo`, referenced project keys, metadata reference paths
with mtimes, sorted file list. The fallback unit uses its usings text and references every
project unit, as its compilation does. A file two projects compile counts in both. Because
referenced fingerprints are included transitively, the set of moved units is closed under
"depends on".

Verification on `workspace/index` with a baseline:

1. Validate the state file. Any failure rejects the baseline.
2. `ProjectModel.Load` over the approved files (measured 0.45-0.6 s). No compilation, no
   parse.
3. Stat every approved file. Hash those whose size or mtime moved, the racy ones, and new
   ones. A file whose hash is unchanged only gets its stat entry refreshed.
4. Recompute fingerprints. Moved units are those that differ or are new.
5. Nothing moved and no file was removed: send the empty delta. Otherwise restore `_walked`
   for files owned by unmoved units, walk the files of moved units, settle, diff, send,
   commit the hashes.

What this covers from coordinator decision 7: the host's selection policy (the approved
list), files added or removed while down (the file list is in `Signature`), files in no
project (the fallback unit), project inputs (the model is re-read from disk, so a changed
csproj, props file, assets file or reference moves `Signature`), a worker version change
(state rejected), a root path change (state and snapshot rejected), a host or format change
(snapshot ignored).

Dormant units (phase 7). A unit of an unmoved project is created without trees or a
compilation. It is materialized, references first, when one of its files must be walked or
a unit that references it must be walked. Materializing reads each file, compares its hash
with the file table, and marks the unit changed on a mismatch, so a live unit's trees always
match its walk results or the unit is re-walked. `workspace/open` becomes declarative: it
records the approved list, and reconciliation runs at the next request that needs it. That
also removes the second build of a first scan (measured 2.0-2.6 s on jellyfin), because
`workspace/index` no longer follows a build that `workspace/open` already did.

The first edit after a verification. The old tree of the edited file is gone, so
`EditClassifier` has nothing to compare. If the new bytes hash to the stored value the
change is a touch and nothing happens. Otherwise the units compiling the file are marked
changed: they and their dependents are materialized and re-walked, like a top-level edit.
Later edits in those units have trees and are classified as today. Units nothing touched
stay dormant.

| Case on jellyfin | Work | Estimate |
|---|---|---|
| First query after a cold start | authenticate, map (materialize until phase 8) | 0.1-0.5 s |
| `ready`, nothing changed | host start and enumeration 0.50-0.55 s measured with the 0.10.x patch (11.7 s without it), worker start 0.4-1.0 s, model 0.45-0.6 s, stats and reference mtimes 0.1-0.4 s, state tables 0.05 s | 1.5-2.6 s |
| First edit, leaf project (Jellyfin.Api) | parse and create its closure, up to 2.4 s; cold walk of the project and its tests; load facts, settle and hash all buckets 0.5-1.0 s | 3-5 s (was 4-8 s before D27) |
| First edit, root project | the same, with most of the repository as dependents | 12-15 s (was about 30 s before D27) |
| Second and later edits there | unchanged | 1.1-1.4 s body, 2.0-5.3 s top-level (v0.10.7) |

Cold binding cost and the one-time hash of all buckets are unmeasured; phase 7 measures
both. The root-project row is why phase 10 exists: with per-file declaration digests a
body-only change is recognized without the old tree and costs est. 3-5 s wherever it is.

### 3.8 Worker state in detail: full scans

A full walk (no baseline, or a rejected one) fills the same file table: every file is read
once, hashed, parsed and walked. `SeedFactHashes` and the fingerprints follow. A worker
that has never been asked to save writes nothing.

### 3.9 Hibernation

States of a worker session, kept by `LanguageWorkerService` and reported through
`ParserSessionRegistry`:

```
Running --idle period elapsed, preconditions hold--> Hibernating --process exited--> Hibernated
Hibernated --activity--> Waking --baseline verification committed--> Running
any --start or request failure--> Failed          (existing rules and restart budget)
```

`Running` is today's `ready` and `indexing`. `Hibernating` and `Waking` show as `starting`
with a message. `Hibernated` is a new `ParserSessionState`, reported as `hibernated`, and
counts as enabled. `Failed` keeps `lastError`; a hibernated session has none and no pid.

Activity for a worker: a file batch routed to it, a project-input event, a scan or refresh,
a syntax-tree request. Queries are not activity: they do not need the worker.

Preconditions, checked inside a `HibernateCommand` on the coordinator so no batch is in
flight: this instance holds the writer lock; the worker's shard is the reference last
captured and that capture recorded a state file. If the shard is newer, the command asks
for a capture instead and hibernation is retried at the next idle check. A worker is never
shut down holding a generation that is not on disk, and hibernation itself writes nothing.

Hibernate: `supervisor.HibernateAsync()` sends `shutdown`, closes stdin, waits for exit and
cleans up. It does not set the supervisor's `_stopping`, does not count against
`MaxRestarts`, and leaves the supervisor reusable.

Wake: the first activity starts a new process. Before the pending operation the host sends
`workspace/open` and `workspace/index` with the baseline, which is the cold-start
verification. The worker finds the changed files itself; the pending batch then applies as
touches. A syntax-tree request on a hibernated worker goes through a `WakeCommand` first, so
the verification commit stays on the coordinator's ordered path. The same rule lets a
crashed worker restart from the last capture instead of re-walking everything, when its
shard has not changed since.

Idle period: `CodeContextOptions.WorkerIdleMinutes`, default 30, 0 disables. Reasons: a
wake with nothing changed costs 1.2-2.2 s; a wake caused by an edit costs the first-edit
row of 3.7, 4-8 s for a leaf project and up to about 30 s for a root project on jellyfin,
and under 6 s on the two small repositories, against 1.3 s warm. That is too much to pay
inside a working session, where edits arrive minutes apart, and cheap after half an hour of
silence, when it frees 0.4-1.0 GB. The instance idle timeout stays at 120 minutes of no API
activity; snapshot, hibernate and wake commands do not count as busy for
`IdleShutdownService`.

### 3.10 Surface

`indexing.status` keeps `scanning`, `ready`, `error`. During the startup verification it is
`scanning` with file progress, as for any scan. New, additive:

```json
"indexing": {
  "indexed": false, "status": "scanning", "filesProcessed": 0, "filesTotal": 2251,
  "operationId": 1,
  "cache": {
    "state": "verifying", "servingCached": true,
    "snapshotAt": "2026-11-04T10:12:03.0000000Z", "snapshotSequence": 42,
    "sizeBytes": 26123456, "workerStateBytes": 13400211,
    "lastWriteMs": 412, "lastCaptureMs": 140, "lastWriteError": null,
    "verification": { "csharp": "pending", "typescript": "pending" } } }
```

| `cache.state` | Meaning | `servingCached` |
|---|---|---|
| absent | cache disabled, or nothing loaded and no scan committed yet in this process | - |
| `ignored` | a snapshot was found and refused; `reason` says why | false |
| `verifying` | answers come from a snapshot not yet checked | true |
| `waking` | a hibernated worker is applying changes | true |
| `unverified` | the verification scan failed; `status` is `error` | true |
| `verified` | a scan committed in this process, whether or not a snapshot was loaded first. On a first-ever start `snapshotAt`, `snapshotSequence` and `sizeBytes` are absent until the first write completes | false |

`verification` per parser is `pending`, `baseline` (the worker confirmed or patched the
snapshot) or `replaced` (it re-walked everything). `parsers.sessions[].state` gains
`hibernated`. `coverage` gains `"cached": true` while `servingCached`. `database` gains
`"storage": "heap" | "mapped" | "mixed"`; `repositoryType` stays `InMemory`.

`codecontext status` prints the same JSON. MCP `GetStatus` returns the same DTO.

Context responses (`/api/context/complete`, `/api/context/multi`, MCP `GetContext` and
`GetMultiContext`, compact and full) gain an optional `cache` object, present only while
`servingCached`:

```json
{ "view": "compact", "matchMode": "exact", "totalMatches": 1, "returnedMatches": 1,
  "cache": { "state": "verifying", "snapshotAt": "2026-11-04T10:12:03.0000000Z" },
  "matches": [ ... ] }
```

`query` treats an instance as answerable when `status` is `ready` or
`cache.servingCached` is true, so it does not wait for verification. `--json` prints the
response unchanged. Compact text prints one line first:

```
cache	verifying	2026-11-04T10:12:03Z	answers come from the saved index while files are re-checked
target	csharp:CodeContext.Core.Services.ContextService	Class	src/CodeContext.Core/Services/ContextService.cs:15
usedBy	3/3
```

A miss reads `cache	verifying	...` then `missing	Foo`: not conclusive until verified.
`--human` prints `Cached, verifying: answers come from the index saved at 10:12:03 and are
being checked against the files.` A coverage line built from cached coverage ends with
`Coverage is from the saved index.` `init --wait` and every client that polls for `ready`
behave as before: `ready` still means scanned and committed.

### 3.11 Failure modes

| Failure | Detected by | Result |
|---|---|---|
| Truncated or torn snapshot | length field, HMAC | ignored, next older or full scan |
| Snapshot from another user, machine or a crafted one | HMAC key | ignored |
| Host or format version changed | header, host meta | ignored |
| Repository moved or copied | root path | ignored; worker state rejected too |
| Key file missing or unreadable | load | cache disabled for this run, full scan |
| Cache path contains a link | attribute check | cache disabled, full scan |
| Writer bug: authenticated but inconsistent bytes | bounds checks | request error, shard dropped, full rescan |
| Mapped file truncated by another process | deny-write on Windows; not preventable on Unix | Windows: cannot happen; Unix: process fault (risk R4) |
| Worker state missing, wrong hash, id, version, root or fingerprints | worker | baseline rejected, full re-walk, facts replaced |
| Worker version changed | host compares at `initialize` | no baseline offered, full re-walk |
| Worker fails during verification | existing scan failure path | reconciliation rolled back, `status: error`, `cache.state: unverified`, cached answers stay labelled |
| Worker dies in `saveState` | request failure | shard captured without state |
| Parser removed since the snapshot | load | its shard is dropped |
| Every file of a language deleted while down | verification scan indexes every worker | scope replaced with nothing |
| Same size and mtime, different content | racy flag for recent files; otherwise undetected until the file is touched | accepted by brief decision 3; a refresh repairs it |
| Lone surrogate in a string, id without ownership prefix | writer | shard not persisted, logged, listed in status |
| Disk full, read-only root | write | no snapshot, error in status, serving unaffected |
| Two instances | lock | one writer; the other read-only |
| Watcher buffer overflow | existing handler | full rescan (existing); snapshot writes must not cause it (risk R3) |

## 4. Invariants checklist

| `AGENTS.md` invariant | How it holds |
|---|---|
| No language toolchain in the host | The host stores bytes. Fingerprints, state and file hashes are computed in the worker; the host treats them as opaque. No shared reader assembly. |
| Worker stdout is protocol only; exit on EOF; no ports, no registration | Unchanged. The state file is written only on a host request to a host-named path. |
| Ids start with `<parserId>:<workspaceId>:` and are stable | Unchanged. The writer refuses a shard that breaks the prefix rule. Restored facts are the worker's own earlier output. |
| Atomic, scoped generation commits; stale generations rejected; no worker overwrites another | A loaded snapshot is published as one map. Verification results arrive as ordinary deltas through `AnalysisDeltaApplier`. Shard swap is compare-and-swap under the commit lock. |
| `IndexCoordinator` is the sole ordered mutation path | Snapshot load, capture, hibernate and wake are coordinator steps or commands. The background writer only reads an immutable capture; its swap cannot change what a shard contains. |
| One file-selection policy | The verification scan enumerates with `RepositoryFileSelector` and passes the approved list; the worker verifies that list and nothing else. `.codecontext/` stays a mandatory exclusion. |
| Relationships are static evidence | Unchanged. |
| Canonical identifiers; count and truncation semantics | Heap and mapped answers are byte-identical by test. Two deliberate changes in phase 1 are listed there. |
| Nothing from a repository is executed | A snapshot is authenticated with a key outside the repository before use, parsed with bounds checks, and never contains code. Links under the cache path are refused. |
| Protocol contract moves together | Phase 5 changes schema, DTOs, `ParserProtocolJsonContext`, fake worker, manifests review, SDK constants and types, SDK guide and protocol tests in one phase. Version stays 1. |
| OpenAPI refreshed from the running endpoint | Phases 4 and 9 refresh `codecontext.openapi.json` from `/openapi/v1.json`. |
| Packaged skill matches byte for byte | Phases 4 and 9 edit `skill/` minimally; `verify-publish.ps1` keeps checking. |

## 5. Phases

Standard gates for every phase: `dotnet build` with no new warnings; `dotnet test`;
`dotnet test tests/CodeContext.Core.Tests/CodeContext.Core.Tests.csproj --filter
"Category=ExternalTooling"`; `git diff --check`. Protocol gates, where named:
`npm test --prefix sdk/npm` and
`PYTHONPATH=sdk/python/src python -m unittest discover -s sdk/python/tests`.

Rules for all phases: tests that start a real worker use `TestWorkerOptions.Patient()` and
no fixed short timeouts; commits carry no co-author trailer; a phase leaves `main`-quality
behavior, and where a phase is dark the option that hides it is named.

Regular expressions (added 2026-11-05; a follow-up after this release adds a build-time
guard): no `new Regex(...)` with a constant pattern without `[GeneratedRegex]`, and no
`RegexOptions.Compiled` in Core, Api, Mcp or Parser.Protocol, where it is a no-op under
Native AOT. `IgnorePattern.cs` and `CompileItemSet.cs` are not changed by this work. Each
implementer report states whether the phase added a `Regex` or touched either file, and
the end-of-work message repeats the total.

### Phase 0: baseline numbers and harness

Status: accepted 2026-11-05 with items carried into phase 11 (listed there). Reviewed by
one Opus and one Sonnet reviewer; 22 confirmed findings fixed by a fixer; the rewritten
script re-reviewed by a Sonnet reviewer, which found no blocker and eight should-fix
items. Those are carried rather than fixed now, because five of them concern paths that
can only be validated against a build that has the cache, and the harness is not run on
jellyfin or d2-roguelike again before then. Results in
`docs/perf/persistent-fact-cache.md`: v0.10.0 and v0.10.7 on all three repositories, 1
discarded and 5 timed runs per column. `scripts/bench/test_measure_cache.py`: 44 tests.

Review outcome. Both reviewers recomputed the recorded cells from the raw results and
every one matched, so the tables stand as a first reading. Neither found the harness safe
to reuse as it was. Confirmed against the script by the coordinator and sent to a fixer:
- Safety: `stop --path` also matches an ancestor instance, and the harness called it
  without checking that its own instance was still the one registered, so it could stop
  another session's instance (for this repository's bench worktree the ancestor is the
  main checkout); a hard kill could leave an edit that the next run would take for the
  original and keep; file writes were not atomic; an unparseable `list --json` read as
  "no instances".
- Numbers: the "warm edit" medians include the first edit in that project (round 0 was
  0.99 s against 0.81 s for later rounds on this repository); edit detection polled every
  52 ms, so figures move in 52 ms steps; memory after queries and after edits was sampled
  with no settle; the root first-edit ran with no settle while the leaf one had 5 s; query
  latency included client-side JSON parsing; two of the three enumeration columns are the
  polling floor on v0.10.7, and only the OS process-creation row measures enumeration.
- Paths that cannot run before the feature exists (snapshot, hibernation, wake, cached
  cold starts) could record "not applicable" or drop a figure silently, and stopped the
  instance without waiting for a snapshot of the unchanged graph.

Decision (coordinator, 2026-11-05): the comparison in phase 11 is taken by running v0.10.7
and the branch build back to back, interleaved, with the fixed harness, on the same day
and the same commits of the three repositories. Reasons: the fixes change how warm edits,
memory and latency are taken, so the phase 0 figures and later figures would differ for
harness reasons; d2-roguelike is edited by other sessions and will have moved by then;
and re-taking v0.10.7 costs under an hour. The phase 0 tables are kept, with their
limitations stated in the document, as the reading that sized this work.

Goal: before-numbers on 0.10.0, taken with the harness the last phase reuses.

Scope: add `scripts/bench/measure-cache.py` (from `%TEMP%\ccbench\measure.py`, same commit
detection: watcher updates do not advance `operationId`, so a commit is a change in node or
edge counts) and `scripts/bench/queries-<repo>.txt` for jellyfin, d2-roguelike and this
repository. Record results in `docs/perf/persistent-fact-cache.md`.

Out of scope: any product code.

Details: section 6 defines each number. The 0.10.0 build is the published release asset
(`gh release download v0.10.0`, win-x64), unpacked outside the repository; it is the
release recipe by construction. Rows that need the cache or hibernation are recorded as
"not applicable in 0.10.0" with the nearest comparable figure (cold start to `ready`).

Host file enumeration is its own column: the time from host start until the C# worker
process exists, read from `parsers.sessions` while polling. Cold start is recorded on
0.10.0 now and again after `main`'s enumeration patch is merged into this branch, so the
0.11 comparison isolates what the cache bought from what the patch bought.

Rules for running it, which also bind phase 11:
- Never `codecontext stop --all`. Stop only instances this harness started, by path. The
  script refuses to start when `codecontext list --json` already shows an instance on the
  target root, because that instance belongs to someone else.
- This repository is measured on a dedicated worktree at the 0.10.0 commit under
  `.claude/worktrees/`, never on the main checkout. jellyfin and d2-roguelike are measured
  in place; every timed edit is reverted, and the script checks `git status --porcelain`
  for the edited files before and after and fails loudly on a difference.
- No other build, test run or agent may be active on the machine during a timed run.

Tests: none. Acceptance: the document holds the 0.10.0 table for the three repositories,
with medians and ranges of at least 3 runs, and the script runs unattended.

Gates: standard.

### Phase 0A: ignore-rule fast path

Status: dropped 2026-11-05, before any work. Built elsewhere as a 0.10.x patch (D25); this
branch merges `main` when it lands and re-records the cold-start baseline then. The text
below is kept only so the phase can be reinstated if that patch does not arrive.

Goal: file enumeration through `RepositoryFileSelector` on jellyfin (2,251 approved files,
161 ignore rules) takes under 0.5 s instead of 11.6 s, with selection results unchanged.
Independent of every other phase.

Scope: `src/CodeContext.Core/Services/RepositoryFileSelector.cs` and, if the rule model is
split out, a new file beside it; tests in `tests/CodeContext.Core.Tests`.

Out of scope: any change to which files are selected; mandatory exclusions; the watcher.

Details: today each `.gitignore` rule becomes a regex, `RegexOptions.Compiled` is a no-op
under Native AOT, and an unanchored rule starts with `(?:^|.*/)`, so every rule is
interpreted against every path. Classify each rule once when it is read:
- no slash other than a trailing one, no wildcard: compare the last path segment (or any
  segment, for a directory rule) by set lookup;
- `*.ext` with no other wildcard and no slash: suffix test on the last segment;
- other rules with no slash except a trailing one: match the pattern against single
  segments only, never the whole path;
- everything else (a slash in the middle, `**`, character classes, escapes): the existing
  regex, unchanged.
Order, negation (`!`), directory-only rules and nested `.gitignore` scoping keep today's
semantics exactly: the fast path changes how one rule is tested, not which rule wins. A
directory that is ignored and cannot be re-included is not descended into, if that is
already today's behavior; do not change traversal rules in this phase.

Tests: `IgnoreRuleFastPathTests`: a differential test that runs the old regex matcher
(kept as a test-only reference) and the new matcher over at least 20,000 generated
(rule set, path) pairs from a seeded generator covering every rule class, negation order,
directory rules, nested ignore files, case, and paths with dots, spaces and non-ASCII
segments, and requires identical results; one named test per rule class;
`StandardVisualStudioGitignore_SelectsTheSameFilesAsBefore` over a checked-in copy of that
template and a generated tree; an `ExternalTooling` test that compares against
`git check-ignore` on a temp repository when git is available.

Acceptance: differential test green; enumeration of jellyfin measured before and after
with the phase 0 harness or a stopwatch around the selector, reported; cold start to
`ready` on jellyfin reported against phase 0.

Gates: standard.

### Phase 1: shard interface and canonical read order

Status: accepted 2026-11-05. Reviewed by one Opus and one Sonnet reviewer; no blocker;
the confirmed findings fixed by a fixer; gates re-run by the coordinator.

Review outcome.
- Commit rules: unchanged apart from the two named fixes. The Sonnet reviewer ran the
  pre-change store against the reference model over 400 seeds x 200 operations and found
  divergence only in the identifier-shadowing corner the fix addresses; the new store
  matched over 300 further seeds with a harsher generator. The Opus reviewer's mutation
  checks (alias bug restored, sorting disabled) fail the new tests.
- Fixed: (1) a legacy in-place mutation on a non-heap shard was lost when a
  reconciliation was staged, because only the live map received the heap copy (latent
  until phase 8; now the staged map receives it too when it holds the same shard);
  (2) `RemoveEdgesTouchingNode(null)` threw on a non-heap shard where a heap shard removes
  edges with a null endpoint; (3) the read matrix now compares the raw identifier map,
  probes every identifier ever issued and asks by-source and by-target for every edge
  endpoint; (4) the generator now also produces blank and `external:` paths, null ids,
  ownership metadata that differs from the scope, null and empty `ReplacesFiles`
  entries, case-variant prune keep-sets, `ReplaceAll`, `Reset`, the four legacy mutations
  and a mid-scenario `TryReplaceShard` swap; (5) both release workflows passed an
  explicit test filter that replaced the project default, so they would have run the
  benchmark; both now exclude it.
- Phase 8 reuses `StoreScenarioRunner.RunAsync(seed, operations, equivalentShard)`: the
  same scenarios run with every live shard swapped for a non-heap equivalent
  (`NonHeapShardStore_MatchesReferenceModel`, today over a delegating test shard).
- Not generated, covered by named tests instead: legacy mutations while a reconciliation
  is staged (whether the write survives depends on whether the staged map still shares
  the shard object, which is today's behaviour and is kept; see section 9).
- Dismissed: line endings of `GraphAdjacency.cs` (the index copy is LF); the order of
  status `languageBreakdown` (file-metadata repository, not the graph store; section 9).
- Fix round re-reviewed by a Sonnet reviewer after the commit (2ccda59): no blocker and
  no correctness bug in the bucket index, the reconciliation fix or the naming
  heuristic. One should-fix and two nits, all about how strongly the non-heap scenario
  tests a non-heap shard; carried into phase 8, where that harness is first used with a
  real mapped shard (listed there). Decision: not fixed now, because the product paths
  they concern have no non-heap shard until phase 8, the named theory
  `LegacyMutation_OverANonHeapShard_ReadsAsOverAHeapShard` already covers the fix, and
  phase 2 is being built in the same tree.

Edit-path cost, measured by the coordinator on the generated shard (26,000 nodes, 298,792
edges), Release, alternating runs, base against this phase. As first delivered: commit
plus one keyed query 507 / 498 / 574 / 538 ms base against 553 / 573 / 581 / 572 ms,
about 8 percent slower and slower in every round. Cause: one wrapper object per bucket
(about 53,000), a garbage-collection cost rather than sorting. Decision: fix it now
rather than carry an 8 percent store regression to phase 11, since the cause was
contained. The buckets became slots of one array with one array of sorted flags. After
the fix, five alternating rounds by the fixer: 578.5 / 553.9, 570.6 / 554.9,
548.4 / 568.8, 538.7 / 509.7, 527.5 / 510.1 ms (base / phase 1): four of five rounds
faster, the other 3.7 percent slower. Commit plus one compact context query (depth 2, tests):
phase 1 faster in all five rounds (599 to 668 ms base, 582 to 606 ms phase 1), because
the naming heuristic reads the cached file-path list instead of every node. Neither
figure moved by more than 10 percent, so the phase 0 harness was not run; phase 11
checks end to end.

Goal: `InMemoryDatabase` works against `IGraphShard`; reads are canonical; no feature yet.

Scope: `src/CodeContext.Core/Repositories/InMemory/IGraphShard.cs` (new);
`InMemoryDatabase.cs` (shard map typed to the interface; `BuildScopedState`,
`RebuildForeignShard`, `PruneShard` moved behind the commit members; identifier-alias fix;
no-op empty scoped commit; `PublishLoadedShards` and `TryReplaceShard` added, unused);
`GraphAdjacency.cs` and `CompositeAdjacency` (sorted buckets, merged results);
`InMemoryNodeRepository.cs`, `InMemoryEdgeRepository.cs`; `ICodeNodeRepository.cs` (add
`GetFilePathsAsync`); `ContextService.cs` (`FindTestFilesAsync`).

Out of scope: mapped shards, files on disk.

Details:
- Canonical order as D4. `GraphState.GetOrBuildAdjacency` sorts once per version;
  `FindByNameAsync` sorts its result.
- `FindTestFilesAsync` becomes: distinct node file paths containing `Test` or `Spec`,
  ordered `OrdinalIgnoreCase` then `Ordinal`; the first 10 for which
  `MightContainTestsForTargetAsync` holds. Today it takes 10 nodes in dictionary order,
  which differs from run to run. This changes which files get `namingHeuristic` evidence.
- Tests that assert dictionary enumeration order (`GraphAdjacencyTests`, repository tests)
  are changed to assert canonical order, each change noted in the report. Golden outputs
  and any `skill/` text that shows or implies a result order are updated in this phase.
- Add a `ReferenceGraphModel` test helper: a single-dictionary implementation of the commit
  rules in 3.4, and a seeded `StoreScenarioGenerator` (scoped and whole-shard commits,
  shadowing ids, dangling and cross-shard edges, duplicate identifiers, prune,
  reconciliation begin, commit and rollback).

Tests: `StoreScenarioTests.HeapStore_MatchesReferenceModel(seed)` over at least 8 seeds and
200 operations, comparing every read path and every thrown exception;
`ScopedCommit_ShadowingDelta_DropsTheOldIdentifier`;
`EmptyScopedCommit_KeepsTheShardReference`;
`ReadsAreInOrdinalIdOrder_AcrossShards`;
`CompactResponses_AreIndependentOfCommitOrderAndCapacity` (same graph built two ways, a
query matrix including `--tests`, JSON equal);
`NamingHeuristic_TakesTheFirstTenFilesByPath`.

Acceptance: all existing store and context tests pass, with only the ordering assertions
changed; the edit-walk convergence tests pass unchanged. Sorting must not move cost onto
edits: the report gives, for a generated shard of 26k nodes and 296k edges, before and
after, the adjacency build time and the time of one single-file scoped commit followed
by one query, and if either grows by more than 25 percent, buckets are sorted on first
access instead of at build. End-to-end warm edit latency needs a release-recipe build
(Native AOT host), which is too slow to produce per phase; it is checked by the
coordinator with the phase 0 harness only if those two figures move by more than 10
percent, and in any case in phase 11 against v0.10.7 re-taken back to back (phase 0
read: jellyfin body 1.09 s, top-level 2.10 s; d2-roguelike 0.67 s, 0.93 s; this
repository 0.84 s, 1.25 s, each a little high because round 0 was included).

Gates: standard.

### Phase 2: snapshot format, authenticated reader, materializing loader

Status: accepted 2026-11-05. Reviewed by two Opus reviewers and one Sonnet reviewer; no
blocker; the confirmed findings fixed by a fixer; gates re-run by the coordinator. The
format as built is listed at the end of section 3.2.

Review outcome.
- Fidelity (Opus): a hand-built shard with every null, empty, non-BMP, case-fold and
  numeric edge case, and scenario stores from the phase 1 generator, read back equal to
  the heap store through the loader, through the in-place accessors and as a whole store.
  Layout decoded independently of the reader and matched 3.2.
- Hostile files (Opus): a structured fuzzer over 32,000 re-signed damaged files produced
  no exception other than `SnapshotCorruptException`, no hang and no leaked handle. A
  version of it runs in the suite (3 seeds x 220 files, 3.5 s).
- Tests (Sonnet): the fixture matches 3.2 byte for byte; five of eight deliberate bugs
  went unnoticed by the first version's 114 tests. After the fix round all eight fail at
  least one test.
- Fixed: a 1 GiB length cap before hashing (a sparse 4 GiB planted file cost 2.6 s per
  start); Unix mode and ownership checks on the file and the key (rule 19 and 23); the
  loader applies the writer's persistability rule (re-signed damage could load a null
  edge source or an id without the shard prefix); index ranges are validated at open (a
  damaged name table made one query take 4.8 s and return 1.2 million duplicates from a
  330 KB file); metadata with a non-ordinal comparer makes a shard not persistable; null
  arguments answer as the heap shard; `Open` releases everything and returns a rejection
  on any exception; partial files are deleted when a write fails.
- Decisions taken in triage:
  - A shard that is not persisted leaves cross-shard edges into it that its worker's later
    commit does not sweep, where the live store would have swept them. Accepted, not
    fixed: the result equals a fresh scan (the other worker emits that edge whatever this
    shard holds, and a fresh store that commits the two in turn keeps it); the live
    store's sweep depends on history. Pinned by
    `SkippedShard_LoadedStoreAfterItsWorkerCommits_ReadsAsAFreshScan_NotAsTheLiveStore`.
    In production only a lone surrogate or the `Default` shard causes a skip.
  - The length cap is 1 GiB (45 times the jellyfin file). Phase 4 additionally tries at
    most three candidate files at start, so planted files cost at most about 2 s.
  - Read leases are not built here; the concurrent-dispose crash is carried to phase 8.
  - No globalization field in the file; no cap on metadata pairs (see phase 8 carried
    items for the cost reasoning).
- Not run: anything on Linux or macOS. The suite runs on Windows locally and in CI; the
  only Linux SDK at hand (WSL) is .NET 9. The Unix-only paths (0600 creation, the mode and
  ownership checks, the key mode check) are built and have tests that return early on
  Windows. No test covers a file owned by another user or a file shrinking between the
  length check and the mapping.
- Test seams in production code, internal: `SnapshotFile.OpenInMemory`, the
  `maxFileLength` overloads, a `wrapStream` parameter on `WriteFile`.
- Fix round re-reviewed by an Opus reviewer after the commit (922a2e8): no blocker. The
  stricter reader refused none of 3,400 writer-produced graphs built to provoke it (case
  and fold collisions, UTF-8 against UTF-16 order, colliding normalized paths, shards of
  only edges or only nodes, power-of-two counts), through the in-place accessors, the
  loader and, for 340 of them, real files. One should-fix and three nits are fixed in
  phase 3's change set: the Unix ownership test reported every failure of the mode
  change as "not owned" (reproduced on a read-only mount); it now runs only when the
  mode is not already 0600 and reports an I/O failure as one. Recorded, not fixed:
  - On a file system that does not keep Unix modes (WSL DrvFs without `metadata`, FAT)
    every file reads back group and other writable, so the key and every snapshot are
    refused and the cache never runs there. It fails closed and says why.
  - Root passes the ownership test for any file. A file someone else wrote still needs
    root's key to authenticate.
  - Open allocates one reference per string of a shard (1.8 MB on the jellyfin-sized
    file). Phase 8 keeps or replaces that with its string cache.
  - Not verified: on a Unix file system without hard links, two first-time key creators
    might both believe they won; the loser's first snapshots would fail authentication
    and be rewritten after one rescan.

Goal: write a shard map to a file and load it back into heap shards. Dark: nothing calls it.

Scope: new `src/CodeContext.Core/Snapshots/`: `SnapshotFormat.cs` (constants, record
layouts), `SnapshotWriter.cs`, `SnapshotFile.cs` (open, validate, map, `MappedRegion`),
`SnapshotShardReader.cs` (the in-place accessors, used by the loader now and by the mapped
shard in phase 8), `SnapshotLoader.cs`, `SnapshotKey.cs` (key file), `Fnv1a.cs`,
`SnapshotCorruptException.cs`. `CodeContext.Core.csproj`: `AllowUnsafeBlocks`.

Out of scope: lifecycle, wiring, mapped shards.

Details: format exactly as 3.2. The writer consumes shards through `IGraphShard`. The key
file is created with user-only permissions on first use. A big-endian process disables the
feature. The loader returns `GraphState` shards equal to the input.

Tests:
- `SnapshotRoundTripTests`: random graphs (null and empty fields, non-ASCII, metadata sets,
  canonical and non-canonical `line`/`column`, dangling targets, all three id shapes of the
  bundled and fake workers, verbatim ids) reproduce every field of every node and edge and
  every read path of phase 1's matrix.
- `SnapshotGoldenTests.TinyGraph_MatchesCheckedInBytes` with a fixed key and time, pinning
  the layout.
- `SnapshotCorruptionTests`: every truncation length of a small file and sampled lengths of
  a large one are rejected; every single-byte flip is rejected; wrong key, format version,
  endian tag, host version, root path, length field, each rejected with its reason.
- `SnapshotBoundsTests`: files re-signed after structural damage (section offsets and
  lengths, counts, string references, node and edge indexes, offsets arrays) and seeded
  random damage; every accessor either answers or throws `SnapshotCorruptException`.
- `SnapshotPersistabilityTests`: lone surrogate, id without prefix, `Default` shard: shard
  skipped, others written.
- `FoldEquivalenceTests`: the superset property of 3.3 over random Unicode.

Acceptance: a generated graph of 26k nodes and 296k edges writes and loads; the report
states file size, write time and load time; no test leaves a mapped file behind (temp
directories delete cleanly on Windows).

Gates: standard.

### Phase 3: snapshot lifecycle, write side

Status: not started

Goal: the host writes snapshots after commits, safely, without anything reading them yet.
Dark: `CodeContextOptions.CacheEnabled` defaults to false in this phase.

Scope: `Snapshots/SnapshotService.cs` (hosted service: dirty tracking, timers on
`TimeProvider`, capture, background write, retire, cleanup, lock), `Snapshots/CacheDirectory.cs`
(layout, names, link check, `.gitignore`), `Services/IndexCoordinator.cs`
(`SnapshotCommand`; maintenance commands excluded from `IsBusy`),
`Workers/AnalysisDeltaApplier.cs` and a new `Workers/ShardProvenanceRegistry.cs` (parser
version per shard at commit), `FileWatcherService.cs` (ignore files below a mandatory
exclusion are not control events), `CodeContextOptions.cs`, `src/CodeContext.Api/ProgramHelpers.cs`
(registration after the coordinator, before the watcher, so it stops before both the
coordinator and the worker service).

Out of scope: loading, status fields, worker state.

Details: section 3.5. Hosted-service stop order is the reverse of registration; the final
capture needs the coordinator loop and the workers alive.

Carried from phase 2:
- `SnapshotWriter.WriteFile` uses `CreateNew`, deletes its own partial file on failure and
  never touches a path that existed; the temp name, rename and retire are this phase's.
- The writer builds every blob in memory before writing anything (about 448 MiB
  allocated for the jellyfin-sized file, peak about 191 MiB above baseline). It must run
  on the background task, never under the coordinator.
- A legacy in-place write during serialization is not detected yet; the
  `GraphState.Version` check of 3.5 is built here.
- A shard key whose facts were all removed is skipped by the writer; an empty key is not
  a change for dirty tracking.
- `SnapshotWriteResult` reports skipped shards with reasons and `NotWritten` when the
  capture exceeds 1 GiB; both are logged, and phase 4 surfaces the latter as
  `lastWriteError`.
- `SnapshotKey.LoadOrCreate` creates a missing `~/.codecontext` itself (0700 on Unix) and
  throws for a key file with group or other access on Unix: the service then runs without
  a cache and says why.
- On Unix the cache directory is created 0700 and snapshot files 0600; the reader's
  ownership check (3.2, rule 19) relies on the file, not the directory.

Tests (`SnapshotServiceTests`, fake time): `Commits_AreCoalescedIntoOneWrite`;
`MaxDelay_ForcesAWriteUnderContinuousCommits`; `OnlyOneWriteRunsAtATime`;
`CleanStore_WritesNothing`; `CrashBeforeRename_LeavesThePreviousSnapshotValid` (fault
injected between flush and rename); `Startup_DeletesTempFilesAndOlderSnapshots`;
`SecondInstance_IsReadOnly`; `BusyFile_IsRetiredLater`; `Shutdown_FlushesADirtyStore`;
`LinkedCacheDirectory_IsRefused`; `WriteFailure_IsRetriedAfterTheNextCommit`;
`CacheGitignore_DoesNotTriggerARescan`;
`SnapshotWrite_RaisesNoWatcherError` (a real `FileSystemWatcher` on the root while a 64 MB
snapshot is written); `MaintenanceCommands_DoNotExtendTheIdleLease`.

Acceptance: with the option on, this repository's host writes a snapshot after its scan
and after an edit, and `git status` shows nothing new; with it off, no file is created.

Gates: standard.

### Phase 4: load, then serve; the cache surface; feature on

Status: not started

Goal: a start with a valid snapshot answers at once, labelled, while every worker re-walks
as today. `CacheEnabled` defaults to true from here.

Scope: `IndexCoordinator.cs` (load step; verification scan), `Services/GraphUpdateService.cs`
and `IGraphUpdateService.cs` (verification scan: full-scan flow), `Workers/LanguageWorkerService.cs`
(a loaded shard counts as indexed for the "nothing to index" shortcut),
`Services/CacheState.cs` (new `ICacheStateProvider`), `StatusService.cs`,
`Serialization/CodeContextJsonContext.cs` (DTOs), `Services/IContextService.cs` and
`ContextService.cs` (`cache` notice, cached coverage), `Workers/AnalysisCoverageRegistry.cs`
(restore), `src/CodeContext.Api/Commands/ReadinessWaiter.cs` (`acceptCached`),
`QueryCommandHandler.cs`, `InitCommandHandler.cs`, `Program.cs`, `StartCommandHandler.cs`,
`DetachedStartOrchestrator.cs` (`--no-cache`), `codecontext.openapi.json` (refreshed),
`AGENTS.md` (replace "The store is intentionally in-memory: every process start performs a
fresh scan" with the load-then-verify description; add `Snapshots/` to the architecture
list), `skill/SKILL.md` and `skill/references/operations.md` (the `cache` line and what an
unverified miss means, in as few words as possible), `DEVELOPMENT.md` (cache location, how
to clear it, and that `.codecontext/` must stay out of version control: a snapshot from
another user or machine is refused, never trusted, so committing it only costs space).

Out of scope: baselines, worker state, mapped shards.

Details: sections 3.5 (startup), 3.10 and the third sequence of 3.6. Dev builds change
their informational version on every commit, so a rebuilt host ignores older snapshots by
design.

Carried from phase 2:
- `PublishLoadedShards` does not set the store generation, so a loaded store reports
  generation 0 while the header carries `storeGeneration`. This phase decides how the
  generation resumes, so that a stale delta from before the restart is still rejected
  and the verification scan's generations are accepted.
- At most three candidate files are opened at start, newest first; each refusal is
  reported with its reason (`SnapshotRejection`), the rest are ignored and deleted by
  cleanup. With the 1 GiB cap that bounds the cost of planted files at about 2 s.
- The loader takes an `acceptShard` filter (registered parser, host workspace id) and
  reports dropped shards; a dropped or skipped shard is absent from the published map and
  its worker's first commit starts from nothing (the accepted cross-shard edge difference
  is in the phase 2 review outcome).
- A loaded fact's metadata dictionary is ordinal and enumerates in key order.

Tests: `StartupFromSnapshotTests` with the fake worker: `AnswersFromTheSnapshotBeforeTheWorkerFinishes`
(slow-index behavior), `LabelGoesWhenTheScanCommits`, `WorkerFailure_LeavesCachedAnswersLabelledUnverified`,
`ShardOfARemovedParser_IsDropped`, `LanguageWithNoFilesLeft_IsEmptied`,
`IgnoredSnapshot_ReportsItsReason`; `ReadinessWaiterTests` for both `acceptCached` values;
`QueryCommandHandlerTests` for the cache line in compact, human and JSON output, on a hit
and on a miss; `StatusServiceScanStateTests` for `indexing.cache` and `coverage.cached`;
`CodeContextToolsTests` for `GetStatus` and `GetContext`; `CodeContextEndpointTests`.
Convergence: `SnapshotReloadConvergenceTests.EveryStepMatchesAFreshScan(seed)`: the existing
edit walk, with the host store thrown away and reloaded from a snapshot before a fresh real
worker at every 8th step, compared with a fresh scan at every step.

Acceptance: second start on this repository answers `query ContextService` with a `cache`
line before `ready`, then without it; `--no-cache` restores 0.10.0 behavior exactly;
`verify-publish.ps1` passes.

Gates: standard.

### Phase 5: protocol, persistent state capability

Status: not started

Goal: host and contract support baselines, fingerprints and `workspace/saveState`. No
bundled worker declares the capability yet.

Scope: `src/CodeContext.Parser.Protocol/protocol/parser-protocol.schema.json`,
`Contracts.cs` (`WorkerCapabilities.PersistentState`, `IndexBaseline`, `UnitFingerprint`,
`SaveStateParams`, `SaveStateResult`, new optional members), `ParserProtocol.cs`
(`SaveState` method name), `ParserProtocolJsonContext.cs`, `ParserWorkerClient.cs`;
`Workers/ParserProcessSupervisor.cs` (`SaveStateAsync`), `LanguageWorkerService.cs`
(baseline offer rule, `SaveStateAsync`), `AnalysisDeltaApplier.cs` and
`ShardProvenanceRegistry.cs` (fingerprints), `Snapshots/SnapshotService.cs` (capture step
1, state file retire), `Snapshots/SnapshotWriter.cs` and `SnapshotLoader.cs` (shard meta);
`tests/CodeContext.FakeWorker/Program.cs`; `sdk/npm/index.js`, `index.d.ts`,
`sdk/python/src/codecontext_worker_sdk/__init__.py` and their tests;
`docs/language-worker-sdk.md` (new section: persisting state across restarts);
`src/CodeContext.Parser.Protocol/README.md`. Both worker manifests are reviewed and need no
change: the capability is declared at `initialize`.

Out of scope: the C# worker's implementation.

Details: section 3.6. Fake worker behaviors: `persistent-state` (accepts a baseline whose
state file it wrote, answers with an empty file-scoped delta and fingerprints),
`baseline-reject`, `save-state-fails`, and `strict` (fails the request if it receives
`baseline` or `workspace/saveState` without having declared the capability).

Tests: `ParserProtocolContractTests`: every new field round-trips, is omitted when null, and
an `initialize` result without `persistentState` reads as false; schema and DTO field names
agree for the new definitions. `ParserProcessSupervisorTests`:
`Baseline_IsSentOnlyToACapableWorker`, `AcceptedBaseline_CommitsAFileScopedDelta`,
`RejectedBaseline_ReplacesTheWorkspace`, `SaveState_RequiresTheCapability`.
`LanguageWorkerServiceTests`: `Baseline_IsNotOfferedWhenTheParserVersionChanged`,
`..._WhenTheShardChangedSinceCapture`, `..._OnASecondIndexOfTheSameProcess`.
`AnalysisDeltaApplierTests`: `Fingerprints_AreStoredOnCommitAndClearedWhenAbsent`.
`SnapshotServiceTests`: `Capture_SavesWorkerStateBeforeLatchingTheStore`,
`FailedSave_WritesTheShardWithoutState`, `StateFiles_AreRetiredWithTheirSnapshot`.
`TypeScriptWorkerProtocolTests` pass unchanged. SDK tests cover the new constant.

Acceptance: with the fake worker, a restart commits an empty delta instead of a
replacement; the TypeScript and C# workers behave exactly as in phase 4.

Gates: standard and protocol.

### Phase 6: C# worker, state and verification

Status: not started

Goal: the C# worker saves state, restores it, and verifies a baseline without binding
unchanged projects. Compilations and trees are still built for every project.

Scope: `src/CodeContext.CSharp.Worker/Program.cs` (capability, `baseline` handling,
`workspace/saveState`, publish logic moved into a testable `WorkerSession` class),
`CSharpWorkspaceAnalyzer.cs` (file table, hashes from raw bytes, fingerprints, state
capture and restore, first-settlement rule, coverage from the directives flag; fix the
CS8604 warning at line 337 while in `Rebuild`), new `State/WorkerStateFile.cs` and
`State/UnitFingerprint.cs`, `Projects/ExternalSymbols.cs` (rebuild from stored nodes and
edges). Tests under `tests/CodeContext.Core.Tests/Workers/`, with the walk generator of
`CSharpWorkerEditWalkConvergenceTests` extracted to `EditWalk.cs` and extended.

Out of scope: dormant units, deferred open (phase 7); narrowing (phase 10).

Details: sections 3.7 and 3.8. On an accepted baseline this phase still runs today's
`Rebuild` (model, compilations, parse: about 3 s on jellyfin) and skips only the walk of
unmoved units. The worker emits one `info` diagnostic per baseline request,
`verify: restored=<n> walked=<n> parsed=<n> compilations=<n>`, which tests and the harness
read. `EditWalk` gains: add, remove and rename a file; toggle a constant in a csproj; add
and remove a project reference; edit `Directory.Build.props`; touch without change; and
"down" steps, where several edits reach the disk with no events before the next restart.

Tests:
- `CSharpWorkerStateRoundTripTests`: save, restore into a new analyzer, then a full
  settlement yields buckets and hashes equal to the original's.
- `CSharpWorkerFingerprintTests`: each input moves exactly the expected units: file
  content, file added, file removed, constant, language version, metadata reference mtime,
  project reference, owner change of a shared file, a dependency three levels down,
  `Directory.Build.props`, worker version.
- `CSharpWorkerBaselineTests`: `Unchanged_SendsOneEmptyDeltaAndWalksNothing`;
  `ChangedLeaf_WalksItsUnitAndDependentsOnly`; `TouchedFile_IsHashedNotWalked`;
  `RacyEntry_IsAlwaysRehashed`; rejection for a missing file, wrong hash, wrong id,
  fingerprint mismatch, version, root; `RejectedBaseline_EqualsAFullIndex`.
- Stub, partial and identifier cases after a restore:
  `StubLeavesWhenItsLastUserGoes_AfterRestore`,
  `PartialOwnershipMovesToTheNextFile_AfterRestore`,
  `CollidingIdentifiersQualifyAndUnqualifyTogether_AfterRestore`,
  `CoverageListsProjectsThatBoundNothing`,
  `CoverageAfterRestore_EqualsCoverageOfAFreshScan` (every field of every entry, including
  a project that is `partial` only because the evaluator assumed an unreadable MSBuild
  condition true, which `main` 2407cf1 introduced). Coverage state is derived from the
  project model and the walk; it is never an input to a fingerprint.
- `CSharpWorkerProtocolFixtureTests`: baseline index and `workspace/saveState` through the
  real process.
- Convergence, tier 1, in process: `RestoreConvergenceTests.EveryStepMatchesAFreshScan(seed)`,
  4 seeds, 120 steps, state saved and a new analyzer restored between all steps, down steps
  included, the graph compared with a fresh analyzer at every step.
- Convergence, tier 2, real processes: `SnapshotRestartConvergenceTests`, host snapshot and
  worker state written, a fresh host store and a fresh worker process started and verified
  between all steps for 2 seeds of 25 steps, and at every 8th step for 2 seeds of 120
  steps; compared with a fresh scan at every step.

Acceptance: both tiers green; injecting a fault into each restored item of the table in
3.7 (drop a non-owner node copy, drop stub use of one file, drop one fact hash) fails a
named test; an unchanged baseline reports `walked=0`; warm edit latency within noise.

Gates: standard and protocol.

### Phase 7: C# worker, dormant units

Status: not started

Goal: an unchanged verification parses nothing and builds no compilation; a first scan
builds once.

Scope: `Projects/ProjectCompilation.cs` (dormant state, `Materialize`),
`CSharpWorkspaceAnalyzer.cs` (`SyncApprovedFiles` deferred, `Rebuild`, `LoadFile`,
`DropFile`, `FilesToWalk`, `AnalyzeAsync` materialization fixpoint, syntax tree for a file
of a dormant unit, lazy load of stored facts), `Program.cs`.

Out of scope: classifying an edit without the old tree (phase 10).

Details: section 3.7, dormant units and first edit. `workspace/open` only records its
arguments. Materialization may parse files in parallel and creates the compilation once.

Tests: `Unchanged_ParsesNothingAndCreatesNoCompilation` (the diagnostic's counters);
`FirstScan_ParsesEachFileOnce`; `FirstEdit_MaterializesOnlyTheClosureOfItsUnit`;
`TouchOnADormantUnit_DoesNothing`;
`MaterializingAUnitWithAStaleFile_RewalksItAndItsDependents` (a file changed on disk with
its event still pending); `TopLevelEditInALiveUnit_MaterializesDormantDependents`;
`SyntaxTree_OfADormantUnit_ParsesOneFile`; `CSharpWorkspaceAnalyzerReuseTests` updated.
All phase 6 convergence tiers run again unchanged.

Acceptance: tiers green; jellyfin first scan loses the second build (report the figure);
unchanged verification on jellyfin within 1.2-2.2 s from host start to `ready`, or the
report explains the difference; cold-binding and bucket-hash costs measured and recorded.

Gates: standard and protocol.

### Phase 8: mapped shard, overlay, compaction

Status: not started

Goal: loaded and compacted shards are served in place; host memory becomes file-backed.

Scope: new `Repositories/InMemory/MappedShard.cs`, `MappedOverlay.cs`;
`Snapshots/SnapshotShardReader.cs` (lease per read, low-cardinality string cache),
`SnapshotLoader.cs` (returns mapped shards), `SnapshotService.cs` (swap after write, retire
after unmap, copy-through of unchanged blobs), `InMemoryDatabase.cs` (`TryReplaceShard`
used; corrupt-shard handling), `StatusService.cs` (`database.storage`).

Out of scope: format changes. If one is needed, bump `formatVersion` and say so.

Details: sections 3.3 and 3.4. A request that hits `SnapshotCorruptException` returns an
error, and the store drops the shard and asks the coordinator for a full rescan.

Carried from the phase 1 fix-round re-review (2026-11-05); first step of this phase,
before `MappedShard` is written:
- The non-heap scenario runs mostly on heap shards. A scoped commit to a key with no
  shard, `ReplaceAll`, an unscoped commit and every legacy mutation hand back a heap
  shard, and a shard becomes non-heap again only through a swap (about 3 percent of
  operations). Measured over 8 seeds x 200 operations: of 1,167 commits, 366 ran with
  some shard non-heap and 31 with every shard non-heap; 12 legacy mutations replaced a
  non-heap shard. The runner must re-install the equivalent shard for every live shard
  after each operation when no reconciliation is staged (this is also what compaction
  does), and `Generator_CoversEveryOutcome` must count an operation as non-heap only
  when the shard it addresses is non-heap before it runs.
- `AsHeapShard` swaps the map without `NotifyMutation` when the legacy mutation then
  changes nothing (`RemoveEdgesTouchingNode(null)` with no match), so the cached
  composite adjacency and statistics keep the replaced shard. Harmless over a
  content-equal heap copy; over a mapped shard it would keep a retired mapping
  reachable. `AsHeapShard` must notify, with a test that no cache holds the replaced
  shard afterwards.
- The reference model routes facts with the product's `ShardKey.ForNode`, `ForEdge` and
  `From`. State the routing rule in the model itself (path matching through
  `FilePathMatcher.Matches` stays: the adjacency claims set-equivalence to it).

Carried from the phase 2 reviews (2026-11-05):
- A read concurrent with `MappedRegion.Dispose` crashes the process. Reproduced by a
  reviewer: four threads in `GetAllEdges` and `FindNodesByName` while the file is
  disposed end in a fatal `AccessViolationException` inside the UTF-8 decode, which
  cannot be caught. Phase 2 is unaffected (the loader is single-threaded and disposes
  after materializing). The lease per read in this phase's scope is therefore the first
  thing built after the harness step: every accessor takes a lease, `Dispose` marks the
  region and unmaps only when the last lease ends, and that reproduction (300 rounds,
  dispose after a varying spin) is a required test.
- The loader refuses a shard that fails the writer's persistability rule after
  materializing it. A mapped shard is not materialized, so the same guarantees must hold
  per record at read time: a null edge source, an id without the shard's prefix or a null
  identifier met in place is `SnapshotCorruptException`, not an answer.
- Proportional cost on an authentic but wrong file is not bounded everywhere, by
  decision: a metadata set shared by many facts is expanded per fact, and a by-target run
  may list an edge twice. Both need a writer bug; bounds, termination and the absence of
  crashes are guaranteed, cost is not. Checking by-target runs for duplicates at open
  would touch every edge, which this phase's open must not do.

Tests: `MappedShardEquivalenceTests.MappedStore_MatchesHeapStoreAndReferenceModel(seed)`:
phase 1's generator drives a heap store and a store whose shards are snapshotted and
mapped at random points; after every operation the full read matrix and thrown exceptions
are equal, at least 8 seeds of 300 operations; `EmptyOverlayCommit_IsConstantTime`;
`Swap_HappensOnlyIfTheShardIsUnchanged`; `Swap_IsRefusedDuringReconciliation`;
`ReaderAcrossASwap_SeesOneCompleteGeneration`; `RetiredFile_IsDeletedAfterTheLastReader`;
a stress test with reader threads, commits and compactions for 10 s;
`AuthenticatedButInconsistentFile_FailsTheRequestAndTriggersARescan`;
`ContextResponses_AreByteIdentical_HeapAndMapped` over the walk workspace and the fixture
sources, including `--tests`, `includeRelated`, file-path and substring queries. Both
convergence tiers of phase 6 run with the mapped store.

Acceptance: equivalence and convergence green; on jellyfin, host private bytes after a
cached start and after the 100-query mix are reported against phase 0, and the query p95
budget of 3.3 is met or the allowed fixes are applied.

Gates: standard.

### Phase 9: worker hibernation

Status: not started

Goal: an idle worker exits; the next activity wakes it through verification.

Scope: `Workers/ParserProcessSupervisor.cs` (`HibernateAsync`),
`Workers/ParserSessionRegistry.cs` (`Hibernated`), `Workers/LanguageWorkerService.cs`
(activity clock, idle timer, wake before the pending operation, crash restart from the last
capture), `Services/IndexCoordinator.cs` (`HibernateCommand`, `WakeCommand`),
`Services/CacheState.cs` (`waking`), `StatusService.cs`, `CodeContextOptions.cs`
(`WorkerIdleMinutes`), `Program.cs`, `StartCommandHandler.cs`,
`DetachedStartOrchestrator.cs` (`--worker-idle-timeout`), `CodeContextEndpoints.cs`
(syntax-tree route wakes through the coordinator), `codecontext.openapi.json` (refreshed),
`skill/SKILL.md` and `references/operations.md` (a `hibernated` session is healthy; the
`waking` label), `AGENTS.md`.

Out of scope: hibernating workers without the capability.

Details: section 3.9.

Tests: `ParserProcessSupervisorTests`: `Hibernate_StopsTheProcessWithoutCountingARestart`,
`RequestAfterHibernate_StartsANewProcess`. `LanguageWorkerServiceTests` on fake time:
`FileBatch_ProjectInput_Refresh_SyntaxTree_EachResetTheIdleClock`,
`QueriesDoNotResetTheIdleClock`, `UnwrittenGeneration_CapturesFirstThenHibernates`,
`ReadOnlyInstance_NeverHibernates`, `Wake_VerifiesBeforeApplyingTheBatch`,
`WakeForASyntaxTree_GoesThroughTheCoordinator`, `CrashRestart_UsesTheLastCapture_WhenTheShardIsUnchanged`.
`StatusServiceScanStateTests`: `HibernatedIsNotFailed`. `IdleShutdown` test: the instance
timeout is unaffected by hibernate and wake. Convergence: tier 2 with a hibernate and wake
at random steps, 2 seeds of 60 steps, including edits written while hibernated.

Acceptance: with `--worker-idle-timeout 1` on this repository the worker exits after a
minute, status shows `hibernated`, an edit produces the corrected graph, and a query in
between answers without a worker process.

Gates: standard.

### Phase 10 (optional): body-only narrowing after a restore

Status: not started

Goal: a body-only change found at verification, or as the first edit of a dormant unit,
re-walks one file, as it does on a live worker.

Scope: `Projects/EditClassifier.cs` (declaration digest), `CSharpWorkspaceAnalyzer.cs`,
`State/WorkerStateFile.cs` (digest per file, state format bump), `State/UnitFingerprint.cs`.

Out of scope: any change to host or protocol.

Details: the digest is SHA-256 over the file's tokens with trivia dropped, each block body
of a method, constructor, destructor, operator or accessor replaced by one marker, and all
top-level statements replaced by one marker. Expression bodies and initializers stay in,
which is stricter than `AreEquivalent(topLevel: true)`. The required property is one
direction only: equal digests imply `EditClassifier.CanAffectOtherFiles` is false.
Dependents then key on a declaration fingerprint (signature, digests, declaration
fingerprints of references) instead of the full fingerprint.

Tests: `DeclarationDigestTests`: for every case of `CSharpWorkerEditClassificationTests`
and for 10,000 generated edit pairs, equal digests imply not visible; a property test that
a body edit keeps the digest. Both convergence tiers run again. Acceptance: first body edit
after a restore walks one file; tiers green.

Gates: standard. May be dropped; nothing later depends on it.

### Phase 11: measurements after, results recorded

Status: not started

Goal: the after-numbers, and the decision whether the release meets its goals.

Scope: run `scripts/bench/measure-cache.py` with `--require-cache` and
`--require-hibernation` on a release-recipe build of the branch AND on the v0.10.7 release
asset, interleaved, same day, same commits of the three repositories (recorded per run); fill `docs/perf/persistent-fact-cache.md`; add a results section to
this document and to the brief; extend `scripts/verify-publish.ps1` with one step: stop,
start again on the same directory, and require `indexing.cache.state` to reach `verified`.

First step, before any timed run and before the harness touches jellyfin or d2-roguelike
again: harden it against the real build. Carried from the phase 0 re-review (each
confirmed against the script by the coordinator or to be confirmed by the fixer):
1. Identity: a host counts as ours only if its parent process is one this harness
   spawned; "exact root and created after our spawn" alone would adopt an instance another
   session started on the same root within the roughly 0.3 s before ours registers.
2. A failed `list --json` during stop must not leak our instance: retry it, send the
   id-checked shutdown whenever our process is alive whatever the registry says, log pid,
   port and instance id when giving up, and do not start the hibernation run with
   `--idle-timeout 0`.
3. `atomic_write` removes its temporary file on every failure path, and `restore_all`
   removes and reports a leftover `.<name>.ccbench-tmp` in a measured repository.
4. `CacheDirGuard` writes its manifest only after the rename succeeded, or removes it when
   the rename fails.
5. Cold starts on a cache build assert what they are: Fatal if `ignored` was seen on a
   start expected to be cached; a start expected to be uncached asserts the cache state
   was absent at first status; each records `cold.cached`; `--warmup` at least 1.
6. The apply and revert clocks start after the atomic replace, not before the temporary
   file is written.
7. Disturbance gating uses a windowed maximum of CPU outside the harness, not the mean of
   a whole run, and only live, creation-verified processes seed "our" process tree.
8. `wait_counts_change` fails at once when our instance has died, instead of leaving a
   file edited for up to 900 s.
9. A `--check-config` mode that starts each repository once, makes no edit, and proves
   that every configured identifier (cold symbol, edited method, edited class) yields
   exactly one `target` line on the build under test; run on all three repositories.
10. The states the harness waits for are checked against what phase 4 and 9 actually
    report (see the `cache.state` table in 3.10), on this repository's bench worktree,
    with one untimed run of every row, before the timed runs.
A Sonnet and an Opus reviewer review that diff like any phase.

Out of scope: tuning. A missed number is reported with its cause and goes back to the
coordinator.

Tests: the new `verify-publish.ps1` step. Acceptance: every row of section 6 has before
and after values with the method stated; warm body and top-level edit medians are within
10 percent plus 0.05 s of phase 0.

Gates: standard; `verify-publish.ps1` on win-x64.

### Phase 12 (final, droppable): syntax-first startup

Status: not started

Goal: on a first-ever scan, serve declarations, `HAS_*` and inheritance by name from
parsing alone, labelled, before binding finishes.

Design, only as far as it touches earlier phases: the host sends two requests, a
`workspace/index` with an optional `"mode": "syntax"` and then the ordinary one. Each is
one atomic generation, so the applier and supervisor need no change. The first generation
is marked provisional on its shard: the snapshot writer skips provisional shards, and the
`cache` notice gains a `syntax` state through the same provider as `verifying`. The second
generation replaces the workspace, so ids of the syntax generation need not match semantic
ids; type ids can match, method ids cannot without binding parameter types. Nothing in
phases 1-11 assumes a shard is final beyond the provisional flag, which costs one bit in
`IGraphShard`.

Scope, tests and acceptance are written when the phase is taken up. Gates: standard and
protocol.

### Closing steps

1. Final adversarial audit by a fresh agent context: bugs and invariant breaks in the
   delivered code first, plan adherence second. Findings are fixed, reviewed and re-gated.
2. Dedicated pass over `skill/SKILL.md`, its frontmatter description and
   `skill/references/*.md`, as the brief describes; the packaged copy must still match.
3. Merge `main` into the branch first (it moved to eb1aa7e, `scripts/install.ps1` only, on
   2026-11-05) and re-run the gates. Then merge to `main` locally with `--no-ff`, bump
   `version.json` from 0.10 to 0.11 as its own final commit, run
   `scripts/prepare-node-worker-runtime.ps1` and `scripts/verify-publish.ps1` for win-x64
   with `-ReleaseVersion` equal to what the built executable reports, push `main`, and
   confirm the release with `gh release list`. Nothing is pushed before this step.

## 6. Measurement plan

Builds: release recipe (`scripts/verify-publish.ps1`, R2R worker, Native AOT host). Before
is tag v0.10.0, after is the branch head. Repositories: jellyfin
(`C:\Users\benpa\source\repos\jellyfin`, restored), d2-roguelike, this repository. Each
figure is the median of 5 runs with its range, on an otherwise idle machine, with the OS
file cache warm (one discarded run first). Memory is read with `Get-Process`: working set
and private bytes, both reported, because mapped pages count in the first only.

| Number | How it is taken |
|---|---|
| Cold start to first answered query, unchanged | No instance running; for after, a snapshot exists (run to `ready`, wait for `indexing.cache.lastWriteMs`, `codecontext stop`). Time from spawning `codecontext query <Symbol> --path <repo>` to its exit with code 0. Also time to `status == ready`, polled every 50 ms from the same instant. Record whether the output carried a `cache` line. |
| Cold start with one file changed, time to the corrected answer | Stop the instance. Apply the harness's body edit (adds one `CALLS` edge) to the body file. Spawn `query` for the edited method: time to first answer. Then poll status every 50 ms until node and edge counts differ from the snapshot's and status is `ready`: time to corrected. Repeat with the top-level edit. Before: both equal the cold start. |
| Host resident after start | Host pid from `codecontext list --json`; sampled 5 s after `ready`. |
| Host resident after 100 mixed queries | The fixed list in `scripts/bench/queries-<repo>.txt` (40 symbols at depth 1, 20 with tests, 20 at depth 3, 10 file paths, 10 substrings) sent in order to `/api/context/complete`; memory sampled after the last; total time, p50 and p95 latency recorded. |
| Worker resident before hibernation | Worker pid from `parsers.sessions`; sampled 5 s after `ready` and after 6 edits. |
| Worker resident after hibernation | Start with `--worker-idle-timeout 1`; wait until the session is `hibernated`; the process is gone, so the figure is the host's memory alone, reported beside it. |
| Wake-up time to first delta | While hibernated, write the body edit; time from the write to the change in counts. Also time to session `ready`. |
| Body and top-level edit latency, warm | The existing `timed_edit`: 3 rounds each of apply and revert, from file write to count change. Must not regress. |
| First edit after a cold start | As above, but the first edit after an unchanged start, body file in a leaf project and in a root project. |
| Snapshot size and write time | `indexing.cache.sizeBytes`, `workerStateBytes`, `lastWriteMs`, `lastCaptureMs` from status after the first scan and after an edit; file sizes cross-checked on disk. |
| Convergence | The test tiers of phases 6, 8 and 9; pass or fail with seed counts. |

## 7. Risks

- R1. The mapped store's commit rules drift from the heap store's. Guard: the reference
  model and seeded scenarios of phases 1 and 8, plus byte-identical `ContextService` output.
- R2. Query latency under materialization. Unmeasured until phase 8; budget and fallback in
  3.3.
- R3. Snapshot writes under the watched root overflow the `FileSystemWatcher` buffer, which
  triggers a full rescan, a commit, and another snapshot. Guard: 1 MiB chunks and the phase
  3 test. If it still overflows, the cache must move outside the watched tree, which
  contradicts the brief and goes back to the coordinator.
- R4. On Linux and macOS another process can truncate a mapped file and fault the host.
  Files are user-only and never rewritten in place; residual risk accepted.
- R5. Worker state that does not match the host's facts would produce wrong deltas.
  Guard: state id, SHA-256 recorded in the authenticated snapshot, fingerprint equality,
  full re-walk on any doubt, and the convergence tiers.
- R6. Same size and mtime with different content is not detected at verification beyond the
  2 s racy window. Accepted by brief decision 3.
- R7. The coordinator waits for `workspace/saveState`. Est. 100-250 ms on jellyfin, only
  after a quiet period. If phase 6 measures more than 250 ms on jellyfin, the worker
  answers after capturing and writes in the background, with `shutdown` waiting for the
  write, and that is built in phase 6.
- R8. The first edit after a restore or wake in a root project costs about a full walk on
  jellyfin until phase 10. The 30-minute idle default limits how often that is paid.
- R9. Re-walking an unchanged file must give the same facts as its stored walk. Roslyn is
  deterministic for this use; the convergence tiers would show otherwise.
- R10. Losing `~/.codecontext/cache.key` invalidates every snapshot of that user: one full
  scan per repository.
- R11. Convergence tests restart real workers. Tier 2 is sized at about 110 restarts;
  tier 1 carries the long walks in process. Debug worker starts under suite load are slow
  for a cause not yet known.
- R12. Two snapshots and two state files exist briefly during a write: about 80 MB on
  jellyfin.
- R13. The naming-heuristic change of phase 1 alters some `--tests` output compared with
  0.10.0.
- R14. Antivirus or indexers holding a new file can fail the rename or the delete on
  Windows. Both are retried; a failed rename leaves the previous snapshot in force.

## 8. Release notes for 0.11

Kept current by each phase that changes observable behavior; the closing step copies it
into the release.

- Result lists keep their ranking (`matches`, `usedBy`, `uses`, related items, test files and
  methods); where the ranking ties, and in the full view's unranked match list, results are
  ordered by id, ordinally, so the same graph gives the same response on every run. Before,
  ties fell to an unspecified order that could differ between runs, and so could which items
  survived truncation to a cap. Status `nodeTypes` and `edgeTypes` are ordered by type name.
  (Phase 1)
- `--tests` naming-heuristic evidence considers the first ten matching files, ordered by path
  (case-insensitively, then ordinally), instead of the first ten matching nodes in unspecified
  order, which could name one file several times. (Phase 1)

## 9. Known items

| Item | Placement |
|---|---|
| CS8604 at `CSharpWorkspaceAnalyzer.cs:337` | Phase 6, in `Rebuild`, which that phase rewrites |
| Redundant second build between open and index | Being removed on `main` by the 0.10.x patch (D25). Phase 7 makes `workspace/open` declarative on top of whatever `main` holds when it starts; it must not reintroduce the second build |
| MimeTypes source-only package gap | Out of scope |
| `JsonRpcConnectionTests.Notification_DispatchesToHandler` 30 s wait | Left as is; the cause of slow tests under suite load is unknown and out of scope. Phase 5 must not shorten it. |
| Test worker starts needing `TestWorkerOptions.Patient()` | Rule for all phases; new real-worker tests use it |
| `ParserProcessSupervisorTests` `CrashingRepeatedly...` and `MalformedOutput...` failed in 1 of 8 full-suite runs on `main` ("The pipe is being closed", state `Failed` where `Unavailable` is expected) | Predates this work: a race in how the supervisor classifies a dying worker. Phase 9 changes that code. If the hibernation work explains it, fix it with a test; if not, record what was seen. The assertion is not loosened. A failure of these two tests in a gate run before phase 9 is re-run once and recorded, not treated as a regression of the phase |
| Dedicated skill pass | Closing step 2 |
| Legacy in-place store writes (`UpsertNode`, `RemoveNode`, `UpsertEdge`, `RemoveEdgesTouchingNode`) during a staged reconciliation reach the live map at once and survive the reconciliation's commit only where the staged map still shares the shard object; a write to a shard the reconciliation already rebuilt, or to a new shard key, is dropped by that commit | Predates this work and is kept; pinned by `LegacyUpsertIntoANewShard_DuringAReconciliation_IsLiveUntilItsCommit` and the phase 1 legacy-mutation tests. No production path writes this way during a reconciliation. Phase 8 must keep a mapped shard behaving as a heap shard here, which those tests check |
| A shard key whose facts were all removed stays in the shard map; `RemoveNode(null)` throws when any shard exists and returns false on an empty store | Found in phase 1; not changed. Phase 2's writer must skip a shard with no facts; phase 3's capture must not treat an empty key as a change |
| Status `languageBreakdown` order follows scan history (file-metadata repository) | Out of scope unless a later phase needs byte-identical status |
| `ParserProcessSupervisorTests.SlowProgressObserver_DoesNotBlockProtocolResponses` failed once in a full-suite run during the phase 1 fix and passed alone and on the re-run | Timing-based, unrelated to the store. Same rule as the two supervisor tests above: re-run once and record |
