using CodeContext.CSharp.Worker;
using CodeContext.Core.Services;
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

    /// <summary>
    /// The worker's state saved and a new worker restored from it, after edits that move only a part of a partial
    /// type (no member, no located edge moves with it), made while the worker ran or while it was down: the facts
    /// the host holds must be a fresh scan's, parts included, as a hibernated worker woken from its state must.
    /// </summary>
    [Fact]
    public async Task APartMovedAlone_ConvergesToAFreshScan_LiveAndAcrossARestore()
    {
        var root = Path.Combine(_tempDir, "repo");
        var states = Path.Combine(root, ".codecontext", "cache", "default");
        Directory.CreateDirectory(states);
        string Put(string name, string content)
        {
            var path = Path.Combine(root, name);
            File.WriteAllText(path, content);
            return path;
        }
        Put("A.cs", "namespace Demo;\npublic partial class P { public void X() { } }\n");
        var b = Put("B.cs", "namespace Demo;\npublic partial class P\n{\n    public void Y() { }\n}\n");
        var c = Put("C.cs", "namespace Demo;\npublic partial class P { }\n");
        var host = new InProcessCSharpHost(root);
        await host.IndexAsync();

        async Task AssertFresh(string step)
        {
            var fresh = new InProcessCSharpHost(root);
            await fresh.IndexAsync();
            Assert.True(fresh.Facts().SequenceEqual(host.Facts()),
                $"After '{step}': stale {string.Join("; ", host.Facts().Except(fresh.Facts()))}");
        }

        // Live: the part's closing line moves down, below its only member.
        Put("B.cs", "namespace Demo;\npublic partial class P\n{\n    public void Y() { }\n\n\n}\n");
        await host.ApplyAsync([new FileChange(b, FileChangeType.Changed)]);
        await AssertFresh("a part's end moved while the worker ran");

        // Down: the part with no member moves, and another one appears, while no worker runs.
        foreach (var (step, edit) in new (string, Action)[]
        {
            ("a part with no member moved", () => Put("C.cs", "\n\n\nnamespace Demo;\npublic partial class P { }\n")),
            ("a part appeared", () => Put("D.cs", "namespace Demo;\npublic partial class P\n{\n}\n")),
            ("a part's end moved", () => Put("D.cs", "namespace Demo;\npublic partial class P\n{\n\n}\n")),
            ("a part disappeared", () => File.Delete(c)),
        })
        {
            foreach (var file in Directory.GetFiles(states)) File.Delete(file);
            var baseline = await host.SaveAsync(states);
            edit();
            host.Restart();
            var result = await host.IndexAsync(baseline);
            Assert.Equal(BaselineOutcomes.Accepted, result.Baseline);
            await AssertFresh(step + " while the worker was down");
        }
    }
}
