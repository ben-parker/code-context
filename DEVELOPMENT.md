# CodeContext Development Guide

This guide covers the development workflow for the CodeContext API, including server management and testing procedures.

## Prerequisites

- **.NET 9.0 SDK** or later
- **Node.js** (required when building/running the TypeScript worker from source;
  release archives bundle it under `workers/typescript/`)
  - Install from [nodejs.org](https://nodejs.org/) or using package manager
  - Version 18.x or later recommended
  - Run `npm install` once in `src/CodeContext.TypeScript.Worker/`
- **Git** for version control

## Quick Start

```bash
# Start the API server
./manage-server.sh start

# In another terminal, run tests
dotnet test

# Check API health
./manage-server.sh health

# Stop the server when done
./manage-server.sh stop
```

## Server Management

The `manage-server.sh` script provides comprehensive server management for development:

### Basic Commands

```bash
./manage-server.sh start           # Start server (foreground)
./manage-server.sh stop            # Stop server
./manage-server.sh restart         # Restart server
./manage-server.sh status          # Check if running
./manage-server.sh health          # Test API endpoints
./manage-server.sh logs            # View server logs
./manage-server.sh guidance        # Show restart guidance
```

### Advanced Usage

```bash
# Run server in background
./manage-server.sh start --background

# Use custom port
./manage-server.sh start --port 8080

# Monitor different path
./manage-server.sh start --path /other/project

# Verbose output for debugging
./manage-server.sh start --verbose
```

## Development Workflow

### 1. Making Code Changes

When you modify C# code, follow this workflow:

```bash
# 1. Make your changes
vim src/CodeContext.Core/Services/ContextService.cs

# 2. Run relevant tests
dotnet test tests/CodeContext.Core.Tests/ --filter "ContextService"

# 3. Restart server to pick up changes
./manage-server.sh restart

# 4. Test the API
./manage-server.sh health
curl -s "http://localhost:7890/api/status" | jq
```

### 2. When to Restart the Server

The script includes guidance (`./manage-server.sh guidance`), but here's a quick reference:

**Always restart for:**
- Any `.cs` file changes in `src/`
- Configuration changes
- NuGet package updates
- Build-related changes

**Never restart for:**
- Test file changes (unless testing API directly)
- Documentation changes
- Script/tool changes

### 3. API Testing

#### Direct API Access

The server provides several endpoints for testing:

```bash
# Basic health check
curl "http://localhost:7890/api/status"

# Test context API
curl "http://localhost:7890/api/context/complete?identifier=CSharpParser&type=Class"

# Test with specific parameters
curl "http://localhost:7890/api/context/complete?identifier=GetUser&type=Method&depth=1"
```

#### Using the API Proxy Script

For easier API access without permission requirements, use the `api-proxy.sh` script:

```bash
# Get API status
./api-proxy.sh status

# Query with parameters
./api-proxy.sh "context/complete?identifier=ContextService&type=Class"

# Pipe to jq for processing
./api-proxy.sh "context/complete?identifier=ContextService" | jq '.matches[0].target'

# POST requests
./api-proxy.sh "context/multi" -X POST -H "Content-Type: application/json" -d '{"identifiers":["Class1","Class2"]}'
```

#### Using the Query Wrapper

For common queries, use the `api-query.sh` convenience wrapper:

```bash
# Find a class
./api-query.sh class ContextService

# Find methods by name
./api-query.sh method GetUser

# Find what uses an identifier
./api-query.sh used-by ContextService

# Find tests for an identifier
./api-query.sh tests UserService

# Get raw JSON for custom processing
./api-query.sh class ContextService --raw | jq '.matches[0].relationships'

# Just count results
./api-query.sh method GetUser --count
```

## Development Tips

### Background Development Mode

For continuous development, run the server in background mode:

```bash
# Start in background
./manage-server.sh start --background

# Make changes and restart when needed
./manage-server.sh restart

# Check logs if needed
./manage-server.sh logs

# Stop when done
./manage-server.sh stop
```

### Multiple Server Instances

You can run multiple instances for testing:

```bash
# Terminal 1: Development server
./manage-server.sh start --port 7890

# Terminal 2: Test server with different config
./manage-server.sh start --port 7891 --path /test/project
```

### Debugging

If the server doesn't start or behaves unexpectedly:

```bash
# Check status and logs
./manage-server.sh status
./manage-server.sh logs

# Try verbose mode
./manage-server.sh start --verbose

# Test API health
./manage-server.sh health

# Check for port conflicts
lsof -i :7890
```

### Integration with Development Tools

The script integrates well with common development workflows:

```bash
# Git workflow
git checkout feature-branch
./manage-server.sh restart     # Pick up new code
# ... make changes ...
./manage-server.sh restart     # Test changes
git commit -m "Add new feature"

# Testing workflow
./manage-server.sh start --background
dotnet test --watch            # Continuous testing
# ... make changes, tests auto-run ...
./manage-server.sh restart     # When ready to test API
```

## Common Issues

### Server Won't Start

1. **Port in use**: Try a different port with `--port`
2. **Build errors**: Check the build output, fix compilation errors
3. **Permission issues**: Ensure the script is executable (`chmod +x manage-server.sh`)
4. **Development Node.js missing**: source-tree builds use Node.js from PATH; release archives bundle it. A launch failure appears on the `typescript` session in `/api/status` (C# indexing is unaffected)

### API Not Responding

1. **Check if running**: `./manage-server.sh status`
2. **Test health**: `./manage-server.sh health`
3. **Check logs**: `./manage-server.sh logs`
4. **Verify port**: Default is 7890, check if using custom port

### File Watching Issues

1. **Path permissions**: Ensure the watched path is readable
2. **File system limits**: On Linux, you may need to increase inotify limits
3. **Network paths**: File watching may not work on network mounted directories

### TypeScript/JavaScript Parsing Issues

1. **Node.js not found in a source build**: ensure Node.js is installed and available in PATH (release archives bundle it)
   ```bash
   node --version  # Should show version number
   ```
2. **Worker dependencies missing**: the TypeScript worker requires the `typescript` npm package next to `typescript-worker.js`
   ```bash
   cd src/CodeContext.TypeScript.Worker
   npm install  # Install TypeScript compiler dependency
   ```
3. **Empty results**: check `/api/status` first — `parsers.sessions[].state` for `typescript` distinguishes "not indexed" (`failed`/`unavailable`, with `lastError`) from "no references" (`ready`). Worker stderr lands in the host log with a `[typescript stderr]` prefix

## Performance Considerations

### Memory Usage

Monitor server memory usage:

```bash
./manage-server.sh status    # Shows memory usage
```

### File Watching

The server monitors file changes automatically, but large repositories may impact performance:

- Consider using `.gitignore` patterns to exclude unnecessary files
- Monitor memory usage during development
- Restart the server if memory usage grows excessively

## API Development

When developing new API endpoints:

1. **Add endpoint** in `src/CodeContext.Api/Program.cs`
2. **Restart server** to pick up changes
3. **Test endpoint** with curl or HTTP client
4. **Add tests** in `tests/CodeContext.Core.Tests/Api/`
5. **Document endpoint** in API documentation

## Continuous Integration

The development workflow integrates with CI/CD:

```bash
# Local CI simulation
./manage-server.sh stop                    # Ensure clean state
dotnet clean                               # Clean build artifacts
dotnet build                               # Build project
dotnet test                                # Run all tests
./manage-server.sh start --background      # Start server
./manage-server.sh health                  # Verify API works
# ... run integration tests ...
./manage-server.sh stop                    # Clean shutdown
```

This mirrors the CI/CD pipeline and helps catch issues early.

### Quarantined tests (`Category=ExternalTooling`)

Tests that depend on tooling the CI runners do not reliably provide carry an explicit
xUnit trait instead of being excluded by name filters:

```csharp
[Trait("Category", "ExternalTooling")]
```

Currently quarantined:

- `TypeScriptWorkerProtocolTests` — require Node.js on PATH and an npm-installed
  `src/CodeContext.TypeScript.Worker`.
- `CSnakesIntegrationTests` — require the CSnakes Python provisioning used by the
  opt-in Kuzu backend.

The mocked `Repositories/Kuzu/*Tests` are ordinary unit tests and remain in the default
gate; they do not provision Python.

The test project supplies a default VSTest filter so the ordinary `dotnet test` command
excludes this category on a clean checkout while an explicit `--filter` overrides it.
`CSnakesIntegrationTests` also carry
`Category=KuzuIntegration`; the dedicated CI job provisions Python 3.12 and the local
Kuzu virtual environment, then runs that category alone. CPython-backed tests share a
non-parallel collection because CPython state is process-global.

The release gate runs the ordinary suite plus `TypeScriptWorkerProtocolTests` after
installing Node dependencies. Run the quarantined set locally with:

```bash
dotnet test --filter "Category=ExternalTooling"
```

Run only the provisioned Kuzu suite with:

```bash
dotnet test --filter "Category=KuzuIntegration"
```

### Repository file selection

Initial scans, resumable/full refreshes, explicit single-file refreshes, and watcher
events use the same project-local `.gitignore` matcher. Root and nested ignore files
support ordered rules, negation, anchoring, directory-only patterns, escaped leading
markers, and normalized separators. A changed `.gitignore` triggers an atomic full
generation so newly ignored facts are pruned and newly included files are parsed.

For safety, `.git/`, `.codecontext/`, configured runtime/build directories (including
`node_modules/`, `bin/`, `obj/`, and `.venv/`), inaccessible/system paths, and reparse
points are never traversed and cannot be re-included with a negated project rule.
