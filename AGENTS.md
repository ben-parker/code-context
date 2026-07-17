# Repository guide for coding agents

## Start with repository context

CodeContext can investigate its own source tree. Before tracing a feature or changing
behavior, run:

```bash
codecontext query ContextService
codecontext query ContextService --tests  # when test evidence matters
```

`query` selects the closest registered ancestor, starts a detached instance when none
exists, waits for indexing, and verifies contract, root, and instance identity. Tests
are omitted by default; add `--tests` whenever test evidence matters.

Use the normalized graph for semantic questions:

```bash
codecontext query ContextService
codecontext query "csharp:CodeContext.Api.Commands.StartCommandHandler" --tests
codecontext query multi ContextService StartCommandHandler
```

Round-trip the returned canonical `target.identifier` for unambiguous follow-up
queries. Inspect relationship totals and truncation flags. `query multi` reduces HTTP
round trips while preserving order and duplicates. Default output is compact
agent-oriented text; use `--human` for expanded prose or `--json` for the exact API
response. Use `POST /api/syntax-tree` only
when exact parser structure, tokens, modifiers, or spans matter.

After a branch switch or large rewrite, call `POST /api/index/refresh` and wait until
`indexing.status` is `ready` with `indexing.operationId` at least the operation ID in
the refresh response.

The tool under investigation can itself be stale or broken. For unexpected empty
results, use `codecontext status --path .` and, when necessary, `codecontext list
--json` or manual `codecontext start --detach --path .`; verify root path, indexing
state, parser state, spelling, filters, caps, and ignore rules. Use `rg` as the required
cross-check and as the primary tool for literal
strings, configuration, comments, filenames, and documentation:

```bash
rg -n "ParserProcessSupervisor" src tests
rg --files -g '*.cs' -g '*.ts'
```

## Solution and runtime architecture

- `src/CodeContext.Api` is the composition root and CLI. `Program.cs` defines
  `start`, `stop`, `list`, `status`, and `query`; `CodeContextEndpoints.cs` owns REST routes.
- `src/CodeContext.Mcp` exposes `GetContext`, `GetMultiContext`, and `GetStatus` over
  MCP stdio using the same Core services as REST.
- `src/CodeContext.Core` owns graph models, in-memory repositories, context assembly,
  indexing, file selection/watching, worker discovery/supervision, and delta commits.
- `src/CodeContext.Parser.Protocol` owns parser-worker JSON-RPC contracts,
  Content-Length framing, manifests, generated JSON serialization metadata, and the
  canonical cross-language schema.
- `src/CodeContext.CSharp.Worker` is a regular JIT worker. Roslyn and persistent C#
  workspace state live here, outside the host.
- `src/CodeContext.TypeScript.Worker` is a persistent Node.js worker with one
  TypeScript language service per workspace.
- `tests/CodeContext.Core.Tests` contains unit, protocol-fixture, worker, endpoint,
  repository, watcher, and coordinator tests. `tests/CodeContext.FakeWorker` supports
  supervisor/protocol failure tests.
- `sdk/npm` and `sdk/python` are dependency-free framing helpers for third-party
  worker authors. `docs/language-worker-sdk.md` is their durable lifecycle guide.

At runtime, `WorkerCatalog` discovers compatible manifests from
`CODECONTEXT_WORKERS_DIR`, `~/.codecontext/workers`, then the bundled `workers`
directory. It never auto-executes repository-local manifests. `LanguageWorkerService`
supervises discovered workers and routes files by claimed extension.

`IndexCoordinator` is the only writer-side entry point. It serializes startup scans,
full/single-file refreshes, and coalesced `FileWatcherService` events. Workers stream
analysis deltas; `AnalysisDeltaApplier` validates and buffers them before an atomic
generation swap in `InMemoryCodeGraphRepository`. Readers see either the previous or
new complete generation, never a partial worker update.

`ContextService` performs identity resolution and assembles relationships for both
REST and MCP. The store is intentionally in-memory: every process start performs a
fresh scan, and there is no configurable database backend.

## Architecture invariants

- Keep language toolchains out of the host. Core, API, and MCP must not gain Roslyn,
  TypeScript compiler, or other language-parser dependencies. Production parsing is
  out of process; `ILanguageParser` remains only a test/future-adapter seam.
- Worker stdout is protocol-only JSON-RPC with Content-Length framing. Diagnostics go
  to stderr. Workers must exit on stdin EOF and must not register an independent
  CodeContext instance or open a service port.
- Every worker-owned node ID, edge ID, and edge source ID starts with
  `<parserId>:<workspaceId>:`. IDs are stable semantic identities, not source offsets.
  A worker must replace all facts whose meaning may change and reanalyze semantic
  dependents when necessary.
- Preserve atomic, parser/workspace-scoped generation commits and stale-generation
  rejection. Never let one worker delete or overwrite another worker's facts.
- Keep `IndexCoordinator` as the sole ordered mutation path. Watchers and endpoints
  enqueue work; they do not mutate graph state independently.
- Apply the same repository file-selection policy to initial scans, refreshes, and
  watcher events. Mandatory exclusions such as `.git/`, `.codecontext/`, `bin/`,
  `obj/`, `node_modules/`, and `.venv/` cannot be re-included by `.gitignore` rules.
- Treat graph relationships as static evidence, not runtime execution or test
  coverage. `relatedItems` is proximity, not dependency evidence.
- Preserve canonical identifiers and compact-response count/truncation semantics
  across REST and MCP.

## Contract and generated-file responsibilities

The canonical external worker contract is
`src/CodeContext.Parser.Protocol/protocol/parser-protocol.schema.json`. A protocol
change must keep the schema, .NET DTOs in `Contracts.cs`/`WorkerManifest.cs`,
`ParserProtocol.Version`, source-generated `ParserProtocolJsonContext`, bundled worker
implementations, manifests, SDK constants/types, and protocol tests aligned. External
workers implement the schema; they do not depend on the host assembly.

`codecontext.openapi.json` is the checked-in public REST contract. Endpoint, query
parameter, or public DTO changes must update the endpoint metadata/source-generated
JSON registrations and then refresh this file from the running
`/openapi/v1.json` endpoint. Review the resulting contract diff; do not hand-edit only
one side.

`skill/SKILL.md` is the canonical packaged agent skill. Its relative references must
remain valid, and `scripts/verify-publish.ps1` requires the packaged copy to match it
byte-for-byte.

## Commands

Install source-only worker dependencies once per clean checkout:

```bash
npm ci --prefix src/CodeContext.TypeScript.Worker
```

Build and run focused tests:

```bash
dotnet build
dotnet test tests/CodeContext.Core.Tests/CodeContext.Core.Tests.csproj --filter "FullyQualifiedName~ContextServiceTests"
dotnet test tests/CodeContext.Core.Tests/CodeContext.Core.Tests.csproj --filter "FullyQualifiedName~TypeScriptWorkerProtocolTests"
```

Run the ordinary and tooling-dependent gates:

```bash
dotnet test
dotnet test tests/CodeContext.Core.Tests/CodeContext.Core.Tests.csproj --filter "Category=ExternalTooling"
npm test --prefix sdk/npm
PYTHONPATH=sdk/python/src python -m unittest discover -s sdk/python/tests
git diff --check
```

Run locally or verify a release payload:

```bash
dotnet run --project src/CodeContext.Api -- start --path .
pwsh ./scripts/prepare-node-worker-runtime.ps1 -RuntimeIdentifier win-x64
pwsh ./scripts/verify-publish.ps1 -RuntimeIdentifier win-x64 -ReleaseVersion 1.0.0-dev -PublishDirectory out/win-x64
```

See [DEVELOPMENT.md](DEVELOPMENT.md) for platform-specific Python test syntax and the
complete packaging workflow.

## Practical test-first workflow

1. Use CodeContext and `rg` to map the affected symbol, callers, dependencies, worker
   boundary, and existing tests.
2. Add the smallest test that demonstrates the missing or incorrect behavior. Run it
   and confirm the failure is relevant.
3. Implement the smallest coherent change while maintaining the invariants above.
4. Re-run the focused test. Add boundary/protocol cases when behavior crosses a
   process, generation, serialization, or file-watcher boundary.
5. Run the ordinary full suite. Run `Category=ExternalTooling` for TypeScript worker
   behavior and both SDK suites for shared framing/protocol changes.
6. Refresh a self-hosted index after broad edits, query the changed architecture, and
   cross-check with `rg`.
7. Run `git diff --check` and inspect the diff for contract drift, stale documentation,
   and accidental generated/build artifacts.
