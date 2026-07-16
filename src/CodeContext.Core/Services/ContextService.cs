using CodeContext.Core.Repositories;
using Microsoft.Extensions.Options;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CodeContext.Core.Services
{
    public class ContextService : IContextService
    {
        private readonly ICodeNodeRepository _nodeRepository;
        private readonly ICodeEdgeRepository _edgeRepository;
        private readonly IFileMetadataRepository _fileMetadataRepository;
        private readonly string? _rootPath;

        public ContextService(
            ICodeNodeRepository nodeRepository,
            ICodeEdgeRepository edgeRepository,
            IFileMetadataRepository fileMetadataRepository,
            IOptions<CodeContextOptions>? options = null)
        {
            _nodeRepository = nodeRepository;
            _edgeRepository = edgeRepository;
            _fileMetadataRepository = fileMetadataRepository;
            _rootPath = options?.Value.RootPath;
        }

        public async Task<CompleteContextResponse> GetCompleteContextAsync(
            string identifier,
            string? type = null,
            int depth = 2,
            bool includeTests = true,
            bool includeContent = false,
            bool exact = false,
            bool includeRelated = true,
            bool includeMetrics = true,
            string? qualifiedIdentifier = null,
            string? containingType = null,
            string? @namespace = null,
            string? signature = null,
            string? sourceFile = null)
        {
            if (depth is < 0 or > 10)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(depth), depth, "Relationship depth must be between 0 and 10.");
            }

            var response = new CompleteContextResponse();
            var (candidates, isFilePath) = await FindCandidatesAsync(
                identifier, type, exact, qualifiedIdentifier, containingType, @namespace, signature, sourceFile);

            if (candidates.Count == 0)
            {
                response.DisambiguationHint = $"No matches found for identifier '{identifier}'";
                return response;
            }

            // Handle ambiguity
            if (candidates.Count > 1 && !isFilePath)
            {
                response.DisambiguationHint = BuildDisambiguationHint(candidates);
            }

            // Build context for each candidate
            foreach (var candidate in candidates)
            {
                var contextMatch = await BuildContextMatchAsync(
                    candidate, depth, includeTests, includeContent, includeRelated, includeMetrics);
                response.Matches.Add(contextMatch);
            }

            return response;
        }

        public async Task<CompactContextResponse> GetCompactContextAsync(
            string identifier,
            string? type = null,
            int depth = 1,
            bool includeTests = false,
            bool includeContent = false,
            bool? exact = null,
            bool includeRelated = false,
            bool includeMetrics = false,
            int maxMatches = 5,
            int maxRelationships = 10,
            bool expandAmbiguous = false,
            int maxCallSites = 3,
            string? qualifiedIdentifier = null,
            string? containingType = null,
            string? @namespace = null,
            string? signature = null,
            string? sourceFile = null)
        {
            ValidateQueryBounds(depth, maxMatches, maxRelationships, maxCallSites);

            List<CodeNode> candidates;
            bool isFilePath;
            string matchMode;
            var substringSearchSkipped = false;
            if (exact is null && !IsFilePath(identifier))
            {
                (candidates, isFilePath) = await FindCandidatesAsync(
                    identifier, type, exact: true, qualifiedIdentifier, containingType, @namespace, signature, sourceFile);
                if (candidates.Count == 0)
                {
                    (candidates, isFilePath) = await FindCandidatesAsync(
                        identifier, type, exact: false, qualifiedIdentifier, containingType, @namespace, signature, sourceFile);
                    matchMode = "substring";
                }
                else
                {
                    matchMode = "exact";
                    substringSearchSkipped = true;
                }
            }
            else
            {
                (candidates, isFilePath) = await FindCandidatesAsync(
                    identifier, type, exact ?? false, qualifiedIdentifier, containingType, @namespace, signature, sourceFile);
                matchMode = isFilePath ? "filePath" : exact == true ? "exact" : "substring";
            }

            candidates = RankCandidates(candidates, identifier).ToList();
            var response = new CompactContextResponse
            {
                MatchMode = matchMode,
                SubstringSearchSkipped = substringSearchSkipped,
                TotalMatches = candidates.Count,
                Ambiguous = candidates.Count > 1,
                Facets = candidates.Count > 1 ? BuildFacets(candidates) : null
            };

            if (candidates.Count == 0)
            {
                response.DisambiguationHint = $"No matches found for identifier '{identifier}'";
                return response;
            }

            if (response.Ambiguous && !expandAmbiguous)
            {
                response.DisambiguationHint = isFilePath
                    ? "File contains multiple symbols. Specify 'type' or a symbol name to expand one."
                    : BuildDisambiguationHint(candidates);
                response.Matches = candidates.Take(maxMatches)
                    .Select(candidate => new CompactContextMatch { Target = ToCompactNode(candidate, includeQualifiedIdentity: true) })
                    .ToList();
            }
            else
            {
                foreach (var candidate in candidates.Take(maxMatches))
                {
                    var match = await BuildContextMatchAsync(
                        candidate, depth, includeTests, includeContent, includeRelated, includeMetrics);
                    response.Matches.Add(await ToCompactMatchAsync(
                        match, includeTests, includeRelated, includeMetrics, maxRelationships,
                        maxCallSites, response.Ambiguous));
                }
            }

            response.ReturnedMatches = response.Matches.Count;
            response.Truncated = response.ReturnedMatches < response.TotalMatches;
            return response;
        }

        public async Task<List<CompleteContextResponse>> GetMultipleContextAsync(MultiContextRequest request)
        {
            var results = new List<CompleteContextResponse>();

            foreach (var identifier in request.Identifiers)
            {
                var context = await GetCompleteContextAsync(
                    identifier,
                    type: request.Type,
                    depth: request.Depth,
                    includeTests: request.IncludeTests,
                    includeContent: request.IncludeContent,
                    exact: request.Exact ?? false,
                    includeRelated: request.IncludeRelated,
                    includeMetrics: request.IncludeMetrics,
                    qualifiedIdentifier: request.QualifiedIdentifier,
                    containingType: request.ContainingType,
                    @namespace: request.Namespace,
                    signature: request.Signature,
                    sourceFile: request.SourceFile);

                results.Add(context);
            }

            return results;
        }

        public async Task<List<CompactContextResponse>> GetMultipleCompactContextAsync(MultiContextRequest request)
        {
            var results = new List<CompactContextResponse>();
            foreach (var identifier in request.Identifiers)
            {
                results.Add(await GetCompactContextAsync(
                    identifier,
                    type: request.Type,
                    depth: request.Depth,
                    includeTests: request.IncludeTests,
                    includeContent: request.IncludeContent,
                    exact: request.Exact,
                    includeRelated: request.IncludeRelated,
                    includeMetrics: request.IncludeMetrics,
                    maxMatches: request.MaxMatches,
                    maxRelationships: request.MaxRelationships,
                    expandAmbiguous: request.ExpandAmbiguous,
                    maxCallSites: request.MaxCallSites,
                    qualifiedIdentifier: request.QualifiedIdentifier,
                    containingType: request.ContainingType,
                    @namespace: request.Namespace,
                    signature: request.Signature,
                    sourceFile: request.SourceFile));
            }

            return results;
        }

        private async Task<(List<CodeNode> Candidates, bool IsFilePath)> FindCandidatesAsync(
            string identifier,
            string? type,
            bool exact,
            string? qualifiedIdentifier = null,
            string? containingType = null,
            string? @namespace = null,
            string? signature = null,
            string? sourceFile = null)
        {
            var isFilePath = IsFilePath(identifier);
            var candidates = isFilePath
                ? await FindNodesByFilePathAsync(identifier, type)
                : await _nodeRepository.FindByNameAsync(identifier, type, exact);
            candidates = candidates.Where(candidate =>
                    (qualifiedIdentifier is null || string.Equals(
                        GetQualifiedIdentifier(candidate), qualifiedIdentifier, StringComparison.OrdinalIgnoreCase))
                    && (containingType is null || string.Equals(
                        GetContainingType(candidate), containingType, StringComparison.OrdinalIgnoreCase))
                    && (@namespace is null || string.Equals(candidate.Namespace, @namespace, StringComparison.OrdinalIgnoreCase))
                    && (signature is null || string.Equals(candidate.Signature, signature, StringComparison.OrdinalIgnoreCase))
                    && (sourceFile is null || FilePathMatches(candidate.FilePath, sourceFile)))
                .ToList();
            return (candidates, isFilePath);
        }

        private static void ValidateQueryBounds(int depth, int maxMatches, int maxRelationships, int maxCallSites)
        {
            if (depth is < 0 or > 10)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(depth), depth, "Relationship depth must be between 0 and 10.");
            }
            if (maxMatches is < 1 or > 50)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxMatches), maxMatches, "maxMatches must be between 1 and 50.");
            }
            if (maxRelationships is < 1 or > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxRelationships), maxRelationships, "maxRelationships must be between 1 and 100.");
            }
            if (maxCallSites is < 0 or > 100)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxCallSites), maxCallSites, "maxCallSites must be between 0 and 100.");
            }
        }

        private static IOrderedEnumerable<CodeNode> RankCandidates(
            IEnumerable<CodeNode> candidates,
            string identifier)
            => candidates
                .OrderByDescending(candidate =>
                    string.Equals(candidate.Name, identifier, StringComparison.OrdinalIgnoreCase))
                .ThenBy(candidate => IsTestPath(candidate.FilePath))
                .ThenBy(candidate => TypeRank(candidate.Type))
                .ThenBy(candidate => candidate.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.StartLine);

        private CompactMatchFacets BuildFacets(IReadOnlyCollection<CodeNode> candidates)
        {
            var files = candidates
                .Where(candidate => !string.IsNullOrEmpty(candidate.FilePath))
                .GroupBy(candidate => ToDisplayPath(candidate.FilePath!), StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new CompactMatchFacets
            {
                Types = candidates
                    .GroupBy(candidate => candidate.Type ?? "Unknown", StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(group => group.Count())
                    .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
                Files = files.Take(10)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase),
                TotalFiles = files.Count
            };
        }

        private async Task<CompactContextMatch> ToCompactMatchAsync(
            ContextMatch match,
            bool includeTests,
            bool includeRelated,
            bool includeMetrics,
            int maxRelationships,
            int maxCallSites,
            bool includeQualifiedIdentity)
        {
            var relationships = match.Relationships;
            var usedByNodes = await FilterCompactUsedByAsync(match.Target, relationships.UsedBy);
            var compactRelationships = new CompactRelationships();
            if (relationships.Uses.Count > 0)
            {
                compactRelationships.Uses = await ToCompactRelatedNodesAsync(
                    match.Target, relationships.Uses, outgoing: true, maxRelationships, maxCallSites);
                compactRelationships.UsesCount = relationships.Uses.Count;
                compactRelationships.UsesReturnedCount = compactRelationships.Uses.Count;
                compactRelationships.UsesTruncated = relationships.Uses.Count > maxRelationships;
                compactRelationships.Truncated |= compactRelationships.UsesTruncated;
            }
            if (usedByNodes.Count > 0)
            {
                compactRelationships.UsedBy = await ToCompactRelatedNodesAsync(
                    match.Target, usedByNodes, outgoing: false, maxRelationships, maxCallSites);
                compactRelationships.UsedByCount = usedByNodes.Count;
                compactRelationships.UsedByReturnedCount = compactRelationships.UsedBy.Count;
                compactRelationships.UsedByTruncated = usedByNodes.Count > maxRelationships;
                compactRelationships.Truncated |= compactRelationships.UsedByTruncated;
            }
            if (relationships.TransitiveUses.Count > 0)
            {
                compactRelationships.TransitiveUses = relationships.TransitiveUses
                    .Take(maxRelationships).Select(ToCompactTransitiveNode).ToList();
                compactRelationships.TransitiveUsesCount = relationships.TransitiveUses.Count;
                compactRelationships.TransitiveUsesReturnedCount = compactRelationships.TransitiveUses.Count;
                compactRelationships.TransitiveUsesTruncated = relationships.TransitiveUses.Count > maxRelationships;
                compactRelationships.Truncated |= compactRelationships.TransitiveUsesTruncated;
            }
            if (relationships.TransitiveUsedBy.Count > 0)
            {
                compactRelationships.TransitiveUsedBy = relationships.TransitiveUsedBy
                    .Take(maxRelationships).Select(ToCompactTransitiveNode).ToList();
                compactRelationships.TransitiveUsedByCount = relationships.TransitiveUsedBy.Count;
                compactRelationships.TransitiveUsedByReturnedCount = compactRelationships.TransitiveUsedBy.Count;
                compactRelationships.TransitiveUsedByTruncated = relationships.TransitiveUsedBy.Count > maxRelationships;
                compactRelationships.Truncated |= compactRelationships.TransitiveUsedByTruncated;
            }
            if (relationships.Dependencies.Count > 0)
            {
                compactRelationships.Dependencies = relationships.Dependencies
                    .Take(maxRelationships).Select(ToDisplayPath).ToList();
                compactRelationships.DependenciesCount = relationships.Dependencies.Count;
                compactRelationships.DependenciesReturnedCount = compactRelationships.Dependencies.Count;
                compactRelationships.DependenciesTruncated = relationships.Dependencies.Count > maxRelationships;
                compactRelationships.Truncated |= compactRelationships.DependenciesTruncated;
            }
            if (relationships.DependedBy.Count > 0)
            {
                compactRelationships.DependedBy = relationships.DependedBy
                    .Take(maxRelationships).Select(ToDisplayPath).ToList();
                compactRelationships.DependedByCount = relationships.DependedBy.Count;
                compactRelationships.DependedByReturnedCount = compactRelationships.DependedBy.Count;
                compactRelationships.DependedByTruncated = relationships.DependedBy.Count > maxRelationships;
                compactRelationships.Truncated |= compactRelationships.DependedByTruncated;
            }

            if (includeRelated)
            {
                compactRelationships.RelatedItems = relationships.RelatedItems
                    .Take(maxRelationships).Select(node => ToCompactNode(node)).ToList();
                compactRelationships.RelatedItemsCount = relationships.RelatedItems.Count;
                compactRelationships.RelatedItemsReturnedCount = compactRelationships.RelatedItems.Count;
                compactRelationships.RelatedItemsTruncated = relationships.RelatedItems.Count > maxRelationships;
                compactRelationships.Truncated |= compactRelationships.RelatedItemsTruncated;
            }

            var compact = new CompactContextMatch
            {
                Target = ToCompactNode(match.Target, includeQualifiedIdentity),
                Relationships = compactRelationships,
                Metrics = includeMetrics ? match.Metrics : null,
                Content = match.Content
            };

            if (includeTests)
            {
                compact.Testing = new CompactTesting
                {
                    IsTested = match.Testing.IsTested,
                    DirectlyTested = match.Testing.DirectlyTested,
                    TestReferenceCount = match.Testing.TestReferenceCount,
                    TestImplementerCount = match.Testing.TestImplementerCount,
                    HeuristicMatchCount = match.Testing.HeuristicMatchCount,
                    TestFileCount = match.Testing.TestFiles.Count,
                    TestFiles = match.Testing.TestFiles.Take(maxRelationships)
                        .Select(file => new CompactTestFile
                        {
                            File = ToDisplayPath(file.FilePath),
                            TestCount = file.TestCount,
                            TestMethods = file.TestMethods.Take(maxRelationships)
                                .Select(node => ToCompactNode(node)).ToList(),
                            Evidence = file.Evidence
                        }).ToList(),
                    Truncated = match.Testing.TestFiles.Count > maxRelationships ||
                        match.Testing.TestFiles.Any(file => file.TestMethods.Count > maxRelationships)
                };
            }

            return compact;
        }

        private async Task<List<CodeNode>> FilterCompactUsedByAsync(
            CodeNode target,
            List<CodeNode> usedBy)
        {
            await Task.CompletedTask;
            return usedBy;
        }

        private async Task<List<CompactCodeNode>> ToCompactRelatedNodesAsync(
            CodeNode target,
            IReadOnlyCollection<CodeNode> nodes,
            bool outgoing,
            int maxRelationships,
            int maxCallSites)
        {
            var directEdges = string.IsNullOrEmpty(target.Id)
                ? new List<CodeEdge>()
                : outgoing
                    ? await _edgeRepository.GetBySourceIdAsync(target.Id)
                    : await _edgeRepository.GetByTargetIdAsync(target.Id);
            var edgesByNode = (directEdges ?? new List<CodeEdge>())
                .Where(IsUsageEdge)
                .Where(edge => !string.IsNullOrEmpty(outgoing ? edge.TargetId : edge.SourceId))
                .GroupBy(edge => outgoing ? edge.TargetId! : edge.SourceId!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

            return nodes
                .Select(node =>
                {
                    edgesByNode.TryGetValue(node.Id ?? string.Empty, out var edges);
                    return (Node: node, Edges: edges ?? new List<CodeEdge>());
                })
                .OrderBy(item => RelationshipRank(item.Edges))
                .ThenBy(item => IsTestPath(item.Node.FilePath))
                .ThenBy(item => TypeRank(item.Node.Type))
                .ThenBy(item => item.Node.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Node.StartLine)
                .Take(maxRelationships)
                .Select(item =>
                {
                    var compact = ToCompactNode(item.Node);
                    if (item.Edges.Count == 0)
                        return compact;

                    compact.Relations = item.Edges
                        .Select(edge => edge.Type ?? "USES")
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(RelationshipRank)
                        .ToList();
                    compact.Occurrences = item.Edges.Count;
                    var allLines = item.Edges
                        .Select(edge => edge.Metadata?.GetValueOrDefault("line"))
                        .Where(line => int.TryParse(line, out _))
                        .Select(line => int.Parse(line!) + 1)
                        .Distinct()
                        .OrderBy(line => line)
                        .ToList();
                    var lines = allLines.Take(maxCallSites).ToList();
                    compact.CallSiteCount = allLines.Count;
                    compact.CallSitesTruncated = allLines.Count > maxCallSites;
                    compact.Lines = lines.Count > 0 ? lines : null;
                    return compact;
                })
                .ToList();
        }

        private CompactCodeNode ToCompactTransitiveNode(ContextTransitiveRelationship relationship)
        {
            var compact = ToCompactNode(relationship.Node);
            compact.Distance = relationship.Distance;
            compact.RelationPath = relationship.RelationPath;
            return compact;
        }

        private CompactCodeNode ToCompactNode(CodeNode node, bool includeQualifiedIdentity = false)
            => new()
            {
                Name = node.Name,
                Type = node.Type,
                File = string.IsNullOrEmpty(node.FilePath) ? null : ToDisplayPath(node.FilePath),
                Line = node.StartLine + 1,
                Signature = Truncate(NormalizeCompactSignature(node.Signature), 160),
                QualifiedIdentifier = includeQualifiedIdentity ? GetQualifiedIdentifier(node) : null
            };

        private string ToDisplayPath(string path)
        {
            if (!string.IsNullOrEmpty(_rootPath))
            {
                try
                {
                    var relative = Path.GetRelativePath(_rootPath, path);
                    if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar))
                    {
                        return NormalizePath(relative);
                    }
                }
                catch (ArgumentException)
                {
                    // Keep the indexed path when it cannot be relativized.
                }
            }

            return NormalizePath(path);
        }

        private string BuildDisambiguationHint(IReadOnlyList<CodeNode> candidates)
        {
            var first = candidates.FirstOrDefault();
            if (first is null) return "Specify type, containingType, namespace, signature, or sourceFile.";
            return $"Multiple matches found. Refine with qualifiedIdentifier='{GetQualifiedIdentifier(first)}' or use containingType, namespace, signature, or sourceFile.";
        }

        private static string GetQualifiedIdentifier(CodeNode node)
        {
            if (!string.IsNullOrEmpty(node.Id))
            {
                var firstSeparator = node.Id.IndexOf(':');
                var secondSeparator = firstSeparator < 0 ? -1 : node.Id.IndexOf(':', firstSeparator + 1);
                if (secondSeparator >= 0 && secondSeparator + 1 < node.Id.Length)
                    return node.Id[(secondSeparator + 1)..];
            }

            var member = node.Signature ?? node.Name ?? string.Empty;
            return string.IsNullOrEmpty(node.Namespace) ? member : $"{node.Namespace}.{member}";
        }

        private static string? GetContainingType(CodeNode node)
        {
            var qualified = GetQualifiedIdentifier(node);
            var hash = qualified.LastIndexOf('#');
            var memberPortion = hash >= 0 ? qualified[(hash + 1)..] : qualified;
            var parameterStart = memberPortion.IndexOf('(');
            if (parameterStart >= 0) memberPortion = memberPortion[..parameterStart];
            var lastDot = memberPortion.LastIndexOf('.');
            if (lastDot <= 0) return null;
            var prefix = memberPortion[..lastDot];
            var containingDot = prefix.LastIndexOf('.');
            return containingDot < 0 ? prefix : prefix[(containingDot + 1)..];
        }

        private static string? Truncate(string? value, int maxLength)
            => value is null || value.Length <= maxLength ? value : value[..maxLength] + "…";

        private static string? NormalizeCompactSignature(string? signature)
        {
            if (signature is null)
                return null;

            return System.Text.RegularExpressions.Regex.Replace(signature.Trim(), @"\s+", " ");
        }

        private static bool IsTestPath(string? path)
            => path?.Contains("test", StringComparison.OrdinalIgnoreCase) == true ||
               path?.Contains("spec", StringComparison.OrdinalIgnoreCase) == true;

        private static int TypeRank(string? type) => type?.ToLowerInvariant() switch
        {
            "class" => 0,
            "interface" => 1,
            "method" => 2,
            "property" => 3,
            _ => 4
        };

        private static int RelationshipRank(IReadOnlyCollection<CodeEdge> edges)
            => edges.Count == 0 ? 5 : edges.Min(edge => RelationshipRank(edge.Type));

        private static int RelationshipRank(string? relationship) => relationship?.ToUpperInvariant() switch
        {
            "IMPLEMENTS" => 0,
            "INHERITS" or "EXTENDS" => 1,
            "CALLS" => 2,
            "REFERENCES" or "USES" => 3,
            "IMPORTS" => 4,
            _ => 5
        };

        private async Task<ContextMatch> BuildContextMatchAsync(
            CodeNode targetNode,
            int depth,
            bool includeTests,
            bool includeContent,
            bool includeRelated,
            bool includeMetrics)
        {
            var match = new ContextMatch
            {
                Target = targetNode
            };

            // Build relationships
            match.Relationships = await BuildRelationshipsAsync(targetNode, depth, includeRelated);

            // Build testing information
            if (includeTests)
            {
                match.Testing = await BuildTestingInfoAsync(targetNode);
            }

            // Build metrics
            if (includeMetrics)
            {
                match.Metrics = BuildMetrics(targetNode, match.Relationships);
            }

            // Include content if requested
            if (includeContent && !string.IsNullOrEmpty(targetNode.FilePath))
            {
                match.Content = await GetFileContentSnippetAsync(targetNode);
            }

            return match;
        }

        /// <summary>
        /// Structural containment edge kinds (a class "has" its own members). These
        /// describe nesting, not usage: they are excluded from Uses/UsedBy so a class
        /// context lists real calls/inheritance instead of its own member list (which
        /// RelatedItems already covers via same-file grouping).
        /// </summary>
        private static readonly HashSet<string> ContainmentEdgeKinds = new(StringComparer.OrdinalIgnoreCase)
        {
            "HAS_METHOD", "HAS_PROPERTY", "HAS_FIELD", "CONTAINS",
        };

        private static bool IsUsageEdge(CodeEdge edge)
            => edge.Type is null || !ContainmentEdgeKinds.Contains(edge.Type);

        private static readonly HashSet<string> SemanticFileRelationshipKinds = new(StringComparer.OrdinalIgnoreCase)
        {
            "CALLS", "MOCK_CALLS", "REFERENCES", "IMPLEMENTS", "INHERITS", "EXTENDS", "IMPORTS",
        };

        private static bool IsSemanticFileRelationship(CodeEdge edge)
            => edge.Type is not null && SemanticFileRelationshipKinds.Contains(edge.Type);

        private async Task<ContextRelationships> BuildRelationshipsAsync(
            CodeNode targetNode,
            int depth,
            bool includeRelated)
        {
            var relationships = new ContextRelationships();

            if (string.IsNullOrEmpty(targetNode.Id) || depth == 0)
                return relationships;

            var uses = await TraverseRelationshipsAsync(targetNode.Id, depth, outgoing: true);
            var usedBy = await TraverseRelationshipsAsync(targetNode.Id, depth, outgoing: false);
            relationships.Uses = uses.Where(item => item.Distance == 1).Select(item => item.Node).ToList();
            relationships.UsedBy = usedBy.Where(item => item.Distance == 1).Select(item => item.Node).ToList();
            relationships.TransitiveUses = uses.Where(item => item.Distance > 1)
                .Select(item => new ContextTransitiveRelationship
                {
                    Node = item.Node, Distance = item.Distance, RelationPath = item.RelationPath
                }).ToList();
            relationships.TransitiveUsedBy = usedBy.Where(item => item.Distance > 1)
                .Select(item => new ContextTransitiveRelationship
                {
                    Node = item.Node, Distance = item.Distance, RelationPath = item.RelationPath
                }).ToList();

            // Get file-level dependencies
            if (!string.IsNullOrEmpty(targetNode.FilePath))
            {
                relationships.Dependencies = await GetFileDependenciesAsync(targetNode.FilePath);
                relationships.DependedBy = await GetFileDependentsAsync(targetNode.FilePath);
            }

            if (includeRelated)
            {
                relationships.RelatedItems = await GetRelatedItemsAsync(targetNode);
            }

            return relationships;
        }

        private sealed record TraversedRelationship(
            CodeNode Node, int Distance, List<string> RelationPath);

        private async Task<List<TraversedRelationship>> TraverseRelationshipsAsync(
            string rootNodeId,
            int depth,
            bool outgoing)
        {
            var result = new List<TraversedRelationship>();
            var visited = new HashSet<string>(StringComparer.Ordinal) { rootNodeId };
            var frontier = new List<(string NodeId, List<string> Path)> { (rootNodeId, []) };

            for (var level = 0; level < depth && frontier.Count > 0; level++)
            {
                var nextFrontier = new List<(string NodeId, List<string> Path)>();
                foreach (var (currentNodeId, currentPath) in frontier)
                {
                    var edges = outgoing
                        ? await _edgeRepository.GetBySourceIdAsync(currentNodeId)
                        : await _edgeRepository.GetByTargetIdAsync(currentNodeId);

                    foreach (var edge in edges ?? new List<CodeEdge>())
                    {
                        if (!IsUsageEdge(edge))
                            continue;

                        var relatedNodeId = outgoing ? edge.TargetId : edge.SourceId;
                        if (string.IsNullOrEmpty(relatedNodeId) || !visited.Add(relatedNodeId))
                            continue;

                        var node = await _nodeRepository.GetByIdAsync(relatedNodeId);
                        if (node is null)
                            continue;

                        var path = new List<string>(currentPath)
                        {
                            edge.Type ?? "USES"
                        };
                        result.Add(new TraversedRelationship(node, level + 1, path));
                        nextFrontier.Add((relatedNodeId, path));
                    }
                }

                frontier = nextFrontier;
            }

            return result;
        }

        private async Task<ContextTesting> BuildTestingInfoAsync(CodeNode targetNode)
        {
            var testing = new ContextTesting();

            if (string.IsNullOrEmpty(targetNode.Name))
                return testing;

            var evidenceByFile = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var graphTestMethodsByFile = new Dictionary<string, List<CodeNode>>(StringComparer.OrdinalIgnoreCase);
            var testReferenceNodes = new HashSet<string>(StringComparer.Ordinal);
            var testImplementers = new HashSet<string>(StringComparer.Ordinal);

            if (!string.IsNullOrEmpty(targetNode.Id))
            {
                var incoming = (await _edgeRepository.GetByTargetIdAsync(targetNode.Id))
                    .Where(IsUsageEdge).ToList();
                foreach (var edge in incoming)
                {
                    if (string.IsNullOrEmpty(edge.SourceId)) continue;
                    var source = await _nodeRepository.GetByIdAsync(edge.SourceId);
                    if (source?.FilePath is null || !IsTestPath(source.FilePath)) continue;
                    AddEvidence(evidenceByFile, source.FilePath,
                        edge.Type == "IMPLEMENTS" ? "testImplementer" : "testReference");
                    if (edge.Type == "IMPLEMENTS") testImplementers.Add(edge.SourceId);
                    else testReferenceNodes.Add(edge.SourceId);

                    if (IsTestMethod(source) && edge.Type is "CALLS" or "MOCK_CALLS")
                    {
                        testing.DirectlyTested = true;
                        AddEvidence(evidenceByFile, source.FilePath, "directCall");
                        AddMethod(graphTestMethodsByFile, source.FilePath, source);
                    }
                }

                // Reverse traversal identifies test methods that reach the target
                // indirectly. This is static graph evidence, never runtime coverage.
                foreach (var reached in await TraverseRelationshipsAsync(targetNode.Id, depth: 5, outgoing: false))
                {
                    var source = reached.Node;
                    if (source.FilePath is null || !IsTestPath(source.FilePath) || !IsTestMethod(source)) continue;
                    testReferenceNodes.Add(source.Id ?? $"{source.FilePath}:{source.StartLine}");
                    AddMethod(graphTestMethodsByFile, source.FilePath, source);
                    AddEvidence(evidenceByFile, source.FilePath,
                        reached.Distance == 1 ? "testReference" : "indirectReference");
                }
            }

            var heuristicFiles = await FindTestFilesAsync(targetNode);
            foreach (var testFile in heuristicFiles.Where(file => file.FilePath is not null))
                AddEvidence(evidenceByFile, testFile.FilePath!, "namingHeuristic");

            foreach (var (filePath, evidence) in evidenceByFile)
            {
                var heuristicMethods = await GetTestMethodsForTargetAsync(filePath, targetNode);
                var graphMethods = graphTestMethodsByFile.GetValueOrDefault(filePath) ?? [];
                var testMethods = graphMethods.Concat(heuristicMethods)
                    .GroupBy(method => method.Id ?? $"{method.FilePath}:{method.StartLine}", StringComparer.Ordinal)
                    .Select(group => group.First()).ToList();
                testing.HeuristicMatchCount += heuristicMethods.Count(method =>
                    graphMethods.All(graph => graph.Id != method.Id));
                var testInfo = new TestFileInfo
                {
                    FilePath = filePath,
                    TestMethods = testMethods,
                    TestCount = testMethods.Count,
                    Evidence = evidence.Order(StringComparer.Ordinal).ToList()
                };

                testing.TestFiles.Add(testInfo);
            }

            testing.TestReferenceCount = testReferenceNodes.Count;
            testing.TestImplementerCount = testImplementers.Count;
            testing.IsTested = testing.DirectlyTested
                || testing.TestReferenceCount > 0
                || testing.TestImplementerCount > 0
                || testing.HeuristicMatchCount > 0;

            return testing;
        }

        private static void AddEvidence(
            Dictionary<string, HashSet<string>> evidenceByFile, string filePath, string evidence)
        {
            if (!evidenceByFile.TryGetValue(filePath, out var values))
                evidenceByFile[filePath] = values = new HashSet<string>(StringComparer.Ordinal);
            values.Add(evidence);
        }

        private static void AddMethod(
            Dictionary<string, List<CodeNode>> methodsByFile, string filePath, CodeNode method)
        {
            if (!methodsByFile.TryGetValue(filePath, out var methods))
                methodsByFile[filePath] = methods = [];
            methods.Add(method);
        }

        private ContextMetrics BuildMetrics(CodeNode targetNode, ContextRelationships relationships)
        {
            return new ContextMetrics
            {
                Complexity = CalculateComplexity(targetNode),
                LinesOfCode = (targetNode.EndLine - targetNode.StartLine) + 1,
                DependencyCount = relationships.Uses.Count,
                DependentCount = relationships.UsedBy.Count
            };
        }

        private async Task<string?> GetFileContentSnippetAsync(CodeNode targetNode)
        {
            if (string.IsNullOrEmpty(targetNode.FilePath) || !File.Exists(targetNode.FilePath))
                return null;

            try
            {
                var lines = await File.ReadAllLinesAsync(targetNode.FilePath);
                var startLine = Math.Max(0, targetNode.StartLine - 1); // Convert to 0-based
                var endLine = Math.Min(lines.Length - 1, targetNode.EndLine - 1);

                if (startLine <= endLine)
                {
                    return string.Join(Environment.NewLine, lines[startLine..(endLine + 1)]);
                }
            }
            catch
            {
                // Ignore file read errors
            }

            return null;
        }

        private async Task<List<CodeNode>> FindNodesByFilePathAsync(string filePath, string? type = null)
        {
            var allNodes = await _nodeRepository.GetAllAsync();
            var matchingNodes = allNodes.Where(n => 
                FilePathMatches(n.FilePath, filePath));

            if (!string.IsNullOrEmpty(type))
            {
                matchingNodes = matchingNodes.Where(n => 
                    string.Equals(n.Type, type, StringComparison.OrdinalIgnoreCase));
            }

            return matchingNodes.ToList();
        }

        private static bool FilePathMatches(string? indexedPath, string requestedPath)
        {
            if (string.IsNullOrWhiteSpace(indexedPath) || string.IsNullOrWhiteSpace(requestedPath))
                return false;

            var normalizedIndexed = NormalizePath(indexedPath);
            var normalizedRequested = NormalizePath(requestedPath);

            if (Path.IsPathRooted(requestedPath))
            {
                return string.Equals(
                    normalizedIndexed, normalizedRequested, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(normalizedIndexed, normalizedRequested, StringComparison.OrdinalIgnoreCase)
                || normalizedIndexed.EndsWith(
                    "/" + normalizedRequested, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string path)
            => path.Replace('\\', '/').TrimEnd('/');

        private async Task<List<string>> GetFileDependenciesAsync(string filePath)
            => await GetSemanticFileRelationshipsAsync(filePath, outgoing: true);

        private async Task<List<string>> GetFileDependentsAsync(string filePath)
            => await GetSemanticFileRelationshipsAsync(filePath, outgoing: false);

        private async Task<List<string>> GetSemanticFileRelationshipsAsync(string filePath, bool outgoing)
        {
            var nodesInFile = await FindNodesByFilePathAsync(filePath);
            var relatedPaths = new List<string>();

            foreach (var node in nodesInFile)
            {
                if (string.IsNullOrEmpty(node.Id))
                    continue;

                var edges = outgoing
                    ? await _edgeRepository.GetBySourceIdAsync(node.Id)
                    : await _edgeRepository.GetByTargetIdAsync(node.Id);
                foreach (var edge in edges ?? new List<CodeEdge>())
                {
                    if (!IsSemanticFileRelationship(edge))
                        continue;

                    var relatedNodeId = outgoing ? edge.TargetId : edge.SourceId;
                    if (string.IsNullOrEmpty(relatedNodeId))
                        continue;

                    var relatedNode = await _nodeRepository.GetByIdAsync(relatedNodeId);
                    if (string.IsNullOrEmpty(relatedNode?.FilePath)
                        || PathsReferToSameFile(relatedNode.FilePath, filePath))
                        continue;

                    relatedPaths.Add(ToDisplayPath(relatedNode.FilePath));
                }
            }

            return relatedPaths
                .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(path => path, StringComparer.Ordinal).First())
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToList();
        }

        private static bool PathsReferToSameFile(string left, string right)
            => FilePathMatches(left, right) || FilePathMatches(right, left);

        private async Task<List<CodeNode>> GetRelatedItemsAsync(CodeNode targetNode)
        {
            var allNodes = await _nodeRepository.GetAllAsync() ?? [];
            return allNodes
                .Where(node => GetNodeIdentity(node) != GetNodeIdentity(targetNode))
                .Select(node => (Node: node, Proximity: RelatedProximity(node, targetNode)))
                .Where(item => item.Proximity < 2)
                .GroupBy(item => GetNodeIdentity(item.Node), StringComparer.Ordinal)
                .Select(group => group.OrderBy(item => item.Proximity).First())
                .OrderBy(item => item.Proximity)
                .ThenBy(item => item.Node.Type, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Node.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Node.StartLine)
                .ThenBy(item => item.Node.StartCol)
                .ThenBy(item => item.Node.Name, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Node)
                .ToList();
        }

        private static int RelatedProximity(CodeNode candidate, CodeNode target)
        {
            if (!string.IsNullOrEmpty(target.FilePath)
                && !string.IsNullOrEmpty(candidate.FilePath)
                && PathsReferToSameFile(candidate.FilePath, target.FilePath))
                return 0;

            if (!string.IsNullOrEmpty(target.Namespace)
                && string.Equals(candidate.Namespace, target.Namespace, StringComparison.OrdinalIgnoreCase))
                return 1;

            return 2;
        }

        private static string GetNodeIdentity(CodeNode node)
            => !string.IsNullOrEmpty(node.Id)
                ? node.Id
                : $"{node.Type}\0{node.FilePath}\0{node.StartLine}\0{node.StartCol}\0{node.Name}";

        private async Task<List<CodeNode>> FindTestFilesAsync(CodeNode targetNode)
        {
            var allNodes = await _nodeRepository.GetAllAsync();
            var testFiles = new List<CodeNode>();

            // Look for test files that might test this construct
            // Common patterns: *Test.cs, *Tests.cs, Test*.cs
            var testFileNodes = allNodes.Where(n => 
                !string.IsNullOrEmpty(n.FilePath) && 
                (n.FilePath.Contains("Test", StringComparison.OrdinalIgnoreCase) ||
                 n.FilePath.Contains("Spec", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            // Filter to files that might contain tests for the target
            foreach (var node in testFileNodes)
            {
                if (await MightContainTestsForTargetAsync(node.FilePath, targetNode))
                {
                    testFiles.Add(node);
                }
            }

            return testFiles.Take(10).ToList();
        }

        private async Task<List<CodeNode>> GetTestMethodsForTargetAsync(string? testFilePath, CodeNode targetNode)
        {
            if (string.IsNullOrEmpty(testFilePath) || string.IsNullOrEmpty(targetNode.Name))
                return new List<CodeNode>();

            var testFileNodes = await FindNodesByFilePathAsync(testFilePath);
            var actualTestMethods = testFileNodes.Where(n =>
                string.Equals(n.Type, "Method", StringComparison.OrdinalIgnoreCase) &&
                IsTestMethod(n));

            var fileNameMatchesTarget = Path.GetFileNameWithoutExtension(testFilePath)
                .Contains(targetNode.Name, StringComparison.OrdinalIgnoreCase);

            var testMethods = actualTestMethods
                .Where(n => fileNameMatchesTarget || IsTestMethodForTarget(n, targetNode))
                .ToList();

            return testMethods;
        }

        private bool IsTestMethodForTarget(CodeNode testMethod, CodeNode targetNode)
        {
            if (string.IsNullOrEmpty(testMethod.Name) || string.IsNullOrEmpty(targetNode.Name))
                return false;

            var testMethodName = testMethod.Name.ToLowerInvariant();
            var targetName = targetNode.Name.ToLowerInvariant();

            // Common test method naming patterns
            var testPatterns = new[]
            {
                // Direct name match: "TestUserService", "UserServiceTests", "Test_UserService"
                $"test{targetName}",
                $"{targetName}test",
                $"{targetName}tests",
                $"test_{targetName}",
                $"{targetName}_test",
                $"{targetName}_tests",
                
                // Method-specific patterns: "TestGetUser", "GetUserTests", "Test_GetUser_ReturnsUser"
                $"test{targetName.Replace("get", "").Replace("set", "").Replace("create", "").Replace("update", "").Replace("delete", "")}",
                
                // BDD-style patterns: "Should_CreateUser_WhenValidInput", "Given_UserExists_When_Delete_Then_UserRemoved"
                $"should_{targetName}",
                $"given_{targetName}",
                $"when_{targetName}",
                
                // Fact/Theory patterns: "CanCreateUser", "ShouldCreateUser"
                $"can{targetName}",
                $"should{targetName}",
                
                // Behavior patterns: "UserService_Create_ShouldReturnUser"
                $"{targetName}_"
            };

            return testPatterns.Any(pattern => testMethodName.Contains(pattern)) ||
                   testMethodName.Contains(targetName) ||
                   // Check if method signature or content might reference the target
                   (testMethod.Signature?.Contains(targetNode.Name, StringComparison.OrdinalIgnoreCase) ?? false);
        }

        private bool IsTestMethod(CodeNode method)
        {
            if (string.IsNullOrEmpty(method.Name))
                return false;

            var methodName = method.Name.ToLowerInvariant();
            var signature = method.Signature?.ToLowerInvariant() ?? "";

            if (method.Metadata?.TryGetValue("isTest", out var isTest) == true &&
                string.Equals(isTest, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return methodName.StartsWith("test") ||
                methodName.EndsWith("test") ||
                methodName.EndsWith("tests") ||
                methodName.StartsWith("should") ||
                methodName.StartsWith("can") ||
                methodName.StartsWith("given") ||
                methodName.StartsWith("when") ||
                methodName.StartsWith("then") ||
                methodName.Contains("_should_") ||
                signature.Contains("[test") ||
                signature.Contains("[fact") ||
                signature.Contains("[theory") ||
                signature.Contains("[testmethod") ||
                signature.Contains("[testcase");
        }

        private async Task<bool> MightContainTestsForTargetAsync(string? testFilePath, CodeNode targetNode)
        {
            if (string.IsNullOrEmpty(testFilePath) || string.IsNullOrEmpty(targetNode.Name))
                return false;

            // Simple heuristic: check if the test file name or content might reference the target
            var fileName = Path.GetFileName(testFilePath);
            if (fileName.Contains(targetNode.Name, StringComparison.OrdinalIgnoreCase))
                return true;

            // Check if any methods in the test file reference the target
            var testMethods = await GetTestMethodsForTargetAsync(testFilePath, targetNode);
            return testMethods.Count > 0;
        }

        private int CalculateComplexity(CodeNode node)
        {
            // Simple complexity calculation based on node type and lines
            var baseComplexity = node.Type switch
            {
                "Class" => 5,
                "Interface" => 3,
                "Method" => 2,
                "Property" => 1,
                _ => 1
            };

            var linesOfCode = (node.EndLine - node.StartLine) + 1;
            return baseComplexity + (linesOfCode / 10); // Add complexity for every 10 lines
        }

        private static bool IsFilePath(string identifier)
        {
            if (identifier.Contains('/') || identifier.Contains('\\'))
                return true;

            var extension = Path.GetExtension(identifier);
            return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".py", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".pyw", StringComparison.OrdinalIgnoreCase);
        }
    }
}
