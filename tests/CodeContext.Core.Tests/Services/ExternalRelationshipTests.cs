using CodeContext.Core;
using CodeContext.Core.Repositories;
using CodeContext.Core.Services;
using NSubstitute;
using Xunit;

namespace CodeContext.Core.Tests.Services
{
    /// <summary>
    /// A library symbol is a target for questions, not part of the project's own shape: it
    /// must not crowd what a method uses, join unrelated methods, or pose as a file.
    /// </summary>
    public class ExternalRelationshipTests
    {
        private readonly ICodeNodeRepository _nodes = Substitute.For<ICodeNodeRepository>();
        private readonly ICodeEdgeRepository _edges = Substitute.For<ICodeEdgeRepository>();
        private readonly ContextService _service;
        private readonly Dictionary<string, CodeNode> _byId = [];
        private readonly List<CodeEdge> _all = [];

        public ExternalRelationshipTests()
        {
            var files = Substitute.For<IFileMetadataRepository>();
            _service = new ContextService(_nodes, _edges, files);
            _nodes.GetByIdentifierAsync(Arg.Any<string>())
                .Returns(call => Task.FromResult(_byId.GetValueOrDefault(call.Arg<string>())));
            _nodes.GetByIdAsync(Arg.Any<string>())
                .Returns(call => Task.FromResult(_byId.GetValueOrDefault(call.Arg<string>())));
            _nodes.FindByNameAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool>())
                .Returns(new List<CodeNode>());
            _nodes.FindByFilePathAsync(Arg.Any<string>(), Arg.Any<string?>())
                .Returns(call => Task.FromResult<IReadOnlyList<CodeNode>>(
                    _byId.Values.Where(node => node.FilePath == (string)call[0]).ToList()));
            _edges.GetBySourceIdAsync(Arg.Any<string>(), Arg.Any<string?>())
                .Returns(call => Task.FromResult<IReadOnlyList<CodeEdge>>(
                    _all.Where(edge => edge.SourceId == (string)call[0]).ToList()));
            _edges.GetByTargetIdAsync(Arg.Any<string>(), Arg.Any<string?>())
                .Returns(call => Task.FromResult<IReadOnlyList<CodeEdge>>(
                    _all.Where(edge => edge.TargetId == (string)call[0]).ToList()));
        }

        private CodeNode Source(string id, string file, string type = "Method")
        {
            var node = new CodeNode { Id = id, Identifier = id, Name = id, Type = type, FilePath = file };
            _byId[id] = node;
            return node;
        }

        private CodeNode External(string id, string type = "Method")
        {
            var node = new CodeNode
            {
                Id = id,
                Identifier = id,
                Name = id,
                Type = type,
                FilePath = "external:Lib",
                Metadata = new Dictionary<string, string> { ["external"] = "true", ["assembly"] = "Lib" },
            };
            _byId[id] = node;
            return node;
        }

        private void Edge(string source, string target, string kind)
            => _all.Add(new CodeEdge { Id = $"{source}=[{kind}]=>{target}", SourceId = source, TargetId = target, Type = kind });

        [Fact]
        public async Task UsesLeavesOutLibrarySymbolsAndSaysHowMany()
        {
            Source("Run", "/repo/A.cs");
            Source("Helper", "/repo/B.cs");
            External("List.Add");
            External("Console.WriteLine");
            Edge("Run", "Helper", "CALLS");
            Edge("Run", "List.Add", "CALLS");
            Edge("Run", "Console.WriteLine", "CALLS");

            var relationships = (await _service.GetCompactContextAsync("Run")).Matches.Single().Relationships!;

            Assert.Equal(["Helper"], relationships.Uses!.Select(node => node.Identifier));
            Assert.Equal(1, relationships.UsesCount);
            Assert.Equal(2, relationships.UsesExternalHidden);
        }

        [Fact]
        public async Task OptingInListsThem()
        {
            Source("Run", "/repo/A.cs");
            External("List.Add");
            Edge("Run", "List.Add", "CALLS");

            var relationships = (await _service.GetCompactContextAsync("Run", includeExternal: true)).Matches.Single().Relationships!;

            Assert.Equal(["List.Add"], relationships.Uses!.Select(node => node.Identifier));
            Assert.Equal("Lib", relationships.Uses![0].External);
            Assert.Equal(0, relationships.UsesExternalHidden);
        }

        [Fact]
        public async Task ALibraryMethodDoesNotJoinWhatImplementsIt()
        {
            Source("A.Dispose", "/repo/A.cs");
            Source("B.Dispose", "/repo/B.cs");
            Source("CallsA", "/repo/C.cs");
            Source("CallsB", "/repo/C.cs");
            External("IDisposable.Dispose");
            Edge("A.Dispose", "IDisposable.Dispose", "IMPLEMENTS_MEMBER");
            Edge("B.Dispose", "IDisposable.Dispose", "IMPLEMENTS_MEMBER");
            Edge("CallsA", "A.Dispose", "CALLS");
            Edge("CallsB", "B.Dispose", "CALLS");

            var relationships = (await _service.GetCompactContextAsync("A.Dispose")).Matches.Single().Relationships!;

            Assert.Equal(["CallsA"], relationships.UsedBy!.Select(node => node.Identifier));
        }

        [Fact]
        public async Task AskedAboutALibraryMethodItsFamilyIsWhatImplementsIt()
        {
            Source("A.Dispose", "/repo/A.cs");
            Source("B.Dispose", "/repo/B.cs");
            Source("CallsA", "/repo/C.cs");
            Source("CallsLibrary", "/repo/D.cs");
            External("IDisposable.Dispose");
            Edge("A.Dispose", "IDisposable.Dispose", "IMPLEMENTS_MEMBER");
            Edge("B.Dispose", "IDisposable.Dispose", "IMPLEMENTS_MEMBER");
            Edge("CallsA", "A.Dispose", "CALLS");
            Edge("CallsLibrary", "IDisposable.Dispose", "CALLS");

            var relationships = (await _service.GetCompactContextAsync("IDisposable.Dispose")).Matches.Single().Relationships!;

            Assert.Equal(["CallsA", "CallsLibrary"], relationships.UsedBy!.Select(node => node.Identifier).Order());
        }

        [Fact]
        public async Task ALibrarySymbolIsNoFileDependency()
        {
            Source("Run", "/repo/A.cs");
            Source("Helper", "/repo/B.cs");
            External("List.Add");
            Edge("Run", "Helper", "CALLS");
            Edge("Run", "List.Add", "CALLS");

            var relationships = (await _service.GetCompactContextAsync("Run")).Matches.Single().Relationships!;

            Assert.Equal(1, relationships.FileDependenciesCount);
            Assert.DoesNotContain(relationships.FileDependencies!, file => file.Contains("external", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ALibrarySymbolHasNoFileRelationshipsOfItsOwn()
        {
            External("List.Add");
            External("List.Clear");
            Source("Run", "/repo/A.cs");
            Edge("Run", "List.Add", "CALLS");

            var relationships = (await _service.GetCompactContextAsync("List.Add", includeRelated: true)).Matches.Single().Relationships!;

            Assert.Empty(relationships.FileDependents ?? []);
            Assert.Empty(relationships.RelatedItems ?? []);
            Assert.Equal(["Run"], relationships.UsedBy!.Select(node => node.Identifier));
        }
    }
}
