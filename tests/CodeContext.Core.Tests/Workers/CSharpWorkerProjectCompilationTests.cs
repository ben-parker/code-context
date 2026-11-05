using System.Text.Json;
using CodeContext.CSharp.Worker;
using CodeContext.Parser.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// One compilation per project, as a build makes them. Each test is a shape a single
/// workspace-wide compilation got wrong: code compiled by a project's own constants, names
/// that mean different things in different projects, and values of library types.
/// </summary>
public class CSharpWorkerProjectCompilationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cc-csharp-projects-").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string Project(string body = "", string properties = "") => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework>{properties}</PropertyGroup>
          <ItemGroup>{body}</ItemGroup>
        </Project>
        """;

    private string Write(string relative, string content)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private CSharpWorkspaceAnalyzer.AnalysisResult Analyze(params (string Name, string Content)[] files)
    {
        var written = files.Select(file => (file.Name, Path: Write(file.Name, file.Content))).ToList();
        var analyzer = new CSharpWorkspaceAnalyzer("test", _root);
        analyzer.ReplaceFiles(
            written.Where(file => file.Name.EndsWith(".cs", StringComparison.Ordinal)).Select(file => file.Path).ToList(),
            CancellationToken.None);
        return analyzer.Analyze(CancellationToken.None);
    }

    private static bool HasNode(CSharpWorkspaceAnalyzer.AnalysisResult result, string display)
        => result.Nodes.Any(node => node.Id.EndsWith(":" + display, StringComparison.Ordinal));

    private static bool HasEdge(CSharpWorkspaceAnalyzer.AnalysisResult result, string source, string target, string kind)
        => result.Edges.Any(edge => edge.Kind == kind
            && edge.SourceId.EndsWith(":" + source, StringComparison.Ordinal)
            && edge.TargetId.EndsWith(":" + target, StringComparison.Ordinal));

    [Fact]
    public void CodeUnderADefinedConstantIsSeenAndItsElseBranchIsNot()
    {
        var result = Analyze(
            ("App/App.csproj", Project(properties: "<DefineConstants>$(DefineConstants);TOOLS</DefineConstants>")),
            ("App/Code.cs", """
                public class Code
                {
                #if TOOLS
                    public void OnlyInTools() { }
                #else
                    public void OnlyWithoutTools() { }
                #endif
                #if DEBUG
                    public void OnlyInDebug() { }
                #else
                    public void OnlyInRelease() { }
                #endif
                #if NET10_0_OR_GREATER
                    public void OnlyOnModernFramework() { }
                #endif
                #if NETFRAMEWORK
                    public void OnlyOnOldFramework() { }
                #endif
                }
                """));

        Assert.True(HasNode(result, "Code.OnlyInTools()"));
        Assert.False(HasNode(result, "Code.OnlyWithoutTools()"));
        Assert.True(HasNode(result, "Code.OnlyInDebug()"));
        Assert.False(HasNode(result, "Code.OnlyInRelease()"));
        Assert.True(HasNode(result, "Code.OnlyOnModernFramework()"));
        Assert.False(HasNode(result, "Code.OnlyOnOldFramework()"));
    }

    [Fact]
    public void EachProjectAppliesItsOwnConstants()
    {
        var result = Analyze(
            ("A/A.csproj", Project(properties: "<DefineConstants>$(DefineConstants);FEATURE</DefineConstants>")),
            ("A/Code.cs", "public class InA {\n#if FEATURE\npublic void On() { }\n#else\npublic void Off() { }\n#endif\n}"),
            ("B/B.csproj", Project()),
            ("B/Code.cs", "public class InB {\n#if FEATURE\npublic void On() { }\n#else\npublic void Off() { }\n#endif\n}"));

        Assert.True(HasNode(result, "InA.On()"));
        Assert.False(HasNode(result, "InA.Off()"));
        Assert.True(HasNode(result, "InB.Off()"));
        Assert.False(HasNode(result, "InB.On()"));
    }

    [Fact]
    public void AFileWithNoProjectKeepsTodaysIdsAndUsesNoConstants()
    {
        var result = Analyze(("Loose.cs", "public class Loose {\n#if DEBUG\npublic void On() { }\n#else\npublic void Off() { }\n#endif\n}"));

        Assert.Contains(result.Nodes, node => node.Id == "csharp:test:Loose" && node.Identifier == "csharp:Loose");
        Assert.True(HasNode(result, "Loose.Off()"));
        Assert.False(HasNode(result, "Loose.On()"));
    }

    private const string ToolProgram = """
        public class Program
        {
            public static void Main() { Helper.Run(); }
        }
        public static class Helper { public static void Run() { } }
        """;

    private CSharpWorkspaceAnalyzer.AnalysisResult TwoTools() => Analyze(
        ("ToolA/ToolA.csproj", Project()),
        ("ToolA/Program.cs", ToolProgram),
        ("ToolB/ToolB.csproj", Project()),
        ("ToolB/Program.cs", ToolProgram));

    [Fact]
    public void SameNamedTypesInTwoProjectsAreTwoNodesInTheirOwnFiles()
    {
        var result = TwoTools();

        var mains = result.Nodes.Where(node => node.Name == "Main").ToList();
        Assert.Equal(2, mains.Count);
        Assert.Equal(2, mains.Select(node => node.FilePath).Distinct().Count());
        Assert.Contains(mains, node => node.FilePath!.EndsWith(Path.Combine("ToolA", "Program.cs"), StringComparison.Ordinal));
        Assert.Contains(mains, node => node.FilePath!.EndsWith(Path.Combine("ToolB", "Program.cs"), StringComparison.Ordinal));
    }

    [Fact]
    public void CallersStayWithTheirOwnProjectsSymbol()
    {
        var result = TwoTools();

        foreach (var main in result.Nodes.Where(node => node.Name == "Main"))
        {
            var target = Assert.Single(result.Edges, edge => edge.SourceId == main.Id && edge.Kind == "CALLS").TargetId;
            var run = Assert.Single(result.Nodes, node => node.Id == target);
            Assert.Equal(main.FilePath, run.FilePath);
        }
    }

    [Fact]
    public void ACollidingIdentifierNamesItsProjectEverywhereAndAnIdNeverChanges()
    {
        var alone = Analyze(("ToolA/ToolA.csproj", Project()), ("ToolA/Program.cs", ToolProgram));
        var aloneMain = Assert.Single(alone.Nodes, node => node.Name == "Main");
        Assert.Equal("csharp:Program.Main()", aloneMain.Identifier);

        var both = TwoTools();

        Assert.Contains(both.Nodes, node => node.Id == aloneMain.Id && node.Identifier == "csharp:ToolA#Program.Main()");
        Assert.Contains(both.Nodes, node => node.Identifier == "csharp:ToolB#Program.Main()");
        Assert.DoesNotContain(both.Nodes, node => node.Identifier == "csharp:Program.Main()");
    }

    [Fact]
    public void ANameNoOtherProjectDeclaresStaysBare()
    {
        var result = Analyze(
            ("A/A.csproj", Project()),
            ("A/Code.cs", "namespace App; public class OnlyHere { public class Nested { } }"),
            ("B/B.csproj", Project()),
            ("B/Code.cs", "namespace App; public class Other { }"));

        Assert.Contains(result.Nodes, node => node.Identifier == "csharp:App.OnlyHere");
        Assert.Contains(result.Nodes, node => node.Identifier == "csharp:App.OnlyHere.Nested");
        Assert.All(result.Nodes, node => Assert.StartsWith("csharp:test:", node.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void ACollisionWithALooseFileQualifiesBoth()
    {
        var result = Analyze(
            ("A/A.csproj", Project()),
            ("A/Code.cs", "public class Shared { }"),
            ("scripts/Loose.cs", "public class Shared { }"));

        Assert.Contains(result.Nodes, node => node.Identifier == "csharp:A#Shared");
        Assert.Contains(result.Nodes, node => node.Identifier == "csharp:<workspace>#Shared");
    }

    [Fact]
    public void ACollisionAppearingOnAnEditReEmitsTheUntouchedFile()
    {
        var a = Write("A/Code.cs", "public class Thing { }");
        Write("A/A.csproj", Project());
        var b = Write("B/Code.cs", "public class Other { }");
        Write("B/B.csproj", Project());
        var analyzer = new CSharpWorkspaceAnalyzer("test", _root);
        analyzer.ReplaceFiles([a, b], CancellationToken.None);
        analyzer.SeedFactHashes(analyzer.Analyze(CancellationToken.None));

        File.WriteAllText(b, "public class Thing { }");
        analyzer.ApplyChanges([new FileChangeDto(b, FileChangeKinds.Changed)], CancellationToken.None);
        var emission = analyzer.BuildIncrementalEmission(analyzer.Analyze(CancellationToken.None));

        Assert.Contains(a, emission.ReplacesFiles);
        Assert.Contains(emission.Nodes, node => node.Identifier == "csharp:A#Thing");
        Assert.Contains(emission.Nodes, node => node.Identifier == "csharp:B#Thing");
    }

    [Fact]
    public void AProjectReferenceBindsAcrossProjectsIncludingTransitively()
    {
        var result = Analyze(
            ("Core/Core.csproj", Project()),
            ("Core/Item.cs", "namespace Core; public class Item { public int Weigh() => 1; }"),
            ("Mid/Mid.csproj", Project("""<ProjectReference Include="..\Core\Core.csproj" />""")),
            ("Mid/Maker.cs", "namespace Mid; public class Maker { public Core.Item Make() => new Core.Item(); }"),
            ("App/App.csproj", Project("""<ProjectReference Include="..\Mid\Mid.csproj" />""")),
            ("App/Use.cs", """
                public class Use
                {
                    public int Through(Mid.Maker maker) => maker.Make().Weigh();
                    public Core.Item Direct() => null;
                }
                """));

        Assert.True(HasEdge(result, "Use.Through(Mid.Maker)", "Mid.Maker.Make()", "CALLS"));
        Assert.True(HasEdge(result, "Use.Through(Mid.Maker)", "Core.Item.Weigh()", "CALLS"));
        Assert.True(HasEdge(result, "Use.Direct()", "Core.Item", "REFERENCES"));
    }

    [Fact]
    public void AProjectDoesNotSeeWhatItDoesNotReference()
    {
        var result = Analyze(
            ("Lib/Lib.csproj", Project()),
            ("Lib/Hidden.cs", "namespace Lib; public class Hidden { public void Call() { } }"),
            ("App/App.csproj", Project()),
            ("App/Use.cs", "public class Use { public void Run(Lib.Hidden hidden) => hidden.Call(); }"));

        Assert.False(HasEdge(result, "Use.Run(Lib.Hidden)", "Lib.Hidden.Call()", "CALLS"));
    }

    [Fact]
    public void InternalsReachAFriendDeclaredByProjectItem()
    {
        var result = Analyze(
            ("Lib/Lib.csproj", Project("""<InternalsVisibleTo Include="Lib.Tests" />""")),
            ("Lib/Secret.cs", "namespace Lib; internal class Secret { internal int Value() => 1; }"),
            ("Tests/Lib.Tests.csproj", Project("""<ProjectReference Include="..\Lib\Lib.csproj" />""")),
            ("Tests/Check.cs", "public class Check { public int Run() => new Lib.Secret().Value(); }"));

        Assert.True(HasEdge(result, "Check.Run()", "Lib.Secret.Value()", "CALLS"));
    }

    [Fact]
    public void AFileCompiledByTwoProjectsIsOneNodeThatBothReach()
    {
        var result = Analyze(
            ("Shared/Shared.csproj", Project()),
            ("Shared/Common.cs", "namespace Common; public class Tool { public int Run() => 1; }"),
            ("App/App.csproj", Project("""<Compile Include="..\Shared\Common.cs" />""")),
            ("App/Use.cs", "public class Use { public int Go() => new Common.Tool().Run(); }"));

        var tool = Assert.Single(result.Nodes, node => node.Name == "Tool");
        Assert.Equal("csharp:Common.Tool", tool.Identifier);
        Assert.Contains(result.Edges, edge => edge.TargetId == tool.Id + ".Run()" && edge.Kind == "CALLS");
    }

    [Fact]
    public void ANestedProjectsFilesAreNotAlsoFactsOfTheParent()
    {
        var result = Analyze(
            ("Parent.csproj", Project()),
            ("Child/Child.csproj", Project()),
            ("Child/C.cs", "namespace Child; public class C { }"));

        Assert.Equal("csharp:Child.C", Assert.Single(result.Nodes, node => node.Name == "C").Identifier);
    }

    [Fact]
    public void ADeclaredLanguageVersionReachesTheParser()
    {
        var result = Analyze(
            ("Old/Old.csproj", Project(properties: "<LangVersion>12.0</LangVersion>")),
            ("Old/Code.cs", "public class Old { public int field; public int Get { get { return field; } } }"),
            ("New/New.csproj", Project(properties: "<LangVersion>14.0</LangVersion>")),
            ("New/Code.cs", "public class New { public int field; public int Get { get { return field; } } }"));

        // In C# 14 `field` inside an accessor is the backing field, not the member.
        Assert.True(HasEdge(result, "Old.Get", "Old.field", "REFERENCES"));
        Assert.False(HasEdge(result, "New.Get", "New.field", "REFERENCES"));
    }

    [Fact]
    public void ACreatedFileJoinsTheProjectThatCompilesIt()
    {
        var existing = Write("App/A.cs", "public class A { public int Size() => 1; }");
        Write("App/App.csproj", Project());
        var analyzer = new CSharpWorkspaceAnalyzer("test", _root);
        analyzer.ReplaceFiles([existing], CancellationToken.None);
        analyzer.Analyze(CancellationToken.None);

        var created = Write("App/B.cs", "public class B { public int Go(A a) => a.Size(); }");
        analyzer.ApplyChanges([new FileChangeDto(created, FileChangeKinds.Created)], CancellationToken.None);

        Assert.True(HasEdge(analyzer.Analyze(CancellationToken.None), "B.Go(A)", "A.Size()", "CALLS"));
    }

    [Fact]
    public void ADeletedFileGoesFromEveryProjectCompilingIt()
    {
        var shared = Write("Shared/Common.cs", "public class Common { }");
        Write("Shared/Shared.csproj", Project());
        Write("App/App.csproj", Project("""<Compile Include="..\Shared\Common.cs" />"""));
        var use = Write("App/Use.cs", "public class Use { public Common Get() => null; }");
        var analyzer = new CSharpWorkspaceAnalyzer("test", _root);
        analyzer.ReplaceFiles([shared, use], CancellationToken.None);
        Assert.True(HasEdge(analyzer.Analyze(CancellationToken.None), "Use.Get()", "Common", "REFERENCES"));

        analyzer.ApplyChanges([new FileChangeDto(shared, FileChangeKinds.Deleted)], CancellationToken.None);
        var result = analyzer.Analyze(CancellationToken.None);

        Assert.False(HasNode(result, "Common"));
    }

    [Fact]
    public void AReferencedProjectsEditReachesItsDependents()
    {
        var item = Write("Core/Item.cs", "namespace Core; public class Item { public int Weigh() => 1; }");
        Write("Core/Core.csproj", Project());
        var use = Write("App/Use.cs", "public class Use { public int Go(Core.Item item) => item.Weigh(); }");
        Write("App/App.csproj", Project("""<ProjectReference Include="..\Core\Core.csproj" />"""));
        var analyzer = new CSharpWorkspaceAnalyzer("test", _root);
        analyzer.ReplaceFiles([item, use], CancellationToken.None);
        analyzer.Analyze(CancellationToken.None);

        File.WriteAllText(item, "namespace Core; public class Item { public int Weigh(int scale = 1) => scale; }");
        analyzer.ApplyChanges([new FileChangeDto(item, FileChangeKinds.Changed)], CancellationToken.None);

        Assert.True(HasEdge(
            analyzer.Analyze(CancellationToken.None), "Use.Go(Core.Item)", "Core.Item.Weigh(int)", "CALLS"));
    }

    /// <summary>A real assembly for a project to reference: its members are read, never run.</summary>
    private string CompileLibrary(string relative, string source)
    {
        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(relative),
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var emitted = compilation.Emit(path);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        return path;
    }

    private const string Engine = """
        namespace Engine
        {
            public class Node
            {
                public T GetNode<T>(string path) where T : new() => new T();
            }
        }
        """;

    private void RestoredPackage(string projectRelative, string assetsRelative)
    {
        var packages = Path.Combine(_root, "_packages");
        CompileLibrary("_packages/enginesharp/1.0.0/lib/net10.0/EngineSharp.dll", Engine);
        Write(assetsRelative, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["version"] = 3,
            ["targets"] = new Dictionary<string, object>
            {
                ["net10.0"] = new Dictionary<string, object>
                {
                    ["EngineSharp/1.0.0"] = new
                    {
                        type = "package",
                        compile = new Dictionary<string, object> { ["lib/net10.0/EngineSharp.dll"] = new { } },
                    },
                },
            },
            ["libraries"] = new Dictionary<string, object>
            {
                ["EngineSharp/1.0.0"] = new { type = "package", path = "enginesharp/1.0.0" },
            },
            ["packageFolders"] = new Dictionary<string, object> { [packages + Path.DirectorySeparatorChar] = new { } },
            ["project"] = new Dictionary<string, object>
            {
                ["restore"] = new { projectPath = Path.GetFullPath(Path.Combine(_root, projectRelative)) },
                ["frameworks"] = new Dictionary<string, object>
                {
                    ["net10.0"] = new
                    {
                        frameworkReferences = new Dictionary<string, object> { ["Microsoft.NETCore.App"] = new { } },
                    },
                },
            },
        }));
    }

    private const string UsesEngine = """
        namespace Game;
        public class Item { public int Weigh() => 1; }
        public class Player : Engine.Node
        {
            public int Run() => GetNode<Item>("x").Weigh();
        }
        """;

    [Fact]
    public void AValueOfAPackageTypeKeepsWhatIsReachedThroughIt()
    {
        RestoredPackage("game/Game.csproj", "game/.engine/obj/project.assets.json");

        var result = Analyze(("game/Game.csproj", Project()), ("game/Player.cs", UsesEngine));

        Assert.True(HasEdge(result, "Game.Player.Run()", "Game.Item.Weigh()", "CALLS"));
    }

    [Fact]
    public void WithoutRestoreDataThatUsageIsLostButTheProjectStillCompilesAlone()
    {
        var result = Analyze(("game/Game.csproj", Project()), ("game/Player.cs", UsesEngine));

        Assert.False(HasEdge(result, "Game.Player.Run()", "Game.Item.Weigh()", "CALLS"));
        Assert.True(HasNode(result, "Game.Player.Run()"));
    }

    [Fact]
    public void ALibraryIsNeverIndexed()
    {
        RestoredPackage("game/Game.csproj", "game/obj/project.assets.json");

        var result = Analyze(("game/Game.csproj", Project()), ("game/Player.cs", UsesEngine));

        Assert.DoesNotContain(result.Nodes, node => node.FilePath is { } path
            && path.Contains("_packages", StringComparison.Ordinal));
    }

    [Fact]
    public void AnOldStyleProjectBindsThroughItsHintPath()
    {
        CompileLibrary("Editor/Managed/EngineSharp.dll", Engine);
        var result = Analyze(
            ("Assembly-CSharp.csproj", """
                <Project ToolsVersion="4.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
                  <PropertyGroup>
                    <TargetFrameworkVersion>v4.7.1</TargetFrameworkVersion>
                    <DefineConstants>EDITOR</DefineConstants>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Assets\Player.cs" />
                    <Reference Include="EngineSharp"><HintPath>Editor\Managed\EngineSharp.dll</HintPath></Reference>
                  </ItemGroup>
                </Project>
                """),
            ("Assets/Player.cs", """
                namespace Game;
                public class Item { public int Weigh() => 1; }
                public class Player : Engine.Node
                {
                #if EDITOR
                    public int Run() => GetNode<Item>("x").Weigh();
                #endif
                }
                """));

        Assert.True(HasEdge(result, "Game.Player.Run()", "Game.Item.Weigh()", "CALLS"));
    }

    [Fact]
    public void AnUnreadableProjectFallsBackAndStillIndexesItsFiles()
    {
        var result = Analyze(
            ("App/App.csproj", "<Project><broken"),
            ("App/Code.cs", "public class Code { public void Run() { Other.Go(); } }"),
            ("App/Other.cs", "public static class Other { public static void Go() { } }"));

        Assert.Contains(result.Nodes, node => node.Id == "csharp:test:Code");
        Assert.True(HasEdge(result, "Code.Run()", "Other.Go()", "CALLS"));
    }

    private static ProjectCoverage Coverage(CSharpWorkspaceAnalyzer.AnalysisResult result, string name)
        => Assert.Single(result.Coverage, project => project.Name == name);

    [Fact]
    public void ACompleteProjectSaysSo()
    {
        var result = Analyze(("App/App.csproj", Project()), ("App/Code.cs", "public class Code { public string Run() => ToString(); }"));

        var coverage = Coverage(result, "App");
        Assert.Equal(ProjectCoverageStates.Complete, coverage.State);
        Assert.Equal("App/App.csproj", coverage.Path);
        Assert.Equal("net10.0", coverage.TargetFramework);
        Assert.Equal(1, coverage.Files);
        Assert.Equal(0, coverage.UnresolvedNames);
    }

    [Fact]
    public void AMissingPackageIsPartialAndWhatItCostsIsCounted()
    {
        var result = Analyze(
            ("App/App.csproj", Project("""<PackageReference Include="No.Such.Package" Version="1.0.0" />""")),
            ("App/Code.cs", "public class Code { public void Run(Widget widget, Widget other) => Tool.Go(widget); }"));

        var coverage = Coverage(result, "App");
        Assert.Equal(ProjectCoverageStates.Partial, coverage.State);
        Assert.Equal(["No.Such.Package"], coverage.MissingReferences);
        Assert.True(coverage.UnresolvedNames >= 3);
        Assert.Contains("Widget", coverage.CommonUnresolvedNames!);
        Assert.Contains(coverage.Notes!, note => note.Contains("No restore data", StringComparison.Ordinal));
    }

    [Fact]
    public void AnotherTargetIsReportedAsInactive()
    {
        var result = Analyze(
            ("Lib/Lib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFrameworks>net10.0;net48</TargetFrameworks></PropertyGroup>
                </Project>
                """),
            ("Lib/Code.cs", "public class Code { }"));

        var coverage = Coverage(result, "Lib");
        Assert.Equal("net10.0", coverage.TargetFramework);
        Assert.Equal(["net48"], coverage.InactiveTargetFrameworks);
    }

    [Fact]
    public void AWorkspaceWithoutProjectsSaysItIsAFallback()
    {
        var result = Analyze(("Loose.cs", "public class Loose { }"));

        var coverage = Coverage(result, "<workspace>");
        Assert.Equal(ProjectCoverageStates.Fallback, coverage.State);
        Assert.Equal(1, coverage.Files);
        Assert.Null(coverage.Path);
    }

    [Fact]
    public void AnUnreadableProjectIsNamedInTheFallback()
    {
        var result = Analyze(("App/App.csproj", "<Project><broken"), ("App/Code.cs", "public class Code { }"));

        var coverage = Coverage(result, "<workspace>");
        Assert.Contains(coverage.Notes!, note => note.Contains("App/App.csproj", StringComparison.Ordinal));
    }

    [Fact]
    public void ContextualKeywordsAreNotUnresolved()
    {
        var result = Analyze(("App/App.csproj", Project()), ("App/Code.cs", """
            public class Code<T> where T : unmanaged
            {
                public string Name(int value) => nameof(value);
                public void Named() => Take(count: 1);
                private void Take(int count) { var local = count; }
            }
            """));

        Assert.Equal(0, Coverage(result, "App").UnresolvedNames);
    }

    [Fact]
    public async Task ARestoreIsPickedUpWithNoSourceEdit()
    {
        Write("game/Game.csproj", Project());
        Write("game/Player.cs", UsesEngine);
        await using var pipeline = new CSharpWorkerPipeline(_root);
        await pipeline.GraphUpdateService.PerformInitialScanAsync(_root, null, CancellationToken.None);
        var edges = pipeline.RepositoryFactory.CreateEdgeRepository();
        Assert.DoesNotContain(await edges.GetAllAsync(), edge => edge.Type == "CALLS" && edge.TargetId!.EndsWith(":Game.Item.Weigh()", StringComparison.Ordinal));

        RestoredPackage("game/Game.csproj", "game/obj/project.assets.json");
        await pipeline.GraphUpdateService.ProcessFileChangeAsync(
            Path.Combine(_root, "game", "obj", "project.assets.json"), CodeContext.Core.Services.FileChangeType.Changed, CancellationToken.None);

        Assert.Contains(await edges.GetAllAsync(), edge => edge.Type == "CALLS" && edge.TargetId!.EndsWith(":Game.Item.Weigh()", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProjectThatAppearsClaimsItsFilesWithNoSourceEdit()
    {
        Write("App/Code.cs", "public class Code {\n#if DEBUG\npublic void InDebug() { }\n#endif\n}");
        await using var pipeline = new CSharpWorkerPipeline(_root);
        await pipeline.GraphUpdateService.PerformInitialScanAsync(_root, null, CancellationToken.None);
        var nodes = pipeline.RepositoryFactory.CreateNodeRepository();
        Assert.DoesNotContain(await nodes.GetAllAsync(), node => node.Name == "InDebug");

        var project = Write("App/App.csproj", Project());
        await pipeline.GraphUpdateService.ProcessFileChangeAsync(
            project, CodeContext.Core.Services.FileChangeType.Created, CancellationToken.None);

        Assert.Contains(await nodes.GetAllAsync(), node => node.Name == "InDebug");
    }

    [Fact]
    public async Task CoverageReachesTheHostWhenAGenerationCommits()
    {
        Write("App/App.csproj", Project());
        Write("App/Code.cs", "public class Code { }");
        var coverage = new CodeContext.Core.Workers.AnalysisCoverageRegistry();
        await using var pipeline = new CSharpWorkerPipeline(_root, coverage);

        await pipeline.GraphUpdateService.PerformInitialScanAsync(_root, null, CancellationToken.None);

        var project = Assert.Single(coverage.GetProjects());
        Assert.Equal("App", project.Name);
        Assert.Equal(ProjectCoverageStates.Complete, project.State);
    }
}
