using CodeContext.Core.Repositories.InMemory;
using System.Runtime.InteropServices;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Services
{
    /// <summary>
    /// The traversals behind the graph queries, over the shards of one published store map. The shards are read
    /// through their own lookups, which hand back each shard's backing arrays: a lookup over several shards
    /// allocates nothing (no list is concatenated), and no edge or node is copied. Edges are visited in canonical
    /// order (shards in key order, id order inside), so every answer is deterministic.
    ///
    /// What a hop may follow:
    /// <list type="bullet">
    /// <item>A usage edge (any kind but containment and method-family kinds; <c>USES</c> when untyped), from its
    /// source to its target, when the relation filter admits its kind.</item>
    /// <item>Dispatch: a call into a member can run an implementation or override of it (the reverse of an
    /// <c>IMPLEMENTS_MEMBER</c> or <c>OVERRIDES_MEMBER</c> edge), unless the worker marked the call statically bound
    /// (<c>dispatch: static</c>, as <c>base.M()</c> and <c>super.m()</c> are), which reaches the named member alone.
    /// A library member is not a dispatch hub (it would link every implementation of, say, Dispose), as in
    /// <c>usedBy</c>. Dispatch is the way a call reaches its target, not a relationship kind: a relation filter
    /// does not remove it.</item>
    /// <item>Containment (<c>HAS_METHOD</c>, <c>HAS_PROPERTY</c>, <c>HAS_FIELD</c>, <c>CONTAINS</c>) only to make a
    /// type stand for its members at an endpoint, never in the middle: a use of a type does not reach its members.</item>
    /// </list>
    /// </summary>
    internal sealed class GraphWalk
    {
        private readonly IGraphShard[] _shards;
        private readonly HashSet<string>? _relations;
        private readonly bool _includeTests;
        private readonly string? _rootPath;
        private readonly CancellationToken _cancellation;
        private int _visits;

        public GraphWalk(
            IGraphShard[] shards, HashSet<string>? relations, bool includeTests, string? rootPath,
            CancellationToken cancellation = default)
        {
            _shards = shards;
            _relations = relations;
            _includeTests = includeTests;
            _rootPath = rootPath;
            _cancellation = cancellation;
        }

        public GraphWalk With(HashSet<string>? relations, bool includeTests)
            => new(_shards, relations, includeTests, _rootPath, _cancellation);

        /// <summary>Symbols visited between two looks at the cancellation token.</summary>
        internal const int VisitsPerCancellationCheck = 1024;

        /// <summary>
        /// Counts a visited symbol and, once in <see cref="VisitsPerCancellationCheck"/>, stops the walk if the caller
        /// has gone away: a query over a large graph does not run on for nobody, and the walk pays an increment.
        /// </summary>
        private void Visit()
        {
            if ((++_visits & (VisitsPerCancellationCheck - 1)) == 0) _cancellation.ThrowIfCancellationRequested();
        }

        public bool FiltersRelations => _relations is not null;

        public bool IncludesTests => _includeTests;

        // ---- Lookups (no allocation) ----

        public CodeNode? Node(string id)
        {
            foreach (var shard in _shards)
            {
                if (shard.TryGetNode(id, out var node)) return node;
            }
            return null;
        }

        public bool IsTestNode(CodeNode node) => RepositoryPaths.IsTestFile(node.FilePath, _rootPath);

        /// <summary>What an edge is to a walk: a use, containment, or a method-family link (implements or overrides a member).</summary>
        internal enum EdgeRole : byte { Usage, Containment, Family }

        /// <summary>
        /// The role of an edge kind. The walks ask this of every edge they look at, so the kinds as the workers write
        /// them are told apart by an ordinal switch; any other spelling goes to the context service's case-insensitive
        /// sets, which this agrees with.
        /// </summary>
        internal static EdgeRole Role(string? type)
        {
            switch (type)
            {
                case null:
                case "CALLS":
                case "REFERENCES":
                case "MOCK_CALLS":
                case "IMPLEMENTS":
                case "INHERITS":
                case "EXTENDS":
                case "IMPORTS":
                case "USES":
                    return EdgeRole.Usage;
                case "HAS_METHOD":
                case "HAS_PROPERTY":
                case "HAS_FIELD":
                case "CONTAINS":
                    return EdgeRole.Containment;
                case "IMPLEMENTS_MEMBER":
                case "OVERRIDES_MEMBER":
                    return EdgeRole.Family;
                default:
                    return ContextService.ContainmentEdgeKinds.Contains(type) ? EdgeRole.Containment
                        : ContextService.MethodFamilyEdgeKinds.Contains(type) ? EdgeRole.Family
                        : EdgeRole.Usage;
            }
        }

        internal static bool IsUsage(CodeEdge edge) => Role(edge.Type) == EdgeRole.Usage;

        internal static bool IsFamily(CodeEdge edge) => Role(edge.Type) == EdgeRole.Family;

        internal static bool IsContainment(CodeEdge edge) => Role(edge.Type) == EdgeRole.Containment;

        internal static bool IsStaticDispatch(CodeEdge edge)
            => edge.Metadata?.GetValueOrDefault(EdgeMetadataKeys.Dispatch) == EdgeDispatch.Static;

        internal static bool IsExternal(CodeNode node)
            => node.Metadata?.GetValueOrDefault("external") == "true";

        internal static string Kind(CodeEdge edge) => edge.Type ?? "USES";

        private bool Admits(CodeEdge edge) => _relations is null || _relations.Contains(Kind(edge));

        // ---- Seeds ----

        /// <summary>
        /// The roots and, transitively, every member they contain, in a stable order (roots by id, then members in the
        /// order containment edges reach them). <paramref name="containedBy"/> maps each member to its container and
        /// the containment edge.
        /// </summary>
        public List<string> ExpandMembers(IEnumerable<string> roots, out Dictionary<string, (string Container, CodeEdge Edge)> containedBy)
        {
            containedBy = new Dictionary<string, (string, CodeEdge)>(StringComparer.Ordinal);
            var ordered = roots.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var seen = new HashSet<string>(ordered, StringComparer.Ordinal);
            for (var index = 0; index < ordered.Count; index++)
            {
                Visit();
                var current = ordered[index];
                foreach (var shard in _shards)
                {
                    var edges = shard.GetEdgesBySource(current);
                    for (var i = 0; i < edges.Count; i++)
                    {
                        var edge = edges[i];
                        if (!IsContainment(edge) || edge.TargetId is not { Length: > 0 } member) continue;
                        if (!seen.Add(member) || Node(member) is null) continue;
                        containedBy[member] = (current, edge);
                        ordered.Add(member);
                    }
                }
            }
            return ordered;
        }

        // ---- Shortest path ----

        public sealed record PathStep(string NodeId, string Hop, CodeEdge? Edge);

        /// <summary>
        /// A shortest path from any of <paramref name="starts"/> to any of <paramref name="goals"/>, counting
        /// relationship hops only (dispatch hops are free), within <paramref name="maxDepth"/> hops; null when there is
        /// none. Symbols in test files are not passed through unless tests are included; endpoints always may be.
        /// <paramref name="depthLimited"/> is true when no path was found within the limit and some node the search did not
        /// reach within it is reachable beyond it (a longer path may exist).
        /// </summary>
        public List<PathStep>? ShortestPath(
            IReadOnlyList<string> starts, IReadOnlySet<string> goals, int maxDepth, out bool depthLimited)
        {
            depthLimited = false;
            // A state is a node and whether a call reached it by dispatch-capable means: a node reached by a statically
            // bound call may not continue to implementations of itself. A start may, as a call to it would: an interface
            // or abstract member has no body of its own, so from it (or from its type) only dispatch leads anywhere, and
            // `affected` lists the callers of such a declaration as reaching what its implementations call. Relationship
            // hops cost one and dispatch hops nothing, so this is a breadth-first search in which a state may still move
            // to an earlier level while that level is open (a dispatch hop finding what a relationship hop found first).
            // One table holds what is known of each state: how far it is and how it was reached, or that it may not be
            // entered at all (no such node, or a test file the path may not pass through), so that every later edge
            // into it costs one lookup.
            var states = new Dictionary<(string Id, bool Dispatch), Reached>();
            var level = new List<(string Id, bool Dispatch)>();
            foreach (var start in starts)
            {
                if (Node(start) is not null && states.TryAdd((start, true), default)) level.Add((start, true));
            }

            // Past the limit the search goes on without looking for goals, only to learn whether anything lies beyond.
            HashSet<string>? reachedWithin = null;
            for (var depth = 0; level.Count > 0; depth++)
            {
                if (depth == maxDepth + 1)
                {
                    reachedWithin = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var (state, reached) in states)
                    {
                        if (reached.Distance >= 0 && reached.Distance <= maxDepth) reachedWithin.Add(state.Id);
                    }
                }
                var next = new List<(string Id, bool Dispatch)>();
                for (var index = 0; index < level.Count; index++)
                {
                    Visit();
                    var state = level[index];
                    // A state that moved to an earlier level was taken there.
                    if (states[state].Distance != depth) continue;
                    if (depth <= maxDepth)
                    {
                        if (goals.Contains(state.Id)) return Reconstruct(state, states);
                    }
                    else if (!reachedWithin!.Contains(state.Id))
                    {
                        depthLimited = true;
                        return null;
                    }

                    if (state.Dispatch && Node(state.Id) is { } member && !IsExternal(member))
                    {
                        foreach (var shard in _shards)
                        {
                            var inbound = shard.GetEdgesByTarget(state.Id);
                            for (var i = 0; i < inbound.Count; i++)
                            {
                                var edge = inbound[i];
                                if (IsFamily(edge) && edge.SourceId is { Length: > 0 } implementation)
                                    Enter((implementation, true), depth, state, edge, byDispatch: true, level);
                            }
                        }
                    }

                    foreach (var shard in _shards)
                    {
                        var outbound = shard.GetEdgesBySource(state.Id);
                        for (var i = 0; i < outbound.Count; i++)
                        {
                            var edge = outbound[i];
                            if (IsUsage(edge) && Admits(edge) && edge.TargetId is { Length: > 0 } target)
                                Enter((target, !IsStaticDispatch(edge)), depth + 1, state, edge, byDispatch: false, next);
                        }
                    }
                }
                level = next;
            }
            return null;

            void Enter((string Id, bool Dispatch) reached, int at, (string Id, bool Dispatch) from, CodeEdge edge, bool byDispatch, List<(string, bool)> into)
            {
                // A node reached with dispatch covers the same node reached without it, no later.
                if (!reached.Dispatch && states.TryGetValue((reached.Id, true), out var covering)
                    && covering.Distance >= 0 && covering.Distance <= at)
                {
                    return;
                }
                ref var known = ref CollectionsMarshal.GetValueRefOrAddDefault(states, reached, out var seen);
                // Reached no later already, or refused (a refusal never changes during one search).
                if (seen && known.Distance <= at) return;
                if (!seen && (Node(reached.Id) is not { } node || (!_includeTests && !goals.Contains(reached.Id) && IsTestNode(node))))
                {
                    known.Distance = Reached.Refused;
                    return;
                }
                known = new Reached { Distance = at, ParentId = from.Id, ParentDispatch = from.Dispatch, Edge = edge, ByDispatch = byDispatch };
                into.Add(reached);
            }
        }

        /// <summary>What a path search knows of a state: its distance and the step that reached it (none for a start).</summary>
        private struct Reached
        {
            /// <summary>The distance of a state that may not be entered.</summary>
            public const int Refused = -1;

            public int Distance;
            public bool ParentDispatch;
            public bool ByDispatch;
            public string? ParentId;
            public CodeEdge? Edge;
        }

        private static List<PathStep> Reconstruct((string Id, bool Dispatch) goal, Dictionary<(string Id, bool Dispatch), Reached> states)
        {
            var steps = new List<PathStep>();
            var current = goal;
            while (states[current] is { ParentId: { } parent } reached)
            {
                steps.Add(new PathStep(current.Id, reached.ByDispatch ? "dispatch" : "uses", reached.Edge));
                current = (parent, reached.ParentDispatch);
            }
            steps.Add(new PathStep(current.Id, "start", null));
            steps.Reverse();
            return steps;
        }

        // ---- Affected ----

        /// <summary>
        /// A dependent: its node, how far it is, the edge kind that carries the impact, the id of the symbol it uses
        /// (<paramref name="Via"/>), the id of the declaration a dispatching use names (<paramref name="Through"/>), and
        /// whether it is in a test file the query leaves out (then it is counted, and impact goes no further).
        /// </summary>
        public sealed record Affected(CodeNode Node, int Distance, string Relation, string Via, string? Through, bool Hidden);

        /// <summary>
        /// Everything that depends on the seeds within <paramref name="maxDepth"/> hops, nearest first, seeds excluded.
        /// Impact moves from a symbol to: the source of every usage edge into it (relation filter applied); without a
        /// relation filter, every implementation or override of it (it must stay compatible with the declaration); and,
        /// through dispatch, the source of every usage edge into a declaration it implements or overrides, at any height,
        /// unless that call is statically bound. Siblings that implement the same declaration are not affected, and
        /// nothing moves along containment or proximity. Unless tests are included, impact reaches a symbol in a test
        /// file but moves no further from it.
        /// </summary>
        public List<Affected> AffectedBy(IReadOnlyList<string> seeds, int maxDepth, out bool depthLimited)
        {
            var visited = new HashSet<string>(seeds, StringComparer.Ordinal);
            var declarationsSeen = new HashSet<string>(StringComparer.Ordinal);
            var results = new List<Affected>();
            var level = seeds.ToList();
            for (var depth = 1; depth <= maxDepth && level.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var id in level) Expand(id, depth, next, probe: false);
                level = next;
            }

            depthLimited = false;
            foreach (var id in level)
            {
                if (Expand(id, maxDepth + 1, null, probe: true)) { depthLimited = true; break; }
            }
            return results;

            // Adds what id affects at distance; in a probe, adds nothing and says whether there is anything to add.
            bool Expand(string id, int distance, List<string>? next, bool probe)
            {
                Visit();
                foreach (var shard in _shards)
                {
                    var inbound = shard.GetEdgesByTarget(id);
                    for (var i = 0; i < inbound.Count; i++)
                    {
                        var edge = inbound[i];
                        var carries = IsUsage(edge) ? Admits(edge) : IsFamily(edge) && _relations is null;
                        if (carries && Add(edge, id, null)) return true;
                    }
                }

                // Up through what id implements or overrides: a call to that declaration can run id.
                // Most symbols implement nothing: the stack and set exist only for those that do.
                Stack<string>? pending = null;
                HashSet<string>? climbed = null;
                PushDeclarations(id, ref pending);
                while (pending is not null && pending.TryPop(out var declaration))
                {
                    if (!(climbed ??= new HashSet<string>(StringComparer.Ordinal)).Add(declaration)) continue;
                    // Once its callers are in, a declaration adds nothing more (in a probe, they are visited already).
                    if (!probe && !declarationsSeen.Add(declaration)) continue;
                    if (Node(declaration) is not { } declared || IsExternal(declared)) continue;
                    foreach (var shard in _shards)
                    {
                        var inbound = shard.GetEdgesByTarget(declaration);
                        for (var i = 0; i < inbound.Count; i++)
                        {
                            var edge = inbound[i];
                            if (IsUsage(edge) && Admits(edge) && !IsStaticDispatch(edge) && Add(edge, id, declaration)) return true;
                        }
                    }
                    PushDeclarations(declaration, ref pending);
                }
                return false;

                bool Add(CodeEdge edge, string via, string? through)
                {
                    if (edge.SourceId is not { Length: > 0 } source) return false;
                    if (probe) return !visited.Contains(source) && Node(source) is not null;
                    // A source with no node is remembered too: it is refused once, not at every edge from it.
                    if (!visited.Add(source) || Node(source) is not { } user) return false;
                    // Unless tests are included, a test-file symbol is affected but carries impact no further, as a path
                    // does not pass through one: a test double implementing a declaration must not make that
                    // declaration's production callers depend on what the double touches.
                    var hidden = !_includeTests && IsTestNode(user);
                    results.Add(new Affected(user, distance, Kind(edge), via, through, hidden));
                    if (!hidden) next!.Add(source);
                    return false;
                }
            }

            void PushDeclarations(string id, ref Stack<string>? into)
            {
                foreach (var shard in _shards)
                {
                    var outbound = shard.GetEdgesBySource(id);
                    for (var i = 0; i < outbound.Count; i++)
                    {
                        var edge = outbound[i];
                        if (IsFamily(edge) && edge.TargetId is { Length: > 0 } declaration && declaration != id)
                            (into ??= new Stack<string>()).Push(declaration);
                    }
                }
            }
        }

        // ---- Hubs ----

        public readonly record struct Hub(CodeNode Node, int Dependents, int DependentFiles);

        /// <summary>
        /// Every node passing <paramref name="accept"/> with at least one dependent: a distinct other symbol with a usage
        /// edge (relation filter applied) into it, not in a test file unless tests are included. Unordered.
        /// </summary>
        public List<Hub> Hubs(Func<CodeNode, bool> accept)
        {
            var hubs = new List<Hub>();
            var sources = new HashSet<string>(StringComparer.Ordinal);
            var files = new HashSet<string>(StringComparer.Ordinal);
            foreach (var shard in _shards)
            {
                var nodes = shard.GetAllNodes();
                for (var n = 0; n < nodes.Count; n++)
                {
                    Visit();
                    var node = nodes[n];
                    if (node.Id is not { Length: > 0 } id || !accept(node)) continue;
                    sources.Clear();
                    files.Clear();
                    var dependents = 0;
                    foreach (var other in _shards)
                    {
                        var inbound = other.GetEdgesByTarget(id);
                        for (var i = 0; i < inbound.Count; i++)
                        {
                            var edge = inbound[i];
                            if (!IsUsage(edge) || !Admits(edge) || edge.SourceId is not { Length: > 0 } source || source == id) continue;
                            // Each source is judged once, whether it counts or not.
                            if (!sources.Add(source) || Node(source) is not { } user) continue;
                            if (!_includeTests && IsTestNode(user)) continue;
                            dependents++;
                            files.Add(user.FilePath ?? string.Empty);
                        }
                    }
                    if (dependents > 0) hubs.Add(new Hub(node, dependents, files.Count));
                }
            }
            return hubs;
        }
    }
}
