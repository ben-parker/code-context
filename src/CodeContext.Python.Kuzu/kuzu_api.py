"""
Optimized Kuzu database API for CodeContext.
Implements performance best practices for fast AST queries.
Module-level functions for CSnakes compatibility.
"""

import json
import kuzu
import logging
import os
import time
import sys
from typing import Optional, Dict, List, Any, Tuple, Union
from functools import lru_cache
from datetime import datetime, timedelta
import multiprocessing
from contextlib import contextmanager
import threading
import traceback

# Configure logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

# Performance configuration - optimized for read-heavy workloads
PERFORMANCE_CONFIG = {
    "num_threads": min(4, multiprocessing.cpu_count()),  # Capped at 4 for single-agent usage
    "buffer_pool_size": int(os.environ.get("KUZU_BUFFER_POOL_SIZE", 8 * 1024 * 1024 * 1024)),  # 8GB default
    "query_timeout": int(os.environ.get("KUZU_QUERY_TIMEOUT", 1000)),  # 1 second default
    "checkpoint_threshold": int(os.environ.get("KUZU_CHECKPOINT_THRESHOLD", 268435456)),  # 256MB for read optimization
    "enable_spilling": os.environ.get("KUZU_ENABLE_SPILLING", "true").lower() == "true",
    "cache_ttl_seconds": int(os.environ.get("KUZU_CACHE_TTL", 1800)),  # 30 minutes for read optimization
    "max_cache_memory_mb": int(os.environ.get("KUZU_MAX_CACHE_MB", 1024)),  # 1GB cache limit
    "batch_size": int(os.environ.get("KUZU_BATCH_SIZE", 10000)),
    "max_traversal_depth": int(os.environ.get("KUZU_MAX_TRAVERSAL_DEPTH", 2)),  # Restrictive depth
    "result_limit": int(os.environ.get("KUZU_RESULT_LIMIT", 200)),  # Reduced default limit
    "large_result_limit": int(os.environ.get("KUZU_LARGE_RESULT_LIMIT", 5000)),  # For edge queries
    "file_context_chunk_size": int(os.environ.get("KUZU_FILE_CHUNK_SIZE", 500)),  # Nodes per chunk
    "complexity_warning_threshold": int(os.environ.get("KUZU_COMPLEXITY_THRESHOLD", 150))  # Nodes before warning
}

# Global database and connection management
_db: Optional[kuzu.Database] = None
_connection_pool: List[kuzu.Connection] = []
_pool_lock = threading.Lock()
_pool_size = 2  # Reduced for single-agent usage

# Standard node properties selection for Cypher queries
# Metadata is now properly handled as JSON strings
NODE_PROPERTIES_SELECT = "n.id, n.name, n.type, n.language, n.file_path, n.start_line, n.end_line, n.start_col, n.end_col, n.namespace, n.visibility, n.signature, n.return_type, n.parameters, n.modifiers, n.metrics, n.metadata"


def _row_to_node_dict(row) -> Dict[str, Any]:
    """Convert a Kuzu query result row to a node dictionary."""
    props = ["id", "name", "type", "language", "file_path", "start_line", "end_line",
             "start_col", "end_col", "namespace", "visibility", "signature", 
             "return_type", "parameters", "modifiers", "metrics", "metadata"]
    
    result = {}
    for i, prop in enumerate(props):
        if i < len(row):
            value = row[i]
            # Handle null values
            if value is None:
                result[prop] = None
            # Parse JSON strings back to objects for certain fields (but not metadata)
            elif prop in ["parameters", "metrics"] and isinstance(value, str) and value:
                try:
                    value = json.loads(value)
                    result[prop] = value
                except:
                    result[prop] = value  # Keep as string if parsing fails
            else:
                result[prop] = value
        else:
            result[prop] = None
    
    return result


def _file_metadata_to_dict(metadata) -> Dict[str, Any]:
    """Convert a Kuzu file metadata node to a dictionary."""
    result = {}
    for prop in ["file_path", "last_modified", "last_scanned", "file_hash", 
                 "status", "error_message", "node_count", "edge_count", 
                 "language", "size_bytes"]:
        if hasattr(metadata, prop):
            result[prop] = getattr(metadata, prop)
    return result

# Query result cache with size tracking
_query_cache: Dict[str, Tuple[Any, datetime, int]] = {}  # key -> (result, timestamp, size_bytes)
_cache_lock = threading.Lock()
_cache_stats = {"hits": 0, "misses": 0, "evictions": 0}
_cache_memory_bytes = 0


def initialize_database(db_path: str) -> None:
    """
    Initialize Kuzu database with performance optimizations.
    
    Args:
        db_path: Path to the Kuzu database directory
    """
    global _db, _connection_pool
    
    # Set buffer pool size before creating database
    os.environ["KUZU_BUFFER_POOL_SIZE"] = str(PERFORMANCE_CONFIG["buffer_pool_size"])
    
    _db = kuzu.Database(db_path)
    
    # Pre-create connection pool
    logger.info(f"Creating connection pool with {_pool_size} connections and {PERFORMANCE_CONFIG['num_threads']} threads")
    for i in range(_pool_size):
        conn = _create_optimized_connection()
        _connection_pool.append(conn)
    
    # Create schema using first connection
    conn = _connection_pool[0]
    create_optimized_schema(conn)
    
    logger.info(f"Database initialized with {PERFORMANCE_CONFIG['num_threads']} threads")


def _create_optimized_connection() -> kuzu.Connection:
    """Create a connection with optimal performance settings for read-heavy workloads."""
    conn = kuzu.Connection(_db, num_threads=PERFORMANCE_CONFIG["num_threads"])
    
    # Configure performance settings using CALL statements
    conn.execute(f"CALL threads={PERFORMANCE_CONFIG['num_threads']};")
    conn.execute(f"CALL timeout={PERFORMANCE_CONFIG['query_timeout']};")
    conn.execute(f"CALL checkpoint_threshold={PERFORMANCE_CONFIG['checkpoint_threshold']};")
    
    if PERFORMANCE_CONFIG["enable_spilling"]:
        conn.execute("CALL spill_to_disk=true;")
    
    return conn


@contextmanager
def get_connection():
    """
    Get a connection from the pool using context manager pattern.
    
    Yields:
        An optimized Kuzu connection
        
    Raises:
        RuntimeError: If database is not initialized
    """
    if not _connection_pool:
        raise RuntimeError("Database not initialized. Call initialize_database() first.")
    
    conn = None
    try:
        with _pool_lock:
            if _connection_pool:
                conn = _connection_pool.pop()
            else:
                # Create new connection if pool is empty
                conn = _create_optimized_connection()
        yield conn
    finally:
        if conn:
            with _pool_lock:
                if len(_connection_pool) < _pool_size:
                    _connection_pool.append(conn)


def create_optimized_schema(conn: Optional[kuzu.Connection] = None) -> None:
    """Create optimized database schema focused on code analysis."""
    if conn is None:
        with get_connection() as conn:
            _create_schema_internal(conn)
    else:
        _create_schema_internal(conn)


def _create_schema_internal(conn: kuzu.Connection) -> None:
    """Internal schema creation with read-optimized design."""
    
    # Schema version tracking
    conn.execute("""
        CREATE NODE TABLE IF NOT EXISTS SchemaVersion (
            version INT32 PRIMARY KEY,
            applied_at TIMESTAMP,
            description STRING
        )
    """)
    
    # Main CodeNode table with all necessary properties
    conn.execute("""
        CREATE NODE TABLE IF NOT EXISTS CodeNode (
            id STRING PRIMARY KEY,
            name STRING,
            type STRING,
            language STRING,
            file_path STRING,
            start_line INT64,
            end_line INT64,
            start_col INT32,
            end_col INT32,
            namespace STRING,
            visibility STRING,
            signature STRING,
            return_type STRING,
            parameters STRING,
            modifiers STRING,
            metrics STRING,
            metadata STRING
        )
    """)
    
    # Optimized relationship tables with specific types for better query performance
    conn.execute("""
        CREATE REL TABLE IF NOT EXISTS Contains (
            FROM CodeNode TO CodeNode,
            order_index INT32,
            MANY_MANY
        )
    """)
    
    conn.execute("""
        CREATE REL TABLE IF NOT EXISTS Inherits (
            FROM CodeNode TO CodeNode,
            inheritance_type STRING,
            MANY_ONE
        )
    """)
    
    conn.execute("""
        CREATE REL TABLE IF NOT EXISTS Implements (
            FROM CodeNode TO CodeNode,
            MANY_MANY
        )
    """)
    
    conn.execute("""
        CREATE REL TABLE IF NOT EXISTS Calls (
            FROM CodeNode TO CodeNode,
            call_site STRING,
            is_async BOOLEAN,
            MANY_MANY
        )
    """)
    
    conn.execute("""
        CREATE REL TABLE IF NOT EXISTS References (
            FROM CodeNode TO CodeNode,
            reference_type STRING,
            MANY_MANY
        )
    """)
    
    conn.execute("""
        CREATE REL TABLE IF NOT EXISTS DependsOn (
            FROM CodeNode TO CodeNode,
            dependency_type STRING,
            MANY_MANY
        )
    """)
    
    # Generic edge table for compatibility
    conn.execute("""
        CREATE REL TABLE IF NOT EXISTS CodeEdge (
            FROM CodeNode TO CodeNode,
            id STRING,
            type STRING,
            metadata STRING
        )
    """)
    
    # File metadata table
    conn.execute("""
        CREATE NODE TABLE IF NOT EXISTS FileMetadata (
            file_path STRING PRIMARY KEY,
            last_modified TIMESTAMP,
            last_scanned TIMESTAMP,
            file_hash STRING,
            status STRING,
            error_message STRING,
            node_count INT32,
            edge_count INT32,
            language STRING,
            size_bytes INT64
        )
    """)
    
    logger.info("Optimized schema created successfully")


def _estimate_size_bytes(obj: Any) -> int:
    """Estimate the memory size of an object in bytes."""
    if isinstance(obj, str):
        return len(obj.encode('utf-8'))
    elif isinstance(obj, (list, dict)):
        return len(json.dumps(obj).encode('utf-8'))
    else:
        return sys.getsizeof(obj)


def _cache_key(operation: str, **kwargs) -> str:
    """Generate a cache key for a query."""
    return f"{operation}:{json.dumps(kwargs, sort_keys=True)}"


def _get_cached_result(cache_key: str) -> Optional[Any]:
    """Get a cached query result if available and not expired."""
    with _cache_lock:
        if cache_key in _query_cache:
            result, timestamp, size_bytes = _query_cache[cache_key]
            if datetime.now() - timestamp < timedelta(seconds=PERFORMANCE_CONFIG["cache_ttl_seconds"]):
                _cache_stats["hits"] += 1
                logger.debug(f"Cache hit for key: {cache_key}")
                return result
            else:
                # Expired entry
                _evict_cache_entry(cache_key)
    
    _cache_stats["misses"] += 1
    return None


def _set_cached_result(cache_key: str, result: Any) -> None:
    """Store a query result in cache with size-based eviction."""
    global _cache_memory_bytes
    
    result_size = _estimate_size_bytes(result)
    max_cache_bytes = PERFORMANCE_CONFIG["max_cache_memory_mb"] * 1024 * 1024
    
    with _cache_lock:
        # Evict entries if adding this would exceed memory limit
        while (_cache_memory_bytes + result_size > max_cache_bytes and _query_cache):
            # Remove oldest entry
            oldest_key = min(_query_cache.keys(), key=lambda k: _query_cache[k][1])
            _evict_cache_entry(oldest_key)
        
        # Add new entry
        _query_cache[cache_key] = (result, datetime.now(), result_size)
        _cache_memory_bytes += result_size


def _evict_cache_entry(cache_key: str) -> None:
    """Evict a cache entry and update memory tracking. Must be called with lock held."""
    global _cache_memory_bytes
    
    if cache_key in _query_cache:
        _, _, size_bytes = _query_cache[cache_key]
        _cache_memory_bytes -= size_bytes
        del _query_cache[cache_key]
        _cache_stats["evictions"] += 1


def _analyze_query_complexity(query: str, params: Dict[str, Any] = None) -> Dict[str, Any]:
    """Analyze query for potential performance issues."""
    issues = []
    complexity_score = "low"
    
    # Check for unbounded path queries
    if "*]" in query and not any(f"*1..{i}]" in query for i in range(1, 10)):
        issues.append("Unbounded path query detected - may cause performance issues")
        complexity_score = "high"
    
    # Check for multiple OPTIONAL MATCH
    optional_match_count = query.count("OPTIONAL MATCH")
    if optional_match_count > 3:
        issues.append(f"Query has {optional_match_count} OPTIONAL MATCH clauses - may create large intermediate results")
        complexity_score = "high"
    elif optional_match_count > 1:
        complexity_score = "medium"
    
    # Check for large IN clauses
    if params and "IN $" in query:
        for key, value in params.items():
            if isinstance(value, list) and len(value) > 100:
                issues.append(f"Large IN clause with {len(value)} items - consider batching")
                complexity_score = "high"
    
    # Check for missing relationship types
    if "-[]-" in query or "-[*" in query and ":" not in query.split("-[")[1].split("]")[0]:
        issues.append("Query uses untyped relationships - specify relationship types for better performance")
        if complexity_score == "low":
            complexity_score = "medium"
    
    # Check for NOT IN
    if "NOT IN" in query:
        issues.append("NOT IN can be slow with large lists - consider alternative patterns")
        if complexity_score == "low":
            complexity_score = "medium"
    
    # Log warnings
    if issues:
        logger.warning(f"Query complexity analysis: {', '.join(issues)}")
    
    return {
        "complexity_score": complexity_score,
        "issues": issues,
        "query_length": len(query)
    }


def _execute_with_timeout(conn: kuzu.Connection, query: str, params: Dict[str, Any] = None, 
                         timeout_ms: Optional[int] = None, operation_name: str = "query") -> Any:
    """Execute a query with timeout protection and monitoring."""
    if timeout_ms is None:
        timeout_ms = PERFORMANCE_CONFIG["query_timeout"]
    
    # Analyze query complexity
    complexity_analysis = _analyze_query_complexity(query, params)
    
    # Set temporary timeout if different from default
    original_timeout = None
    if timeout_ms != PERFORMANCE_CONFIG["query_timeout"]:
        original_timeout = PERFORMANCE_CONFIG["query_timeout"]
        conn.execute(f"CALL timeout={timeout_ms};")
    
    start_time = time.time()
    nodes_processed = 0
    
    try:
        result = conn.execute(query, params or {})
        execution_time = time.time() - start_time
        
        # Process results and count nodes
        processed_results = []
        while result and result.has_next():
            row = result.get_next()
            processed_results.append(row)
            nodes_processed += 1
            
            # Warn if processing too many nodes
            if nodes_processed > PERFORMANCE_CONFIG["complexity_warning_threshold"]:
                logger.warning(f"Query processing {nodes_processed}+ nodes - consider more specific filters")
        
        # Build query stats
        query_stats = {
            "query_time_ms": int(execution_time * 1000),
            "nodes_processed": nodes_processed,
            "complexity_score": complexity_analysis["complexity_score"],
            "cache_hit": False  # Will be overridden by cache logic
        }
        
        if complexity_analysis["issues"]:
            query_stats["warnings"] = complexity_analysis["issues"]
        
        return processed_results, query_stats
        
    except Exception as e:
        execution_time = time.time() - start_time
        error_msg = str(e)
        
        # Check if it's a timeout
        if "timeout" in error_msg.lower() or execution_time * 1000 >= timeout_ms * 0.9:
            logger.error(f"Query timeout after {execution_time:.2f}s: {operation_name}")
            error_response = {
                "error": True,
                "error_type": "query_timeout",
                "message": f"Query exceeded {timeout_ms/1000:.1f} second timeout. Consider using ReadFile tool for direct file access.",
                "suggestions": [
                    "Use ReadFile tool for direct file access",
                    "Use more specific filters to reduce result set",
                    "Reduce traversal depth"
                ],
                "query_metrics": {
                    "timeout_ms": timeout_ms,
                    "attempted_nodes": nodes_processed,
                    "complexity_score": complexity_analysis["complexity_score"]
                }
            }
            raise QueryTimeoutError(json.dumps(error_response))
        else:
            # Other error
            logger.error(f"Query error in {operation_name}: {error_msg}")
            error_response = {
                "error": True,
                "error_type": "query_error",
                "message": f"Query failed: {error_msg}",
                "suggestions": ["Check query syntax", "Verify node IDs exist"],
                "query_metrics": {
                    "execution_time_ms": int(execution_time * 1000),
                    "nodes_processed": nodes_processed
                }
            }
            raise QueryExecutionError(json.dumps(error_response))
    
    finally:
        # Restore original timeout
        if original_timeout is not None:
            conn.execute(f"CALL timeout={original_timeout};")


class QueryTimeoutError(Exception):
    """Raised when a query exceeds the timeout limit."""
    pass


class QueryExecutionError(Exception):
    """Raised when a query fails for reasons other than timeout."""
    pass


def _add_query_stats(result_dict: Any, query_stats: Dict[str, Any]) -> str:
    """Add query statistics to a result dictionary and return as JSON."""
    if isinstance(result_dict, dict):
        result_dict["_query_stats"] = query_stats
        return json.dumps(result_dict)
    elif isinstance(result_dict, list):
        return json.dumps({
            "results": result_dict,
            "_query_stats": query_stats
        })
    else:
        return json.dumps({
            "result": result_dict,
            "_query_stats": query_stats
        })


def insert_node(node_json: str) -> None:
    """
    Insert or update a single code node (delegates to batch for efficiency).
    
    Args:
        node_json: JSON string containing node properties
    """
    insert_nodes_batch(json.dumps([json.loads(node_json)]))


def insert_edge(edge_json: str) -> None:
    """
    Insert or update a single code edge (delegates to batch for efficiency).
    
    Args:
        edge_json: JSON string containing edge properties
    """
    insert_edges_batch(json.dumps([json.loads(edge_json)]))


def insert_nodes_batch(nodes_json: str) -> None:
    """
    Insert multiple nodes using optimized batch operations with UNWIND.
    
    Args:
        nodes_json: JSON string containing list of node dictionaries
    """
    nodes = json.loads(nodes_json)
    if not nodes:
        return
    
    with get_connection() as conn:
        # Process in chunks to avoid memory issues
        for i in range(0, len(nodes), PERFORMANCE_CONFIG["batch_size"]):
            batch = nodes[i:i + PERFORMANCE_CONFIG["batch_size"]]
            
            # Preprocess batch to convert complex fields to strings
            processed_batch = []
            for node in batch:
                processed_node = node.copy()
                
                # Convert parameters to JSON string if it's a list/dict
                if "parameters" in processed_node and processed_node["parameters"] is not None:
                    if isinstance(processed_node["parameters"], (list, dict)):
                        processed_node["parameters"] = json.dumps(processed_node["parameters"])
                
                # Convert metrics to JSON string if it's a dict
                if "metrics" in processed_node and processed_node["metrics"] is not None:
                    if isinstance(processed_node["metrics"], dict):
                        processed_node["metrics"] = json.dumps(processed_node["metrics"])
                
                # Convert metadata to JSON string if it's a dict
                if "metadata" in processed_node and processed_node["metadata"] is not None:
                    if isinstance(processed_node["metadata"], dict):
                        processed_node["metadata"] = json.dumps(processed_node["metadata"])
                
                processed_batch.append(processed_node)
            
            # Use UNWIND for efficient batch processing
            conn.execute("""
                UNWIND $nodes AS node
                MERGE (n:CodeNode {id: node.id})
                SET n.name = node.name,
                    n.type = node.type,
                    n.language = node.language,
                    n.file_path = node.file_path,
                    n.start_line = node.start_line,
                    n.end_line = node.end_line,
                    n.start_col = node.start_col,
                    n.end_col = node.end_col,
                    n.namespace = node.namespace,
                    n.visibility = node.visibility,
                    n.signature = node.signature,
                    n.return_type = node.return_type,
                    n.parameters = node.parameters,
                    n.modifiers = node.modifiers,
                    n.metrics = node.metrics,
                    n.metadata = node.metadata
            """, {"nodes": processed_batch})
    
    # Clear cache after bulk insert
    clear_cache()
    logger.info(f"Batch inserted {len(nodes)} nodes")


def insert_edges_batch(edges_json: str) -> None:
    """
    Insert multiple edges using optimized batch operations with UNWIND.
    
    Args:
        edges_json: JSON string containing list of edge dictionaries
    """
    edges = json.loads(edges_json)
    logger.info(f"insert_edges_batch called with {len(edges)} edges")
    
    if not edges:
        logger.info("No edges to insert, returning early")
        return
    
    # Debug: Log first few edges to understand structure
    if edges:
        logger.info(f"Sample edge: {edges[0]}")
    
    with get_connection() as conn:
        # Group edges by type for optimized insertion
        edges_by_type = {}
        for edge in edges:
            edge_type = edge.get("type", "CodeEdge")
            if edge_type not in edges_by_type:
                edges_by_type[edge_type] = []
            edges_by_type[edge_type].append(edge)
        
        logger.info(f"Edges grouped by type: {[(k, len(v)) for k, v in edges_by_type.items()]}")
        
        # Process each edge type with appropriate relationship table
        for edge_type, typed_edges in edges_by_type.items():
            valid_edges = [e for e in typed_edges if e.get("source_id") and e.get("target_id")]
            
            logger.info(f"Processing {edge_type}: {len(typed_edges)} total, {len(valid_edges)} valid")
            
            if not valid_edges:
                logger.warning(f"No valid edges for type {edge_type}")
                # Debug: Show invalid edges
                for i, edge in enumerate(typed_edges[:3]):  # Show first 3 invalid edges
                    logger.warning(f"Invalid edge {i}: {edge}")
                continue
            
            # Process in chunks
            for i in range(0, len(valid_edges), PERFORMANCE_CONFIG["batch_size"]):
                batch = valid_edges[i:i + PERFORMANCE_CONFIG["batch_size"]]
                
                try:
                    if edge_type in ["INHERITS", "EXTENDS"]:
                        logger.info(f"Inserting {len(batch)} {edge_type} edges")
                        conn.execute("""
                            UNWIND $edges AS edge
                            MATCH (source:CodeNode {id: edge.source_id}), (target:CodeNode {id: edge.target_id})
                            MERGE (source)-[e:Inherits]->(target)
                            SET e.inheritance_type = edge.type
                        """, {"edges": batch})
                    elif edge_type == "IMPLEMENTS":
                        logger.info(f"Inserting {len(batch)} {edge_type} edges")
                        conn.execute("""
                            UNWIND $edges AS edge
                            MATCH (source:CodeNode {id: edge.source_id}), (target:CodeNode {id: edge.target_id})
                            MERGE (source)-[e:Implements]->(target)
                        """, {"edges": batch})
                    elif edge_type == "CALLS":
                        logger.info(f"Inserting {len(batch)} {edge_type} edges")
                        # Preprocess batch to construct call_site from metadata line/column
                        processed_batch = []
                        for edge in batch:
                            processed_edge = edge.copy()
                            
                            # Construct call_site from metadata if it exists
                            if "metadata" in processed_edge and processed_edge["metadata"]:
                                metadata = processed_edge["metadata"]
                                if isinstance(metadata, dict):
                                    # Create call_site from line and column
                                    line = metadata.get("line", "")
                                    column = metadata.get("column", "")
                                    if line and column:
                                        processed_edge["call_site"] = f"line:{line},column:{column}"
                                    else:
                                        processed_edge["call_site"] = json.dumps(metadata)
                                else:
                                    processed_edge["call_site"] = str(metadata)
                            else:
                                processed_edge["call_site"] = None
                            
                            # Extract is_async from metadata if it exists
                            if "metadata" in processed_edge and processed_edge["metadata"]:
                                processed_edge["is_async"] = processed_edge["metadata"].get("is_async", False)
                            else:
                                processed_edge["is_async"] = False
                                
                            processed_batch.append(processed_edge)
                        
                        conn.execute("""
                            UNWIND $edges AS edge
                            MATCH (source:CodeNode {id: edge.source_id}), (target:CodeNode {id: edge.target_id})
                            MERGE (source)-[e:Calls]->(target)
                            SET e.call_site = edge.call_site,
                                e.is_async = edge.is_async
                        """, {"edges": processed_batch})
                    elif edge_type == "CONTAINS":
                        logger.info(f"Inserting {len(batch)} {edge_type} edges")
                        # Preprocess batch to extract order_index from metadata if needed
                        processed_batch = []
                        for edge in batch:
                            processed_edge = edge.copy()
                            if "order_index" not in processed_edge and "metadata" in processed_edge and processed_edge["metadata"]:
                                processed_edge["order_index"] = processed_edge["metadata"].get("order_index", 0)
                            elif "order_index" not in processed_edge:
                                processed_edge["order_index"] = 0
                            processed_batch.append(processed_edge)
                        
                        conn.execute("""
                            UNWIND $edges AS edge
                            MATCH (source:CodeNode {id: edge.source_id}), (target:CodeNode {id: edge.target_id})
                            MERGE (source)-[e:Contains]->(target)
                            SET e.order_index = edge.order_index
                        """, {"edges": processed_batch})
                    elif edge_type in ["REFERENCES", "USES"]:
                        logger.info(f"Inserting {len(batch)} {edge_type} edges")
                        # Preprocess batch to extract reference_type from metadata if needed
                        processed_batch = []
                        for edge in batch:
                            processed_edge = edge.copy()
                            if "reference_type" not in processed_edge and "metadata" in processed_edge and processed_edge["metadata"]:
                                processed_edge["reference_type"] = processed_edge["metadata"].get("reference_type", edge_type)
                            elif "reference_type" not in processed_edge:
                                processed_edge["reference_type"] = edge_type
                            processed_batch.append(processed_edge)
                        
                        conn.execute("""
                            UNWIND $edges AS edge
                            MATCH (source:CodeNode {id: edge.source_id}), (target:CodeNode {id: edge.target_id})
                            MERGE (source)-[e:References]->(target)
                            SET e.reference_type = edge.reference_type
                        """, {"edges": processed_batch})
                    elif edge_type in ["IMPORTS", "DEPENDS_ON", "USING", "REQUIRE"]:
                        logger.info(f"Inserting {len(batch)} {edge_type} edges")
                        conn.execute("""
                            UNWIND $edges AS edge
                            MATCH (source:CodeNode {id: edge.source_id}), (target:CodeNode {id: edge.target_id})
                            MERGE (source)-[e:DependsOn]->(target)
                            SET e.dependency_type = edge.type
                        """, {"edges": batch})
                    else:
                        logger.info(f"Inserting {len(batch)} {edge_type} edges (generic)")
                        # Generic CodeEdge for unknown types
                        # Preprocess batch to convert metadata to JSON string if needed
                        processed_batch = []
                        for edge in batch:
                            processed_edge = edge.copy()
                            if "metadata" in processed_edge and processed_edge["metadata"] is not None:
                                if isinstance(processed_edge["metadata"], dict):
                                    processed_edge["metadata"] = json.dumps(processed_edge["metadata"])
                            else:
                                processed_edge["metadata"] = None
                            processed_batch.append(processed_edge)
                        
                        conn.execute("""
                            UNWIND $edges AS edge
                            MATCH (source:CodeNode {id: edge.source_id}), (target:CodeNode {id: edge.target_id})
                            MERGE (source)-[e:CodeEdge {id: edge.id}]->(target)
                            SET e.type = edge.type,
                                e.metadata = edge.metadata
                        """, {"edges": processed_batch})
                        
                except Exception as e:
                    logger.error(f"Error inserting {edge_type} edges: {str(e)}")
                    logger.error(f"Batch sample: {batch[:2] if batch else 'empty'}")
                    raise
    
    # Clear cache after bulk insert
    clear_cache()
    logger.info(f"Batch inserted {len(edges)} edges")


def find_nodes_by_name(name: str, exact: bool = False) -> str:
    """
    Find nodes by name with optimized query patterns.
    
    Args:
        name: Name to search for
        exact: If True, perform exact match; otherwise, use contains
    
    Returns:
        JSON string containing list of matching nodes with query stats
    """
    # Check cache first
    cache_key = _cache_key("find_name", name=name, exact=exact)
    cached = _get_cached_result(cache_key)
    if cached is not None:
        query_stats = {"cache_hit": True, "query_time_ms": 0}
        return _add_query_stats(json.loads(cached), query_stats)
    
    try:
        with get_connection() as conn:
            if exact:
                query = f"""
                    MATCH (n:CodeNode {{name: $name}})
                    RETURN {NODE_PROPERTIES_SELECT}
                    ORDER BY n.file_path, n.start_line
                    LIMIT $limit
                """
            else:
                query = f"""
                    MATCH (n:CodeNode)
                    WHERE n.name CONTAINS $name
                    RETURN {NODE_PROPERTIES_SELECT}
                    ORDER BY n.name, n.file_path
                    LIMIT $limit
                """
            
            results, query_stats = _execute_with_timeout(
                conn, query, 
                {"name": name, "limit": PERFORMANCE_CONFIG["result_limit"]},
                operation_name=f"find_nodes_by_name('{name}')"
            )
            
            nodes = [_row_to_node_dict(row) for row in results]
            
        json_result = json.dumps(nodes)
        _set_cached_result(cache_key, json_result)
        query_stats["cache_hit"] = False
        return _add_query_stats(nodes, query_stats)
        
    except (QueryTimeoutError, QueryExecutionError) as e:
        return str(e)


def find_nodes_by_type(node_type: str) -> str:
    """
    Find all nodes of a specific type with caching.
    
    Args:
        node_type: Type of nodes to find (e.g., "Class", "Method")
    
    Returns:
        JSON string containing list of matching nodes with query stats
    """
    # Check cache first
    cache_key = _cache_key("by_type", type=node_type)
    cached = _get_cached_result(cache_key)
    if cached is not None:
        query_stats = {"cache_hit": True, "query_time_ms": 0}
        return _add_query_stats(json.loads(cached), query_stats)
    
    try:
        with get_connection() as conn:
            query = f"""
                MATCH (n:CodeNode {{type: $type}})
                RETURN {NODE_PROPERTIES_SELECT}
                ORDER BY n.namespace, n.name
                LIMIT $limit
            """
            
            results, query_stats = _execute_with_timeout(
                conn, query,
                {"type": node_type, "limit": PERFORMANCE_CONFIG["result_limit"]},
                operation_name=f"find_nodes_by_type('{node_type}')"
            )
            
            nodes = [_row_to_node_dict(row) for row in results]
        
        json_result = json.dumps(nodes)
        _set_cached_result(cache_key, json_result)
        query_stats["cache_hit"] = False
        return _add_query_stats(nodes, query_stats)
        
    except (QueryTimeoutError, QueryExecutionError) as e:
        return str(e)


def find_nodes_by_name_and_type(name: str, node_type: str, exact: bool = False) -> str:
    """
    Find nodes by name and type using optimized query patterns.
    
    Args:
        name: Name to search for
        node_type: Type of nodes to find
        exact: If True, perform exact match; otherwise, use contains
    
    Returns:
        JSON string containing list of matching nodes with query stats
    """
    # Check cache first
    cache_key = _cache_key("find_nodes", name=name, type=node_type, exact=exact)
    cached = _get_cached_result(cache_key)
    if cached is not None:
        query_stats = {"cache_hit": True, "query_time_ms": 0}
        return _add_query_stats(json.loads(cached), query_stats)
    
    try:
        with get_connection() as conn:
            if exact:
                query = f"""
                    MATCH (n:CodeNode {{name: $name, type: $type}})
                    RETURN {NODE_PROPERTIES_SELECT}
                    ORDER BY n.file_path, n.start_line
                    LIMIT $limit
                """
            else:
                query = f"""
                    MATCH (n:CodeNode {{type: $type}})
                    WHERE n.name CONTAINS $name
                    RETURN {NODE_PROPERTIES_SELECT}
                    ORDER BY n.name, n.file_path
                    LIMIT $limit
                """
            
            results, query_stats = _execute_with_timeout(
                conn, query,
                {"name": name, "type": node_type, "limit": PERFORMANCE_CONFIG["result_limit"]},
                operation_name=f"find_nodes_by_name_and_type('{name}', '{node_type}')"
            )
            
            nodes = [_row_to_node_dict(row) for row in results]
        
        json_result = json.dumps(nodes)
        _set_cached_result(cache_key, json_result)
        query_stats["cache_hit"] = False
        return _add_query_stats(nodes, query_stats)
        
    except (QueryTimeoutError, QueryExecutionError) as e:
        return str(e)


def find_nodes_by_file(file_path: str) -> str:
    """
    Find all nodes in a specific file with optimized ordering.
    
    Args:
        file_path: Path to the file
    
    Returns:
        JSON string containing list of nodes in the file with query stats
    """
    try:
        with get_connection() as conn:
            query = f"""
                MATCH (n:CodeNode {{file_path: $file_path}})
                RETURN {NODE_PROPERTIES_SELECT}
                ORDER BY n.start_line, n.start_col
            """
            
            results, query_stats = _execute_with_timeout(
                conn, query,
                {"file_path": file_path},
                operation_name=f"find_nodes_by_file('{file_path}')"
            )
            
            nodes = [_row_to_node_dict(row) for row in results]
        
        return _add_query_stats(nodes, query_stats)
        
    except (QueryTimeoutError, QueryExecutionError) as e:
        return str(e)


def get_node_by_id(node_id: str) -> Optional[str]:
    """
    Get a specific node by its ID using primary key lookup.
    
    Args:
        node_id: The node's unique identifier
    
    Returns:
        JSON string containing node with query stats or None if not found
    """
    try:
        with get_connection() as conn:
            query = f"""
                MATCH (n:CodeNode {{id: $id}})
                RETURN {NODE_PROPERTIES_SELECT}
                LIMIT 1
            """
            
            results, query_stats = _execute_with_timeout(
                conn, query,
                {"id": node_id},
                timeout_ms=100,  # Very fast for PK lookup
                operation_name=f"get_node_by_id('{node_id}')"
            )
            
            if results:
                node = _row_to_node_dict(results[0])
                return _add_query_stats(node, query_stats)
        
        return None
        
    except (QueryTimeoutError, QueryExecutionError) as e:
        return str(e)


def get_callers(node_id: str) -> str:
    """
    Get all nodes that call/reference the specified node.
    
    Args:
        node_id: ID of the target node
    
    Returns:
        JSON string containing list of caller nodes with edge information and query stats
    """
    try:
        with get_connection() as conn:
            # Use UNION to query specific relationship tables for better performance
            query = f"""
                MATCH (caller:CodeNode)-[e:Calls]->(target:CodeNode {{id: $id}})
                RETURN {NODE_PROPERTIES_SELECT.replace('n.', 'caller.')}, 'CALLS' as rel_type, CAST(e.call_site AS STRING) as edge_metadata
                UNION ALL
                MATCH (caller:CodeNode)-[e:References]->(target:CodeNode {{id: $id}})
                RETURN {NODE_PROPERTIES_SELECT.replace('n.', 'caller.')}, 'REFERENCES' as rel_type, CAST(e.reference_type AS STRING) as edge_metadata
                UNION ALL
                MATCH (caller:CodeNode)-[e:Inherits]->(target:CodeNode {{id: $id}})
                RETURN {NODE_PROPERTIES_SELECT.replace('n.', 'caller.')}, 'INHERITS' as rel_type, CAST(e.inheritance_type AS STRING) as edge_metadata
                UNION ALL
                MATCH (caller:CodeNode)-[e:Implements]->(target:CodeNode {{id: $id}})
                RETURN {NODE_PROPERTIES_SELECT.replace('n.', 'caller.')}, 'IMPLEMENTS' as rel_type, CAST(null AS STRING) as edge_metadata
                LIMIT $limit
            """
            
            results, query_stats = _execute_with_timeout(
                conn, query,
                {"id": node_id, "limit": PERFORMANCE_CONFIG["result_limit"]},
                operation_name=f"get_callers('{node_id}')"
            )
            
            callers = []
            for row in results:
                # Extract node properties from the first 17 columns (including metadata)
                node_row = row[:17]
                caller = _row_to_node_dict(node_row)
                # Convert edge metadata to dictionary format expected by C#
                edge_metadata_raw = row[18]  # edge_metadata column
                edge_metadata_dict = {}
                
                if edge_metadata_raw:
                    # If it's a call_site string like "line:80,column:53", parse it
                    if isinstance(edge_metadata_raw, str) and "line:" in edge_metadata_raw:
                        # Parse "line:80,column:53" format
                        parts = edge_metadata_raw.split(',')
                        for part in parts:
                            if ':' in part:
                                key, value = part.split(':', 1)
                                edge_metadata_dict[key.strip()] = value.strip()
                    else:
                        # Try to parse as JSON if it's a string
                        try:
                            if isinstance(edge_metadata_raw, str):
                                parsed = json.loads(edge_metadata_raw)
                                if isinstance(parsed, dict):
                                    edge_metadata_dict = {k: str(v) for k, v in parsed.items()}
                                else:
                                    edge_metadata_dict = {"value": str(edge_metadata_raw)}
                            else:
                                edge_metadata_dict = {"value": str(edge_metadata_raw)}
                        except:
                            edge_metadata_dict = {"value": str(edge_metadata_raw)}
                
                caller['edge_info'] = {
                    "type": row[17],  # rel_type column
                    "metadata": edge_metadata_dict
                }
                callers.append(caller)
        
        return _add_query_stats(callers, query_stats)
        
    except (QueryTimeoutError, QueryExecutionError) as e:
        return str(e)


def get_dependencies(node_id: str) -> str:
    """
    Get all nodes that the specified node depends on.
    
    Args:
        node_id: ID of the source node
    
    Returns:
        JSON string containing list of dependency nodes with query stats
    """
    try:
        with get_connection() as conn:
            # Query specific relationship tables for better performance
            query = f"""
                MATCH (source:CodeNode {{id: $id}})-[e:Calls]->(dep:CodeNode)
                RETURN {NODE_PROPERTIES_SELECT.replace('n.', 'dep.')}, 'CALLS' as rel_type
                UNION ALL
                MATCH (source:CodeNode {{id: $id}})-[e:References]->(dep:CodeNode)
                RETURN {NODE_PROPERTIES_SELECT.replace('n.', 'dep.')}, 'REFERENCES' as rel_type
                UNION ALL
                MATCH (source:CodeNode {{id: $id}})-[e:DependsOn]->(dep:CodeNode)
                RETURN {NODE_PROPERTIES_SELECT.replace('n.', 'dep.')}, e.dependency_type as rel_type
                UNION ALL
                MATCH (source:CodeNode {{id: $id}})-[e:Inherits]->(dep:CodeNode)
                RETURN {NODE_PROPERTIES_SELECT.replace('n.', 'dep.')}, 'INHERITS' as rel_type
                UNION ALL
                MATCH (source:CodeNode {{id: $id}})-[e:Implements]->(dep:CodeNode)
                RETURN {NODE_PROPERTIES_SELECT.replace('n.', 'dep.')}, 'IMPLEMENTS' as rel_type
                LIMIT $limit
            """
            
            results, query_stats = _execute_with_timeout(
                conn, query,
                {"id": node_id, "limit": PERFORMANCE_CONFIG["result_limit"]},
                operation_name=f"get_dependencies('{node_id}')"
            )
            
            deps = []
            for row in results:
                # Extract node properties from the first 17 columns (including metadata)
                node_row = row[:17]
                dep = _row_to_node_dict(node_row)
                dep['relationship_type'] = row[17]  # rel_type column
                deps.append(dep)
        
        return _add_query_stats(deps, query_stats)
        
    except (QueryTimeoutError, QueryExecutionError) as e:
        return str(e)


def get_inheritance_hierarchy(node_id: str) -> str:
    """
    Get inheritance hierarchy for a node (parents and children).
    
    Args:
        node_id: ID of the node
    
    Returns:
        JSON string containing dictionary with 'parents' and 'children' lists and query stats
    """
    try:
        with get_connection() as conn:
            # Get parents
            parents_query = f"""
                MATCH (child:CodeNode {{id: $id}})-[e:Inherits]->(parent:CodeNode)
                RETURN {NODE_PROPERTIES_SELECT.replace('n.', 'parent.')}, 'INHERITS' as rel_type
                UNION ALL
                MATCH (child:CodeNode {{id: $id}})-[e:Implements]->(parent:CodeNode)
                RETURN {NODE_PROPERTIES_SELECT.replace('n.', 'parent.')}, 'IMPLEMENTS' as rel_type
            """
            
            parents_results, parents_stats = _execute_with_timeout(
                conn, parents_query,
                {"id": node_id},
                operation_name=f"get_inheritance_hierarchy_parents('{node_id}')"
            )
            
            # Get children
            children_query = f"""
                MATCH (parent:CodeNode {{id: $id}})<-[e:Inherits]-(child:CodeNode)
                RETURN {NODE_PROPERTIES_SELECT.replace('n.', 'child.')}, 'INHERITS' as rel_type
                UNION ALL
                MATCH (parent:CodeNode {{id: $id}})<-[e:Implements]-(child:CodeNode)
                RETURN {NODE_PROPERTIES_SELECT.replace('n.', 'child.')}, 'IMPLEMENTS' as rel_type
            """
            
            children_results, children_stats = _execute_with_timeout(
                conn, children_query,
                {"id": node_id},
                operation_name=f"get_inheritance_hierarchy_children('{node_id}')"
            )
            
            hierarchy = {
                "parents": [],
                "children": []
            }
            
            # Process parents
            for row in parents_results:
                # Extract node properties from the first 17 columns (including metadata)
                node_row = row[:17]
                parent = _row_to_node_dict(node_row)
                parent['relationship_type'] = row[17]  # rel_type column
                hierarchy["parents"].append(parent)
            
            # Process children
            for row in children_results:
                # Extract node properties from the first 17 columns (including metadata)
                node_row = row[:17]
                child = _row_to_node_dict(node_row)
                child['relationship_type'] = row[17]  # rel_type column
                hierarchy["children"].append(child)
            
            # Combine stats
            combined_stats = {
                "query_time_ms": parents_stats["query_time_ms"] + children_stats["query_time_ms"],
                "nodes_processed": parents_stats["nodes_processed"] + children_stats["nodes_processed"],
                "complexity_score": max(parents_stats["complexity_score"], children_stats["complexity_score"], key=lambda x: ["low", "medium", "high"].index(x))
            }
        
        return _add_query_stats(hierarchy, combined_stats)
        
    except (QueryTimeoutError, QueryExecutionError) as e:
        return str(e)


def find_related_nodes(node_id: str, depth: int = 1, 
                      relationship_types: Optional[str] = None) -> str:
    """
    Find related nodes with bounded traversal and optimizations.
    
    Args:
        node_id: ID of the starting node
        depth: Maximum traversal depth (capped at max_traversal_depth)
        relationship_types: JSON string of relationship types to follow
    
    Returns:
        JSON string containing related nodes with distance information and query stats
    """
    # Cap depth to prevent runaway queries
    depth = min(depth, PERFORMANCE_CONFIG["max_traversal_depth"])
    
    # Check cache
    cache_key = _cache_key("related", id=node_id, depth=depth, types=relationship_types)
    cached = _get_cached_result(cache_key)
    if cached is not None:
        query_stats = {"cache_hit": True, "query_time_ms": 0}
        return _add_query_stats(json.loads(cached), query_stats)
    
    try:
        with get_connection() as conn:
            # Build relationship filter
            if relationship_types:
                types_list = json.loads(relationship_types)
                # Map to actual relationship tables
                rel_filters = []
                for rel_type in types_list:
                    if rel_type == "CALLS":
                        rel_filters.append("Calls")
                    elif rel_type in ["INHERITS", "EXTENDS"]:
                        rel_filters.append("Inherits")
                    elif rel_type == "IMPLEMENTS":
                        rel_filters.append("Implements")
                    elif rel_type == "CONTAINS":
                        rel_filters.append("Contains")
                    elif rel_type in ["REFERENCES", "USES"]:
                        rel_filters.append("References")
                    elif rel_type in ["IMPORTS", "DEPENDS_ON"]:
                        rel_filters.append("DependsOn")
                    else:
                        rel_filters.append("CodeEdge")
                
                # Build relationship pattern
                rel_pattern = "|".join(rel_filters)
                query = f"""
                    MATCH path = (start:CodeNode {{id: $id}})-[:{rel_pattern}*1..{depth}]-(related:CodeNode)
                    WHERE related.id != $id
                    WITH DISTINCT related, min(length(path)) as min_distance
                    RETURN {NODE_PROPERTIES_SELECT.replace('n.', 'related.')}, min_distance
                    ORDER BY min_distance, related.name
                    LIMIT $limit
                """
            else:
                # Warning: untyped relationships can be slow
                logger.warning(f"find_related_nodes called without relationship types - may be slow")
                query = f"""
                    MATCH path = (start:CodeNode {{id: $id}})-[:Calls|Inherits|Implements|References|DependsOn*1..{depth}]-(related:CodeNode)
                    WHERE related.id != $id
                    WITH DISTINCT related, min(length(path)) as min_distance
                    RETURN {NODE_PROPERTIES_SELECT.replace('n.', 'related.')}, min_distance
                    ORDER BY min_distance, related.name
                    LIMIT $limit
                """
            
            results, query_stats = _execute_with_timeout(
                conn, query,
                {"id": node_id, "limit": PERFORMANCE_CONFIG["result_limit"]},
                operation_name=f"find_related_nodes('{node_id}', depth={depth})"
            )
            
            related_nodes = []
            for row in results:
                # Extract node properties from the first 17 columns (including metadata)
                node_row = row[:17]
                node = _row_to_node_dict(node_row)
                node['distance'] = row[17]  # min_distance column
                related_nodes.append(node)
        
        json_result = json.dumps(related_nodes)
        _set_cached_result(cache_key, json_result)
        query_stats["cache_hit"] = False
        return _add_query_stats(related_nodes, query_stats)
        
    except (QueryTimeoutError, QueryExecutionError) as e:
        return str(e)


def delete_node(node_id: str) -> None:
    """
    Delete a specific node by its ID (DETACH DELETE for safety).
    
    Args:
        node_id: The node's unique identifier
    """
    with get_connection() as conn:
        conn.execute("""
            MATCH (n:CodeNode {id: $id})
            DETACH DELETE n
        """, {"id": node_id})
    
    clear_cache()


def delete_edges_by_node(node_id: str) -> None:
    """
    Delete all edges connected to a specific node.
    
    Args:
        node_id: The node's unique identifier
    """
    with get_connection() as conn:
        # Delete all edges in one query using DETACH pattern
        conn.execute("""
            MATCH (n:CodeNode {id: $id})-[e]-()
            DELETE e
        """, {"id": node_id})
    
    clear_cache()


def clear_database() -> None:
    """Clear all data from the database using DETACH DELETE."""
    with get_connection() as conn:
        conn.execute("MATCH (n) DETACH DELETE n")
    
    clear_cache()


def get_statistics() -> str:
    """
    Get detailed database statistics with performance metrics.
    
    Returns:
        JSON string containing comprehensive statistics with query stats
    """
    try:
        with get_connection() as conn:
            stats = {}
            total_time = 0
            
            # Get node count with timeout protection
            results, node_stats = _execute_with_timeout(
                conn, 
                "MATCH (n:CodeNode) RETURN COUNT(n) as count",
                timeout_ms=5000,
                operation_name="get_statistics_nodes"
            )
            if results:
                stats["CodeNode_count"] = results[0][0]
            total_time += node_stats["query_time_ms"]
            
            # Get file metadata count
            results, file_stats = _execute_with_timeout(
                conn,
                "MATCH (f:FileMetadata) RETURN COUNT(f) as count",
                timeout_ms=5000,
                operation_name="get_statistics_files"
            )
            if results:
                stats["FileMetadata_count"] = results[0][0]
            total_time += file_stats["query_time_ms"]
            
            # Get edge counts by type (more efficient than counting all edges)
            edge_types = ["Calls", "Inherits", "Implements", "Contains", "References", "DependsOn", "CodeEdge"]
            edge_counts = {}
            
            for edge_type in edge_types:
                results, edge_stats = _execute_with_timeout(
                    conn,
                    f"MATCH ()-[e:{edge_type}]->() RETURN COUNT(e) as count",
                    timeout_ms=2000,
                    operation_name=f"get_statistics_edges_{edge_type}"
                )
                if results:
                    edge_counts[edge_type] = results[0][0]
                total_time += edge_stats["query_time_ms"]
            
            stats["edge_counts"] = edge_counts
            stats["total_edges"] = sum(edge_counts.values())
            
            # Get node type distribution (limited to prevent long query)
            results, type_stats = _execute_with_timeout(
                conn,
                """
                MATCH (n:CodeNode)
                WITH n.type as node_type, COUNT(n) as count
                ORDER BY count DESC
                LIMIT 20
                RETURN node_type, count
                """,
                timeout_ms=5000,
                operation_name="get_statistics_types"
            )
            
            type_distribution = {}
            for row in results:
                if row[0]:  # Skip null types
                    type_distribution[row[0]] = row[1]
            stats["type_distribution"] = type_distribution
            total_time += type_stats["query_time_ms"]
            
            # Get language distribution
            results, lang_stats = _execute_with_timeout(
                conn,
                """
                MATCH (n:CodeNode)
                WHERE n.language IS NOT NULL
                WITH n.language as lang, COUNT(n) as count
                ORDER BY count DESC
                RETURN lang, count
                """,
                timeout_ms=5000,
                operation_name="get_statistics_languages"
            )
            
            language_distribution = {}
            for row in results:
                language_distribution[row[0]] = row[1]
            stats["language_distribution"] = language_distribution
            total_time += lang_stats["query_time_ms"]
            
            # Add performance metrics
            stats["performance"] = {
                "num_threads": PERFORMANCE_CONFIG["num_threads"],
                "buffer_pool_size_gb": PERFORMANCE_CONFIG["buffer_pool_size"] / (1024**3),
                "cache_stats": _cache_stats.copy(),
                "cache_memory_mb": _cache_memory_bytes / (1024**2),
                "connection_pool_size": len(_connection_pool),
                "query_timeout_ms": PERFORMANCE_CONFIG["query_timeout"],
                "max_traversal_depth": PERFORMANCE_CONFIG["max_traversal_depth"]
            }
            
            query_stats = {
                "query_time_ms": total_time,
                "complexity_score": "low"
            }
        
        return _add_query_stats(stats, query_stats)
        
    except (QueryTimeoutError, QueryExecutionError) as e:
        return str(e)


# FileMetadata management functions

def upsert_file_metadata(metadata_json: str) -> None:
    """
    Insert or update file metadata using MERGE.
    
    Args:
        metadata_json: JSON string containing file metadata
    """
    metadata = json.loads(metadata_json)
    with get_connection() as conn:
        conn.execute("""
            MERGE (f:FileMetadata {file_path: $file_path})
            SET f.last_modified = TIMESTAMP($last_modified),
                f.last_scanned = TIMESTAMP($last_scanned),
                f.file_hash = $file_hash,
                f.status = $status,
                f.error_message = $error_message,
                f.node_count = $node_count,
                f.edge_count = $edge_count,
                f.language = $language,
                f.size_bytes = $size_bytes
        """, {
            "file_path": metadata.get("file_path"),
            "last_modified": metadata.get("last_modified"),
            "last_scanned": metadata.get("last_scanned"),
            "file_hash": metadata.get("file_hash"),
            "status": metadata.get("status"),
            "error_message": metadata.get("error_message"),
            "node_count": metadata.get("node_count", 0),
            "edge_count": metadata.get("edge_count", 0),
            "language": metadata.get("language"),
            "size_bytes": metadata.get("size_bytes", 0)
        })


def get_file_metadata(file_path: str) -> str:
    """
    Get file metadata by file path using primary key lookup.
    
    Args:
        file_path: Path to the file
        
    Returns:
        JSON string containing file metadata with query stats, or null if not found
    """
    try:
        with get_connection() as conn:
            query = """
                MATCH (f:FileMetadata {file_path: $file_path})
                RETURN f
                LIMIT 1
            """
            
            results, query_stats = _execute_with_timeout(
                conn, query,
                {"file_path": file_path},
                timeout_ms=100,  # Fast for PK lookup
                operation_name=f"get_file_metadata('{file_path}')"
            )
            
            if results:
                metadata = _file_metadata_to_dict(results[0][0])
                return _add_query_stats(metadata, query_stats)
        
        return json.dumps(None)
        
    except (QueryTimeoutError, QueryExecutionError) as e:
        return str(e)


def get_all_file_metadata() -> str:
    """
    Get all file metadata with limit to prevent memory issues.
    
    Returns:
        JSON string containing array of all file metadata with query stats
    """
    try:
        with get_connection() as conn:
            query = """
                MATCH (f:FileMetadata)
                RETURN f
                ORDER BY f.file_path
                LIMIT $limit
            """
            
            results, query_stats = _execute_with_timeout(
                conn, query,
                {"limit": PERFORMANCE_CONFIG["large_result_limit"]},
                operation_name="get_all_file_metadata"
            )
            
            metadata_list = [_file_metadata_to_dict(row[0]) for row in results]
        
        return _add_query_stats(metadata_list, query_stats)
        
    except (QueryTimeoutError, QueryExecutionError) as e:
        return str(e)


def get_file_metadata_by_status(status: str) -> str:
    """
    Get file metadata by status with limit.
    
    Args:
        status: Processing status to filter by
        
    Returns:
        JSON string containing array of matching file metadata with query stats
    """
    try:
        with get_connection() as conn:
            query = """
                MATCH (f:FileMetadata {status: $status})
                RETURN f
                ORDER BY f.file_path
                LIMIT $limit
            """
            
            results, query_stats = _execute_with_timeout(
                conn, query,
                {"status": status, "limit": PERFORMANCE_CONFIG["result_limit"]},
                operation_name=f"get_file_metadata_by_status('{status}')"
            )
            
            metadata_list = [_file_metadata_to_dict(row[0]) for row in results]
        
        return _add_query_stats(metadata_list, query_stats)
        
    except (QueryTimeoutError, QueryExecutionError) as e:
        return str(e)


def delete_file_metadata(file_path: str) -> None:
    """
    Delete file metadata by file path.
    
    Args:
        file_path: Path to the file
    """
    with get_connection() as conn:
        conn.execute("""
            MATCH (f:FileMetadata {file_path: $file_path})
            DELETE f
        """, {"file_path": file_path})


def clear_file_metadata() -> None:
    """Clear all file metadata."""
    with get_connection() as conn:
        conn.execute("MATCH (f:FileMetadata) DELETE f")


def reconcile_and_prune_graph(nodes_json: str, edges_json: str) -> str:
    """
    Reconcile and prune the entire graph to match the provided state.
    
    This function performs a complete synchronization:
    1. MERGE all nodes (upsert existing, insert new)
    2. MERGE all edges (upsert existing, insert new) 
    3. DELETE stale nodes that are no longer in the current graph
    
    Args:
        nodes_json: JSON string containing array of all current nodes
        edges_json: JSON string containing array of all current edges
        
    Returns:
        JSON string containing operation statistics with query stats
    """
    nodes = json.loads(nodes_json)
    edges = json.loads(edges_json)
    
    logger.info(f"Starting graph reconciliation: {len(nodes)} nodes, {len(edges)} edges")
    
    stats = {
        "nodes_merged": 0,
        "edges_merged": 0,
        "nodes_deleted": 0,
        "operation": "reconcile_and_prune"
    }
    
    start_time = time.time()
    
    try:
        with get_connection() as conn:
            # Step 1: MERGE all nodes using UNWIND for performance
            for i in range(0, len(nodes), PERFORMANCE_CONFIG["batch_size"]):
                batch = nodes[i:i + PERFORMANCE_CONFIG["batch_size"]]
                
                conn.execute("""
                    UNWIND $nodes AS node
                    MERGE (n:CodeNode {id: node.id})
                    SET n.name = node.name,
                        n.type = node.type,
                        n.language = node.language,
                        n.file_path = node.file_path,
                        n.start_line = node.start_line,
                        n.end_line = node.end_line,
                        n.start_col = node.start_col,
                        n.end_col = node.end_col,
                        n.namespace = node.namespace,
                        n.visibility = node.visibility,
                        n.signature = node.signature
                """, {"nodes": batch})
                
                stats["nodes_merged"] += len(batch)
            
            # Step 2: Process edges more efficiently by delegating to insert_edges_batch
            insert_edges_batch(json.dumps(edges))
            stats["edges_merged"] = len(edges)
            
            # Step 3: Delete stale nodes
            live_node_ids = [node.get("id") for node in nodes if node.get("id")]
            
            if live_node_ids:
                # Process deletion in batches to avoid query size limits
                for i in range(0, len(live_node_ids), 1000):
                    batch_ids = live_node_ids[i:i + 1000]
                    
                    # Get count of nodes to be deleted in this batch
                    results, _ = _execute_with_timeout(
                        conn,
                        """
                        MATCH (n:CodeNode)
                        WHERE NOT n.id IN $live_ids
                        RETURN COUNT(n)
                        """,
                        {"live_ids": batch_ids},
                        timeout_ms=5000,
                        operation_name="reconcile_count_deletions"
                    )
                    
                    if results:
                        stats["nodes_deleted"] += results[0][0]
                    
                    # Delete stale nodes with DETACH DELETE
                    conn.execute("""
                        MATCH (n:CodeNode)
                        WHERE NOT n.id IN $live_ids
                        DETACH DELETE n
                    """, {"live_ids": batch_ids})
            else:
                # If no live nodes, count and delete everything
                results, _ = _execute_with_timeout(
                    conn,
                    "MATCH (n:CodeNode) RETURN COUNT(n)",
                    timeout_ms=5000,
                    operation_name="reconcile_count_all"
                )
                if results:
                    stats["nodes_deleted"] = results[0][0]
                conn.execute("MATCH (n:CodeNode) DETACH DELETE n")
        
        execution_time = time.time() - start_time
        query_stats = {
            "query_time_ms": int(execution_time * 1000),
            "complexity_score": "high" if len(nodes) + len(edges) > 10000 else "medium"
        }
        
        clear_cache()
        return _add_query_stats(stats, query_stats)
        
    except Exception as e:
        logger.error(f"Reconciliation failed: {str(e)}")
        error_response = {
            "error": True,
            "error_type": "reconciliation_error",
            "message": f"Graph reconciliation failed: {str(e)}",
            "suggestions": ["Check data format", "Verify node IDs are unique"],
            "partial_stats": stats
        }
        return json.dumps(error_response)


def get_file_metadata_count_by_status(status: str) -> str:
    """
    Get count of file metadata by status.
    
    Args:
        status: Processing status to count
        
    Returns:
        JSON string containing count of files with the given status and query stats
    """
    try:
        with get_connection() as conn:
            query = """
                MATCH (f:FileMetadata {status: $status})
                RETURN COUNT(f) as count
            """
            
            results, query_stats = _execute_with_timeout(
                conn, query,
                {"status": status},
                timeout_ms=1000,
                operation_name=f"get_file_metadata_count_by_status('{status}')"
            )
            
            count = results[0][0] if results else 0
        
        return _add_query_stats({"count": count}, query_stats)
        
    except (QueryTimeoutError, QueryExecutionError) as e:
        return str(e)


def get_all_edges() -> str:
    """
    Get all edges from the database with pagination support.
    
    Returns:
        JSON string containing list of all edges with query stats
    """
    try:
        with get_connection() as conn:
            # Query each edge type separately to avoid performance issues
            all_edges = []
            total_time = 0
            
            edge_queries = [
                ("Calls", "MATCH (s:CodeNode)-[e:Calls]->(t:CodeNode) RETURN s.id, t.id, e"),
                ("Inherits", "MATCH (s:CodeNode)-[e:Inherits]->(t:CodeNode) RETURN s.id, t.id, e"),
                ("Implements", "MATCH (s:CodeNode)-[e:Implements]->(t:CodeNode) RETURN s.id, t.id, e"),
                ("Contains", "MATCH (s:CodeNode)-[e:Contains]->(t:CodeNode) RETURN s.id, t.id, e"),
                ("References", "MATCH (s:CodeNode)-[e:References]->(t:CodeNode) RETURN s.id, t.id, e"),
                ("DependsOn", "MATCH (s:CodeNode)-[e:DependsOn]->(t:CodeNode) RETURN s.id, t.id, e"),
                ("CodeEdge", "MATCH (s:CodeNode)-[e:CodeEdge]->(t:CodeNode) RETURN s.id, t.id, e")
            ]
            
            for edge_type, query in edge_queries:
                results, edge_stats = _execute_with_timeout(
                    conn,
                    f"{query} LIMIT $limit",
                    {"limit": 1000},
                    timeout_ms=2000,
                    operation_name=f"get_all_edges_{edge_type}"
                )
                
                total_time += edge_stats["query_time_ms"]
                
                for row in results:
                    edge = {
                        "source_id": row[0],
                        "target_id": row[1],
                        "type": edge_type
                    }
                    
                    # Add edge-specific properties
                    if edge_type == "CodeEdge":
                        edge["id"] = getattr(row[2], "id", None)
                        edge["metadata"] = getattr(row[2], "metadata", None)
                    elif edge_type == "Calls":
                        edge["call_site"] = getattr(row[2], "call_site", None)
                        edge["is_async"] = getattr(row[2], "is_async", None)
                    elif edge_type == "Contains":
                        edge["order_index"] = getattr(row[2], "order_index", None)
                    
                    all_edges.append(edge)
            
            query_stats = {
                "query_time_ms": total_time,
                "edges_retrieved": len(all_edges),
                "complexity_score": "medium"
            }
        
        return _add_query_stats(all_edges, query_stats)
        
    except (QueryTimeoutError, QueryExecutionError) as e:
        return str(e)


def close_database() -> None:
    """Close all database connections and cleanup."""
    global _db, _connection_pool
    
    with _pool_lock:
        _connection_pool.clear()
    
    _db = None
    clear_cache()
    
    logger.info("Database connections closed")


def clear_cache() -> None:
    """Clear the query result cache."""
    global _cache_memory_bytes
    
    with _cache_lock:
        _query_cache.clear()
        _cache_stats["hits"] = 0
        _cache_stats["misses"] = 0
        _cache_stats["evictions"] = 0
        _cache_memory_bytes = 0
    
    logger.info("Query cache cleared")