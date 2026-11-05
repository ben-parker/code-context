using CodeContext.CSharp.Worker;
using CodeContext.CSharp.Worker.Projects;
using CodeContext.Parser.Protocol;
using Microsoft.CodeAnalysis.CSharp;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// A body-only edit re-walks one file, which is sound only if no other file could bind
/// differently. These pin, case by case, what Roslyn's top-level comparison actually ignores,
/// and show through the analyzer that what it keeps can change another file's facts.
/// </summary>
public class CSharpWorkerEditClassificationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cc-csharp-classify-").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static bool Reaches(string before, string after)
        => EditClassifier.CanAffectOtherFiles(CSharpSyntaxTree.ParseText(before), CSharpSyntaxTree.ParseText(after));

    [Theory]
    // A constant's value can pick an overload or a pattern elsewhere.
    [InlineData("class C { const int V = 1; }", "class C { const int V = 1000; }")]
    [InlineData("class C { public const string N = nameof(A); }", "class C { public const string N = nameof(B); }")]
    [InlineData("class C { const int V = A.X + 1; }", "class C { const int V = B.X + 1; }")]
    [InlineData("class C { const int A = 1, B = 2; }", "class C { const int A = 1, B = 3; }")]
    // Attribute arguments, wherever the attribute sits.
    [InlineData("[A(1)] class C { }", "[A(2)] class C { }")]
    [InlineData("[A(typeof(X))] class C { }", "[A(typeof(Y))] class C { }")]
    [InlineData("[A(nameof(X))] class C { }", "[A(nameof(Y))] class C { }")]
    [InlineData("class C { [A(1)] void M() { } }", "class C { [A(2)] void M() { } }")]
    [InlineData("class C { void M([A(1)] int p) { } }", "class C { void M([A(2)] int p) { } }")]
    [InlineData("class C { [return: A(1)] int M() => 0; }", "class C { [return: A(2)] int M() => 0; }")]
    [InlineData("class C { [A(1)] int f; }", "class C { [A(2)] int f; }")]
    [InlineData("class C { [A(1)] int P { get; } }", "class C { [A(2)] int P { get; } }")]
    [InlineData("class C { int P { [A(1)] get; } }", "class C { int P { [A(2)] get; } }")]
    [InlineData("[assembly: A(1)]", "[assembly: A(2)]")]
    // Default parameter values.
    [InlineData("class C { void M(int a = 1) { } }", "class C { void M(int a = 2) { } }")]
    [InlineData("class C { void M(string a = nameof(X)) { } }", "class C { void M(string a = nameof(Y)) { } }")]
    [InlineData("class C { int this[int a = 1] => 0; }", "class C { int this[int a = 2] => 0; }")]
    [InlineData("delegate void D(int a = 1);", "delegate void D(int a = 2);")]
    [InlineData("record R(int A = 1);", "record R(int A = 2);")]
    // Enum member values.
    [InlineData("enum E { A = 1 }", "enum E { A = 2 }")]
    [InlineData("enum E { A = 1, B = A + 1 }", "enum E { A = 1, B = A + 2 }")]
    // The shape of a member, with or without a body.
    [InlineData("class C { int M() => 1; }", "class C { long M() => 1; }")]
    [InlineData("class C { void M() { } }", "class C { void N() { } }")]
    [InlineData("class C { void M() { } }", "class C { public void M() { } }")]
    [InlineData("class C { void M() { } }", "class C { async void M() { } }")]
    [InlineData("class C { int P { get; } }", "class C { int P { get { return 1; } } }")]
    [InlineData("class C { int P { get; } }", "class C { int P { get; set; } }")]
    [InlineData("partial class C { partial void M(); }", "partial class C { partial void M() { } }")]
    [InlineData("class C : A { C() : base(1) { } }", "class C : A { C() : base(2) { } }")]
    [InlineData("class C : A { }", "class C : B { }")]
    [InlineData("class C<T> where T : A { }", "class C<T> where T : B { }")]
    [InlineData("class C { fixed int b[1]; }", "class C { fixed int b[2]; }")]
    // What changes the meaning of names in the file.
    [InlineData("using A; class C { }", "using B; class C { }")]
    [InlineData("global using A; class C { }", "global using B; class C { }")]
    [InlineData("using X = A; class C { }", "using X = B; class C { }")]
    [InlineData("namespace A { class C { } }", "namespace B { class C { } }")]
    [InlineData("#define X\n#if X\nclass C { }\n#endif", "#if X\nclass C { }\n#endif")]
    // Whether a file holds an entry point at all.
    [InlineData("class C { }", "M(); class C { }")]
    [InlineData("M(); class C { }", "class C { }")]
    // Top-level statements beside a declaration that changed.
    [InlineData("M(); class C { }", "N(); class D { }")]
    public void WhatADependentCanBindToIsTopLevel(string before, string after)
        => Assert.True(Reaches(before, after));

    [Theory]
    [InlineData("class C { void M() { A(); } }", "class C { void M() { B(); } }")]
    [InlineData("class C { void M() { } }", "class C { void M() { void L() { } } }")]
    [InlineData("class C { int M() => 1; }", "class C { int M() => A.Value; }")]
    [InlineData("class C { string M() => nameof(A); }", "class C { string M() => nameof(B); }")]
    [InlineData("class C { System.Type M() => typeof(A); }", "class C { System.Type M() => typeof(B); }")]
    [InlineData("class C { int P => 1; }", "class C { int P => 2; }")]
    [InlineData("class C { int P { get { return 1; } } }", "class C { int P { get { return 2; } } }")]
    [InlineData("class C { int this[int i] => 1; }", "class C { int this[int i] => 2; }")]
    [InlineData("class C { event System.Action E { add { A(); } remove { } } }", "class C { event System.Action E { add { B(); } remove { } } }")]
    [InlineData("class C { C() { A(); } }", "class C { C() { B(); } }")]
    [InlineData("class C { public static C operator +(C a, C b) => a; }", "class C { public static C operator +(C a, C b) => b; }")]
    [InlineData("class C { int f = 1; }", "class C { int f = 2; }")]
    [InlineData("class C { static readonly int f = A.X; }", "class C { static readonly int f = B.X; }")]
    [InlineData("class C { }", "class C { } // comment")]
    [InlineData("class C { }", "class  C  {  }")]
    [InlineData("#if NEVER\nclass A { }\n#endif\nclass C { }", "#if NEVER\nclass B { }\n#endif\nclass C { }")]
    // Top-level statements are private to their file.
    [InlineData("M();", "N();")]
    [InlineData("M(); class C { void X() { } }", "N(); class C { void X() { } }")]
    [InlineData("var a = 1; int L() => a;", "var a = 2; int L() => a + 1;")]
    public void WhatNoOtherFileCanSeeIsNot(string before, string after)
        => Assert.False(Reaches(before, after));

    private CSharpWorkspaceAnalyzer Workspace(out string edited, string consts, string user)
    {
        edited = Path.Combine(_root, "Consts.cs");
        File.WriteAllText(edited, consts);
        var use = Path.Combine(_root, "Use.cs");
        File.WriteAllText(use, user);
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        analyzer.ReplaceFiles([edited, use], CancellationToken.None);
        analyzer.Analyze(CancellationToken.None);
        return analyzer;
    }

    private static bool Calls(CSharpWorkspaceAnalyzer analyzer, string target)
        => analyzer.Analyze(CancellationToken.None).Edges.Any(edge => edge.Kind == "CALLS"
            && edge.SourceId == "csharp:test:Use.Run()" && edge.TargetId == "csharp:test:" + target);

    private void Edit(CSharpWorkspaceAnalyzer analyzer, string path, string text)
    {
        File.WriteAllText(path, text);
        analyzer.ApplyChanges([new FileChangeDto(path, FileChangeKinds.Changed)], CancellationToken.None);
    }

    [Fact]
    public void AConstantsValueChoosesAnOverloadInAnotherFile()
    {
        var analyzer = Workspace(
            out var consts,
            "public static class Consts { public const int V = 1; }",
            """
            public class Use
            {
                public void Run() => Take(Consts.V);
                public void Take(byte value) { }
                public void Take(long value) { }
            }
            """);
        Assert.True(Calls(analyzer, "Use.Take(byte)"));

        Edit(analyzer, consts, "public static class Consts { public const int V = 1000; }");

        Assert.True(Calls(analyzer, "Use.Take(long)"));
    }

    [Fact]
    public void AnEnumValueChoosesAnOverloadInAnotherFile()
    {
        var analyzer = Workspace(
            out var consts,
            "public enum E { A = 1 }",
            """
            public class Use
            {
                public const int Picked = (int)E.A;
                public void Run() => Take(Picked);
                public void Take(byte value) { }
                public void Take(long value) { }
            }
            """);
        Assert.True(Calls(analyzer, "Use.Take(byte)"));

        Edit(analyzer, consts, "public enum E { A = 1000 }");

        Assert.True(Calls(analyzer, "Use.Take(long)"));
    }

    [Fact]
    public void ABodyEditChangesOnlyItsOwnFacts()
    {
        var analyzer = Workspace(
            out var consts,
            "public class Consts { public int Value() => 1; public void Other() { } }",
            "public class Use { public int Run(Consts c) => c.Value(); }");
        analyzer.SeedFactHashes(analyzer.Analyze(CancellationToken.None));

        Edit(analyzer, consts, "public class Consts { public int Value() { Other(); return 2; } public void Other() { } }");
        var emission = analyzer.BuildIncrementalEmission(analyzer.Analyze(CancellationToken.None));

        Assert.Equal([consts], emission.ReplacesFiles);
        Assert.Contains(emission.Edges, edge => edge.TargetId == "csharp:test:Consts.Other()" && edge.Kind == "CALLS");
    }
}
