using CodeContext.CSharp.Worker;
using CodeContext.Core.Repositories.InMemory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodeContext.Core.Tests.Services
{
    public class RelationshipDebuggingTests
    {
        [Fact]
        public async Task Debug_EdgeRepository_HasExpectedEdges()
        {
            // Arrange
            var repositoryFactory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
            await repositoryFactory.InitializeAsync("test");

            var nodeRepository = repositoryFactory.CreateNodeRepository();
            var edgeRepository = repositoryFactory.CreateEdgeRepository();

            // Analyze the test file with the worker's engine
            var analyzer = new CSharpWorkspaceAnalyzer("test");
            var filePath = Path.GetFullPath("./TestFiles/TestClass.cs");
            analyzer.ReplaceFiles([filePath], CancellationToken.None);
            var result = analyzer.Analyze(CancellationToken.None);

            // Store nodes and edges in repository (shape check only; the production
            // path goes through AnalysisDeltaApplier)
            foreach (var node in result.Nodes)
            {
                await nodeRepository.UpsertAsync(new CodeNode
                {
                    Id = node.Id,
                    Name = node.Name,
                    Type = node.Kind,
                    Language = node.Language,
                    FilePath = node.FilePath,
                });
            }
            foreach (var edge in result.Edges)
            {
                await edgeRepository.UpsertAsync(new CodeEdge
                {
                    Id = edge.Id,
                    SourceId = edge.SourceId,
                    TargetId = edge.TargetId,
                    Type = edge.Kind,
                });
            }

            // Act - Check if edges exist
            var allNodes = await nodeRepository.GetAllAsync();
            var testClassNode = allNodes.FirstOrDefault(n => n.Name == "TestClass");

            Assert.NotNull(testClassNode);
            Assert.NotNull(testClassNode.Id);

            var outgoingEdges = await edgeRepository.GetBySourceIdAsync(testClassNode.Id);
            Assert.NotNull(outgoingEdges);
            Assert.NotEmpty(outgoingEdges);

            // Target nodes referenced by the edges exist in the same result
            var myBaseClassNode = allNodes.FirstOrDefault(n => n.Name == "MyBaseClass");
            var iTestNode = allNodes.FirstOrDefault(n => n.Name == "ITest");
            Assert.NotNull(myBaseClassNode);
            Assert.NotNull(iTestNode);

            Assert.True(result.Edges.Count > 0, "Analyzer should create edges");
        }
    }
}
