"""
CodeContext Python Kuzu Repository Implementation

This module provides a Kuzu graph database backend for CodeContext,
implementing the repository pattern to store and query code graphs.
"""

from models import (
    CodeNode,
    CodeEdge,
    CodeGraph,
    FileMetadata,
    FileProcessingStatus
)

from kuzu_repositories import (
    KuzuDatabase,
    KuzuNodeRepository,
    KuzuEdgeRepository,
    KuzuGraphRepository,
    KuzuFileMetadataRepository,
    KuzuRepositoryFactory
)

__all__ = [
    # Models
    'CodeNode',
    'CodeEdge',
    'CodeGraph',
    'FileMetadata',
    'FileProcessingStatus',
    # Repositories
    'KuzuDatabase',
    'KuzuNodeRepository',
    'KuzuEdgeRepository',
    'KuzuGraphRepository',
    'KuzuFileMetadataRepository',
    'KuzuRepositoryFactory'
]

__version__ = '1.0.0'