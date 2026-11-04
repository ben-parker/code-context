using CodeContext.CSharp.Worker;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// One compilation serves every project in a workspace, but implicit usings belong to one
/// project each. These pin the failure that follows from confusing the two: a repository
/// type named like a framework type going ambiguous, which leaves a query printing a
/// confident <c>usedBy 0/0</c> for something plainly used.
/// </summary>
public class CSharpWorkerImplicitUsingsTests : IDisposable
{
    private const string Enabled = "<Project><PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>";
    private const string Disabled = "<Project><PropertyGroup><ImplicitUsings>disable</ImplicitUsings></PropertyGroup></Project>";
    private const string Silent = "<Project><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>";

    /// <summary>Binds only when the SDK usings are in scope, so it detects them.</summary>
    private const string NeedsUsings = """
        namespace App;
        public class Item { public int Weigh() => 1; }
        public class User { public int Calls(List<Item> items) => items.Select(i => i.Weigh()).Sum(); }
        """;

    private const string ProjB = """
        namespace Game.Model;
        public class Timer { public Timer() { } public int Interval { get; set; } public void Start() { } }
        public static class Path { public const char Separator = '/'; public static string Combine(string a) => a; }
        public class Task { public void Run() { } }
        public class Task<T> { public T Result = default!; }
        public class ObsoleteAttribute : System.Attribute { }
        public interface IDisposable { void Close(); }
        """;

    private const string ProjBUses = """
        using Game.Model;
        namespace Game.Client;
        public class Uses
        {
            public Timer Field = null!;
            public Timer Param(Timer timer) => timer;
            public object Typeof() => typeof(Timer);
            public Timer[] Array() => null!;
            public Task<int> Generic() => null!;
            public Timer Create() => new Timer();
            public Timer Implicit() => new();
            [Obsolete] public void Tagged() { }
            public void CallThroughParameter(Timer timer) => timer.Start();
            public int ReadThroughLocal() { var timer = new Timer(); return timer.Interval; }
            public string CallStatic() => Path.Combine("a");
            public char ReadConstant() => Path.Separator;
            public void CallOnNonGeneric(Task task) => task.Run();
            public int ReadOnGeneric(Task<int> task) => task.Result;
        }
        public class Derived : Timer { }
        public class Closer : IDisposable { public void Close() { } }
        """;

    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("cc-csharp-usings-").FullName;

    private string Root => Path.Combine(_tempDir, "repo");

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static string Id(string display) => CSharpWorkspaceAnalyzer.IdPrefix + "test:" + display;

    private string Write(string name, string content)
    {
        var path = Path.GetFullPath(Path.Combine(Root, name));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Files named <c>*.cs</c> are indexed; the rest only sit on disk.</summary>
    private CSharpWorkspaceAnalyzer.AnalysisResult Analyze(params (string Name, string Content)[] files)
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test", Root);
        var paths = files.Select(file => (file.Name, Path: Write(file.Name, file.Content))).ToList();
        analyzer.ReplaceFiles(
            paths.Where(file => file.Name.EndsWith(".cs", StringComparison.Ordinal)).Select(file => file.Path).ToList(),
            CancellationToken.None);
        return analyzer.Analyze(CancellationToken.None);
    }

    private static bool HasEdge(
        CSharpWorkspaceAnalyzer.AnalysisResult result, string source, string target, string kind)
        => result.Edges.Any(edge => edge.SourceId == Id(source) && edge.TargetId == Id(target) && edge.Kind == kind);

    private static void AssertEdge(
        CSharpWorkspaceAnalyzer.AnalysisResult result, string source, string target, string kind)
        => Assert.True(
            HasEdge(result, source, target, kind),
            $"Expected {source} =[{kind}]=> {target}. Edges:{Environment.NewLine}" +
            string.Join(Environment.NewLine, result.Edges.Select(edge => edge.Id)));

    private static bool SawUsings(CSharpWorkspaceAnalyzer.AnalysisResult result)
        => HasEdge(result, "App.User.Calls(System.Collections.Generic.List<App.Item>)", "App.Item.Weigh()", "CALLS");

    private CSharpWorkspaceAnalyzer.AnalysisResult AnalyzeMixedWorkspace() => Analyze(
        ("ProjA/ProjA.csproj", Enabled),
        ("ProjA/Code.cs", NeedsUsings),
        ("ProjB/ProjB.csproj", Silent),
        ("ProjB/Model.cs", ProjB),
        ("ProjB/Uses.cs", ProjBUses));

    [Theory]
    [InlineData("Game.Client.Uses", "Game.Model.Timer", "REFERENCES")]
    [InlineData("Game.Client.Uses.Param(Game.Model.Timer)", "Game.Model.Timer", "REFERENCES")]
    [InlineData("Game.Client.Uses.Typeof()", "Game.Model.Timer", "REFERENCES")]
    [InlineData("Game.Client.Uses.Array()", "Game.Model.Timer", "REFERENCES")]
    [InlineData("Game.Client.Uses.Generic()", "Game.Model.Task<T>", "REFERENCES")]
    [InlineData("Game.Client.Uses.Create()", "Game.Model.Timer.Timer()", "CALLS")]
    [InlineData("Game.Client.Uses.Implicit()", "Game.Model.Timer.Timer()", "CALLS")]
    [InlineData("Game.Client.Uses.Tagged()", "Game.Model.ObsoleteAttribute", "REFERENCES")]
    [InlineData("Game.Client.Uses.CallStatic()", "Game.Model.Path", "REFERENCES")]
    [InlineData("Game.Client.Derived", "Game.Model.Timer", "INHERITS")]
    [InlineData("Game.Client.Closer", "Game.Model.IDisposable", "IMPLEMENTS")]
    public void AnotherProjectsImplicitUsings_DoNotHideRepositoryTypes(string source, string target, string kind)
        => AssertEdge(AnalyzeMixedWorkspace(), source, target, kind);

    [Theory]
    [InlineData("Game.Client.Uses.CallThroughParameter(Game.Model.Timer)", "Game.Model.Timer.Start()", "CALLS")]
    [InlineData("Game.Client.Uses.ReadThroughLocal()", "Game.Model.Timer.Interval", "REFERENCES")]
    [InlineData("Game.Client.Uses.CallStatic()", "Game.Model.Path.Combine(string)", "CALLS")]
    [InlineData("Game.Client.Uses.ReadConstant()", "Game.Model.Path.Separator", "REFERENCES")]
    [InlineData("Game.Client.Uses.CallOnNonGeneric(Game.Model.Task)", "Game.Model.Task.Run()", "CALLS")]
    [InlineData("Game.Client.Uses.ReadOnGeneric(Game.Model.Task<int>)", "Game.Model.Task<T>.Result", "REFERENCES")]
    public void AnotherProjectsImplicitUsings_DoNotHideMembersOfRepositoryTypes(string source, string target, string kind)
        => AssertEdge(AnalyzeMixedWorkspace(), source, target, kind);

    [Fact]
    public void EachProjectKeepsItsOwnUsings_AcrossAReferenceInEitherDirection()
    {
        var result = Analyze(
            ("ProjA/ProjA.csproj", Enabled),
            ("ProjA/Code.cs", NeedsUsings),
            ("ProjA/Store.cs", """
                namespace App;
                public class Store
                {
                    public List<Item> Items { get; } = new();
                    public int Count(List<Item> items) => items.Count;
                    public void Fire(Game.Client.Uses uses) => uses.Param(uses.Field).Start();
                }
                """),
            ("ProjB/ProjB.csproj", Silent),
            ("ProjB/Model.cs", ProjB),
            ("ProjB/Uses.cs", ProjBUses),
            ("ProjB/Reader.cs", """
                using System.Linq;
                using App;
                namespace Game.Client;
                public class Reader
                {
                    public int Through(Store store) => store.Items.Select(item => item.Weigh()).Sum();
                    public int Count(Store store) => store.Count(store.Items);
                }
                """));

        Assert.True(SawUsings(result));
        AssertEdge(result, "Game.Client.Reader.Through(App.Store)", "App.Item.Weigh()", "CALLS");
        AssertEdge(result, "Game.Client.Reader.Count(App.Store)",
            "App.Store.Count(System.Collections.Generic.List<App.Item>)", "CALLS");
        AssertEdge(result, "App.Store.Fire(Game.Client.Uses)", "Game.Client.Uses.Param(Game.Model.Timer)", "CALLS");
        AssertEdge(result, "App.Store.Fire(Game.Client.Uses)", "Game.Model.Timer.Start()", "CALLS");
    }

    /// <summary>
    /// Ambiguity that survives without any injected using: the analysis binds against a
    /// newer framework than a Unity or netstandard project targets, so a type the framework
    /// later added collides with the file's own <c>using System;</c>.
    /// </summary>
    private const string SourceAgainstMetadata = """
        using System;
        using Game.Model;
        namespace Game.Client;
        public class Uses
        {
            public Range Param(Range range) => range;
            public Lazy<int> Generic() => null!;
            public Range Create() => new Range(1);
            public Lazy<int> CreateGeneric() => new Lazy<int>();
            [Obsolete] public void Tagged() { }
            [Obsolete("reason")] public void TaggedWithArgument() { }
        }
        public class Derived : Range { public Derived() : base(1) { } }
        """;

    private const string SourceAgainstMetadataModel = """
        namespace Game.Model;
        public class Range { public Range(int start) { } }
        public class Lazy<T> { public Lazy() { } }
        public class ObsoleteAttribute : System.Attribute
        {
            public ObsoleteAttribute() { }
            public ObsoleteAttribute(string reason) { }
        }
        """;

    [Theory]
    [InlineData("Game.Client.Uses.Param(Game.Model.Range)", "Game.Model.Range", "REFERENCES")]
    [InlineData("Game.Client.Uses.Generic()", "Game.Model.Lazy<T>", "REFERENCES")]
    [InlineData("Game.Client.Uses.Create()", "Game.Model.Range.Range(int)", "CALLS")]
    [InlineData("Game.Client.Uses.CreateGeneric()", "Game.Model.Lazy<T>.Lazy()", "CALLS")]
    [InlineData("Game.Client.Uses.Tagged()", "Game.Model.ObsoleteAttribute", "REFERENCES")]
    [InlineData("Game.Client.Uses.TaggedWithArgument()", "Game.Model.ObsoleteAttribute", "REFERENCES")]
    public void AmbiguityBetweenSourceAndMetadata_TakesTheSourceSymbol(string source, string target, string kind)
    {
        var result = Analyze(("Model.cs", SourceAgainstMetadataModel), ("Uses.cs", SourceAgainstMetadata));
        AssertEdge(result, source, target, kind);
    }

    [Fact]
    public void AmbiguityBetweenTwoSourceTypes_IsNotGuessed()
    {
        var result = Analyze(
            ("Model.cs", """
                namespace Game.A { public class Range { public Range() { } } public class Only { } }
                namespace Game.B { public class Range { public Range() { } } public class Only { } }
                """),
            ("Uses.cs", """
                using System;
                using Game.A;
                using Game.B;
                namespace Game.Client;
                public class Uses
                {
                    public Range WithMetadata(Range range) => new Range();
                    public Only WithoutMetadata(Only only) => new Only();
                }
                """));

        Assert.DoesNotContain(result.Edges, edge => edge.SourceId.StartsWith(Id("Game.Client.Uses."), StringComparison.Ordinal)
            && edge.Kind is "REFERENCES" or "CALLS");
    }

    [Fact]
    public void ProjectFilesAboveTheWorkspaceRoot_AreIgnored()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), Enabled);
        File.WriteAllText(Path.Combine(_tempDir, "Outer.csproj"), Enabled);

        Assert.False(SawUsings(Analyze(("src/Code.cs", NeedsUsings))));
    }

    [Theory]
    // Project files whose setting is on.
    [InlineData(true, "<Project><PropertyGroup><ImplicitUsings Condition=\"'$(TargetFramework)' == 'net10.0'\">enable</ImplicitUsings></PropertyGroup></Project>")]
    [InlineData(true, "<Project><PropertyGroup Condition=\"'$(TargetFramework)' == 'net10.0'\"><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>")]
    [InlineData(true, "<Project><PropertyGroup><ImplicitUsings>ENABLE</ImplicitUsings></PropertyGroup></Project>")]
    [InlineData(true, "<Project><Choose><When Condition=\"'$(X)' == ''\"><PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></When></Choose></Project>")]
    // A value that cannot be evaluated counts as on: wrongly off loses a whole project's
    // LINQ and collections, wrongly on costs only a name clash.
    [InlineData(true, "<Project><PropertyGroup><ImplicitUsings>$(UseImplicitUsings)</ImplicitUsings></PropertyGroup></Project>")]
    [InlineData(true, "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"><PropertyGroup><ImplicitUsings>true</ImplicitUsings></PropertyGroup></Project>")]
    [InlineData(true, "<Project><PropertyGroup><ImplicitUsings>disable</ImplicitUsings></PropertyGroup><PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>")]
    // A target that turns them off leaves the code other targets compile relying on them.
    [InlineData(true, "<Project><PropertyGroup><ImplicitUsings>enable</ImplicitUsings><ImplicitUsings Condition=\"'$(TargetFramework)' == 'net48'\">disable</ImplicitUsings></PropertyGroup></Project>")]
    // Project files whose setting is off.
    [InlineData(false, "<Project><PropertyGroup><!-- <ImplicitUsings>enable</ImplicitUsings> --></PropertyGroup></Project>")]
    [InlineData(false, "<Project><PropertyGroup><ImplicitUsings>enable</ImplicitUsings><ImplicitUsings>disable</ImplicitUsings></PropertyGroup></Project>")]
    [InlineData(false, "<Project><PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><PropertyGroup><ImplicitUsings>false</ImplicitUsings></PropertyGroup></Project>")]
    [InlineData(false, "<Project><Target Name=\"X\"><PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Target></Project>")]
    [InlineData(false, "<Project><PropertyGroup><ImplicitUsings>enable</PropertyGroup>")]
    // MSBuild compares the raw value, so padding turns the setting off.
    [InlineData(false, "<Project><PropertyGroup><ImplicitUsings> enable </ImplicitUsings></PropertyGroup></Project>")]
    public void TheSettingIsReadFromTheProjectFilesStructure(bool expected, string project)
        => Assert.Equal(expected, SawUsings(Analyze(("src/App.csproj", project), ("src/Code.cs", NeedsUsings))));

    [Theory]
    [InlineData(Enabled, Disabled, false)]
    [InlineData(Disabled, Enabled, true)]
    [InlineData(Enabled, Silent, true)]
    public void AProjectFileOverridesDirectoryBuildProps(string props, string project, bool expected)
        => Assert.Equal(expected, SawUsings(Analyze(
            ("Directory.Build.props", props), ("src/App.csproj", project), ("src/Code.cs", NeedsUsings))));

    private const string ChainsToParent =
        "<Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))\" />";

    [Theory]
    // MSBuild imports only the nearest Directory.Build.props, unless it chains upwards.
    [InlineData(Enabled, "<Project></Project>", false)]
    [InlineData(Enabled, $"<Project>{ChainsToParent}</Project>", true)]
    [InlineData(Enabled, $"<Project>{ChainsToParent}<PropertyGroup><ImplicitUsings>disable</ImplicitUsings></PropertyGroup></Project>", false)]
    [InlineData(Disabled, "<Project><PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>", true)]
    public void TheNearestDirectoryBuildPropsDecides(string outer, string inner, bool expected)
        => Assert.Equal(expected, SawUsings(Analyze(
            ("Directory.Build.props", outer),
            ("src/Directory.Build.props", inner),
            ("src/App/App.csproj", Silent),
            ("src/App/Code.cs", NeedsUsings))));

    private const string ImportsCommon = "<Project><Import Project=\"..\\..\\build\\Common.props\" /></Project>";

    [Theory]
    [InlineData(ImportsCommon, true)]
    [InlineData("<Project><Import Project=\"$(MSBuildThisFileDirectory)..\\..\\build\\Common.props\" /></Project>", true)]
    [InlineData("<Project><ImportGroup><Import Project=\"..\\..\\build\\Common.props\" /></ImportGroup></Project>", true)]
    [InlineData("<Project><Import Project=\"..\\..\\build\\Common.props\" /><PropertyGroup><ImplicitUsings>disable</ImplicitUsings></PropertyGroup></Project>", false)]
    [InlineData("<Project><PropertyGroup><ImplicitUsings>disable</ImplicitUsings></PropertyGroup><Import Project=\"..\\..\\build\\Common.props\" /></Project>", true)]
    [InlineData("<Project><Import Project=\"$(SomewhereUnknown)\\Common.props\" /></Project>", false)]
    public void ImportsOfLiteralPaths_AreFollowedInDocumentOrder(string project, bool expected)
        => Assert.Equal(expected, SawUsings(Analyze(
            ("build/Common.props", Enabled), ("src/App/App.csproj", project), ("src/App/Code.cs", NeedsUsings))));

    [Fact]
    public void ImportsOutsideTheWorkspaceRoot_AreIgnored()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(_tempDir, "Outer.props"), Enabled);

        Assert.False(SawUsings(Analyze(
            ("App.csproj", "<Project><Import Project=\"..\\Outer.props\" /></Project>"), ("Code.cs", NeedsUsings))));
    }

    [Fact]
    public void AnImportCycle_EndsWithoutAnAnswerFromIt()
        => Assert.False(SawUsings(Analyze(
            ("a.props", "<Project><Import Project=\"b.props\" /></Project>"),
            ("b.props", "<Project><Import Project=\"a.props\" /></Project>"),
            ("App.csproj", "<Project><Import Project=\"a.props\" /></Project>"),
            ("Code.cs", NeedsUsings))));

    [Fact]
    public void AChainInsideAnImportGroup_IsFollowed()
        => Assert.True(SawUsings(Analyze(
            ("Directory.Build.props", Enabled),
            ("src/Directory.Build.props", $"<Project><ImportGroup>{ChainsToParent}</ImportGroup></Project>"),
            ("src/App/App.csproj", Silent),
            ("src/App/Code.cs", NeedsUsings))));

    [Theory]
    [InlineData(Silent, Enabled, true)]
    [InlineData(Enabled, Disabled, false)]
    public void DirectoryBuildTargets_HaveTheLastWord(string project, string targets, bool expected)
        => Assert.Equal(expected, SawUsings(Analyze(
            ("Directory.Build.targets", targets), ("src/App.csproj", project), ("src/Code.cs", NeedsUsings))));

    private const string EnablesWhenUnset =
        "<Project><PropertyGroup><ImplicitUsings Condition=\"'$(ImplicitUsings)' == ''\">enable</ImplicitUsings></PropertyGroup></Project>";

    [Theory]
    [InlineData(Disabled, EnablesWhenUnset, false)]
    [InlineData(Silent, EnablesWhenUnset, true)]
    [InlineData(EnablesWhenUnset, Disabled, false)]
    public void ADefaultGuardedOnTheSettingItself_YieldsToAnEarlierValue(string props, string project, bool expected)
        => Assert.Equal(expected, SawUsings(Analyze(
            ("Directory.Build.props", props), ("src/App.csproj", project), ("src/Code.cs", NeedsUsings))));

    [Fact]
    public void AFileUnderNoProject_FollowsAProjectThatEnablesThem()
    {
        // Typically linked in by a .csproj elsewhere, which cannot be seen from here.
        var result = Analyze(
            ("ProjA/ProjA.csproj", Enabled),
            ("ProjA/Other.cs", "namespace Other; public class Thing { }"),
            ("Shared/Code.cs", NeedsUsings));

        Assert.True(SawUsings(result));
    }

    [Fact]
    public void AFileUnderNoProject_GetsNothingWhenNoProjectEnablesThem()
        => Assert.False(SawUsings(Analyze(
            ("ProjA/ProjA.csproj", Silent),
            ("ProjA/Other.cs", "namespace Other; public class Thing { }"),
            ("Shared/Code.cs", NeedsUsings))));

    [Fact]
    public void EveryDefaultNamespaceOfTheSdk_IsInScope()
    {
        var result = Analyze(("App.csproj", Enabled), ("Code.cs", """
            namespace App;
            public static class Extensions { public static int Fetch(this System.Net.Http.HttpClient client) => 1; }
            public class User { public int Calls(HttpClient client) => client.Fetch(); }
            """));

        AssertEdge(result, "App.User.Calls(System.Net.Http.HttpClient)",
            "App.Extensions.Fetch(System.Net.Http.HttpClient)", "CALLS");
    }

    [Fact]
    public void TypesOfOneNameAndDifferentKinds_KeepTheUsersOfTheirOneNode()
    {
        var result = Analyze(
            ("A/A.csproj", Silent),
            ("A/Options.cs", "namespace App; public class Options { public int Size; }"),
            ("A/UseA.cs", "namespace Use; public class UseA { public App.Options Get(App.Options o) => o; }"),
            ("B/B.csproj", Silent),
            ("B/Options.cs", "namespace App; public record Options(int Width);"),
            ("B/UseB.cs", "namespace Use; public class UseB { public App.Options Get(App.Options o) => new App.Options(1); }"));

        AssertEdge(result, "Use.UseA.Get(App.Options)", "App.Options", "REFERENCES");
        AssertEdge(result, "Use.UseB.Get(App.Options)", "App.Options", "REFERENCES");
    }

    [Fact]
    public void TheNearestProjectOwnsAFile()
        => Assert.False(SawUsings(Analyze(
            ("App.csproj", Enabled),
            ("nested/Nested.csproj", Silent),
            ("nested/deep/Code.cs", NeedsUsings))));

    [Fact]
    public void AProjectEdit_IsReadByTheNextFullRefresh()
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test", Root);
        var project = Write("src/App.csproj", Disabled);
        var code = Write("src/Code.cs", NeedsUsings);

        analyzer.ReplaceFiles([code], CancellationToken.None);
        Assert.False(SawUsings(analyzer.Analyze(CancellationToken.None)));

        File.WriteAllText(project, Enabled);
        analyzer.ReplaceFiles([code], CancellationToken.None);
        Assert.True(SawUsings(analyzer.Analyze(CancellationToken.None)));

        File.WriteAllText(project, Disabled);
        analyzer.ReplaceFiles([code], CancellationToken.None);
        Assert.False(SawUsings(analyzer.Analyze(CancellationToken.None)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AProjectEdit_IsReadByTheNextIncrementalChange(bool startEnabled)
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test", Root);
        var project = Write("src/App.csproj", startEnabled ? Enabled : Disabled);
        var code = Write("src/Code.cs", NeedsUsings);
        var other = Write("src/Other.cs", "namespace App; public class Other { }");
        analyzer.ReplaceFiles([code, other], CancellationToken.None);
        analyzer.SeedFactHashes(analyzer.Analyze(CancellationToken.None));

        File.WriteAllText(project, startEnabled ? Disabled : Enabled);
        File.WriteAllText(other, "namespace App; public class Other { public int Value; }");
        analyzer.SyncApprovedFiles([code, other], CancellationToken.None);
        analyzer.ApplyChanges([new FileChangeDto(other, FileChangeKinds.Changed, null)], CancellationToken.None);
        var analysis = analyzer.Analyze(CancellationToken.None);

        Assert.Equal(!startEnabled, SawUsings(analysis));
        Assert.Contains(code, analyzer.BuildIncrementalEmission(analysis).ReplacesFiles);
    }

    [Theory]
    [InlineData("namespace App; public class Item { public int Size; public int Weigh() => Size; }")]
    [InlineData("// header\nnamespace App; public class Item { public int Weigh() => 1; }")]
    [InlineData("#nullable enable\nnamespace App; public class Item { public int Weigh() => 1; }")]
    [InlineData("#define TOOLS\n#if TOOLS\nnamespace App; public class Item { public int Weigh() => 1; }\n#endif")]
    [InlineData("extern alias Other;\nusing System.Text; namespace App { public class Item { public int Weigh() => 1; } }")]
    [InlineData("global using System.Text; using System.Xml; namespace App { public class Item { public int Weigh() => 1; } }")]
    [InlineData("[assembly: System.CLSCompliant(true)] namespace App { public class Item { public int Weigh() => 1; } }")]
    [InlineData("var item = new Item(); item.Weigh(); public class Item { public int Weigh() => 1; }")]
    public void ImplicitUsings_LeaveEveryPositionAsTheFileHasIt(string code)
    {
        // Same directory name on both sides: top-level node ids carry the file's path.
        var plain = Analyze(("App.csproj", Disabled), ("Code.cs", code));
        var withUsings = Analyze(("App.csproj", Enabled), ("Code.cs", code));

        static IEnumerable<string> Positions(CSharpWorkspaceAnalyzer.AnalysisResult result)
            => result.Nodes.Select(node => $"{node.Id} {node.StartLine}:{node.StartColumn}-{node.EndLine}:{node.EndColumn}")
                .Concat(result.Edges.Select(edge => edge.Id + " " + string.Join(",", edge.Metadata?.Values ?? [])))
                .Order(StringComparer.Ordinal);

        Assert.NotEmpty(plain.Nodes);
        Assert.Equal(Positions(plain), Positions(withUsings));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(15, 6)]
    public void ImplicitUsings_DoNotAppearInTheNativeSyntaxTree(int? start, int? length)
    {
        const string code = "namespace App; public class Item { }";
        var plainAnalyzer = new CSharpWorkspaceAnalyzer("test", Root);
        Write("plain/Plain.csproj", Disabled);
        var plainFile = Write("plain/Code.cs", code);
        plainAnalyzer.ReplaceFiles([plainFile], CancellationToken.None);
        var analyzer = new CSharpWorkspaceAnalyzer("test", Root);
        Write("implicit/Plain.csproj", Enabled);
        var file = Write("implicit/Code.cs", code);
        analyzer.ReplaceFiles([file], CancellationToken.None);

        var plain = plainAnalyzer.GetNativeSyntaxTree(plainFile, start, length, 8, CancellationToken.None);
        var native = analyzer.GetNativeSyntaxTree(file, start, length, 8, CancellationToken.None);

        Assert.Equal(plain.Tree.GetRawText(), native.Tree.GetRawText());
    }

    [Fact]
    public void ImplicitUsings_ContributeNoFacts()
    {
        var result = Analyze(("App.csproj", Enabled), ("Code.cs", NeedsUsings));
        var code = Path.Combine(Root, "Code.cs");

        Assert.All(result.Nodes, node => Assert.Equal(code, node.FilePath));
        Assert.All(result.Edges, edge => Assert.StartsWith(Id("App."), edge.SourceId));
    }
}
