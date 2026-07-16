using CodeContext.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeContext.Core.Tests.Workers;

public sealed class WorkerCatalogTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("cc-catalog-").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void RootsUseDeclaredPrecedenceAndDirectoryOrderIsDeterministic()
    {
        var first = Directory.CreateDirectory(Path.Combine(_temp, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_temp, "second")).FullName;
        WriteManifest(first, "z-worker", "same", "2.0.0", 1, 1);
        WriteManifest(first, "a-worker", "alpha", "1.0.0", 1, 1);
        WriteManifest(second, "same-worker", "same", "1.0.0", 1, 1);

        var catalog = new WorkerCatalog([first, second], NullLogger<WorkerCatalog>.Instance);

        Assert.Equal(["alpha", "same"], catalog.Workers.Select(w => w.Manifest.ParserId));
        Assert.Equal("2.0.0", catalog.Workers.Single(w => w.Manifest.ParserId == "same").Manifest.Version);
    }

    [Fact]
    public void IncompatibleHigherPrecedenceManifestDoesNotHideCompatibleFallback()
    {
        var first = Directory.CreateDirectory(Path.Combine(_temp, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_temp, "second")).FullName;
        WriteManifest(first, "worker", "same", "9.0.0", 99, 100);
        WriteManifest(second, "worker", "same", "1.0.0", 1, 1);

        var catalog = new WorkerCatalog([first, second], NullLogger<WorkerCatalog>.Instance);

        var worker = Assert.Single(catalog.Workers);
        Assert.Equal("1.0.0", worker.Manifest.Version);
        Assert.StartsWith(second, worker.ManifestPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteManifest(
        string root, string directory, string parserId, string version, int minProtocol, int maxProtocol)
    {
        var worker = Directory.CreateDirectory(Path.Combine(root, directory)).FullName;
        File.WriteAllText(Path.Combine(worker, "worker-manifest.json"), $$"""
            {
              "manifestVersion": 1,
              "parserId": "{{parserId}}",
              "displayName": "{{parserId}}",
              "version": "{{version}}",
              "command": "worker",
              "minProtocolVersion": {{minProtocol}},
              "maxProtocolVersion": {{maxProtocol}},
              "extensions": [".{{parserId}}"]
            }
            """);
    }
}
