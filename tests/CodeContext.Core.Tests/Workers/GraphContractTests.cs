using CodeContext.Core.Services;
using CodeContext.CSharp.Worker;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// The producer/consumer contract for graph edge kinds.
///
/// ContextService (the consumer) reads specific edge kinds out of the graph, but the
/// graph's shape is produced independently by language workers. When the two drift —
/// ContextService assumes a kind no worker emits — features silently return nothing,
/// and mocked tests never catch it (that is exactly how the containment-edge bug
/// shipped). This suite makes the contract explicit and CI-enforced:
///
///   * a single source-of-truth per worker declares the edge kinds it is contracted to
///     emit (<see cref="CSharpContractedKinds"/>, <see cref="TypeScriptContractedKinds"/>);
///   * <see cref="EveryConsumedEdgeKind_IsCoveredByAWorkerContractOrExplicitlyReserved"/>
///     proves every kind ContextService consumes is covered by some worker contract or an
///     explicit exemption — so adding a kind to ContextService without a producer fails CI;
///   * per-kind theories run the *real* workers (Roslyn for C#, the Node worker for TS)
///     and assert each contracted kind actually appears — so a worker silently dropping a
///     kind also fails CI.
///
/// See CLAUDE.md ("Graph edge-kind contract"): every edge kind consumed by ContextService
/// must be covered here by a worker contract test or explicitly reserved/synthetic.
/// </summary>
public sealed class GraphContractTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("cc-graph-contract-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // ===================================================================================
    // Source of truth: what each worker is contracted to emit.
    // ===================================================================================

    /// <summary>Edge kinds the C# worker (Roslyn) is contracted to emit. C# emits no
    /// field nodes, so HAS_FIELD is absent; it also never emits CONTAINS/EXTENDS/IMPORTS.</summary>
    private static readonly IReadOnlySet<string> CSharpContractedKinds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CALLS", "MOCK_CALLS", "REFERENCES", "IMPLEMENTS", "INHERITS",
            "IMPLEMENTS_MEMBER", "OVERRIDES_MEMBER", "HAS_METHOD", "HAS_PROPERTY",
        };

    /// <summary>Edge kinds the TypeScript/JavaScript worker is contracted to emit. TS uses
    /// EXTENDS (not INHERITS), emits HAS_FIELD, and resolves IMPORTS; it emits no
    /// MOCK_CALLS/REFERENCES/INHERITS/CONTAINS.</summary>
    private static readonly IReadOnlySet<string> TypeScriptContractedKinds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CALLS", "EXTENDS", "HAS_FIELD", "HAS_METHOD", "HAS_PROPERTY",
            "IMPLEMENTS", "IMPLEMENTS_MEMBER", "IMPORTS", "OVERRIDES_MEMBER",
        };

    /// <summary>
    /// RESERVED = consumed by ContextService but produced by NO worker (yet). It is kept in
    /// ContextService defensively/for a future producer. CONTAINS is the live example of the
    /// drift this suite exists to surface: <c>ContainmentEdgeKinds</c> includes it, but
    /// neither the C# nor TS worker emits it. Reserving it here is a deliberate, visible
    /// acknowledgement — remove a kind from this set the moment a worker starts emitting it
    /// (see <see cref="ReservedKinds_AreNotAlsoClaimedByAWorkerContract"/>).
    /// </summary>
    private static readonly IReadOnlySet<string> ReservedEdgeKinds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CONTAINS",
        };

    /// <summary>
    /// SYNTHETIC = a relation filter token that is never a stored edge <c>Type</c>. USES is
    /// accepted by the uses/usedBy <c>relation</c> filter and ContextService maps *null-typed*
    /// edges to it (<c>edge.Type ?? "USES"</c>); no worker ever emits a "USES" edge, so it can
    /// never be — and must not be — covered by a worker contract test.
    /// </summary>
    private static readonly IReadOnlySet<string> SyntheticRelationKinds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "USES",
        };

    private static HashSet<string> ConsumedEdgeKinds()
    {
        // Read straight from the consumer's own lists (internal, exposed to this test
        // assembly via InternalsVisibleTo) so a copy can never rot. When someone adds a
        // kind to any of these sets, the completeness test below fails until a worker
        // contract — or an explicit RESERVED/SYNTHETIC entry — covers it.
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        consumed.UnionWith(ContextService.ContainmentEdgeKinds);
        consumed.UnionWith(ContextService.MethodFamilyEdgeKinds);
        consumed.UnionWith(ContextService.SemanticFileRelationshipKinds);
        consumed.UnionWith(ContextService.FilterableRelationKinds);
        return consumed;
    }

    // ===================================================================================
    // Completeness: consumer ⊆ producers ∪ reserved ∪ synthetic.
    // ===================================================================================

    [Fact]
    public void EveryConsumedEdgeKind_IsCoveredByAWorkerContractOrExplicitlyReserved()
    {
        var consumed = ConsumedEdgeKinds();

        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        covered.UnionWith(CSharpContractedKinds);
        covered.UnionWith(TypeScriptContractedKinds);
        covered.UnionWith(ReservedEdgeKinds);
        covered.UnionWith(SyntheticRelationKinds);

        var uncovered = consumed.Where(kind => !covered.Contains(kind)).OrderBy(k => k).ToList();

        Assert.True(uncovered.Count == 0,
            "ContextService consumes edge kind(s) covered by no worker contract, RESERVED, or "
            + "SYNTHETIC set: " + string.Join(", ", uncovered) + ". Either add a worker contract "
            + "test in GraphContractTests proving a worker emits it, or declare it RESERVED "
            + "(consumed defensively, no producer yet) / SYNTHETIC (a filter alias, never a "
            + "stored edge type) with justification.");
    }

    [Fact]
    public void ReservedKinds_AreNotAlsoClaimedByAWorkerContract()
    {
        // Keeps RESERVED honest: the instant a worker is contracted to emit a kind, it must
        // leave the reserved set (otherwise "reserved = nobody emits it" becomes a lie).
        var producedByAWorker = new HashSet<string>(CSharpContractedKinds, StringComparer.OrdinalIgnoreCase);
        producedByAWorker.UnionWith(TypeScriptContractedKinds);

        var contradictions = ReservedEdgeKinds.Concat(SyntheticRelationKinds)
            .Where(producedByAWorker.Contains).OrderBy(k => k).ToList();

        Assert.True(contradictions.Count == 0,
            "Kind(s) are declared RESERVED/SYNTHETIC yet also claimed by a worker contract: "
            + string.Join(", ", contradictions) + ". Remove them from the reserved/synthetic set.");
    }

    // ===================================================================================
    // C# worker contract: each contracted kind is produced by the real Roslyn analyzer.
    // ===================================================================================

    /// <summary>
    /// One minimal snippet per contracted C# kind. The keys of this table must equal
    /// <see cref="CSharpContractedKinds"/> exactly (guarded below), so adding a kind to the
    /// C# contract forces a snippet that proves the worker actually emits it.
    /// </summary>
    private static readonly IReadOnlyList<(string Kind, string Source)> CSharpContractSnippets =
    [
        ("HAS_METHOD", "public class Foo { public void Bar() { } }"),
        ("HAS_PROPERTY", "public class Foo { public int Value { get; set; } }"),
        ("IMPLEMENTS", "public interface IFoo { } public class Foo : IFoo { }"),
        ("INHERITS", "public class Base { } public class Derived : Base { }"),
        ("CALLS", "public class Foo { public void A() { B(); } public void B() { } }"),
        ("IMPLEMENTS_MEMBER",
            "public interface IFoo { void Bar(); } public class Foo : IFoo { public void Bar() { } }"),
        ("OVERRIDES_MEMBER",
            "public class Base { public virtual void Run() { } } "
            + "public class Derived : Base { public override void Run() { } }"),
        ("REFERENCES",
            "namespace Ex { public class Model { } "
            + "public class Consumer { private Model _model; public Model Current => _model; } }"),
        // MOCK_CALLS: the worker rewrites NSubstitute-shaped fluent/mock calls to the inner
        // production method. Self-contained stand-ins for Received/Returns reproduce it
        // without the NSubstitute package (mirrors CSharpWorkerAnalyzerTests' mock test).
        ("MOCK_CALLS", """
            using System;
            public sealed class FactAttribute : Attribute { }
            public interface IService { int Get(int value); }
            public static class MockExtensions
            {
                public static T Received<T>(this T value) => value;
                public static T Returns<T>(this T value, T configured) => value;
            }
            public class ServiceTests
            {
                [Fact]
                public void Verify()
                {
                    IService service = null;
                    service.Received().Get(1);
                    service.Get(3).Returns(4);
                }
            }
            """),
    ];

    public static IEnumerable<object[]> CSharpContractCases() =>
        CSharpContractSnippets.Select(snippet => new object[] { snippet.Kind, snippet.Source });

    [Theory]
    [MemberData(nameof(CSharpContractCases))]
    public void CSharpWorker_EmitsContractedKind(string kind, string source)
    {
        var result = Analyze(("Contract.cs", source));

        Assert.Contains(result.Edges,
            edge => string.Equals(edge.Kind, kind, StringComparison.OrdinalIgnoreCase));

        // The other half of the contract: a kind declared RESERVED/SYNTHETIC must not, in
        // fact, be produced by the real worker. If this fires, a worker started emitting a
        // kind the matrix claims nobody produces — promote it to a worker contract rather
        // than silencing this check.
        AssertNoReservedOrSyntheticKindsEmitted(result.Edges.Select(edge => edge.Kind), "C#");
    }

    [Fact]
    public void CSharpContractSnippets_CoverExactlyTheCSharpContractedKinds()
    {
        var snippetKinds = CSharpContractSnippets
            .Select(snippet => snippet.Kind)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            CSharpContractedKinds.OrderBy(k => k, StringComparer.OrdinalIgnoreCase),
            snippetKinds.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Asserts the real edges a worker just produced contain no kind the matrix declares
    /// RESERVED or SYNTHETIC. This is the escape-valve check: the self-consistency guards
    /// above only compare the declared tables to each other, so without this a worker that
    /// silently began emitting CONTAINS (or USES) would make the declaration a lie while
    /// every test stayed green — precisely the drift this suite exists to catch.
    /// </summary>
    private static void AssertNoReservedOrSyntheticKindsEmitted(
        IEnumerable<string> emittedKinds, string worker)
    {
        var offenders = emittedKinds
            .Where(kind => ReservedEdgeKinds.Contains(kind) || SyntheticRelationKinds.Contains(kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"The {worker} worker emitted edge kind(s) declared RESERVED/SYNTHETIC: "
            + string.Join(", ", offenders) + ". A worker now produces a kind the matrix claims "
            + "nobody produces. Update the matrix: move the kind out of ReservedEdgeKinds/"
            + "SyntheticRelationKinds into that worker's contracted set (with a proving snippet/"
            + "fixture) — do not delete this assertion.");
    }

    private CSharpWorkspaceAnalyzer.AnalysisResult Analyze(params (string Name, string Content)[] files)
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var paths = new List<string>();
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(_tempDir, name);
            File.WriteAllText(path, content);
            paths.Add(path);
        }
        analyzer.ReplaceFiles(paths, CancellationToken.None);
        return analyzer.Analyze(CancellationToken.None);
    }

    // ===================================================================================
    // TypeScript worker contract: one fixture workspace exercises every contracted kind.
    // Runs the real Node worker, so it carries the ExternalTooling quarantine trait — CI
    // runs it only when Node + the npm-installed worker are present. A single index pass
    // covers all kinds (the Node worker is heavyweight; do not spawn it per-kind).
    // ===================================================================================

    private const string TypeScriptBaseModule = """
        export interface IService { run(): void; }
        export class Base {
            count: number = 0;
            get label(): string { return 'b'; }
            greet(): string { return 'hi'; }
        }
        """;

    private const string TypeScriptMainModule = """
        import { Base, IService } from './base';
        export class Derived extends Base {
            greet(): string { return this.helper(); }
            helper(): string { return 'x'; }
        }
        export class Impl implements IService {
            run(): void { }
        }
        """;

    [Trait("Category", "ExternalTooling")]
    [Fact]
    public async Task TypeScriptWorker_EmitsAllContractedKinds_FromOneFixtureWorkspace()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "base.ts"), TypeScriptBaseModule);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "main.ts"), TypeScriptMainModule);

        await using var pipeline = new CSharpWorkerPipeline(
            _tempDir, TypeScriptWorkerProtocolTests.TypeScriptWorkerRegistration());
        await pipeline.GraphUpdateService.PerformInitialScanAsync(_tempDir, null, CancellationToken.None);

        var edges = await pipeline.RepositoryFactory.CreateEdgeRepository().GetAllAsync();
        var emitted = edges
            .Where(edge => edge.Type is not null)
            .Select(edge => edge.Type!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = TypeScriptContractedKinds
            .Where(kind => !emitted.Contains(kind))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.True(missing.Count == 0,
            "TypeScript worker did not emit contracted kind(s): " + string.Join(", ", missing)
            + ". Emitted: " + string.Join(", ", emitted.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) + ".");

        // The real edges must also never contain a kind the matrix declares RESERVED/SYNTHETIC.
        AssertNoReservedOrSyntheticKindsEmitted(emitted, "TypeScript");
    }
}
