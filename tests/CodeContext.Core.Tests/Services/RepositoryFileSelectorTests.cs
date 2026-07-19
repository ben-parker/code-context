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

    // ---- Memoized include/exclude verdict cache ---------------------------------------------

    // Builds a fixture with nested .gitignore files exercising negation and directory-only rules,
    // returning both the paths to probe and the ground-truth verdict for each from a cache-cold
    // selector (one query per fresh instance, so no memoization can influence the reference).
    private (List<(string Path, bool IsDirectory)> Probes, Dictionary<string, bool> Expected) BuildVerdictFixture()
    {
        Write(".gitignore", """
            build/
            *.tmp.cs
            !keep.txt
            secret/
            """);
        Write("src/.gitignore", """
            *.gen.cs
            !important.gen.cs
            """);

        Write("src/app.cs");
        Write("src/module.gen.cs");
        Write("src/important.gen.cs");
        Write("build/output.cs");
        Write("src/build/nested.cs");
        Write("data/notes.tmp.cs");
        Write("keep.txt", "keep");
        Write("secret/inner/leaf.cs");
        Write("plain/file.cs");

        var probes = new List<(string Path, bool IsDirectory)>
        {
            (Path.Combine(_root, "src", "app.cs"), false),
            (Path.Combine(_root, "src", "module.gen.cs"), false),
            (Path.Combine(_root, "src", "important.gen.cs"), false),
            (Path.Combine(_root, "build"), true),
            (Path.Combine(_root, "build", "output.cs"), false),
            (Path.Combine(_root, "src", "build"), true),
            (Path.Combine(_root, "src", "build", "nested.cs"), false),
            (Path.Combine(_root, "data", "notes.tmp.cs"), false),
            (Path.Combine(_root, "keep.txt"), false),
            (Path.Combine(_root, "secret"), true),
            (Path.Combine(_root, "secret", "inner"), true),
            (Path.Combine(_root, "secret", "inner", "leaf.cs"), false),
            (Path.Combine(_root, "plain"), true),
            (Path.Combine(_root, "plain", "file.cs"), false),
            // Same leaf name as a file vs. directory query, to exercise key separation.
            (Path.Combine(_root, "build", "output.cs"), true),
        };

        var expected = new Dictionary<string, bool>();
        foreach (var (path, isDir) in probes)
        {
            // Fresh selector per probe: the reference verdict is never memoized.
            expected[path + "|" + isDir] = CreateSelector().IsIncluded(path, isDir);
        }

        return (probes, expected);
    }

    [Fact]
    public void VerdictCache_RandomizedQueryOrders_MatchCacheColdVerdicts()
    {
        var (probes, expected) = BuildVerdictFixture();

        // Two selector instances each queried in a different randomized order. A shared verdict
        // cache that leaked across file/dir keys or was polluted by query order would diverge from
        // the per-probe cache-cold reference.
        var orderA = probes.OrderBy(_ => Guid.NewGuid()).ToList();
        var orderB = probes.OrderBy(_ => Guid.NewGuid()).ToList();

        var selectorA = CreateSelector();
        foreach (var (path, isDir) in orderA)
            Assert.Equal(expected[path + "|" + isDir], selectorA.IsIncluded(path, isDir));

        var selectorB = CreateSelector();
        foreach (var (path, isDir) in orderB)
            Assert.Equal(expected[path + "|" + isDir], selectorB.IsIncluded(path, isDir));

        // Re-query the first selector (now fully warm) in yet another order: cached verdicts still
        // match ground truth.
        foreach (var (path, isDir) in probes.OrderBy(_ => Guid.NewGuid()))
            Assert.Equal(expected[path + "|" + isDir], selectorA.IsIncluded(path, isDir));
    }

    [Fact]
    public void VerdictCache_StalePersistsWithoutInvalidate_FlipsAfterInvalidate()
    {
        var source = Write("generated.cs");
        var ignore = Write(".gitignore", "generated.cs\n");
        var selector = CreateSelector();

        Assert.False(selector.IsIncluded(source)); // computes and memoizes the excluded verdict

        // Change the ignore rule on disk WITHOUT invalidating: the memoized verdict must persist,
        // documenting that verdict staleness is bounded by the existing rules-cache contract.
        File.WriteAllText(ignore, "!generated.cs\n");
        Assert.False(selector.IsIncluded(source));

        // Invalidate() is the single clearing point: the verdict now flips.
        selector.Invalidate();
        Assert.True(selector.IsIncluded(source));
    }

    [Fact]
    public void VerdictCache_CaseInsensitivity_SameVerdictAcrossCasings()
    {
        // Windows path semantics are case-insensitive; verdict keys use PathComparer, so differing
        // casings must resolve to the same cached verdict. On Unix the comparer is ordinal and the
        // two casings are genuinely distinct paths, so we assert the platform-correct behavior.
        Write(".gitignore", "Ignored.cs\n");
        Write("Ignored.cs");
        var mixed = Path.Combine(_root, "Ignored.cs");
        var lower = Path.Combine(_root, "ignored.cs");

        var coldMixed = CreateSelector().IsIncluded(mixed);
        var coldLower = CreateSelector().IsIncluded(lower);

        var selector = CreateSelector();
        Assert.Equal(coldMixed, selector.IsIncluded(mixed));
        Assert.Equal(coldLower, selector.IsIncluded(lower));
        // Warm re-query in the opposite order still matches the cache-cold reference.
        Assert.Equal(coldMixed, selector.IsIncluded(mixed));

        if (OperatingSystem.IsWindows())
        {
            // Case-insensitive: both casings are excluded and share one verdict.
            Assert.False(coldMixed);
            Assert.False(coldLower);
        }
    }

    [Fact]
    public void VerdictCache_ConcurrentQueriesRacingInvalidate_StayConsistent()
    {
        var (probes, expected) = BuildVerdictFixture();
        var selector = CreateSelector();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var mismatches = 0;

        Parallel.For(0, 64, iteration =>
        {
            try
            {
                if (iteration % 8 == 0)
                {
                    // Racing invalidations swap the verdict cache underneath the readers.
                    selector.Invalidate();
                    return;
                }

                foreach (var (path, isDir) in probes)
                {
                    var verdict = selector.IsIncluded(path, isDir);
                    if (verdict != expected[path + "|" + isDir])
                        Interlocked.Increment(ref mismatches);
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        });

        Assert.Empty(errors);
        // Verdicts are order- and race-independent; the rules cache only ever gets cleared and
        // reloaded from the same on-disk state, so every observed verdict matches ground truth.
        Assert.Equal(0, mismatches);

        // Post-race, a fresh selector still agrees with a warm one across every probe.
        var fresh = CreateSelector();
        foreach (var (path, isDir) in probes)
            Assert.Equal(fresh.IsIncluded(path, isDir), selector.IsIncluded(path, isDir));
    }
}
