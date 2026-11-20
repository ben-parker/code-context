using CodeContext.CSharp.Worker;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// A call that binds to nothing is sent as a CALLS edge to the name it invokes, marked unresolved, so the host can
/// list it as a possible caller of every method of that name. A call that binds, to project code or a library, is
/// sent as before, and nothing that is not a call of a method name is sent.
/// </summary>
public sealed class CSharpWorkerPossibleCallTests : IDisposable
{
    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
        </Project>
        """;

    private const string Cases = """
        using System;
        using System.Collections.Generic;

        namespace Fixture
        {
            public class Cases
            {
                private readonly Func<int, int> _adder = value => value + 1;
                public int Inst() => 1;
                public int Over(int a) => a;
                public int Over(string a) => 0;

                public int Missing1() => Missing();
                public int MissingMember(Cases other) => other.Missing();
                public int? MissingConditional(Cases? other) => other?.Missing();
                public int MissingGeneric() => Missing<int>();
                public dynamic Dynamic(dynamic d) => d.Anything();
                public int NoOverloadFits() => Over(1, 2, 3);
                public int Twice() => Missing() + Missing();

                public int Bound() => Inst();
                public int Library(List<int> items) { items.Add(1); return items.Count; }
                public string NameOf() => nameof(Inst);
                public string NameOfMissing() => nameof(Nowhere);
                public int Delegate() => _adder(1, 2);
                public int Returned() => Make()();
                public static Func<int> Make() => () => 1;
            }
        }
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("cc-csharp-possible-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private CSharpWorkspaceAnalyzer.AnalysisResult Analyze()
    {
        Write("App/App.csproj", Project);
        var analyzer = new CSharpWorkspaceAnalyzer("test", _root);
        analyzer.ReplaceFiles([Write("App/Cases.cs", Cases)], CancellationToken.None);
        return analyzer.Analyze(CancellationToken.None);
    }

    /// <summary>The calling method's name, from an id such as <c>csharp:test:…:Fixture.Cases.MissingMember(Fixture.Cases)</c>.</summary>
    private static string Caller(ProtocolEdge edge)
    {
        var member = edge.SourceId[..edge.SourceId.IndexOf('(')];
        return member[(member.LastIndexOf('.') + 1)..];
    }

    [Fact]
    public void ACallThatBindsToNothing_IsSentAsAnUnresolvedCallOfItsName()
    {
        var unresolved = Analyze().Edges
            .Where(edge => edge.Metadata?.GetValueOrDefault(EdgeMetadataKeys.Unresolved) == "true")
            .ToList();

        Assert.All(unresolved, edge =>
        {
            Assert.Equal("CALLS", edge.Kind);
            Assert.True(edge.Metadata!.ContainsKey("line") && edge.Metadata.ContainsKey("column"));
            Assert.False(edge.Metadata.ContainsKey(EdgeMetadataKeys.External));
            Assert.StartsWith("csharp:test:", edge.Id, StringComparison.Ordinal);
        });
        Assert.Equal(
            [
                "Dynamic Anything", "Missing1 Missing", "MissingConditional Missing", "MissingGeneric Missing",
                "MissingMember Missing", "NoOverloadFits Over", "Twice Missing", "Twice Missing",
            ],
            unresolved.Select(edge => $"{Caller(edge)} {edge.TargetId}").Order(StringComparer.Ordinal));
        // Two calls on one line are two edges, told apart by their column.
        Assert.Equal(2, unresolved.Where(edge => Caller(edge) == "Twice").Select(edge => edge.Id).Distinct().Count());
    }

    [Fact]
    public void ACallThatBinds_OrNamesNoMethod_IsNotUnresolved()
    {
        var result = Analyze();

        var callers = result.Edges
            .Where(edge => edge.Metadata?.GetValueOrDefault(EdgeMetadataKeys.Unresolved) == "true")
            .Select(Caller)
            .ToHashSet();
        Assert.DoesNotContain("Bound", callers);
        Assert.DoesNotContain("Library", callers);
        Assert.DoesNotContain("NameOf", callers);
        Assert.DoesNotContain("NameOfMissing", callers);
        Assert.DoesNotContain("Delegate", callers);
        Assert.DoesNotContain("Returned", callers);
        Assert.Contains(result.Edges, edge => edge.Kind == "CALLS" && edge.SourceId.EndsWith("Cases.Bound()", StringComparison.Ordinal)
            && edge.TargetId.EndsWith("Cases.Inst()", StringComparison.Ordinal));
    }

    /// <summary>
    /// A file is not walked again unless something may change its facts. A declaration created elsewhere binds a call
    /// that bound to nothing, and deleting it unbinds the call again; each answer equals a fresh walk.
    /// </summary>
    [Fact]
    public void ACallBindsWhenItsTargetIsCreated_AndUnbindsWhenItIsDeleted()
    {
        Write("App/App.csproj", Project);
        var user = Write("App/User.cs", "namespace App; public class User { public int Run() => Later.Go(); }");
        var analyzer = new CSharpWorkspaceAnalyzer("test", _root);
        analyzer.ReplaceFiles([user], CancellationToken.None);
        string Call(CSharpWorkspaceAnalyzer.AnalysisResult result)
        {
            var edge = Assert.Single(result.Edges, edge => edge.SourceId.EndsWith("App.User.Run()", StringComparison.Ordinal) && edge.Kind == "CALLS");
            return edge.Metadata?.GetValueOrDefault(EdgeMetadataKeys.Unresolved) == "true" ? "unresolved " + edge.TargetId : edge.TargetId[(edge.TargetId.LastIndexOf(':') + 1)..];
        }
        Assert.Equal("unresolved Go", Call(analyzer.Analyze(CancellationToken.None)));

        var later = Write("App/Later.cs", "namespace App; public static class Later { public static int Go() => 1; }");
        analyzer.ApplyChanges([new FileChangeDto(later, FileChangeKinds.Created)], CancellationToken.None);
        Assert.Equal("App.Later.Go()", Call(analyzer.Analyze(CancellationToken.None)));

        File.Delete(later);
        analyzer.ApplyChanges([new FileChangeDto(later, FileChangeKinds.Deleted)], CancellationToken.None);
        Assert.Equal("unresolved Go", Call(analyzer.Analyze(CancellationToken.None)));
    }
}
