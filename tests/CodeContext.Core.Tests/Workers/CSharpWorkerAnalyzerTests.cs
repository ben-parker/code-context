using CodeContext.CSharp.Worker;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Engine-level tests for the C# worker's Roslyn analysis — the direct successor of
/// the old in-process CSharpParser tests. Node/edge IDs are language-namespaced
/// (<c>csharp:</c>) per the parser protocol's ownership rule.
/// </summary>
public class CSharpWorkerAnalyzerTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("cc-csharp-analyzer-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static string Id(string display) => CSharpWorkspaceAnalyzer.IdPrefix + "test:" + display;

    private CSharpWorkspaceAnalyzer.AnalysisResult Analyze(params (string Name, string Content)[] files)
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var paths = new List<string>();
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(_tempDir, name);
            File.WriteAllText(path, content);
            paths.Add(path);
        }
        analyzer.ReplaceFiles(paths, CancellationToken.None);
        return analyzer.Analyze(CancellationToken.None);
    }

    private CSharpWorkspaceAnalyzer.AnalysisResult AnalyzeTestClassFile(out string filePath)
    {
        filePath = Path.GetFullPath("./TestFiles/TestClass.cs");
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        analyzer.ReplaceFiles([filePath], CancellationToken.None);
        return analyzer.Analyze(CancellationToken.None);
    }

    [Fact]
    public void Analyze_ShouldIdentifyClassAndCreateCorrectNode()
    {
        var result = AnalyzeTestClassFile(out var filePath);

        var node = result.Nodes.FirstOrDefault(n => n.Id == Id("CodeContext.Core.Tests.TestFiles.TestClass"));
        Assert.NotNull(node);
        Assert.Equal("TestClass", node.Name);
        Assert.Equal("Class", node.Kind);
        Assert.Equal("csharp", node.Language);
        Assert.Equal(filePath, node.FilePath);
        Assert.Equal("CodeContext.Core.Tests.TestFiles", node.Namespace);
        Assert.Equal("public", node.Visibility);
        Assert.Equal("TestClass: MyBaseClass, ITest", node.Signature);
    }

    [Fact]
    public void Analyze_ShouldIdentifyMethodAndCreateCorrectNode()
    {
        var result = AnalyzeTestClassFile(out var filePath);

        var methodNode = result.Nodes.FirstOrDefault(
            n => n.Id == Id("CodeContext.Core.Tests.TestFiles.TestClass.MyMethod()"));
        Assert.NotNull(methodNode);
        Assert.Equal("MyMethod", methodNode.Name);
        Assert.Equal("Method", methodNode.Kind);
        Assert.Equal(filePath, methodNode.FilePath);
        Assert.Equal("CodeContext.Core.Tests.TestFiles", methodNode.Namespace);
        Assert.Equal("public", methodNode.Visibility);
        Assert.Equal("MyMethod()", methodNode.Signature);
        Assert.Equal("csharp:CodeContext.Core.Tests.TestFiles.TestClass.MyMethod()", methodNode.Identifier);
    }

    [Fact]
    public void Analyze_EmitsUniqueIdentifiersAndMethodFamilyEdges()
    {
        var result = Analyze(("Family.cs", """
            public interface IBase<T> { void Work(T value); }
            public interface IDerived : IBase<int> { }
            public class First : IDerived { public void Work(int value) { } }
            public class Second : IDerived { void IBase<int>.Work(int value) { } }
            public class Base { public virtual void Run(int value) { } }
            public class Derived : Base { public override void Run(int value) { } }
            """));

        Assert.All(result.Nodes, node => Assert.False(string.IsNullOrWhiteSpace(node.Identifier)));
        Assert.Equal(result.Nodes.Count, result.Nodes.Select(node => node.Identifier).Distinct().Count());
        Assert.True(result.Edges.Count(edge => edge.Kind == "IMPLEMENTS_MEMBER") >= 2);
        Assert.Contains(result.Edges, edge => edge.Kind == "OVERRIDES_MEMBER");
    }

    [Fact]
    public void Analyze_ShouldCreateCallsEdgeBetweenMethods()
    {
        var result = AnalyzeTestClassFile(out _);

        var edge = result.Edges.FirstOrDefault(e => e.Kind == "CALLS");
        Assert.NotNull(edge);
        Assert.Equal(Id("CodeContext.Core.Tests.TestFiles.TestClass.AnotherMethod()"), edge.SourceId);
        Assert.Equal(Id("CodeContext.Core.Tests.TestFiles.TestClass.MyMethod()"), edge.TargetId);
        Assert.NotNull(edge.Metadata);
        Assert.True(edge.Metadata!.ContainsKey("line"));
    }

    [Fact]
    public void Analyze_ShouldIdentifyInterfaceAndCreateCorrectNode()
    {
        var result = AnalyzeTestClassFile(out _);

        var interfaceNode = result.Nodes.FirstOrDefault(n => n.Kind == "Interface");
        Assert.NotNull(interfaceNode);
        Assert.Equal(Id("CodeContext.Core.Tests.TestFiles.ITest"), interfaceNode.Id);
        Assert.Equal("ITest", interfaceNode.Name);
    }

    [Fact]
    public void Analyze_ShouldCreateImplementsEdgeBetweenClassAndInterface()
    {
        var result = AnalyzeTestClassFile(out _);

        var implementsEdge = result.Edges.FirstOrDefault(e => e.Kind == "IMPLEMENTS");
        Assert.NotNull(implementsEdge);
        Assert.Equal(Id("CodeContext.Core.Tests.TestFiles.TestClass"), implementsEdge.SourceId);
        Assert.Equal(Id("CodeContext.Core.Tests.TestFiles.ITest"), implementsEdge.TargetId);
    }

    [Fact]
    public void Analyze_ShouldCreateInheritsEdgeBetweenClasses()
    {
        var result = AnalyzeTestClassFile(out _);

        var inheritsEdge = result.Edges.FirstOrDefault(e => e.Kind == "INHERITS");
        Assert.NotNull(inheritsEdge);
        Assert.Equal(Id("CodeContext.Core.Tests.TestFiles.TestClass"), inheritsEdge.SourceId);
        Assert.Equal(Id("CodeContext.Core.Tests.TestFiles.MyBaseClass"), inheritsEdge.TargetId);
    }

    [Fact]
    public void Analyze_ShouldIdentifyPropertyAndCreateCorrectNode()
    {
        var result = AnalyzeTestClassFile(out _);

        var propertyNode = result.Nodes.FirstOrDefault(n => n.Kind == "Property");
        Assert.NotNull(propertyNode);
        Assert.Equal(Id("CodeContext.Core.Tests.TestFiles.TestClass.MyProperty"), propertyNode.Id);
        Assert.Equal("MyProperty", propertyNode.Name);
    }

    [Fact]
    public void Analyze_ShouldCreateImplementsEdgeWhenInterfaceIsInSameFile()
    {
        var result = Analyze(("CombinedTest.cs", @"
            namespace CodeContext.Core
            {
                public interface ILanguageParser
                {
                    string[] SupportedExtensions { get; }
                    object ParseFile(string filePath, string content);
                }

                public class TestParser : ILanguageParser
                {
                    public string[] SupportedExtensions => new[] { "".cs"" };
                    public object ParseFile(string filePath, string content) => new object();
                }
            }"));

        var testParserNode = result.Nodes.FirstOrDefault(n => n.Name == "TestParser" && n.Kind == "Class");
        var interfaceNode = result.Nodes.FirstOrDefault(n => n.Name == "ILanguageParser" && n.Kind == "Interface");
        Assert.NotNull(testParserNode);
        Assert.NotNull(interfaceNode);
        Assert.Contains(result.Edges, e =>
            e.Kind == "IMPLEMENTS" && e.SourceId == testParserNode.Id && e.TargetId == interfaceNode.Id);
    }

    [Fact]
    public void Analyze_ShouldCreateCrossFileImplementsEdge()
    {
        var result = Analyze(
            ("ILanguageParser.cs", @"
                namespace CodeContext.Core
                {
                    public interface ILanguageParser
                    {
                        string[] SupportedExtensions { get; }
                    }
                }"),
            ("MyParser.cs", @"
                namespace CodeContext.Core
                {
                    public class MyParser : ILanguageParser
                    {
                        public string[] SupportedExtensions => new[] { "".cs"" };
                    }
                }"));

        var parserNode = result.Nodes.FirstOrDefault(n => n.Name == "MyParser" && n.Kind == "Class");
        var interfaceNode = result.Nodes.FirstOrDefault(n => n.Name == "ILanguageParser" && n.Kind == "Interface");
        Assert.NotNull(parserNode);
        Assert.NotNull(interfaceNode);
        Assert.Contains(result.Edges, e =>
            e.Kind == "IMPLEMENTS" && e.SourceId == parserNode.Id && e.TargetId == interfaceNode.Id);
    }

    [Fact]
    public void Analyze_ShouldCreateCrossFileInheritsEdge()
    {
        var result = Analyze(
            ("BaseService.cs", @"
                namespace CodeContext.Core
                {
                    public class BaseService
                    {
                        protected virtual void DoWork() { }
                    }
                }"),
            ("UserService.cs", @"
                namespace CodeContext.Core
                {
                    public class UserService : BaseService
                    {
                        public void ProcessUser() { DoWork(); }
                    }
                }"));

        Assert.Contains(result.Edges, e =>
            e.Kind == "INHERITS"
            && e.SourceId == Id("CodeContext.Core.UserService")
            && e.TargetId == Id("CodeContext.Core.BaseService"));
    }

    [Fact]
    public void Analyze_ShouldCreateCrossFileCallsEdge()
    {
        var result = Analyze(
            ("Calculator.cs", @"
                namespace CodeContext.Core
                {
                    public class Calculator
                    {
                        public int Add(int a, int b) => a + b;
                    }
                }"),
            ("MathService.cs", @"
                namespace CodeContext.Core
                {
                    public class MathService
                    {
                        private Calculator _calculator = new Calculator();
                        public int Calculate(int x, int y) => _calculator.Add(x, y);
                    }
                }"));

        var calculateMethod = result.Nodes.FirstOrDefault(
            n => n.Name == "Calculate" && n.Kind == "Method" && n.Id.Contains("MathService"));
        var addMethod = result.Nodes.FirstOrDefault(
            n => n.Name == "Add" && n.Kind == "Method" && n.Id.Contains("Calculator"));
        Assert.NotNull(calculateMethod);
        Assert.NotNull(addMethod);
        Assert.Contains(result.Edges, e =>
            e.Kind == "CALLS" && e.SourceId == calculateMethod.Id && e.TargetId == addMethod.Id);
    }

    [Fact]
    public void Analyze_ShouldResolveMockAndFluentCallsToTheInnerProductionMethod()
    {
        var result = Analyze(("MockCalls.cs", @"
            using System;
            public sealed class FactAttribute : Attribute { }
            public interface IService { int Get(int value); }
            public static class MockExtensions
            {
                public static T Received<T>(this T value) => value;
                public static T DidNotReceive<T>(this T value) => value;
                public static T Returns<T>(this T value, T configured) => value;
            }
            public class ServiceTests
            {
                [Fact]
                public void Verify()
                {
                    IService service = null;
                    service.Received().Get(1);
                    service.DidNotReceive().Get(2);
                    service.Get(3).Returns(4);
                }
            }"));

        var target = Assert.Single(result.Nodes, node => node.Name == "Get" && node.Kind == "Method");
        var calls = result.Edges.Where(edge => edge.TargetId == target.Id).ToList();
        Assert.Equal(3, calls.Count);
        Assert.All(calls, edge => Assert.Equal("MOCK_CALLS", edge.Kind));
        Assert.Single(calls.Select(edge => edge.TargetId).Distinct());
    }

    [Fact]
    public void Analyze_ShouldCreateReferenceEdgesToConcreteTypes()
    {
        var result = Analyze(
            ("Model.cs", "namespace Example { public class Model { } }"),
            ("Consumers.cs", @"
                namespace Example
                {
                    public interface IStore { Model Load(); }
                    public class Consumer
                    {
                        private Model _model;
                        public Model Current => _model;
                        public void Set(Model model) { _model = new Model(); }
                    }
                }"));

        var modelId = Id("Example.Model");
        Assert.Contains(result.Edges, edge =>
            edge.Kind == "REFERENCES" && edge.SourceId == Id("Example.IStore.Load()") && edge.TargetId == modelId);
        Assert.Contains(result.Edges, edge =>
            edge.Kind == "REFERENCES" && edge.SourceId == Id("Example.Consumer") && edge.TargetId == modelId);
        Assert.Contains(result.Edges, edge =>
            edge.Kind == "REFERENCES" && edge.SourceId == Id("Example.Consumer.Current") && edge.TargetId == modelId);
        Assert.Contains(result.Edges, edge =>
            edge.Kind == "REFERENCES" && edge.SourceId == Id("Example.Consumer.Set(Example.Model)") && edge.TargetId == modelId);
    }

    [Fact]
    public void Analyze_ShouldMarkAttributedTestMethods()
    {
        var result = Analyze(("WidgetTests.cs", @"
            public class WidgetTests
            {
                [Fact]
                public void CreatesWidget() { }
                private object CreateService() => new object();
            }"));

        var test = Assert.Single(result.Nodes, node => node.Name == "CreatesWidget");
        Assert.Equal("true", test.Metadata?["isTest"]);
        var helper = Assert.Single(result.Nodes, node => node.Name == "CreateService");
        Assert.Null(helper.Metadata);
    }

    [Fact]
    public void Analyze_ShouldHandleMultipleFilesWithComplexRelationships()
    {
        var result = Analyze(
            ("IService.cs", @"
                namespace CodeContext.Core
                {
                    public interface IService { void Execute(); }
                }"),
            ("BaseService.cs", @"
                namespace CodeContext.Core
                {
                    public abstract class BaseService : IService
                    {
                        public abstract void Execute();
                        protected void Log(string message) { }
                    }
                }"),
            ("UserService.cs", @"
                namespace CodeContext.Core
                {
                    public class UserService : BaseService
                    {
                        public override void Execute() { Log(""Executing user service""); }
                    }
                }"));

        Assert.Contains(result.Nodes, n => n.Name == "IService" && n.Kind == "Interface");
        Assert.Contains(result.Nodes, n => n.Name == "BaseService" && n.Kind == "Class");
        Assert.Contains(result.Nodes, n => n.Name == "UserService" && n.Kind == "Class");
        Assert.Contains(result.Edges, e => e.Kind == "IMPLEMENTS");
        Assert.Contains(result.Edges, e => e.Kind == "INHERITS");
        Assert.Contains(result.Edges, e => e.Kind == "CALLS");
    }

    [Fact]
    public void ApplyChanges_DeletedFile_RemovesItsFactsFromTheNextAnalysis()
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var keep = Path.Combine(_tempDir, "Keep.cs");
        var drop = Path.Combine(_tempDir, "Drop.cs");
        File.WriteAllText(keep, "public class Keep { }");
        File.WriteAllText(drop, "public class Drop { }");
        analyzer.ReplaceFiles([keep, drop], CancellationToken.None);
        Assert.Equal(2, analyzer.Analyze(CancellationToken.None).Nodes.Count);

        File.Delete(drop);
        analyzer.ApplyChanges([new FileChangeDto(drop, FileChangeKinds.Deleted)], CancellationToken.None);

        var result = analyzer.Analyze(CancellationToken.None);
        var node = Assert.Single(result.Nodes);
        Assert.Equal("Keep", node.Name);
    }

    [Fact]
    public void SyncApprovedFiles_LoadsMissingAndDropsUnapproved()
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var a = Path.Combine(_tempDir, "A.cs");
        var b = Path.Combine(_tempDir, "B.cs");
        File.WriteAllText(a, "public class A { }");
        File.WriteAllText(b, "public class B { }");
        analyzer.ReplaceFiles([a], CancellationToken.None);

        // A restarted worker resyncs from the approved list: B appears, and when A
        // later leaves the approved set its facts disappear.
        analyzer.SyncApprovedFiles([a, b], CancellationToken.None);
        Assert.Equal(2, analyzer.Analyze(CancellationToken.None).Nodes.Count);

        analyzer.SyncApprovedFiles([b], CancellationToken.None);
        var node = Assert.Single(analyzer.Analyze(CancellationToken.None).Nodes);
        Assert.Equal("B", node.Name);
    }

    [Fact]
    public void Analyze_UnreadableFile_ReportsDiagnosticInsteadOfFailing()
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var missing = Path.Combine(_tempDir, "Missing.cs");
        analyzer.ReplaceFiles([missing], CancellationToken.None);

        var result = analyzer.Analyze(CancellationToken.None);
        Assert.Empty(result.Nodes);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(missing, diagnostic.FilePath);
        Assert.Equal("warning", diagnostic.Severity);
    }

    [Fact]
    public void NativeSyntaxTree_ReturnsBoundedRoslynNodesAndTokens()
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var file = Path.Combine(_tempDir, "Native.cs");
        File.WriteAllText(file, "public class Native { public int Value => 42; }");
        analyzer.ReplaceFiles([file], CancellationToken.None);

        var result = analyzer.GetNativeSyntaxTree(
            file, start: null, length: null, maxDepth: 1, CancellationToken.None);

        Assert.Equal("CompilationUnit", result.Tree.GetProperty("kind").GetString());
        Assert.True(result.Truncated);
        Assert.True(result.Tree.GetProperty("children").GetArrayLength() > 0);
    }
}
