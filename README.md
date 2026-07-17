# CodeContext

CodeContext is a local code-analysis service for coding agents and developer tools. It
indexes a repository into a semantic graph, keeps that graph current while files
change, and exposes symbol relationships through a REST API or an MCP server. Queries
can answer questions such as what a method calls, who calls it, which types implement
an interface, which files depend on another file, and where static test evidence
exists.

The index stays on the local machine. The current host uses an in-memory store, so a
new process performs a fresh scan and leaves no repository database behind.

## Languages and platforms

Bundled language workers provide semantic analysis for:

- C# (`.cs`) through a long-lived Roslyn worker.
- TypeScript and JavaScript (`.ts`, `.tsx`, `.js`, `.jsx`) through a long-lived
  TypeScript language-service worker.

Release archives are published for Windows x64, Linux x64, macOS x64, and macOS
Arm64. The archives are self-contained: they include the .NET host, the C# worker,
Node.js, and the TypeScript worker dependencies. Building from source requires the
local prerequisites in [DEVELOPMENT.md](DEVELOPMENT.md).

## Install a release

On Windows PowerShell:

```powershell
irm https://raw.githubusercontent.com/ben-parker/code-context/main/scripts/install.ps1 | iex
```

On Linux or macOS:

```bash
curl -fsSL https://raw.githubusercontent.com/ben-parker/code-context/main/scripts/install.sh | sh
```

The installer places a versioned release under `~/.codecontext`, adds a stable
`codecontext` launcher, and installs the bundled agent skill. If it updates your user
`PATH`, open a new terminal before continuing. Stop running CodeContext instances
before upgrading.

## Quick start

Start an instance for the current repository in the background:

```bash
codecontext start --detach --path .
```

The command prints JSON containing the allocated localhost port, process ID, root
path, and instance ID. Wait for the index and both relevant parser sessions to become
ready:

```bash
codecontext status --path .
```

Then query the port returned by `start` (7890 is the first port considered):

```bash
curl "http://localhost:7890/api/context/complete?identifier=ContextService"
```

Returned targets include canonical identifiers that can be passed back as
`identifier` without modification. Name searches use exact-first matching with a
substring fallback; optional filters include `type`, `containingType`, `namespace`,
`signature`, and `sourceFile`.

## Instance lifecycle

One registered instance watches each repository root. Ports are allocated from 7890
unless `--port` is supplied.

```bash
codecontext start --path .                  # foreground REST service
codecontext start --detach --path .         # background REST service
codecontext start --path . --port 8080      # fixed port
codecontext list                            # human-readable instances
codecontext list --json                     # machine-readable instances
codecontext status --path .                 # status for the containing instance
codecontext stop --path .                   # stop that instance
codecontext stop --all                      # stop every registered instance
```

Instances shut down after 120 minutes without API activity by default. Set
`--idle-timeout 0` to disable idle shutdown or pass another number of minutes.

## REST API

The main endpoints are:

- `GET /api/status` for indexing, parser, graph, watcher, and contract status.
- `GET /api/context/complete` for one symbol or source file.
- `POST /api/context/multi` for several context queries in one round trip.
- `POST /api/syntax-tree` for an optional parser-native syntax tree.
- `POST /api/index/refresh` for a full or single-file refresh.
- `GET /api/schema` for the generated OpenAPI document.
- `GET /healthz` for process liveness.

Compact context is the default and omits content, metrics, related items, and tests
unless requested. Use `view=full` for parser/debug details, and inspect each returned
count and truncation marker before treating an omitted relationship as absent.

The checked-in [OpenAPI specification](codecontext.openapi.json) documents request and
response shapes. The installed service also serves its generated schema at runtime.

## MCP server

MCP mode uses stdio and the same context and status services as REST mode:

```bash
codecontext start --mcp --path /absolute/path/to/repository
```

A typical MCP client configuration is:

```json
{
  "mcpServers": {
    "codecontext": {
      "command": "codecontext",
      "args": ["start", "--mcp", "--path", "/absolute/path/to/repository"]
    }
  }
}
```

The MCP tools are `GetContext`, `GetMultiContext`, and `GetStatus`. MCP mode is owned
by the client process and does not register a REST instance.

## Architecture

The API/MCP host discovers language-worker manifests, supervises one private process
per worker, and sends workspace changes over JSON-RPC 2.0 with Content-Length framing.
Workers retain compiler state and stream normalized analysis deltas. The host validates
worker ownership and commits each generation atomically to the in-memory graph.

`IndexCoordinator` serializes startup scans, explicit refreshes, and coalesced watcher
changes through one writer queue. `ContextService` resolves identities and assembles
the compact or full relationship response consumed by both public transports. Roslyn
and the TypeScript compiler stay outside the host process.

Durable technical references:

- [Development and contribution guide](DEVELOPMENT.md)
- [Repository agent guide](AGENTS.md)
- [Language-worker SDK guide](docs/language-worker-sdk.md)
- [.NET parser protocol package](src/CodeContext.Parser.Protocol/README.md)
- [Canonical parser protocol schema](src/CodeContext.Parser.Protocol/protocol/parser-protocol.schema.json)
- [npm worker SDK](sdk/npm/README.md) and [Python worker SDK](sdk/python/README.md)
- [Agent skill](skill/SKILL.md) and [native syntax-tree reference](skill/references/native-syntax.md)
