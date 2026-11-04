using CodeContext.CSharp.Worker;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Pins the kinds of C# usage the worker records, so a query for a symbol can only come
/// back empty when nothing uses it. Each case here was a silent miss: the query succeeded
/// and reported no users of something the code plainly uses.
/// </summary>
public class CSharpWorkerSymbolCoverageTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("cc-csharp-coverage-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static string Id(string display) => CSharpWorkspaceAnalyzer.IdPrefix + "test:" + display;

    private CSharpWorkspaceAnalyzer.AnalysisResult Analyze(params (string Name, string Content)[] files)
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var paths = new List<string>();
        foreach (var (name, content) in files)
        {
            var path = Path.Combine(_tempDir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            paths.Add(path);
        }
        analyzer.ReplaceFiles(paths, CancellationToken.None);
        return analyzer.Analyze(CancellationToken.None);
    }

    private static void AssertEdge(
        CSharpWorkspaceAnalyzer.AnalysisResult result, string source, string target, string kind)
        => Assert.True(
            result.Edges.Any(edge => edge.SourceId == Id(source) && edge.TargetId == Id(target) && edge.Kind == kind),
            $"Expected {source} =[{kind}]=> {target}. Edges:{Environment.NewLine}" +
            string.Join(Environment.NewLine, result.Edges.Select(edge => edge.Id)));

    private static void AssertNode(
        CSharpWorkspaceAnalyzer.AnalysisResult result, string display, string kind)
    {
        var node = result.Nodes.SingleOrDefault(candidate => candidate.Id == Id(display));
        Assert.True(node is not null, $"Expected node {display}. Nodes:{Environment.NewLine}" +
            string.Join(Environment.NewLine, result.Nodes.Select(candidate => candidate.Id)));
        Assert.Equal(kind, node.Kind);
    }

    /// <summary>Every edge must start at a node, or traversal silently drops it.</summary>
    private static void AssertEdgeSourcesExist(CSharpWorkspaceAnalyzer.AnalysisResult result)
    {
        var nodeIds = result.Nodes.Select(node => node.Id).ToHashSet();
        var dangling = result.Edges.Where(edge => !nodeIds.Contains(edge.SourceId)).Select(edge => edge.Id).ToList();
        Assert.True(dangling.Count == 0, "Edges with no source node: " + string.Join(", ", dangling));
    }

    [Fact]
    public void RecordsStructsEnumsAndDelegates_AreIndexedAsSymbols()
    {
        var result = Analyze(("Types.cs", """
            namespace App;
            public record Chunk(int Width);
            public record class Named(string Name);
            public record struct Point(int X, int Y);
            public readonly struct Vec { public readonly int Length; }
            public enum Edges { North, South = 4 }
            public delegate int Scorer(int value);
            """));

        AssertNode(result, "App.Chunk", "Record");
        AssertNode(result, "App.Named", "Record");
        AssertNode(result, "App.Point", "Struct");
        AssertNode(result, "App.Vec", "Struct");
        AssertNode(result, "App.Edges", "Enum");
        AssertNode(result, "App.Edges.North", "EnumMember");
        AssertNode(result, "App.Edges.South", "EnumMember");
        AssertNode(result, "App.Scorer", "Delegate");
        AssertEdge(result, "App.Edges", "App.Edges.North", "HAS_FIELD");
        Assert.DoesNotContain(result.Edges, edge => edge.TargetId == Id("System.ValueType"));
        Assert.DoesNotContain(result.Edges, edge => edge.TargetId == Id("System.Enum"));
    }

    [Fact]
    public void ReferencesToRecordsStructsAndEnums_AreRecorded()
    {
        var result = Analyze(("Use.cs", """
            namespace App;
            public record Chunk(int Width);
            public struct Vec { }
            public enum Edges { North }
            public class Reader
            {
                public Chunk Make(Vec vec, Edges edge) => null!;
            }
            """));

        var make = "App.Reader.Make(App.Vec, App.Edges)";
        AssertEdge(result, make, "App.Chunk", "REFERENCES");
        AssertEdge(result, make, "App.Vec", "REFERENCES");
        AssertEdge(result, make, "App.Edges", "REFERENCES");
    }

    [Fact]
    public void RecordInheritance_IsRecordedAndMembersAttachToTheRecord()
    {
        var result = Analyze(("Records.cs", """
            namespace App;
            public record Base(int Id);
            public record Child(int Id, string Tag) : Base(Id)
            {
                public string Upper() => Tag;
            }
            """));

        AssertEdge(result, "App.Child", "App.Base", "INHERITS");
        AssertEdge(result, "App.Child", "App.Child.Upper()", "HAS_METHOD");
        AssertEdge(result, "App.Child", "App.Child.Tag", "HAS_PROPERTY");
    }

    [Fact]
    public void PositionalRecordParameters_AreIndexedAsProperties()
    {
        var result = Analyze(("Records.cs", """
            namespace App;
            public record Chunk(int Interior, int Declared)
            {
                public int Declared { get; init; } = Declared;
            }
            public class Reader
            {
                public int Read(Chunk chunk) => chunk.Interior;
            }
            """));

        AssertNode(result, "App.Chunk.Interior", "Property");
        AssertNode(result, "App.Chunk.Declared", "Property");
        AssertEdge(result, "App.Chunk", "App.Chunk.Interior", "HAS_PROPERTY");
        AssertEdge(result, "App.Reader.Read(App.Chunk)", "App.Chunk.Interior", "REFERENCES");
    }

    [Fact]
    public void PropertyFieldEventAndEnumMemberUses_EachProduceAnIncomingEdge()
    {
        var result = Analyze(("Use.cs", """
            namespace App;
            public enum Edges { North }
            public class Holder
            {
                public int Value { get; set; }
                public int Field;
                public event System.Action? Changed;
            }
            public class User
            {
                public int ReadProperty(Holder holder) => holder.Value;
                public void WriteProperty(Holder holder) { holder.Value = 2; }
                public void WriteField(Holder holder) { holder.Field = 1; }
                public void Subscribe(Holder holder) { holder.Changed += () => { }; }
                public Edges UseEnum() => Edges.North;
                public Holder Init() => new Holder { Value = 3 };
                public bool Match(Holder holder) => holder is { Field: 1 };
            }
            """));

        AssertNode(result, "App.Holder.Field", "Field");
        AssertNode(result, "App.Holder.Changed", "Event");
        AssertEdge(result, "App.User.ReadProperty(App.Holder)", "App.Holder.Value", "REFERENCES");
        AssertEdge(result, "App.User.WriteProperty(App.Holder)", "App.Holder.Value", "REFERENCES");
        AssertEdge(result, "App.User.WriteField(App.Holder)", "App.Holder.Field", "REFERENCES");
        AssertEdge(result, "App.User.Subscribe(App.Holder)", "App.Holder.Changed", "REFERENCES");
        AssertEdge(result, "App.User.UseEnum()", "App.Edges.North", "REFERENCES");
        AssertEdge(result, "App.User.Init()", "App.Holder.Value", "REFERENCES");
        AssertEdge(result, "App.User.Match(App.Holder)", "App.Holder.Field", "REFERENCES");
        AssertEdge(result, "App.Holder", "App.Holder.Field", "HAS_FIELD");
        AssertEdge(result, "App.Holder", "App.Holder.Changed", "HAS_FIELD");
        AssertEdgeSourcesExist(result);
    }

    [Fact]
    public void MembersOfConstructedGenerics_TargetTheDeclaration()
    {
        var result = Analyze(("Generic.cs", """
            namespace App;
            public class Box<T> { public T Item { get; set; } = default!; }
            public class User { public int Read(Box<int> box) => box.Item; }
            """));

        AssertEdge(result, "App.User.Read(App.Box<int>)", "App.Box<T>.Item", "REFERENCES");
    }

    [Fact]
    public void AnonymousTypeAndTupleMembers_AreNotReferences()
    {
        var result = Analyze(("Anon.cs", """
            namespace App;
            public class User
            {
                public int Anon() { var value = new { Width = 1 }; return value.Width; }
                public int Tuple() { (int Left, int Right) pair = (1, 2); return pair.Left; }
            }
            """));

        var nodeIds = result.Nodes.Select(node => node.Id).ToHashSet();
        Assert.DoesNotContain(result.Edges,
            edge => edge.Kind == "REFERENCES" && !nodeIds.Contains(edge.TargetId));
    }

    [Fact]
    public void LocalsAndParameters_AreNotReferences()
    {
        var result = Analyze(("Locals.cs", """
            namespace App;
            public class User { public int Add(int left) { var right = 1; return left + right; } }
            """));

        Assert.DoesNotContain(result.Edges, edge => edge.Kind == "REFERENCES");
    }

    [Fact]
    public void MethodGroups_ProduceAReferenceAndInvokedNamesDoNot()
    {
        var result = Analyze(("Groups.cs", """
            namespace App;
            public class User
            {
                public event System.Action? Changed;
                private void OnChanged() { }
                public void Wire() { Changed += OnChanged; }
                public void Direct() { OnChanged(); }
                public void Conditional(User? user) { user?.OnChanged(); }
            }
            """));

        AssertEdge(result, "App.User.Wire()", "App.User.OnChanged()", "REFERENCES");
        AssertEdge(result, "App.User.Direct()", "App.User.OnChanged()", "CALLS");
        Assert.DoesNotContain(result.Edges, edge => edge.SourceId == Id("App.User.Direct()")
            && edge.TargetId == Id("App.User.OnChanged()") && edge.Kind == "REFERENCES");
        Assert.DoesNotContain(result.Edges, edge => edge.SourceId == Id("App.User.Conditional(App.User?)")
            && edge.TargetId == Id("App.User.OnChanged()") && edge.Kind == "REFERENCES");
    }

    [Fact]
    public void AttributeUsage_ReferencesTheAttributeClass()
    {
        var result = Analyze(("Attr.cs", """
            namespace App;
            public class MarkerAttribute : System.Attribute { }
            public class User { [Marker] public void Tagged() { } }
            """));

        AssertEdge(result, "App.User.Tagged()", "App.MarkerAttribute", "REFERENCES");
    }

    [Fact]
    public void CallsInsideConstructorsAndInitializers_AreAttributedToThatMember()
    {
        var result = Analyze(("Members.cs", """
            namespace App;
            public class Holder
            {
                public static int Make() => 1;
                public int Init = Make();
                public int Auto { get; } = Make();
                public int Computed => Make();
                public int WithAccessor { get { return Make(); } }
                public int this[int index] => Make();
                public event System.Action Fired { add { Make(); } remove { } }
                public static Holder operator +(Holder left, Holder right) { Make(); return left; }
                public static implicit operator int(Holder value) => Make();
                public Holder() { Make(); }
                ~Holder() { Make(); }
            }
            public enum Flags { A = 1, B = A }
            """));

        var make = "App.Holder.Make()";
        AssertNode(result, "App.Holder.Holder()", "Method");
        AssertEdge(result, "App.Holder.Init", make, "CALLS");
        AssertEdge(result, "App.Holder.Auto", make, "CALLS");
        AssertEdge(result, "App.Holder.Computed", make, "CALLS");
        AssertEdge(result, "App.Holder.WithAccessor", make, "CALLS");
        AssertEdge(result, "App.Holder.this[int]", make, "CALLS");
        AssertEdge(result, "App.Holder.Fired", make, "CALLS");
        AssertEdge(result, "App.Holder.operator +(App.Holder, App.Holder)", make, "CALLS");
        AssertEdge(result, "App.Holder.implicit operator int(App.Holder)", make, "CALLS");
        AssertEdge(result, "App.Holder.Holder()", make, "CALLS");
        AssertEdge(result, "App.Holder.~Holder()", make, "CALLS");
        AssertEdge(result, "App.Flags.B", "App.Flags.A", "REFERENCES");
        AssertEdgeSourcesExist(result);
    }

    [Fact]
    public void ConstructorNodes_DoNotCollideWithTheTypeName()
    {
        var result = Analyze(("Ctor.cs", """
            namespace App;
            public class Holder { public Holder() { } }
            """));

        Assert.Single(result.Nodes, node => node.Name == "Holder");
        Assert.Single(result.Nodes, node => node.Name == "constructor");
    }

    [Fact]
    public void StaticAndInstanceConstructors_AreDistinctNodes()
    {
        var result = Analyze(("Ctor.cs", """
            namespace App;
            public class Holder
            {
                public static int Make() => 1;
                static Holder() { Make(); }
                public Holder() { }
            }
            """));

        var constructors = result.Nodes.Where(node => node.Name == "constructor").ToList();
        Assert.Equal(2, constructors.Select(node => node.Id).Distinct().Count());
        Assert.Equal(2, constructors.Select(node => node.Identifier).Distinct().Count());
        AssertEdgeSourcesExist(result);
    }

    [Fact]
    public void ObjectCreation_CallsTheDeclaredConstructor()
    {
        var result = Analyze(("Create.cs", """
            namespace App;
            public class Base { public Base(int seed) { } }
            public class Derived : Base
            {
                public Derived() : this(1) { }
                public Derived(int seed) : base(seed) { }
            }
            public class Plain { }
            public class User
            {
                public Derived Explicit() => new Derived(2);
                public Derived Implicit() => new(3);
                public Plain NoConstructor() => new();
            }
            """));

        AssertEdge(result, "App.User.Explicit()", "App.Derived.Derived(int)", "CALLS");
        AssertEdge(result, "App.User.Implicit()", "App.Derived.Derived(int)", "CALLS");
        AssertEdge(result, "App.Derived.Derived()", "App.Derived.Derived(int)", "CALLS");
        AssertEdge(result, "App.Derived.Derived(int)", "App.Base.Base(int)", "CALLS");
        AssertEdge(result, "App.User.NoConstructor()", "App.Plain", "CALLS");
        AssertEdgeSourcesExist(result);
    }

    [Fact]
    public void TargetTypedNew_CreditsTheTypeAsWellAsItsConstructor()
    {
        var result = Analyze(("Create.cs", """
            namespace App;
            public class Keyed { public Keyed(int seed) { } }
            public class User { public void Run() { Take(new(1)); } private void Take(object value) { } private Keyed Make() => new(2); }
            """));

        AssertEdge(result, "App.User.Make()", "App.Keyed.Keyed(int)", "CALLS");
        Assert.Equal(2, result.Edges.Count(edge => edge.TargetId == Id("App.Keyed")
            && edge.SourceId == Id("App.User.Make()")));
    }

    [Fact]
    public void PrimaryConstructorBaseCalls_AreRecorded()
    {
        var result = Analyze(("Primary.cs", """
            namespace App;
            public class Base { public Base(int seed) { } }
            public class Derived(int seed) : Base(seed);
            public record Parent(int Id);
            public record Child(int Id) : Parent(Id);
            """));

        AssertEdge(result, "App.Derived", "App.Base.Base(int)", "CALLS");
        AssertEdge(result, "App.Child", "App.Parent", "CALLS");
        AssertEdgeSourcesExist(result);
    }

    [Fact]
    public void IndexerUse_IsRecordedForReadsWritesInitializersAndConditionalAccess()
    {
        var result = Analyze(("Indexer.cs", """
            namespace App;
            public class Bag { public int this[int index] { get => 0; set { } } }
            public class User
            {
                public int Read(Bag bag) => bag[1];
                public void Write(Bag bag) { bag[1] = 2; }
                public Bag Init() => new Bag { [2] = 3 };
                public int? Conditional(Bag? bag) => bag?[1];
            }
            """));

        var indexer = "App.Bag.this[int]";
        AssertNode(result, indexer, "Property");
        AssertEdge(result, "App.User.Read(App.Bag)", indexer, "REFERENCES");
        AssertEdge(result, "App.User.Write(App.Bag)", indexer, "REFERENCES");
        AssertEdge(result, "App.User.Init()", indexer, "REFERENCES");
        AssertEdge(result, "App.User.Conditional(App.Bag?)", indexer, "REFERENCES");
    }

    [Fact]
    public void ConstructedGenericBasesAndInterfaces_TargetTheDeclaration()
    {
        var result = Analyze(("Generic.cs", """
            namespace App;
            public interface IStore<T> { }
            public interface IIntStore : IStore<int> { }
            public class Base<T> { }
            public class Derived : Base<int>, IStore<int> { }
            public record Rec : IStore<string>;
            """));

        AssertEdge(result, "App.Derived", "App.Base<T>", "INHERITS");
        AssertEdge(result, "App.Derived", "App.IStore<T>", "IMPLEMENTS");
        AssertEdge(result, "App.Rec", "App.IStore<T>", "IMPLEMENTS");
        AssertEdge(result, "App.IIntStore", "App.IStore<T>", "INHERITS");
    }

    [Fact]
    public void TopLevelStatements_AreAttributedToASyntheticMainPerFile()
    {
        var result = Analyze(
            ("tools/one/Program.cs", """
                using App;
                var width = Worker.Run();
                void Local() { Worker.Run(); }
                Local();
                """),
            ("Worker.cs", """
                namespace App;
                public static class Worker { public static int Run() => 7; }
                """));

        var main = Assert.Single(result.Nodes, node => node.Name == "<Main>");
        Assert.Equal("Method", main.Kind);
        Assert.Contains(result.Edges, edge => edge.SourceId == main.Id
            && edge.TargetId == Id("App.Worker.Run()") && edge.Kind == "CALLS");
        Assert.Contains(result.Edges, edge => edge.SourceId == main.Id
            && edge.TargetId == Id("App.Worker") && edge.Kind == "REFERENCES");
        AssertEdgeSourcesExist(result);
    }

    [Fact]
    public void TopLevelMains_InDifferentFilesHaveDistinctIdentities()
    {
        var result = Analyze(
            ("tools/one/Program.cs", "System.Console.WriteLine(1);"),
            ("tools/two/Program.cs", "System.Console.WriteLine(2);"));

        var mains = result.Nodes.Where(node => node.Name == "<Main>").ToList();
        Assert.Equal(2, mains.Count);
        Assert.Equal(2, mains.Select(node => node.Id).Distinct().Count());
        Assert.Equal(2, mains.Select(node => node.Identifier).Distinct().Count());
        Assert.All(mains, node => Assert.StartsWith(CSharpWorkspaceAnalyzer.IdPrefix + "test:", node.Id));
    }

    [Fact]
    public void TextThatIsNotCSharp_GetsNoSyntheticMain()
    {
        var result = Analyze(("Invalid.cs", "This is not valid C# code at all!"));

        Assert.Empty(result.Nodes);
        Assert.Empty(result.Edges);
    }

    [Fact]
    public void AProgramWithOneBrokenStatement_KeepsItsSyntheticMain()
    {
        var result = Analyze(("Program.cs", """
            System.Console.WriteLine(1);
            var broken = ;
            """));

        Assert.Single(result.Nodes, node => node.Name == "<Main>");
        AssertEdgeSourcesExist(result);
    }

    [Fact]
    public void CallsAndReadsInsideLinqLambdas_AreRecorded()
    {
        var result = Analyze(("Linq.cs", """
            using System.Collections.Generic;
            using System.Linq;
            namespace App;
            public class Item
            {
                public int Size { get; set; }
                public int Weigh() => 1;
            }
            public class User
            {
                public int Calls(List<Item> items) => items.Select(item => item.Weigh()).Sum();
                public int Reads(List<Item> items) => items.Where(item => item.Size > 0).Count();
            }
            """));

        AssertEdge(result, "App.User.Calls(System.Collections.Generic.List<App.Item>)", "App.Item.Weigh()", "CALLS");
        AssertEdge(result, "App.User.Reads(System.Collections.Generic.List<App.Item>)", "App.Item.Size", "REFERENCES");
    }

    [Fact]
    public void ChainedLinqAndQuerySyntax_KeepEveryStep()
    {
        var result = Analyze(("Linq.cs", """
            using System.Collections.Generic;
            using System.Linq;
            namespace App;
            public class Item
            {
                public bool Ok { get; set; }
                public int Size { get; set; }
                public int Weigh() => 1;
                public bool Check() => true;
            }
            public class User
            {
                public List<int> Chain(List<Item> items) => items.Where(i => i.Ok).Select(i => i.Weigh()).ToList();
                public Item Then(List<Item> items) => items.Where(i => i.Ok).First(i => i.Check());
                public IEnumerable<int> Query(List<Item> items) => from i in items where i.Ok select i.Size;
                public int Total(List<Item> items) => items.Sum(i => i.Size);
            }
            """));

        const string list = "System.Collections.Generic.List<App.Item>";
        AssertEdge(result, $"App.User.Chain({list})", "App.Item.Weigh()", "CALLS");
        AssertEdge(result, $"App.User.Then({list})", "App.Item.Check()", "CALLS");
        AssertEdge(result, $"App.User.Query({list})", "App.Item.Size", "REFERENCES");
        AssertEdge(result, $"App.User.Total({list})", "App.Item.Size", "REFERENCES");
    }

    private const string CodeWithoutUsings = """
        namespace App;
        public class Item { public int Size { get; set; } public int Weigh() => 1; }
        public class User
        {
            public int Calls(List<Item> items) => items.Select(i => i.Weigh()).Sum();
            public int Reads(List<Item> items) => items.Where(i => i.Size > 0).Count();
        }
        """;

    [Theory]
    [InlineData("src/App.csproj", "<Project><PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>")]
    [InlineData("src/App.csproj", "<Project><PropertyGroup><ImplicitUsings>true</ImplicitUsings></PropertyGroup></Project>")]
    [InlineData("Directory.Build.props", "<Project><PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>")]
    public void ProjectsWithImplicitUsings_BindWithoutUsingDirectives(string projectPath, string project)
    {
        var result = Analyze(("src/Code.cs", CodeWithoutUsings), (projectPath, project));

        const string list = "System.Collections.Generic.List<App.Item>";
        AssertEdge(result, $"App.User.Calls({list})", "App.Item.Weigh()", "CALLS");
        AssertEdge(result, $"App.User.Reads({list})", "App.Item.Size", "REFERENCES");
    }

    [Fact]
    public void ProjectsWithoutImplicitUsings_DoNotGetAmbiguousTypeNames()
    {
        var result = Analyze(
            ("src/App.csproj", "<Project><PropertyGroup><ImplicitUsings>disable</ImplicitUsings></PropertyGroup></Project>"),
            ("src/Model.cs", "namespace App.Model; public class Timer { }"),
            ("src/Use.cs", """
                using App.Model;
                namespace App;
                public class User { public Timer Make(Timer timer) => timer; }
                """));

        AssertEdge(result, "App.User.Make(App.Model.Timer)", "App.Model.Timer", "REFERENCES");
    }

    [Fact]
    public void ImplicitUsings_SurviveAFullReplaceAndContributeNoFacts()
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var code = Path.Combine(_tempDir, "Code.cs");
        File.WriteAllText(code, CodeWithoutUsings);
        File.WriteAllText(Path.Combine(_tempDir, "App.csproj"),
            "<Project><PropertyGroup><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");

        analyzer.ReplaceFiles([code], CancellationToken.None);
        analyzer.ReplaceFiles([code], CancellationToken.None);
        var result = analyzer.Analyze(CancellationToken.None);

        Assert.All(result.Nodes, node => Assert.Equal(code, node.FilePath));
        Assert.Contains(result.Edges, edge => edge.TargetId == Id("App.Item.Weigh()"));
    }

    [Fact]
    public void UsageThroughUnresolvedTypes_IsKeptWhenOneCandidateRemains()
    {
        var result = Analyze(("Engine.cs", """
            namespace App;
            public enum Edges { North }
            public class MarkAttribute : System.Attribute { public MarkAttribute(int value) { } }
            public class Keyed { public Keyed(Edges edge, int value) { } }
            public class User
            {
                private void OnTimeout() { }
                public void Wire(EngineTimer timer) { timer.Timeout += OnTimeout; }
                public void Pass() { Engine.Connect(OnTimeout); }
                public Keyed Create() => new Keyed(Edges.North, Unknown.Value);
                public Keyed Implicit() => new(Edges.North, Unknown.Value);
                [Mark(Unknown.Value)] public void Tagged() { }
            }
            """));

        AssertEdge(result, "App.User.Wire(EngineTimer)", "App.User.OnTimeout()", "REFERENCES");
        AssertEdge(result, "App.User.Pass()", "App.User.OnTimeout()", "REFERENCES");
        AssertEdge(result, "App.User.Create()", "App.Keyed.Keyed(App.Edges, int)", "CALLS");
        AssertEdge(result, "App.User.Implicit()", "App.Keyed", "REFERENCES");
        AssertEdge(result, "App.User.Tagged()", "App.MarkAttribute", "REFERENCES");
    }

    [Fact]
    public void AmbiguousOverloadGroups_AreNotGuessed()
    {
        var result = Analyze(("Group.cs", """
            namespace App;
            public class User
            {
                private void Handler() { }
                private void Handler(int value) { }
                public void Wire() { Engine.Connect(Handler); }
            }
            """));

        Assert.DoesNotContain(result.Edges, edge => edge.SourceId == Id("App.User.Wire()"));
    }

    [Fact]
    public void TypeArgumentsInBaseLists_AreReferencesButTheBaseItselfIsNot()
    {
        var result = Analyze(("Bases.cs", """
            namespace App;
            public class Msg { }
            public class Base<T> { }
            public interface IHandler<T> { }
            public class Derived : Base<Msg>, IHandler<Msg> { }
            """));

        AssertEdge(result, "App.Derived", "App.Msg", "REFERENCES");
        Assert.DoesNotContain(result.Edges, edge => edge.SourceId == Id("App.Derived")
            && edge.TargetId == Id("App.Base<T>") && edge.Kind == "REFERENCES");
    }

    [Fact]
    public void ExtensionMethodGroups_AreReferences()
    {
        var result = Analyze(("Ext.cs", """
            namespace App;
            public static class Extensions { public static int Twice(this string value) => 2; }
            public class User { public System.Func<int> Bind() { System.Func<int> f = "x".Twice; return f; } }
            """));

        AssertEdge(result, "App.User.Bind()", "App.Extensions.Twice(string)", "REFERENCES");
    }

    [Fact]
    public void ExtensionBlocks_AreRealSourcesForTheirEdges()
    {
        var result = Analyze(("Block.cs", """
            namespace App;
            public record Chunk(int Width);
            public static class ChunkExtensions
            {
                extension(Chunk chunk)
                {
                    public int Doubled() => chunk.Width * 2;
                }
            }
            """));

        Assert.Single(result.Nodes, node => node.Kind == "Extension");
        Assert.Contains(result.Edges, edge => edge.TargetId == Id("App.Chunk") && edge.Kind == "REFERENCES");
        AssertEdgeSourcesExist(result);
    }

    [Fact]
    public void LambdasPassedToCommonFrameworkLibraries_Bind()
    {
        var result = Analyze(("Lambdas.cs", """
            using System.Collections.Concurrent;
            using System.Collections.Immutable;
            using System.Linq;
            using System.Text.RegularExpressions;
            using System.Threading.Tasks;
            namespace App;
            public class Item { public int Weigh() => 1; }
            public class User
            {
                public void Concurrent(ConcurrentDictionary<int, Item> map) => map.GetOrAdd(1, key => { new Item().Weigh(); return null!; });
                public void Immutable(ImmutableArray<Item> items) => items.Select(item => item.Weigh()).ToList();
                public void Parallelized(Item[] items) => Parallel.ForEach(items, item => item.Weigh());
                public string Regexes(string text) => Regex.Replace(text, "a", match => new Item().Weigh().ToString());
            }
            """));

        var weigh = "App.Item.Weigh()";
        AssertEdge(result, "App.User.Concurrent(System.Collections.Concurrent.ConcurrentDictionary<int, App.Item>)", weigh, "CALLS");
        AssertEdge(result, "App.User.Immutable(System.Collections.Immutable.ImmutableArray<App.Item>)", weigh, "CALLS");
        AssertEdge(result, "App.User.Parallelized(App.Item[])", weigh, "CALLS");
        AssertEdge(result, "App.User.Regexes(string)", weigh, "CALLS");
    }

    [Fact]
    public void MemberUsageEdges_SurviveAnIncrementalEditOfTheDeclaringFile()
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test");
        var modelPath = Path.Combine(_tempDir, "Model.cs");
        var usePath = Path.Combine(_tempDir, "Use.cs");
        File.WriteAllText(modelPath, "namespace App; public record Chunk(int Width);");
        File.WriteAllText(usePath,
            "namespace App; public class User { public int Read(Chunk c) => c.Width; }");
        analyzer.ReplaceFiles([modelPath, usePath], CancellationToken.None);
        analyzer.SeedFactHashes(analyzer.Analyze(CancellationToken.None));

        File.WriteAllText(modelPath, "namespace App; public record Chunk(int Height);");
        analyzer.ApplyChanges(
            [new FileChangeDto(modelPath, FileChangeKinds.Changed, null)], CancellationToken.None);
        var emission = analyzer.BuildIncrementalEmission(analyzer.Analyze(CancellationToken.None));

        // The rename leaves Use.cs reading a member that no longer exists, so its stale
        // edge must be replaced along with the declaring file's facts.
        Assert.Contains(modelPath, emission.ReplacesFiles);
        Assert.Contains(usePath, emission.ReplacesFiles);
        Assert.DoesNotContain(emission.Edges, edge => edge.TargetId == Id("App.Chunk.Width"));
    }
}
