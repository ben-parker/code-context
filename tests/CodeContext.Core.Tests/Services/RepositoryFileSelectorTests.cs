using CodeContext.Core.Services;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Services;

public sealed class RepositoryFileSelectorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cc-ignore-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private RepositoryFileSelector CreateSelector()
        => new(Options.Create(new CodeContextOptions { RootPath = _root }));

    private string Write(string relativePath, string content = "public class Fixture { }")
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void GitIgnoreSemantics_HandleNestedOverridesNegationAnchorsEscapesAndSeparators()
    {
        Write(".gitignore", """
            out/
            bin/
            ignored.cs
            /root-only.cs
            *.generated.cs
            !important.generated.cs
            nested/*
            blocked/
            !blocked/keep.cs
            \#literal.cs
            \!literal.cs
            file\ with\ space.cs
            """);
        Write("nested/.gitignore", "!keep.cs\n");

        var included = Write("included.cs");
        var nestedKeep = Write("nested/keep.cs");
        var important = Write("deep/important.generated.cs");
        var nestedRootName = Write("deep/root-only.cs");
        var ignored = new[]
        {
            Write("out/worker.cs"), Write("src/bin/generated.cs"), Write("ignored.cs"),
            Write("root-only.cs"), Write("deep/drop.generated.cs"), Write("nested/drop.cs"),
            Write("blocked/keep.cs"), Write("#literal.cs"), Write("!literal.cs"),
            Write("file with space.cs"), Write(".git/never.cs")
        };

        var selector = CreateSelector();
        var files = selector.EnumerateIncludedFiles([".cs"]);

        Assert.Contains(included, files);
        Assert.Contains(nestedKeep, files);
        Assert.Contains(important, files);
        Assert.Contains(nestedRootName, files);
        Assert.All(ignored, path => Assert.DoesNotContain(path, files));
        Assert.True(selector.IsIncluded(nestedKeep.Replace('/', '\\')));
        Assert.Equal(2, selector.IgnoreSourceCount);
        Assert.True(selector.IgnoredPathCount > 0);
    }

    [Fact]
    public void Invalidate_RecompilesChangedRules()
    {
        var source = Write("generated.cs");
        var ignore = Write(".gitignore", "generated.cs\n");
        var selector = CreateSelector();

        Assert.False(selector.IsIncluded(source));
        File.WriteAllText(ignore, "!generated.cs\n");
        selector.Invalidate();

        Assert.True(selector.IsIncluded(source));
    }

    [Fact]
    public void DeepPath_WithNoRules_IsIncludedAndEnumerated()
    {
        var deep = Write("a/b/c/d/e/f/g/keep.cs");
        var selector = CreateSelector();

        Assert.True(selector.IsIncluded(deep));
        Assert.Contains(deep, selector.EnumerateIncludedFiles([".cs"]));
    }

    [Fact]
    public void MandatoryDirectory_IsExcludedAtAnyDepth()
    {
        var nestedGit = Write("src/lib/.git/config.cs");
        var nestedCodeContext = Write("a/b/.codecontext/cache.cs");
        var ordinary = Write("a/b/ordinary.cs");
        var selector = CreateSelector();

        var files = selector.EnumerateIncludedFiles([".cs"]);
        Assert.DoesNotContain(nestedGit, files);
        Assert.DoesNotContain(nestedCodeContext, files);
        Assert.Contains(ordinary, files);
        Assert.False(selector.IsIncluded(nestedGit));
        Assert.False(selector.IsIncluded(nestedCodeContext));
    }

    [Fact]
    public void HiddenDotDirectory_NotMandatory_IsIncluded()
    {
        // Only .git/.codecontext are mandatory exclusions; other dotfiles/dotdirs are ordinary.
        var dotDirFile = Write(".config/app.cs");
        var dotFile = Write(".hidden.cs");
        var selector = CreateSelector();

        var files = selector.EnumerateIncludedFiles([".cs"]);
        Assert.Contains(dotDirFile, files);
        Assert.Contains(dotFile, files);
    }

    [Fact]
    public void NestedIgnore_DeepNegationReincludesUnderAllowedParent()
    {
        Write(".gitignore", "*.log.cs\n");
        Write("a/b/.gitignore", "!keep.log.cs\n");
        var reincluded = Write("a/b/keep.log.cs");
        var stillIgnored = Write("a/b/drop.log.cs");
        var rootIgnored = Write("root.log.cs");
        var selector = CreateSelector();

        var files = selector.EnumerateIncludedFiles([".cs"]);
        Assert.Contains(reincluded, files);
        Assert.DoesNotContain(stillIgnored, files);
        Assert.DoesNotContain(rootIgnored, files);
    }

    [Fact]
    public void IgnoreRule_CaseSensitivity_MatchesPlatformPathSemantics()
    {
        Write(".gitignore", "Ignored.cs\n");
        var lower = Write("ignored.cs");
        var selector = CreateSelector();

        // Windows path semantics are case-insensitive; Unix are case-sensitive.
        if (OperatingSystem.IsWindows())
            Assert.False(selector.IsIncluded(lower));
        else
            Assert.True(selector.IsIncluded(lower));
    }

    [Fact]
    public void SymbolicLink_IsSkipped_WhenReparsePointsUnsupportedIsNoted()
    {
        var target = Write("realdir/target.cs");
        var linkPath = Path.Combine(_root, "link");
        try
        {
            Directory.CreateSymbolicLink(linkPath, Path.Combine(_root, "realdir"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Creating symlinks needs privilege/developer mode on Windows; behavior under
            // reparse points is that they are skipped, but we cannot always create one to assert.
            return;
        }

        var selector = CreateSelector();
        var files = selector.EnumerateIncludedFiles([".cs"]);
        // The real file is enumerated via its real path; the reparse-point link is skipped,
        // so target.cs is never reached a second time through "link/target.cs".
        Assert.Contains(target, files);
        Assert.DoesNotContain(Path.Combine(linkPath, "target.cs"), files);
    }
}
