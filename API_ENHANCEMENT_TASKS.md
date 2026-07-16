# CodeContext API Enhancement Tasks

## Current Status
Working on enhancing the `/api/context/complete` endpoint to provide more valuable information for LLMs.

## Task List (Priority Order)

### 🔥 High Priority

#### 1. **Debug Relationship Arrays** ✅ COMPLETED
- **Status**: ✅ Complete
- **Context**: `uses`/`usedBy` arrays are empty in API responses
- **Issue**: ~~Need to determine if C# parser creates edges OR if edge repository queries fail~~ **SOLVED**
- **Root Cause**: API uses Kuzu database while relationships work with InMemory database
- **Finding**: Parser creates edges correctly, issue is in database layer (Kuzu vs InMemory)

#### 2. **Fix Call Relationships** ✅ ROOT CAUSE IDENTIFIED
- **Status**: 🟡 Single-file compilation limitation
- **Context**: Limited cross-file dependencies shown in API responses
- **Root Cause CONFIRMED** (2025-07-12):
  - ✅ **Database Layer Works**: Kuzu database stores and retrieves edges correctly (47 edges found)
  - ✅ **Parser Logic Works**: Edge creation code is correct in `CSharpGraphWalker`
  - ✅ **Test Proof**: Added unit tests that demonstrate the exact issue
    - `ParseFile_ShouldCreateImplementsEdgeForCSharpParserToILanguageParser()` - ❌ FAILS (as expected)
    - `ParseFile_ShouldCreateImplementsEdgeWhenInterfaceIsInSameCompilation()` - ✅ PASSES (proves parser works)
  - ❌ **Single-File Limitation**: Parser creates compilation with only one syntax tree
  - ❌ **Missing Dependencies**: `ILanguageParser` not available when parsing `CSharpParser.cs`
  - ❌ **Semantic Model Scope**: `symbol.AllInterfaces` returns empty for cross-file relationships
- **Technical Issue**: 
  - Lines 16-18 in `CSharpParser.cs` create `CSharpCompilation` with single syntax tree
  - Interface definitions in separate files are not available to semantic model
  - Cross-file relationships cannot be resolved without multi-file compilation context
- **Solution Options**:
  - A) **Multi-file compilation**: Include related files in compilation context
  - B) **Project-wide parsing**: Use full project compilation instead of single files
  - C) **Post-processing**: Add missing relationships after initial parsing
  - D) **Incremental compilation**: Build up compilation context as files are processed

#### 3. **Implement File-Level Dependencies** ✅ COMPLETED
- **Status**: ✅ Complete
- **Context**: Enhanced using statement parsing with Roslyn for better accuracy
- **Achievement**: 
  - ✅ Replaced regex-based parsing with Roslyn AST parsing
  - ✅ Added namespace-to-file mapping functionality
  - ✅ Implemented cross-file dependency detection
  - ✅ Added fallback regex parsing for robustness
- **Technical**: Enhanced `GetFileDependenciesAsync()` and `GetFileDependentsAsync()` methods

### 🟡 Medium Priority

#### 4. **Improve Test Method Detection** ✅ COMPLETED
- **Status**: ✅ Complete
- **Context**: Enhanced test method detection with sophisticated naming patterns
- **Achievement**:
  - ✅ Added comprehensive test method naming pattern recognition
  - ✅ Support for BDD-style test names (Should_*, Given_*, When_*, Then_*)
  - ✅ Support for Fact/Theory patterns (Can*, Should*)
  - ✅ Test attribute detection ([Test], [Fact], [Theory], [TestMethod])
  - ✅ Behavioral patterns (Class_Method_Behavior)
- **Technical**: Enhanced `GetTestMethodsForTargetAsync()` and `IsTestMethodForTarget()` methods

### 🟢 Low Priority

#### 5. **Add Documentation Comments**
- **Status**: 🔴 Pending
- **Context**: No XML documentation or code comments in responses
- **Goal**: Include doc comments and inline comments in responses
- **Technical**: Extend C# parser to capture documentation comments

#### 6. **Add Usage Examples**
- **Status**: 🔴 Pending
- **Context**: No examples of how methods/classes are used
- **Goal**: Show common usage patterns and example calls
- **Technical**: Analyze call sites to generate usage patterns

#### 7. **Add Performance Metrics**
- **Status**: 🔴 Pending
- **Context**: No performance or usage data available
- **Goal**: Show call frequency, performance characteristics
- **Technical**: Add instrumentation and analysis capabilities

## Current Investigation ✅ COMPLETED

### API Testing Results
Using `curl "http://localhost:7890/api/context/complete?identifier=CSharpParser&type=Class"`:

- ✅ **Content Snippets**: Working correctly with `includeContent=true`
- ❌ **Relationships**: All arrays empty (`uses`, `usedBy`, `dependencies`, `dependedBy`)
- ✅ **Related Items**: Working correctly (shows related classes/methods)
- ❌ **Test Methods**: Found test files but `testMethods` arrays empty

### Root Cause Analysis ✅ IDENTIFIED

**Problem**: The API uses KuzuRepositoryFactory (database-backed) while relationships work correctly with InMemoryRepositoryFactory.

**Evidence**:
1. ✅ InMemory test shows edges ARE created and stored correctly
2. ✅ Method-level relationships work: `AnotherMethod` -> `MyMethod` CALLS edge
3. ❌ Class-level relationships don't work: `TestClass` -> `MyBaseClass` INHERITS edge
4. ✅ Parser creates all expected edges (INHERITS, IMPLEMENTS, CALLS)

**Issue**: The Kuzu database used by the API is either:
- Empty (no data processed)
- Has a bug in the edge repository implementation
- Not being populated with edges during file processing

### C# Parser Analysis ✅ CONFIRMED WORKING
File: `/home/ben/source/repos/code-context/src/CodeContext.Core/CSharpParser.cs`

The `CSharpGraphWalker` DOES create edges correctly:
- Lines 61-71: Creates `INHERITS` edges for base classes
- Lines 73-83: Creates `IMPLEMENTS` edges for interfaces  
- Lines 143-155: Creates `CALLS` edges for method invocations

**Key Finding**: Parser logic is correct, issue is in database layer.

## Next Steps

1. Add logging to `BuildRelationshipsAsync()` to see if edges exist in database
2. Check if node IDs match between nodes and edges
3. Verify edge repository implementation is working
4. Test with a simple class that inherits/implements to see if edges appear

## API Usage Examples

```bash
# Test basic functionality
curl -s "http://localhost:7890/api/context/complete?identifier=CSharpParser&type=Class" | jq '.matches[0].relationships'

# Test with content
curl -s "http://localhost:7890/api/context/complete?identifier=CSharpParser&type=Class&includeContent=true" | jq '.matches[0].content'

# Test file path query
curl -s "http://localhost:7890/api/context/complete?identifier=/home/ben/source/repos/code-context/src/CodeContext.Core/CSharpParser.cs"
```

## Success Criteria

- [ ] API returns populated `uses`/`usedBy` arrays showing actual call relationships
- [ ] File-level dependencies show using statements and imports  
- [ ] Test method detection identifies specific test methods
- [ ] Much more valuable for LLMs to understand code architecture and impact analysis

---

*Last Updated: 2025-07-12 (Root cause confirmed with unit tests)*
*API Running: http://localhost:7890*