# CodeContext - Local Code Context API for LLMs

## 1. Project Overview

**CodeContext** is a local background service that monitors source code repositories and builds a comprehensive dependency graph. It exposes this graph via REST API to enable LLMs and coding assistants to understand codebases without ingesting entire files.

**Key Value Proposition**: Provide LLMs with surgical precision in understanding code relationships, reducing token usage and improving accuracy.

## 2. Architecture Overview

```
┌─────────────────┐     ┌──────────────┐     ┌─────────────┐
│ File System     │────▶│ AST Parser   │────▶│ Graph DB via CSnakes    │
│ Watcher         │     │ (Extensible) │     │ (Kuzu) │
└─────────────────┘     └──────────────┘     └─────────────┘
                                                     │
                                                     ▼
                                            ┌─────────────┐
                                            │ REST API    │
                                            │ Server      │
                                            └─────────────┘
```

## 3. Core Features

### 3.1 File System Watcher
- Monitor specified directory recursively for file changes
- Debounce rapid changes (e.g., 500ms delay)
- Filter by file extensions: `.cs`, `.js`, `.ts`, `.jsx`, `.tsx`
- Ignore patterns: `node_modules/`, `bin/`, `obj/`, `.git/`
- Use `FileSystemWatcher` with buffer overflow handling

### 3.2 Extensible AST Parser Architecture

**Critical Design Goal**: Easy community contributions for new language parsers.

```csharp
// Example interface for language parsers
public interface ILanguageParser
{
    string[] SupportedExtensions { get; }
    CodeGraph ParseFile(string filePath, string content);
}

public class CodeGraph
{
    public List<CodeNode> Nodes { get; set; }
    public List<CodeEdge> Edges { get; set; }
}
```

**Initial Parsers**:
- **C# Parser**: Use Microsoft.CodeAnalysis.CSharp (Roslyn)
- **JavaScript/TypeScript Parser**: Use Esprima-DotNet or TypeScript.NET

### 3.3 Graph Database Schema

**Nodes** (Code Constructs):
```json
{
  "id": "guid",
  "name": "UserService",
  "type": "Class|Interface|Function|Method|Property|Variable",
  "filePath": "/src/services/UserService.cs",
  "startLine": 10,
  "endLine": 45,
  "namespace": "MyApp.Services",
  "visibility": "public|private|protected",
  "signature": "public class UserService : IUserService"
}
```

**Edges** (Relationships):
```json
{
  "id": "guid",
  "sourceId": "node-guid",
  "targetId": "node-guid",
  "type": "CALLS|INHERITS|IMPLEMENTS|IMPORTS|REFERENCES",
  "metadata": {
    "callSite": "line:15,column:8"
  }
}
```

### 3.4 REST API Design

**Base URL**: `http://localhost:7890`

**Authentication**: None (local only) or optional API key

## 4. API Endpoints

### 4.1 Core Query Endpoints

```yaml
GET /api/definitions/find
  Query Parameters:
    - name: string (required) - Name to search for
    - type: string (optional) - Filter by type (Class, Method, etc.)
    - exact: boolean (default: false) - Exact match vs contains
  Response:
    {
      "results": [
        {
          "id": "guid",
          "name": "UserService",
          "type": "Class",
          "filePath": "/src/services/UserService.cs",
          "startLine": 10,
          "endLine": 45,
          "signature": "public class UserService : IUserService"
        }
      ]
    }

GET /api/relationships/callers/{nodeId}
  Response:
    {
      "callers": [
        {
          "id": "guid",
          "name": "ProcessOrder",
          "filePath": "/src/OrderProcessor.cs",
          "callSites": [
            { "line": 25, "column": 12 }
          ]
        }
      ]
    }

GET /api/dependencies/file
  Query Parameters:
    - path: string (required) - File path
  Response:
    {
      "imports": ["list of imported files"],
      "importedBy": ["list of files importing this one"]
    }

GET /api/impact/analyze/{nodeId}
  Response:
    {
      "directCallers": [...],
      "childClasses": [...],
      "implementations": [...],
      "potentiallyAffected": [...]
    }

GET /api/graph/search
  Body: {
    "query": "MATCH (n:Class)-[:INHERITS]->(m:Class {name: 'BaseController'}) RETURN n"
  }
  Response: Graph query results (Cypher-like syntax)

GET /api/context/complete
  Query Parameters:
    - identifier: string (required) - Name or file path to search for
    - type: string (optional) - Filter by type (Class, Method, File, etc.)
    - depth: int (default: 2) - How many relationship levels to traverse
    - includeTests: boolean (default: true)
    - includeContent: boolean (default: false) - Include file content snippets
  
  Behavior:
    - Without 'type': Searches across all entity types, returns best match or multiple matches
    - With 'type': Searches only within specified type
    - Ambiguity handling: Returns array if multiple exact matches found
  
  Examples:
    GET /api/context/complete?identifier=UserService
    # Returns: Class named UserService (if unique)
    
    GET /api/context/complete?identifier=GetUser
    # Returns: Array of all methods named GetUser across different classes
    
    GET /api/context/complete?identifier=/src/services/UserService.cs
    # Returns: File context (detects path format)
    
    GET /api/context/complete?identifier=GetUser&type=Method
    # Returns: Only methods named GetUser
  
  Response Structure:
    {
      "matches": [  // Array, even for single results
        {
          "target": { /* node details */ },
          "relationships": { /* as before */ },
          "testing": { /* as before */ },
          "metrics": { /* as before */ }
        }
      ],
      "disambiguationHint": "Multiple matches found. Specify 'type' parameter or use more specific identifier"
    }

POST /api/context/multi
  Body: {
    "identifiers": ["UserService", "OrderService", "/src/models/User.cs"],
    "depth": 1,
    "relationshipTypes": ["calls", "implements", "tests"]
  }
  Response: Array of complete context objects
```

### 4.2 Utility Endpoints

```yaml
GET /api/status
  Response: { "indexed": true, "fileCount": 150, "nodeCount": 3200 }

POST /api/index/refresh
  Body: { "path": "/specific/file.cs" }  # Optional, full scan if omitted

GET /api/schema
  Response: OpenAPI 3.0 specification
```

## 5. Technical Implementation Details

### 5.1 Technology Stack

- **Language**: C# with .NET 9
- **Project Type**: Worker Service with CLI support
- **Deployment**: Native AOT compilation
- **Database**: Kuzu via CSnakes and the Python SDK
- **API Framework**: ASP.NET Core Minimal APIs (AOT-compatible)
- **CLI Framework**: System.CommandLine (AOT-compatible)
- **Python Integration**: CSnakes for Python-based parsers
- **Parsers**:
  - C#: Microsoft.CodeAnalysis.CSharp
  - JS/TS: Acorn.NET or Python's esprima via CSnakes
  - Python: Python's ast module via CSnakes
  - Additional: Community-contributed parsers in C# or Python
- **JSON**: System.Text.Json (AOT-friendly)

**Project Setup**:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <AssemblyName>codecontext</AssemblyName>
    <RootNamespace>CodeContext</RootNamespace>
  </PropertyGroup>
  
  <ItemGroup>
    <PackageReference Include="System.CommandLine" Version="2.0.0-beta4.*" />
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="*" />
    <PackageReference Include="CSnakes.Runtime" Version="*" />
  </ItemGroup>
</Project>
```

**Application Architecture**:
- Single executable serving dual purpose: CLI tool and background service
- Commands: `start`, `stop`, `status`, `list`, `refresh`
- Service hosts: FileWatcher, GraphDatabase, and HTTP API
- Background mode uses PID files in `.codecontext/` for process management

### 5.2 AOT Compatibility Checklist

- ✅ Use source generators where possible
- ✅ Avoid reflection-heavy libraries
- ✅ Prefer System.Text.Json over Newtonsoft.Json
- ✅ Test trimming warnings early
- ✅ Consider using `[DynamicallyAccessedMembers]` attributes

**CSnakes + AOT Considerations**:
- **Embedded Python**: Bundle Python runtime with AOT binary using CSnakes' embedded mode
- **Static Registration**: Register Python parsers at compile time rather than dynamic discovery
- **Type Mapping**: Define explicit C#/Python type mappings to avoid runtime reflection
- **Testing**: Verify Python interop works in AOT mode early in development
- **Fallback Strategy**: Consider shipping both C# and Python versions of critical parsers

### 5.3 Performance Considerations

- **Incremental Updates**: Only reparse changed files
- **Lazy Loading**: Don't load full file contents unless needed
- **Caching**: In-memory cache for frequent queries
- **Batch Processing**: Queue file changes and process in batches

**Caching Strategy Guidelines**:
- **Multi-Level Cache**: 
  - L1: In-memory LRU cache for hot paths (complete context objects)
  - L2: Serialized cache in local SQLite for warm restarts
- **Cache Keys**: Use file content hash + parser version for invalidation
- **Granularity**: Cache both individual nodes and complete context responses
- **TTL Strategy**: 
  - Nodes: Invalidate on file change
  - Context responses: Short TTL (5-10 minutes) or invalidate on graph changes
- **Memory Budget**: Configure max cache size, implement eviction policies
- **Pre-warming**: Background process to cache frequently accessed contexts

**Graph Traversal Optimization**:
- **Query Planning**: Analyze query patterns to optimize common traversals
- **Index Strategy**: 
  - Create indexes on name, type, and file path for fast lookups
  - Consider bloom filters for existence checks
- **Depth Limiting**: Implement configurable max depth with sensible defaults
- **Pagination**: For large result sets, support cursor-based pagination
- **Materialized Views**: Pre-compute common relationship patterns
- **Parallel Traversal**: Use concurrent graph walks for independent branches
- **Circuit Breaker**: Prevent runaway queries on large/circular graphs

## 6. Configuration

```json
{
  "CodeContext": {
    "RootPath": "/path/to/repository",  // Overridden by CLI working directory
    "Port": 7890,
    "FilePatterns": ["*.cs", "*.js", "*.ts"],
    "IgnorePatterns": ["node_modules/**", "bin/**", "obj/**"],
    "EnabledParsers": ["CSharp", "JavaScript"],
    "Database": {
      "Path": "./codecontext.db",  // Relative to .codecontext/ folder
      "InMemory": false
    }
  }
}
```

**Path Resolution Priority**:
1. `--path` command line argument
2. Current working directory when `codecontext start` is run
3. `RootPath` in config file (if explicitly set)
4. Error if no valid directory can be determined

## 7. Extension Points

### 7.1 Plugin Architecture

**Dual Language Support**: Parsers can be written in either C# or Python.

```csharp
// C# Parser plugin interface
public interface IParserPlugin
{
    string Name { get; }
    string Version { get; }
    string[] SupportedExtensions { get; }
    Task<CodeGraph> ParseAsync(string filePath, string content);
}

// Python parser wrapper using CSnakes
public class PythonParserAdapter : IParserPlugin
{
    private readonly IPythonEnvironment _python;
    private dynamic _parserModule;
    
    public async Task<CodeGraph> ParseAsync(string filePath, string content)
    {
        // Call Python parser via CSnakes
        var module = await _python.ParserTemplate();
        var result = module.ParseFile(filePath, content);
        return ConvertToCodeGraph(result);
    }
}
```

**Python Parser Template** (`parser_template.py`):
```python
from typing import List, Dict, Any
import tree_sitter
import ast  # For Python parsing
import esprima  # For JavaScript parsing

class ParserPlugin:
    def __init__(self):
        self.name = "PythonASTParser"
        self.version = "1.0.0"
        self.supported_extensions = [".py", ".pyw"]
    
    def parse_file(self, file_path: str, content: str) -> Dict[str, Any]:
        """Parse file and return graph structure"""
        nodes = []
        edges = []
        
        # Use tree-sitter or ast module
        tree = ast.parse(content)
        
        # Extract nodes and relationships
        for node in ast.walk(tree):
            if isinstance(node, ast.ClassDef):
                nodes.append({
                    "id": f"{file_path}:{node.name}",
                    "name": node.name,
                    "type": "Class",
                    "filePath": file_path,
                    "startLine": node.lineno,
                    "endLine": node.end_lineno
                })
        
        return {
            "nodes": nodes,
            "edges": edges
        }

# Entry point for CSnakes
parser = ParserPlugin()
```

### 7.2 Community Parser Template

Provide a NuGet package template: `CodeContext.Parser.Template` with:
- Base interfaces
- Helper utilities
- Test framework
- Sample parser implementation

## 8. Error Handling

- **Graceful Degradation**: If a file can't be parsed, log and continue
- **Parser Errors**: Return partial results with error metadata
- **API Errors**: Consistent error response format:
  ```json
  {
    "error": {
      "code": "PARSER_ERROR",
      "message": "Failed to parse file",
      "details": { "file": "/src/broken.cs", "line": 45 }
    }
  }
  ```

## 9. Testing Strategy

- **Unit Tests**: Parser accuracy for each language
- **Integration Tests**: File watcher → Parser → Database flow
- **Performance Tests**: Large repository handling (10k+ files)
- **API Contract Tests**: Ensure backwards compatibility

## 10. Deployment & Distribution

### 10.1 Primary Usage Pattern
Developers run CodeContext directly from their project root:
```bash
# Navigate to your project
cd /path/to/my-project

# Run CodeContext (monitors current directory by default)
codecontext start

# Or explicitly specify a path
codecontext start --path /different/project

# Run in background with custom name
codecontext start --daemon --name myproject
```

**Directory Detection Logic**:
1. If `--path` is specified, use that directory
2. Otherwise, use current working directory (`Directory.GetCurrentDirectory()`)
3. Create `.codecontext/` folder in the monitored directory

### 10.2 Local Directory Structure
```
my-project/                # <-- This is monitored
├── src/
├── tests/
├── package.json
└── .codecontext/          # Auto-created by CodeContext
    ├── codecontext.db     # Graph database for this project
    ├── config.json        # Project-specific settings
    ├── cache/             # Query cache
    └── logs/              # Application logs
```

### 10.3 Distribution Strategy

**Native Binaries via GitHub Releases**:
```bash
# macOS (Apple Silicon)
curl -L https://github.com/you/codecontext/releases/latest/download/codecontext-osx-arm64 -o codecontext
chmod +x codecontext

# macOS (Intel)
curl -L https://github.com/you/codecontext/releases/latest/download/codecontext-osx-x64 -o codecontext

# Linux
curl -L https://github.com/you/codecontext/releases/latest/download/codecontext-linux-x64 -o codecontext

# Windows
Invoke-WebRequest -Uri https://github.com/you/codecontext/releases/latest/download/codecontext-win-x64.exe -OutFile codecontext.exe
```

**Package Managers**:
```bash
# Homebrew (macOS/Linux)
brew install codecontext

# Scoop (Windows)
scoop install codecontext

# .NET Tool
dotnet tool install --global CodeContext
```

### 10.4 Configuration Discovery
Priority order for configuration (highest to lowest precedence):
1. Command-line arguments (`--port 8080`)
2. Environment variables (`CODECONTEXT_PORT=8080`)
3. `.codecontext/config.json` in current directory
4. `codecontext.json` in project root (for version control)
5. User config: `~/.config/codecontext/config.json` (Linux/macOS) or `%APPDATA%\codecontext\config.json` (Windows)
6. System-wide config: `/etc/codecontext/config.json` (Linux/macOS)
7. Built-in defaults

**Standard .NET Configuration Pattern**:
```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{environment}.json", optional: true)
    .AddJsonFile("/etc/codecontext/config.json", optional: true)
    .AddJsonFile(Path.Combine(userProfile, ".config/codecontext/config.json"), optional: true)
    .AddJsonFile("codecontext.json", optional: true)
    .AddJsonFile(".codecontext/config.json", optional: true)
    .AddEnvironmentVariables(prefix: "CODECONTEXT_")
    .AddCommandLine(args);
```

**Example Override Behavior**:
```bash
# config.json has port: 7890
# But can override at runtime:
codecontext start --port 8080  # Uses 8080

# Or via environment:
CODECONTEXT_PORT=8080 codecontext start  # Uses 8080

# Command line wins over environment:
CODECONTEXT_PORT=8080 codecontext start --port 9000  # Uses 9000
```

### 10.5 Integration Patterns

**VS Code Extension** (future):
```json
{
  "codecontext.autoStart": true,
  "codecontext.port": 7890,
  "codecontext.showStatusBar": true
}
```

**Git Hooks Integration**:
```bash
# .git/hooks/post-checkout
#!/bin/sh
# Refresh CodeContext index after branch switch
codecontext refresh --async
```

**IDE/Editor Integration**:
- Provide plugins that auto-start CodeContext when opening a project
- Status bar indicators showing indexing progress
- Quick access to API endpoints from editor

### 10.6 Multi-Project Support

**Separate Process Per Directory** (Recommended):
Each directory gets its own CodeContext instance with isolated database and configuration.

```bash
# Terminal 1: Frontend project
cd /path/to/frontend
codecontext start --port 7890 --name frontend

# Terminal 2: Backend project
cd /path/to/backend
codecontext start --port 7891 --name backend

# Terminal 3: Shared libraries
cd /path/to/shared-libs
codecontext start --port 7892 --name libs

# List all running instances
codecontext list
# Output:
# NAME       PATH                    PORT   PID    STATUS
# frontend   /path/to/frontend       7890   12345  running
# backend    /path/to/backend        7891   12346  running  
# libs       /path/to/shared-libs    7892   12347  running

# Stop specific instance
codecontext stop backend

# Or stop instance from anywhere using name
cd ~
codecontext stop --name frontend
```

**Instance Registry**:
CodeContext maintains a global registry at `~/.config/codecontext/instances.json`:
```json
{
  "instances": [
    {
      "name": "frontend",
      "path": "/path/to/frontend",
      "port": 7890,
      "pid": 12345,
      "startedAt": "2025-01-15T10:00:00Z"
    }
  ]
}
```

**Benefits of Process Isolation**:
- Each project has its own database and cache
- No cross-project pollution
- Can run different CodeContext versions per project
- Simple mental model: one folder = one process
- Natural cleanup when process ends

### 10.7 Build & Release Process
```yaml
# GitHub Actions workflow
name: Release
on:
  push:
    tags: ['v*']

jobs:
  build:
    strategy:
      matrix:
        include:
          - os: ubuntu-latest
            target: linux-x64
          - os: windows-latest
            target: win-x64
          - os: macos-latest
            target: osx-x64
          - os: macos-latest
            target: osx-arm64
    
    steps:
      - name: Build AOT
        run: |
          dotnet publish -c Release -r ${{ matrix.target }} \
            --self-contained true \
            -p:PublishAot=true \
            -p:PublishSingleFile=true \
            -o ./publish
      
      - name: Upload Release
        uses: actions/upload-release-asset@v1
        with:
          upload_url: ${{ github.event.release.upload_url }}
          asset_path: ./publish/codecontext${{ matrix.target == 'win-x64' && '.exe' || '' }}
          asset_name: codecontext-${{ matrix.target }}${{ matrix.target == 'win-x64' && '.exe' || '' }}
```

### 10.8 Developer Workflow
1. **Install once globally**: Download binary or use package manager
2. **Run in project**: `cd my-project && codecontext start`
3. **Configure if needed**: Edit `.codecontext/config.json`
4. **Use with AI tools**: Point Claude Code, Cursor, etc. to `http://localhost:7890`
5. **Stop when done**: `codecontext stop` or just close terminal

### 10.9 Alternative Deployment Scenarios

**Shared Team Server** (optional):
- Run on dedicated machine for large monorepos
- Configure with authentication enabled
- Access via `http://teamserver:7890`

**CI/CD Integration**:
- Run during builds to generate complexity reports
- Export dependency graphs for documentation
- Validate architectural constraints

## 11. Future Enhancements

- **WebSocket Support**: Real-time updates to connected clients
- **MCP Adapter**: Optional MCP server mode
- **Language Server Protocol**: VSCode integration
- **Distributed Mode**: Multi-repository support
- **AI-Powered Queries**: Natural language to graph queries
- **Auto-Discovery Protocol**: 
  - Well-known port scanning (7890-7899) for running instances
  - DNS-SD/mDNS broadcasting for local network discovery
  - `.codecontext.port` file in project root for explicit declaration
  - Standard discovery endpoint: `GET /api/discovery` returning tool capabilities
  - Integration with AI tool registries (when they emerge)

## 12. Example Usage for LLMs

### 12.1 Traditional Multi-Call Approach
```python
# Multiple API calls to gather context
import requests

base_url = 'http://localhost:7890/api'

# Find definition
definition = requests.get(f'{base_url}/definitions/find?name=UserService').json()
# Get callers
callers = requests.get(f'{base_url}/relationships/callers/{definition["id"]}').json()
# Get dependencies
deps = requests.get(f'{base_url}/dependencies/file?path={definition["filePath"]}').json()
# Find tests
# ... more calls
```

### 12.2 Unified Context Approach (Recommended)
```python
# Single API call for complete context
response = requests.get(
    'http://localhost:7890/api/context/complete',
    params={
        'identifier': 'UserService',
        'depth': 2,
        'includeTests': True
    }
)

context = response.json()

# LLM now has everything in one response:
# - What UserService is (definition)
# - What calls it (usedBy)
# - What it depends on (uses, dependencies)
# - How it's tested (testing)
# - Related types (relatedItems)
# - Code metrics (complexity, etc.)

# This enables prompts like:
prompt = f"""
Given this context about {context['target']['name']}:
- Used by {len(context['relationships']['usedBy'])} other components
- Has {context['testing']['testFiles'][0]['testCount']} tests with {context['testing']['testFiles'][0]['coverage']}% coverage
- Complexity score: {context['metrics']['complexity']}

Should I refactor this class? What are the risks?
"""
```

### 12.3 Benefits of Unified Context Endpoint

1. **Reduced Latency**: Single HTTP round-trip instead of 5-10 calls
2. **Atomic Consistency**: All data from same graph state
3. **Token Efficiency**: LLM gets exactly what it needs
4. **Easier Integration**: Simpler client code for AI tools
5. **Better Caching**: Can cache complete context objects