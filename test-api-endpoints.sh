#!/bin/bash

# CodeContext API Endpoint Test Suite
# Comprehensive testing script to validate API functionality and AOT compatibility

set -euo pipefail

# Configuration
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
API_HOST="${CODECONTEXT_HOST:-localhost}"
API_PORT="${CODECONTEXT_PORT:-7890}"
API_TIMEOUT="${CODECONTEXT_TIMEOUT:-30}"
BASE_URL="http://${API_HOST}:${API_PORT}/api"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Test counters
TOTAL_TESTS=0
PASSED_TESTS=0
FAILED_TESTS=0

# Function to print colored output
print_status() {
    local color=$1
    local message=$2
    echo -e "${color}${message}${NC}"
}

# Function to log test results
log_test() {
    local test_name=$1
    local result=$2
    local details=${3:-""}
    
    TOTAL_TESTS=$((TOTAL_TESTS + 1))
    
    if [[ "$result" == "PASS" ]]; then
        PASSED_TESTS=$((PASSED_TESTS + 1))
        print_status "$GREEN" "✓ $test_name"
        [[ -n "$details" ]] && echo "  $details"
    else
        FAILED_TESTS=$((FAILED_TESTS + 1))
        print_status "$RED" "✗ $test_name"
        [[ -n "$details" ]] && echo "  $details"
    fi
}

# Function to make API request with error handling
api_request() {
    local endpoint=$1
    local method=${2:-GET}
    local data=${3:-""}
    local expected_status=${4:-200}
    
    local curl_cmd="curl -s -w '%{http_code}' --connect-timeout 5 --max-time $API_TIMEOUT"
    
    if [[ "$method" == "POST" ]] && [[ -n "$data" ]]; then
        curl_cmd="$curl_cmd -X POST -H 'Content-Type: application/json' -d '$data'"
    fi
    
    local response
    response=$(eval "$curl_cmd '$BASE_URL/$endpoint'" 2>/dev/null || echo "ERROR")
    
    if [[ "$response" == "ERROR" ]]; then
        echo "ERROR: Failed to connect to API"
        return 1
    fi
    
    # Extract status code (last 3 characters)
    local status_code="${response: -3}"
    local body="${response%???}"
    
    if [[ "$status_code" != "$expected_status" ]]; then
        echo "ERROR: Expected status $expected_status, got $status_code"
        return 1
    fi
    
    echo "$body"
}

# Function to validate JSON structure
validate_json() {
    local json=$1
    local schema_check=$2
    
    # Check if valid JSON
    if ! echo "$json" | jq . >/dev/null 2>&1; then
        echo "ERROR: Invalid JSON"
        return 1
    fi
    
    # Apply schema check
    if ! echo "$json" | jq -e "$schema_check" >/dev/null 2>&1; then
        echo "ERROR: Schema validation failed: $schema_check"
        return 1
    fi
    
    return 0
}

# Test server connectivity
test_server_connectivity() {
    local test_name="Server Connectivity"
    
    if curl -s --connect-timeout 5 --max-time 10 "$BASE_URL/status" >/dev/null 2>&1; then
        log_test "$test_name" "PASS" "Server is responsive"
    else
        log_test "$test_name" "FAIL" "Cannot connect to server at $BASE_URL"
        print_status "$RED" "Make sure the server is running: ./manage-server.sh start"
        exit 1
    fi
}

# Test /api/status endpoint
test_status_endpoint() {
    local test_name="Status Endpoint"
    
    local response
    if response=$(api_request "status"); then
        # Validate basic structure
        local schema='.indexed != null and .fileCount != null and .nodeCount != null and .system != null'
        if validate_json "$response" "$schema"; then
            local indexed=$(echo "$response" | jq -r '.indexed')
            local file_count=$(echo "$response" | jq -r '.fileCount')
            local node_count=$(echo "$response" | jq -r '.nodeCount')
            log_test "$test_name" "PASS" "Indexed: $indexed, Files: $file_count, Nodes: $node_count"
        else
            log_test "$test_name" "FAIL" "Invalid response structure"
        fi
    else
        log_test "$test_name" "FAIL" "Request failed: $response"
    fi
}

# Test /api/context/complete endpoint with various queries
test_complete_context_endpoint() {
    local test_cases=(
        "GraphUpdateService|Class|GraphUpdateService class context"
        "ILanguageWorkerService|Interface|ILanguageWorkerService interface context"
        "ProcessFileChangesAsync|Method|ProcessFileChangesAsync method context"
        "NonExistentClass12345||Non-existent identifier"
    )
    
    for test_case in "${test_cases[@]}"; do
        IFS='|' read -r identifier type description <<< "$test_case"
        
        local endpoint="context/complete?identifier=$identifier"
        [[ -n "$type" ]] && endpoint="$endpoint&type=$type"
        
        local response
        if response=$(api_request "$endpoint"); then
            # Validate response structure
            local schema='.matches != null and (.matches | type) == "array"'
            if validate_json "$response" "$schema"; then
                local match_count=$(echo "$response" | jq '.matches | length')
                
                if [[ "$identifier" == "NonExistentClass12345" ]]; then
                    # Should have no matches
                    if [[ "$match_count" == "0" ]]; then
                        log_test "Complete Context: $description" "PASS" "No matches found as expected"
                    else
                        log_test "Complete Context: $description" "FAIL" "Expected no matches, got $match_count"
                    fi
                else
                    # Should have at least one match for existing identifiers
                    if [[ "$match_count" -gt "0" ]]; then
                        # Test first match structure
                        local first_match_valid=$(echo "$response" | jq -e '.matches[0].target != null and .matches[0].relationships != null' >/dev/null 2>&1 && echo "true" || echo "false")
                        if [[ "$first_match_valid" == "true" ]]; then
                            log_test "Complete Context: $description" "PASS" "Found $match_count matches with valid structure"
                        else
                            log_test "Complete Context: $description" "FAIL" "Invalid match structure"
                        fi
                    else
                        log_test "Complete Context: $description" "FAIL" "No matches found for existing identifier"
                    fi
                fi
            else
                log_test "Complete Context: $description" "FAIL" "Invalid response structure"
            fi
        else
            log_test "Complete Context: $description" "FAIL" "Request failed: $response"
        fi
    done
}

# Test relationship arrays in context responses
test_relationship_arrays() {
    local test_name="Relationship Arrays Structure"
    
    local response
    if response=$(api_request "context/complete?identifier=GraphUpdateService&depth=2"); then
        local schema='.matches != null and .matches | length > 0'
        if validate_json "$response" "$schema"; then
            local relationships_valid=true
            local details=""
            
            # Check all relationship arrays exist and are arrays
            local relationship_arrays=(
                "uses" "usedBy" "implements" "implementedBy" 
                "inherits" "inheritedBy" "calls" "calledBy" "relatedItems"
            )
            
            for array_name in "${relationship_arrays[@]}"; do
                local array_check=$(echo "$response" | jq -e ".matches[0].relationships.$array_name != null and (.matches[0].relationships.$array_name | type) == \"array\"" >/dev/null 2>&1 && echo "true" || echo "false")
                if [[ "$array_check" != "true" ]]; then
                    relationships_valid=false
                    details="$details Missing or invalid $array_name array. "
                fi
            done
            
            # Check CodeNode structure in relationship arrays
            local node_structure_valid=$(echo "$response" | jq -e '
                .matches[0].relationships | 
                to_entries[] | 
                .value[] | 
                select(.id != null and .name != null and .type != null and .filePath != null and .startLine != null and .endLine != null)
            ' >/dev/null 2>&1 && echo "true" || echo "false")
            
            if [[ "$relationships_valid" == "true" ]] && [[ "$node_structure_valid" == "true" ]]; then
                log_test "$test_name" "PASS" "All relationship arrays present with valid structure"
            else
                log_test "$test_name" "FAIL" "$details Invalid node structure in relationships"
            fi
        else
            log_test "$test_name" "FAIL" "Invalid response structure"
        fi
    else
        log_test "$test_name" "FAIL" "Request failed: $response"
    fi
}

# Test for duplicate test files bug
test_duplicate_test_files_bug() {
    local test_name="Duplicate Test Files Bug Check"
    
    local response
    if response=$(api_request "context/complete?identifier=GraphUpdateService"); then
        local schema='.matches != null and .matches | length > 0'
        if validate_json "$response" "$schema"; then
            # Check for duplicate test files
            local duplicate_count=$(echo "$response" | jq '.matches[0].testing.testFiles | group_by(.filePath) | map(select(length > 1)) | length')
            
            if [[ "$duplicate_count" == "0" ]]; then
                log_test "$test_name" "PASS" "No duplicate test files found"
            else
                local duplicates=$(echo "$response" | jq -r '.matches[0].testing.testFiles | group_by(.filePath) | map(select(length > 1)) | .[].filePath | unique[]')
                log_test "$test_name" "FAIL" "Found duplicate test files: $duplicates"
            fi
        else
            log_test "$test_name" "FAIL" "Invalid response structure"
        fi
    else
        log_test "$test_name" "FAIL" "Request failed: $response"
    fi
}

# Test /api/context/multi endpoint
test_multi_context_endpoint() {
    local test_name="Multi Context Endpoint"
    
    local request_data='{"identifiers": ["GraphUpdateService", "ILanguageWorkerService"], "depth": 1}'
    local response
    if response=$(api_request "context/multi" "POST" "$request_data"); then
        # Should return array with 2 items
        local schema='. != null and (. | type) == "array" and (. | length) == 2'
        if validate_json "$response" "$schema"; then
            local valid_contexts=$(echo "$response" | jq '[.[] | select(.matches != null)] | length')
            if [[ "$valid_contexts" == "2" ]]; then
                log_test "$test_name" "PASS" "Returned 2 valid context objects"
            else
                log_test "$test_name" "FAIL" "Invalid context objects in response"
            fi
        else
            log_test "$test_name" "FAIL" "Invalid response structure"
        fi
    else
        log_test "$test_name" "FAIL" "Request failed: $response"
    fi
}

# Test invalid requests
test_error_handling() {
    local test_cases=(
        "context/multi|POST|{\"identifiers\": []}|400|Empty identifiers array"
        "context/complete?identifier=|GET||400|Empty identifier parameter"
        "nonexistent/endpoint|GET||404|Non-existent endpoint"
    )
    
    for test_case in "${test_cases[@]}"; do
        IFS='|' read -r endpoint method data expected_status description <<< "$test_case"
        
        local response
        response=$(api_request "$endpoint" "$method" "$data" "$expected_status" 2>/dev/null || echo "EXPECTED_ERROR")
        
        if [[ "$response" != "EXPECTED_ERROR" ]] && [[ "$response" != *"ERROR"* ]]; then
            log_test "Error Handling: $description" "PASS" "Returned expected status $expected_status"
        else
            log_test "Error Handling: $description" "FAIL" "Unexpected response or wrong status code"
        fi
    done
}

# Test AOT JSON serialization
test_aot_serialization() {
    local test_name="AOT JSON Serialization"
    
    # Test various endpoints to ensure JSON serialization works
    local endpoints=("status" "context/complete?identifier=GraphUpdateService" "context/complete?identifier=ILanguageWorkerService&type=Interface")
    local all_valid=true
    local details=""
    
    for endpoint in "${endpoints[@]}"; do
        local response
        if response=$(api_request "$endpoint"); then
            if ! echo "$response" | jq . >/dev/null 2>&1; then
                all_valid=false
                details="$details Invalid JSON from $endpoint. "
            fi
        else
            all_valid=false
            details="$details Failed to get response from $endpoint. "
        fi
    done
    
    if [[ "$all_valid" == "true" ]]; then
        log_test "$test_name" "PASS" "All endpoints return valid JSON"
    else
        log_test "$test_name" "FAIL" "$details"
    fi
}

# Test performance/timeout handling
test_performance() {
    local test_name="Performance and Timeout Handling"
    
    # Test with complex query that might take time
    local start_time=$(date +%s.%N)
    local response
    if response=$(api_request "context/complete?identifier=ProcessFileChangesAsync&type=Method&depth=3"); then
        local end_time=$(date +%s.%N)
        local duration=$(echo "$end_time - $start_time" | bc -l)
        local duration_int=$(printf "%.0f" "$duration")
        
        if [[ "$duration_int" -lt "$API_TIMEOUT" ]]; then
            log_test "$test_name" "PASS" "Response time: ${duration}s (under ${API_TIMEOUT}s timeout)"
        else
            log_test "$test_name" "FAIL" "Response time: ${duration}s (exceeded ${API_TIMEOUT}s timeout)"
        fi
    else
        log_test "$test_name" "FAIL" "Request failed or timed out"
    fi
}

# Main test execution
main() {
    print_status "$BLUE" "============================================"
    print_status "$BLUE" "CodeContext API Endpoint Test Suite"
    print_status "$BLUE" "============================================"
    echo
    print_status "$YELLOW" "Testing API at: $BASE_URL"
    print_status "$YELLOW" "Timeout: ${API_TIMEOUT}s"
    echo
    
    # Check if jq is available
    if ! command -v jq &> /dev/null; then
        print_status "$RED" "Error: jq is required but not installed"
        exit 1
    fi
    
    # Check if bc is available for performance tests
    if ! command -v bc &> /dev/null; then
        print_status "$YELLOW" "Warning: bc not found, skipping performance tests"
    fi
    
    # Run all tests
    test_server_connectivity
    test_status_endpoint
    test_complete_context_endpoint
    test_relationship_arrays
    test_duplicate_test_files_bug
    test_multi_context_endpoint
    test_error_handling
    test_aot_serialization
    
    if command -v bc &> /dev/null; then
        test_performance
    fi
    
    # Summary
    echo
    print_status "$BLUE" "============================================"
    print_status "$BLUE" "Test Results Summary"
    print_status "$BLUE" "============================================"
    
    print_status "$GREEN" "Passed: $PASSED_TESTS"
    print_status "$RED" "Failed: $FAILED_TESTS"
    print_status "$YELLOW" "Total:  $TOTAL_TESTS"
    
    if [[ "$FAILED_TESTS" -eq 0 ]]; then
        print_status "$GREEN" "All tests passed! ✓"
        exit 0
    else
        print_status "$RED" "Some tests failed! ✗"
        exit 1
    fi
}

# Handle command line arguments
case "${1:-}" in
    --help|-h)
        echo "CodeContext API Endpoint Test Suite"
        echo
        echo "Usage: $0 [options]"
        echo
        echo "Options:"
        echo "  --help, -h     Show this help message"
        echo "  --quiet, -q    Suppress detailed output"
        echo
        echo "Environment Variables:"
        echo "  CODECONTEXT_HOST     API host (default: localhost)"
        echo "  CODECONTEXT_PORT     API port (default: 7890)"
        echo "  CODECONTEXT_TIMEOUT  Request timeout in seconds (default: 30)"
        echo
        exit 0
        ;;
    --quiet|-q)
        # Redirect detailed output for quiet mode
        exec > >(grep -E "(✓|✗|Test Results Summary|Passed:|Failed:|Total:)" | head -20)
        ;;
esac

# Run the tests
main "$@"