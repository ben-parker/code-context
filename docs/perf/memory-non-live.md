# Memory that is not live managed objects (M2)

Item M2 of [../MEMORY_FOOTPRINT_PLAN.md](../MEMORY_FOOTPRINT_PLAN.md). Investigation only:
every patch used here was throwaway and is not committed. Taken 2026-11-18 on this
repository (worktree of `claude/confident-cerf-5c3o1u` at `42d2d03`), Linux, 4 vCPU,
16 GB, .NET 10.0.15, Node 22.23.5, Release JIT build unless a row says "published".

**Machine caveat.** This VM reports a 260 MB L3 cache (`lscpu`). The .NET GC sizes the
gen0 budget from L3, so here a workstation-GC process may allocate about 130 MB between
gen0 collections. A desktop with an 8 to 36 MB L3 gets a much smaller budget. The C#
worker's idle gen0 garbage (below) is therefore larger here than it is on most user
machines. The other findings do not depend on L3.

## Answer in one table

Cached start, idle. MB of anonymous memory (`Pss_Anon`). The "after GC" column is one
`GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true)` with LOH
compaction, triggered by a throwaway probe thread.

| Process | Anon idle | Live managed | GC heap: dead objects and free space | Other native | Loader and JIT data | Anon after one aggressive GC |
|---|---|---|---|---|---|---|
| Host, 5 s after ready (M0 timing) | 182 | 107 | ~57 resident (committed 278, most of it never touched) | malloc ~5, thread stacks ~7 | ~12 (+15 code, shared mapping) | 120 |
| Host, 25 s after ready (after the snapshot write) | 265 | 108 | ~130 (gen0 56 with 29 free, LOH 68 with 22 free) | as above | as above | 134 (122 with `malloc_trim`) |
| Host after the 60-query mix | 321 | 108 | ~200 | as above | as above | 123 |
| C# worker | 200 | 50 | ~50 (gen0 76 MB never collected, gen1 13) | **85: Roslyn metadata images** | ~12 (+13 code, shared mapping) | 149 |
| C# worker, uncached start | 463 | 100 | ~250 (committed 354) | 85 | ~12 | 214 |
| TypeScript worker | 173-215 | V8 heap 124 live of 166 | V8: new space 32 reserved, ~10 old-space slack | malloc 22-26 | V8 code 5-15 | not tried (no managed GC) |

What returns it:

- **Host and worker GC garbage**: only an *aggressive* blocking compacting collection.
  A `Forced` compacting collection frees the objects but keeps the regions committed (host
  committed bytes stayed at 237 MB, RSS fell only 52 MB). Nothing in the GC's own
  scheduling returns it at idle: an idle process allocates nothing, so no further
  collection runs. GC settings help only partly (table "GC settings").
- **Worker native 85 MB**: nothing at runtime. It is the full PE image of 530 referenced
  assemblies copied into native memory by Roslyn. It is returned by loading fewer or
  smaller images (table "Reference loading").
- **Loader, JIT, stacks, malloc**: not worth chasing (about 25 to 35 MB per .NET process; a
  Native AOT host drops most of the code share).
- **TypeScript worker**: nothing short of the process exiting (M3). Its floor while alive
  is about 180 to 210 MB RSS.

## Method

- Instances were started with a copy of the `measure-memory.sh` logic (cached start after a
  warm-up start; ready means indexed, ready, and not serving cached). Readings at 5 s and 20
  to 25 s after ready. RSS and `Pss_Anon` from `/proc/<pid>/smaps_rollup`.
- Anonymous regions were classified from `/proc/<pid>/smaps`: the GC's reserved range is
  the longest chain of adjacent unnamed mappings (about 50 GB span); `memfd:doublemapper`
  mappings are JIT and stub code (W^X double mapping, counted as shared memory, so in RSS
  but not in `Pss_Anon`); `[heap]` plus 64 MB arena reservations are glibc malloc.
- `dotnet-dump collect --type Full` and `dotnet-dump analyze` (`eeheap`, `gcheapstat`,
  `dumpheap -stat -live`, `dumpobj`) gave per-generation committed, allocated and free
  space, loader and code heap sizes, and the `NativeHeapMemoryBlock` sizes.
  `maddress` is not available on Linux.
- A throwaway `M2Probe` class (a `[ModuleInitializer]` thread in the host and the C#
  worker that polls a command file) ran `GC.Collect` aggressive or forced, `malloc_trim(0)`
  and printed `GC.GetGCMemoryInfo` per generation.
- Allocation sources: EventPipe started from the environment
  (`DOTNET_EnableEventPipe=1`, `DOTNET_EventPipeConfig=Microsoft-Windows-DotNETRuntime:0x1:5`)
  and a throwaway TraceEvent script summing `GCAllocationTick` by type and by inclusive
  CodeContext frame. Sampled figures: every ~100 KB allocated, one event.
- Worker settings went through `CodeContext__WorkerEnvironment__csharp__<NAME>`; the host's
  own environment is inherited by workers, so host-only rows reset the worker value.
- TypeScript worker: a `--require` hook in `NODE_OPTIONS` (through
  `CodeContext__WorkerEnvironment__typescript__NODE_OPTIONS`) printed
  `process.memoryUsage()`, `v8.getHeapStatistics()` and heap spaces, forced a full GC, and
  wrote a heap snapshot summarised by constructor. Floors were measured with stand-alone
  Node scripts.
- Host anonymous memory at a fixed time is noisy (±40 MB between runs) because it depends on
  whether a DATAS collection happened to run. Worker figures repeat within 1 MB.

## Findings

### 1. The M0 reading is taken before the snapshot write

The snapshot service writes 10 s after the last commit (`SnapshotService` quiet period).
On a cached start the TypeScript worker always re-walks and replaces, so a new snapshot is
written on every start. M0 settles 5 s, so its cached host figure (254 RSS, 182 anon) is
before the write. Sampled every 3 s from ready, the host goes from 247/175 to 353/280 at
9 to 12 s and stays there. The steady idle host after a cached start is about **340 to 360
MB RSS, 265 to 287 MB anonymous**. The TypeScript worker goes the other way: 252 to 212 MB
at about 12 s, when the V8 memory reducer shrinks its heap. This explains the "212 or 249"
outlier in the M0 log. Measurements should settle 20 s or more.

### 2. Host: the non-live memory is GC garbage from three bursts

Host GC at 5 s (dump): DATAS had grown to 4 heaps; committed 278 MB, allocated 263 MB, of
which free space 98 MB (gen0 68, LOH 24, gen2 6); live 107 MB. Resident in the GC range:
164 MB. Committed but untouched pages do not count in RSS, so the committed figure overstates
the cost.

Allocations (sampled), cached start:

| Phase | Allocated | Main sources |
|---|---|---|
| Snapshot load (`SnapshotLoader.Materialize`) | 231 MB | edge ids, metadata dictionaries; ~100 MB of it stays live |
| (caveat, final review) | | Measured on a JIT build: the persistability check allocates about 1.4 KB per edge in tier-0 code (126 MB here) and 3.6 MB once optimized (`DOTNET_TieredCompilation=0`, the stand-in for the AOT host, where a load allocated 57 MB). The same holds for a write's check after an uncached start |
| Snapshot write (`SnapshotService.Write`) | 218 MB for an 8.3 MB file | `SnapshotWriter.CheckPersistable` 185 MB: it walks every edge of a shard still backed by the loaded snapshot, which decodes each edge again (`SnapshotShardReader.ReadEdge` 90, `EdgeId` 47, `Metadata` 41) only to check strings for lone surrogates; boxed `IReadOnlyDictionary` enumerators 17 MB. The sampler attributes 174 MB to `SnapshotFormat.IsWellFormed`, which does not allocate; that is inlined string allocation in the decode path. |
| 60 queries | ~56 MB of gen0 garbage, 0 MB more live (108 MB before and after) | per-request allocation; nothing retained |

Uncached start: 298 MB allocated, of which 200 MB in JSON-RPC receive and deserialise
(strings 118, frame byte arrays 92) and 49 MB in `AnalysisDeltaApplier`.

DATAS sets a gen0 budget that an idle process never reaches, so the garbage of each burst
stays resident until the next burst. An aggressive collection takes 84 to 215 ms and
brings the host to 120 to 135 MB anonymous after load, after the write and after queries.
After it, the same 60 queries grow the host to 210 MB anonymous instead of 321, and the 60
queries took 16.4 s against 20.1 s without (CLI start-up dominates both).

### 3. C# worker: one third garbage, one third assembly images

Dump at idle after a cached start: one heap; committed 108.6 MB; gen0 76.4 MB of which
almost all is dead (the worker had run 2 collections: gen0 budget about 130 MB on this
machine); gen1 12.6; gen2 4; LOH 15.2; live 51.5 MB. Loader heaps 12.3 MB, JIT code heap
1.5 MB, double-mapped code 13 MB (shared, not anonymous).

The other 85 MB anonymous is **530 `NativeHeapMemoryBlock`s, 85.1 MB**: Roslyn's
`MetadataReference.CreateFromFile` reads each assembly with `PEStreamOptions.PrefetchEntireImage`
into native memory (`Disk.Reference`). 167 of them, **56.8 MB**, are the runtime's own
implementation assemblies (`System.Private.CoreLib` alone is 15.6 MB, ReadyToRun code
included) loaded for the fallback compilation, which `CSharpWorkspaceAnalyzer`'s constructor
creates eagerly through `SharedReferences.Value` whether or not any file falls back. The
other 363 (28 MB) are project references from reference packs and NuGet.

Allocations (sampled): cached start 105 MB (`SyncApprovedFiles`/`Rebuild` 43, state
restore 23 of which `WorkerStateFile.Read` 14, `BuildBucket` 14). Uncached start 953 MB:
the walker (`NodeId` 122, `DisplayNameOf` 65, `EdgeFact.Id` 91: a computed property that
formats a new string on every access), and **sending deltas 150 MB**: `JsonRpcConnection`
serialises the parameters to a `JsonElement` (`SerializeToElement`) and then the whole
message again to a `byte[]` (`SerializeToUtf8Bytes`), so each delta is buffered twice.

An aggressive collection takes 78 to 236 ms and brings the cached worker from 200 to 149 MB
anonymous and the uncached worker from 463 to 214. A gcdump-induced gen2 (what the
coordinator tried) did not shrink RSS for the same reason a `Forced` collection does not:
it does not decommit.

### 4. TypeScript worker

At 20 s: RSS 211-215, anonymous 173-175. V8 heap 166 MB, 124 MB live after a full GC
(old space 98, large objects 17.5, code 4-5; new space 32 MB reserved and nearly empty);
malloc 22-26 MB. Heap snapshot (125 MB self size): AST objects (`NodeObject` 26 MB,
`IdentifierObject` 11, `TokenObject` 4.4, arrays 16), strings 23.5, compiled code 15.4,
`SymbolObject` 8. The type checker's own state is small. The program holds 228 declaration
files, 3.8 MB of text: TypeScript's `lib.*.d.ts` 2.4 MB (the default set includes
`lib.dom.d.ts`), `@babel/types` 0.64 MB and `typescript.d.ts` 0.56 MB (the worker's own
JavaScript requires them), for 12 indexed files.

Floor, stand-alone, after full GC:

| What is loaded | RSS | Anon |
|---|---|---|
| bare `node` | 37 | 6 |
| + `typescript` module | 85 | 47 |
| + `@vue/compiler-sfc` | 95 | 56 |
| + a program over `typescript-worker.js` with default libs | 210 | 171 |
| same with `lib: es2022`, `types: []` (no DOM) | 180 | 141 |

`--max-semi-space-size=1` saved 23 MB stand-alone but nothing in the real worker (the
memory reducer already shrinks new space). Dropping `--use-openssl-ca` (inherited from this
environment's `NODE_OPTIONS`) made no difference. The floor while the language service is
alive is about 180 to 210 MB RSS; most of it is the TypeScript compiler and the parsed
default libraries, not the repository.

### 5. GC settings (cached start unless noted, anonymous MB, idle at 20 s)

| Setting | Host | C# worker |
|---|---|---|
| baseline | 265-287 | 200 |
| `DOTNET_GCgen0size=0x1000000` (16 MB), worker | | 176 (uncached 463 to 364) |
| `DOTNET_GCgen0size=0x400000` (4 MB), worker | | 170 |
| `System.GC.Gen0Size` or `GCgen0size` in the worker's runtimeconfig | | 200: not honoured; only the environment variable works |
| `DOTNET_gcConcurrent=0`, worker | | 200 |
| `DOTNET_GCConserveMemory=9` + `gcConcurrent=0`, worker | | 190 |
| `DOTNET_GCHeapHardLimit` 160 MB worker, 256 MB host | 258 | 180 (200 MB, uncached: 266) |
| `DOTNET_GCRetainVM=1` | 287 | 200 |
| `DOTNET_GCgen0MaxBudget=16 MB`, host | no change (5 s) | |
| `DOTNET_GCConserveMemory=9`, host | 202 at 5 s (baseline 182-195) | |
| `DOTNET_GCHeapCount=1`, host | 225 at 5 s, worse | |
| `DOTNET_gcServer=0`, host | 247 at 5 s, worse | |
| `DOTNET_gcServer=0` + `GCgen0size=16 MB`, host | 153 at 5 s | |
| `DOTNET_GCDynamicAdaptationMode=0` (DATAS off), host | 378, worse | |
| `DOTNET_TieredPGO=0` / `TieredCompilation=0` / `ReadyToRun=1` | code mapping -1.5 to -4 MB; noise otherwise | same |
| `malloc_trim(0)` after an aggressive GC | -12 | 0 |

### 6. Published build

`dotnet publish src/CodeContext.Api -c Release -r linux-x64 --self-contained` built here
(AOT toolchain present; the system Node was copied in as the bundled runtime for the
build only). The C# worker it bundled is ReadyToRun but framework-dependent (its
runtimeconfig names the shared framework); a self-contained worker was not measured.

| Build | Host cached 20 s | Host after aggressive GC | Worker cached | Worker uncached | Worker uncached after GC |
|---|---|---|---|---|---|
| JIT (bin/Release) | 338-360 RSS / 265-287 anon | 208 / 134 | 266 / 200 | 566 / 463 | 317 / 214 |
| Published (AOT host, R2R worker) | 268 / 238 | 154.5 / 124 | 265 / 195 | 549 / 444 | 312 / 207 |

The AOT host is 40 to 55 MB smaller in RSS (no JIT, no double-mapped code; 25 MB of its
binary is file-backed). Its GC behaviour is the same. The R2R worker is the same as the JIT
worker. The M0 host figures (JIT build) overstate a release host by about 50 MB.

### 7. Reference loading in the C# worker (throwaway patch, cached, anonymous MB)

| Variant | Idle | After aggressive GC |
|---|---|---|
| baseline (`CreateFromFile`: whole image) | 200.6 | 149.0 |
| no eager fallback references | 143.5 | 91.7 |
| metadata only (`ModuleMetadata.CreateFromStream(..., PrefetchMetadata)`, stream kept open) | 144.2 | 92.8 |
| memory-mapped (`PEStreamOptions.Default`, stream kept open) | 115.3 | 63.8 (RSS 130; mapped pages are shared page cache) |
| no eager fallback + metadata only | 132.8 | 81.2 |

The cached start still answered from the baseline in every variant. Answers were not
compared in the variants that change loading; the lazy-fallback change cannot alter them
if the references are created when the first file falls back.

## Recommended changes, ranked

| Rank | Change | Measured saving | Risk | Where |
|---|---|---|---|---|
| 1 | Idle compaction: after a burst that allocated more than a threshold (e.g. 32 MB by `GC.GetTotalAllocatedBytes`), once the process has been quiet a few seconds, run `GCSettings.LargeObjectHeapCompactionMode = CompactOnce; GC.Collect(2, GCCollectionMode.Aggressive, true, true)`. Must be `Aggressive`: `Forced` does not decommit. | Host: -130 MB anon after a cached start (265 to 134), -198 after the query mix; -46 uncached. C# worker: -52 cached (200 to 149), -249 uncached (463 to 214). Pause 80-240 ms, off the request path. | Low. A pause on a quiet process; Roslyn weak caches may refill on the next edit (edit latency after it not measured). | Host: a small hosted service fed by the end of `SnapshotService.Write`, the end of an `IndexCoordinator` operation and request completion. Worker: after the reply to `index`/`open` and after a state save in `CSharpWorkerHost`/`WorkerSession`. |
| 2 | Create the fallback compilation's runtime references only when a file first falls back. | -57 MB anon in the C# worker, idle and after GC. | Low; answers unchanged by construction. | `CSharpWorkspaceAnalyzer`: constructor and `CreateFallback` pass `SharedReferences.Value` eagerly. |
| 3 | Load references as metadata only, or memory-mapped, instead of whole images. | Metadata only -56 MB (with 2: -68 together); memory-mapped -85 MB anon. | Medium: keeps 530 file handles open; on Windows they lock reference packs and NuGet files (`FileShare.Delete` helps deletes, not writes). Memory-mapping needs a route through `Disk` (the `MemoryMappedFile` ban) and the disk guard tests. | `Projects/Disk.cs` `Reference`. |
| 4 | Snapshot writer: do not re-decode shards still backed by the loaded snapshot to check strings (they were checked when written and authenticated when read); iterate metadata without boxing. Consider not rewriting the snapshot when a cached start committed identical facts. | 185 of 218 MB allocated per write; resident effect up to 90-130 MB until the next GC (rank 1 removes the resident part anyway). | Low for the first; the second needs a content comparison. | `SnapshotWriter.CheckPersistable`, `SnapshotService`. |
| 5 | Cap the C# worker's gen0 budget by launching it with `DOTNET_GCgen0size` (the runtimeconfig keys are not honoured). | -24 MB cached, -100 MB uncached on this machine; little on a machine with a small L3. | Low to medium: more gen0 collections during the scan. Machine-dependent. | Worker launch environment (`WorkerLaunchSpec.Environment` or the bundled manifest). |
| 6 | Serialise outgoing JSON-RPC messages once (write the parameters straight into the message writer instead of `SerializeToElement` then `SerializeToUtf8Bytes`); cache `EdgeFact.Id`. | Allocation only: about 75 of the 150 MB delta-send allocation and 91 MB of id formatting on an uncached start; idle RSS effect not measured (rank 1 covers the residue). | Low. Protocol bytes unchanged. | `JsonRpcConnection` (lines 278, 374, 399), `CSharpWorkspaceAnalyzer.EdgeFact`. |
| 7 | Release builds already use the AOT host; measure M-items on the published build. | -40 to -55 MB host RSS against the JIT figures. | None. | `measure-memory.sh --binary`. |
| 8 | TypeScript worker: hibernate (M3). Not recommended short of that: dropping `lib.dom` when no `tsconfig` asks for it saves about 30 MB but changes answers for DOM code. | ~210 MB per idle worker with M3; 30 MB otherwise. | M3 as planned. | M3. |

Not recommended: `GCHeapHardLimit` (risk of out-of-memory on a larger repository for -20
to -30 MB cached), `GCConserveMemory`, `GCRetainVM`, `GCHeapCount`, turning DATAS off, a
workstation-GC host (worse alone; with a gen0 cap it is about rank 1's effect without the
pause but more GCs while serving), tiering settings, V8 semi-space size, `malloc_trim`
(-12 MB host after a GC, nothing in the worker).

Also for M0: settle 20 s or more, or wait for the snapshot sequence to change, before
reading a cached start.

## Not determined

- Behaviour on a machine with a typical L3 (gen0 budget) and on Windows.
- Edit latency after an aggressive collection (Roslyn caches held weakly).
- A self-contained ReadyToRun worker (the publish produced a framework-dependent one).
- The split of the 28 MB of non-runtime reference images by package.
- Whether the metadata-only and memory-mapped loading variants keep answers byte-identical
  on an uncached start.
