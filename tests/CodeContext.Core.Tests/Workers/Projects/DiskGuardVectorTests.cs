using System.Text.Json;
using CodeContext.CSharp.Worker.Projects;

namespace CodeContext.Core.Tests.Workers.Projects;

/// <summary>
/// What <see cref="PathGuard"/> decides, from the file the TypeScript worker's guard is tested
/// against as well (<c>tests/Shared/disk-guard-vectors.json</c>), so the two cannot drift
/// apart unseen. Each scenario is a made-up disk: a link to a share needs no privilege to
/// make, and a host that does not exist is never waited for.
/// </summary>
public sealed class DiskGuardVectorTests
{
    private static string VectorFile()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "Shared", "disk-guard-vectors.json");
            if (File.Exists(candidate)) return candidate;
        }

        throw new InvalidOperationException("The guard's vectors were not found above the test output.");
    }

    public static TheoryData<string> Scenarios()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(VectorFile()));
        var names = new TheoryData<string>();
        foreach (var scenario in document.RootElement.GetProperty("scenarios").EnumerateArray())
        {
            names.Add(scenario.GetProperty("name").GetString()!);
        }

        return names;
    }

    [WindowsTheory]
    [MemberData(nameof(Scenarios))]
    public void TheGuardDecidesAsTheVectorsSay(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(VectorFile()));
        var scenario = document.RootElement.GetProperty("scenarios").EnumerateArray()
            .Single(candidate => candidate.GetProperty("name").GetString() == name);
        var disk = new MadeUpDisk(scenario);
        var allowed = scenario.TryGetProperty("allow", out var allow)
            ? allow.EnumerateArray().Select(location => location.GetString()).ToList()
            : [];
        var guard = new PathGuard(scenario.GetProperty("root").GetString(), allowed, disk, windows: true);

        var wrong = new List<string>();
        foreach (var vector in scenario.GetProperty("cases").EnumerateArray())
        {
            var path = vector.GetProperty("path").GetString()!;
            var refused = vector.GetProperty("refused");
            var expected = refused.ValueKind == JsonValueKind.Object ? refused.GetProperty("csharp").GetBoolean() : refused.GetBoolean();
            if (guard.Refused(path) != expected)
            {
                wrong.Add($"{vector.GetProperty("why").GetString()}: {path} should be {(expected ? "refused" : "read")}");
            }
        }

        Assert.Empty(wrong);
        // No share nobody chose was looked at to decide.
        Assert.DoesNotContain(disk.Asked, asked => asked.StartsWith(@"\\evil", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A disk that tells one case from another, as a directory can be made to, with one link.</summary>
    private sealed class ExactNames(string link, bool listable = true) : ILinkProbe
    {
        public Dictionary<string, bool>? ReparsePointsIn(string directory)
            => !listable ? null
                : string.Equals(Path.GetDirectoryName(link), directory, StringComparison.Ordinal)
                    ? new(StringComparer.OrdinalIgnoreCase) { [Path.GetFileName(link)] = true }
                    : new(StringComparer.OrdinalIgnoreCase);

        public (bool IsReparsePoint, bool IsDirectory)? Entry(string path) => (path == link, true);

        public bool IsLink(string path, bool isDirectory) => path == link;

        public string Expand(string path) => path;
    }

    [WindowsFact]
    public void TwoEntriesThatDifferOnlyInCaseAreTwoQuestions()
    {
        var guard = new PathGuard(@"C:\repo", [], new ExactNames(@"C:\repo\PKG"), windows: true);

        // The plain one first: what was learned of it must not answer for the link.
        Assert.False(guard.Refused(@"C:\repo\pkg\a.json"));
        Assert.True(guard.Refused(@"C:\repo\PKG\b.json"));
        Assert.False(guard.Refused(@"C:\repo\pkg\c.json"));
    }

    [WindowsFact]
    public void ADirectoryThatCannotBeListedIsAskedAboutNameByName()
    {
        var guard = new PathGuard(@"C:\repo", [], new ExactNames(@"C:\repo\vendor", listable: false), windows: true);

        Assert.True(guard.Refused(@"C:\repo\vendor\x.json"));
        Assert.False(guard.Refused(@"C:\repo\src\A.cs"));
    }

    /// <summary>
    /// The rule for a device's name reads only the path's text, so it is tested on every
    /// platform. On Windows the vectors hold the same cases; this keeps the rule from resting
    /// on what one version of Windows makes of such a path, as it once did.
    /// </summary>
    [Theory]
    [InlineData(@"C:\repo\CON", true)]
    [InlineData(@"C:\repo\src\nul", true)]
    [InlineData(@"C:\repo\src\NUL.txt", true)]
    [InlineData(@"C:\repo\src\aux .d.ts", true)]
    [InlineData(@"C:\repo\src\con.", true)]
    [InlineData(@"C:\repo\PRN\", true)]
    [InlineData(@"C:\repo\COM1", true)]
    [InlineData(@"C:\repo\com0.txt", true)]
    [InlineData(@"C:\repo\LPT9.log", true)]
    [InlineData("C:\\repo\\COM\u00b9", true)]
    [InlineData("C:\\repo\\lpt\u00b3", true)]
    [InlineData(@"C:\repo\CONIN$", true)]
    [InlineData(@"C:\repo\conout$", true)]
    [InlineData(@"\.\NUL", true)]
    [InlineData(@"C:\repo\CON\x.ts", false)]
    [InlineData(@"C:\repo\src\console.ts", false)]
    [InlineData(@"C:\repo\src\com10.ts", false)]
    [InlineData(@"C:\repo\src\comx", false)]
    [InlineData(@"C:\repo\src\.nul", false)]
    [InlineData(@"C:\repo\src\a.nul", false)]
    [InlineData(@"C:\repo\src\ nul", false)]
    [InlineData(@"C:\", false)]
    public void ADeviceNameIsToldByTheLastNameAlone(string path, bool device)
        => Assert.Equal(device, PathGuard.NamesADevice(path));

    /// <summary>The disk a scenario describes: its links, and the second names its entries answer to.</summary>
    private sealed class MadeUpDisk : ILinkProbe
    {
        private readonly HashSet<string> _links = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

        public MadeUpDisk(JsonElement scenario)
        {
            if (scenario.TryGetProperty("links", out var links))
            {
                foreach (var link in links.EnumerateObject()) _links.Add(link.Name);
            }

            if (scenario.TryGetProperty("aliases", out var aliases))
            {
                foreach (var alias in aliases.EnumerateObject()) _aliases[alias.Name] = alias.Value.GetString()!;
            }
        }

        public List<string> Asked { get; } = [];

        /// <summary>A path as the disk spells it: each second name replaced by the entry it names.</summary>
        private string Real(string spelled)
        {
            var root = Path.GetPathRoot(spelled) ?? string.Empty;
            var current = root;
            foreach (var name in spelled[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Join(current, name);
                if (_aliases.TryGetValue(current, out var entry)) current = entry;
            }

            return current;
        }

        public Dictionary<string, bool>? ReparsePointsIn(string directory)
        {
            Asked.Add(directory);
            var listed = Path.TrimEndingDirectorySeparator(Real(directory));
            return _links
                .Where(link => string.Equals(Path.GetDirectoryName(link)?.TrimEnd('\\'), listed.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                .ToDictionary(link => Path.GetFileName(link), _ => true, StringComparer.OrdinalIgnoreCase);
        }

        public (bool IsReparsePoint, bool IsDirectory)? Entry(string path)
        {
            Asked.Add(path);
            return (_links.Contains(Real(path)), true);
        }

        public bool IsLink(string path, bool isDirectory)
        {
            Asked.Add(path);
            return _links.Contains(Real(path));
        }

        public string Expand(string path)
        {
            Asked.Add(path);
            return Real(path);
        }
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "PathGuard normalizes with the system's own rules, which are Windows' only on Windows.";
        }
    }

    private sealed class WindowsTheoryAttribute : TheoryAttribute
    {
        public WindowsTheoryAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "PathGuard normalizes with the system's own rules, which are Windows' only on Windows.";
        }
    }
}
