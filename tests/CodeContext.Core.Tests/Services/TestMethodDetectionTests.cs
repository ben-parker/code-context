using CodeContext.Core;
using CodeContext.Core.Repositories;
using CodeContext.Core.Services;
using NSubstitute;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace CodeContext.Core.Tests.Services
{
    public class TestMethodDetectionTests
    {
        private readonly ICodeNodeRepository _nodeRepository;
        private readonly ICodeEdgeRepository _edgeRepository;
        private readonly IFileMetadataRepository _fileMetadataRepository;
        private readonly ContextService _contextService;

        public TestMethodDetectionTests()
        {
            _nodeRepository = Substitute.For<ICodeNodeRepository>();
            _edgeRepository = Substitute.For<ICodeEdgeRepository>();
            _fileMetadataRepository = Substitute.For<IFileMetadataRepository>();
            _contextService = new ContextService(_nodeRepository, _edgeRepository, _fileMetadataRepository);
            // Mirror the real path index over whatever node set each test feeds GetAllAsync.
            _nodeRepository.StubFindByFilePathFromGetAll();
        }

        [Fact]
        public async Task GetTestMethodsForTarget_WithMatchingTestMethods_ReturnsTestMethods()
        {
            // Arrange
            var targetNode = new CodeNode
            {
                Id = "target-id",
                Name = "UserService",
                Type = "Class",
                FilePath = "/src/UserService.cs"
            };

            var testMethods = new List<CodeNode>
            {
                new CodeNode
                {
                    Id = "test1",
                    Name = "TestUserService",
                    Type = "Method",
                    FilePath = "/tests/UserServiceTests.cs",
                    Signature = "[Test] public void TestUserService()"
                },
                new CodeNode
                {
                    Id = "test2",
                    Name = "UserServiceTests",
                    Type = "Method",
                    FilePath = "/tests/UserServiceTests.cs",
                    Signature = "[Test] public void UserServiceTests()"
                },
                new CodeNode
                {
                    Id = "test3",
                    Name = "Should_CreateUser_WhenValidInput",
                    Type = "Method",
                    FilePath = "/tests/UserServiceTests.cs",
                    Signature = "[Fact] public void Should_CreateUser_WhenValidInput()"
                },
                new CodeNode
                {
                    Id = "test4",
                    Name = "CanCreateUserService",
                    Type = "Method",
                    FilePath = "/tests/UserServiceTests.cs",
                    Signature = "[Theory] public void CanCreateUserService()"
                },
                new CodeNode
                {
                    Id = "not-test",
                    Name = "SomeOtherMethod",
                    Type = "Method",
                    FilePath = "/tests/UserServiceTests.cs",
                    Signature = "public void SomeOtherMethod()"
                }
            };

            _nodeRepository.GetAllAsync().Returns(testMethods);
            _edgeRepository.GetBySourceIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());
            _edgeRepository.GetByTargetIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());
            _nodeRepository.FindByNameAsync("UserService", null).Returns(new List<CodeNode> { targetNode });

            // Act
            var result = await _contextService.GetCompleteContextAsync("UserService");

            // Assert
            Assert.NotNull(result);
            Assert.Single(result.Matches);
            
            var testing = result.Matches[0].Testing;
            Assert.NotNull(testing);
            
            if (testing.TestFiles.Count > 0)
            {
                var testFile = testing.TestFiles[0];
                Assert.True(testFile.TestMethods.Count > 0, "Expected test methods to be found");
                Assert.True(testFile.TestCount > 0, "Expected test count to be greater than 0");
            }
        }

        [Fact]
        public async Task GetTestMethodsForTarget_WithDifferentNamingPatterns_FindsAllPatterns()
        {
            // Arrange
            var targetNode = new CodeNode
            {
                Id = "target-id",
                Name = "GetUser",
                Type = "Method",
                FilePath = "/src/UserService.cs"
            };

            var testMethods = new List<CodeNode>
            {
                new CodeNode
                {
                    Id = "test1",
                    Name = "TestGetUser",
                    Type = "Method",
                    FilePath = "/tests/UserServiceTests.cs"
                },
                new CodeNode
                {
                    Id = "test2",
                    Name = "GetUserTests",
                    Type = "Method",
                    FilePath = "/tests/UserServiceTests.cs"
                },
                new CodeNode
                {
                    Id = "test3",
                    Name = "Test_GetUser_ReturnsUser",
                    Type = "Method",
                    FilePath = "/tests/UserServiceTests.cs"
                },
                new CodeNode
                {
                    Id = "test4",
                    Name = "Should_GetUser_WhenUserExists",
                    Type = "Method",
                    FilePath = "/tests/UserServiceTests.cs"
                },
                new CodeNode
                {
                    Id = "test5",
                    Name = "CanGetUser",
                    Type = "Method",
                    FilePath = "/tests/UserServiceTests.cs"
                },
                new CodeNode
                {
                    Id = "test6",
                    Name = "GetUser_Should_ReturnUser",
                    Type = "Method",
                    FilePath = "/tests/UserServiceTests.cs"
                }
            };

            _nodeRepository.GetAllAsync().Returns(testMethods);
            _edgeRepository.GetBySourceIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());
            _edgeRepository.GetByTargetIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());
            _nodeRepository.FindByNameAsync("GetUser", null).Returns(new List<CodeNode> { targetNode });

            // Act
            var result = await _contextService.GetCompleteContextAsync("GetUser");

            // Assert
            Assert.NotNull(result);
            Assert.Single(result.Matches);
            
            var testing = result.Matches[0].Testing;
            Assert.NotNull(testing);
            
            if (testing.TestFiles.Count > 0)
            {
                var testFile = testing.TestFiles[0];
                Assert.True(testFile.TestMethods.Count >= 3, $"Expected at least 3 test methods, got {testFile.TestMethods.Count}");
            }
        }

        [Fact]
        public async Task CountTestMethodsInFile_WithTestAttributes_CountsCorrectly()
        {
            // Arrange
            var testMethods = new List<CodeNode>
            {
                new CodeNode
                {
                    Id = "test1",
                    Name = "TestMethod1",
                    Type = "Method",
                    FilePath = "/tests/TestFile.cs",
                    Signature = "[Test] public void TestMethod1()"
                },
                new CodeNode
                {
                    Id = "test2",
                    Name = "ShouldDoSomething",
                    Type = "Method",
                    FilePath = "/tests/TestFile.cs",
                    Signature = "[Fact] public void ShouldDoSomething()"
                },
                new CodeNode
                {
                    Id = "test3",
                    Name = "CanExecute",
                    Type = "Method",
                    FilePath = "/tests/TestFile.cs",
                    Signature = "[Theory] public void CanExecute()"
                },
                new CodeNode
                {
                    Id = "not-test",
                    Name = "HelperMethod",
                    Type = "Method",
                    FilePath = "/tests/TestFile.cs",
                    Signature = "public void HelperMethod()"
                }
            };

            _nodeRepository.GetAllAsync().Returns(testMethods);
            _edgeRepository.GetBySourceIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());
            _edgeRepository.GetByTargetIdAsync(Arg.Any<string>()).Returns(new List<CodeEdge>());
            
            var targetNode = new CodeNode
            {
                Id = "target-id",
                Name = "SomeClass",
                Type = "Class",
                FilePath = "/src/SomeClass.cs"
            };
            
            _nodeRepository.FindByNameAsync("SomeClass", null).Returns(new List<CodeNode> { targetNode });

            // Act
            var result = await _contextService.GetCompleteContextAsync("SomeClass");

            // Assert
            Assert.NotNull(result);
            Assert.Single(result.Matches);
            
            var testing = result.Matches[0].Testing;
            Assert.NotNull(testing);
            
            if (testing.TestFiles.Count > 0)
            {
                var testFile = testing.TestFiles[0];
                Assert.Equal(3, testFile.TestCount); // Should count 3 test methods, not the helper method
            }
        }
    }
}