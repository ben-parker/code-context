using CodeContext.CSharp.Worker;
using CodeContext.CSharp.Worker.State;
using CodeContext.Parser.Protocol;
using Xunit.Abstractions;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// The facts that settle across files, after a restore: a stub shared by files of two projects, a partial
/// type whose copies sit in two files, identifiers that qualify when they collide, and coverage of projects
/// that bound nothing in the restored run. Each scenario restores a worker and then edits through it,
/// comparing with a fresh scan after the restore and after every edit.
///
/// Each restored item of plan 3.7 is needed: <see cref="Faults"/> drops or changes one item of the state
/// before the restore (a test seam of the analyzer), and the scenario that depends on it must then diverge.
/// A fault that went unnoticed would mean the item is not checked.
/// </summary>
public sealed class CSharpWorkerRestoredFactsTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("cc-csharp-restored-").FullName;
    private string Root => Path.Combine(_base, "repo");
    private string StateDirectory => Path.Combine(_base, "state");

    /// <summary>A fault must be caught: returns the divergence it caused, written to the test output.</summary>
    private string Caught(string? divergence)
    {
        Assert.NotNull(divergence);
        output.WriteLine(divergence);
        return divergence;
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private string Write(string relative, string text)
    {
        Directory.CreateDirectory(StateDirectory);
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Indexes, saves, restores a new worker (with <paramref name="fault"/> applied to the state it reads),
    /// then applies each edit in turn. Returns where the graph or the coverage first differed from a fresh
    /// scan, or null when it never did.
    /// </summary>
    private async Task<string?> DivergenceAsync(Action<WorkerState>? fault, Action? whileDown, params Func<IReadOnlyList<FileChangeDto>>[] edits)
    {
        var host = new InProcessCSharpHost(Root, (workspaceId, root) => new CSharpWorkspaceAnalyzer(workspaceId, root) { RestoreFault = fault });
        await host.IndexAsync();
        var baseline = await host.SaveAsync(StateDirectory);
        host.Restart();
        whileDown?.Invoke();
        var result = await host.IndexAsync(baseline);
        Assert.Equal(BaselineOutcomes.Accepted, result.Baseline);
        if (await DifferenceAsync(host) is { } atRestore) return "after the restore: " + atRestore;

        for (var index = 0; index < edits.Length; index++)
        {
            await host.ApplyChangesAsync(edits[index]());
            if (await DifferenceAsync(host) is { } difference) return $"after edit {index + 1}: {difference}";
        }
        return null;
    }

    private async Task<string?> DifferenceAsync(InProcessCSharpHost host)
    {
        var fresh = new InProcessCSharpHost(Root);
        await fresh.IndexAsync();
        var (expected, actual) = (fresh.Facts(), host.Facts());
        if (!expected.SequenceEqual(actual))
        {
            return "facts differ; missing: " + string.Join(" ; ", expected.Except(actual).Take(3))
                + " stale: " + string.Join(" ; ", actual.Except(expected).Take(3));
        }
        return fresh.Coverage() == host.Coverage() ? null : $"coverage differs: {fresh.Coverage()} vs {host.Coverage()}";
    }

    private static FileChangeDto[] Changed(string path) => [new(path, FileChangeKinds.Changed)];

    private FileChangeDto[] Deleted(string relative)
    {
        File.Delete(PathOf(relative));
        return [new(PathOf(relative), FileChangeKinds.Deleted)];
    }

    // ---- stubs ----

    private void StubWorkspace()
    {
        Write("A/A.csproj", CSharpWorkerBaselineTests.Project());
        Write("A/a.cs", "namespace A; public class UsesMath { public int M() => System.Math.Abs(1); }");
        Write("A/keep.cs", "namespace A; public class Keep { }");
        Write("B/B.csproj", CSharpWorkerBaselineTests.Project());
        Write("B/b.cs", "namespace B; public class AlsoUsesMath { public int M() => System.Math.Abs(2); }");
        Write("B/keep.cs", "namespace B; public class Keep { }");
    }

    /// <summary>Two independent projects use one library symbol: the stub stays while either does, and only B's walk is restored when A's file goes.</summary>
    private Task<string?> StubScenarioAsync(Action<WorkerState>? fault)
    {
        StubWorkspace();
        return DivergenceAsync(fault, null, () => Deleted("A/a.cs"), () => Deleted("B/b.cs"));
    }

    [Fact]
    public async Task StubLeavesWhenItsLastUserGoes_AfterRestore()
        => Assert.Null(await StubScenarioAsync(null));

    // ---- partial types ----

    private void PartialWorkspace()
    {
        Write("P/P.csproj", CSharpWorkerBaselineTests.Project());
        Write("P/p1.cs", "namespace P; public partial class Thing { public void First() { } }");
        Write("P/p2.cs", "namespace P; public partial class Thing : System.IDisposable { public void Dispose() { } public void Second() { System.Math.Abs(1); } }");
    }

    /// <summary>A partial type in two files: the ordinally first owns the node, and when it goes the other's copy, with its own span, becomes the node.</summary>
    private Task<string?> PartialScenarioAsync(Action<WorkerState>? fault)
    {
        PartialWorkspace();
        return DivergenceAsync(fault, null,
            () => Changed(Write("P/p2.cs", "namespace P; public partial class Thing : System.IDisposable { public void Dispose() { } public void Second() { System.Math.Abs(2); } }")),
            () => Deleted("P/p1.cs"));
    }

    [Fact]
    public async Task PartialOwnershipMovesToTheNextFile_AfterRestore()
        => Assert.Null(await PartialScenarioAsync(null));

    // ---- identifiers ----

    [Fact]
    public async Task CollidingIdentifiersQualifyAndUnqualifyTogether_AfterRestore()
    {
        Write("X/X.csproj", CSharpWorkerBaselineTests.Project());
        Write("X/same.cs", "namespace N; public class Same { public void Go() { } }");
        Write("Y/Y.csproj", CSharpWorkerBaselineTests.Project());
        Write("Y/same.cs", "namespace N; public class Same { public void Run() { } }");
        Write("Y/other.cs", "namespace N; public class Other { }");

        Assert.Null(await DivergenceAsync(null, null,
            () => Changed(Write("Y/same.cs", "namespace N; public class Unique { public void Run() { } }")),
            () => Changed(Write("Y/other.cs", "namespace N; public class Same { }"))));
    }

    [Fact]
    public async Task CollidingIdentifiers_ChangedWhileDown_AreQualifiedAsAFreshScanQualifiesThem()
    {
        Write("X/X.csproj", CSharpWorkerBaselineTests.Project());
        Write("X/same.cs", "namespace N; public class Same { }");
        Write("Y/Y.csproj", CSharpWorkerBaselineTests.Project());
        Write("Y/same.cs", "namespace N; public class Unique { }");

        // Only Y moves; X's restored node must qualify because Y's now collides with it.
        Assert.Null(await DivergenceAsync(null, () => Write("Y/same.cs", "namespace N; public class Same { }")));
    }

    /// <summary>
    /// A type declared in two files of different projects, both compiled by one of them: its id was taken
    /// from whichever declaration the compilation listed first, and that order follows the order files were
    /// added, so deleting and re-adding a file changed the id. It now follows the ordinally first file.
    /// </summary>
    [Fact]
    public async Task ATypeDeclaredInTwoFiles_IsNamedTheSameWhateverOrderItsFilesWereAdded()
    {
        Write("A/A.csproj", CSharpWorkerBaselineTests.Project());
        var first = Write("A/a.cs", "namespace N; public class Dup { public void One() { } }");
        Write("B/B.csproj", CSharpWorkerBaselineTests.Project("<Compile Include=\"..\\A\\a.cs\" />"));
        Write("B/b.cs", "namespace N; public class Dup { public void Two() { } } public class User { public void M() => new Dup().One(); }");
        var text = File.ReadAllText(first);

        var host = new InProcessCSharpHost(Root);
        await host.IndexAsync();
        Assert.Null(await DifferenceAsync(host));

        // Removed and added back: the file's tree goes to the end of the compilation's list.
        File.Delete(first);
        await host.ApplyChangesAsync([new FileChangeDto(first, FileChangeKinds.Deleted)]);
        File.WriteAllText(first, text);
        await host.ApplyChangesAsync([new FileChangeDto(first, FileChangeKinds.Changed)]);
        Assert.Null(await DifferenceAsync(host));
    }

    // ---- coverage ----

    private void CoverageWorkspace()
    {
        // Unresolved names in a project that will bind nothing in the restored run, a project that is partial
        // only because a condition could not be read and was assumed true, and a file in no project.
        Write("Quiet/Quiet.csproj", CSharpWorkerBaselineTests.Project());
        Write("Quiet/q.cs", "namespace Quiet; public class Q { public void M() { Missing.Call(); Gone.Away(); Missing.Again(); } }");
        Write("Doubt/Doubt.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <PropertyGroup Condition="'$(Unknown)' =="><DefineConstants>$(DefineConstants);ODD</DefineConstants></PropertyGroup>
            </Project>
            """);
        Write("Doubt/d.cs", "namespace Doubt; public class D { public void M() { Nowhere.Call(); } }");
        Write("Busy/Busy.csproj", CSharpWorkerBaselineTests.Project());
        Write("Busy/b.cs", "namespace Busy; public class B { }");
        Write("loose/l.cs", "public class Loose { public void M() { Unknown.Thing(); } }");
    }

    private Task<string?> CoverageScenarioAsync(Action<WorkerState>? fault)
    {
        CoverageWorkspace();
        return DivergenceAsync(fault,
            () => Write("Busy/b.cs", "namespace Busy; public class B { public void M() { Absent.Call(); } }"),
            () => Changed(Write("Busy/b.cs", "namespace Busy; public class B { }")));
    }

    [Fact]
    public async Task CoverageAfterRestore_EqualsCoverageOfAFreshScan()
    {
        Assert.Null(await CoverageScenarioAsync(null));

        // The workspace has the cases it is meant to have.
        var host = new InProcessCSharpHost(Root);
        await host.IndexAsync();
        var coverage = host.LastDeltas[0].Coverage!;
        Assert.Equal(ProjectCoverageStates.Partial, coverage.Single(entry => entry.Name == "Doubt").State);
        Assert.True(coverage.Single(entry => entry.Name == "Quiet").UnresolvedNames > 0);
        Assert.Contains(coverage, entry => entry.State == ProjectCoverageStates.Fallback);
    }

    [Fact]
    public async Task CoverageListsProjectsThatBoundNothing()
    {
        CoverageWorkspace();
        var host = new InProcessCSharpHost(Root);
        await host.IndexAsync();
        var before = host.LastDeltas[0].Coverage!;
        var baseline = await host.SaveAsync(StateDirectory);
        host.Restart();

        await host.IndexAsync(baseline);

        Assert.Equal(0, VerifyLine.Parse(host.LastVerify).Walked);
        var after = host.LastDeltas[0].Coverage!;
        Assert.Equal(before.Select(entry => entry.Name), after.Select(entry => entry.Name));
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize(before.ToList(), ParserProtocolJsonContext.Default.ListProjectCoverage),
            System.Text.Json.JsonSerializer.Serialize(after.ToList(), ParserProtocolJsonContext.Default.ListProjectCoverage));
        Assert.True(after.Single(entry => entry.Name == "Quiet").UnresolvedNames > 0);
    }

    // ---- faults ----

    /// <summary>Drops the copy of a partial type's node that the file not owning it walked.</summary>
    private static void DropNonOwnerNodeCopy(WorkerState state)
    {
        var second = state.Files.Single(file => file.Path.EndsWith("p2.cs", StringComparison.Ordinal));
        Assert.Equal(1, second.Nodes.RemoveAll(node => node.Name == "Thing"));
    }

    /// <summary>Forgets that B's file used library symbols.</summary>
    private static void DropStubUseOfOneFile(WorkerState state)
    {
        var user = state.Files.Single(file => file.Path.EndsWith("b.cs", StringComparison.Ordinal) && file.StubNodes.Count > 0);
        user.StubNodes.Clear();
        user.StubEdges.Clear();
    }

    /// <summary>Forgets what the host holds for one file.</summary>
    private static void DropOneFactHash(WorkerState state)
        => state.FactHashes.RemoveAt(state.FactHashes.FindIndex(hash => !hash.Path.StartsWith("external:", StringComparison.Ordinal)));

    /// <summary>One file's unresolved-name count as it was before the last change.</summary>
    private static void StaleUnresolvedCount(WorkerState state)
    {
        var quiet = state.Files.Single(file => file.Path.EndsWith("q.cs", StringComparison.Ordinal));
        var name = quiet.UnresolvedNames!.Keys.First();
        quiet.UnresolvedNames[name]++;
    }

    [Fact]
    public async Task Faults_DroppingANonOwnerNodeCopy_FailsPartialOwnershipMovesToTheNextFile()
        => Caught(await PartialScenarioAsync(DropNonOwnerNodeCopy));

    [Fact]
    public async Task Faults_DroppingTheStubUseOfOneFile_FailsStubLeavesWhenItsLastUserGoes()
        => Caught(await StubScenarioAsync(DropStubUseOfOneFile));

    [Fact]
    public async Task Faults_RestoringAStaleUnresolvedNameCount_FailsCoverageAfterRestore()
        => Caught(await CoverageScenarioAsync(StaleUnresolvedCount));

    /// <summary>
    /// A fact hash is the worker's only record that the host holds a file's facts. Without it an unchanged
    /// restore resends that file, which <see cref="CSharpWorkerBaselineTests.Unchanged_SendsOneEmptyDeltaAndWalksNothing"/>
    /// forbids; and a file removed while down would never be removed, which the convergence check catches.
    /// </summary>
    [Fact]
    public async Task Faults_DroppingOneFactHash_FailsUnchangedSendsOneEmptyDelta_AndLeavesARemovedFile()
    {
        StubWorkspace();
        var host = new InProcessCSharpHost(Root, (workspaceId, root) => new CSharpWorkspaceAnalyzer(workspaceId, root) { RestoreFault = DropOneFactHash });
        await host.IndexAsync();
        var baseline = await host.SaveAsync(StateDirectory);
        host.Restart();
        await host.IndexAsync(baseline);
        Assert.NotEmpty(host.LastDeltas[0].ReplacesFiles);
        output.WriteLine("unchanged restore resent: " + string.Join(", ", host.LastDeltas[0].ReplacesFiles));

        // Removed while down: the file whose hash was dropped is never removed.
        var dropped = WorkerStateFile.Read(File.ReadAllBytes(baseline.StatePath)).FactHashes
            .First(hash => !hash.Path.StartsWith("external:", StringComparison.Ordinal)).Path;
        var restarted = new InProcessCSharpHost(Root, (workspaceId, root) => new CSharpWorkspaceAnalyzer(workspaceId, root) { RestoreFault = DropOneFactHash });
        await restarted.IndexAsync();
        var second = await restarted.SaveAsync(StateDirectory);
        restarted.Restart();
        File.Delete(dropped);
        await restarted.IndexAsync(second);
        Caught(await DifferenceAsync(restarted));
    }
}
