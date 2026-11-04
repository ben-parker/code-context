using CodeContext.Core;
using CodeContext.Core.Repositories;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
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
