using CodeContext.CSharp.Worker;
using CodeContext.CSharp.Worker.State;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// A restarted C# worker offered the state it saved (plan 3.7): it accepts only a state that is exactly
/// its own and the one the host recorded, re-walks only the units whose fingerprint moved, and sends only
/// what differs from what the host holds, so the host ends with the facts of a fresh scan. A baseline it
/// rejects is answered exactly as an index without one.
/// </summary>
public sealed class CSharpWorkerBaselineTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("cc-csharp-baseline-").FullName;
    private string Root => Path.Combine(_base, "repo");
    private string StateDirectory => Path.Combine(_base, "state");

    public CSharpWorkerBaselineTests()
    {
        Directory.CreateDirectory(StateDirectory);
        Write("Lib/Lib.csproj", Project());
        Write("Lib/Shape.cs", "namespace Lib; public class Shape { public int Area() => 1; }");
        Write("Lib/Circle.cs", "namespace Lib; public class Circle : Shape { public double Radius() => 2; }");
        Write("App/App.csproj", Project("<ProjectReference Include=\"..\\Lib\\Lib.csproj\" />"));
        Write("App/Program.cs", "namespace App; public class Program { public int Run() => new Lib.Circle().Area(); }");
        Write("Other/Other.csproj", Project());
        Write("Other/Tool.cs", "namespace Other; public class Tool { public void Go() => System.Math.Abs(1); }");
        Write("scripts/Loose.cs", "public class Loose { public void Go() => new Lib.Shape().Area(); }");
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    internal static string Project(string items = "", string properties = "") => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>disable</ImplicitUsings>{properties}</PropertyGroup>
          <ItemGroup>{items}</ItemGroup>
        </Project>
        """;

    private string Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>A host that indexed the workspace, saved the worker's state and restarted the worker: the baseline it would offer.</summary>
    private async Task<(InProcessCSharpHost Host, IndexBaseline Baseline)> SavedAndRestartedAsync(
        Func<string, string?, CSharpWorkspaceAnalyzer>? createAnalyzer = null)
    {
        var host = new InProcessCSharpHost(Root, createAnalyzer);
        await host.IndexAsync();
        var baseline = await host.SaveAsync(StateDirectory);
        host.Restart();
        return (host, baseline);
    }

    private async Task<(List<string> Facts, string Coverage)> FreshScanAsync()
    {
        var fresh = new InProcessCSharpHost(Root);
        await fresh.IndexAsync();
        return (fresh.Facts(), fresh.Coverage());
    }

    private async Task AssertEqualsAFreshScanAsync(InProcessCSharpHost host)
    {
        var (facts, coverage) = await FreshScanAsync();
        Assert.Equal(facts, host.Facts());
        Assert.Equal(coverage, host.Coverage());
    }

    private int FilesUnder(params string[] directories)
        => directories.Sum(directory => Directory.GetFiles(Path.Combine(Root, directory), "*.cs").Length);

    [Fact]
    public async Task Unchanged_SendsOneEmptyDeltaAndWalksNothing()
    {
        var (host, baseline) = await SavedAndRestartedAsync();
        var facts = host.Facts();

        var result = await host.IndexAsync(baseline);

        Assert.Equal(BaselineOutcomes.Accepted, result.Baseline);
        Assert.Null(result.BaselineReason);
        var delta = Assert.Single(host.LastDeltas);
        Assert.False(delta.ReplacesWorkspace);
        Assert.Empty(delta.ReplacesFiles);
        Assert.Empty(delta.Nodes);
        Assert.Empty(delta.Edges);
        Assert.Equal(baseline.Fingerprints, delta.Fingerprints);
        Assert.NotNull(delta.Coverage);
        Assert.Equal(facts, host.Facts());

        var verify = VerifyLine.Parse(host.LastVerify);
        Assert.Equal(0, verify.Walked);
        Assert.Equal(host.ApprovedFiles().Count, verify.Restored);

        // Progress covers the approved list either way and ends at its total.
        var files = host.ApprovedFiles().Count;
        Assert.Equal((files, files), (host.LastProgress[^1].FilesProcessed, host.LastProgress[^1].FilesTotal));
    }

    [Fact]
    public async Task ChangedLeaf_WalksItsUnitAndDependentsOnly()
    {
        var (host, baseline) = await SavedAndRestartedAsync();
        Write("App/Program.cs", "namespace App; public class Program { public double Run() => new Lib.Circle().Radius(); }");

        var result = await host.IndexAsync(baseline);

        Assert.Equal(BaselineOutcomes.Accepted, result.Baseline);
        // App and the fallback, which references every project; not Lib or Other.
        Assert.Equal(FilesUnder("App", "scripts"), VerifyLine.Parse(host.LastVerify).Walked);
        Assert.Equal([Path.Combine(Root, "App", "Program.cs")], host.LastDeltas[0].ReplacesFiles);
        await AssertEqualsAFreshScanAsync(host);
    }

    [Fact]
    public async Task ChangedRoot_WalksItsDependentsAndNotTheRest()
    {
        var (host, baseline) = await SavedAndRestartedAsync();
        Write("Lib/Shape.cs", "namespace Lib; public class Shape { public int Area() => 1; public int Sides() => 0; }");

        await host.IndexAsync(baseline);

        // Lib, App and the fallback, which references every project; not Other.
        Assert.Equal(FilesUnder("Lib", "App", "scripts"), VerifyLine.Parse(host.LastVerify).Walked);
        await AssertEqualsAFreshScanAsync(host);
    }

    [Fact]
    public async Task TouchedFile_IsHashedNotWalked()
    {
        var (host, baseline) = await SavedAndRestartedAsync();
        var path = Path.Combine(Root, "Lib", "Shape.cs");
        File.WriteAllText(path, File.ReadAllText(path));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        var result = await host.IndexAsync(baseline);

        Assert.Equal(BaselineOutcomes.Accepted, result.Baseline);
        Assert.Equal(0, VerifyLine.Parse(host.LastVerify).Walked);
        Assert.Empty(Assert.Single(host.LastDeltas).ReplacesFiles);
    }

    [Fact]
    public async Task RacyEntry_IsAlwaysRehashed()
    {
        // Saved within the racy window of the write: the stamp says so.
        var path = Write("Lib/Shape.cs", "namespace Lib; public class Shape { public int Area() => 1; }");
        var (host, baseline) = await SavedAndRestartedAsync();
        var stamp = WorkerStateFile.Read(File.ReadAllBytes(baseline.StatePath)).Files.Single(file => file.Path == path).Stamp;
        Assert.True(stamp.Racy);

        // Same size, same time, other bytes: only the content hash can tell.
        File.WriteAllText(path, "namespace Lib; public class Shape { public int Aria() => 1; }");
        File.SetLastWriteTimeUtc(path, new DateTime(stamp.LastWriteTicks, DateTimeKind.Utc));
        Assert.Equal(stamp.Size, new FileInfo(path).Length);

        await host.IndexAsync(baseline);

        Assert.True(VerifyLine.Parse(host.LastVerify).Walked > 0);
        await AssertEqualsAFreshScanAsync(host);
    }

    [Fact]
    public async Task OldEntry_IsNotRacy()
    {
        var path = Path.Combine(Root, "Lib", "Shape.cs");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        var (_, baseline) = await SavedAndRestartedAsync();
        Assert.False(WorkerStateFile.Read(File.ReadAllBytes(baseline.StatePath)).Files.Single(file => file.Path == path).Stamp.Racy);
    }

    [Fact]
    public async Task FileAddedAndRemovedWhileDown_AreSentAndRemoved()
    {
        var (host, baseline) = await SavedAndRestartedAsync();
        File.Delete(Path.Combine(Root, "Lib", "Circle.cs"));
        Write("Other/Hammer.cs", "namespace Other; public class Hammer { }");

        var result = await host.IndexAsync(baseline);

        Assert.Equal(BaselineOutcomes.Accepted, result.Baseline);
        var replaced = host.LastDeltas[0].ReplacesFiles;
        Assert.Contains(Path.Combine(Root, "Lib", "Circle.cs"), replaced);
        Assert.Contains(Path.Combine(Root, "Other", "Hammer.cs"), replaced);
        await AssertEqualsAFreshScanAsync(host);
    }

    public static TheoryData<string> Rejections() =>
    [
        "missing", "hash", "id", "fingerprint", "version", "root", "workspace", "malformed", "second request",
    ];

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task Rejected_ReplacesTheWorkspace_AndEqualsAFreshScan(string why)
    {
        var otherBuild = why == "version";
        var (host, baseline) = await SavedAndRestartedAsync(otherBuild
            ? null
            : (workspaceId, root) => new CSharpWorkspaceAnalyzer(workspaceId, why == "root" && root is not null ? Path.Combine(_base, "elsewhere", "..", "repo") + Path.DirectorySeparatorChar : root));
        if (otherBuild)
        {
            host = new InProcessCSharpHost(Root, (workspaceId, root) => new CSharpWorkspaceAnalyzer(workspaceId, root) { Identity = "another build" });
        }
        switch (why)
        {
            case "missing":
                File.Delete(baseline.StatePath);
                break;
            case "hash":
                baseline = baseline with { StateSha256 = new string('0', 64) };
                break;
            case "id":
                baseline = baseline with { StateId = new string('a', 32) };
                break;
            case "fingerprint":
                baseline = baseline with { Fingerprints = [.. baseline.Fingerprints.Skip(1)] };
                break;
            case "workspace":
                break;
            case "malformed":
                var bytes = File.ReadAllBytes(baseline.StatePath);
                bytes[8] = 99;
                File.WriteAllBytes(baseline.StatePath, bytes);
                baseline = baseline with { StateSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) };
                break;
            case "second request":
                await host.IndexAsync();
                break;
        }
        if (why == "root")
        {
            // The same directory reached another way is the same root; a different one is not.
            var same = await host.IndexAsync(baseline);
            Assert.Equal(BaselineOutcomes.Accepted, same.Baseline);
            return;
        }
        if (why == "workspace")
        {
            baseline = baseline with { };
            var state = File.ReadAllBytes(baseline.StatePath);
            host = new InProcessCSharpHost(Root);
            await host.IndexAsync();
            host.Restart();
            // A state saved for "default" offered to a worker whose workspace is another.
            var other = new WorkerSession(new NullOutput(), (_, root) => new CSharpWorkspaceAnalyzer("other", root));
            await other.OpenAsync(new OpenWorkspaceParams("other", Root, [], host.ApprovedFiles()), CancellationToken.None);
            var answer = await other.IndexAsync(1, new IndexWorkspaceParams("other", 1, host.ApprovedFiles(), baseline), CancellationToken.None);
            Assert.Equal(BaselineOutcomes.Rejected, answer.Baseline);
            Assert.Equal("state is for another workspace", answer.BaselineReason);
            Assert.Equal(state, File.ReadAllBytes(baseline.StatePath));
            return;
        }

        var result = await host.IndexAsync(baseline);

        Assert.Equal(BaselineOutcomes.Rejected, result.Baseline);
        Assert.False(string.IsNullOrWhiteSpace(result.BaselineReason));
        Assert.All(host.LastDeltas, delta => Assert.True(delta.ReplacesWorkspace));
        await AssertEqualsAFreshScanAsync(host);
    }

    [Fact]
    public async Task RejectedForAnotherRoot()
    {
        var host = new InProcessCSharpHost(Root);
        await host.IndexAsync();
        var baseline = await host.SaveAsync(StateDirectory);

        // The same files under another root: a copied repository.
        var copy = Path.Combine(_base, "copy");
        CopyDirectory(Root, copy);
        var moved = new InProcessCSharpHost(copy);
        var result = await moved.IndexAsync(baseline);

        Assert.Equal(BaselineOutcomes.Rejected, result.Baseline);
        Assert.Equal("state is for another root", result.BaselineReason);
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    [Fact]
    public async Task RejectedBaseline_EqualsAFullIndex()
    {
        var (host, baseline) = await SavedAndRestartedAsync();
        await host.IndexAsync(baseline with { StateSha256 = new string('0', 64) });
        var rejected = host.LastDeltas.ToList();

        var plain = new InProcessCSharpHost(Root);
        await plain.IndexAsync();

        // Chunk for chunk, the same answer as an index that carried no baseline.
        Assert.Equal(plain.LastDeltas.Count, rejected.Count);
        for (var index = 0; index < rejected.Count; index++)
        {
            var (a, b) = (rejected[index], plain.LastDeltas[index]);
            Assert.True(a.ReplacesWorkspace);
            Assert.Equal(b.ReplacesFiles, a.ReplacesFiles);
            Assert.Equal(b.Nodes.Select(Describe), a.Nodes.Select(Describe));
            Assert.Equal(b.Edges.Select(edge => edge.Id), a.Edges.Select(edge => edge.Id));
            Assert.Equal(b.IsLastForRequest, a.IsLastForRequest);
            Assert.Equal(Json(b.Coverage), Json(a.Coverage));
            Assert.Equal(b.Fingerprints, a.Fingerprints);
        }
        Assert.Equal(plain.Facts(), host.Facts());
        Assert.Equal(plain.Coverage(), host.Coverage());
    }

    private static string Describe(ProtocolNode node) => System.Text.Json.JsonSerializer.Serialize(node, ParserProtocolJsonContext.Default.ProtocolNode);

    private static string Json(IReadOnlyList<ProjectCoverage>? coverage)
        => coverage is null ? "null" : System.Text.Json.JsonSerializer.Serialize(coverage.ToList(), ParserProtocolJsonContext.Default.ListProjectCoverage);

    [Fact]
    public async Task EveryRequest_CarriesFingerprintsOnItsFirstChunkOnly()
    {
        var host = new InProcessCSharpHost(Root);
        await host.IndexAsync();
        Assert.NotEmpty(host.LastDeltas[0].Fingerprints!);
        Assert.All(host.LastDeltas.Skip(1), delta => Assert.Null(delta.Fingerprints));
        var before = host.LastDeltas[0].Fingerprints;

        // A change that changes no fact still sends them, moved.
        var path = Path.Combine(Root, "Lib", "Shape.cs");
        File.AppendAllText(path, "\n");
        await host.ApplyChangesAsync([new FileChangeDto(path, FileChangeKinds.Changed)]);
        var delta = Assert.Single(host.LastDeltas);
        Assert.Empty(delta.Nodes);
        Assert.NotEqual(before, delta.Fingerprints);
        Assert.Equal(
            ["<workspace>", .. before!.Select(fingerprint => fingerprint.Unit).Where(unit => unit != "<workspace>")],
            delta.Fingerprints!.Select(fingerprint => fingerprint.Unit).OrderBy(unit => unit == "<workspace>" ? 0 : 1).ThenBy(unit => unit, StringComparer.Ordinal));
    }

    [Fact]
    public async Task OpenAfterACommit_DoesNotChangeWhatIsSaved()
    {
        var host = new InProcessCSharpHost(Root);
        await host.IndexAsync();
        var first = await host.SaveAsync(StateDirectory);

        // The host re-opens with a new file before the next mutation: the worker loads it, emits nothing.
        Write("Other/Late.cs", "namespace Other; public class Late { }");
        await host.Session.OpenAsync(new OpenWorkspaceParams("default", Root, [], host.ApprovedFiles()), CancellationToken.None);
        var second = await host.SaveAsync(StateDirectory);

        var a = WorkerStateFile.Read(File.ReadAllBytes(first.StatePath));
        var b = WorkerStateFile.Read(File.ReadAllBytes(second.StatePath));
        Assert.Equal(a.Fingerprints, b.Fingerprints);
        Assert.Equal(a.Files.Select(file => file.Path), b.Files.Select(file => file.Path));
        Assert.Equal(a.FactHashes.Select(hash => hash.Path), b.FactHashes.Select(hash => hash.Path));
    }

    private sealed class NullOutput : IWorkerOutput
    {
        public Task ProgressAsync(AnalysisProgress progress, CancellationToken ct) => Task.CompletedTask;
        public Task DeltaAsync(AnalysisDelta delta, CancellationToken ct) => Task.CompletedTask;
        public void Diagnostic(string line) { }
    }
}
