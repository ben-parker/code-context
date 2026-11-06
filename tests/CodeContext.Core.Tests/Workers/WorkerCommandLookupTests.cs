using CodeContext.Core.Tests.Services;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Given a bare name, Windows and .NET on Unix both look in the current directory, which is
/// usually the repository being indexed. A worker's command is found on PATH here instead, so
/// a program committed to a repository is never run. These tests change the process's current
/// directory and PATH, so they run alone.
/// </summary>
[Collection(nameof(CurrentDirectoryCollection))]
public sealed class WorkerCommandLookupTests : IDisposable
{
    private const string Command = "cc-probe-worker";
    private static readonly string ExecutableName = OperatingSystem.IsWindows() ? Command + ".exe" : Command;

    private readonly string _originalDirectory = Directory.GetCurrentDirectory();
    private readonly string? _originalPath = Environment.GetEnvironmentVariable("PATH");
    private readonly string _scratch = Directory.CreateTempSubdirectory("cc-worker-lookup-").FullName;
    private readonly string _repository;
    private readonly string _manifestDirectory;
    private readonly string _bin;
    private readonly WorkerManifest _manifest = new(1, "fake", "Fake", "1", Command, null, 1, 1);

    public WorkerCommandLookupTests()
    {
        _repository = Directory.CreateDirectory(Path.Combine(_scratch, "repository")).FullName;
        _manifestDirectory = Directory.CreateDirectory(Path.Combine(_scratch, "worker")).FullName;
        _bin = Directory.CreateDirectory(Path.Combine(_scratch, "bin")).FullName;
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDirectory);
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        try { Directory.Delete(_scratch, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string PlaceIn(string directory, string? name = null, bool executable = true)
    {
        var path = Path.Combine(directory, name ?? ExecutableName);
        File.WriteAllText(path, string.Empty);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, executable
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return path;
    }

    private void StartInsideRepository(params string[] pathEntries)
    {
        Directory.SetCurrentDirectory(_repository);
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, pathEntries));
    }

    [Fact]
    public void ABareCommandOnPath_ResolvesToItsFullPath()
    {
        var onPath = PlaceIn(_bin);
        StartInsideRepository(_bin);

        Assert.Equal(onPath, _manifest.ResolveCommand(_manifestDirectory));
    }

    [Fact]
    public void ACommandInTheRepository_LosesToTheOneOnPath()
    {
        PlaceIn(_repository);
        var onPath = PlaceIn(_bin);
        StartInsideRepository(_bin);

        Assert.Equal(onPath, _manifest.ResolveCommand(_manifestDirectory));
    }

    [Fact]
    public void ACommandOnlyInTheRepository_IsNeverHandedToTheOperatingSystem()
    {
        var planted = PlaceIn(_repository);
        StartInsideRepository(_bin);

        var resolved = _manifest.ResolveCommand(_manifestDirectory);

        // A bare name would be found in the current directory; a full path to nothing fails to
        // start, the way any missing executable does.
        Assert.True(Path.IsPathFullyQualified(resolved), resolved);
        Assert.NotEqual(planted, resolved);
        Assert.False(File.Exists(resolved), resolved);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("repository")]
    public void ARelativePathEntry_IsNotSearched(string entry)
    {
        PlaceIn(_repository);
        Directory.SetCurrentDirectory(_scratch);
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, entry, _bin));
        var fromScratch = _manifest.ResolveCommand(_manifestDirectory);
        Directory.SetCurrentDirectory(_repository);
        var fromRepository = _manifest.ResolveCommand(_manifestDirectory);

        foreach (var resolved in new[] { fromScratch, fromRepository })
        {
            Assert.True(Path.IsPathFullyQualified(resolved), resolved);
            Assert.False(File.Exists(resolved), resolved);
        }
    }

    [Fact]
    public void TheCopyBesideTheManifest_WinsOverPath()
    {
        var beside = PlaceIn(_manifestDirectory);
        PlaceIn(_bin);
        StartInsideRepository(_bin);

        Assert.Equal(beside, _manifest.ResolveCommand(_manifestDirectory));
    }

    [Fact]
    public void AnExplicitRelativeCommand_NeverSearchesPath()
    {
        PlaceIn(_bin);
        StartInsideRepository(_bin);
        var manifest = _manifest with { Command = Path.Combine("sub", Command) };

        Assert.Equal(
            Path.Combine(_manifestDirectory, "sub", Command),
            manifest.ResolveCommand(_manifestDirectory));
    }

    [Fact]
    public void OnWindows_AQuotedPathEntry_IsSearched()
    {
        if (!OperatingSystem.IsWindows()) return;
        var onPath = PlaceIn(_bin);
        StartInsideRepository($"\"{_bin}\"");

        Assert.Equal(onPath, _manifest.ResolveCommand(_manifestDirectory));
    }

    [Fact]
    public void OnWindows_AnExtensionlessShimOnPath_LosesToTheExecutableLaterOn()
    {
        // Node version managers and Git Bash leave shell scripts with the bare name, which
        // CreateProcess would never have chosen.
        if (!OperatingSystem.IsWindows()) return;
        var shims = Directory.CreateDirectory(Path.Combine(_scratch, "shims")).FullName;
        PlaceIn(shims, Command);
        var onPath = PlaceIn(_bin);
        StartInsideRepository(shims, _bin);

        Assert.Equal(onPath, _manifest.ResolveCommand(_manifestDirectory));
    }

    [Fact]
    public void OnUnix_AFileThatCannotBeRun_IsPassedOver()
    {
        if (OperatingSystem.IsWindows()) return;
        var data = Directory.CreateDirectory(Path.Combine(_scratch, "data")).FullName;
        PlaceIn(data, executable: false);
        var onPath = PlaceIn(_bin);
        StartInsideRepository(data, _bin);

        Assert.Equal(onPath, _manifest.ResolveCommand(_manifestDirectory));
    }
}
