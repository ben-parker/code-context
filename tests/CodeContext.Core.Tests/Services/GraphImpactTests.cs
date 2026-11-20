using System.Text;
using CodeContext.Core.Diffs;
using CodeContext.Core.Services;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// Diff impact on hand-built graphs and hand-written diffs against real files, so that the mapping, the agreement checks
/// and the walk each have an answer that is plain from the input.
/// </summary>
public sealed class GraphImpactTests : IDisposable
{
    private readonly string _root;

    public GraphImpactTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cc-impact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string I(string name) => GraphTestGraph.Identifier(name);

    private sealed class Fresh(DateTime? asOf) : IIndexFreshness
    {
        public DateTime? FilesReadAsOfUtc { get; } = asOf;
    }

    /// <summary>A file of <paramref name="lines"/> numbered lines under the root; returns its git blob id.</summary>
    private string WriteFile(string relative, int lines)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = string.Concat(Enumerable.Range(1, lines).Select(line => $"line {line}\n"));
        File.WriteAllText(path, content);
        return DiffWorkingTree.BlobId(Encoding.UTF8.GetBytes(content), sha256: false);
    }

    /// <summary>
    /// The sample: src/Foo.cs (30 lines) holds class Foo at 3-28 with Run at 6-12 and Other at 16-24; Caller (src/Caller.cs)
    /// calls Run, Far calls Caller, UsesFoo references Foo, OtherCaller calls Other, FooTests (a test file) calls Run.
    /// </summary>
    private GraphTestGraph Sample(DateTime? indexedAsOf = null)
    {
        WriteFile("src/Foo.cs", 30);
        WriteFile("src/Caller.cs", 10);
        WriteFile("tests/FooTests.cs", 10);
        var graph = new GraphTestGraph(_root) { Freshness = new Fresh(indexedAsOf ?? DateTime.UtcNow.AddHours(1)) };
        return graph
            .Node("Foo", "Class", file: "src/Foo.cs", line: 2, endLine: 27)
            .Node("Foo.Run", file: "src/Foo.cs", line: 5, endLine: 11)
            .Node("Foo.Other", file: "src/Foo.cs", line: 15, endLine: 23)
            .Node("Caller", file: "src/Caller.cs", line: 1, endLine: 5)
            .Node("Far", file: "src/Caller.cs", line: 6, endLine: 8)
            .Node("UsesFoo", file: "src/Caller.cs", line: 9, endLine: 9)
            .Node("OtherCaller", file: "src/Other.cs", line: 0, endLine: 3)
            .Node("FooTests.RunTest", file: "tests/FooTests.cs", line: 1, endLine: 4)
            .Edge("Foo", "Foo.Run", "HAS_METHOD").Edge("Foo", "Foo.Other", "HAS_METHOD")
            .Edge("Caller", "Foo.Run").Edge("Far", "Caller").Edge("UsesFoo", "Foo", "REFERENCES")
            .Edge("OtherCaller", "Foo.Other").Edge("FooTests.RunTest", "Foo.Run");
    }

    private static string Diff(string path, string? blob, params string[] hunks)
        => $"diff --git a/{path} b/{path}\n"
            + (blob is null ? string.Empty : $"index 1234567..{blob} 100644\n")
            + $"--- a/{path}\n+++ b/{path}\n" + string.Join("\n", hunks) + "\n";

    /// <summary>A hunk replacing new line <paramref name="line"/> (one removed, one added, no context).</summary>
    private static string Replace(int line) => $"@@ -{line} +{line} @@\n-old\n+new";

    private static Task<GraphImpactResponse> Impact(ContextService service, string diff, int limit = 20, bool includeTests = false,
        string? relation = null, int maxDepth = 5)
        => service.GetImpactAsync(new GraphImpactRequest
        {
            Diff = diff, Limit = limit, IncludeTests = includeTests, Relation = relation, MaxDepth = maxDepth,
        });

    private string Blob(string relative) => DiffWorkingTree.BlobId(File.ReadAllBytes(Path.Combine(_root, relative)), sha256: false)[..7];

    [Fact]
    public async Task AChangeInsideOneMember_AgreesWithAffectedOnThatMember()
    {
        var service = Sample().Build();

        var impact = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"), Replace(8)));
        var affected = await service.GetAffectedAsync(new GraphAffectedRequest { Identifier = I("Foo.Run") });

        var change = Assert.Single(impact.Changes);
        Assert.Equal((I("Foo.Run"), "member", "8"), (change.Identifier, change.Level, change.ChangedLines));
        Assert.True(impact.Complete);
        Assert.Equal(1, impact.ContentChecked);
        Assert.Equal(
            affected.Items.Select(item => (item.Identifier, item.Distance, item.Relation, item.Via, item.Through)),
            impact.Items.Select(item => (item.Identifier, item.Distance, item.Relation, item.Via, item.Through)));
        Assert.Equal((affected.TotalCount, affected.TestsHidden, affected.FileCount, affected.DepthLimited),
            (impact.TotalCount, impact.TestsHidden, impact.FileCount, impact.DepthLimited));
        Assert.All(impact.Items, item => Assert.Equal(I("Foo.Run"), item.Seed));
        Assert.Equal(["Caller", "Far"], impact.Items.Select(item => item.Name));
        Assert.Equal((2, 1), (change.Dependents, change.TestsHidden));
        Assert.Equal(["tests/FooTests.cs"], impact.TestFiles);
    }

    [Fact]
    public async Task ALineOfTheTypeItself_ChangesTheTypeAlone_NotEveryMember()
    {
        var service = Sample().Build();

        // Line 3 is the class header, line 14 lies between Run and Other.
        var impact = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"), Replace(3), Replace(14)));

        var change = Assert.Single(impact.Changes);
        Assert.Equal((I("Foo"), "container", "3,14"), (change.Identifier, change.Level, change.ChangedLines));
        // The users of the type, not the callers of its members.
        Assert.Equal(["UsesFoo"], impact.Items.Select(item => item.Name));
        // The replaced lines were not a member's: what they held before (a type's old name?) is the old side's alone.
        var replaced = Assert.Single(impact.Unmapped);
        Assert.Equal(("removedBetweenDeclarations", "3,14"), (replaced.Reason, replaced.OldLines));
        Assert.False(impact.Complete);
    }

    [Fact]
    public async Task LinesNoDeclarationHolds_AreReportedAndTheAnswerIsNotComplete()
    {
        var service = Sample().Build();

        // Lines 1-2 (a using, a namespace) and 29-30 lie outside class Foo; line 8 is in Run.
        var impact = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"),
            "@@ -1,2 +1,2 @@\n-a\n-b\n+using X;\n+namespace Y;", Replace(8), Replace(30)));

        Assert.Equal([I("Foo.Run")], impact.Changes.Select(change => change.Identifier));
        Assert.False(impact.Complete);
        Assert.Equal(2, impact.UnmappedCount);
        var outside = impact.Unmapped[0];
        Assert.Equal(("src/Foo.cs", "outsideDeclarations", "1-2,30", 3), (outside.File, outside.Reason, outside.Lines, outside.LineCount));
        // The lines they replaced are the old side's, and are reported as such.
        var replaced = impact.Unmapped[1];
        Assert.Equal(("removedBetweenDeclarations", "1-2,30", 3), (replaced.Reason, replaced.OldLines, replaced.LineCount));
        Assert.Equal(new Dictionary<string, int> { ["outsideDeclarations"] = 1, ["removedBetweenDeclarations"] = 1 }, impact.UnmappedReasons);
    }

    [Fact]
    public async Task RemovedLines_InsideAMemberChangeIt_BetweenMembersAreReportedWithTheirOldLines()
    {
        var service = Sample().Build();

        var inside = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"), "@@ -8,3 +8,2 @@\n line 8\n-gone\n line 9"));
        var insideChange = Assert.Single(inside.Changes);
        Assert.Equal((I("Foo.Run"), (string?)null, 1), (insideChange.Identifier, insideChange.ChangedLines, insideChange.RemovedLines));
        Assert.True(inside.Complete);

        // Between Run (ends 12) and Other (starts 16): lines 13-14 are kept, and old 14-18 were removed after line 13.
        var between = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"),
            "@@ -13,7 +13,2 @@\n line 13\n-void Removed()\n-{\n-  x();\n-}\n-\n line 14"));
        // The declaration the lines held, if any, is on the old side only: nothing is walked from a guess.
        Assert.Empty(between.Changes);
        Assert.False(between.Complete);
        var unmapped = Assert.Single(between.Unmapped);
        Assert.Equal(("removedBetweenDeclarations", "14-18", 5), (unmapped.Reason, unmapped.OldLines, unmapped.LineCount));
        Assert.Contains(I("Foo"), unmapped.Detail);
    }

    [Fact]
    public async Task AChangedSignatureLine_MapsTheNewDeclaration_AndSaysTheOldOneIsNotInTheIndex()
    {
        var service = Sample().Build();

        // Line 6 is Run's first line: the removed line held the old signature.
        var impact = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"), "@@ -5,3 +5,3 @@\n line 5\n-void Run(int x)\n+void Run(long x)\n line 7"));

        Assert.Equal([I("Foo.Run")], impact.Changes.Select(change => change.Identifier));
        Assert.Equal("removedBetweenDeclarations", Assert.Single(impact.Unmapped).Reason);
        Assert.False(impact.Complete);
    }

    [Fact]
    public async Task WhitespaceOnlyLines_ChangeNothing()
    {
        var service = Sample().Build();

        var impact = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"), "@@ -13,2 +13,3 @@\n line 13\n+   \n line 14"));

        Assert.Empty(impact.Changes);
        Assert.Equal(1, impact.IgnoredLines);
        Assert.True(impact.Complete);
        Assert.Equal(0, impact.TotalCount);
    }

    [Fact]
    public async Task CommentLines_ChangeTheMemberHoldingThem_AndNothingWhereNoMemberDoes()
    {
        var service = Sample().Build();

        // A doc comment above Other (lines 13-15 lie between Run and Other) and a file header: nothing changes.
        var above = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"),
            "@@ -1 +1 @@\n-// old header\n+// new header",
            "@@ -14,2 +14,2 @@\n-    /// <summary>Old.</summary>\n-    /// </remarks>\n+    /// <summary>New.</summary>\n+    /* and */"));
        Assert.Empty(above.Changes);
        Assert.Empty(above.Unmapped);
        Assert.Equal(6, above.IgnoredLines);
        Assert.True(above.Complete);

        // The same comment inside Run is Run's.
        var inside = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"), "@@ -8 +8 @@\n-        // old\n+        * new"));
        Assert.Equal([(I("Foo.Run"), "8")], inside.Changes.Select(change => (change.Identifier, change.ChangedLines)));

        // A code line replaced by a comment between members may have held a declaration.
        var replaced = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"), "@@ -14 +14 @@\n-    int Removed;\n+    // gone"));
        Assert.Empty(replaced.Changes);
        Assert.Equal("removedBetweenDeclarations", Assert.Single(replaced.Unmapped).Reason);
    }

    [Fact]
    public async Task AnAddedOrCopiedFile_ChangesEveryDeclarationInIt()
    {
        var blob = WriteFile("src/New.cs", 6);
        var copy = WriteFile("src/Copy.cs", 6);
        var service = new GraphTestGraph(_root) { Freshness = new Fresh(DateTime.UtcNow.AddHours(1)) }
            .Node("New", "Class", file: "src/New.cs", line: 0, endLine: 5).Node("New.Make", file: "src/New.cs", line: 2, endLine: 4)
            .Node("Copy", "Class", file: "src/Copy.cs", line: 0, endLine: 5).Node("Copy.Make", file: "src/Copy.cs", line: 2, endLine: 4)
            .Node("Maker", file: "src/Maker.cs").Node("CopyUser", file: "src/Maker.cs", line: 3)
            .Edge("New", "New.Make", "HAS_METHOD").Edge("Copy", "Copy.Make", "HAS_METHOD")
            .Edge("Maker", "New.Make").Edge("CopyUser", "Copy.Make")
            .Build();

        var diff = $"diff --git a/src/New.cs b/src/New.cs\nnew file mode 100644\nindex 0000000..{blob[..7]}\n--- /dev/null\n+++ b/src/New.cs\n"
            + "@@ -0,0 +1,6 @@\n" + string.Concat(Enumerable.Range(1, 6).Select(line => $"+line {line}\n"))
            + $"diff --git a/src/Orig.cs b/src/Copy.cs\nsimilarity index 100%\ncopy from src/Orig.cs\ncopy to src/Copy.cs\nindex {copy[..7]}..{copy[..7]}\n";
        var impact = await Impact(service, diff);

        Assert.Equal(new[] { I("New"), I("New.Make"), I("Copy"), I("Copy.Make") }.Order(), impact.Changes.Select(change => change.Identifier).Order());
        Assert.Equal(["CopyUser", "Maker"], impact.Items.Select(item => item.Name).Order());
        Assert.True(impact.Complete);
    }

    [Fact]
    public async Task ADeletedFile_IsReportedAsOnlyOnTheOldSide_OrAsNotYetIndexedOrMismatchedWhenTheTreeSaysOtherwise()
    {
        var service = Sample().Build();
        var deletion = "diff --git a/src/Gone.cs b/src/Gone.cs\ndeleted file mode 100644\nindex 1234567..0000000\n--- a/src/Gone.cs\n+++ /dev/null\n"
            + "@@ -1,3 +0,0 @@\n-a\n-b\n-c\n";

        var gone = Assert.Single((await Impact(service, deletion)).Unmapped);
        Assert.Equal(("deletedFile", "1-3", 3), (gone.Reason, gone.OldLines, gone.LineCount));

        // Still in the index: not yet re-indexed. In the working tree: the diff is not the checkout.
        var stale = deletion.Replace("Gone.cs", "Caller.cs");
        File.Delete(Path.Combine(_root, "src", "Caller.cs"));
        Assert.Equal("notYetIndexed", Assert.Single((await Impact(service, stale)).Unmapped).Reason);
        Assert.Equal("contentMismatch", Assert.Single((await Impact(service, deletion.Replace("Gone.cs", "Foo.cs"))).Unmapped).Reason);
    }

    [Fact]
    public async Task ARenameWithoutChangedLines_IsReported_NotSilentlyMapped()
    {
        var service = Sample().Build();

        var impact = await Impact(service, "diff --git a/src/Old.cs b/src/Foo.cs\nsimilarity index 100%\nrename from src/Old.cs\nrename to src/Foo.cs\n");

        Assert.Empty(impact.Changes);
        var renamed = Assert.Single(impact.Unmapped);
        Assert.Equal(("renamed", "src/Old.cs", "src/Foo.cs"), (renamed.Reason, renamed.OldFile, renamed.File));
        Assert.False(impact.Complete);
    }

    [Fact]
    public async Task BinaryUnparsedAndModeOnlyFiles_AreToldApart()
    {
        File.WriteAllText(Path.Combine(_root, "README.md"), "hello\n");
        var service = Sample().Build();

        var impact = await Impact(service,
            "diff --git a/logo.png b/logo.png\nindex 1111111..2222222 100644\nBinary files a/logo.png and b/logo.png differ\n"
            + "diff --git a/README.md b/README.md\n--- a/README.md\n+++ b/README.md\n@@ -1 +1 @@\n-hi\n+hello\n"
            + "diff --git a/run.sh b/run.sh\nold mode 100644\nnew mode 100755\n");

        Assert.Equal(["logo.png:binary", "README.md:notIndexed"], impact.Unmapped.Select(entry => $"{entry.File}:{entry.Reason}"));
        Assert.Equal(1, impact.ModeOnlyFiles);
        Assert.Equal(3, impact.DiffFiles);
    }

    [Fact]
    public async Task ADiffWhoseNewSideIsNotCheckedOut_MapsNothing_AndSaysSo()
    {
        var service = Sample().Build();

        // A diff between two commits: its new side's blob id is not the working tree's.
        var impact = await Impact(service, Diff("src/Foo.cs", "abcdef0", Replace(8)));

        Assert.Empty(impact.Changes);
        Assert.Equal("contentMismatch", Assert.Single(impact.Unmapped).Reason);
        Assert.Contains("No file of the diff matches the working tree", impact.Hint);
        Assert.False(impact.Complete);
    }

    [Fact]
    public async Task ADiffWithNoBlobIds_IsMappedButNotComplete()
    {
        var service = Sample().Build();

        var impact = await Impact(service, $"--- src/Foo.cs\n+++ src/Foo.cs\n{Replace(8)}\n");

        Assert.Equal([I("Foo.Run")], impact.Changes.Select(change => change.Identifier));
        Assert.Equal((0, 1), (impact.ContentChecked, impact.ContentUnchecked));
        Assert.False(impact.Complete);
        Assert.Contains("no blob id", impact.Hint);
    }

    [Fact]
    public async Task AFileWrittenWithCarriageReturns_MatchesItsBlobWithout()
    {
        var service = Sample().Build();
        var lf = string.Concat(Enumerable.Range(1, 30).Select(line => $"line {line}\n"));
        File.WriteAllText(Path.Combine(_root, "src", "Foo.cs"), lf.Replace("\n", "\r\n"));

        var impact = await Impact(service, Diff("src/Foo.cs", DiffWorkingTree.BlobId(Encoding.UTF8.GetBytes(lf), sha256: false)[..9], Replace(8)));

        Assert.Equal(1, impact.ContentChecked);
        Assert.True(impact.Complete);
    }

    [Fact]
    public async Task AFileChangedAfterTheIndexLastReadTheTree_IsNotMapped()
    {
        var service = Sample(indexedAsOf: DateTime.UtcNow.AddHours(-1)).Build();

        var impact = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"), Replace(8)));

        Assert.Empty(impact.Changes);
        Assert.Equal("notYetIndexed", Assert.Single(impact.Unmapped).Reason);
    }

    [Fact]
    public async Task BeforeAnyScanHasFinished_TheDiffIsRefused()
    {
        var graph = Sample();
        graph.Freshness = new Fresh(null);
        var service = graph.Build();

        var refusal = await Assert.ThrowsAsync<IndexNotReadyException>(() => Impact(service, Diff("src/Foo.cs", null, Replace(8))));
        Assert.Contains("No scan of the files has finished", refusal.Message);
    }

    [Fact]
    public async Task PathsOutsideTheRoot_AreNeverLookedAt()
    {
        var service = Sample().Build();

        var impact = await Impact(service,
            "--- a/../escape.cs\n+++ b/../escape.cs\n@@ -1 +1 @@\n-a\n+b\n"
            + "--- /etc/passwd\n+++ /etc/passwd\n@@ -1 +1 @@\n-a\n+b\n");

        Assert.Equal(["invalidPath", "invalidPath"], impact.Unmapped.Select(entry => entry.Reason));
        Assert.Empty(impact.Changes);
    }

    [Fact]
    public async Task ARootBelowTheRepositoryTop_TakesTheDiffsPathsFromTheTop()
    {
        // The repository's top holds .git; the indexed root is its src/ folder.
        var top = _root;
        Directory.CreateDirectory(Path.Combine(top, ".git"));
        var root = Path.Combine(top, "app");
        var blob = WriteFile("app/Foo.cs", 10);
        var service = new GraphTestGraph(root) { Freshness = new Fresh(DateTime.UtcNow.AddHours(1)) }
            .Node("Foo.Run", file: "Foo.cs", line: 0, endLine: 9).Node("Caller", file: "Caller.cs").Edge("Caller", "Foo.Run")
            .Build();

        var impact = await Impact(service, Diff("app/Foo.cs", blob[..7], Replace(3)) + Diff("docs/x.md", null, Replace(1)));

        Assert.Equal([I("Foo.Run")], impact.Changes.Select(change => change.Identifier));
        Assert.Equal("Foo.cs", impact.Changes[0].File);
        var outside = Assert.Single(impact.Unmapped);
        Assert.Equal(("docs/x.md", "outsideRoot"), (outside.File, outside.Reason));
    }

    [Fact]
    public async Task AFileReachedThroughALink_IsNotRead()
    {
        if (OperatingSystem.IsWindows()) return;
        var service = Sample().Build();
        var outside = Path.Combine(Path.GetTempPath(), "cc-impact-target-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(outside, "secret\n");
        try
        {
            File.Delete(Path.Combine(_root, "src", "Foo.cs"));
            File.CreateSymbolicLink(Path.Combine(_root, "src", "Foo.cs"), outside);

            var impact = await Impact(service, Diff("src/Foo.cs", "1234567", Replace(1)));

            Assert.Equal("unreadable", Assert.Single(impact.Unmapped).Reason);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task ManyChanges_AreWalkedOnce_AndEachDependentIsAttributedToTheNearestChange()
    {
        // Run and Other both change; Shared calls Caller (which calls Run) and OtherCaller (which calls Other) at once.
        var graph = Sample().Node("Shared", file: "src/Shared.cs").Edge("Shared", "Caller").Edge("Shared", "OtherCaller");
        WriteFile("src/Other.cs", 5);
        var service = graph.Build();

        var impact = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"), Replace(8), Replace(20)), includeTests: true);

        Assert.Equal([I("Foo.Run"), I("Foo.Other")], impact.Changes.Select(change => change.Identifier));
        var seeds = impact.Items.ToDictionary(item => item.Name!, item => item.Seed);
        Assert.Equal(I("Foo.Run"), seeds["Caller"]);
        Assert.Equal(I("Foo.Other"), seeds["OtherCaller"]);
        Assert.Single(impact.Items, item => item.Name == "Shared");
        // Every listed dependent is attributed to exactly one change.
        Assert.Equal(impact.TotalCount, impact.Changes.Sum(change => change.Dependents));
        Assert.Contains("RunTest", impact.Items.Select(item => item.Name));
    }

    [Fact]
    public async Task DeclarationsWithOneSpan_AreOneChange()
    {
        WriteFile("src/Shared.cs", 5);
        var service = new GraphTestGraph(_root) { Freshness = new Fresh(DateTime.UtcNow.AddHours(1)) }
            .Node("Shared.A", file: "src/Shared.cs", line: 0, endLine: 4).Node("Shared.Bb", file: "src/Shared.cs", line: 0, endLine: 4)
            .Node("UserA", file: "src/U.cs").Node("UserBb", file: "src/U.cs", line: 1)
            .Edge("UserA", "Shared.A").Edge("UserBb", "Shared.Bb")
            .Build();

        var impact = await Impact(service, Diff("src/Shared.cs", Blob("src/Shared.cs"), Replace(2)));

        var change = Assert.Single(impact.Changes);
        Assert.Equal([I("Shared.Bb")], change.SameDeclaration);
        Assert.Equal(["UserA", "UserBb"], impact.Items.Select(item => item.Name));
    }

    [Fact]
    public async Task EveryListIsCappedByLimit_WithExactCountsAndTruncation()
    {
        var graph = new GraphTestGraph(_root) { Freshness = new Fresh(DateTime.UtcNow.AddHours(1)) };
        var diff = new StringBuilder();
        for (var i = 0; i < 6; i++)
        {
            var blob = WriteFile($"src/F{i}.cs", 5);
            graph.Node($"F{i}.M", file: $"src/F{i}.cs", line: 0, endLine: 2)
                .Node($"U{i}", file: $"src/U{i}.cs").Node($"T{i}", file: $"tests/T{i}Tests.cs")
                .Edge($"U{i}", $"F{i}.M").Edge($"T{i}", $"F{i}.M");
            // Line 2 is added in M, line 5 in nothing.
            diff.Append(Diff($"src/F{i}.cs", blob[..7], "@@ -1,0 +2 @@\n+new", "@@ -3,0 +5 @@\n+new"));
        }
        var service = graph.Build();

        var impact = await Impact(service, diff.ToString(), limit: 4);

        Assert.Equal((6, 4, true), (impact.ChangeCount, impact.ChangesReturned, impact.ChangesTruncated));
        Assert.Equal((6, 4, true), (impact.UnmappedCount, impact.UnmappedReturned, impact.UnmappedTruncated));
        Assert.Equal(6, impact.UnmappedLines);
        Assert.Equal((6, 4, true), (impact.TotalCount, impact.ReturnedCount, impact.Truncated));
        Assert.Equal((6, 4, true), (impact.TestFileCount, impact.TestFiles.Count, impact.TestFilesTruncated));
        Assert.Equal(6, impact.TestsHidden);
    }

    [Fact]
    public async Task BadArguments_AreRefusedWithAMessage()
    {
        var service = Sample().Build();

        Assert.Contains("maxDepth", (await Assert.ThrowsAsync<ArgumentException>(() => Impact(service, "x", maxDepth: 0))).Message);
        Assert.Contains("limit", (await Assert.ThrowsAsync<ArgumentException>(() => Impact(service, "x", limit: 101))).Message);
        Assert.Contains("empty", (await Assert.ThrowsAsync<ArgumentException>(() => Impact(service, ""))).Message);
        Assert.Contains("ends early", (await Assert.ThrowsAsync<ArgumentException>(() => Impact(service, "--- a/x\n+++ b/x\n@@ -1,2 +1,2 @@\n x\n"))).Message);
    }

    [Fact]
    public async Task AnAnswerIsReadFromOneGeneration()
    {
        var graph = Sample();
        var service = graph.Build();
        var attempts = 0;
        service.AfterGraphReadForTests = attempt =>
        {
            attempts = attempt;
            if (attempt == 1) graph.Commit();
            return Task.CompletedTask;
        };

        var impact = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"), Replace(8)));

        Assert.Equal(2, attempts);
        Assert.Equal(["Caller", "Far"], impact.Items.Select(item => item.Name));
    }
}
