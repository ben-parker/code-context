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
}
