using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Serialization;
using CodeContext.Core.Services;
using CodeContext.Core.Snapshots;
using CodeContext.Core.Tests.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodeContext.Core.Tests.Snapshots;

/// <summary>
/// Directory events reach the index: a directory created and filled at once, a populated directory moved in, a
/// directory renamed and a directory deleted are followed by the running instance (production services, the real
/// watcher, the fake worker), and the snapshot it then writes answers a query without an instance the same way.
/// </summary>
public sealed class DirectoryChangeTests : IAsyncLifetime
{
    private readonly string _base = Directory.CreateTempSubdirectory("cc-dirs-").FullName;
    private string Root => Path.Combine(_base, "repo");
    private string KeyPath => Path.Combine(_base, "home", "cache.key");
    private CachedInstance _instance = null!;
    private FileWatcherService _watcher = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Root, "old", "inner"));
        File.WriteAllText(Path.Combine(Root, "a.fake"), "a");
        File.WriteAllText(Path.Combine(Root, "old", "inner", "moved.fake"), "m");
        Directory.CreateDirectory(Path.Combine(Root, "doomed", "deep"));
        File.WriteAllText(Path.Combine(Root, "doomed", "deep", "gone.fake"), "g");
        _instance = new CachedInstance(Root, KeyPath, "normal");
        _watcher = new FileWatcherService(
            Options.Create(new CodeContextOptions { RootPath = Root }), NullLogger<FileWatcherService>.Instance,
            _instance.Coordinator, _instance.ScanState, _instance.Workers);
        await _instance.StartAsync();
        await _instance.WaitForAsync(() => _instance.ScanState.Phase == ScanPhase.Ready, "the first scan");
        await _watcher.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _watcher.StopAsync(CancellationToken.None);
        _watcher.Dispose();
        await _instance.DisposeAsync();
        Directory.Delete(_base, recursive: true);
    }

    [Fact]
    public async Task DirectoryCreatedAndFilledAtOnce_IsIndexed()
    {
        var file = Path.Combine(Root, "fresh", "deep", "fresh.fake");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "f");

        await ExpectAsync("freshClass", "fresh/deep/fresh.fake");
    }

    [Fact]
    public async Task PopulatedDirectoryMovedIn_IsIndexed()
    {
        var staged = Path.Combine(_base, "stage", "pkg");
        Directory.CreateDirectory(Path.Combine(staged, "inner"));
        File.WriteAllText(Path.Combine(staged, "inner", "arrived.fake"), "x");
        File.WriteAllText(Path.Combine(staged, "top.fake"), "t");
        Directory.Move(staged, Path.Combine(Root, "pkg"));

        await ExpectAsync("arrivedClass", "pkg/inner/arrived.fake");
        await ExpectAsync("topClass", "pkg/top.fake");
    }

    [Fact]
    public async Task DirectoryRenamed_ItsFilesFollow()
    {
        Directory.Move(Path.Combine(Root, "old"), Path.Combine(Root, "renamed"));

        await ExpectAsync("movedClass", "renamed/inner/moved.fake");
    }

    [Fact]
    public async Task DirectoryMovedOut_ItsFactsGo()
    {
        Directory.CreateDirectory(Path.Combine(_base, "out"));
        Directory.Move(Path.Combine(Root, "doomed"), Path.Combine(_base, "out", "doomed"));

        await ExpectAsync("goneClass", null);
    }

    [Fact]
    public async Task DirectoryDeleted_ItsFactsGo()
    {
        Directory.Delete(Path.Combine(Root, "doomed"), recursive: true);

        await ExpectAsync("goneClass", null);
    }

    [Fact]
    public async Task MovedInDirectoryBelowAMandatoryExclusionOrIgnored_IsNotIndexed()
    {
        File.WriteAllText(Path.Combine(Root, ".gitignore"), "skipped/\n");
        await ExpectAsync("aClass", "a.fake");
        foreach (var target in new[] { Path.Combine("node_modules", "pkg"), "skipped" })
        {
            var staged = Path.Combine(_base, "stage-" + Path.GetFileName(target));
            Directory.CreateDirectory(staged);
            File.WriteAllText(Path.Combine(staged, "hidden.fake"), "h");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root, target))!);
            Directory.Move(staged, Path.Combine(Root, target));
        }
        // A change that is seen marks the point by which the others would have been.
        Directory.CreateDirectory(Path.Combine(Root, "seen"));
        File.WriteAllText(Path.Combine(Root, "seen", "seen.fake"), "s");

        await ExpectAsync("seenClass", "seen/seen.fake");
        Assert.Null(await FindAsync(_instance.Context, "hiddenClass"));
    }

    /// <summary>The running instance comes to answer so, and so does the snapshot it leaves, asked without an instance.</summary>
    private async Task ExpectAsync(string name, string? relativePath)
    {
        string? found = "(never asked)";
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            found = await FindAsync(_instance.Context, name);
            if (Same(found, relativePath)) break;
            await Task.Delay(50);
        }
        Assert.True(Same(found, relativePath), $"the instance answers '{found}' for {name}, expected '{relativePath}'");
    }


    [Fact]
    public async Task AfterDirectoryChanges_TheSnapshotAnswersAsTheInstanceDid()
    {
        var file = Path.Combine(Root, "fresh", "deep", "fresh.fake");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "f");
        Directory.Move(Path.Combine(Root, "old"), Path.Combine(Root, "renamed"));
        Directory.Delete(Path.Combine(Root, "doomed"), recursive: true);
        await ExpectAsync("freshClass", "fresh/deep/fresh.fake");
        await ExpectAsync("movedClass", "renamed/inner/moved.fake");
        await ExpectAsync("goneClass", null);

        await _watcher.StopAsync(CancellationToken.None);
        await _instance.StopAsync();
        var opened = OneShotSnapshot.TryOpen(
            new CodeContextOptions { RootPath = Root, CacheKeyPath = KeyPath },
            new StaticWorkerCatalog(CachedInstance.FakeWorker("normal")),
            checkAgainst: DateTime.UtcNow + SnapshotFreshness.Tolerance);

        Assert.True(opened.Snapshot is not null, opened.Reason);
        Assert.True(Same(await FindAsync(opened.Snapshot!.Context, "freshClass"), "fresh/deep/fresh.fake"));
        Assert.True(Same(await FindAsync(opened.Snapshot.Context, "movedClass"), "renamed/inner/moved.fake"));
        Assert.Null(await FindAsync(opened.Snapshot.Context, "goneClass"));
    }

    private bool Same(string? found, string? relativePath)
        => relativePath is null
            ? found is null
            : found is not null && string.Equals(
                Path.GetFullPath(Path.Combine(Root, found)), Path.GetFullPath(Path.Combine(Root, relativePath)), StringComparison.Ordinal);

    /// <summary>The file the answer places the one node of that name in; null when the answer finds none.</summary>
    private static async Task<string?> FindAsync(IContextService context, string name)
    {
        var response = await context.GetCompactContextAsync(name, exact: true);
        if (response.TotalMatches == 0) return null;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response, CodeContextJsonContext.Default.CompactContextResponse));
        return FirstFile(json.RootElement) ?? "(no file)";
    }

    private static string? FirstFile(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("file") && property.Value.ValueKind == JsonValueKind.String) return property.Value.GetString();
                if (FirstFile(property.Value) is { } nested) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (FirstFile(item) is { } nested) return nested;
        }
        return null;
    }
}
