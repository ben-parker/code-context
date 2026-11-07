using System.Collections.Generic;
using System.Threading.Tasks;

namespace CodeContext.Core.Repositories
{
    /// <summary>
    /// Node reads. Every list is in canonical order: ordinal by node id within a shard, shards in
    /// (parserId, workspaceId) ordinal order, unless a member documents another order.
    /// </summary>
    public interface ICodeNodeRepository
    {
        Task<CodeNode?> GetByIdAsync(string id);
        Task<CodeNode?> GetByIdentifierAsync(string identifier);
        /// <summary>
        /// Finds nodes by name. The optional <paramref name="type"/> filter is matched
        /// case-insensitively (ordinal) — this is part of the interface contract.
        /// </summary>
        Task<IReadOnlyList<CodeNode>> FindByNameAsync(string name, string? type = null, bool exact = false);
        /// <summary>
        /// Finds nodes whose file path matches <paramref name="filePath"/> using the in-memory
        /// adjacency path index (rooted = exact, relative = exact-or-suffix, byte-identical to
        /// <c>FilePathMatcher.Matches</c>). The optional <paramref name="type"/> filter is matched
        /// case-insensitively (ordinal), mirroring <see cref="FindByNameAsync"/>.
        /// </summary>
        Task<IReadOnlyList<CodeNode>> FindByFilePathAsync(string filePath, string? type = null);
        Task<IReadOnlyList<CodeNode>> GetAllAsync();
        /// <summary>
        /// The distinct (ordinal) non-empty file paths of all nodes, ordered
        /// <see cref="System.StringComparer.OrdinalIgnoreCase"/> then <see cref="System.StringComparer.Ordinal"/>.
        /// </summary>
        Task<IReadOnlyList<string>> GetFilePathsAsync();
        Task UpsertAsync(CodeNode node);
        Task DeleteAsync(string id, CancellationToken cancellationToken);
    }
}
