using CodeContext.Core.Services;
using CodeContext.CSharp.Worker;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// A library symbol is known only through what the code does with it: a stub for each symbol
/// used, and never anything from inside the library.
/// </summary>
public class CSharpWorkerExternalSymbolTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cc-csharp-external-").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private const string ListId = "csharp:test:ext:System.Collections.Generic.List<T>";
    private const string AddId = ListId + ".Add(T)";

    private string Write(string relative, string content)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private CSharpWorkspaceAnalyzer.AnalysisResult Analyze(string source)
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        analyzer.ReplaceFiles([Write("Code.cs", source)], CancellationToken.None);
        return analyzer.Analyze(CancellationToken.None);
    }

    private static bool HasEdge(CSharpWorkspaceAnalyzer.AnalysisResult result, string source, string target, string kind)
        => result.Edges.Any(edge => edge.Kind == kind && edge.SourceId == source && edge.TargetId == target);

    [Fact]
    public void AUsedTypeAndMemberAreStubsWithAPathToEachOther()
    {
        var result = Analyze("""
            using System.Collections.Generic;
            public class User { public void Run(List<int> items) => items.Add(1); }
            """);

        var list = Assert.Single(result.Nodes, node => node.Id == ListId);
        Assert.Equal("List", list.Name);
        Assert.Equal("Class", list.Kind);
        Assert.Equal("csharp:System.Collections.Generic.List<T>", list.Identifier);
        Assert.Equal("true", list.Metadata!["external"]);
        Assert.False(string.IsNullOrEmpty(list.Metadata["assembly"]));
        Assert.Equal("external:System.Collections.Generic", list.FilePath);
        Assert.Equal(0, list.StartLine);

        var add = Assert.Single(result.Nodes, node => node.Id == AddId);
        Assert.Equal("Add", add.Name);
        Assert.Equal("Method", add.Kind);

        Assert.True(HasEdge(result, ListId, AddId, "HAS_METHOD"));
        Assert.True(HasEdge(result, "csharp:test:User.Run(System.Collections.Generic.List<int>)", AddId, "CALLS"));
        Assert.True(HasEdge(result, "csharp:test:User.Run(System.Collections.Generic.List<int>)", ListId, "REFERENCES"));
    }

    [Fact]
    public void AFrameworkInheritedThroughAProjectReferenceBindsToItsAssembly()
    {
        Write("Lib/Lib.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
            </Project>
            """);
        Write("App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../Lib/Lib.csproj" /></ItemGroup>
            </Project>
            """);
        var files = new[]
        {
            Write("Lib/Lib.cs", "namespace Lib; public class L { }"),
            Write("App/App.cs", "namespace App; public class Handler { public string Path(Microsoft.AspNetCore.Http.HttpContext context) => context.Request.Path; }"),
        };
        var analyzer = new CSharpWorkspaceAnalyzer("test", _root);
        analyzer.ReplaceFiles(files, CancellationToken.None);

        var result = analyzer.Analyze(CancellationToken.None);

        var context = Assert.Single(result.Nodes, node => node.Name == "HttpContext");
        Assert.Equal("Microsoft.AspNetCore.Http.Abstractions", context.Metadata!["assembly"]);
        Assert.Contains(result.Nodes, node => node.Name == "Request");
    }

    [Fact]
    public void WhatTheCodeNeverUsesHasNoStub()
    {
        var result = Analyze("""
            using System.Collections.Generic;
            public class User { public void Run(List<int> items) => items.Add(1); }
            """);

        Assert.DoesNotContain(result.Nodes, node => node.Name is "Remove" or "Clear" or "Dictionary");
    }

    [Fact]
    public void EveryKindOfUseReachesItsStub()
    {
        var result = Analyze("""
            using System;
            using System.Collections.Generic;
            public class Base : IDisposable
            {
                public void Dispose() { }
                public override string ToString() => DayOfWeek.Monday.ToString();
                public int Count(List<int> items) => items.Count;
                public void Listen(AppDomain domain) { domain.DomainUnload += OnUnload; }
                private void OnUnload(object sender, EventArgs e) { }
                public int Max() => int.MaxValue;
                public object Make() => new object();
                public object Build() => new Exception("x");
                [Obsolete] public void Old() { }
            }
            public class Mine : Exception { }
            """);

        static string Ext(string display) => "csharp:test:ext:" + display;
        Assert.True(HasEdge(result, "csharp:test:Base", Ext("System.IDisposable"), "IMPLEMENTS"));
        Assert.True(HasEdge(result, "csharp:test:Base.Dispose()", Ext("System.IDisposable.Dispose()"), "IMPLEMENTS_MEMBER"));
        Assert.True(HasEdge(result, "csharp:test:Base.ToString()", Ext("object.ToString()"), "OVERRIDES_MEMBER"));
        Assert.True(HasEdge(result, "csharp:test:Base.ToString()", Ext("System.DayOfWeek.Monday"), "REFERENCES"));
        Assert.True(HasEdge(result, "csharp:test:Base.Count(System.Collections.Generic.List<int>)", Ext("System.Collections.Generic.List<T>.Count"), "REFERENCES"));
        Assert.True(HasEdge(result, "csharp:test:Base.Listen(System.AppDomain)", Ext("System.AppDomain.DomainUnload"), "REFERENCES"));
        Assert.True(HasEdge(result, "csharp:test:Base.Max()", Ext("int.MaxValue"), "REFERENCES"));
        Assert.True(HasEdge(result, "csharp:test:Base.Build()", Ext("System.Exception"), "CALLS"));
        Assert.True(HasEdge(result, "csharp:test:Base.Old()", Ext("System.ObsoleteAttribute"), "REFERENCES"));
        Assert.True(HasEdge(result, "csharp:test:Mine", Ext("System.Exception"), "INHERITS"));

        Assert.Equal("Interface", Assert.Single(result.Nodes, node => node.Id == Ext("System.IDisposable")).Kind);
        Assert.Equal("Enum", Assert.Single(result.Nodes, node => node.Id == Ext("System.DayOfWeek")).Kind);
        Assert.Equal("EnumMember", Assert.Single(result.Nodes, node => node.Id == Ext("System.DayOfWeek.Monday")).Kind);
        Assert.Equal("Struct", Assert.Single(result.Nodes, node => node.Id == Ext("int")).Kind);
        Assert.Equal("Event", Assert.Single(result.Nodes, node => node.Id == Ext("System.AppDomain.DomainUnload")).Kind);
    }

    [Fact]
    public void VarNamesNoType()
    {
        var result = Analyze("""
            public class User { public int Run() { var x = 1; var y = new System.Random(); return x; } }
            """);

        Assert.DoesNotContain(result.Edges, edge => edge.TargetId.EndsWith(":int", StringComparison.Ordinal));
        Assert.Single(result.Edges, edge => edge.Kind == "REFERENCES" && edge.TargetId == "csharp:test:ext:System.Random");
    }

    [Fact]
    public void ASymbolThatFailedToBindIsNotAStub()
    {
        var result = Analyze("public class User : Missing.Base { public void Run(Unknown u) => u.Go(); }");

        Assert.DoesNotContain(result.Nodes, node => node.Metadata?.ContainsKey("external") == true);
    }

    private static string Project(string body = "") => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <ItemGroup>{body}</ItemGroup>
        </Project>
        """;

    [Fact]
    public void TwoProjectsUsingALibraryShareOneStub()
    {
        Write("A/A.csproj", Project());
        Write("B/B.csproj", Project());
        var a = Write("A/Code.cs", "public class InA { public string Get(System.Random r) => r.ToString(); }");
        var b = Write("B/Code.cs", "public class InB { public string Get(System.Random r) => r.ToString(); }");
        var analyzer = new CSharpWorkspaceAnalyzer("test", _root);
        analyzer.ReplaceFiles([a, b], CancellationToken.None);

        var result = analyzer.Analyze(CancellationToken.None);

        var random = Assert.Single(result.Nodes, node => node.Name == "Random");
        Assert.Equal(2, result.Edges.Count(edge => edge.TargetId == random.Id && edge.Kind == "REFERENCES"));
    }

    [Fact]
    public void AProjectTypeNamedLikeALibraryTypeQualifiesBoth()
    {
        Write("A/A.csproj", Project());
        var code = Write("A/Code.cs", """
            namespace System { public class Random { } }
            public class User { public int Get(System.IO.Stream stream) => stream.ReadByte(); }
            """);
        var other = Write("A/Other.cs", "public class Other { public object Get() => new System.Random(); }");
        Write("B/B.csproj", Project());
        var usesLibrary = Write("B/Code.cs", "public class Lib { public object Get() => new System.Random(); }");
        var analyzer = new CSharpWorkspaceAnalyzer("test", _root);
        analyzer.ReplaceFiles([code, other, usesLibrary], CancellationToken.None);

        var result = analyzer.Analyze(CancellationToken.None);

        var randoms = result.Nodes.Where(node => node.Name == "Random").ToList();
        Assert.Equal(2, randoms.Count);
        Assert.Contains(randoms, node => node.Identifier == "csharp:A#System.Random");
        Assert.Contains(randoms, node => node.Identifier.EndsWith("#System.Random", StringComparison.Ordinal)
            && node.Metadata?.ContainsKey("external") == true);
    }

    [Fact]
    public void AStubGoesWithItsLastUseAndAppearsWithItsFirst()
    {
        var first = Write("A.cs", "public class A { public object Get() => new System.Random(); }");
        var second = Write("B.cs", "public class B { public object Get() => new System.Random(); }");
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        analyzer.ReplaceFiles([first, second], CancellationToken.None);
        analyzer.SeedFactHashes(analyzer.Analyze(CancellationToken.None));

        File.WriteAllText(first, "public class A { public object Get() => null; }");
        analyzer.ApplyChanges([new FileChangeDto(first, FileChangeKinds.Changed)], CancellationToken.None);
        var stillUsed = analyzer.Analyze(CancellationToken.None);
        var emission = analyzer.BuildIncrementalEmission(stillUsed);
        analyzer.CommitIncremental(emission);
        Assert.Contains(stillUsed.Nodes, node => node.Name == "Random");
        Assert.DoesNotContain("external:System", emission.ReplacesFiles);

        File.WriteAllText(second, "public class B { public object Get() => null; }");
        analyzer.ApplyChanges([new FileChangeDto(second, FileChangeKinds.Changed)], CancellationToken.None);
        var gone = analyzer.Analyze(CancellationToken.None);
        emission = analyzer.BuildIncrementalEmission(gone);
        analyzer.CommitIncremental(emission);
        Assert.DoesNotContain(gone.Nodes, node => node.Name == "Random");
        Assert.Contains("external:System", emission.ReplacesFiles);

        File.WriteAllText(first, "public class A { public object Get() => new System.Random(); }");
        analyzer.ApplyChanges([new FileChangeDto(first, FileChangeKinds.Changed)], CancellationToken.None);
        emission = analyzer.BuildIncrementalEmission(analyzer.Analyze(CancellationToken.None));
        Assert.Contains("external:System", emission.ReplacesFiles);
        Assert.Contains(emission.Nodes, node => node.Name == "Random");
    }

    private ContextService LiveContext(CSharpWorkerPipeline pipeline) => new(
        pipeline.RepositoryFactory.CreateNodeRepository(),
        pipeline.RepositoryFactory.CreateEdgeRepository(),
        pipeline.FileMetadataRepository,
        Options.Create(new CodeContextOptions { RootPath = _root }));

    [Fact]
    public async Task ALibraryTypesUsersAreListedFromTheProjectAlone()
    {
        Write("Uses.cs", """
            using System.Text;
            public class Report
            {
                public string Render() { var builder = new StringBuilder(); builder.Append("x"); return builder.ToString(); }
                public void Other(StringBuilder builder) { }
            }
            """);
        await using var pipeline = new CSharpWorkerPipeline(_root);
        await pipeline.GraphUpdateService.PerformInitialScanAsync(_root, null, CancellationToken.None);
        var context = LiveContext(pipeline);

        var response = await context.GetCompactContextAsync("StringBuilder");

        var match = Assert.Single(response.Matches);
        Assert.Equal("Class", match.Target.Type);
        Assert.Null(match.Target.File);
        Assert.False(string.IsNullOrEmpty(match.Target.External));
        Assert.Equal(
            ["Other", "Render"],
            match.Relationships!.UsedBy!.Select(node => node.Name!).Order());
    }

    [Fact]
    public async Task AnEditKeepsEdgesIntoAStubTheDeltaReEmits()
    {
        var edited = Write("A.cs", "public class A { public object Get() => new System.Random(); }");
        Write("B.cs", "public class B { public object Get() => new System.Random(); }");
        await using var pipeline = new CSharpWorkerPipeline(_root);
        await pipeline.GraphUpdateService.PerformInitialScanAsync(_root, null, CancellationToken.None);

        File.WriteAllText(edited, "public class A { public object Get() => new System.Random(); public object Other() => new System.Text.StringBuilder(); }");
        await pipeline.GraphUpdateService.ProcessFileChangeAsync(edited, FileChangeType.Changed, CancellationToken.None);
        File.WriteAllText(edited, "public class A { public object Get() => new System.Random(); public object Other() => new System.Uri(\"x\"); }");
        await pipeline.GraphUpdateService.ProcessFileChangeAsync(edited, FileChangeType.Changed, CancellationToken.None);

        var usedBy = (await LiveContext(pipeline).GetCompactContextAsync("System.Random"))
            .Matches.Single().Relationships!.UsedBy!;

        Assert.Equal(["Get", "Get"], usedBy.Select(node => node.Name!));
    }
}
