using System.Text.Json;
using CSnakes.Runtime;
using CSnakes.Runtime.Python;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CodeContext.Core.Tests.Python;

[Trait("Category", "ExternalTooling")]
[Trait("Category", "KuzuIntegration")]
[Collection("KuzuIntegration")]

public class CSnakesIntegrationTests
{
    private static string GetPythonHome()
    {
        // Get the directory where the test assembly is located
        var assemblyLocation = Path.Join(Environment.CurrentDirectory);
        return Path.Join(assemblyLocation, "TestFiles");
    }

    private static string GetKuzuPythonHome()
    {
        // Get the directory where the Kuzu Python files are located
        var assemblyLocation = Path.GetDirectoryName(typeof(CSnakesIntegrationTests).Assembly.Location);
        return Path.Combine(assemblyLocation!, "..", "..", "..", "..", "..", "src", "CodeContext.Python.Kuzu");
    }

    [Fact]
    public void CanCreatePythonEnvironment()
    {
        var builder = Host.CreateApplicationBuilder();
        var home = GetPythonHome();

        builder.Services
            .WithPython()
            .WithHome(home)
            .FromRedistributable(); // This will download Python 3.12 automatically

        var app = builder.Build();
        var pythonEnv = app.Services.GetService<IPythonEnvironment>();

        Assert.NotNull(pythonEnv);
    }

    [Fact]
    public void CanCallHelloWorldFunction()
    {
        var builder = Host.CreateApplicationBuilder();
        var home = GetPythonHome();

        builder.Services
            .WithPython()
            .WithHome(home)
            .FromRedistributable();

        using var app = builder.Build();
        var pythonEnv = app.Services.GetRequiredService<IPythonEnvironment>();

        var module = pythonEnv.HelloKuzu();
        var result = module.HelloWorld("Ben");
        Assert.NotNull(result);
        Assert.Equal("Hello from Python, Ben!", result);
    }

    [Fact]
    public void CanProcessDictionary()
    {
        var builder = Host.CreateApplicationBuilder();
        var home = GetPythonHome();

        builder.Services
            .WithPython()
            .WithHome(home)
            .FromRedistributable();

        using var app = builder.Build();
        var pythonEnv = app.Services.GetRequiredService<IPythonEnvironment>();

        var module = pythonEnv.HelloKuzu();

        var inputData = new Dictionary<string, PyObject>
        {
            ["name"] = PyObject.From("test"),
            ["value"] = PyObject.From(42),
        };

        IReadOnlyDictionary<string, PyObject> result = module.ProcessData(inputData);

        Assert.NotNull(result);
        Assert.True(result.ContainsKey("processed"));
        Assert.True(result["processed"].As<bool>());
        Assert.Equal("Processed 2 items", result["message"].As<string>());
    }

    [Fact]
    public void CanGetNumbers()
    {
        var builder = Host.CreateApplicationBuilder();
        var home = GetPythonHome();

        builder.Services
            .WithPython()
            .WithHome(home)
            .FromRedistributable();

        using var app = builder.Build();
        var pythonEnv = app.Services.GetRequiredService<IPythonEnvironment>();

        var module = pythonEnv.HelloKuzu();
        IReadOnlyList<long> numbers = module.GetNumbers(5);

        Assert.NotNull(numbers);
        Assert.Equal(5, numbers.Count);
        Assert.Equal([0, 1, 2, 3, 4], numbers);
    }

    [Fact]
    public void CanAddNumbersWithDefaultParameter()
    {
        var builder = Host.CreateApplicationBuilder();
        var home = GetPythonHome();

        builder.Services
            .WithPython()
            .WithHome(home)
            .FromRedistributable();

        using var app = builder.Build();
        var pythonEnv = app.Services.GetRequiredService<IPythonEnvironment>();

        var module = pythonEnv.HelloKuzu();

        // Test with both parameters
        long result1 = module.AddNumbers(5, 3);
        Assert.Equal(8, result1);

        // Test with default parameter
        long result2 = module.AddNumbers(5);
        Assert.Equal(15, result2); // 5 + default 10
    }

    // Kuzu API Tests

    [Fact]
    public void CanInitializeKuzuDatabase()
    {
        var home = GetKuzuPythonHome();
        var venv = Path.Join(home, ".venv");

        // First, set up Python environment
        var pythonBuilder = Host.CreateApplicationBuilder();
        pythonBuilder.Services.WithPython()
            .WithHome(home)
            .WithVirtualEnvironment(venv)
            .WithPipInstaller("requirements.txt")
            .FromRedistributable();

        var pythonApp = pythonBuilder.Build();
        
        // Get the Python environment and create KuzuApi instance
        var pythonEnv = pythonApp.Services.GetRequiredService<IPythonEnvironment>();
        var kuzuApi = pythonEnv.KuzuApi();
        
        // Now create the final app with all services including IKuzuApi
        var builder = Host.CreateApplicationBuilder();
        builder.Services.WithPython()
            .WithHome(home)
            .WithVirtualEnvironment(venv)
            .WithPipInstaller("requirements.txt")
            .FromRedistributable();

        // Register the KuzuApi instance
        builder.Services.AddSingleton<IKuzuApi>(_ => kuzuApi);

        using var app = builder.Build();

        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var dbPath = Path.Combine(tempDir, "test.kuzu");
            
            kuzuApi.InitializeDatabase(dbPath);
            
            // Verify database was created
            Assert.True(Directory.Exists(dbPath));
            
            // Close the database
            kuzuApi.CloseDatabase();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void CanInsertAndRetrieveNode()
    {
        var home = GetKuzuPythonHome();
        var venv = Path.Join(home, ".venv");

        // First, set up Python environment
        var pythonBuilder = Host.CreateApplicationBuilder();
        pythonBuilder.Services.WithPython()
            .WithHome(home)
            .WithVirtualEnvironment(venv)
            .WithPipInstaller("requirements.txt")
            .FromRedistributable();

        var pythonApp = pythonBuilder.Build();
        
        // Get the Python environment and create KuzuApi instance
        var pythonEnv = pythonApp.Services.GetRequiredService<IPythonEnvironment>();
        var kuzuApi = pythonEnv.KuzuApi();

        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var dbPath = Path.Combine(tempDir, "test.kuzu");
            
            kuzuApi.InitializeDatabase(dbPath);

            // Create a test node
            var node = new
            {
                id = "test-node-1",
                name = "TestClass",
                type = "Class",
                file_path = "/test/file.cs",
                start_line = 10,
                end_line = 50,
                @namespace = "Test.Namespace",
                visibility = "public",
                signature = "public class TestClass"
            };
            var nodeJson = JsonSerializer.Serialize(node);

            // Insert the node
            kuzuApi.InsertNode(nodeJson);

            // Retrieve the node
            var retrievedNodeJson = kuzuApi.GetNodeById("test-node-1");

            Assert.NotNull(retrievedNodeJson);
            var retrievedNode = JsonSerializer.Deserialize<JsonElement>(retrievedNodeJson);
            Assert.Equal("TestClass", retrievedNode.GetProperty("name").GetString());
            Assert.Equal("Class", retrievedNode.GetProperty("type").GetString());
            Assert.Equal("/test/file.cs", retrievedNode.GetProperty("file_path").GetString());
            Assert.Equal(10, retrievedNode.GetProperty("start_line").GetInt32());
            Assert.Equal(50, retrievedNode.GetProperty("end_line").GetInt32());

            kuzuApi.CloseDatabase();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void CanInsertAndRetrieveEdge()
    {
        var home = GetKuzuPythonHome();
        var venv = Path.Join(home, ".venv");

        // First, set up Python environment
        var pythonBuilder = Host.CreateApplicationBuilder();
        pythonBuilder.Services.WithPython()
            .WithHome(home)
            .WithVirtualEnvironment(venv)
            .WithPipInstaller("requirements.txt")
            .FromRedistributable();

        var pythonApp = pythonBuilder.Build();
        
        // Get the Python environment and create KuzuApi instance
        var pythonEnv = pythonApp.Services.GetRequiredService<IPythonEnvironment>();
        var kuzuApi = pythonEnv.KuzuApi();

        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var dbPath = Path.Combine(tempDir, "test.kuzu");
            
            kuzuApi.InitializeDatabase(dbPath);

            // Create two nodes
            var classNode = new
            {
                id = "class-1",
                name = "UserService",
                type = "Class",
                file_path = "/services/UserService.cs",
                start_line = 1,
                end_line = 100
            };

            var interfaceNode = new
            {
                id = "interface-1",
                name = "IUserService",
                type = "Interface",
                file_path = "/interfaces/IUserService.cs",
                start_line = 1,
                end_line = 20
            };

            kuzuApi.InsertNode(JsonSerializer.Serialize(classNode));
            kuzuApi.InsertNode(JsonSerializer.Serialize(interfaceNode));

            // Create an edge
            var edge = new
            {
                id = "edge-1",
                source_id = "class-1",
                target_id = "interface-1",
                type = "IMPLEMENTS"
            };

            kuzuApi.InsertEdge(JsonSerializer.Serialize(edge));

            // Get inheritance hierarchy
            var hierarchyJson = kuzuApi.GetInheritanceHierarchy("class-1");
            Assert.NotNull(hierarchyJson);

            var hierarchy = JsonSerializer.Deserialize<JsonElement>(hierarchyJson);
            var parents = hierarchy.GetProperty("parents");
            Assert.Equal(1, parents.GetArrayLength());

            var parent = parents[0];
            Assert.Equal("IUserService", parent.GetProperty("name").GetString());

            kuzuApi.CloseDatabase();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void CanFindNodesByName()
    {
        var home = GetKuzuPythonHome();
        var venv = Path.Join(home, ".venv");

        // First, set up Python environment
        var pythonBuilder = Host.CreateApplicationBuilder();
        pythonBuilder.Services.WithPython()
            .WithHome(home)
            .WithVirtualEnvironment(venv)
            .WithPipInstaller("requirements.txt")
            .FromRedistributable();

        var pythonApp = pythonBuilder.Build();
        
        // Get the Python environment and create KuzuApi instance
        var pythonEnv = pythonApp.Services.GetRequiredService<IPythonEnvironment>();
        var kuzuApi = pythonEnv.KuzuApi();

        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var dbPath = Path.Combine(tempDir, "test.kuzu");
            
            kuzuApi.InitializeDatabase(dbPath);

            // Insert multiple nodes
            var nodes = new[]
            {
                new {
                    id = "1",
                    name = "UserService",
                    type = "Class",
                    file_path = "/UserService.cs",
                    start_line = 1,
                    end_line = 50
                },
                new {
                    id = "2",
                    name = "UserRepository",
                    type = "Class",
                    file_path = "/UserRepository.cs",
                    start_line = 1,
                    end_line = 30
                },
                new {
                    id = "3",
                    name = "OrderService",
                    type = "Class",
                    file_path = "/OrderService.cs",
                    start_line = 1,
                    end_line = 40
                }
            };

            kuzuApi.InsertNodesBatch(JsonSerializer.Serialize(nodes));

            // Find nodes containing "User"
            var userNodesJson = kuzuApi.FindNodesByName("User", false);
            Assert.NotNull(userNodesJson);
            var userNodes = JsonSerializer.Deserialize<JsonElement>(userNodesJson);
            Assert.Equal(2, userNodes.GetArrayLength());

            // Find exact match
            var exactNodesJson = kuzuApi.FindNodesByName("UserService", true);
            Assert.NotNull(exactNodesJson);
            var exactNodes = JsonSerializer.Deserialize<JsonElement>(exactNodesJson);
            Assert.Equal(1, exactNodes.GetArrayLength());

            kuzuApi.CloseDatabase();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void CanGetDatabaseStatistics()
    {
        var home = GetKuzuPythonHome();
        var venv = Path.Join(home, ".venv");

        // First, set up Python environment
        var pythonBuilder = Host.CreateApplicationBuilder();
        pythonBuilder.Services.WithPython()
            .WithHome(home)
            .WithVirtualEnvironment(venv)
            .WithPipInstaller("requirements.txt")
            .FromRedistributable();

        var pythonApp = pythonBuilder.Build();
        
        // Get the Python environment and create KuzuApi instance
        var pythonEnv = pythonApp.Services.GetRequiredService<IPythonEnvironment>();
        var kuzuApi = pythonEnv.KuzuApi();

        var tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDir);

        try
        {
            var dbPath = Path.Combine(tempDir, "test.kuzu");
            
            kuzuApi.InitializeDatabase(dbPath);

            // Insert some test data
            var nodes = new[]
            {
                new {
                    id = "1",
                    name = "TestClass1",
                    type = "Class",
                    file_path = "/test1.cs",
                    start_line = 1,
                    end_line = 10
                },
                new {
                    id = "2",
                    name = "TestMethod",
                    type = "Method",
                    file_path = "/test1.cs",
                    start_line = 5,
                    end_line = 8
                }
            };

            kuzuApi.InsertNodesBatch(JsonSerializer.Serialize(nodes));

            // Get statistics
            var statsJson = kuzuApi.GetStatistics();
            Assert.NotNull(statsJson);
            var stats = JsonSerializer.Deserialize<JsonElement>(statsJson);
            Assert.Equal(2, stats.GetProperty("total_nodes").GetInt32());
            
            var nodesByType = stats.GetProperty("nodes_by_type");
            Assert.Equal(1, nodesByType.GetProperty("Class").GetInt32());
            Assert.Equal(1, nodesByType.GetProperty("Method").GetInt32());

            kuzuApi.CloseDatabase();
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}
