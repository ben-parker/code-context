using System;
using System.Collections.Generic;
using System.Linq;
using CodeContext.Core;
using CodeContext.Core.Repositories;
using CodeContext.Core.Services;
using NSubstitute;

namespace CodeContext.Core.Tests.Services
{
    /// <summary>
    /// Test wiring for the node repository's <see cref="ICodeNodeRepository.FindByFilePathAsync"/>
    /// and <see cref="ICodeNodeRepository.GetFilePathsAsync"/>.
    /// Production resolves it through the in-memory adjacency path index, which is set-equivalent to a
    /// brute-force <see cref="FilePathMatcher.Matches"/> scan of every node. For mocked repositories we
    /// reproduce exactly that: filter the substitute's own <see cref="ICodeNodeRepository.GetAllAsync"/>
    /// node set by <see cref="FilePathMatcher.Matches"/> plus the same case-insensitive (ordinal) type
    /// filter <c>ContextService</c> applied pre-index — so the mock stays faithful to the real index
    /// without pinning an enumeration order the production index does not promise.
    /// </summary>
    internal static class NodeRepositoryPathMock
    {
        public static void StubFindByFilePathFromGetAll(this ICodeNodeRepository repo)
        {
            repo.FindByFilePathAsync(Arg.Any<string>(), Arg.Any<string?>()).Returns(call =>
            {
                var path = call.ArgAt<string>(0);
                var type = call.ArgAt<string?>(1);
                var all = repo.GetAllAsync().GetAwaiter().GetResult()
                    ?? (IReadOnlyList<CodeNode>)Array.Empty<CodeNode>();

                IEnumerable<CodeNode> matches = all.Where(n => FilePathMatcher.Matches(n.FilePath, path));
                if (!string.IsNullOrEmpty(type))
                {
                    matches = matches.Where(n =>
                        string.Equals(n.Type, type, StringComparison.OrdinalIgnoreCase));
                }

                return (IReadOnlyList<CodeNode>)matches.ToList();
            });

            // GetFilePathsAsync is defined over the same node set: distinct (ordinal) non-empty
            // paths, ordered OrdinalIgnoreCase then Ordinal.
            repo.GetFilePathsAsync().Returns(_ =>
            {
                var all = repo.GetAllAsync().GetAwaiter().GetResult()
                    ?? (IReadOnlyList<CodeNode>)Array.Empty<CodeNode>();
                return (IReadOnlyList<string>)all
                    .Select(n => n.FilePath)
                    .Where(path => !string.IsNullOrEmpty(path))
                    .Select(path => path!)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .ToList();
            });
        }
    }
}
