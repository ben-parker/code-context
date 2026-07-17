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
4. `workspace/applyChanges`: update persistent state and replace every fact whose
   semantics may have changed. Reanalyze dependents when a changed file can affect
   their resolution.
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
