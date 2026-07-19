# Building a CodeContext language worker

A worker is useful when a language has a real parser, compiler, or language service
that can answer semantic questions better than text search. It runs as a private,
long-lived child process and contributes normalized symbols and relationships to the
same graph as the bundled C# and TypeScript workers. The host stays compiler-agnostic.

## Package and discover a worker

Place one worker in its own directory with `worker-manifest.json` at the directory
root. The host searches these roots in order:

1. paths in `CODECONTEXT_WORKERS_DIR`, in declared order (use the OS path separator);
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
  "projectMarkers": ["pyproject.toml"]
}
```

A command found beside the manifest is preferred; otherwise a bare command uses
`PATH`. Explicit relative commands resolve from the manifest directory. Releases
should carry their runtime or a self-contained executable so installing CodeContext
once is sufficient.

## Implement the lifecycle

Use JSON-RPC 2.0 with `Content-Length` framing on stdin/stdout. Never log to stdout;
use stderr. Exit when stdin reaches EOF, even if `shutdown` was not received.

Implement this sequence:

1. `initialize`: negotiate a protocol version and declare capabilities/span semantics.
2. `workspace/open`: accept only the host's approved file set and build/reconcile
   project state.
3. `workspace/index`: emit one or more `analysis/delta` notifications, then return a
   complete response.
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
- **`replacesFiles` = changed ∪ removed, verbatim.** List each changed file plus each
  file removed since the last emission, as byte-for-byte the same strings the emitted
  nodes carry in `filePath` (the host matches them raw — case-insensitively on
  Windows, no normalization on either side). A listed file with no facts in the
  request has its facts deleted.
- **Every fact belongs to the file whose analysis produced it.** Group edges under the
  file whose walk emitted them (their source is always in that file), never under a
  resolved target's file. If a language can emit the same node or edge id from more
  than one file (e.g. C# partial types), pick one deterministic owner that depends
  only on the current file set — the reference workers use the ordinally smallest
  owning path — so incremental results never depend on edit order.
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
