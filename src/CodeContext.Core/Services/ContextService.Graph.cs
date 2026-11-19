using CodeContext.Parser.Protocol;
using CodeContext.Core.Repositories.InMemory;

namespace CodeContext.Core.Services
{
    /// <summary>
    /// Whole-graph queries (<see cref="IGraphQueryService"/>), answered by the same service as context queries so they
    /// resolve identifiers, label cached answers, report coverage and shape nodes exactly as <c>query</c> does.
    /// </summary>
    public partial class ContextService : IGraphQueryService
    {
        /// <summary>Attempts at reading one generation before giving up while the graph keeps changing.</summary>
        internal const int GraphReadAttempts = 3;

        /// <summary>Called after each attempt's reads and before its generation check, with the attempt number. Tests only.</summary>
        internal Func<int, Task>? AfterGraphReadForTests { get; set; }

        private const int MaxGraphCandidates = 5;

        public Task<GraphPathResponse> FindPathAsync(GraphPathRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            RequireIdentifier(request.From, "from");
            RequireIdentifier(request.To, "to");
            RequireRange(request.MaxDepth, 1, GraphQueryLimits.MaxDepth, "maxDepth");
            var relations = ParseRelationFilter(request.Relation);
            return ReadOneGenerationAsync(shards => FindPathOnceAsync(
                request, new GraphWalk(shards, relations, request.IncludeTests, _rootPath, cancellationToken)), cancellationToken);
        }

        public Task<GraphAffectedResponse> GetAffectedAsync(GraphAffectedRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            RequireIdentifier(request.Identifier, "identifier");
            RequireRange(request.MaxDepth, 1, GraphQueryLimits.MaxDepth, "maxDepth");
            RequireRange(request.Limit, 1, GraphQueryLimits.MaxLimit, "limit");
            var relations = ParseRelationFilter(request.Relation);
            return ReadOneGenerationAsync(shards => GetAffectedOnceAsync(
                request, new GraphWalk(shards, relations, request.IncludeTests, _rootPath, cancellationToken)), cancellationToken);
        }

        public Task<GraphHubsResponse> GetHubsAsync(GraphHubsRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            RequireRange(request.Limit, 1, GraphQueryLimits.MaxLimit, "limit");
            var relations = ParseRelationFilter(request.Relation);
            var scope = RepositoryPaths.NormalizeScope(request.Within, _rootPath);
            var type = string.IsNullOrWhiteSpace(request.Type) ? null : request.Type.Trim();
            return ReadOneGenerationAsync(shards => Task.FromResult(GetHubsOnce(
                request, new GraphWalk(shards, relations, request.IncludeTests, _rootPath, cancellationToken), scope, type)), cancellationToken);
        }

        /// <summary>
        /// Runs <paramref name="read"/> over the shards of the published map and returns its answer only if no commit
        /// landed while it ran. The shards themselves never change once published, but identifier resolution reads
        /// through the repositories, which follow the newest map, and a legacy in-place write changes a shard where it
        /// stands: either would mix two generations into one answer. A changed generation is read again.
        /// </summary>
        private async Task<T> ReadOneGenerationAsync<T>(Func<IGraphShard[], Task<T>> read, CancellationToken cancellationToken)
        {
            var database = _database ?? throw new NotSupportedException("Graph queries need the in-memory graph store.");
            for (var attempt = 1; attempt <= GraphReadAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var version = database.MutationVersion;
                var result = await read(database.GetAdjacency().Shards);
                if (AfterGraphReadForTests is { } hook) await hook(attempt);
                if (database.MutationVersion == version) return result;
            }
            throw new GraphChangingException(
                $"The index changed during each of {GraphReadAttempts} attempts to read it; retry once indexing settles.");
        }

        // ---- Path ----

        private async Task<GraphPathResponse> FindPathOnceAsync(GraphPathRequest request, GraphWalk walk)
        {
            var response = new GraphPathResponse { Cache = _cacheState?.GetNotice(), MaxDepth = request.MaxDepth };
            var from = await ResolveEndpointAsync(request.From, walk);
            var to = await ResolveEndpointAsync(request.To, walk);
            response.From = from.Endpoint;
            response.To = to.Endpoint;
            if (from.Seeds is null || to.Seeds is null)
            {
                response.Explanation = (from.Seeds is null ? "from" : "to") + " did not resolve to one symbol or file; nothing was searched.";
                response.Cache = CacheAfterReads(response.Cache);
                return response;
            }

            var goals = to.Seeds.ToHashSet(StringComparer.Ordinal);
            var steps = walk.ShortestPath(from.Seeds, goals, request.MaxDepth, out var depthLimited);
            if (steps is not null)
            {
                response.Found = true;
                response.Path = ToHops(steps, from.ContainedBy, to.ContainedBy, walk);
                response.Length = steps.Count(step => step.Hop == "uses");
            }
            else
            {
                response.DepthLimited = depthLimited;
                (response.ExcludedBy, response.Explanation) = ExplainNoPath(request, walk, from, to, depthLimited);
                response.Coverage = CoverageForEmptyAnswer();
            }
            response.Cache = CacheAfterReads(response.Cache);
            return response;
        }

        private List<GraphPathHop> ToHops(
            List<GraphWalk.PathStep> steps,
            Dictionary<string, (string Container, CodeEdge Edge)> startContainers,
            Dictionary<string, (string Container, CodeEdge Edge)> goalContainers,
            GraphWalk walk)
        {
            var hops = new List<GraphPathHop>();

            // A path that starts at a member of the starting type begins at the type.
            var lead = new List<(string Id, CodeEdge Edge)>();
            for (var id = steps[0].NodeId; startContainers.TryGetValue(id, out var container); id = container.Container)
                lead.Add((container.Container, container.Edge));
            lead.Reverse();
            if (lead.Count > 0)
            {
                hops.Add(Hop(walk.Node(lead[0].Id)!, "start", null, null));
                for (var i = 0; i < lead.Count; i++)
                {
                    var memberId = i + 1 < lead.Count ? lead[i + 1].Id : steps[0].NodeId;
                    hops.Add(Hop(walk.Node(memberId)!, "member", lead[i].Edge.Type, null));
                }
            }
            else hops.Add(Hop(walk.Node(steps[0].NodeId)!, "start", null, null));

            for (var i = 1; i < steps.Count; i++)
            {
                var step = steps[i];
                hops.Add(Hop(walk.Node(step.NodeId)!, step.Hop, GraphWalk.Kind(step.Edge!),
                    step.Hop == "uses" ? EdgeLine(step.Edge!) : null));
            }

            // A path that ends at a member of the target type ends at the type.
            for (var id = steps[^1].NodeId; goalContainers.TryGetValue(id, out var container); id = container.Container)
                hops.Add(Hop(walk.Node(container.Container)!, "memberOf", container.Edge.Type, null));
            return hops;
        }

        private GraphPathHop Hop(CodeNode node, string hop, string? relation, int? at)
        {
            var compact = ToCompact<GraphPathHop>(node);
            compact.Hop = hop;
            compact.Relation = relation;
            compact.At = at;
            return compact;
        }

        private static int? EdgeLine(CodeEdge edge)
            => int.TryParse(edge.Metadata?.GetValueOrDefault("line"), out var line) ? line + 1 : null;

        /// <summary>
        /// Why there is no path, in the terms of the request: a filter of the caller's own that hides a path is named
        /// as the reason (never "no path exists"); a search cut by the depth limit says so; a path the other way round
        /// is pointed out; otherwise the reader is reminded what the graph does not record.
        /// </summary>
        private static (List<string>? ExcludedBy, string Explanation) ExplainNoPath(
            GraphPathRequest request, GraphWalk walk, ResolvedEndpoint from, ResolvedEndpoint to, bool depthLimited)
        {
            var goals = to.Seeds!.ToHashSet(StringComparer.Ordinal);
            var filters = new List<string>();
            if (walk.FiltersRelations) filters.Add($"relation={request.Relation!.Trim()}");
            if (!walk.IncludesTests) filters.Add("test files excluded");
            var filtered = filters.Count == 0 ? string.Empty : $" with {string.Join(" and ", filters)}";
            var names = $"from {from.Endpoint.Requested} to {to.Endpoint.Requested}";

            var unfilteredLimited = depthLimited;
            if (filters.Count > 0)
            {
                var excluded = new List<string>();
                int? length = null;
                var limitedWithoutRelation = false;
                var limitedWithTests = false;
                if (walk.FiltersRelations
                    && walk.With(null, walk.IncludesTests).ShortestPath(from.Seeds!, goals, request.MaxDepth, out limitedWithoutRelation) is { } withoutRelation)
                {
                    excluded.Add("relation");
                    length = UsesHops(withoutRelation);
                }
                if (!walk.IncludesTests
                    && walk.With(walk.FiltersRelations ? RelationsOf(request) : null, true)
                        .ShortestPath(from.Seeds!, goals, request.MaxDepth, out limitedWithTests) is { } withTests)
                {
                    excluded.Add("tests");
                    length = Math.Min(length ?? int.MaxValue, UsesHops(withTests));
                }
                if (excluded.Count == 0)
                {
                    // With one filter, the search without it was the unfiltered search: it is not run again.
                    if (walk.FiltersRelations && !walk.IncludesTests)
                    {
                        if (walk.With(null, true).ShortestPath(from.Seeds!, goals, request.MaxDepth, out unfilteredLimited) is { } unfiltered)
                        {
                            excluded.AddRange(["relation", "tests"]);
                            length = UsesHops(unfiltered);
                        }
                    }
                    else unfilteredLimited = walk.FiltersRelations ? limitedWithoutRelation : limitedWithTests;
                }
                if (excluded.Count > 0)
                {
                    var remedy = string.Join(" and ", excluded.Select(filter => filter == "relation"
                        ? "without the relation filter" : "with test files included (includeTests, --tests)"));
                    return (excluded,
                        $"No path {names}{filtered}, but one of {length} hop(s) exists {remedy}: your own filter excluded it, not the graph.");
                }
            }

            if (depthLimited)
            {
                return (null,
                    $"No path {names}{filtered} within {request.MaxDepth} hop(s), and the search stopped at that limit with symbols "
                    + $"left to explore, so a longer path may exist: raise maxDepth (at most {GraphQueryLimits.MaxDepth}).");
            }
            if (unfilteredLimited)
            {
                // The filtered search reached everything it could: only without the filters is there more past the limit.
                var remedies = new List<string>();
                if (walk.FiltersRelations) remedies.Add("without the relation filter");
                if (!walk.IncludesTests) remedies.Add("with test files included (includeTests, --tests)");
                return (null,
                    $"No path {names}{filtered}, which reaches nothing more at any depth. Without those filters the search "
                    + $"stopped at {request.MaxDepth} hop(s) with symbols left to explore, so a longer path may exist "
                    + $"{string.Join(" and ", remedies)} and a larger maxDepth (at most {GraphQueryLimits.MaxDepth}).");
            }

            var reverse = walk.ShortestPath(to.Seeds!, from.Seeds!.ToHashSet(StringComparer.Ordinal), request.MaxDepth, out _);
            if (reverse is not null)
            {
                return (null,
                    $"No path {names}{filtered}, but {to.Endpoint.Requested} reaches {from.Endpoint.Requested} "
                    + $"in {UsesHops(reverse)} hop(s): swap from and to.");
            }

            return (null,
                $"No path {names}{filtered}{(filters.Count > 0 ? ", nor without those filters" : string.Empty)}: nothing recorded "
                + "leads from one to the other through calls, references, inheritance, imports and dispatch to implementations. "
                + "Relationships are static evidence; reflection, dependency injection by convention, events, configuration and "
                + "anything the workers do not record are invisible here, so cross-check with rg.");
        }

        private static HashSet<string>? RelationsOf(GraphPathRequest request) => ParseRelationFilter(request.Relation);

        private static int UsesHops(List<GraphWalk.PathStep> steps) => steps.Count(step => step.Hop == "uses");

        // ---- Affected ----

        private async Task<GraphAffectedResponse> GetAffectedOnceAsync(GraphAffectedRequest request, GraphWalk walk)
        {
            var response = new GraphAffectedResponse { Cache = _cacheState?.GetNotice(), MaxDepth = request.MaxDepth };
            var seed = await ResolveEndpointAsync(request.Identifier, walk);
            response.Seed = seed.Endpoint;
            if (seed.Seeds is null)
            {
                response.Hint = "identifier did not resolve to one symbol or file; nothing was searched.";
                response.Cache = CacheAfterReads(response.Cache);
                return response;
            }

            var affected = walk.AffectedBy(seed.Seeds, request.MaxDepth, out var depthLimited);
            response.DepthLimited = depthLimited;
            // What is hidden is only counted.
            var listed = affected;
            response.TestsHidden = listed.RemoveAll(item => item.Hidden);
            var files = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in listed)
            {
                if (!string.IsNullOrEmpty(item.Node.FilePath)) files.Add(item.Node.FilePath);
            }

            response.TotalCount = listed.Count;
            response.FileCount = files.Count;
            foreach (var item in Leading(listed, request.Limit, static (left, right) =>
            {
                var order = left.Distance.CompareTo(right.Distance);
                return order != 0 ? order : CompareByPlace(left.Node, right.Node);
            }))
            {
                var compact = ToCompact<GraphAffectedNode>(item.Node);
                compact.Distance = item.Distance;
                compact.Relation = item.Relation;
                compact.Via = PublicIdentifier(walk.Node(item.Via)!);
                compact.Through = item.Through is null ? null : PublicIdentifier(walk.Node(item.Through)!);
                response.Items.Add(compact);
            }
            response.ReturnedCount = response.Items.Count;
            response.Truncated = response.TotalCount > response.ReturnedCount;
            if (response.TotalCount == 0)
            {
                response.Hint = response.TestsHidden > 0
                    ? $"Only symbols in test files depend on it ({response.TestsHidden}); includeTests (--tests) lists them."
                    : $"Nothing recorded depends on it{(walk.FiltersRelations ? $" through relation={request.Relation!.Trim()}" : string.Empty)}. "
                        + "Relationships are static evidence: cross-check with rg before concluding it is unused.";
                response.Coverage = CoverageForEmptyAnswer();
            }
            response.Cache = CacheAfterReads(response.Cache);
            return response;
        }

        // ---- Hubs ----

        private GraphHubsResponse GetHubsOnce(GraphHubsRequest request, GraphWalk walk, string scope, string? type)
        {
            var response = new GraphHubsResponse
            {
                Cache = _cacheState?.GetNotice(),
                Within = scope.Length == 0 ? null : scope,
            };
            var considered = 0;
            var hubs = walk.Hubs(node =>
            {
                var external = IsExternal(node);
                if (external && (!request.IncludeExternal || scope.Length > 0)) return false;
                if (type is not null && !string.Equals(node.Type, type, StringComparison.OrdinalIgnoreCase)) return false;
                if (!external && !request.IncludeTests && walk.IsTestNode(node)) return false;
                if (!external && !RepositoryPaths.IsWithin(node.FilePath, _rootPath, scope)) return false;
                considered++;
                return true;
            });
            response.TotalCount = hubs.Count;
            foreach (var hub in Leading(hubs, request.Limit, static (left, right) =>
            {
                var order = right.Dependents.CompareTo(left.Dependents);
                if (order == 0) order = right.DependentFiles.CompareTo(left.DependentFiles);
                return order != 0 ? order : CompareByPlace(left.Node, right.Node);
            }))
            {
                var compact = ToCompact<GraphHubNode>(hub.Node);
                compact.Dependents = hub.Dependents;
                compact.DependentFiles = hub.DependentFiles;
                response.Items.Add(compact);
            }
            response.ReturnedCount = response.Items.Count;
            response.Truncated = response.TotalCount > response.ReturnedCount;
            if (considered == 0)
            {
                var what = new List<string>();
                if (type is not null) what.Add($"of type {type}");
                if (scope.Length > 0) what.Add($"under {scope}");
                response.Hint = $"No indexed symbol{(what.Count > 0 ? " " + string.Join(" ", what) : string.Empty)} passes the filters.";
            }
            else if (response.TotalCount == 0)
            {
                response.Hint = $"None of the {considered} symbol(s) passing the filters has a recorded dependent.";
            }
            response.Cache = CacheAfterReads(response.Cache);
            return response;
        }

        // ---- Shared ----

        /// <summary>
        /// The first <paramref name="count"/> of <paramref name="items"/> in <paramref name="comparison"/> order, without
        /// ordering the rest: a query that finds a hundred thousand symbols returns twenty of them.
        /// </summary>
        private static List<T> Leading<T>(List<T> items, int count, Comparison<T> comparison)
        {
            var leading = new List<T>(Math.Min(count, items.Count) + 1);
            var comparer = Comparer<T>.Create(comparison);
            foreach (var item in items)
            {
                if (leading.Count == count && comparison(item, leading[^1]) >= 0) continue;
                var at = leading.BinarySearch(item, comparer);
                leading.Insert(at < 0 ? ~at : at, item);
                if (leading.Count > count) leading.RemoveAt(count);
            }
            return leading;
        }

        /// <summary>The order of listed symbols that tie otherwise: by file, line, then id, so every answer is deterministic.</summary>
        private static int CompareByPlace(CodeNode left, CodeNode right)
        {
            var order = GraphAdjacency.ComparePaths(left.FilePath ?? string.Empty, right.FilePath ?? string.Empty);
            if (order == 0) order = left.StartLine.CompareTo(right.StartLine);
            return order != 0 ? order : string.CompareOrdinal(left.Id, right.Id);
        }

        private sealed record ResolvedEndpoint(
            GraphEndpoint Endpoint,
            List<string>? Seeds,
            Dictionary<string, (string Container, CodeEdge Edge)> ContainedBy);

        /// <summary>
        /// Resolves an identifier as <c>query --exact</c> would (canonical identifier, exact name, qualified name, or
        /// file path; no substring guess, which would traverse from a symbol nobody named), then expands what it stands
        /// for: a type's members, or every symbol of a file. Library symbols drop out when project code matches too.
        /// </summary>
        private async Task<ResolvedEndpoint> ResolveEndpointAsync(string identifier, GraphWalk walk)
        {
            var endpoint = new GraphEndpoint { Requested = identifier };
            var none = new Dictionary<string, (string, CodeEdge)>(StringComparer.Ordinal);
            var (candidates, isFilePath) = await FindCandidatesAsync(identifier, type: null, exact: true);
            if (!isFilePath && candidates.Exists(candidate => !IsExternal(candidate)))
                candidates.RemoveAll(IsExternal);
            candidates.RemoveAll(candidate => string.IsNullOrEmpty(candidate.Id));
            endpoint.TotalMatches = candidates.Count;

            if (candidates.Count == 0)
            {
                endpoint.Hint = $"No symbol or indexed file matches '{identifier}' exactly. Graph queries take a canonical identifier, "
                    + "an exact or qualified name, or a file path; `query` searches by substring and returns identifiers to pass back.";
                return new ResolvedEndpoint(endpoint, null, none);
            }

            if (isFilePath)
            {
                var files = candidates.Select(candidate => candidate.FilePath!).Distinct(StringComparer.Ordinal)
                    .Order(Comparer<string>.Create(GraphAdjacency.ComparePaths)).ToList();
                endpoint.MatchMode = "filePath";
                if (files.Count > 1)
                {
                    endpoint.Ambiguous = true;
                    endpoint.Hint = $"'{identifier}' matches {files.Count} files ("
                        + string.Join(", ", files.Take(MaxGraphCandidates).Select(ToDisplayPath))
                        + (files.Count > MaxGraphCandidates ? ", ..." : string.Empty) + "); pass a longer path.";
                    return new ResolvedEndpoint(endpoint, null, none);
                }
                endpoint.File = ToDisplayPath(files[0]);
            }
            else
            {
                // A canonical identifier names one copy of its declaration; the others are found by its name.
                if (candidates.Count == 1 && candidates[0] is { Name.Length: > 0, FilePath.Length: > 0 } named)
                {
                    foreach (var other in await _nodeRepository.FindByNameAsync(named.Name, named.Type, exact: true))
                    {
                        if (other.Id != named.Id && !string.IsNullOrEmpty(other.Id) && IsSameDeclaration(named, other))
                            candidates.Add(other);
                    }
                }

                endpoint.MatchMode = candidates.Count == 1 && PublicIdentifier(candidates[0]) == identifier ? "identity" : "exact";
                if (candidates.Count > 1)
                {
                    var asked = candidates[0];
                    var ranked = RankCandidates(candidates, identifier).ThenBy(PublicIdentifier, StringComparer.Ordinal).ToList();
                    if (!ranked.TrueForAll(candidate => IsSameDeclaration(ranked[0], candidate)))
                    {
                        endpoint.Ambiguous = true;
                        endpoint.Candidates = ranked.Take(MaxGraphCandidates).Select(candidate => ToCompactNode(candidate)).ToList();
                        // Graph queries take no narrowing filters: the way to one symbol is its identifier.
                        endpoint.Hint = $"'{identifier}' names {ranked.Count} symbols. Pass one of the candidates' identifiers unchanged"
                            + (ranked.Count > MaxGraphCandidates ? $" (the first {MaxGraphCandidates} are listed; `query {identifier}` narrows by type, namespace or file)." : ".");
                        return new ResolvedEndpoint(endpoint, null, none);
                    }

                    // One declaration, indexed once per project or workspace that sees its file: one thing to change.
                    var target = PublicIdentifier(asked) == identifier ? asked : ranked[0];
                    if (PublicIdentifier(target) == identifier) endpoint.MatchMode = "identity";
                    endpoint.Target = ToCompactNode(target);
                    endpoint.SameDeclaration = ranked.Where(candidate => candidate != target).Select(PublicIdentifier).ToList();
                    endpoint.Hint = $"'{identifier}' is one declaration ({endpoint.Target.File}:{endpoint.Target.Line}) indexed "
                        + $"{ranked.Count} times, as {endpoint.Target.Identifier} and {string.Join(", ", endpoint.SameDeclaration)}; "
                        + "all of them were searched together.";
                }
                else endpoint.Target = ToCompactNode(candidates[0]);
            }

            var seeds = walk.ExpandMembers(candidates.Select(candidate => candidate.Id!), out var containedBy);
            endpoint.SeedCount = seeds.Count;
            return new ResolvedEndpoint(endpoint, seeds, containedBy);
        }

        /// <summary>
        /// Whether two nodes are the same source declaration indexed twice: the same file, span, name, type and
        /// signature. Overloads, neighbours on one line and library symbols (which have no file) are not.
        /// </summary>
        private static bool IsSameDeclaration(CodeNode left, CodeNode right)
            => !string.IsNullOrEmpty(left.FilePath)
                && string.Equals(left.FilePath, right.FilePath, StringComparison.Ordinal)
                && left.StartLine == right.StartLine && left.StartCol == right.StartCol
                && left.EndLine == right.EndLine && left.EndCol == right.EndCol
                && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
                && string.Equals(left.Type, right.Type, StringComparison.Ordinal)
                && string.Equals(left.Signature, right.Signature, StringComparison.Ordinal);

        /// <summary>For an empty answer: which projects were analyzed without all they declare, if any.</summary>
        private CoverageSummary? CoverageForEmptyAnswer()
        {
            if (_coverage is null) return null;
            var projects = _coverage.GetProjects();
            var incomplete = projects.Where(project => project.State != ProjectCoverageStates.Complete).ToList();
            return incomplete.Count == 0
                ? null
                : new CoverageSummary
                {
                    State = OverallState(projects),
                    IncompleteProjects = incomplete.Count,
                    TotalProjects = projects.Count,
                    Names = incomplete.Take(MaxCoverageNames).Select(project => project.Name).ToList(),
                };
        }

        private static void RequireIdentifier(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"{name} must be a non-empty identifier, name or file path.");
        }

        private static void RequireRange(int value, int min, int max, string name)
        {
            if (value < min || value > max)
                throw new ArgumentException($"{name} must be between {min} and {max} (got {value}).");
        }
    }
}
