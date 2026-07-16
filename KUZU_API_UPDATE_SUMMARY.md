# Kuzu API Update Summary

## Overview
Updated the CodeContext Kuzu repository implementations to handle the new response format from the Python Kuzu API that includes `_query_stats` metadata.

## Changes Made

### 1. Created KuzuResponseParser Helper Class
- **File**: `/src/CodeContext.Core/Repositories/Kuzu/KuzuResponseParser.cs`
- **Purpose**: Centralized response parsing logic to handle various response formats
- **Features**:
  - Handles null responses ("null" string)
  - Extracts data from `_query_stats` wrapped responses
  - Handles array responses wrapped in `results` property
  - Parses error responses and throws appropriate exceptions
  - Supports both direct objects and wrapped objects

### 2. Updated Repository Implementations
Updated all Kuzu repository classes to use the new response parser:

- **KuzuNodeRepository.cs**
- **KuzuEdgeRepository.cs**  
- **KuzuFileMetadataRepository.cs**
- **KuzuGraphRepository.cs**

Each repository now uses `KuzuResponseParser.ParseResponse()` instead of direct `JsonSerializer.Deserialize()`.

### 3. Fixed Python Schema Issues
Modified `kuzu_api.py` to simplify complex data types that were causing parsing errors:
- Changed `parameters LIST(STRUCT(name STRING, type STRING))` to `parameters STRING`
- Changed `metrics STRUCT(...)` to `metrics STRING`
- Changed `call_site STRUCT(line INT32, col INT32)` to `call_site STRING`
- Added preprocessing to convert complex objects to JSON strings before insertion
- Added helper functions `_node_to_dict` and `_file_metadata_to_dict` for data conversion

### 4. Added Unit Tests
- **File**: `/tests/CodeContext.Core.Tests/Repositories/Kuzu/KuzuResponseParserTests.cs`
- Comprehensive tests covering all response scenarios
- All tests passing

## Response Format Examples

### Direct Object Response
```json
{
  "id": "test-1",
  "name": "TestClass",
  "_query_stats": {
    "query_time_ms": 5,
    "cache_hit": false
  }
}
```

### Array Response
```json
{
  "results": [
    {"id": "1", "name": "Class1"},
    {"id": "2", "name": "Class2"}
  ],
  "_query_stats": {
    "query_time_ms": 10
  }
}
```

### Error Response
```json
{
  "error": true,
  "error_type": "query_timeout",
  "message": "Query exceeded timeout",
  "suggestions": ["Use more specific filters"]
}
```

## Impact
- All Kuzu repository operations now correctly handle the new response format
- Error responses are properly parsed and thrown as appropriate exceptions
- The code is backward compatible - if responses don't have `_query_stats`, they're parsed normally
- Schema simplified to avoid Kuzu parsing errors with complex types

## Testing
- Unit tests created and passing for the response parser
- Integration with actual Kuzu database would require running the Python environment
- Mocked tests show the repositories work correctly with the new parser