# Building a CodeContext language worker

A worker is useful when a language has a real parser, compiler, or language service
that can answer semantic questions better than text search. It runs as a private,
long-lived child process and contributes normalized symbols and relationships to the
same graph as the bundled C# and TypeScript workers. The host stays compiler-agnostic.

## Package and discover a worker

Place one worker in its own directory with `worker-manifest.json` at the directory
root. The host searches these roots in order:

1. absolute paths in `CODECONTEXT_WORKERS_DIR`, in declared order (use the OS path
   separator; relative entries are ignored);
2. `~/.codecontext/workers`;
3. the release's bundled `workers` directory.

The first protocol-compatible manifest for each `parserId` wins. Directories within a
root are sorted ordinally, so duplicate selection is deterministic. Repository-local
manifests are never auto-executed.

```json
{
  "manifestVersion": 1,
  "parserId": "python",
  "displayName": "Python",
  "version": "1.0.0",
  "command": "python-worker",
  "args": [],
  "minProtocolVersion": 1,
  "maxProtocolVersion": 1,
  "languages": ["python"],
  "extensions": [".py"],
  "projectMarkers": ["pyproject.toml"],
  "projectInputs": ["pyproject.toml", ".lock"]
}
```

A command found beside the manifest is preferred; otherwise a bare command uses
absolute `PATH` entries, and never the current directory, which is usually the
repository being indexed. An absolute entry is trusted as the machine owner's choice,
even inside a repository: an activated `.venv/bin` or `node_modules/.bin` on `PATH` is
searched, so a worker with a bare command can resolve there. Explicit relative commands
resolve from the manifest directory. Releases
should carry their runtime or a self-contained executable so installing CodeContext
once is sufficient.

## Implement the lifecycle

Use JSON-RPC 2.0 with `Content-Length` framing on stdin/stdout. Never log to stdout;
use stderr. Exit when stdin reaches EOF, even if `shutdown` was not received.

Implement this sequence:

1. `initialize`: negotiate a protocol version and declare capabilities/span semantics.
2. `workspace/open`: accept only the host's approved file set and build/reconcile
   project state.
3. `workspace/index`: emit monotonic `analysis/progress` notifications while walking
   files, emit one or more `analysis/delta` notifications, then return a complete
   response.
4. `workspace/applyChanges`: update persistent state and emit a **file-scoped**
   replacement (`replacesWorkspace:false`) covering every file whose emitted facts
   changed — see "Emit file-scoped deltas" below.
5. `$/cancel`: cooperatively stop work for the named request.
6. `shutdown`: answer, then exit when the host closes stdin.

The [canonical schema](../src/CodeContext.Parser.Protocol/protocol/parser-protocol.schema.json)
ships in releases as `protocol/parser-protocol.schema.json`. Helper packages are
released as `CodeContext.Worker.Protocol`, `@codecontext/worker-sdk`, and
`codecontext-worker-sdk` for .NET, npm, and PyPI-style packaging respectively. See the
[.NET protocol package](../src/CodeContext.Parser.Protocol/README.md),
[npm SDK](../sdk/npm/README.md), and [Python SDK](../sdk/python/README.md) for the
language-specific entry points.

### Report indexing progress

For every `workspace/index` request, emit `analysis/progress` notifications correlated
by `requestId`, `workspaceId`, and `generation`. The request's `files` array is a set:
paths must be unique under the host platform's path comparer. `filesTotal` is the
number of approved paths, `filesProcessed` is monotonic, and `currentFile` is the most
recently attempted path. Unreadable or vanished files still count as processed after
the worker records their diagnostic. The final notification must report
`filesTotal/filesTotal` before the successful response; an empty workspace reports
`0/0` with a null file. Progress describes analysis work only. The host continues
buffering graph deltas and does not expose the new generation until its terminal delta
commits.

## Preserve graph ownership

Every emitted node ID, edge ID, and edge source ID must begin with:

```text
<parserId>:<workspaceId>:
```

Follow it with a stable language identity, not a source offset. Include overload or
declaration shape where the language permits same-name declarations. Edge targets may
be unresolved/external names, but sources are always worker-owned. The host rejects a
delta that violates this boundary so one extension cannot overwrite another worker's
facts.

Declare the worker's line/column bases and inclusive/exclusive end convention during
`initialize`. The host normalizes stored graph spans to zero-based, end-exclusive
coordinates.

## Emit file-scoped deltas

`workspace/index` replaces the whole workspace: every delta carries
`replacesWorkspace:true` and an empty `replacesFiles`. `workspace/applyChanges` is
file-scoped: every delta carries `replacesWorkspace:false` and only the changed
files' facts. The contract:

- **Scope = facts changed, not files edited.** A change to one file can change how an
  untouched file's facts resolve (cross-file binding). The worker must include every
  file whose *emitted facts* differ from its previous emission. The reference workers
  do this by re-analyzing the workspace, hashing each file's fact set, and diffing
  against the previous emission's hashes — files whose hash changed join the delta.
  The host drops an edge when its target node is removed, so if the same edge is still
  emitted once its target has no node, its file has to join the delta too: the C#
  worker hashes whether each edge's target has a node.
- **`replacesFiles` = changed ∪ removed, verbatim.** List each changed file plus each
  file removed since the last emission, as byte-for-byte the same strings the emitted
  nodes carry in `filePath` (the host matches them raw — case-insensitively on
  Windows, no normalization on either side). A listed file with no facts in the
  request has its facts deleted.
- **A node belongs to its `filePath`; an edge belongs to its source node's file.** The
  host removes the nodes of each listed file, and every edge sourced at those nodes,
  so that is the unit to hash and emit. Never group an edge under a resolved target's
  file. If a language can emit the same node or edge id from more than one file (e.g.
  C# partial types), pick one deterministic owner that depends only on the current
  file set — the reference workers use the ordinally smallest owning path — so
  incremental results never depend on edit order. An edge found in one file can then
  be sourced at a node owned by another: it belongs to the owner. Grouping it under
  the file that found it loses it when the owner is replaced, and leaves it stale when
  the other file stops emitting it.
- **Symbols from outside the workspace are stubs.** A worker may emit a node for a library
  symbol the code uses, so a query on it can list its users. Set `metadata.external` to
  `"true"` and `metadata.assembly` to where it came from, and give it a virtual `filePath`
  starting `external:`, which `replacesFiles` lists like any other file. The host keeps such
  a node out of file relationships and never prunes it as a missing file, so a worker must
  remove it when nothing uses it.
- **Project inputs are not source files.** `projectInputs` in the manifest lists file names,
  or extensions starting with a dot, that change what the worker's files mean. The host
  watches an exact name wherever it appears, including paths it would not index, and an
  extension only in files it would. It answers a change with an empty
  `workspace/applyChanges`, on which the worker rereads its projects. The usual
  rule still holds: emit at least one delta.
- **Chunks agree.** All chunks of one request carry the same `replacesWorkspace`
  value (the host ORs the flags) and should repeat the full `replacesFiles` list (the
  host unions them). Always emit at least one delta per mutation, ending with
  `isLastForRequest:true`, even when nothing changed.
- **Commit your diff baseline only after the final chunk is sent.** If emission fails
  mid-stream the host commits nothing; keeping the previous baseline means the next
  batch re-emits the same difference and converges.

## Normalized graph and native trees

Normalized nodes/edges are the primary extension contract. Use them for semantic and
cross-language relationships. Do not put a detailed universal AST into graph nodes.

Optionally advertise `nativeSyntaxTree` and implement `syntaxTree/get`. Return a
parser-owned, versioned `format` plus a bounded JSON tree. Support `start`/`length`
ranges and `maxDepth` so callers can request a focused subtree. Native trees are
generated on demand, are not persisted, and supplement rather than replace normalized
facts.

## Verify before distribution

Test fragmented/adjacent frames, EOF exit, cancellation, crashes/restarts, invalid
requests, workspace replacement, one-file semantic changes, and protocol range
fallback. Start CodeContext from a directory unrelated to the install and confirm the
worker reaches `ready` in `/api/status`.
