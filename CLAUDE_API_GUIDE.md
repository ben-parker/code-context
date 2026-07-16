# CodeContext API Usage Guide for Claude

## Overview

The CodeContext API provides a powerful way to understand code relationships and dependencies in this codebase. **Important**: The codebase you're working in IS the source code for this API itself! If the API returns unexpected results, you can examine the implementation directly in `src/CodeContext.Core/Services/ContextService.cs` and related files to understand the behavior or fix bugs.

## When to Use the API vs Read Tool

### Use the API when:
- **Finding code relationships**: "What calls this method?", "What does this class inherit from?"
- **Understanding impact**: "What would be affected if I change this class?"
- **Discovering tests**: "What tests cover this component?"
- **Exploring dependencies**: "What files depend on this namespace?"
- **Getting metrics**: Complexity scores, lines of code, dependency counts
- **Batch queries**: Getting context for multiple identifiers at once

### Use the Read tool when:
- **Reading specific files**: You know exactly which file to examine
- **Viewing implementation details**: Need to see the actual code
- **Making edits**: The API is read-only; use Read → Edit for changes
- **Debugging API behavior**: Read the API's own source code to understand results

## Available Endpoints

### 1. `/api/status`
Check if the API is healthy and see indexing statistics.
```bash
./api-proxy.sh status
```

### 2. `/api/context/complete`
Get comprehensive information about any code construct (class, method, interface, etc.).

**Parameters:**
- `identifier`: Returned canonical identifier, name, or file path
- `type`: Filter by type (Class, Method, Interface, etc.) - optional
- `depth`: Relationship traversal depth (default: 1)
- `includeTests`: Include test information (default: false)
- `includeContent`: Include code snippets (default: false)
- `exact`: `true` for exact-only or `false` for substring; omitted uses exact-first fallback
- `view`: `compact` (default) or `full`
- `includeRelated`: Include same-file/namespace suggestions (default: false)
- `includeMetrics`: Include heuristic metrics (default: false)
- `maxMatches`: Ambiguous candidate cap (default: 5)
- `maxRelationships`: Per-relationship cap (default: 10)
- `maxCallSites`: Per-caller location cap (default: 3; zero is count-only)
- `maxTestFiles`: Test-file cap (default: 5; zero is count-only)
- `maxTestMethods`: Test-method cap per file (default: 5; zero is count-only)
- `expandAmbiguous`: Expand capped ambiguous matches instead of summaries (default: false)

**Examples:**
```bash
# Find a class with all its relationships
./api-proxy.sh "context/complete?identifier=ContextService&type=Class" | jq

# Find exact match only (avoid "ContextServiceTests")
./api-proxy.sh "context/complete?identifier=ContextService&type=Class&exact=true" | jq

# Find all methods named "GetUser"
./api-proxy.sh "context/complete?identifier=GetUser&type=Method" | jq

# Get deep relationships
./api-proxy.sh "context/complete?identifier=IRepository&depth=3" | jq

# Request parser/debug-oriented details explicitly
./api-proxy.sh "context/complete?identifier=ContextService&type=Class&view=full&includeTests=true&includeRelated=true&includeMetrics=true" | jq
```

Ambiguous compact queries return ranked symbol summaries with canonical identifiers.
Pass a returned `target.identifier` back unchanged to select it.

### 3. `/api/context/multi` (POST)
Get context for multiple identifiers in one request.
This reduces HTTP round trips; it does not compress response tokens.
Multi-context defaults to three entries per relationship list; raise
`maxRelationships` explicitly when a wider batch is worth the response cost.

```bash
./api-proxy.sh "context/multi" -X POST -H "Content-Type: application/json" \
  -d '{"identifiers":["ContextService","GraphUpdateService"],"type":"Class","depth":1}' | jq
```

### 4. `/api/index/refresh` (POST)
Refresh the index for a specific file after changes.

```bash
./api-proxy.sh "index/refresh" -X POST -H "Content-Type: application/json" \
  -d '{"path":"/src/CodeContext.Core/Services/ContextService.cs"}' | jq
```

## Using the Proxy Scripts

### Low-Level API Access (`api-proxy.sh`)
Direct access to any endpoint with curl options:

```bash
# Basic GET request
./api-proxy.sh "context/complete?identifier=UserService"

# With jq processing
./api-proxy.sh "context/complete?identifier=UserService" | jq '.matches[0].relationships'

# POST request with data
./api-proxy.sh "context/multi" -X POST -H "Content-Type: application/json" -d '{...}'

# Custom timeout
CODECONTEXT_TIMEOUT=60 ./api-proxy.sh "context/complete?identifier=ComplexQuery"
```

### High-Level Queries (`api-query.sh`)
Convenient wrappers for common queries:

```bash
# Find a specific class
./api-query.sh class ContextService

# Find methods by name
./api-query.sh method GetCompleteContextAsync

# What does this identifier use?
./api-query.sh uses ContextService

# What uses this identifier?
./api-query.sh used-by IRepository

# Find tests for a component
./api-query.sh tests GraphUpdateService

# Get all information about an identifier
./api-query.sh all FileWatcherService

# Get raw JSON for custom processing
./api-query.sh class ContextService --raw | jq '.matches[0].target'

# Just count results
./api-query.sh method Parse --count
```

## Server Lifecycle Management

### Starting the Server
```bash
# Start in background (recommended for development)
./manage-server.sh start --background

# Start in foreground (for debugging)
./manage-server.sh start

# Start on different port
./manage-server.sh start --background --port 8080
```

### Managing the Server
```bash
# Check if running
./manage-server.sh status

# Test API health
./manage-server.sh health

# View server logs
./manage-server.sh logs

# Stop the server
./manage-server.sh stop
```

### When to Restart the Server

**Always restart after changing:**
- Any `.cs` files in `src/CodeContext.Core/` (business logic)
- Any `.cs` files in `src/CodeContext.Api/` (API endpoints)
- `appsettings.json` or configuration files
- Repository implementations
- Service implementations
- Domain models (CodeNode, CodeEdge, etc.)

**No restart needed for:**
- Test files (unless testing the API directly)
- Documentation files (.md)
- Script files (.sh)
- Comments in code

**Quick restart:**
```bash
./manage-server.sh restart
```

## Debugging API Behavior

Since you're working on the API's own codebase, you can debug unexpected behavior directly:

1. **Check the implementation**:
   ```bash
   # The main context service
   cat src/CodeContext.Core/Services/ContextService.cs
   
   # API endpoint definitions
   cat src/CodeContext.Api/Program.cs
   ```

2. **Understand the data flow**:
   - API receives request → `Program.cs` routes to endpoint
   - Endpoint calls `IContextService` methods
   - `ContextService` queries repositories
   - Repositories fetch from database (currently InMemory)

3. **Common debugging locations**:
   - Empty relationships? Check `BuildRelationshipsAsync()` in `ContextService.cs`
   - Wrong results? Check `FindByNameAsync()` logic
   - Missing data? Verify the file was parsed (check logs)

4. **Fix and test**:
   ```bash
   # Make the fix
   vim src/CodeContext.Core/Services/ContextService.cs
   
   # Restart server
   ./manage-server.sh restart
   
   # Test the fix
   ./api-query.sh class YourTestCase
   ```

## Example Workflow

```bash
# Start your development session
./manage-server.sh start --background

# Explore the codebase structure
./api-query.sh class ContextService
./api-query.sh uses ContextService

# Find related components
./api-proxy.sh "context/complete?identifier=IContextService" | jq '.matches[0].relationships.usedBy'

# Discover tests
./api-query.sh tests ContextService

# Make changes to improve the API
vim src/CodeContext.Core/Services/ContextService.cs

# Restart to test your changes
./manage-server.sh restart

# Verify your improvements
./api-query.sh class ContextService

# Stop when done
./manage-server.sh stop
```

## Tips

1. **Use the API for navigation**: Instead of grep/find, use the API to understand code structure
2. **Combine with Read**: Use API to find relevant files, then Read for implementation details
3. **Check logs for issues**: `./manage-server.sh logs` shows parsing errors and processing details
4. **The API is your assistant**: It helps you understand the very codebase you're improving

Remember: The API is a tool you're both using AND improving. If it doesn't work as expected, you have full access to fix it!
