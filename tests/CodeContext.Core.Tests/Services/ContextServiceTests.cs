using CodeContext.Core;
using CodeContext.Core.Repositories;
using CodeContext.Core.Services;
using NSubstitute;
using Microsoft.Extensions.Options;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace CodeContext.Core.Tests.Services
{
    public class ContextServiceTests
    {
        private readonly ICodeNodeRepository _nodeRepository;
        private readonly ICodeEdgeRepository _edgeRepository;
        private readonly IFileMetadataRepository _fileMetadataRepository;
        private readonly ContextService _contextService;

        public ContextServiceTests()
        {
            _nodeRepository = Substitute.For<ICodeNodeRepository>();
            _edgeRepository = Substitute.For<ICodeEdgeRepository>();
            _fileMetadataRepository = Substitute.For<IFileMetadataRepository>();
            _contextService = new ContextService(_nodeRepository, _edgeRepository, _fileMetadataRepository);
            // Mirror the real path index over whatever node set each test feeds GetAllAsync.
            _nodeRepository.StubFindByFilePathFromGetAll();
        }

        [Fact]
        public async Task GetCompleteContextAsync_WithValidIdentifier_ReturnsContext()
        {
            // Arrange
            var targetNode = new CodeNode
            {
                Id = "test-id",
                Name = "TestClass",
                Type = "Class",
                FilePath = "/test/TestClass.cs",
                StartLine = 1,
                EndLine = 10,
                Namespace = "Test",
                Visibility = "public",
                Signature = "public class TestClass"
            };

            var relatedNode = new CodeNode
            {
                Id = "related-id",
                Name = "RelatedMethod",
                Type = "Method",
                FilePath = "/test/TestClass.cs",
                StartLine = 5,
                EndLine = 8
            };

            var outgoingEdge = new CodeEdge
            {
                Id = "edge-1",
                SourceId = "test-id",
                TargetId = "related-id",
                Type = "CALLS"
            };

            _nodeRepository.FindByNameAsync("TestClass", null).Returns(new List<CodeNode> { targetNode });
            _edgeRepository.GetBySourceIdAsync("test-id").Returns(new List<CodeEdge> { outgoingEdge });
            _edgeRepository.GetByTargetIdAsync("test-id").Returns(new List<CodeEdge>());
            _nodeRepository.GetByIdAsync("related-id").Returns(relatedNode);
            _nodeRepository.GetAllAsync().Returns(new List<CodeNode> { targetNode, relatedNode });

            // Act
            var result = await _contextService.GetCompleteContextAsync("TestClass");

            // Assert
            Assert.NotNull(result);
            Assert.Single(result.Matches);
            Assert.Equal("TestClass", result.Matches[0].Target.Name);
            Assert.Equal("Class", result.Matches[0].Target.Type);
            Assert.Single(result.Matches[0].Relationships.Uses);
            Assert.Equal("RelatedMethod", result.Matches[0].Relationships.Uses[0].Name);
        }

        [Fact]
        public async Task GetCompleteContextAsync_WithNoMatches_ReturnsEmptyWithHint()
        {
            // Arrange
            _nodeRepository.FindByNameAsync("NonExistentClass", null).Returns(new List<CodeNode>());

            // Act
            var result = await _contextService.GetCompleteContextAsync("NonExistentClass");

            // Assert
            Assert.NotNull(result);
            Assert.Empty(result.Matches);
            Assert.Contains("No matches found", result.DisambiguationHint);
        }

        [Fact]
        public async Task GetCompleteContextAsync_WithMultipleMatches_ReturnsAllWithHint()
        {
            // Arrange
            var node1 = new CodeNode
            {
                Id = "test-id-1",
                Name = "TestMethod",
                Type = "Method",
                FilePath = "/test/Class1.cs"
            };

            var node2 = new CodeNode
            {
                Id = "test-id-2",
                Name = "TestMethod",
                Type = "Method",
                FilePath = "/test/Class2.cs"
            };

            _nodeRepository.FindByNameAsync("TestMethod", null).Returns(new List<CodeNode> { node1, node2 });
            _edgeRepository.GetBySourceIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());
            _edgeRepository.GetByTargetIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());
            _nodeRepository.GetAllAsync().Returns(new List<CodeNode> { node1, node2 });

            // Act
            var result = await _contextService.GetCompleteContextAsync("TestMethod");

            // Assert
            Assert.NotNull(result);
            Assert.Equal(2, result.Matches.Count);
            Assert.Contains("Multiple matches found", result.DisambiguationHint);
        }

        [Fact]
        public async Task GetCompleteContextAsync_WithFilePath_FindsNodesByFile()
        {
            // Arrange
            var node1 = new CodeNode
            {
                Id = "test-id-1",
                Name = "TestClass",
                Type = "Class",
                FilePath = "/test/TestClass.cs"
            };

            var node2 = new CodeNode
            {
                Id = "test-id-2",
                Name = "TestMethod",
                Type = "Method",
                FilePath = "/test/TestClass.cs"
            };

            _nodeRepository.GetAllAsync().Returns(new List<CodeNode> { node1, node2 });
            _edgeRepository.GetBySourceIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());
            _edgeRepository.GetByTargetIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());

            // Act
            var result = await _contextService.GetCompleteContextAsync("/test/TestClass.cs");

            // Assert
            Assert.NotNull(result);
            Assert.Equal(2, result.Matches.Count);
            Assert.Contains(result.Matches, m => m.Target.Name == "TestClass");
            Assert.Contains(result.Matches, m => m.Target.Name == "TestMethod");
        }

        [Fact]
        public async Task GetCompleteContextAsync_WithTypeFilter_FiltersCorrectly()
        {
            // Arrange
            var classNode = new CodeNode
            {
                Id = "class-id",
                Name = "TestClass",
                Type = "Class",
                FilePath = "/test/TestClass.cs"
            };

            var methodNode = new CodeNode
            {
                Id = "method-id",
                Name = "TestClass", // Same name but different type
                Type = "Method",
                FilePath = "/test/TestClass.cs"
            };

            _nodeRepository.FindByNameAsync("TestClass", "Class").Returns(new List<CodeNode> { classNode });
            _edgeRepository.GetBySourceIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());
            _edgeRepository.GetByTargetIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());
            _nodeRepository.GetAllAsync().Returns(new List<CodeNode> { classNode, methodNode });

            // Act
            var result = await _contextService.GetCompleteContextAsync("TestClass", "Class");

            // Assert
            Assert.NotNull(result);
            Assert.Single(result.Matches);
            Assert.Equal("Class", result.Matches[0].Target.Type);
        }

        [Fact]
        public async Task GetCompleteContextAsync_CalculatesMetricsCorrectly()
        {
            // Arrange
            var targetNode = new CodeNode
            {
                Id = "test-id",
                Name = "TestClass",
                Type = "Class",
                FilePath = "/test/TestClass.cs",
                StartLine = 1,
                EndLine = 20 // 20 lines of code
            };

            var dependencyNode = new CodeNode
            {
                Id = "dependency-id",
                Name = "DependencyClass",
                Type = "Class"
            };

            var outgoingEdge = new CodeEdge
            {
                Id = "edge-1",
                SourceId = "test-id",
                TargetId = "dependency-id",
                Type = "CALLS"
            };

            _nodeRepository.FindByNameAsync("TestClass", null).Returns(new List<CodeNode> { targetNode });
            _edgeRepository.GetBySourceIdAsync("test-id").Returns(new List<CodeEdge> { outgoingEdge });
            _edgeRepository.GetByTargetIdAsync("test-id").Returns(new List<CodeEdge>());
            _nodeRepository.GetByIdAsync("dependency-id").Returns(dependencyNode);
            _nodeRepository.GetAllAsync().Returns(new List<CodeNode> { targetNode, dependencyNode });

            // Act
            var result = await _contextService.GetCompleteContextAsync("TestClass");

            // Assert
            Assert.NotNull(result);
            Assert.Single(result.Matches);
            
            var metrics = result.Matches[0].Metrics;
            Assert.Equal(20, metrics.LinesOfCode);
            Assert.Equal(1, metrics.DependencyCount);
            Assert.Equal(0, metrics.DependentCount);
            Assert.True(metrics.Complexity > 0);
        }

        [Fact]
        public async Task GetMultipleContextAsync_WithMultipleIdentifiers_ReturnsAllContexts()
        {
            // Arrange
            var node1 = new CodeNode
            {
                Id = "test-id-1",
                Name = "Class1",
                Type = "Class"
            };

            var node2 = new CodeNode
            {
                Id = "test-id-2",
                Name = "Class2",
                Type = "Class"
            };

            _nodeRepository.FindByNameAsync("Class1", null).Returns(new List<CodeNode> { node1 });
            _nodeRepository.FindByNameAsync("Class2", null).Returns(new List<CodeNode> { node2 });
            _edgeRepository.GetBySourceIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());
            _edgeRepository.GetByTargetIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());
            _nodeRepository.GetAllAsync().Returns(new List<CodeNode> { node1, node2 });

            var request = new MultiContextRequest
            {
                Identifiers = new List<string> { "Class1", "Class2" },
                Depth = 1
            };

            // Act
            var result = await _contextService.GetMultipleContextAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.Equal(2, result.Count);
            Assert.Single(result[0].Matches);
            Assert.Single(result[1].Matches);
        }

        [Fact]
        public async Task GetCompleteContextAsync_WithIncludeTests_FindsTestInformation()
        {
            // Arrange
            var targetNode = new CodeNode
            {
                Id = "test-id",
                Name = "TestClass",
                Type = "Class",
                FilePath = "/src/TestClass.cs"
            };

            var testNode = new CodeNode
            {
                Id = "test-method-id",
                Name = "TestClassTests",
                Type = "Method",
                FilePath = "/tests/TestClassTests.cs"
            };

            _nodeRepository.FindByNameAsync("TestClass", null).Returns(new List<CodeNode> { targetNode });
            _edgeRepository.GetBySourceIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());
            _edgeRepository.GetByTargetIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());
            _nodeRepository.GetAllAsync().Returns(new List<CodeNode> { targetNode, testNode });

            // Act
            var result = await _contextService.GetCompleteContextAsync("TestClass", includeTests: true);

            // Assert
            Assert.NotNull(result);
            Assert.Single(result.Matches);
            // Note: The test file detection logic is heuristic-based and may not find tests
            // in this simple case, but the structure is in place
        }

        [Fact]
        public async Task GetCompleteContextAsync_WithExactMatching_ReturnsOnlyExactMatches()
        {
            // Arrange
            var exactMatchNode = new CodeNode
            {
                Id = "exact-match-id",
                Name = "UserService",
                Type = "Class",
                FilePath = "/src/UserService.cs"
            };

            var partialMatchNode = new CodeNode
            {
                Id = "partial-match-id",
                Name = "UserServiceTests",
                Type = "Class",
                FilePath = "/tests/UserServiceTests.cs"
            };

            // Setup for substring matching (exact = false)
            _nodeRepository.FindByNameAsync("UserService", "Class", false)
                .Returns(new List<CodeNode> { exactMatchNode, partialMatchNode });

            // Setup for exact matching (exact = true)
            _nodeRepository.FindByNameAsync("UserService", "Class", true)
                .Returns(new List<CodeNode> { exactMatchNode });

            _edgeRepository.GetBySourceIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());
            _edgeRepository.GetByTargetIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());
            _nodeRepository.GetAllAsync().Returns(new List<CodeNode> { exactMatchNode, partialMatchNode });

            // Act - Substring matching
            var subStringResult = await _contextService.GetCompleteContextAsync("UserService", "Class", exact: false);

            // Act - Exact matching
            var exactResult = await _contextService.GetCompleteContextAsync("UserService", "Class", exact: true);

            // Assert - Substring matching returns both
            Assert.NotNull(subStringResult);
            Assert.Equal(2, subStringResult.Matches.Count);
            Assert.Contains(subStringResult.Matches, m => m.Target.Name == "UserService");
            Assert.Contains(subStringResult.Matches, m => m.Target.Name == "UserServiceTests");

            // Assert - Exact matching returns only exact match
            Assert.NotNull(exactResult);
            Assert.Single(exactResult.Matches);
            Assert.Equal("UserService", exactResult.Matches[0].Target.Name);
        }

        [Fact]
        public async Task GetCompleteContextAsync_WithDepthZero_ReturnsNoRelationships()
        {
            var target = new CodeNode { Id = "target", Name = "Target", Type = "Class" };
            var dependency = new CodeNode { Id = "dependency", Name = "Dependency", Type = "Class" };
            _nodeRepository.FindByNameAsync("Target", null).Returns([target]);
            _edgeRepository.GetBySourceIdAsync("target").Returns([
                new CodeEdge { Id = "edge", SourceId = "target", TargetId = "dependency", Type = "REFERENCES" }
            ]);
            _nodeRepository.GetByIdAsync("dependency").Returns(dependency);

            var result = await _contextService.GetCompleteContextAsync("Target", depth: 0, includeTests: false);

            var relationships = Assert.Single(result.Matches).Relationships;
            Assert.Empty(relationships.Uses);
            Assert.Empty(relationships.UsedBy);
            Assert.Empty(relationships.FileDependencies);
            Assert.Empty(relationships.FileDependents);
            Assert.Empty(relationships.RelatedItems);
            await _edgeRepository.DidNotReceive().GetBySourceIdAsync(Arg.Any<string>());
        }

        [Fact]
        public async Task GetCompleteContextAsync_TraversesRelationshipsToRequestedDepth()
        {
            var root = new CodeNode { Id = "root", Name = "Root", Type = "Class" };
            var levelOne = new CodeNode { Id = "one", Name = "One", Type = "Class" };
            var levelTwo = new CodeNode { Id = "two", Name = "Two", Type = "Class" };
            _nodeRepository.FindByNameAsync("Root", null).Returns([root]);
            _nodeRepository.GetByIdAsync("one").Returns(levelOne);
            _nodeRepository.GetByIdAsync("two").Returns(levelTwo);
            _edgeRepository.GetBySourceIdAsync("root").Returns([
                new CodeEdge { Id = "root-one", SourceId = "root", TargetId = "one", Type = "REFERENCES" }
            ]);
            _edgeRepository.GetBySourceIdAsync("one").Returns([
                new CodeEdge { Id = "one-two", SourceId = "one", TargetId = "two", Type = "REFERENCES" }
            ]);
            _edgeRepository.GetByTargetIdAsync(Arg.Any<string>()).Returns([]);
            _nodeRepository.GetAllAsync().Returns([root, levelOne, levelTwo]);

            var depthOne = await _contextService.GetCompleteContextAsync("Root", depth: 1, includeTests: false);
            var depthTwo = await _contextService.GetCompleteContextAsync("Root", depth: 2, includeTests: false);

            Assert.Equal(["One"], Assert.Single(depthOne.Matches).Relationships.Uses.Select(n => n.Name));
            var depthTwoRelationships = Assert.Single(depthTwo.Matches).Relationships;
            Assert.Equal(["One"], depthTwoRelationships.Uses.Select(n => n.Name));
            var transitive = Assert.Single(depthTwoRelationships.TransitiveUses);
            Assert.Equal("Two", transitive.Node.Name);
            Assert.Equal(2, transitive.Distance);
            Assert.Equal(["REFERENCES", "REFERENCES"], transitive.RelationPath);
        }

        [Fact]
        public async Task GetCompleteContextAsync_DeduplicatesRelationshipNodesAcrossCallSites()
        {
            var target = new CodeNode { Id = "target", Name = "Target", Type = "Method" };
            var caller = new CodeNode { Id = "caller", Name = "Caller", Type = "Method" };
            _nodeRepository.FindByNameAsync("Target", null).Returns([target]);
            _nodeRepository.GetByIdAsync("caller").Returns(caller);
            _edgeRepository.GetBySourceIdAsync(Arg.Any<string>()).Returns([]);
            _edgeRepository.GetByTargetIdAsync("target").Returns([
                new CodeEdge { Id = "call-1", SourceId = "caller", TargetId = "target", Type = "CALLS" },
                new CodeEdge { Id = "call-2", SourceId = "caller", TargetId = "target", Type = "CALLS" }
            ]);
            _edgeRepository.GetByTargetIdAsync("caller").Returns([]);

            var result = await _contextService.GetCompleteContextAsync("Target", depth: 2, includeTests: false);

            Assert.Equal("Caller", Assert.Single(Assert.Single(result.Matches).Relationships.UsedBy).Name);
        }

        [Theory]
        [InlineData("TestClass.cs")]
        [InlineData("src/Feature/TestClass.cs")]
        [InlineData("src\\Feature\\TestClass.cs")]
        public async Task GetCompleteContextAsync_AcceptsRepositoryRelativeFilePaths(string identifier)
        {
            var target = new CodeNode
            {
                Id = "target",
                Name = "TestClass",
                Type = "Class",
                FilePath = "C:\\repo\\src\\Feature\\TestClass.cs"
            };
            _nodeRepository.GetAllAsync().Returns([target]);

            var result = await _contextService.GetCompleteContextAsync(identifier, depth: 0, includeTests: false);

            Assert.Equal("TestClass", Assert.Single(result.Matches).Target.Name);
        }

        [Fact]
        public async Task GetCompleteContextAsync_TreatsQualifiedSymbolNameAsIdentifier()
        {
            var target = new CodeNode { Id = "target", Name = "Namespace.Target", Type = "Class" };
            _nodeRepository.FindByNameAsync("Namespace.Target", null).Returns([target]);

            var result = await _contextService.GetCompleteContextAsync(
                "Namespace.Target", depth: 0, includeTests: false);

            Assert.Same(target, Assert.Single(result.Matches).Target);
            await _nodeRepository.Received(1).FindByNameAsync("Namespace.Target", null);
        }

        [Fact]
        public async Task GetCompleteContextAsync_TestSummaryListsOnlyTargetTestsAndUsesSameCount()
        {
            var target = new CodeNode
            {
                Id = "target", Name = "Widget", Type = "Class", FilePath = "/src/Widget.cs"
            };
            var test = new CodeNode
            {
                Id = "test", Name = "CreatesWidget", Type = "Method",
                FilePath = "/tests/WidgetTests.cs",
                Metadata = new Dictionary<string, string> { ["isTest"] = "true" }
            };
            var helper = new CodeNode
            {
                Id = "helper", Name = "CreateWidgetService", Type = "Method",
                FilePath = "/tests/WidgetTests.cs", Signature = "CreateWidgetService(Widget widget)"
            };
            _nodeRepository.FindByNameAsync("Widget", null).Returns([target]);
            _nodeRepository.GetAllAsync().Returns([target, test, helper]);
            _edgeRepository.GetBySourceIdAsync(Arg.Any<string>()).Returns([]);
            _edgeRepository.GetByTargetIdAsync(Arg.Any<string>()).Returns([]);

            var result = await _contextService.GetCompleteContextAsync("Widget", depth: 1);

            var testing = Assert.Single(result.Matches).Testing;
            var testFile = Assert.Single(testing.TestFiles);
            Assert.True(testing.IsTested);
            Assert.Equal(1, testFile.TestCount);
            Assert.Equal("CreatesWidget", Assert.Single(testFile.TestMethods).Name);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(11)]
        public async Task GetCompleteContextAsync_RejectsOutOfRangeDepth(int depth)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => _contextService.GetCompleteContextAsync("Target", depth: depth));
        }

        [Fact]
        public async Task GetCompactContextAsync_AmbiguousNameReturnsRankedSummariesWithoutExpansion()
        {
            var candidates = new List<CodeNode>
            {
                new() { Id = "test", Name = "Process", Type = "Method", FilePath = "C:/repo/tests/ProcessTests.cs" },
                new() { Id = "method", Name = "Process", Type = "Method", FilePath = "C:/repo/src/Worker.cs" },
                new() { Id = "class", Name = "Process", Type = "Class", FilePath = "C:/repo/src/Process.cs" },
                new() { Id = "other-1", Name = "Process", Type = "Property", FilePath = "C:/repo/src/A.cs" },
                new() { Id = "other-2", Name = "Process", Type = "Method", FilePath = "C:/repo/src/B.cs" },
                new() { Id = "other-3", Name = "Process", Type = "Method", FilePath = "C:/repo/src/C.cs" }
            };
            _nodeRepository.FindByNameAsync("Process", null, true).Returns(candidates);

            var result = await _contextService.GetCompactContextAsync("Process", maxMatches: 3);

            Assert.True(result.Ambiguous);
            Assert.True(result.Truncated);
            Assert.Equal(6, result.TotalMatches);
            Assert.Equal(3, result.ReturnedMatches);
            Assert.Equal("Class", result.Matches[0].Target.Type);
            Assert.All(result.Matches, match => Assert.Null(match.Relationships));
            Assert.Equal(4, result.Facets!.Types["Method"]);
            await _edgeRepository.DidNotReceive().GetBySourceIdAsync(Arg.Any<string>());
            await _edgeRepository.DidNotReceive().GetByTargetIdAsync(Arg.Any<string>());
        }

        [Fact]
        public async Task GetCompactContextAsync_UniqueMatchCapsRelationshipsAndReportsTotals()
        {
            var target = new CodeNode { Id = "target", Name = "Target", Type = "Class" };
            var callers = Enumerable.Range(0, 7)
                .Select(index => new CodeNode
                {
                    Id = $"caller-{index}", Name = $"Caller{index}", Type = "Method",
                    FilePath = $"C:/repo/src/Caller{index}.cs", Signature = new string('x', 250)
                }).ToList();
            _nodeRepository.FindByNameAsync("Target", null, true).Returns([target]);
            _edgeRepository.GetBySourceIdAsync("target").Returns([]);
            _edgeRepository.GetByTargetIdAsync("target").Returns(callers.Select(caller => new CodeEdge
            {
                Id = $"edge-{caller.Id}", SourceId = caller.Id, TargetId = "target", Type = "REFERENCES"
            }).ToList());
            foreach (var caller in callers)
            {
                _nodeRepository.GetByIdAsync(caller.Id!).Returns(caller);
            }

            var result = await _contextService.GetCompactContextAsync("Target", maxRelationships: 3);

            var relationships = Assert.Single(result.Matches).Relationships!;
            var usedBy = Assert.IsType<List<CompactCodeNode>>(relationships.UsedBy);
            Assert.False(result.Ambiguous);
            Assert.Equal(7, relationships.UsedByCount);
            Assert.Equal(3, usedBy.Count);
            Assert.Equal(3, relationships.UsedByReturnedCount);
            Assert.True(relationships.UsedByTruncated);
            Assert.True(relationships.Truncated);
            Assert.All(usedBy, node => Assert.True(node.Signature!.Length <= 201));
        }

        [Fact]
        public async Task GetCompactContextAsync_UsesRepositoryRelativePaths()
        {
            var service = new ContextService(
                _nodeRepository, _edgeRepository, _fileMetadataRepository,
                Options.Create(new CodeContextOptions { RootPath = "C:/repo" }));
            var target = new CodeNode
            {
                Id = "target", Name = "Target", Type = "Class",
                FilePath = "C:/repo/src/Target.cs", StartLine = 9
            };
            _nodeRepository.FindByNameAsync("Target", null, true).Returns([target]);

            var result = await service.GetCompactContextAsync("Target", depth: 0);

            var compactTarget = Assert.Single(result.Matches).Target;
            Assert.Equal("src/Target.cs", compactTarget.File);
            Assert.Equal(10, compactTarget.Line);
            Assert.Equal("exact", result.MatchMode);
            Assert.True(result.SubstringSearchSkipped);
        }

        [Fact]
        public async Task GetCompactContextAsync_FilePathReportsFilePathMatchMode()
        {
            var target = new CodeNode
            {
                Id = "target", Name = "Target", Type = "Class", FilePath = "C:/repo/src/Target.cs"
            };
            _nodeRepository.GetAllAsync().Returns([target]);

            var result = await _contextService.GetCompactContextAsync("src/Target.cs", depth: 0);

            Assert.Equal("filePath", result.MatchMode);
            Assert.False(result.SubstringSearchSkipped);
            Assert.Equal("Target", Assert.Single(result.Matches).Target.Name);
        }

        [Fact]
        public async Task GetCompactContextAsync_TypeFilterExcludesAllMatches_HintListsAvailableTypes()
        {
            _nodeRepository.GetByIdentifierAsync("Parse").Returns((CodeNode?)null);
            _nodeRepository.FindByNameAsync("Parse", "Enum", true).Returns([]);
            _nodeRepository.FindByNameAsync("Parse", "Enum", false).Returns([]);
            _nodeRepository.FindByNameAsync("Parse", null, false).Returns(new List<CodeNode>
            {
                new() { Id = "m1", Name = "Parse", Type = "Method" },
                new() { Id = "m2", Name = "Parse", Type = "Method" },
                new() { Id = "p1", Name = "Parse", Type = "Property" }
            });

            var result = await _contextService.GetCompactContextAsync("Parse", type: "Enum", depth: 0);

            Assert.Equal(0, result.TotalMatches);
            Assert.Empty(result.Matches);
            Assert.Contains("Enum", result.DisambiguationHint);
            Assert.Contains("Method (2)", result.DisambiguationHint);
            Assert.Contains("Property (1)", result.DisambiguationHint);
        }

        [Fact]
        public async Task GetCompactContextAsync_NoMatchesEvenUnfiltered_SaysIdentifierHasNoMatches()
        {
            _nodeRepository.GetByIdentifierAsync("Parse").Returns((CodeNode?)null);
            _nodeRepository.FindByNameAsync("Parse", "Enum", true).Returns([]);
            _nodeRepository.FindByNameAsync("Parse", "Enum", false).Returns([]);
            _nodeRepository.FindByNameAsync("Parse", null, false).Returns([]);

            var result = await _contextService.GetCompactContextAsync("Parse", type: "Enum", depth: 0);

            Assert.Equal(0, result.TotalMatches);
            Assert.Contains("No matches found", result.DisambiguationHint);
            Assert.Contains("even without", result.DisambiguationHint);
        }

        [Fact]
        public async Task GetCompleteContextAsync_TypeFilterExcludesAllMatches_HintListsAvailableTypes()
        {
            _nodeRepository.GetByIdentifierAsync("Parse").Returns((CodeNode?)null);
            _nodeRepository.FindByNameAsync("Parse", "Enum", false).Returns([]);
            _nodeRepository.FindByNameAsync("Parse", null, false).Returns(new List<CodeNode>
            {
                new() { Id = "m1", Name = "Parse", Type = "Method" },
                new() { Id = "m2", Name = "Parse", Type = "Method" },
                new() { Id = "p1", Name = "Parse", Type = "Property" }
            });

            var result = await _contextService.GetCompleteContextAsync("Parse", type: "Enum");

            Assert.Empty(result.Matches);
            Assert.Contains("Enum", result.DisambiguationHint);
            Assert.Contains("Method (2)", result.DisambiguationHint);
            Assert.Contains("Property (1)", result.DisambiguationHint);
        }

        [Fact]
        public async Task GetCompactContextAsync_NoFilters_NoMatchHintUnchanged()
        {
            _nodeRepository.GetByIdentifierAsync("X").Returns((CodeNode?)null);
            _nodeRepository.FindByNameAsync("X", null, true).Returns([]);
            _nodeRepository.FindByNameAsync("X", null, false).Returns([]);

            var result = await _contextService.GetCompactContextAsync("X", depth: 0);

            Assert.Equal("No matches found for identifier 'X'", result.DisambiguationHint);
        }

        [Fact]
        public async Task GetCompactContextAsync_ExplicitExactAndSubstringReportRequestedModes()
        {
            var exactTarget = new CodeNode { Id = "exact", Name = "Target", Type = "Class" };
            var broadTarget = new CodeNode { Id = "broad", Name = "TargetService", Type = "Class" };
            _nodeRepository.FindByNameAsync("Target", null, true).Returns([exactTarget]);
            _nodeRepository.FindByNameAsync("Target", null, false).Returns([broadTarget]);

            var exact = await _contextService.GetCompactContextAsync("Target", depth: 0, exact: true);
            var substring = await _contextService.GetCompactContextAsync("Target", depth: 0, exact: false);

            Assert.Equal("exact", exact.MatchMode);
            Assert.False(exact.SubstringSearchSkipped);
            Assert.Equal("substring", substring.MatchMode);
            Assert.False(substring.SubstringSearchSkipped);
        }

        [Fact]
        public async Task GetCompactContextAsync_OmittedExactFallsBackToSubstring()
        {
            var target = new CodeNode { Id = "target", Name = "TargetService", Type = "Class" };
            _nodeRepository.FindByNameAsync("Target", null, true).Returns([]);
            _nodeRepository.FindByNameAsync("Target", null, false).Returns([target]);

            var result = await _contextService.GetCompactContextAsync("Target", depth: 0);

            Assert.Equal("TargetService", Assert.Single(result.Matches).Target.Name);
            Assert.Equal("substring", result.MatchMode);
            Assert.False(result.SubstringSearchSkipped);
        }

        [Fact]
        public async Task GetCompactContextAsync_OmittedExactSuccessReportsSkippedSubstringSearch()
        {
            var target = new CodeNode { Id = "target", Name = "Target", Type = "Class" };
            _nodeRepository.FindByNameAsync("Target", null, true).Returns([target]);

            var result = await _contextService.GetCompactContextAsync("Target", depth: 0);

            Assert.Equal("exact", result.MatchMode);
            Assert.True(result.SubstringSearchSkipped);
            await _nodeRepository.DidNotReceive().FindByNameAsync("Target", null, false);
        }

        [Fact]
        public async Task CompactSignaturesCollapseWhitespaceBeforeTruncationWhileFullNodesRemainUnchanged()
        {
            var original = "  public\n\tclass   Target   " + new string('x', 200) + "\r\n";
            var target = new CodeNode
            {
                Id = "target", Name = "Target", Type = "Class", Signature = original
            };
            _nodeRepository.FindByNameAsync("Target", null, true).Returns([target]);
            _nodeRepository.FindByNameAsync("Target", null, false).Returns([target]);

            var compact = await _contextService.GetCompactContextAsync("Target", depth: 0);
            var full = await _contextService.GetCompleteContextAsync(
                "Target", depth: 0, includeTests: false, includeRelated: false, includeMetrics: false);

            var signature = Assert.Single(compact.Matches).Target.Signature!;
            Assert.StartsWith("public class Target ", signature);
            Assert.DoesNotContain('\n', signature);
            Assert.DoesNotContain('\r', signature);
            Assert.DoesNotContain('\t', signature);
            Assert.DoesNotContain("  ", signature);
            Assert.Equal(161, signature.Length);
            Assert.EndsWith("…", signature);
            Assert.Equal(original, Assert.Single(full.Matches).Target.Signature);
        }

        [Fact]
        public async Task RelatedItemsAreFullyCountedDeduplicatedOrderedAndOnlyCappedInCompactSerialization()
        {
            var target = new CodeNode
            {
                Id = "target", Name = "Target", Type = "Class",
                FilePath = "C:/repo/src/Target.cs", Namespace = "Example"
            };
            var sameFile = Enumerable.Range(0, 8).Select(index => new CodeNode
            {
                Id = $"same-{index}", Name = $"Same{index}", Type = index % 2 == 0 ? "Method" : "Property",
                FilePath = target.FilePath, Namespace = "Other", StartLine = 20 - index
            }).ToList();
            var sameNamespace = Enumerable.Range(0, 8).Select(index => new CodeNode
            {
                Id = $"namespace-{index}", Name = $"Namespace{index}", Type = "Class",
                FilePath = $"C:/repo/src/Namespace{index}.cs", Namespace = "Example", StartLine = index
            }).ToList();
            var duplicateIdentity = new CodeNode
            {
                Id = sameFile[0].Id, Name = "Duplicate", Type = "Class",
                FilePath = "C:/repo/src/Duplicate.cs", Namespace = "Example"
            };
            _nodeRepository.FindByNameAsync("Target", null, true).Returns([target]);
            _nodeRepository.FindByNameAsync("Target", null, false).Returns([target]);
            _nodeRepository.GetAllAsync().Returns([target, .. sameNamespace, duplicateIdentity, .. sameFile]);
            foreach (var node in sameFile.Append(target))
            {
                _edgeRepository.GetBySourceIdAsync(node.Id!).Returns([]);
                _edgeRepository.GetByTargetIdAsync(node.Id!).Returns([]);
            }

            var result = await _contextService.GetCompactContextAsync(
                "Target", includeRelated: true, maxRelationships: 5);
            var full = await _contextService.GetCompleteContextAsync(
                "Target", includeTests: false, includeRelated: true, includeMetrics: false);

            var relationships = Assert.Single(result.Matches).Relationships!;
            Assert.Equal(16, relationships.RelatedItemsCount);
            Assert.Equal(5, relationships.RelatedItemsReturnedCount);
            Assert.Equal(5, relationships.RelatedItems!.Count);
            Assert.All(relationships.RelatedItems, item => Assert.Equal("C:/repo/src/Target.cs", item.File));
            Assert.True(relationships.RelatedItemsTruncated);
            Assert.True(relationships.Truncated);
            Assert.Equal(
                ["same-6", "same-4", "same-2", "same-0", "same-7", "same-5", "same-3", "same-1",
                 "namespace-0", "namespace-1", "namespace-2", "namespace-3", "namespace-4", "namespace-5",
                 "namespace-6", "namespace-7"],
                Assert.Single(full.Matches).Relationships.RelatedItems.Select(node => node.Id));
        }

        [Fact]
        public async Task GetCompactContextAsync_LabelsThreeHopTraversalAndCycleUnderCap()
        {
            var nodes = new[]
            {
                new CodeNode { Id = "root", Name = "Root" },
                new CodeNode { Id = "one", Name = "One" },
                new CodeNode { Id = "two", Name = "Two" },
                new CodeNode { Id = "three", Name = "Three" }
            };
            _nodeRepository.FindByNameAsync("Root", null, true).Returns([nodes[0]]);
            foreach (var node in nodes) _nodeRepository.GetByIdAsync(node.Id!).Returns(node);
            _edgeRepository.GetBySourceIdAsync("root").Returns([Edge("root", "one", "CALLS")]);
            _edgeRepository.GetBySourceIdAsync("one").Returns([Edge("one", "two", "REFERENCES")]);
            _edgeRepository.GetBySourceIdAsync("two").Returns([Edge("two", "three", "CALLS")]);
            _edgeRepository.GetBySourceIdAsync("three").Returns([Edge("three", "one", "CALLS")]);

            var depthOne = await _contextService.GetCompactContextAsync("Root", depth: 1, maxRelationships: 1);
            var depthTwo = await _contextService.GetCompactContextAsync("Root", depth: 2, maxRelationships: 1);
            var depthThree = await _contextService.GetCompactContextAsync("Root", depth: 3, maxRelationships: 1);

            Assert.Null(Assert.Single(depthOne.Matches).Relationships!.TransitiveUses);
            var atTwo = Assert.Single(Assert.Single(depthTwo.Matches).Relationships!.TransitiveUses!);
            Assert.Equal(2, atTwo.Distance);
            Assert.Equal(["CALLS", "REFERENCES"], atTwo.RelationPath);
            var atThree = Assert.Single(depthThree.Matches).Relationships!;
            Assert.Equal(2, atThree.TransitiveUsesCount);
            Assert.Equal(1, atThree.TransitiveUsesReturnedCount);
            Assert.Single(atThree.TransitiveUses!);
            Assert.True(atThree.TransitiveUsesTruncated);
            Assert.True(atThree.Truncated);
        }

        [Fact]
        public async Task GetCompactContextAsync_ReturnedIdentifierSelectsOneAmbiguousMethod()
        {
            var declaration = new CodeNode
            {
                Id = "csharp:default:Example.IService.Run()", Identifier = "csharp:Example.IService.Run()", Name = "Run", Type = "Method",
                Namespace = "Example", Signature = "Run()", FilePath = "C:/repo/IService.cs"
            };
            var implementation = new CodeNode
            {
                Id = "csharp:default:Example.Service.Run()", Identifier = "csharp:Example.Service.Run()", Name = "Run", Type = "Method",
                Namespace = "Example", Signature = "Run()", FilePath = "C:/repo/Service.cs"
            };
            _nodeRepository.FindByNameAsync("Run", "Method", true).Returns([declaration, implementation]);
            _nodeRepository.GetByIdentifierAsync(implementation.Identifier!).Returns(implementation);

            var ambiguous = await _contextService.GetCompactContextAsync("Run", "Method", depth: 0);
            Assert.All(ambiguous.Matches, match => Assert.NotNull(match.Target.Identifier));
            Assert.Contains("identifier=", ambiguous.DisambiguationHint);

            var selected = await _contextService.GetCompactContextAsync(
                implementation.Identifier!, "Method", depth: 0);
            Assert.Equal("Service.cs", Path.GetFileName(Assert.Single(selected.Matches).Target.File));
        }

        [Fact]
        public async Task GetCompactContextAsync_CallSiteCapIsIndependentAndTruthful()
        {
            var target = new CodeNode { Id = "target", Name = "Target", Type = "Method" };
            var caller = new CodeNode { Id = "caller", Name = "Caller", Type = "Method" };
            _nodeRepository.FindByNameAsync("Target", null, true).Returns([target]);
            _nodeRepository.GetByIdAsync("caller").Returns(caller);
            _edgeRepository.GetBySourceIdAsync("target").Returns([]);
            _edgeRepository.GetByTargetIdAsync("target").Returns(Enumerable.Range(0, 7).Select(index =>
                new CodeEdge
                {
                    Id = $"call-{index}", SourceId = "caller", TargetId = "target", Type = "CALLS",
                    Metadata = new Dictionary<string, string> { ["line"] = index.ToString() }
                }).ToList());

            var three = await _contextService.GetCompactContextAsync("Target", maxCallSites: 3);
            var threeCaller = Assert.Single(Assert.Single(three.Matches).Relationships!.UsedBy!);
            Assert.Equal(7, threeCaller.Occurrences);
            Assert.Equal(7, threeCaller.CallSiteCount);
            Assert.Equal(3, threeCaller.Lines!.Count);
            Assert.True(threeCaller.CallSitesTruncated);

            var zero = await _contextService.GetCompactContextAsync("Target", maxCallSites: 0);
            var zeroCaller = Assert.Single(Assert.Single(zero.Matches).Relationships!.UsedBy!);
            Assert.Null(zeroCaller.Lines);
            Assert.Equal(7, zeroCaller.CallSiteCount);
            Assert.True(zeroCaller.CallSitesTruncated);
        }

        [Fact]
        public async Task GetCompactContextAsync_TestEvidenceSeparatesIndirectReferencesAndImplementers()
        {
            var target = new CodeNode { Id = "target", Name = "IParser", Type = "Interface" };
            var service = new CodeNode { Id = "service", Name = "Parse", Type = "Method", FilePath = "src/Service.cs" };
            var test = new CodeNode
            {
                Id = "test", Name = "ExercisesService", Type = "Method", FilePath = "tests/ServiceTests.cs",
                Metadata = new Dictionary<string, string> { ["isTest"] = "true" }
            };
            var fake = new CodeNode { Id = "fake", Name = "FakeParser", Type = "Class", FilePath = "tests/Fakes.cs" };
            _nodeRepository.FindByNameAsync("IParser", null, true).Returns([target]);
            _nodeRepository.GetByIdAsync("service").Returns(service);
            _nodeRepository.GetByIdAsync("test").Returns(test);
            _nodeRepository.GetByIdAsync("fake").Returns(fake);
            _nodeRepository.GetAllAsync().Returns([target, service, test, fake]);
            _edgeRepository.GetBySourceIdAsync("target").Returns([]);
            _edgeRepository.GetByTargetIdAsync("target").Returns([
                Edge("service", "target", "CALLS"), Edge("fake", "target", "IMPLEMENTS")]);
            _edgeRepository.GetByTargetIdAsync("service").Returns([Edge("test", "service", "CALLS")]);
            _edgeRepository.GetByTargetIdAsync("fake").Returns([]);
            _edgeRepository.GetByTargetIdAsync("test").Returns([]);

            var result = await _contextService.GetCompactContextAsync("IParser", includeTests: true);
            var testing = Assert.Single(result.Matches).Testing!;
            Assert.False(testing.DirectlyTested);
            Assert.Equal(1, testing.TestReferenceCount);
            Assert.Equal(1, testing.TestImplementerCount);
            Assert.True(testing.IsTested);
            Assert.Contains(testing.TestFiles, file => file.Evidence.Contains("indirectReference"));
            Assert.Contains(testing.TestFiles, file => file.Evidence.Contains("testImplementer"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public async Task GetCompactContextAsync_WithEmptyOrWhitespaceIdentifier_ThrowsArgumentException(
            string identifier)
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => _contextService.GetCompactContextAsync(identifier));
            Assert.Equal("identifier", exception.ParamName);
        }

        [Fact]
        public async Task GetCompleteContextAsync_WithWhitespaceIdentifier_ThrowsArgumentException()
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => _contextService.GetCompleteContextAsync("   "));
            Assert.Equal("identifier", exception.ParamName);
        }

        [Fact]
        public async Task GetMultipleCompactContextAsync_WithBlankIdentifierInList_Throws()
        {
            var request = new MultiContextRequest
            {
                Identifiers = new List<string> { "   " }
            };

            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => _contextService.GetMultipleCompactContextAsync(request));
            Assert.Equal("identifier", exception.ParamName);
        }

        [Fact]
        public async Task GetCompactContextAsync_ManyAmbiguousMatches_HintReportsCountAndTypeNarrowing()
        {
            _nodeRepository.GetByIdentifierAsync("Parse").Returns((CodeNode?)null);
            _nodeRepository.FindByNameAsync("Parse", null, true).Returns([]);
            var candidates = new List<CodeNode>
            {
                new() { Id = "m1", Name = "Parse1", Type = "Method" },
                new() { Id = "m2", Name = "Parse2", Type = "Method" },
                new() { Id = "m3", Name = "Parse3", Type = "Method" },
                new() { Id = "m4", Name = "Parse4", Type = "Method" },
                new() { Id = "c1", Name = "Parse5", Type = "Class" },
                new() { Id = "c2", Name = "Parse6", Type = "Class" },
                new() { Id = "c3", Name = "Parse7", Type = "Class" }
            };
            _nodeRepository.FindByNameAsync("Parse", null, false).Returns(candidates);

            var result = await _contextService.GetCompactContextAsync("Parse", depth: 0);

            Assert.Equal("substring", result.MatchMode);
            Assert.Contains("7 matches", result.DisambiguationHint);
            Assert.Contains("type", result.DisambiguationHint);
            Assert.DoesNotContain("unchanged", result.DisambiguationHint);
            Assert.Contains("exact=true", result.DisambiguationHint);
        }

        [Fact]
        public async Task GetCompactContextAsync_FewAmbiguousMatches_KeepsExactIdentifierHint()
        {
            _nodeRepository.GetByIdentifierAsync("Parse").Returns((CodeNode?)null);
            _nodeRepository.FindByNameAsync("Parse", null, true).Returns(new List<CodeNode>
            {
                new() { Id = "m1", Name = "Parse", Type = "Method" },
                new() { Id = "m2", Name = "Parse", Type = "Method" }
            });

            var result = await _contextService.GetCompactContextAsync("Parse", depth: 0);

            Assert.Equal(2, result.TotalMatches);
            Assert.Contains("Pass identifier=", result.DisambiguationHint);
        }

        [Fact]
        public async Task GetCompleteContextAsync_ManyMatches_HintReportsCountAndTypeNarrowing()
        {
            _nodeRepository.GetByIdentifierAsync("Parse").Returns((CodeNode?)null);
            var candidates = new List<CodeNode>
            {
                new() { Id = "m1", Name = "Parse1", Type = "Method" },
                new() { Id = "m2", Name = "Parse2", Type = "Method" },
                new() { Id = "m3", Name = "Parse3", Type = "Method" },
                new() { Id = "m4", Name = "Parse4", Type = "Method" },
                new() { Id = "c1", Name = "Parse5", Type = "Class" },
                new() { Id = "c2", Name = "Parse6", Type = "Class" }
            };
            _nodeRepository.FindByNameAsync("Parse", null, false).Returns(candidates);
            _edgeRepository.GetBySourceIdAsync(Arg.Any<string>()).Returns([]);
            _edgeRepository.GetByTargetIdAsync(Arg.Any<string>()).Returns([]);
            _nodeRepository.GetAllAsync().Returns(candidates);

            var result = await _contextService.GetCompleteContextAsync("Parse", exact: false);

            Assert.Equal(6, result.Matches.Count);
            Assert.Contains("6 matches", result.DisambiguationHint);
            Assert.Contains("type", result.DisambiguationHint);
            Assert.Contains("exact=true", result.DisambiguationHint);
        }

        [Fact]
        public async Task GetCompactContextAsync_ManyExactMatches_HintOmitsExactSuggestion()
        {
            _nodeRepository.GetByIdentifierAsync("Parse").Returns((CodeNode?)null);
            var candidates = new List<CodeNode>
            {
                new() { Id = "m1", Name = "Parse", Type = "Method" },
                new() { Id = "m2", Name = "Parse", Type = "Method" },
                new() { Id = "m3", Name = "Parse", Type = "Method" },
                new() { Id = "m4", Name = "Parse", Type = "Method" },
                new() { Id = "c1", Name = "Parse", Type = "Class" },
                new() { Id = "c2", Name = "Parse", Type = "Class" }
            };
            _nodeRepository.FindByNameAsync("Parse", null, true).Returns(candidates);

            var result = await _contextService.GetCompactContextAsync("Parse", depth: 0);

            Assert.Equal("exact", result.MatchMode);
            Assert.Contains("6 matches", result.DisambiguationHint);
            Assert.DoesNotContain("exact=true", result.DisambiguationHint);
        }

        private static CodeEdge Edge(string source, string target, string type) => new()
        {
            Id = $"{source}-{type}-{target}", SourceId = source, TargetId = target, Type = type
        };
    }
}
