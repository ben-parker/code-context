using CodeContext.CSharp.Worker;
using CodeContext.CSharp.Worker.Projects;
using CodeContext.CSharp.Worker.State;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Each input of a unit's fingerprint (plan 3.7) moves exactly the units whose facts it can change: the
/// unit itself, every unit depending on it however far up, and the fallback, which references every
/// project. Coverage is not an input. The workspace is a chain L1 &lt;- L2 &lt;- L3 &lt;- L4, an independent
/// Solo, and a file in no project.
/// </summary>
public sealed class CSharpWorkerFingerprintTests : IDisposable
{
    private const string Fallback = "<workspace>";

    private readonly string _root = Directory.CreateTempSubdirectory("cc-csharp-fingerprint-").FullName;

    public CSharpWorkerFingerprintTests()
    {
        Write("L1/L1.csproj", CSharpWorkerBaselineTests.Project());
        Write("L1/a.cs", "namespace L1; public class A { }");
        Write("L1/Sub/s.cs", "namespace L1; public class S { }");
        for (var level = 2; level <= 4; level++)
        {
            Write($"L{level}/L{level}.csproj", CSharpWorkerBaselineTests.Project($"<ProjectReference Include=\"..\\L{level - 1}\\L{level - 1}.csproj\" />"));
            Write($"L{level}/c.cs", $"namespace L{level}; public class C{level} {{ }}");
        }
        Write("Solo/Solo.csproj", CSharpWorkerBaselineTests.Project());
        Write("Solo/t.cs", "namespace Solo; public class T { }");
        Write("scripts/x.cs", "public class X { }");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static string Key(string project) => ProjectModel.KeyFor($"{project}/{project}.csproj");

    private async Task<Dictionary<string, string>> FingerprintsAsync(string identity = "test build")
    {
        var host = new InProcessCSharpHost(_root, (workspaceId, root) => new CSharpWorkspaceAnalyzer(workspaceId, root) { Identity = identity });
        await host.IndexAsync();
        return host.LastDeltas[0].Fingerprints!.ToDictionary(fingerprint => fingerprint.Unit, fingerprint => fingerprint.Value, StringComparer.Ordinal);
    }

    /// <summary>The units, by project name, whose fingerprint differs after <paramref name="change"/>.</summary>
    private async Task<string[]> MovedByAsync(Action change, string? identity = null)
    {
        var before = await FingerprintsAsync();
        change();
        var after = await FingerprintsAsync(identity ?? "test build");
        var names = new[] { "L1", "L2", "L3", "L4", "Solo" }.ToDictionary(Key, name => name, StringComparer.Ordinal);
        names[ProjectModel.KeyFor("L1/Sub/Sub.csproj")] = "Sub";
        names[Fallback] = Fallback;
        return after.Keys.Union(before.Keys)
            .Where(unit => !before.TryGetValue(unit, out var value) || !after.TryGetValue(unit, out var now) || value != now)
            .Select(unit => names[unit])
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static readonly string[] Chain = ["<workspace>", "L1", "L2", "L3", "L4"];

    [Fact]
    public async Task NothingChanged_MovesNothing() => Assert.Empty(await MovedByAsync(() => { }));

    [Fact]
    public async Task FileContent_MovesItsUnitAndDependents()
    {
        Assert.Equal(Chain, await MovedByAsync(() => Write("L1/a.cs", "namespace L1; public class A { int f; }")));
        Assert.Equal(["<workspace>", "L4"], await MovedByAsync(() => Write("L4/c.cs", "namespace L4; public class C4 { int f; }")));
    }

    [Fact]
    public async Task ADependencyThreeLevelsDown_MovesTheTop()
        => Assert.Contains("L4", await MovedByAsync(() => Write("L1/a.cs", "namespace L1; public class A { void M() { } }")));

    [Fact]
    public async Task FileAdded() => Assert.Equal(["<workspace>", "Solo"], await MovedByAsync(() => Write("Solo/u.cs", "namespace Solo; class U { }")));

    [Fact]
    public async Task FileRemoved()
    {
        Write("Solo/u.cs", "namespace Solo; class U { }");
        Assert.Equal(["<workspace>", "Solo"], await MovedByAsync(() => File.Delete(Path.Combine(_root, "Solo", "u.cs"))));
    }

    [Fact]
    public async Task FileInNoProject_MovesOnlyTheFallback()
        => Assert.Equal(["<workspace>"], await MovedByAsync(() => Write("scripts/x.cs", "public class X { int f; }")));

    [Fact]
    public async Task Constant()
        => Assert.Equal(["<workspace>", "Solo"], await MovedByAsync(() => Write("Solo/Solo.csproj",
            CSharpWorkerBaselineTests.Project(properties: "<DefineConstants>$(DefineConstants);EXTRA</DefineConstants>"))));

    [Fact]
    public async Task LanguageVersion()
        => Assert.Equal(["<workspace>", "Solo"], await MovedByAsync(() => Write("Solo/Solo.csproj",
            CSharpWorkerBaselineTests.Project(properties: "<LangVersion>12</LangVersion>"))));

    [Fact]
    public async Task MetadataReferenceTime()
    {
        var library = Path.Combine(_root, "Solo", "lib", "Library.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(library)!);
        File.Copy(typeof(CodeContext.Parser.Protocol.ParserProtocol).Assembly.Location, library);
        Write("Solo/Solo.csproj", CSharpWorkerBaselineTests.Project("<Reference Include=\"Library\"><HintPath>lib\\Library.dll</HintPath></Reference>"));

        Assert.Equal(["<workspace>", "Solo"], await MovedByAsync(() => File.SetLastWriteTimeUtc(library, DateTime.UtcNow.AddMinutes(5))));
    }

    [Fact]
    public async Task ProjectReference()
        => Assert.Equal(["<workspace>", "Solo"], await MovedByAsync(() => Write("Solo/Solo.csproj",
            CSharpWorkerBaselineTests.Project("<ProjectReference Include=\"..\\L1\\L1.csproj\" />"))));

    [Fact]
    public async Task OwnerChangeOfASharedFile()
    {
        // Solo also compiles L1's file; L1 owns it. A project appearing over it takes it from L1, and every unit
        // compiling it sees its new owner.
        Write("Solo/Solo.csproj", CSharpWorkerBaselineTests.Project("<Compile Include=\"..\\L1\\Sub\\s.cs\" />"));
        Assert.Equal(["<workspace>", "L1", "L2", "L3", "L4", "Solo", "Sub"],
            await MovedByAsync(() => Write("L1/Sub/Sub.csproj", CSharpWorkerBaselineTests.Project())));
    }

    [Fact]
    public async Task DirectoryBuildProps_MovesEveryProject()
        => Assert.Equal(["<workspace>", "L1", "L2", "L3", "L4", "Solo"], await MovedByAsync(() => Write("Directory.Build.props",
            "<Project><PropertyGroup><DefineConstants>$(DefineConstants);EVERYWHERE</DefineConstants></PropertyGroup></Project>")));

    [Fact]
    public async Task WorkerVersion_MovesEveryUnit()
        => Assert.Equal(["<workspace>", "L1", "L2", "L3", "L4", "Solo"], await MovedByAsync(() => { }, identity: "the next build"));

    [Fact]
    public void Identity_ChangesWithTheBuild_NotOnlyTheVersion()
    {
        // The informational version, then one module version id per assembly that decides facts.
        var parts = WorkerIdentity.Current.Split('|');
        Assert.Equal(6, parts.Length);
        Assert.Equal(typeof(CSharpWorkspaceAnalyzer).Assembly.ManifestModule.ModuleVersionId.ToString("N"), parts[1]);
        Assert.All(parts.Skip(1), part => Assert.Equal(32, part.Length));
        Assert.Equal(WorkerIdentity.Current, new CSharpWorkspaceAnalyzer("x").Identity);
    }

    // ---- the function itself ----

    private static UnitFingerprints.UnitInput Unit(string key, string signature = "sig", string owner = "a", params string[] references)
        => new(key, signature, BindsAgainstRuntime: false, [new("C:\\f.cs", new byte[32], owner)], references);

    [Fact]
    public void Compute_IsInKeyOrder_AndDeterministic()
    {
        var first = UnitFingerprints.Compute("id", [Unit("b"), Unit("a")]);
        Assert.Equal(["a", "b"], first.Select(fingerprint => fingerprint.Unit));
        Assert.Equal(first, UnitFingerprints.Compute("id", [Unit("b"), Unit("a")]));
        Assert.All(first, fingerprint => Assert.Matches("^[0-9a-f]{64}$", fingerprint.Value));
    }

    [Fact]
    public void Compute_EveryInputMovesTheValue()
    {
        var value = UnitFingerprints.Compute("id", [Unit("a")])[0].Value;
        Assert.NotEqual(value, UnitFingerprints.Compute("other", [Unit("a")])[0].Value);
        Assert.NotEqual(value, UnitFingerprints.Compute("id", [Unit("a", signature: "changed")])[0].Value);
        Assert.NotEqual(value, UnitFingerprints.Compute("id", [Unit("a", owner: "b")])[0].Value);
        Assert.NotEqual(value, UnitFingerprints.Compute("id", [Unit("a") with { BindsAgainstRuntime = true }])[0].Value);
        Assert.NotEqual(value, UnitFingerprints.Compute("id", [Unit("a") with { Files = [new("C:\\g.cs", new byte[32], "a")] }])[0].Value);
        var content = new byte[32];
        content[0] = 1;
        Assert.NotEqual(value, UnitFingerprints.Compute("id", [Unit("a") with { Files = [new("C:\\f.cs", content, "a")] }])[0].Value);
    }

    [Fact]
    public void Compute_IncludesWhatAUnitReferences()
    {
        var plain = UnitFingerprints.Compute("id", [Unit("r"), Unit("a", references: "r")]);
        var moved = UnitFingerprints.Compute("id", [Unit("r", signature: "changed"), Unit("a", references: "r")]);
        Assert.NotEqual(plain.Single(f => f.Unit == "a").Value, moved.Single(f => f.Unit == "a").Value);
        Assert.Throws<InvalidOperationException>(() => UnitFingerprints.Compute("id", [Unit("a", references: "r"), Unit("r")]));
    }
}
