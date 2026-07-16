using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

public class WorkerManifestTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cc-manifest-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string WriteManifest(string json)
    {
        var path = Path.Combine(_directory, "worker-manifest.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Load_ValidManifest_PopulatesAllFields()
    {
        var path = WriteManifest("""
            {
              "manifestVersion": 1,
              "parserId": "fake",
              "displayName": "Fake Worker",
              "version": "1.2.3",
              "command": "worker.exe",
              "args": ["--flag"],
              "minProtocolVersion": 1,
              "maxProtocolVersion": 1,
              "languages": ["fake"],
              "extensions": [".fake"],
              "projectMarkers": ["fake.proj"]
            }
            """);

        var manifest = WorkerManifest.Load(path);

        Assert.Equal("fake", manifest.ParserId);
        Assert.Equal(["--flag"], manifest.Args);
        Assert.Equal([".fake"], manifest.Extensions);
    }

    [Fact]
    public void Load_UnsupportedManifestVersion_Throws()
    {
        var path = WriteManifest("""
            {"manifestVersion": 99, "parserId": "x", "displayName": "X", "version": "1",
             "command": "x", "minProtocolVersion": 1, "maxProtocolVersion": 1}
            """);
        Assert.Throws<InvalidDataException>(() => WorkerManifest.Load(path));
    }

    [Fact]
    public void Load_EmptyProtocolRange_Throws()
    {
        var path = WriteManifest("""
            {"manifestVersion": 1, "parserId": "x", "displayName": "X", "version": "1",
             "command": "x", "minProtocolVersion": 2, "maxProtocolVersion": 1}
            """);
        Assert.Throws<InvalidDataException>(() => WorkerManifest.Load(path));
    }

    [Theory]
    [InlineData("Upper")]
    [InlineData("bad id")]
    [InlineData("bad:id")]
    public void Load_InvalidParserIdentifier_Throws(string parserId)
    {
        var path = WriteManifest($$"""
            {"manifestVersion": 1, "parserId": "{{parserId}}", "displayName": "X", "version": "1",
             "command": "x", "minProtocolVersion": 1, "maxProtocolVersion": 1}
            """);
        Assert.Throws<InvalidDataException>(() => WorkerManifest.Load(path));
    }

    [Fact]
    public void ResolveCommand_RelativeCommandBesideManifest_ResolvesAgainstManifestDirectory()
    {
        var workerPath = Path.Combine(_directory, "worker.exe");
        File.WriteAllText(workerPath, "");
        var manifest = new WorkerManifest(1, "fake", "Fake", "1", "worker.exe", null, 1, 1);

        Assert.Equal(workerPath, manifest.ResolveCommand(_directory));
    }

    [Fact]
    public void ResolveCommand_BareCommandNotBesideManifest_FallsBackToPathLookup()
    {
        var manifest = new WorkerManifest(1, "fake", "Fake", "1", "dotnet", null, 1, 1);
        Assert.Equal("dotnet", manifest.ResolveCommand(_directory));
    }

    [Fact]
    public void ResolveCommand_DottedWindowsApphost_ProbesExeSuffix()
    {
        if (!OperatingSystem.IsWindows()) return;
        var workerPath = Path.Combine(_directory, "CodeContext.CSharp.Worker.exe");
        File.WriteAllText(workerPath, "");
        var manifest = new WorkerManifest(
            1, "csharp", "CSharp", "1", "CodeContext.CSharp.Worker", null, 1, 1);

        Assert.Equal(workerPath, manifest.ResolveCommand(_directory));
    }

    [Fact]
    public void FromManifest_BuildsLaunchSpecWithManifestWorkingDirectory()
    {
        var path = WriteManifest("""
            {"manifestVersion": 1, "parserId": "fake", "displayName": "Fake Worker", "version": "1",
             "command": "dotnet", "args": ["exec", "worker.dll"], "minProtocolVersion": 1, "maxProtocolVersion": 1}
            """);

        var spec = WorkerLaunchSpec.FromManifest(path);

        Assert.Equal("fake", spec.ParserId);
        Assert.Equal("dotnet", spec.FileName);
        Assert.Equal(["exec", "worker.dll"], spec.Arguments);
        Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(path)), spec.WorkingDirectory);
        Assert.Equal(1, spec.MinProtocolVersion);
        Assert.Equal(1, spec.MaxProtocolVersion);
    }
}
