using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// A directory the watcher reports, or a path a refresh names, may be a link the repository holds, or lie beneath
/// one. The scan follows none, so neither does the reconciliation of a directory nor a file change: nothing under
/// such a path is selected, and nothing under it is looked at, which would already reach where the link leads.
/// </summary>
public sealed class LinkedSubtreeTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("cc-linked-").FullName;
    private string Root => Path.Combine(_base, "repo");
    private string Outside => Path.Combine(_base, "outside");
    private readonly RecordingSelector _selector;
    private readonly ILanguageWorkerService _workers = Substitute.For<ILanguageWorkerService>();
    private readonly InMemoryFileMetadataRepository _metadata = new();
    private readonly GraphUpdateService _service;

    public LinkedSubtreeTests()
    {
        Directory.CreateDirectory(Path.Combine(Root, "plain"));
        Directory.CreateDirectory(Path.Combine(Outside, "deeper"));
        File.WriteAllText(Path.Combine(Outside, "linked.fake"), "l");
        File.WriteAllText(Path.Combine(Outside, "deeper", "deep.fake"), "d");
        File.WriteAllText(Path.Combine(Outside, ".gitignore"), "");
        var options = Options.Create(new CodeContextOptions { RootPath = Root });
        _selector = new RecordingSelector(new RepositoryFileSelector(options));
        _workers.OwnedExtensions.Returns([".fake"]);
        _workers.TryGetParserForExtension(".fake", out Arg.Any<string>())
            .Returns(call => { call[1] = "fake"; return true; });
        var factory = new InMemoryRepositoryFactory(NullLogger<InMemoryRepositoryFactory>.Instance);
        _service = new GraphUpdateService(factory, options, NullLogger<GraphUpdateService>.Instance, _metadata, _workers, _selector);
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    [Theory]
    [InlineData("link")]
    [InlineData("plain/link")]
    public async Task ADirectoryLink_IsNotWalked(string relative)
    {
        if (OperatingSystem.IsWindows()) return; // creating a link needs a privilege there
        var link = Path.Combine(Root, relative);
        Directory.CreateSymbolicLink(link, Outside);

        var changes = await _service.ExpandSubtreesAsync([link, Path.Combine(link, "deeper")], CancellationToken.None);

        Assert.Empty(changes);
        Assert.DoesNotContain(_selector.Asked, path => path.StartsWith(link + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADirectoryReplacedByALink_LosesWhatItHeld()
    {
        if (OperatingSystem.IsWindows()) return;
        var known = Path.Combine(Root, "plain", "was.fake");
        await _metadata.UpsertAsync(new() { FilePath = known, LastModified = DateTime.UtcNow }, CancellationToken.None);
        Directory.Delete(Path.Combine(Root, "plain"));
        Directory.CreateSymbolicLink(Path.Combine(Root, "plain"), Outside);

        var changes = await _service.ExpandSubtreesAsync([Path.Combine(Root, "plain")], CancellationToken.None);

        var change = Assert.Single(changes);
        Assert.Equal((known, FileChangeType.Deleted), (change.Path, change.Type));
    }

    [Theory]
    [InlineData("filelink.fake", false)]
    [InlineData("dirlink/linked.fake", true)]
    [InlineData("dirlink/deeper/deep.fake", true)]
    public async Task AFileChangeThroughALink_IsADeletion(string relative, bool throughDirectory)
    {
        if (OperatingSystem.IsWindows()) return;
        if (throughDirectory) Directory.CreateSymbolicLink(Path.Combine(Root, "dirlink"), Outside);
        else File.CreateSymbolicLink(Path.Combine(Root, "filelink.fake"), Path.Combine(Outside, "linked.fake"));
        var path = Path.Combine(Root, relative);
        IReadOnlyList<FileChange>? sent = null;
        await _workers.ApplyChangesAsync("fake", Arg.Do<IReadOnlyList<FileChange>>(changes => sent = changes),
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());

        await _service.ProcessFileChangesAsync([new FileChange(path, FileChangeType.Created)], CancellationToken.None);

        var change = Assert.Single(sent!);
        Assert.Equal((path, FileChangeType.Deleted), (change.Path, change.Type));
        Assert.Null(await _metadata.GetByFilePathAsync(path, CancellationToken.None));
        // No ignore rules were looked for beneath the link: that would already be a look through it.
        Assert.DoesNotContain(_selector.Asked, asked => asked.StartsWith(Path.Combine(Root, "dirlink"), StringComparison.Ordinal));
    }

    /// <summary>The real selector, recording every path it is asked about.</summary>
    private sealed class RecordingSelector(IRepositoryFileSelector inner) : IRepositoryFileSelector
    {
        private readonly ConcurrentQueue<string> _asked = new();
        public IReadOnlyCollection<string> Asked => _asked.ToArray();
        public IReadOnlyList<string> EnumerateIncludedFiles(IEnumerable<string> supportedExtensions) => inner.EnumerateIncludedFiles(supportedExtensions);
        public bool IsIncluded(string path, bool isDirectory = false) { _asked.Enqueue(path); return inner.IsIncluded(path, isDirectory); }
        public bool IsIgnoreFile(string path) { _asked.Enqueue(path); return inner.IsIgnoreFile(path); }
        public void Invalidate() => inner.Invalidate();
        public int IgnoreSourceCount => inner.IgnoreSourceCount;
        public int IgnoredPathCount => inner.IgnoredPathCount;
        public IReadOnlyList<string> MandatoryExclusions => inner.MandatoryExclusions;
    }
}
