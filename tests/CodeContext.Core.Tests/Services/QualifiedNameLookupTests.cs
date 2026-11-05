using CodeContext.Core;
using CodeContext.Core.Repositories;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using NSubstitute;
using Xunit;

namespace CodeContext.Core.Tests.Services
{
    /// <summary>
    /// <c>Type.Member</c> is how people and agents write a member from habit, so it has
    /// to resolve even though no node is literally named that.
    /// </summary>
    public class QualifiedNameLookupTests
    {
        private readonly ICodeNodeRepository _nodes = Substitute.For<ICodeNodeRepository>();
        private readonly ContextService _service;

        public QualifiedNameLookupTests()
        {
            var edges = Substitute.For<ICodeEdgeRepository>();
            var files = Substitute.For<IFileMetadataRepository>();
            var workers = Substitute.For<ILanguageWorkerService>();
            workers.OwnedExtensions.Returns([".cs", ".ts"]);
            _service = new ContextService(_nodes, edges, files, workerService: workers);
            _nodes.GetByIdentifierAsync(Arg.Any<string>()).Returns((CodeNode?)null);
            _nodes.FindByNameAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>())
                .Returns(new List<CodeNode>());
        }

        private void Stub(string name, params CodeNode[] nodes)
            => _nodes.FindByNameAsync(
                    Arg.Is<string>(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase)),
                    Arg.Any<string?>(), true)
                .Returns(nodes.ToList());

        private static CodeNode Member(string identifier, string name, string type = "Method")
            => new() { Id = identifier, Identifier = identifier, Name = name, Type = type };

        private static CodeNode External(string identifier, string name, string assembly = "Lib")
            => new()
            {
                Id = identifier,
                Identifier = identifier,
                Name = name,
                Type = "Method",
                FilePath = "external:Lib",
                Metadata = new Dictionary<string, string> { ["external"] = "true", ["assembly"] = assembly },
            };

        private ContextService WithCoverage(params ProjectCoverage[] projects)
        {
            var coverage = new AnalysisCoverageRegistry();
            coverage.Report("csharp", "default", projects);
            var workers = Substitute.For<ILanguageWorkerService>();
            workers.OwnedExtensions.Returns([".cs"]);
            return new ContextService(
                _nodes, Substitute.For<ICodeEdgeRepository>(), Substitute.For<IFileMetadataRepository>(),
                workerService: workers, coverage: coverage);
        }

        private static readonly ProjectCoverage Complete = new("A", "A/A.csproj", ProjectCoverageStates.Complete, 3);
        private static readonly ProjectCoverage Partial = new("B", "B/B.csproj", ProjectCoverageStates.Partial, 3);
        private static readonly ProjectCoverage Fallback = new("<workspace>", null, ProjectCoverageStates.Fallback, 1);

        [Fact]
        public async Task ANameNotFound_NamesTheProjectsThatWereNotFullySeen()
        {
            var response = await WithCoverage(Complete, Partial, Fallback).GetCompactContextAsync("Node3D");

            Assert.Equal(0, response.TotalMatches);
            Assert.Equal("fallback", response.Coverage!.State);
            Assert.Equal(2, response.Coverage.IncompleteProjects);
            Assert.Equal(3, response.Coverage.TotalProjects);
            Assert.Equal(["B", "<workspace>"], response.Coverage.Names);
        }

        [Fact]
        public async Task NoUsers_NamesThemToo()
        {
            Stub("Quiet", Member("csharp:App.Quiet", "Quiet"));

            var response = await WithCoverage(Partial).GetCompactContextAsync("Quiet");

            Assert.NotNull(response.Coverage);
        }

        [Fact]
        public async Task EverythingSeen_AddsNothing()
        {
            var response = await WithCoverage(Complete).GetCompactContextAsync("Node3D");

            Assert.Null(response.Coverage);
        }

        [Fact]
        public async Task LibrarySymbolsOfAName_GiveWayToProjectCodeAndAreCounted()
        {
            Stub("Add",
                Member("csharp:App.Cart.Add(int)", "Add"),
                External("csharp:List<T>.Add(T)", "Add"),
                External("csharp:HashSet<T>.Add(T)", "Add"));

            var response = await _service.GetCompactContextAsync("Add");

            Assert.Equal(1, response.TotalMatches);
            Assert.False(response.Ambiguous);
            Assert.Equal(2, response.ExternalMatchesHidden);
            Assert.Equal("csharp:App.Cart.Add(int)", response.Matches.Single().Target.Identifier);
        }

        [Fact]
        public async Task AskingForExternalSymbols_ListsThemAfterProjectCode()
        {
            Stub("Add",
                External("csharp:List<T>.Add(T)", "Add"),
                Member("csharp:App.Cart.Add(int)", "Add"));

            var response = await _service.GetCompactContextAsync("Add", includeExternal: true);

            Assert.Equal(2, response.TotalMatches);
            Assert.Equal(0, response.ExternalMatchesHidden);
            Assert.Equal("csharp:App.Cart.Add(int)", response.Matches[0].Target.Identifier);
        }

        [Fact]
        public async Task ALibrarySymbolAloneIsAnAnswer()
        {
            Stub("Node3D", External("csharp:Godot.Node3D", "Node3D", "GodotSharp"));

            var response = await _service.GetCompactContextAsync("Node3D");

            var target = response.Matches.Single().Target;
            Assert.Equal("GodotSharp", target.External);
            Assert.Null(target.File);
            Assert.Equal(0, target.Line);
            Assert.Equal(0, response.ExternalMatchesHidden);
        }

        [Fact]
        public async Task BareIdentifierWithSeveralOwners_ReturnsThemAllAsAmbiguous()
        {
            Stub("Main",
                Member("csharp:ToolA#Program.Main()", "Main"),
                Member("csharp:ToolB#Program.Main()", "Main"),
                Member("csharp:ToolB#Other.Main()", "Main"));

            var response = await _service.GetCompactContextAsync("csharp:Program.Main()");

            Assert.True(response.Ambiguous);
            Assert.Equal(2, response.TotalMatches);
            Assert.Equal(
                ["csharp:ToolA#Program.Main()", "csharp:ToolB#Program.Main()"],
                response.Matches.Select(match => match.Target.Identifier).Order());
        }

        [Fact]
        public async Task BareIdentifierOfAConstructorWithSeveralOwners_FindsTheConstructors()
        {
            Stub("constructor",
                Member("csharp:A#Thing.Thing()", "constructor"),
                Member("csharp:B#Thing.Thing()", "constructor"));

            var response = await _service.GetCompactContextAsync("csharp:Thing.Thing()");

            Assert.Equal(2, response.TotalMatches);
        }

        [Fact]
        public async Task QualifiedIdentifierOfAnotherScheme_IsNotAnOwner()
        {
            Stub("Main", Member("typescript:a.ts#Program.Main()", "Main"));

            var response = await _service.GetCompactContextAsync("csharp:Program.Main()");

            Assert.Equal(0, response.TotalMatches);
        }

        [Fact]
        public async Task DottedName_FindsEveryProjectsMember()
        {
            Stub("Main",
                Member("csharp:ToolA#Program.Main()", "Main"),
                Member("csharp:ToolB#Program.Main()", "Main"));

            var response = await _service.GetCompactContextAsync("Program.Main");

            Assert.Equal(2, response.TotalMatches);
        }

        [Theory]
        [InlineData("Wilderness.Generate")]
        [InlineData("wilderness.generate")]
        [InlineData("Rules.Wilderness.Generate")]
        [InlineData("App.Rules.Wilderness.Generate")]
        [InlineData("Wilderness.Generate()")]
        public async Task DottedName_MatchesMemberOfThatContainingType(string query)
        {
            Stub("Generate",
                Member("csharp:App.Rules.Wilderness.Generate(int, string)", "Generate"),
                Member("csharp:App.Rules.Town.Generate()", "Generate"));

            var response = await _service.GetCompactContextAsync(query, depth: 0);

            var match = Assert.Single(response.Matches);
            Assert.Equal("csharp:App.Rules.Wilderness.Generate(int, string)", match.Target.Identifier);
            Assert.Equal("exact", response.MatchMode);
        }

        [Fact]
        public async Task DottedName_MatchesAcrossGenericContainers()
        {
            Stub("Item", Member("csharp:App.Box<T>.Item", "Item", "Property"));

            var response = await _service.GetCompactContextAsync("Box.Item", depth: 0);

            Assert.Equal("csharp:App.Box<T>.Item", Assert.Single(response.Matches).Target.Identifier);
        }

        [Fact]
        public async Task DottedName_MatchesTypeScriptMembersAfterTheFileSeparator()
        {
            Stub("run", Member("typescript:ws:src/app.ts#Worker.run(a)", "run"));

            var response = await _service.GetCompactContextAsync("Worker.run", depth: 0);

            Assert.Single(response.Matches);
        }

        [Fact]
        public async Task DottedName_RequiresWholeSegments()
        {
            Stub("Generate", Member("csharp:App.OldWilderness.Generate()", "Generate"));

            var response = await _service.GetCompactContextAsync("Wilderness.Generate", depth: 0);

            Assert.Empty(response.Matches);
            Assert.Equal(0, response.TotalMatches);
        }

        [Fact]
        public async Task DottedName_KeepsEveryOverloadAsAmbiguous()
        {
            Stub("Run", Member("csharp:App.Tool.Run()", "Run"), Member("csharp:App.Tool.Run(int)", "Run"));

            var response = await _service.GetCompactContextAsync("Tool.Run", depth: 0);

            Assert.True(response.Ambiguous);
            Assert.Equal(2, response.TotalMatches);
        }

        [Fact]
        public async Task DottedName_AppliesAlsoUnderExact()
        {
            Stub("Generate", Member("csharp:App.Wilderness.Generate()", "Generate"));

            var response = await _service.GetCompactContextAsync("Wilderness.Generate", depth: 0, exact: true);

            Assert.Single(response.Matches);
        }

        [Fact]
        public async Task AMissingCanonicalIdentifier_IsNotReinterpretedAsADottedName()
        {
            Stub("Generate", Member("csharp:App.Wilderness.Generate()", "Generate"));

            var response = await _service.GetCompactContextAsync("csharp:Other.Wilderness.Generate()", depth: 0);

            Assert.Empty(response.Matches);
        }
    }
}
