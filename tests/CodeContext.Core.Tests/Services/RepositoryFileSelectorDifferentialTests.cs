using CodeContext.Core.Services;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// The gate for ignore matching: every decision must equal the regular expression selector's,
/// kept frozen in <see cref="LegacyRepositoryFileSelector"/>. Scans, refreshes and watcher
/// events all ask the same selector, so agreement here covers all three.
/// </summary>
public sealed class RepositoryFileSelectorDifferentialTests : IDisposable
{
    private const string Kelvin = "K";
    private const string LongS = "ſ";

    private static readonly string[] RuleFiles = ["visual-studio", "d2-roguelike", "code-context"];

    private readonly string _root = Directory.CreateTempSubdirectory("cc-ignore-diff-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string[] ReadRules(string name)
        => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "IgnoreRules", name + ".rules"));

    public static TheoryData<string, bool> RuleFilesInBothCaseModes()
    {
        var data = new TheoryData<string, bool>();
        foreach (var file in RuleFiles)
        {
            data.Add(file, true);
            data.Add(file, false);
        }
        return data;
    }

    /// <summary>Asserts that a line reads the same both ways, and decides each path the same.</summary>
    private static void AssertSameDecisions(string line, IReadOnlyList<string> paths, bool ignoreCase)
    {
        LegacyRepositoryFileSelector.IgnoreRule legacy;
        bool legacyParsed;
        try
        {
            legacyParsed = LegacyRepositoryFileSelector.IgnoreRule.TryParse(line, out legacy, ignoreCase);
        }
        catch (ArgumentException)
        {
            Assert.ThrowsAny<ArgumentException>(() => IgnorePattern.TryParse(line, ignoreCase, out _));
            return;
        }

        var parsed = IgnorePattern.TryParse(line, ignoreCase, out var pattern);
        Assert.True(legacyParsed == parsed, $"Rule '{line}': parsed {parsed}, expected {legacyParsed}");
        if (!parsed) return;

        Assert.Equal(legacy.Negated, pattern.Negated);
        foreach (var text in paths)
        {
            var path = IgnorePath.Create(text, ignoreCase);
            foreach (var isDirectory in new[] { false, true })
            {
                var expected = legacy.Matches(text, isDirectory);
                if (pattern.Matches(path, isDirectory) != expected)
                {
                    Assert.Fail($"Rule '{line}' on '{text}' (directory: {isDirectory}, ignoreCase: {ignoreCase}): expected {expected}");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(RuleFilesInBothCaseModes))]
    public void EveryRuleOfARealFileDecidesLikeTheRegex(string file, bool ignoreCase)
    {
        var rules = ReadRules(file);
        var paths = GeneratedPaths(rules, seed: file.Length, randomCount: 800);

        foreach (var line in rules) AssertSameDecisions(line, paths, ignoreCase);
    }

    [Fact]
    public void RealFilesAreMostlyDecidedWithoutARegex()
    {
        foreach (var file in RuleFiles)
        {
            var rules = ReadRules(file)
                .Select(line => IgnorePattern.TryParse(line, ignoreCase: true, out var pattern) ? pattern : null)
                .OfType<IgnorePattern>()
                .ToList();

            Assert.True(rules.Count(rule => rule.IsPlain) * 100 >= rules.Count * 95, file);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TrickyRulesDecideLikeTheRegex(bool ignoreCase)
    {
        string[] rules =
        [
            "*", "**", "***", "****", "/*", "/**", "**/", "/**/", "**/*", "*/**", "a/**", "**/a", "a/**/b", "a/**/b/**/c",
            "a**", "**a", "a**b", "a***b", "a*b*c", "a?b", "?", "??", "a/?", "?/a", "*/*", "*/a", "a/*",
            "/", "//", "///", "\\", "a\\", "\\\\", "\\a", "\\*", "\\?", "\\[", "\\ ", "a\\ ", "a\\  ", "a  ", " a", " ", "\t",
            "#a", "\\#a", "!a", "\\!a", "!", "!!a", "!/a", "!a/", "!**/a", "!*", "a!b", "a#b",
            "[a]", "[ab]", "[a-c]", "[a-cx-z]", "[A-Z]", "[0-9]", "[A-z]", "[Z-a]", "[1-Z]", "[a-]", "[-a]", "[a-c-e]",
            "[!a]", "[!a]b", "x[!a]b", "[^a]", "x[^a]b", "[!/]", "[/]", "a[/]b", "[]", "[]a]", "[!]", "[a", "a[", "]", "[[]", "[]]",
            "[a\\]", "[\\]", "[.]", "[_]", "[a_]", "[a.b]", "[ ]", "[+]", "[*]", "[?]", "[z-a]", "[a-a]",
            "a/", "a//", "/a/", "a/b/", "*.cs", "*.cs/", "/*.cs", "*.[Cc]s", "[Bb]in/", "[Dd]ebug*/", "x86/", "~$*", "*~", "$tf/", "a.b", "a+b", "a(b", "a{1}", "a|b", "^a", "a$",
            "é", "é*", "*é", "[é]", "k", "K", "s", "S", "i", "I", "ss", "*k", "k*", "[k]", "[s]", "[i]", "[a-z]", Kelvin, LongS, "\u0130", "\u0131", "\u00DF", "\u212B",
            "a\r", "*.cs\r",
            "!tools/build/", "!src/lib/out/", "tools/out/logs/", "docs/site", "a/b/c", ".idea/", "build", "out/", "!tools/build",
        ];
        string[] extra =
        [
            "", "/", "//", "a", "A", "b", "ab", "aab", "abc", "axc", "a/b", "a/b/c", "a/x/b", "a/x/y/b", "b/a", "x/a", "x/ab", "xab", "x/b", "xbb", "x\\b",
            "a b", "a ", " a", "a  ", " ", "\t", "#a", "!a", "!!a", "[a]", "[", "]", "[]", "]a", "a]", "[a", "a[", "\\", "a\\b", "*", "?", "a*", "a?", "-", "a-", "-a", "_", ".", "..", "a.b", "axb",
            "build", "out", "tools/build", "src/lib/out", "tools/out/logs", "docs/site", "x/tools/build", "a+b", "a(b", "a{1}", "aa", "a|b", "^a", "a$", "$tf", "~$x", "x~", "x.cs", "dir/x.CS", "bin", "Bin", "BIN", "x/bin", "x86", "X86", "debug", "Debug1",
            "é", "É", "xé", "k", "K", Kelvin, "s", "S", LongS, "i", "I", "İ", "ı", "ss", "ß", "SS",
            "a\n", "a\nb", "\na", "a/\nb", "x\n/a", "a\r", "x.cs\r", "x.cs\n",
        ];
        var paths = GeneratedPaths(rules, seed: 7, randomCount: 300).Concat(extra).ToList();

        foreach (var line in rules) AssertSameDecisions(line, paths, ignoreCase);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void GeneratedRulesDecideLikeTheRegex(int seed, bool ignoreCase)
    {
        const string Alphabet = "abAB.-_/*?[]!\\ 01";
        var random = new Random(seed);
        var rules = new List<string>();
        for (var count = 0; count < 3000; count++)
        {
            var length = random.Next(1, 9);
            rules.Add(new string(Enumerable.Range(0, length).Select(_ => Alphabet[random.Next(Alphabet.Length)]).ToArray()));
        }

        var paths = new List<string>();
        for (var count = 0; count < 300; count++)
        {
            var length = random.Next(0, 8);
            paths.Add(new string(Enumerable.Range(0, length).Select(_ => "abAB.-_/ 01"[random.Next(11)]).ToArray()));
        }

        foreach (var line in rules) AssertSameDecisions(line, paths, ignoreCase);
    }

    [Theory]
    [InlineData("visual-studio")]
    [InlineData("d2-roguelike")]
    [InlineData("code-context")]
    public void ScansRefreshesAndWatcherEventsAgreeWithTheRegexSelector(string file)
    {
        var rules = ReadRules(file);
        var random = new Random(file.Length);
        var paths = GeneratedPaths(rules, file.Length, randomCount: 400)
            .Where(CanBeOnDisk)
            .OrderBy(_ => random.Next())
            .Take(900)
            .ToList();
        var onDisk = new List<string>();
        foreach (var path in paths.Select(path => path + ".cs"))
        {
            if (TryWrite(path, "class C { }")) onDisk.Add(path);
        }

        WriteRules(".gitignore", rules);
        WriteRules("src/.gitignore", ["!keep.cs", "*.tmp", "/rooted", "inner/", "!inner/kept.cs", "build/", "!tools/build/"]);
        WriteRules("src/a/b/.gitignore", ["*", "!*.cs", "!*/"]);
        foreach (var path in new[] { "src/keep.cs", "src/inner/kept.cs", "src/build/x.cs", "src/tools/build/x.cs", "src/deep/tools/build/x.cs", "src/a/b/deep/x.cs", "src/a/b/x.txt", ".git/x.cs", ".codecontext/x.cs", "x/.git/y.cs", "bin/z.cs", "x/obj/z.cs" })
        {
            TryWrite(path, "class C { }");
            onDisk.Add(path);
        }

        var options = Options.Create(new CodeContextOptions
        {
            RootPath = _root,
            IgnorePatterns = ["bin/", "obj/", "node_modules/**"],
        });
        var legacy = new LegacyRepositoryFileSelector(options);
        var selector = new RepositoryFileSelector(options);

        void AssertAgreement()
        {
            // A scan: the same files in the same order, from the same directories.
            Assert.Equal(legacy.EnumerateIncludedFiles([".cs"]), selector.EnumerateIncludedFiles([".cs"]));
            Assert.Equal(legacy.IgnoredPathCount, selector.IgnoredPathCount);
            Assert.Equal(legacy.IgnoreSourceCount, selector.IgnoreSourceCount);

            // Watcher events: single paths, including ones that are gone or never existed.
            foreach (var text in paths.Concat(onDisk).Append("../outside.cs").Append("src/../up.cs"))
            {
                var path = Path.Combine(_root, text.Replace('/', Path.DirectorySeparatorChar));
                foreach (var isDirectory in new[] { false, true })
                {
                    Assert.True(
                        legacy.IsIncluded(path, isDirectory) == selector.IsIncluded(path, isDirectory),
                        $"'{text}' as directory: {isDirectory}");
                }
            }
        }

        AssertAgreement();

        // A refresh: the rules change and both are told to reread them.
        WriteRules(".gitignore", [.. rules.Reverse().Take(rules.Length / 2), "!*.cs", "*.cs", "!src/"]);
        legacy.Invalidate();
        selector.Invalidate();
        AssertAgreement();
    }

    [Fact]
    public void NothingBeneathAnIgnoredDirectoryIsRead()
    {
        WriteRules(".gitignore", ["skipped/", "!skipped/kept.cs"]);
        WriteRules("skipped/.gitignore", ["!*"]);
        TryWrite("skipped/kept.cs", "class C { }");
        TryWrite("seen.cs", "class C { }");
        var selector = new RepositoryFileSelector(Options.Create(new CodeContextOptions { RootPath = _root }));

        var files = selector.EnumerateIncludedFiles([".cs"]);

        Assert.Equal([Path.Combine(_root, "seen.cs")], files);
        Assert.Equal(1, selector.IgnoreSourceCount);
    }

    private void WriteRules(string relative, IEnumerable<string> lines)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    private bool TryWrite(string relative, string content)
    {
        try
        {
            var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A generated path can need a directory where a file already is.
            return false;
        }
    }

    private static bool CanBeOnDisk(string path)
        => path.Length is > 0 and < 120
            && path.IndexOfAny(['<', '>', ':', '"', '|', '?', '*', '\\', '\n', '\r', '\t']) < 0
            && path.Split('/').All(segment => segment.Length > 0 && segment[^1] is not (' ' or '.') && segment != "..");

    /// <summary>
    /// Paths that exercise these rules: each rule written out as a path that should match it,
    /// the same with a character changed or the case changed, at several depths and with
    /// children, and random combinations of the words the rules use.
    /// </summary>
    private static List<string> GeneratedPaths(IReadOnlyList<string> rules, int seed, int randomCount)
    {
        var random = new Random(seed);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var words = new HashSet<string>(StringComparer.Ordinal) { "src", "a", "Debug", "bin", "packages", "Program.cs", "x.user", "Keep.CS" };
        foreach (var line in rules)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var example = WriteOut(line.TrimStart('!').TrimEnd('\r', ' ').Trim('/'), random);
            if (example.Length == 0) continue;

            foreach (var word in example.Split('/', StringSplitOptions.RemoveEmptyEntries)) words.Add(word);
            paths.Add(example);
            paths.Add("src/" + example);
            paths.Add(example + "/child.cs");
            paths.Add("deep/er/" + example + "/x/y.txt");
            paths.Add(example.ToUpperInvariant());
            paths.Add(example.ToLowerInvariant());
            paths.Add(example + "x");
            paths.Add("x" + example);
            var changed = example.ToCharArray();
            changed[random.Next(changed.Length)] = 'q';
            paths.Add(new string(changed));
        }

        var vocabulary = words.ToArray();
        for (var count = 0; count < randomCount; count++)
        {
            var depth = random.Next(1, 6);
            paths.Add(string.Join('/', Enumerable.Range(0, depth).Select(_ => vocabulary[random.Next(vocabulary.Length)])));
        }

        paths.Add("café/x.cs");
        paths.Add(Kelvin + "/" + LongS + ".txt");
        return paths.ToList();
    }

    private static string WriteOut(string glob, Random random)
    {
        var result = new System.Text.StringBuilder();
        for (var index = 0; index < glob.Length; index++)
        {
            var character = glob[index];
            if (character == '*')
            {
                if (index + 1 < glob.Length && glob[index + 1] == '*')
                {
                    index++;
                    result.Append(random.Next(2) == 0 ? string.Empty : "p/q");
                    if (result.Length == 0 && index + 1 < glob.Length && glob[index + 1] == '/') index++;
                }
                else
                {
                    result.Append(random.Next(2) == 0 ? string.Empty : "mid");
                }
            }
            else if (character == '?')
            {
                result.Append('z');
            }
            else if (character == '[' && glob.IndexOf(']', index + 1) is var close and > 0)
            {
                var content = glob[(index + 1)..close];
                result.Append(content.Length == 0 ? 'c' : content[random.Next(content.Length)]);
                index = close;
            }
            else if (character == '\\' && index + 1 < glob.Length)
            {
                result.Append(glob[++index]);
            }
            else
            {
                result.Append(character);
            }
        }
        return result.ToString();
    }
}
