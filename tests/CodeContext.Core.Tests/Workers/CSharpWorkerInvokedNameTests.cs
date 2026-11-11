using System.Text.Json;
using CodeContext.Core.Services;
using CodeContext.CSharp.Worker;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Options;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Asking Roslyn about the name in <c>x.Foo()</c> builds the whole method group, and the walker
/// used to do that for every invoked name and then ignore the answer. It now skips the question
/// when the invocation has settled it. The claim is that no fact changes, so these tests hold
/// the old path beside the new one and compare everything they produce.
/// </summary>
public sealed class CSharpWorkerInvokedNameTests : IDisposable
{
    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework><AllowUnsafeBlocks>true</AllowUnsafeBlocks></PropertyGroup>
        </Project>
        """;

    private const string Extensions = """
        using System.Collections.Generic;
        namespace Fixture.Extensions
        {
            public interface IThing { void Run(); }
            public static class Ext
            {
                public static int Twice(this int value) => value * 2;
                public static T Echo<T>(this T value) => value;
                public static int Spin(this IThing thing) => 1;
                public static int Size<T>(this IEnumerable<T> items) => 0;
            }
        }
        namespace Fixture.Statics
        {
            public static class Util { public static int Ping() => 1; public static int Choose(int a) => a; public static int Choose(string b) => 0; }
        }
        """;

    /// <summary>Every way an invoked name can bind, one method each, named for what it shows.</summary>
    private const string Cases = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using Fixture.Extensions;
        using static Fixture.Statics.Util;

        namespace Fixture
        {
            public class NoteAttribute(string text) : Attribute { public string Text { get; } = text; }

            public class Parent
            {
                public virtual int Virtual() => 1;
                public int Shared() => 1;
                protected Func<int> Inherited = () => 1;
            }

            public unsafe class Cases : Parent, IThing
            {
                private Action? _field;
                private readonly Func<int, int> _adder = value => value + 1;
                private delegate*<int> _pointer;
                public event Action? Raised;
                public Func<int> Prop => () => 1;
                public static int Stat() => 1;
                public int Inst() => 1;
                public void Run() { }
                public override int Virtual() => 2;
                public T Gen<T>() => default!;
                public static Func<int> MakeFunc() => () => 1;
                public int Use(Func<int> f) => f();
                public int Over(int a) => a;
                public int Over(string a) => 0;

                public int InstanceCall() => Inst();
                public int StaticCall() => Stat();
                public int StaticViaType() => Cases.Stat();
                public int ThisCall() => this.Inst();
                public int BaseCall() => base.Virtual();
                public int InheritedCall() => Shared();
                public int ExplicitGeneric() => Gen<int>();
                public int ExtensionReduced() => 5.Twice();
                public string ExtensionGeneric() => "a".Echo();
                public int ExtensionOnInterface(IThing thing) => thing.Spin();
                public int ExtensionUnreduced() => Ext.Twice(5);
                public string ExtensionUnreducedGeneric() => Ext.Echo<string>("a");
                public int ExtensionFromFramework(List<int> items) => items.Size() + items.Count();
                public int StaticUsing() => Ping();
                public int StaticUsingOverload() => Choose(1);
                public int? ConditionalCall(Cases? other) => other?.Inst();
                public int? ConditionalChain(Cases? other) => other?.Inst().Twice();

                public int MethodGroupArgument() => Use(Inst);
                public int MethodGroupAssigned() { Func<int> f = Inst; return 0; }
                public int MethodGroupStatic() { Func<int> f = Cases.Stat; return f(); }
                public IEnumerable<int> MethodGroupInLinq(List<int> items) => items.Select(_adder);

                public void DelegateField() => _field();
                public int DelegateFieldWithArgument() => _adder(1);
                public int DelegateInherited() => Inherited();
                public int DelegateProperty() => Prop();
                public int DelegateParameter(Func<int> p) => p();
                public int DelegateLocal() { Func<int> local = Stat; return local(); }
                public void DelegateConditional() => _field?.Invoke();
                public void EventRaised() => Raised();
                public void EventRaisedConditional() => Raised?.Invoke();
                public int ReturnedAndInvoked() => MakeFunc()();
                public int FunctionPointer() => _pointer();

                public string NameOf() => nameof(Inst);
                public int LocalFunction() { int Local() => 1; return Local(); }
                public int StaticLocalFunction() { static int Local() => 1; return Local(); }

                public int FailedOverload() => Over(1, 2, 3);
                public int FailedDelegateArguments() => _adder(1, 2);
                public int Unresolved() => Missing();
                public int UnresolvedMember(Cases other) => other.Missing();
                public dynamic DynamicReceiver(dynamic d) => d.Anything();
                public int DynamicArgument(dynamic d) => Over(d);
                public dynamic DynamicExtensionless(dynamic d) => Stat() + d;

                [Note(Stat())] public int InAttributeArgument() => 0;
                [Note(nameof(Inst))] public int NameOfInAttribute() => 0;
            }
        }
        """;

    /// <summary>Which invoked names the invocation settles, written out so a change has to be chosen.</summary>
    private static readonly Dictionary<string, bool> Expected = new()
    {
        ["InstanceCall.Inst"] = true,
        ["StaticCall.Stat"] = true,
        ["StaticViaType.Stat"] = true,
        ["ThisCall.Inst"] = true,
        ["BaseCall.Virtual"] = true,
        ["InheritedCall.Shared"] = true,
        ["ExplicitGeneric.Gen"] = true,
        ["ExtensionReduced.Twice"] = true,
        ["ExtensionGeneric.Echo"] = true,
        ["ExtensionOnInterface.Spin"] = true,
        ["ExtensionUnreduced.Twice"] = true,
        ["ExtensionUnreducedGeneric.Echo"] = true,
        ["ExtensionFromFramework.Size"] = true,
        ["ExtensionFromFramework.Count"] = true,
        ["StaticUsing.Ping"] = true,
        ["StaticUsingOverload.Choose"] = true,
        ["ConditionalCall.Inst"] = true,
        ["ConditionalChain.Inst"] = true,
        ["ConditionalChain.Twice"] = true,
        ["MethodGroupArgument.Use"] = true,
        ["MethodGroupInLinq.Select"] = true,
        ["ReturnedAndInvoked.MakeFunc"] = true,
        ["InAttributeArgument.Stat"] = true,

        // A delegate: the name is the field, local or event, and that reference is a fact.
        ["MethodGroupStatic.f"] = false,
        ["DelegateField._field"] = false,
        ["DelegateFieldWithArgument._adder"] = false,
        ["DelegateInherited.Inherited"] = false,
        ["DelegateProperty.Prop"] = false,
        ["DelegateParameter.p"] = false,
        ["DelegateLocal.local"] = false,
        ["DelegateConditional.Invoke"] = false,
        ["EventRaised.Raised"] = false,
        ["EventRaisedConditional.Invoke"] = false,
        ["FunctionPointer._pointer"] = false,

        // Not a plain method: nothing to skip, or the old branch decides.
        ["NameOf.nameof"] = false,
        ["NameOfInAttribute.nameof"] = false,
        ["LocalFunction.Local"] = false,
        ["StaticLocalFunction.Local"] = false,

        // Nothing resolved: the name may still bind, or be counted as unresolved.
        ["FailedOverload.Over"] = false,
        ["FailedDelegateArguments._adder"] = false,
        ["Unresolved.Missing"] = false,
        ["UnresolvedMember.Missing"] = false,
        ["DynamicReceiver.Anything"] = false,
        ["DynamicArgument.Over"] = false,
        ["DynamicExtensionless.Stat"] = true,
    };

    private readonly string _root = Directory.CreateTempSubdirectory("cc-csharp-invoked-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private static CSharpWorkspaceAnalyzer.AnalysisResult Analyze(string? root, IReadOnlyList<string> files, bool askEveryInvokedName)
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test", root) { AskEveryInvokedName = askEveryInvokedName };
        analyzer.ReplaceFiles(files, CancellationToken.None);
        return analyzer.Analyze(CancellationToken.None);
    }

    /// <summary>Every fact as text, sorted, with what each project could not see.</summary>
    private static List<string> Facts(CSharpWorkspaceAnalyzer.AnalysisResult result)
    {
        var facts = new List<string>();
        facts.AddRange(result.Nodes.Select(node => "node " + JsonSerializer.Serialize(node, ParserProtocolJsonContext.Default.ProtocolNode)));
        facts.AddRange(result.Edges.Select(edge => "edge " + JsonSerializer.Serialize(edge, ParserProtocolJsonContext.Default.ProtocolEdge)));
        facts.AddRange(result.Coverage.Select(coverage =>
            $"coverage {coverage.Name} {coverage.State} {coverage.UnresolvedNames} {string.Join(",", coverage.CommonUnresolvedNames ?? [])}"));
        facts.Sort(StringComparer.Ordinal);
        return facts;
    }

    private static void AssertSameFacts(string? root, IReadOnlyList<string> files)
    {
        var expected = Facts(Analyze(root, files, askEveryInvokedName: true));
        var actual = Facts(Analyze(root, files, askEveryInvokedName: false));

        var lost = expected.Except(actual).Take(5).ToList();
        var gained = actual.Except(expected).Take(5).ToList();
        Assert.True(lost.Count == 0 && gained.Count == 0,
            $"Lost:{Environment.NewLine}{string.Join(Environment.NewLine, lost)}{Environment.NewLine}Gained:{Environment.NewLine}{string.Join(Environment.NewLine, gained)}");
        Assert.Equal(expected, actual);
    }

    private (string[] Files, string Root) WriteFixture()
    {
        Write("App/App.csproj", Project);
        return ([Write("App/Extensions.cs", Extensions), Write("App/Cases.cs", Cases)], _root);
    }

    [Fact]
    public void EveryWayANameCanBeInvokedKeepsItsFacts()
    {
        var (files, root) = WriteFixture();

        AssertSameFacts(root, files);
    }

    [Fact]
    public void EveryWayANameCanBeInvokedKeepsItsFactsWithoutAProject()
    {
        var (files, _) = WriteFixture();

        AssertSameFacts(null, files);
    }

    /// <summary>
    /// The comparison above proves nothing if the facts that could be lost are not there, so the
    /// ones a wrong skip would remove are named.
    /// </summary>
    [Fact]
    public void TheFixtureHoldsTheFactsAWrongSkipWouldLose()
    {
        var (files, root) = WriteFixture();

        var result = Analyze(root, files, askEveryInvokedName: false);

        var references = result.Edges.Where(edge => edge.Kind == "REFERENCES").Select(edge => (edge.SourceId, edge.TargetId)).ToList();
        bool Refers(string from, string to) => references.Exists(edge => edge.SourceId.EndsWith(from, StringComparison.Ordinal) && edge.TargetId.EndsWith(to, StringComparison.Ordinal));
        Assert.True(Refers("Cases.DelegateField()", "Cases._field"));
        Assert.True(Refers("Cases.DelegateProperty()", "Cases.Prop"));
        Assert.True(Refers("Cases.EventRaised()", "Cases.Raised"));
        Assert.True(Refers("Cases.FunctionPointer()", "Cases._pointer"));
        Assert.True(Refers("Cases.FailedDelegateArguments()", "Cases._adder"));
        Assert.True(Refers("Cases.MethodGroupArgument()", "Cases.Inst()"));
        Assert.True(Refers("Cases.MethodGroupAssigned()", "Cases.Inst()"));
        Assert.Contains("Missing", Assert.Single(result.Coverage, coverage => coverage.Name == "App").CommonUnresolvedNames ?? []);
    }

    [Fact]
    public void ThisRepositoryKeepsItsFacts()
    {
        var root = RepositoryRoot();
        // The files the host would approve, so the walk skips what the host skips: a
        // `.venv` holding a link this platform cannot open, for one.
        var src = Path.Combine(root, "src") + Path.DirectorySeparatorChar;
        var files = new RepositoryFileSelector(Options.Create(new CodeContextOptions { RootPath = root }))
            .EnumerateIncludedFiles([".cs"])
            .Where(file => file.StartsWith(src, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(files);

        AssertSameFacts(root, files);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodeContext.sln"))) return directory.FullName;
        }
        throw new InvalidOperationException("Not running under a checkout of this repository.");
    }

    [Fact]
    public void TheInvocationSettlesExactlyTheNamesItShould()
    {
        var trees = new[] { Extensions, Cases }.Select(text => CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Preview))).ToList();
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("Fixture", trees, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        var model = compilation.GetSemanticModel(trees[1]);

        var actual = new Dictionary<string, bool>();
        foreach (var name in trees[1].GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (CSharpWorkspaceAnalyzer.InvocationOf(name) is null) continue;
            if (name.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() is not { } method) continue;
            if (Expected.ContainsKey(method.Identifier.ValueText + "." + name.Identifier.ValueText) is false && !IsCase(method)) continue;
            actual[method.Identifier.ValueText + "." + name.Identifier.ValueText] = CSharpWorkspaceAnalyzer.InvocationSettlesName(model, name);
        }

        Assert.Equal(Expected.OrderBy(entry => entry.Key, StringComparer.Ordinal), actual.OrderBy(entry => entry.Key, StringComparer.Ordinal));
    }

    /// <summary>The members before the cases are scaffolding, and <c>Use</c> invokes its parameter.</summary>
    private static bool IsCase(MethodDeclarationSyntax method) => method.Identifier.ValueText is not "Use";
}
