using CodeContext.CSharp.Worker;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// The C# worker keeps each file's walk and each file's bucket (the edges the host holds under it). When the two hold
/// the same edges in the same order, which is the usual case, they must share one list: a second copy of every edge
/// was about 9 MB on jellyfin. A bucket that differs from its walk keeps its own list, and the facts do not change.
/// </summary>
public sealed class CSharpWorkerFactCopyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cc-csharp-fact-copy-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ABucketEqualToItsWalk_SharesTheWalksEdgeList_AndOneThatDiffers_KeepsItsOwn()
    {
        var plain = Write("Plain.cs", """
            namespace Demo { public class Plain { public int Value() => new Other().Count(); } }
            """);
        var other = Write("Other.cs", """
            namespace Demo { public class Other { public int Count() => 1; } }
            """);
        // Both halves declare Split; the ordinally smaller path owns the node, so the other half's edges sourced at
        // it move into the owner's bucket and leave its own.
        var partA = Write("PartA.cs", """
            namespace Demo { public partial class Split { public void A() { } } }
            """);
        var partB = Write("PartB.cs", """
            namespace Demo { public partial class Split : System.IDisposable { public void Dispose() { } } }
            """);

        var analyzer = new CSharpWorkspaceAnalyzer("test");
        analyzer.ReplaceFiles([plain, other, partA, partB], CancellationToken.None);
        var result = analyzer.Analyze(CancellationToken.None);
        var buckets = result.Buckets.ToDictionary(bucket => bucket.FilePath);

        Assert.NotEmpty(buckets[plain].Edges);
        Assert.Same(analyzer.WalkedEdges(plain), buckets[plain].Edges);
        Assert.Same(analyzer.WalkedEdges(other), buckets[other].Edges);
        Assert.NotSame(analyzer.WalkedEdges(partA), buckets[partA].Edges);
        Assert.NotSame(analyzer.WalkedEdges(partB), buckets[partB].Edges);
        Assert.NotEqual(analyzer.WalkedEdges(partB)!.Count, buckets[partB].Edges.Count);

        AnalyzerReuseFacts.AssertFactsEqual(AnalyzerReuseFacts.FreshAnalyze([plain, other, partA, partB]), result);
    }

    [Fact]
    public void AnEditedFile_GetsANewSharedList_AndTheOthersKeepTheirs()
    {
        var a = Write("A.cs", "namespace Demo { public class A { public int Value() => 1; } }");
        var b = Write("B.cs", "namespace Demo { public class B { public int Use(A a) => a.Value(); } }");
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        analyzer.ReplaceFiles([a, b], CancellationToken.None);
        analyzer.Analyze(CancellationToken.None);
        var before = analyzer.WalkedEdges(a);

        File.WriteAllText(b, "namespace Demo { public class B { public int Use(A a) => a.Value() + a.Value(); } }");
        analyzer.ApplyChanges([new FileChangeDto(b, FileChangeKinds.Changed)], CancellationToken.None);
        var result = analyzer.Analyze(CancellationToken.None);
        var buckets = result.Buckets.ToDictionary(bucket => bucket.FilePath);

        Assert.Same(analyzer.WalkedEdges(b), buckets[b].Edges);
        Assert.Same(analyzer.WalkedEdges(a), buckets[a].Edges);
        AnalyzerReuseFacts.AssertFactsEqual(AnalyzerReuseFacts.FreshAnalyze([a, b]), result);
    }
}
