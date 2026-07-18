using System.Diagnostics;
using System.Reflection;
using CodeContext.CSharp.Worker;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>Shared helpers for the compilation-reuse tests.</summary>
internal static class AnalyzerReuseFacts
{
    public static CSharpWorkspaceAnalyzer.AnalysisResult FreshAnalyze(IReadOnlyList<string> files)
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        analyzer.ReplaceFiles(files, CancellationToken.None);
        return analyzer.Analyze(CancellationToken.None);
    }

    public static void AssertFactsEqual(
        CSharpWorkspaceAnalyzer.AnalysisResult expected,
        CSharpWorkspaceAnalyzer.AnalysisResult actual)
    {
        var expectedNodes = expected.Nodes.Select(n => n.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var actualNodes = actual.Nodes.Select(n => n.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedNodes, actualNodes);

        var expectedEdges = expected.Edges.Select(e => e.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var actualEdges = actual.Edges.Select(e => e.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(expectedEdges, actualEdges);
    }
}

/// <summary>
/// Guards the persistent-compilation reuse in <see cref="CSharpWorkspaceAnalyzer"/>:
/// mutating the workspace through the real entry points must leave the incrementally
/// reused compilation producing facts byte-identical to a compilation freshly built
/// over the same final file set.
/// </summary>
public class CSharpWorkspaceAnalyzerReuseTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("cc-csharp-reuse-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ReusedCompilation_AcrossFullMutationSequence_MatchesFreshAnalyzer()
    {
        // Start with three files where B calls into A across a file boundary.
        var a = Write("A.cs", """
            namespace Demo { public class A { public int Value() => 1; } }
            """);
        var b = Write("B.cs", """
            namespace Demo { public class B { public int Use(A a) => a.Value(); } }
            """);
        var c = Write("C.cs", """
            namespace Demo { public class C { public void Noop() { } } }
            """);
        var toRename = Write("Renamable.cs", """
            namespace Demo { public class Renamable { } }
            """);
        var toDrop = Write("Dropped.cs", """
            namespace Demo { public class Dropped { } }
            """);

        var analyzer = new CSharpWorkspaceAnalyzer("test");
        analyzer.ReplaceFiles([a, b, c, toRename, toDrop], CancellationToken.None);
        analyzer.Analyze(CancellationToken.None);

        // Edit A to add a method that B will now call — cross-file semantics change.
        File.WriteAllText(a, """
            namespace Demo { public class A { public int Value() => 1; public int Extra() => 2; } }
            """);
        File.WriteAllText(b, """
            namespace Demo { public class B { public int Use(A a) => a.Value() + a.Extra(); } }
            """);
        analyzer.ApplyChanges(
            [new FileChangeDto(a, FileChangeKinds.Changed), new FileChangeDto(b, FileChangeKinds.Changed)],
            CancellationToken.None);

        // Add a brand-new file.
        var added = Write("Added.cs", """
            namespace Demo { public class Added { public void Go() { } } }
            """);
        analyzer.ApplyChanges([new FileChangeDto(added, FileChangeKinds.Created)], CancellationToken.None);

        // Rename a file (OldPath -> new path) via ApplyChanges.
        var renamed = Path.Combine(_tempDir, "Renamed.cs");
        File.Move(toRename, renamed);
        analyzer.ApplyChanges(
            [new FileChangeDto(renamed, FileChangeKinds.Renamed, toRename)],
            CancellationToken.None);

        // Delete a file.
        File.Delete(c);
        analyzer.ApplyChanges([new FileChangeDto(c, FileChangeKinds.Deleted)], CancellationToken.None);

        // Drop one file and keep the rest via SyncApprovedFiles.
        var finalFiles = new List<string> { a, b, added, renamed };
        analyzer.SyncApprovedFiles(finalFiles, CancellationToken.None);

        var reused = analyzer.Analyze(CancellationToken.None);
        var fresh = AnalyzerReuseFacts.FreshAnalyze(finalFiles);

        AnalyzerReuseFacts.AssertFactsEqual(fresh, reused);

        // The cross-file edit must have taken effect: B now calls A.Extra().
        Assert.Contains(reused.Edges, e =>
            e.Kind == "CALLS"
            && e.SourceId.Contains("Demo.B.Use")
            && e.TargetId.Contains("Demo.A.Extra"));
    }

    [Fact]
    public void ApplyChanges_ModifiedVanishedFile_MatchesFreshAndReportsDiagnostic()
    {
        var keep = Write("Keep.cs", "public class Keep { }");
        var gone = Write("Gone.cs", "public class Gone { }");

        var analyzer = new CSharpWorkspaceAnalyzer("test");
        analyzer.ReplaceFiles([keep, gone], CancellationToken.None);
        analyzer.Analyze(CancellationToken.None);

        // The file vanishes on disk, then a Modified change tries to re-read it.
        File.Delete(gone);
        analyzer.ApplyChanges([new FileChangeDto(gone, FileChangeKinds.Changed)], CancellationToken.None);

        var reused = analyzer.Analyze(CancellationToken.None);
        var fresh = AnalyzerReuseFacts.FreshAnalyze([keep]);
        AnalyzerReuseFacts.AssertFactsEqual(fresh, reused);

        var node = Assert.Single(reused.Nodes);
        Assert.Equal("Keep", node.Name);
        var diagnostic = Assert.Single(reused.Diagnostics);
        Assert.Equal(gone, diagnostic.FilePath);
        Assert.Equal("warning", diagnostic.Severity);
    }

    [Fact]
    public void ReplaceFiles_CalledTwice_IsIdempotentAndMatchesFresh()
    {
        var a = Write("A.cs", "public class A { public void M() { } }");
        var b = Write("B.cs", "public class B { public void N() { } }");

        var analyzer = new CSharpWorkspaceAnalyzer("test");
        analyzer.ReplaceFiles([a, b], CancellationToken.None);
        analyzer.Analyze(CancellationToken.None);

        // A second ReplaceFiles over the same set must not throw a duplicate-tree
        // error and must produce identical facts.
        analyzer.ReplaceFiles([a, b], CancellationToken.None);
        var reused = analyzer.Analyze(CancellationToken.None);

        AnalyzerReuseFacts.AssertFactsEqual(AnalyzerReuseFacts.FreshAnalyze([a, b]), reused);
    }

    [Fact]
    public void SyncApprovedFiles_Dropping_MatchesFresh()
    {
        var a = Write("A.cs", "public class A { }");
        var b = Write("B.cs", "public class B { }");
        var c = Write("C.cs", "public class C { }");

        var analyzer = new CSharpWorkspaceAnalyzer("test");
        analyzer.ReplaceFiles([a, b, c], CancellationToken.None);
        analyzer.Analyze(CancellationToken.None);

        analyzer.SyncApprovedFiles([a, c], CancellationToken.None);
        var reused = analyzer.Analyze(CancellationToken.None);

        AnalyzerReuseFacts.AssertFactsEqual(AnalyzerReuseFacts.FreshAnalyze([a, c]), reused);
    }

    [Fact]
    public void RenameWhereOldPathEqualsPath_DegeneratesToReplace_MatchesFresh()
    {
        var a = Write("A.cs", "public class A { public int V() => 1; }");
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        analyzer.ReplaceFiles([a], CancellationToken.None);
        analyzer.Analyze(CancellationToken.None);

        // Same path in and out (a content edit reported as a rename onto itself):
        // must not double-remove or throw, and must reflect the new content.
        File.WriteAllText(a, "public class A { public int V() => 1; public int W() => 2; }");
        analyzer.ApplyChanges(
            [new FileChangeDto(a, FileChangeKinds.Renamed, a)],
            CancellationToken.None);

        var reused = analyzer.Analyze(CancellationToken.None);
        AnalyzerReuseFacts.AssertFactsEqual(AnalyzerReuseFacts.FreshAnalyze([a]), reused);
        Assert.Contains(reused.Nodes, n => n.Name == "W");
    }
}

/// <summary>
/// Marks the drift-guard collection non-parallel: these tests mutate process-global
/// <see cref="Trace.Listeners"/> to suppress the intentional Debug.Assert, so they must
/// not run alongside any test that relies on assertions throwing.
/// </summary>
[CollectionDefinition(CSharpWorkspaceAnalyzerDriftTests.CollectionName, DisableParallelization = true)]
public sealed class CSharpWorkspaceAnalyzerDriftCollection;

/// <summary>
/// Exercises the drift guard's release self-heal path. The guard fires Debug.Assert on
/// purpose; under the test host that listener throws, so each test temporarily clears
/// and restores <see cref="Trace.Listeners"/> around the drifted Analyze. Because that
/// mutates process-global state, the whole collection is serialized.
/// </summary>
[Collection(CollectionName)]
public sealed class CSharpWorkspaceAnalyzerDriftTests : IDisposable
{
    public const string CollectionName = "CSharpWorkspaceAnalyzerDrift";

    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("cc-csharp-drift-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static readonly FieldInfo CompilationField = typeof(CSharpWorkspaceAnalyzer)
        .GetField("_compilation", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Runs Analyze with trace listeners suppressed so the intentional
    /// Debug.Assert in the drift guard does not terminate the test host.</summary>
    private static CSharpWorkspaceAnalyzer.AnalysisResult AnalyzeSuppressingAssert(
        CSharpWorkspaceAnalyzer analyzer)
    {
        var originalListeners = new TraceListener[Trace.Listeners.Count];
        Trace.Listeners.CopyTo(originalListeners, 0);
        Trace.Listeners.Clear();
        try
        {
            return analyzer.Analyze(CancellationToken.None);
        }
        finally
        {
            Trace.Listeners.AddRange(originalListeners);
        }
    }

    [Fact]
    public void DriftGuard_EmptyCompilation_SelfHealsAndEmitsDiagnostic()
    {
        var a = Write("A.cs", "public class A { public void M() { } }");
        var b = Write("B.cs", "public class B { public void N() { } }");

        var analyzer = new CSharpWorkspaceAnalyzer("test");
        analyzer.ReplaceFiles([a, b], CancellationToken.None);
        analyzer.Analyze(CancellationToken.None);

        // Force the compilation out of sync with the tree cache by reaching in and
        // replacing it with an empty compilation (count mismatch). Analyze must notice
        // the drift, rebuild from the trees, produce correct facts, and record a
        // diagnostic.
        CompilationField.SetValue(analyzer, CSharpCompilation.Create("Empty"));

        var reused = AnalyzeSuppressingAssert(analyzer);

        AnalyzerReuseFacts.AssertFactsEqual(AnalyzerReuseFacts.FreshAnalyze([a, b]), reused);
        Assert.Contains(reused.Diagnostics, d =>
            d.Severity == "warning" && d.Message.Contains("drifted"));
    }

    [Fact]
    public void DriftGuard_CountPreservingInstanceSwap_SelfHealsAndEmitsDiagnostic()
    {
        var a = Write("A.cs", "public class A { public void M() { } }");
        var b = Write("B.cs", "public class B { public void N() { } }");

        var analyzer = new CSharpWorkspaceAnalyzer("test");
        analyzer.ReplaceFiles([a, b], CancellationToken.None);
        analyzer.Analyze(CancellationToken.None);

        // Simulate a missed ReplaceSyntaxTree: the compilation still holds two trees
        // (count matches the cache) but one is a stale re-parsed instance that is not the
        // instance the cache holds. A count-only guard would pass and GetSemanticModel
        // would then throw; the instance-membership guard must catch this and self-heal.
        var live = (CSharpCompilation)CompilationField.GetValue(analyzer)!;
        var victim = live.SyntaxTrees.First();
        var impostor = CSharpSyntaxTree.ParseText(victim.ToString(), path: victim.FilePath);
        var drifted = live.ReplaceSyntaxTree(victim, impostor);
        CompilationField.SetValue(analyzer, drifted);

        var reused = AnalyzeSuppressingAssert(analyzer);

        AnalyzerReuseFacts.AssertFactsEqual(AnalyzerReuseFacts.FreshAnalyze([a, b]), reused);
        Assert.Contains(reused.Diagnostics, d =>
            d.Severity == "warning" && d.Message.Contains("drifted"));
    }
}
