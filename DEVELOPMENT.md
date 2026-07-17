# Developing CodeContext

## Prerequisites

- .NET 9 SDK.
- Node.js 18 or newer; Node.js 22 is the release/CI version.
- npm (included with Node.js).
- Git.
- Python 3.10 or newer only when testing the Python worker SDK.
- PowerShell 7 when running the release packaging scripts.

The application itself has no Python dependency. Release archives bundle Node.js;
source builds use `node` from `PATH`.

## Set up dependencies

From the repository root:

```bash
npm ci --prefix src/CodeContext.TypeScript.Worker
dotnet restore
```

The npm step is required before building or running the TypeScript worker from the
source tree. Use `npm ci`, not `npm install`, so `package-lock.json` remains the source
of truth.

## Build and test

```bash
dotnet build
dotnet test
```

The test project defaults to `Category!=ExternalTooling`, so `dotnet test` is the
ordinary deterministic gate. Run a focused class or method with xUnit's fully
qualified name filter:

```bash
dotnet test tests/CodeContext.Core.Tests/CodeContext.Core.Tests.csproj --filter "FullyQualifiedName~ContextServiceTests"
dotnet test tests/CodeContext.Core.Tests/CodeContext.Core.Tests.csproj --filter "FullyQualifiedName~CSharpWorkerProtocolFixtureTests.InitialIndex_CommitsCrossFileRelationshipsThroughProtocol"
```

The external-tooling category currently covers the real TypeScript worker and
requires Node.js plus the installed worker dependencies:

```bash
npm ci --prefix src/CodeContext.TypeScript.Worker
dotnet test tests/CodeContext.Core.Tests/CodeContext.Core.Tests.csproj --filter "Category=ExternalTooling"
```

Test the standalone worker SDKs as well:

```bash
npm test --prefix sdk/npm
PYTHONPATH=sdk/python/src python -m unittest discover -s sdk/python/tests
```

PowerShell uses this equivalent Python command:

```powershell
$env:PYTHONPATH = 'sdk/python/src'
python -m unittest discover -s sdk/python/tests
```

## Run locally

Run a foreground REST instance against any repository:

```bash
dotnet run --project src/CodeContext.Api -- start --path .
```

The host selects the first free port from 7890. In another terminal, inspect it with
the built application or query the reported port directly:

```bash
dotnet run --project src/CodeContext.Api -- list --json
dotnet run --project src/CodeContext.Api -- status --path .
curl "http://localhost:7890/api/status"
```

Use Ctrl+C to stop a foreground instance. The remaining lifecycle commands and MCP
mode work from source too:

```bash
dotnet run --project src/CodeContext.Api -- start --detach --path .
dotnet run --project src/CodeContext.Api -- stop --path .
dotnet run --project src/CodeContext.Api -- start --mcp --path .
```

After changing host or worker code, stop and restart the running instance. After a
large source rewrite that does not require a restart, request `POST
/api/index/refresh` and poll `/api/status` until the returned operation has completed.

## Packaging checks

Release archives support `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64`. Packaging
first downloads and verifies the target Node.js runtime, then publishes and smoke-tests
the self-contained host. The publish directory must be absent or empty.

```powershell
./scripts/prepare-node-worker-runtime.ps1 -RuntimeIdentifier win-x64
npm ci --prefix src/CodeContext.TypeScript.Worker
./scripts/verify-publish.ps1 `
  -RuntimeIdentifier win-x64 `
  -ReleaseVersion 1.0.0-dev `
  -PublishDirectory out/win-x64
```

Use the RID for the machine running the smoke test. The verifier checks the packaged
skill, both workers, protocol version, indexing readiness, semantic graph results,
and authenticated shutdown. The release workflow in
[`.github/workflows/release.yml`](.github/workflows/release.yml) is the canonical
cross-platform packaging matrix.

The independently distributed protocol and helper packages can be checked with:

```bash
dotnet pack src/CodeContext.Parser.Protocol -c Release -o out/sdk
npm pack ./sdk/npm --pack-destination out/sdk
python -m pip install build
python -m build sdk/python --outdir out/sdk
```

## Contribution workflow

1. Investigate the symbol, its callers/dependencies, and nearby tests. Use a healthy
   local CodeContext index for semantic relationships and `rg` for literal text and
   configuration.
2. Add or adjust the smallest focused test and confirm it fails for the intended
   reason.
3. Implement the change while preserving the architecture and protocol invariants in
   [AGENTS.md](AGENTS.md).
4. Run the focused test, then `dotnet test`, the external-tooling category when worker
   behavior is affected, and the SDK tests when their code or contract changes.
5. Run `git diff --check` and review the final diff for generated-contract updates,
   accidental build output, and unrelated changes.

Use xUnit and NSubstitute in the .NET tests. Keep orchestration and policy in services;
domain models should own only their local state transitions. Do not commit `bin/`,
`obj/`, `node_modules/`, virtual environments, runtime indexes, or release output.
