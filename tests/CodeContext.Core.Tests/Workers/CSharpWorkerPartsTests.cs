using CodeContext.CSharp.Worker;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// A symbol declared in several places is one node, at its ordinally first file's declaration, carrying the other
/// declarations as parts: so the lines of a partial type's second part belong to the type. The parts follow edits
/// of either file, and a bucket whose node carries none hashes as it did before parts existed.
/// </summary>
public sealed class CSharpWorkerPartsTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("cc-csharp-parts-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static ProtocolNode NodeOf(CSharpWorkspaceAnalyzer.AnalysisResult analysis, string identifier)
        => analysis.Buckets.SelectMany(bucket => bucket.Nodes).Single(node => node.Identifier == identifier);

    [Fact]
    public void APartialTypeAndMember_CarryTheirOtherDeclarationsAsParts_AndFollowEdits()
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var p1 = Write("P1.cs", "namespace Demo;\npublic partial class P { private int n; public int X() => n; partial void Hook(); }\n");
        var p2 = Write("P2.cs", "namespace Demo;\n\npublic partial class P\n    : System.IDisposable\n{\n    public void Dispose() { }\n    partial void Hook() { }\n}\n");
        var plain = Write("Q.cs", "namespace Demo;\npublic class Q { }\n");
        analyzer.ReplaceFiles([p1, p2, plain], CancellationToken.None);
        var analysis = analyzer.Analyze(CancellationToken.None);
        analyzer.SeedFactHashes(analysis);

        var type = NodeOf(analysis, "csharp:Demo.P");
        Assert.Equal((p1, 1), (type.FilePath, type.StartLine));
        var part = Assert.Single(type.Parts!);
        Assert.Equal((p2, 2, 7, 0, 1), (part.FilePath, part.StartLine, part.EndLine, part.StartColumn, part.EndColumn));
        // A partial method: its other declaration is a part too, whichever file holds the node.
        var hook = analysis.Buckets.SelectMany(bucket => bucket.Nodes).Single(node => node.Name == "Hook");
        Assert.Single(hook.Parts!);
        Assert.Null(NodeOf(analysis, "csharp:Demo.Q").Parts);

        // An edit of the second part moves its span, and the owner's file is emitted again with the new one.
        File.WriteAllText(p2, "namespace Demo;\npublic partial class P : System.IDisposable { public void Dispose() { } partial void Hook() { } }\n");
        analyzer.ApplyChanges([new FileChangeDto(p2, FileChangeKinds.Changed)], CancellationToken.None);
        var emission = analyzer.BuildIncrementalEmission(analyzer.Analyze(CancellationToken.None));
        Assert.Contains(p1, emission.ReplacesFiles);
        var moved = emission.Nodes.Single(node => node.Identifier == "csharp:Demo.P");
        Assert.Equal(1, Assert.Single(moved.Parts!).StartLine);
        analyzer.CommitIncremental(emission);

        // The second part removed: the type is declared in one place again.
        File.Delete(p2);
        analyzer.ApplyChanges([new FileChangeDto(p2, FileChangeKinds.Deleted)], CancellationToken.None);
        emission = analyzer.BuildIncrementalEmission(analyzer.Analyze(CancellationToken.None));
        Assert.Contains(p1, emission.ReplacesFiles);
        Assert.Null(emission.Nodes.Single(node => node.Identifier == "csharp:Demo.P").Parts);
    }
}
