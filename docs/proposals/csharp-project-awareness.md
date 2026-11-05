# C# worker project awareness

Status: all decisions agreed on 2026-11-04 and delivered on this branch. Results are below.

## Problem

`CSharpWorkspaceAnalyzer` compiles every `.cs` file as one compilation against the
worker's own runtime. It has no notion of a project, which causes four confirmed wrong
answers: `ImplicitUsings` leaks between projects, `#if` symbols are not applied,
same-named types merge into one node, and values of NuGet or engine types lose their
members. `query Node3D` prints `missing` because a library type has no node.

The target is Visual Studio's behavior. A project's references are read as metadata so
types resolve, library internals are never indexed, and Find References on a library
type lists uses in the project alone.

## Measurements behind this design

Release R2R worker at 7a87c85, driven over JSON-RPC, on this machine (16 logical cores).
The 316k-line corpora are generated: 1,500 files, flat or as 20 projects.

| Repo | First scan | Per edit | Peak memory |
|---|---|---|---|
| Flat, 316k lines, no csproj | 46.1 s | 35-37 s | 1,206 MB |
| 20 projects, 312k lines | 47.2 s | 35-41 s | 1,286 MB |
| d2-roguelike, 44k lines | 13.8 s | 3.4 s | 397 MB |

A spike on the flat corpus shows where the time goes:

- Binding is the floor. `GetSymbolInfo` over all 735k names takes 28-32 s alone, so the
  graph walker adds little and caching display strings would not help. Only binding less
  will help. Eight threads cut this to 13.7 s.
- After one edit, `ReplaceSyntaxTree` plus rebinding that file takes about 50 ms.
- `SyntaxFactory.AreEquivalent(old, new, topLevel: true)` takes under 10 ms and answers
  whether an edit touched only method bodies, accessors or non-const field initializers.
- Parsing plus binding peaked near 440 MB in the spike against 1.2 GB in the worker. Most
  of the gap is facts: every edge is an object with a long id string and a metadata
  dictionary, all held until sent.
- Library use is cheap to represent. `src/` of this repo binds 7,154 uses to only 628
  distinct library symbols, so stub nodes are few. Most of those edges exist today as
  dangling `CALLS`.

## Design

### Project model, read from files only

A new `ProjectModel` in the worker does one directory walk from the workspace root,
collecting `*.csproj`, `*.sln` and `project.assets.json`, skipping `.git`, `.vs` and
`node_modules`. Hidden directories are walked, because that is where Godot keeps restore
data. Only projects owning at least one approved file are compiled.

Each `.csproj` goes through a small static evaluator: properties, items, `Import`,
`Directory.Build.props/targets`, `Directory.Packages.props`, and `Condition` expressions
over those values. `Directory.Build.*` is searched up to the enclosing repository, or the
workspace root when there is none, so a workspace opened on a subdirectory sees its
repository's settings and nothing above the repository counts. It runs no tasks and no targets, and it never loads an assembly. Only
pure string functions and `Exists` are understood. A condition it cannot read counts as
true and is recorded. `Configuration` is `Debug`, or the first declared configuration
when a project has no `Debug`.

`Microsoft.NET.Sdk*` is built in: default globs, TFM constants, `DEBUG;TRACE`. A
third-party SDK such as `Godot.NET.Sdk/4.8.0` is resolved in the package folder and its
`Sdk/Sdk.props` and `Sdk.targets` are read as data by the same evaluator. That is how
`TOOLS` and `GODOT` arrive with no Godot-specific code.

| Input | Source, in order |
|---|---|
| Package compile assets | `project.assets.json`, matched to the project by its `project.restore.projectPath`, so no location is assumed. Then a file at the evaluated intermediate path. |
| Framework references | `frameworkReferences` and `downloadDependencies` resolved to `Microsoft.*.App.Ref` ref packs in the package folders, then `<dotnet root>/packs`, then `<dotnet root>/shared`. A framework also arrives through a package or a project reference: restore records both, and without restore data the frameworks referenced projects declare are followed |
| An assembly arriving twice | One copy per file name, the highest assembly version, as a build's conflict resolution does. Two signed versions would make every type in them ambiguous |
| `netstandard`, `net4x` | `NETStandard.Library`, `Microsoft.NETFramework.ReferenceAssemblies` or the system reference assemblies, then a newer reference set or the installed runtime, reported as a stand-in |
| Project references | Restored `projectReferences`, else `<ProjectReference>` |
| `<Reference HintPath>` | Path as written, after property expansion. A file named like a workspace project's assembly becomes a project reference, which matters for Unity. |
| Defines, `LangVersion` | Evaluated properties, and the SDK's default language version for the TFM when a project names none, since newer versions change what code binds to. `Nullable` is deliberately not applied: it would change no fact, would slow binding, and would rewrite parameter types in identifiers. |
| Implicit usings | `*.GlobalUsings.g.cs` in the intermediate directory when present, else the SDK base set, plus `<Using>` items |
| Multi-targeting | First TFM only, as Visual Studio's active target. The others are reported. |

Which project compiles a file is decided by running its `Compile` item operations in
order over the approved files. A file in two projects is parsed and compiled in both,
but facts come from one owner: the deepest project whose directory holds the file, then the
ordinal smallest path. Code only a non-owner compiles has no nodes, which status reports.

### Compilations

Each project gets one `CSharpCompilation` with its own parse options, a global-usings
tree, shared `MetadataReference` instances, and a `CompilationReference` per project
reference. The spike confirmed source locations survive across that reference.
Analyzers and source generators are never loaded, because that would run repository
code. Generated members therefore stay unresolved, and that is reported (see below).

Files in no project go into one fallback compilation, which is today's behavior: runtime
references, no defines. It references every project compilation so that its code keeps
binding as it does now. A repository with no csproj has only this compilation, so its ids
do not change.

### Identity

| Field | Rule |
|---|---|
| `id` | `csharp:<ws>:<projectKey>:<display>`. `projectKey` is an 8-hex hash of the project's relative path, so ids stay stable when another project adds the same name and the host's memory cost stays small. Fallback files keep today's `csharp:<ws>:<display>`. |
| `identifier` | `csharp:<display>` while unique, so existing identifiers keep working. When two projects or libraries declare the same name, each becomes `csharp:<project>#<display>`. |

A bare identifier that now has several owners returns all of them as an ambiguous match,
never a silent pick, so `query Program.Main` returns two targets. The host resolves this
by matching the `#<display>` suffix, which is its only identity change. The id is the
stable thing; an identifier can change form, and the owning files re-emit together so
the store's uniqueness check holds.

A symbol's owner comes from its declaring file, not from the compilation being walked, so
a type compiled into two projects stays one node.

### Library symbols

The walker creates a stub node for each library symbol the project actually uses, plus
its containing types, with `HAS_*` edges between them. A stub is flagged
`metadata.external=true`, names its assembly, and has no span. Library-to-library edges
are never recorded. Stubs are identified by display name, so they survive package
upgrades and type forwarding.

Every library is treated alike, the BCL included, with no list of which ones count. A
stub lives in a virtual file `external:<namespace>`, so the existing file-scoped delta
adds and removes it unchanged. A stub goes when the last file using it stops.

Host changes for stubs:

- `query` prints `external <assembly>` instead of a location.
- `uses` hides external targets by default and reports how many it hid, or every project
  method would list `List<T>.Add`. `--external` shows them.
- A name that matches project code leaves library symbols of that name out and reports how
  many, or `query Add` would turn ambiguous in any project with a `List<T>`. `--external`
  lists them, and a name only a library declares resolves to its stub as usual.
- Method-family expansion stops at a stub. Otherwise every `Dispose` becomes one family.
- File dependencies and related items skip stubs, and pruning files missing from disk
  leaves their virtual files alone.
- Transitive traversal needs no change: a stub has no edges back into the project.

### Saying what is not known

A worker reports project coverage in a new optional `coverage` field on the first delta
chunk. It is additive, so the protocol version stays. Diagnostics are ignored by the host
today, so this cannot ride on them.

| State | Meaning |
|---|---|
| `complete` | Project evaluated, restore data read, every reference found |
| `partial` | Compiled as its own project, but some references are missing, for example no restore data. Packages are then tried from the global package folder by declared version and the rest are listed. |
| `fallback` | Project unreadable, or no framework reference found. Today's behavior. Also used for files in no project. |

Each entry carries the target framework, inactive TFMs, file count, missing references,
whether generators were skipped, and a count of names that failed to bind, which is what
exposes a project that looks complete but is not. `/api/status` and `codecontext status`
list these. `query` prints a `coverage` line whenever a `missing` result or an empty
`usedBy` could be caused by a degraded project.

### Incremental analysis

Facts are retained per file in a compact form: interned id strings and structs with no
per-edge dictionary. `ProtocolEdge` objects are built chunk by chunk at send time. Each
edit is classified:

| Edit | Re-walked |
|---|---|
| Body, accessor or initializer only (`AreEquivalent` top-level) | That file alone, in every project compiling it |
| Top-level statements only | That file alone, by comparing the non-statement members |
| Anything else in a file, or a file added or removed | Its project and transitive dependents |
| A project input changed | The projects it affects, after re-evaluating them |

Dependents keep their reference to the previous compilation across a body-only edit. Its
declarations are identical, so nothing can bind differently. Unchanged files keep their
retained facts, and the per-file hash diff still decides what to send. Ownership of ids
emitted by several files (partial types) is settled in one pass over nodes, not edges, and
only files that could have changed are rebuilt.

Walks run in parallel, capped at 8 threads.

Project inputs are stat-checked on each `workspace/open`, which the host sends before
every mutation. A new optional `projectInputs` manifest field names what else to watch, so
a `.csproj` edit or a restore needs no source edit: the host watches exact names everywhere,
including `obj/`, and extensions where it would index, and answers a change with an empty `workspace/applyChanges`, on which the
worker rereads its projects. A project that reads as it did keeps its compilation and facts.

## Results

Release R2R worker, driven over JSON-RPC, on the same machine as the baseline. Before is
7a87c85, after is this branch. The graphs differ: after holds library stubs and the usage
they make visible, which adds 24% edges on the generated corpora.

| Repo | | First scan | Body edit | Top-level edit | Peak memory |
|---|---|---|---|---|---|
| Flat, 316k lines, no csproj | before | 46.1 s | 35-37 s | 37 s | 1,206 MB |
| | after | 25.2 s | 0.07-0.44 s | 13-14 s | 755 MB |
| 20 projects, 312k lines | before | 47.2 s | 35-41 s | 38 s | 1,286 MB |
| | after | 24.7 s | 0.08-0.56 s | 6.6-7.0 s | 772 MB |
| d2-roguelike, 44k lines | before | 13.8 s | 3.4 s | 3.4 s | 397 MB |
| | after | 5.4 s | 0.05-0.07 s | 0.2-0.6 s | 453-508 MB |
| This repo, 41k lines | after | 6.3 s | 0.07-0.11 s | 1.9-4.1 s | 410 MB |

d2-roguelike uses more memory than before: it now holds 25 compilations and 27% more nodes.
That held at about 460 MB across 180 edits rather than growing. This repo's before figures
are left out because it grew between the two measurements.

The body-only path is guarded by a committed walk: four seeds through 120 mixed edits across
projects, compared with a fresh scan at every step. Injecting a bug into each path it guards
fails it, and two rounds of independent review drove the shape-change cases into it.

## Invariants

- No Roslyn or project-file logic enters the host. The host gains only the `coverage`
  plumbing, the `external` handling and the identifier suffix match.
- Every node and edge id still starts with `<parserId>:<workspaceId>:`, and generation
  commits stay atomic.
- Nothing from a repository is executed: no MSBuild, tasks, analyzers or generators.
  Assemblies are read through Roslyn's metadata reader only. Paths a repository supplies are
  not opened if they name another host, because that alone makes Windows authenticate to it.

## Known gaps, reported rather than fixed

- Source-generated members stay unresolved. Godot's `SignalName` and `PropertyName` are
  examples. The unbound name count makes this visible.
- Code that only compiles for an inactive TFM or for `Release` is not seen, as in an IDE.
  Status reports inactive targets. `rg` remains the cross-check.
- Files a project compiles but the file selector refuses, such as gitignored generated
  code, do not bind.
- `extern alias` on references is not applied.
- Ignore rules are pinned to the old selector, not to Git, and at least one case differs:
  `x[!a]b` matches `x/b`. A `git check-ignore` comparison is a follow-up.
- A source-only package, whose `contentFiles` a build compiles into the project, is not read, so its types do not bind.
- Code only a non-owner project compiles, because it defines a constant the owner does not,
  has no nodes. Status notes how many such files a project has.
- A top-level edit in one huge project still re-walks that project. A later step could
  re-walk only files that mention a changed name, but implicit uses such as conversions
  and `foreach` make that unsafe without more work, so it is left out.

## Decisions

All were agreed on 2026-11-04.

1. Agreed. Identifiers stay bare until a collision, then gain a `<project>#` qualifier. A
   bare identifier with several owners returns all of them as an ambiguous match, never a
   silent pick. The alternative, always qualifying, never changes form, but it breaks every
   existing identifier and adds tokens to all output.
2. Agreed. All libraries get stubs, including the BCL. `uses` hides external targets by
   default and reports how many it hid. An opt-in `--external` flag reveals them, so the
   count is not a dead end.
3. Agreed. Multi-targeted projects analyze the first TFM only, and the others are
   reported in status and coverage. Analyzing every TFM would see more code but multiplies
   cost for those projects.
4. Agreed. Third-party SDK `.props` and `.targets` are read as data by the same static
   evaluator and are never executed. That yields `TOOLS` for Godot without a Godot list.
5. Agreed, on two conditions. A body-only edit re-walks that file alone, which goes beyond
   the brief's "changed project and dependents" and carries the per-edit win, since binding
   is the floor.
   - Anything a dependent can bind differently against must classify as top-level: const
     field initializers, attribute arguments, default parameter values, enum member values,
     and expression bodies or `nameof`/`typeof` uses whose result flows into a signature or
     constant. What `AreEquivalent(topLevel: true)` ignores is verified, with a test per case.
   - Before step 5 ships, a committed deterministic edit walk (several seeds, 100+ steps,
     body-only and top-level edits across files and projects) compares the incremental
     graph to a fresh scan at every step.

## Delivery order

1. Project model and evaluator, unit-tested on fixture project shapes with no Roslyn.
2. Per-project compilations, project-scoped ids and identifiers, replacing the implicit
   usings text insertion on main.
3. Stub nodes and host handling.
4. Coverage reporting through protocol, status, query, OpenAPI and skill.
5. Compact facts, classified incremental walks, parallel first scan, behind the
   convergence walk that decision 5 requires.
6. Shape tests, live check on a library type, Release R2R measurements.
