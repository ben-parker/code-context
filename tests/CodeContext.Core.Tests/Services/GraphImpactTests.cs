using System.Text;
using CodeContext.Core.Diffs;
using CodeContext.Core.Models;
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

    /// <summary>A file holding <paramref name="content"/> under the root; returns its abbreviated git blob id.</summary>
    private string WriteText(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return DiffWorkingTree.BlobId(Encoding.UTF8.GetBytes(content), sha256: false)[..7];
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

    /// <summary>
    /// Members, one per file of 5 lines, wired with every rule affected applies: interface and base dispatch, a call
    /// marked static, an implementation of a changed declaration, a test double implementing what production calls,
    /// REFERENCES beside CALLS, and a chain long enough for the depth limit.
    /// </summary>
    private GraphTestGraph Dispatching()
    {
        var graph = new GraphTestGraph(_root) { Freshness = new Fresh(DateTime.UtcNow.AddHours(1)) };
        foreach (var (name, file) in new[]
        {
            ("IFoo.Run", "src/IFoo.cs"), ("Base.Run", "src/Base.cs"), ("Foo.Run", "src/Foo.cs"), ("Bar.Run", "src/Bar.cs"),
            ("ThroughInterface", "src/A.cs"), ("ThroughBase", "src/B.cs"), ("StaticBase", "src/C.cs"), ("Refers", "src/D.cs"),
            ("Chain1", "src/E.cs"), ("Chain2", "src/F.cs"), ("Chain3", "src/G.cs"),
            ("StubFoo.Run", "tests/StubFoo.cs"), ("StubTests.Check", "tests/StubTests.cs"), ("Seedling", "src/H.cs"),
        })
        {
            WriteFile(file, 5);
            graph.Node(name, file: file, line: 0, endLine: 4);
        }
        return graph
            .Edge("Foo.Run", "Base.Run", "OVERRIDES_MEMBER").Edge("Base.Run", "IFoo.Run", "IMPLEMENTS_MEMBER")
            .Edge("Bar.Run", "IFoo.Run", "IMPLEMENTS_MEMBER")
            .Edge("ThroughInterface", "IFoo.Run").Edge("ThroughBase", "Base.Run").Edge("StaticBase", "Base.Run", staticCall: true)
            .Edge("Refers", "Foo.Run", "REFERENCES").Edge("Chain1", "ThroughInterface").Edge("Chain2", "Chain1").Edge("Chain3", "Chain2")
            .Edge("StubFoo.Run", "IFoo.Run", "IMPLEMENTS_MEMBER").Edge("StubFoo.Run", "Seedling")
            .Edge("StubTests.Check", "StubFoo.Run");
    }

    [Theory]
    [InlineData(null, false, 5)]
    [InlineData(null, true, 5)]
    [InlineData(null, false, 2)]
    [InlineData("CALLS", false, 5)]
    [InlineData("CALLS", true, 1)]
    [InlineData("REFERENCES,CALLS", true, 20)]
    public async Task AOneMemberDiff_AgreesWithAffectedOnThatMember_UnderEveryRuleAndFilter(string? relation, bool includeTests, int maxDepth)
    {
        var service = Dispatching().Build();
        var listed = 0;

        foreach (var (name, file) in new[]
        {
            ("IFoo.Run", "src/IFoo.cs"), ("Base.Run", "src/Base.cs"), ("Foo.Run", "src/Foo.cs"), ("Seedling", "src/H.cs"),
            ("ThroughInterface", "src/A.cs"), ("StubFoo.Run", "tests/StubFoo.cs"),
        })
        {
            var impact = await Impact(service, Diff(file, Blob(file), Replace(3)), includeTests: includeTests, relation: relation, maxDepth: maxDepth);
            var affected = await service.GetAffectedAsync(new GraphAffectedRequest
            {
                Identifier = I(name), IncludeTests = includeTests, Relation = relation, MaxDepth = maxDepth,
            });

            Assert.Equal([I(name)], impact.Changes.Select(change => change.Identifier));
            Assert.True(impact.Complete, name);
            Assert.Equal(
                affected.Items.Select(item => (item.Identifier, item.Distance, item.Relation, item.Via, item.Through)),
                impact.Items.Select(item => (item.Identifier, item.Distance, item.Relation, item.Via, item.Through)));
            Assert.Equal((affected.TotalCount, affected.ReturnedCount, affected.Truncated, affected.TestsHidden, affected.FileCount, affected.DepthLimited),
                (impact.TotalCount, impact.ReturnedCount, impact.Truncated, impact.TestsHidden, impact.FileCount, impact.DepthLimited));
            Assert.All(impact.Items, item => Assert.Equal(I(name), item.Seed));
            listed += impact.TotalCount + impact.TestsHidden;
        }
        // The rules were exercised, not two empty answers compared.
        Assert.True(listed >= 6, $"only {listed} dependents in all");
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

    /// <summary>
    /// src/Doc.cs as it is on disk: a header, a using, class Doc (4-16) with Run (7-10) under a doc comment and Other
    /// (13-15) under a block comment, and a comment at the end of the class that stands above no declaration.
    /// </summary>
    private (ContextService Service, string Blob) Documented()
    {
        var blob = WriteText("src/Doc.cs", string.Join("\n",
            "// header",                          // 1
            "using System;",                      // 2
            "",                                   // 3
            "class Doc",                          // 4
            "{",                                  // 5
            "    /// <summary>Runs.</summary>",   // 6
            "    void Run()",                     // 7
            "    {",                              // 8
            "        // inside",                  // 9
            "    }",                              // 10
            "    /* Other,",                      // 11
            "     * explained. */",               // 12
            "    void Other()",                   // 13
            "    {",                              // 14
            "    }",                              // 15
            "    // nothing follows",             // 16
            "}") + "\n");                         // 17
        var service = new GraphTestGraph(_root) { Freshness = new Fresh(DateTime.UtcNow.AddHours(1)) }
            .Node("Doc", "Class", file: "src/Doc.cs", line: 3, endLine: 16)
            .Node("Doc.Run", file: "src/Doc.cs", line: 6, endLine: 9)
            .Node("Doc.Other", file: "src/Doc.cs", line: 12, endLine: 14)
            .Node("UsesDoc", file: "src/Uses.cs")
            .Edge("Doc", "Doc.Run", "HAS_METHOD").Edge("Doc", "Doc.Other", "HAS_METHOD").Edge("UsesDoc", "Doc", "REFERENCES")
            .Build();
        return (service, blob);
    }

    [Fact]
    public async Task CommentLines_ChangeTheMemberHoldingThem_AndNothingAboveADeclarationOrAtTheHeadOfTheFile()
    {
        var (service, blob) = Documented();

        // The file header, the doc comment above Run and the block comment above Other: nothing changes.
        var above = await Impact(service, Diff("src/Doc.cs", blob,
            "@@ -1 +1 @@\n-// old header\n+// header",
            "@@ -6 +6 @@\n-    /// <summary>Old.</summary>\n+    /// <summary>Runs.</summary>",
            "@@ -11,2 +11,2 @@\n-    /* Old,\n-     * old. */\n+    /* Other,\n+     * explained. */"));
        Assert.Empty(above.Changes);
        Assert.Empty(above.Unmapped);
        Assert.Equal(8, above.IgnoredLines);
        Assert.True(above.Complete);

        // The same comment inside Run is Run's.
        var inside = await Impact(service, Diff("src/Doc.cs", blob, "@@ -9 +9 @@\n-        // old\n+        // inside"));
        Assert.Equal([(I("Doc.Run"), "9")], inside.Changes.Select(change => (change.Identifier, change.ChangedLines)));

        // A code line replaced by a comment between members may have held a declaration.
        var replaced = await Impact(service, Diff("src/Doc.cs", blob, "@@ -16 +16 @@\n-    int Removed;\n+    // nothing follows"));
        Assert.Equal("removedBetweenDeclarations", Assert.Single(replaced.Unmapped).Reason);
    }

    [Fact]
    public async Task ACommentLookingLineThatStandsAboveNoDeclaration_ChangesWhatHoldsIt()
    {
        var (service, blob) = Documented();

        // Line 16 is a comment at the end of the class: nothing in the file says what it belongs to, so it is the class's.
        var trailing = await Impact(service, Diff("src/Doc.cs", blob, "@@ -15,0 +16 @@\n+    // nothing follows"));
        Assert.Equal([(I("Doc"), "container", "16")], trailing.Changes.Select(change => (change.Identifier, change.Level, change.ChangedLines)));
        Assert.Equal(0, trailing.IgnoredLines);
        Assert.True(trailing.Complete);

        // Removed there, it is the class's removed line.
        var removed = await Impact(service, Diff("src/Doc.cs", blob, "@@ -17 +16,0 @@\n-    // gone"));
        var change = Assert.Single(removed.Changes);
        Assert.Equal((I("Doc"), "container", 1), (change.Identifier, change.Level, change.RemovedLines));
    }

    [Fact]
    public async Task ACommentAboveADeclaration_IsFoundInALargeFileWithAVeryLongLine()
    {
        // Over the size read into a pooled buffer, with one line longer than any looked at: read all the same.
        var filler = string.Concat(Enumerable.Repeat("// filler line of a large file\n", 40_000));
        var blob = WriteText("src/Big.cs", "class Big\n{\n" + filler + "    int x = " + new string('1', 70_000) + ";\n    /// <summary>Doc.</summary>\n    void Run()\n    {\n    }\n}\n");
        var service = new GraphTestGraph(_root) { Freshness = new Fresh(DateTime.UtcNow.AddHours(1)) }
            .Node("Big", "Class", file: "src/Big.cs", line: 0, endLine: 40_007)
            .Node("Big.Run", file: "src/Big.cs", line: 40_004, endLine: 40_006)
            .Build();

        var doc = await Impact(service, Diff("src/Big.cs", blob, "@@ -40004 +40004 @@\n-    /// <summary>Old.</summary>\n+    /// <summary>Doc.</summary>"));
        Assert.Empty(doc.Changes);
        Assert.Equal(2, doc.IgnoredLines);
        Assert.True(doc.Complete);

        // A comment directly under the long line stands above no declaration: the type's.
        var filler2 = await Impact(service, Diff("src/Big.cs", blob, "@@ -40002,0 +40002 @@\n+// filler line of a large file"));
        Assert.Equal([(I("Big"), "40002")], filler2.Changes.Select(change => (change.Identifier, change.ChangedLines)));
    }

    [Fact]
    public async Task ACommentLookingLineOfAStringInANamespace_ChangesTheNamespace()
    {
        // A namespace holds statements no declaration does, as a module does: a line of its template literal reads as
        // a comment and is not one. Neither is a line that ends a literal just above a declaration.
        var blob = WriteText("src/n.ts", string.Join("\n",
            "export namespace Inner {",            // 1
            "  const text = `",                    // 2
            "   * item",                           // 3
            "  `;",                                // 4
            "  const more = `",                    // 5
            "   * last`;",                         // 6
            "  export function inside() {",        // 7
            "    return 2;",                       // 8
            "  }",                                 // 9
            "}") + "\n");                          // 10
        var service = new GraphTestGraph(_root) { Freshness = new Fresh(DateTime.UtcNow.AddHours(1)) }
            .Node("Module", "Module", file: "src/n.ts", line: 0, endLine: 10)
            .Node("Inner", "Namespace", file: "src/n.ts", line: 0, endLine: 9)
            .Node("Inner.inside", "Function", file: "src/n.ts", line: 6, endLine: 8)
            .Node("Importer", file: "src/b.ts")
            .Edge("Importer", "Inner", "REFERENCES")
            .Build();

        var inString = await Impact(service, Diff("src/n.ts", blob, "@@ -3 +3 @@\n-   * old\n+   * item"));
        Assert.Equal([(I("Inner"), "container", "3")], inString.Changes.Select(change => (change.Identifier, change.Level, change.ChangedLines)));
        Assert.Equal(["Importer"], inString.Items.Select(item => item.Name));
        Assert.Equal(0, inString.IgnoredLines);

        var endsAboveADeclaration = await Impact(service, Diff("src/n.ts", blob, "@@ -6 +6 @@\n-   * first`;\n+   * last`;"));
        Assert.Equal([(I("Inner"), "container", "6")], endsAboveADeclaration.Changes.Select(change => (change.Identifier, change.Level, change.ChangedLines)));
        Assert.Equal(0, endsAboveADeclaration.IgnoredLines);
    }

    [Fact]
    public async Task ALineThatStartsWithACommentAndGoesOnWithCode_IsCode()
    {
        var service = Sample().Build();

        // Line 14 lies between Run and Other: a declaration after a comment on its own line is the type's change.
        var impact = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"), "@@ -13,0 +14 @@\n+    /* cache */ int added;"));

        Assert.Equal([(I("Foo"), "container", "14")], impact.Changes.Select(change => (change.Identifier, change.Level, change.ChangedLines)));
        Assert.Equal(0, impact.IgnoredLines);
    }

    [Fact]
    public async Task ABlockCommentAroundUnchangedMembers_IsReported_NotIgnored()
    {
        var service = Sample().Build();

        // Other (16-24) commented out by a "/*" above it and a "*/" below it: it is gone from the new side, though no
        // line of it changed.
        var wrapped = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"), "@@ -14,0 +15 @@\n+/*", "@@ -23,0 +25 @@\n+*/"));

        Assert.Empty(wrapped.Changes);
        var entry = Assert.Single(wrapped.Unmapped);
        Assert.Equal(("blockCommentBoundary", "15,25", 2), (entry.Reason, entry.Lines, entry.LineCount));
        Assert.Equal(0, wrapped.IgnoredLines);
        Assert.False(wrapped.Complete);

        // Inside a member the same marker changes the member, as any line there does.
        var inside = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"), "@@ -7,0 +8 @@\n+        /*"));
        Assert.Equal([(I("Foo.Run"), "8")], inside.Changes.Select(change => (change.Identifier, change.ChangedLines)));
        Assert.True(inside.Complete);
    }

    [Fact]
    public async Task AConditionalDirectiveAroundUnchangedMembers_IsReported_NotOnlyMappedToTheType()
    {
        var service = Sample().Build();

        // Other (16-24) compiled out by an "#if" above it and an "#endif" below it: no line of it changed.
        var wrapped = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"), "@@ -14,0 +15 @@\n+#if LEGACY", "@@ -23,0 +25 @@\n+  #  endif // LEGACY"));

        var entry = Assert.Single(wrapped.Unmapped);
        Assert.Equal(("conditionalDirective", "15,25", 2), (entry.Reason, entry.Lines, entry.LineCount));
        Assert.False(wrapped.Complete);
        // The lines are the type's own as well.
        Assert.Equal([(I("Foo"), "container")], wrapped.Changes.Select(change => (change.Identifier, change.Level)));

        // Inside a member the same directive changes the member, as any line there does.
        var inside = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"), "@@ -7,0 +8 @@\n+#if DEBUG", "@@ -8,0 +10 @@\n+#endif"));
        Assert.Equal([(I("Foo.Run"), "8,10")], inside.Changes.Select(change => (change.Identifier, change.ChangedLines)));
        Assert.True(inside.Complete);
    }

    [Fact]
    public async Task ALineOfAModuleThatOnlyLooksLikeAComment_ChangesTheModule_AndADocCommentOrFileHeaderDoesNot()
    {
        // A module holds statements no declaration does (here a template literal): its comment-looking lines are read
        // in the file, and only those above a declaration, or in the file's leading comment, change nothing.
        const string content = "// header\n\n/** Helper. */\nexport function helper() {\n  return 1;\n}\n"
            + "export const help = `\n* run\n`;\n";
        var path = Path.Combine(_root, "src", "a.ts");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        var blob = DiffWorkingTree.BlobId(Encoding.UTF8.GetBytes(content), sha256: false)[..7];
        var service = new GraphTestGraph(_root) { Freshness = new Fresh(DateTime.UtcNow.AddHours(1)) }
            .Node("Module", "Module", file: "src/a.ts", line: 0, endLine: 8)
            .Node("Module.helper", "Function", file: "src/a.ts", line: 3, endLine: 5)
            .Node("Importer", file: "src/b.ts")
            .Edge("Importer", "Module", "IMPORTS")
            .Build();

        var inString = await Impact(service, Diff("src/a.ts", blob, "@@ -8 +8 @@\n-* stop\n+* run"));
        var change = Assert.Single(inString.Changes);
        Assert.Equal((I("Module"), "container", "8"), (change.Identifier, change.Level, change.ChangedLines));
        Assert.Equal(["Importer"], inString.Items.Select(item => item.Name));
        Assert.Equal(0, inString.IgnoredLines);
        Assert.True(inString.Complete);

        var docAndHeader = await Impact(service, Diff("src/a.ts", blob, "@@ -1 +1 @@\n-// old header\n+// header", "@@ -3 +3 @@\n-/** Old. */\n+/** Helper. */"));
        Assert.Empty(docAndHeader.Changes);
        Assert.Equal(4, docAndHeader.IgnoredLines);
        Assert.True(docAndHeader.Complete);
    }

    [Fact]
    public async Task AnAddedFilesUsingsAndNamespace_BindOnlyItsOwnDeclarations_AndAreNotReported()
    {
        const string content = "using System;\nusing static System.Math;\nusing Alias = System.Text;\nglobal using System.IO;\n#nullable enable\n\n"
            + "namespace Demo;\n\npublic class New\n{\n    public int Make() => 1;\n}\n";
        var path = Path.Combine(_root, "src", "New.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        var blob = DiffWorkingTree.BlobId(Encoding.UTF8.GetBytes(content), sha256: false)[..7];
        var service = new GraphTestGraph(_root) { Freshness = new Fresh(DateTime.UtcNow.AddHours(1)) }
            .Node("New", "Class", file: "src/New.cs", line: 8, endLine: 11).Node("New.Make", file: "src/New.cs", line: 10, endLine: 10)
            .Edge("New", "New.Make", "HAS_METHOD")
            .Build();
        var lines = content.TrimEnd('\n').Split('\n');
        string Added(string text) => $"diff --git a/src/New.cs b/src/New.cs\nnew file mode 100644\nindex 0000000..{blob}\n--- /dev/null\n+++ b/src/New.cs\n"
            + $"@@ -0,0 +1,{lines.Length} @@\n" + string.Concat(text.TrimEnd('\n').Split('\n').Select(line => $"+{line}\n"));

        var impact = await Impact(service, Added(content));

        Assert.Equal([I("New"), I("New.Make")], impact.Changes.Select(change => change.Identifier));
        // A global using binds every file of the project: it is still reported.
        var entry = Assert.Single(impact.Unmapped);
        Assert.Equal(("outsideDeclarations", "4"), (entry.Reason, entry.Lines));
        // using, using static, an alias, #nullable and the namespace line; two blank lines.
        Assert.Equal(7, impact.IgnoredLines);

        // The same lines in a file that existed before can change how its unchanged declarations bind.
        var modified = await Impact(service, Diff("src/New.cs", blob, "@@ -1 +1 @@\n-using Other;\n+using System;"));
        Assert.Contains("outsideDeclarations", modified.Unmapped.Select(unmapped => unmapped.Reason));
        Assert.False(modified.Complete);
    }

    /// <summary>A file holding <paramref name="text"/> as UTF-8, after a byte-order mark when <paramref name="bom"/>; returns its abbreviated blob id.</summary>
    private string WriteUtf8(string relative, string text, bool bom)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes = [.. (bom ? (byte[])[0xEF, 0xBB, 0xBF] : []), .. Encoding.UTF8.GetBytes(text)];
        File.WriteAllBytes(path, bytes);
        return DiffWorkingTree.BlobId(bytes, sha256: false)[..7];
    }

    /// <summary>A diff adding <paramref name="path"/> with <paramref name="text"/>, its first line after a byte-order mark when <paramref name="bom"/>.</summary>
    private static string AddedFile(string path, string blob, string text, bool bom)
    {
        var lines = text.TrimEnd('\n').Split('\n');
        return $"diff --git a/{path} b/{path}\nnew file mode 100644\nindex 0000000..{blob}\n--- /dev/null\n+++ b/{path}\n"
            + $"@@ -0,0 +1,{lines.Length} @@\n" + string.Concat(lines.Select((line, index) => $"+{(bom && index == 0 ? "\uFEFF" : "")}{line}\n"));
    }

    [Fact]
    public async Task AByteOrderMark_IsNoTextOfTheFirstLine_InTheDiffOrInTheFile()
    {
        // The reported case: B.cs written with a byte-order mark, C.cs the same text without one.
        const string text = "using System;\n\nnamespace Demo;\n\npublic class B\n{\n    public int N() => new A().M();\n}\n";
        var withMark = WriteUtf8("src/B.cs", text, bom: true);
        var without = WriteUtf8("src/C.cs", text.Replace("class B", "class C"), bom: false);
        // A generated file: the mark, then a comment (EF Core's designer files).
        const string generated = "// <auto-generated />\nusing System;\n\nnamespace Demo;\n\npublic class G\n{\n    public int N() => 1;\n}\n";
        var generatedBlob = WriteUtf8("src/G.Designer.cs", generated, bom: true);
        var service = new GraphTestGraph(_root) { Freshness = new Fresh(DateTime.UtcNow.AddHours(1)) }
            .Node("B", "Class", file: "src/B.cs", line: 4, endLine: 7).Node("B.N", file: "src/B.cs", line: 6, endLine: 6)
            .Node("C", "Class", file: "src/C.cs", line: 4, endLine: 7).Node("C.N", file: "src/C.cs", line: 6, endLine: 6)
            .Node("G", "Class", file: "src/G.Designer.cs", line: 5, endLine: 8).Node("G.N", file: "src/G.Designer.cs", line: 7, endLine: 7)
            .Edge("B", "B.N", "HAS_METHOD").Edge("C", "C.N", "HAS_METHOD").Edge("G", "G.N", "HAS_METHOD")
            .Build();

        var impact = await Impact(service, AddedFile("src/B.cs", withMark, text, bom: true)
            + AddedFile("src/C.cs", without, text.Replace("class B", "class C"), bom: false)
            + AddedFile("src/G.Designer.cs", generatedBlob, generated, bom: true));

        Assert.Empty(impact.Unmapped);
        Assert.True(impact.Complete);
        Assert.Equal(new[] { "B", "B.N", "C", "C.N", "G", "G.N" }.Select(I), impact.Changes.Select(change => change.Identifier));
        // Per file the using and namespace lines and two blank lines; in G the header comment besides.
        Assert.Equal(4 + 4 + 5, impact.IgnoredLines);
    }

    [Fact]
    public async Task AByteOrderMarkOnAModifiedFilesFirstLine_IsReadTheSameOnBothSides_AndAloneChangesNothing()
    {
        const string text = "using System;\n\nnamespace Demo;\n\npublic class B\n{\n    public int N() => 1;\n}\n";
        var blob = WriteUtf8("src/B.cs", text, bom: true);
        const string script = "// header\n\n/** Helper. */\nexport function helper() {\n  return 1;\n}\n";
        var scriptBlob = WriteUtf8("src/a.ts", script, bom: true);
        var service = new GraphTestGraph(_root) { Freshness = new Fresh(DateTime.UtcNow.AddHours(1)) }
            .Node("B", "Class", file: "src/B.cs", line: 4, endLine: 7).Node("B.N", file: "src/B.cs", line: 6, endLine: 6)
            .Edge("B", "B.N", "HAS_METHOD")
            .Node("Module", "Module", file: "src/a.ts", line: 0, endLine: 5)
            .Node("Module.helper", "Function", file: "src/a.ts", line: 3, endLine: 5)
            .Build();

        // A changed using on line 1 is a change all the same, mark or no mark, and is reported.
        var changedUsing = await Impact(service, Diff("src/B.cs", blob, "@@ -1 +1 @@\n-\uFEFFusing Other;\n+\uFEFFusing System;"));
        Assert.Equal([("outsideDeclarations", "1", null), ("removedBetweenDeclarations", null, "1")],
            changedUsing.Unmapped.Select(entry => (entry.Reason, entry.Lines, entry.OldLines)));
        Assert.Equal(0, changedUsing.IgnoredLines);

        // Only the mark added, or only removed: git shows line 1 changed with the same visible text. No token differs.
        foreach (var (before, after) in new[] { ("", "\uFEFF"), ("\uFEFF", "") })
        {
            var markOnly = await Impact(service, Diff("src/B.cs", blob, $"@@ -1 +1 @@\n-{before}using System;\n+{after}using System;"));
            Assert.Empty(markOnly.Unmapped);
            Assert.Empty(markOnly.Changes);
            Assert.Equal(2, markOnly.IgnoredLines);
            Assert.True(markOnly.Complete);
        }

        // The mark added with other lines changed: line 1 is still the same line, the others are mapped as ever.
        var withMore = await Impact(service, Diff("src/B.cs", blob, "@@ -1,7 +1,7 @@\n-using System;\n+\uFEFFusing System;\n \n namespace Demo;\n \n"
            + " public class B\n {\n-    public int N() => 2;\n+    public int N() => 1;"));
        Assert.Equal([(I("B.N"), "7")], withMore.Changes.Select(change => (change.Identifier, change.ChangedLines)));
        Assert.Equal(2, withMore.IgnoredLines);
        // The one-line member's replaced line is reported as ever (it may have declared something else); line 1 is not.
        Assert.Equal([("removedBetweenDeclarations", "7")], withMore.Unmapped.Select(entry => (entry.Reason, entry.OldLines)));

        // TypeScript: the file's leading comment, after the mark in the diff and on disk, changes nothing.
        var header = await Impact(service, Diff("src/a.ts", scriptBlob, "@@ -1 +1 @@\n-\uFEFF// old header\n+\uFEFF// header"));
        Assert.Empty(header.Changes);
        Assert.Equal(2, header.IgnoredLines);
        Assert.True(header.Complete);
    }

    [Fact]
    public async Task UFeffBelowTheFirstLine_IsNoByteOrderMark_AndStaysText()
    {
        // U+FEFF that does not start the file is a character of its line (a zero-width no-break space): a line that
        // starts with it is not read as a comment, so it is not counted away.
        const string text = "public class B\n{\n    public int N() => 1;\n}\n\uFEFF// note\n";
        var blob = WriteUtf8("src/B.cs", text, bom: false);
        var service = new GraphTestGraph(_root) { Freshness = new Fresh(DateTime.UtcNow.AddHours(1)) }
            .Node("B", "Class", file: "src/B.cs", line: 0, endLine: 3).Node("B.N", file: "src/B.cs", line: 2, endLine: 2)
            .Edge("B", "B.N", "HAS_METHOD")
            .Build();

        var impact = await Impact(service, Diff("src/B.cs", blob, "@@ -5 +5 @@\n-\uFEFF// old\n+\uFEFF// note"));

        Assert.Equal(0, impact.IgnoredLines);
        Assert.Contains(impact.Unmapped, entry => entry.Reason == "outsideDeclarations" && entry.Lines == "5");
        Assert.Contains(impact.Unmapped, entry => entry.Reason == "removedBetweenDeclarations" && entry.OldLines == "5");
        Assert.False(impact.Complete);
    }

    /// <summary>
    /// A partial type in three files, as the C# worker reports it: one node, at the ordinally first file's part, with
    /// the other parts' spans. P1.cs holds P and X on one line; P2.cs a second part over several lines; P3.cs a part
    /// with no member, which only adds an interface. UsesP references P, CallsY calls Y.
    /// </summary>
    private (ContextService Service, string P2Blob, string P2Text, string P3Blob) Partial()
    {
        WriteUtf8("src/P1.cs", "namespace Demo;\n\npublic partial class P { private int n; public int X() => n; }\n", bom: false);
        const string p2 = "namespace Demo;\n\n/// <inheritdoc />\npublic partial class P\n    : System.IDisposable\n{\n    public int Y() => n;\n\n"
            + "    public void Dispose() { }\n}\n";
        var p2Blob = WriteUtf8("src/P2.cs", p2, bom: false);
        var p3Blob = WriteUtf8("src/P3.cs", "namespace Demo;\n\npublic partial class P : System.IComparable { public int CompareTo(object? o) => 0; }\n", bom: false);
        var service = new GraphTestGraph(_root) { Freshness = new Fresh(DateTime.UtcNow.AddHours(1)) }
            .Node("P", "Class", file: "src/P1.cs", line: 2, endCol: 70, parts: [("src/P2.cs", 3, 9), ("src/P3.cs", 2, 2)])
            .Node("P.X", file: "src/P1.cs", line: 2, col: 40, endCol: 65).Node("P.Y", file: "src/P2.cs", line: 6).Node("P.Dispose", file: "src/P2.cs", line: 8)
            .Node("P.CompareTo", file: "src/P3.cs", line: 2, col: 50, endCol: 80)
            .Node("UsesP", file: "src/Use.cs").Node("CallsY", file: "src/Call.cs")
            .Edge("P", "P.X", "HAS_METHOD").Edge("P", "P.Y", "HAS_METHOD").Edge("P", "P.Dispose", "HAS_METHOD").Edge("P", "P.CompareTo", "HAS_METHOD")
            .Edge("UsesP", "P", "REFERENCES").Edge("CallsY", "P.Y")
            .Build();
        return (service, p2Blob, p2, p3Blob);
    }

    [Fact]
    public async Task AnAddedPartOfAPartialType_ChangesTheType_AsItsFirstPartsHeaderWould()
    {
        var (service, blob, text, _) = Partial();

        var impact = await Impact(service, AddedFile("src/P2.cs", blob, text, bom: false));

        Assert.Empty(impact.Unmapped);
        Assert.True(impact.Complete);
        // The part's header, base list, braces: the type's own lines in this file, as a container change.
        Assert.Equal([(I("P"), "container", "4-6,10", "src/P2.cs"), (I("P.Y"), "member", "7", "src/P2.cs"), (I("P.Dispose"), "member", "9", "src/P2.cs")],
            impact.Changes.Select(change => (change.Identifier, change.Level, change.ChangedLines, change.File)));
        // The namespace line, two blank lines and the doc comment just above the part.
        Assert.Equal(4, impact.IgnoredLines);
        Assert.Equal([("CallsY", I("P.Y")), ("UsesP", I("P"))], impact.Items.Select(item => (item.Name, item.Seed)).Order());
    }

    [Fact]
    public async Task AChangedHeaderOfAnotherPart_IsAChangeOfTheType_NeverIgnored()
    {
        var (service, blob, _, p3Blob) = Partial();

        // The base list of the second part, in a file that existed before: the type changed.
        var baseList = await Impact(service, Diff("src/P2.cs", blob, "@@ -5 +5 @@\n-    : System.ICloneable\n+    : System.IDisposable"));
        var change = Assert.Single(baseList.Changes);
        Assert.Equal((I("P"), "container", "5"), (change.Identifier, change.Level, change.ChangedLines));
        Assert.Equal(["UsesP"], baseList.Items.Select(item => item.Name));
        // The replaced line is reported as any replaced line of a type's own is.
        var removed = Assert.Single(baseList.Unmapped);
        Assert.Equal(("removedBetweenDeclarations", "5"), (removed.Reason, removed.OldLines));
        Assert.Contains(I("P"), removed.Detail);

        // The part's modifiers.
        var header = await Impact(service, Diff("src/P2.cs", blob, "@@ -4 +4 @@\n-internal partial class P\n+public partial class P"));
        Assert.Equal([(I("P"), "container", "4")], header.Changes.Select(c => (c.Identifier, c.Level, c.ChangedLines)));
        Assert.False(header.Complete);

        // A part with no member of its own, on one line: the file holds a declaration's part, so it is mapped.
        var lone = await Impact(service, Diff("src/P3.cs", p3Blob, "@@ -3 +3 @@\n-public partial class P { public int CompareTo(object? o) => 0; }\n"
            + "+public partial class P : System.IComparable { public int CompareTo(object? o) => 0; }"));
        Assert.Contains((I("P"), "container", "3"), lone.Changes.Select(c => (c.Identifier, c.Level, c.ChangedLines)));
        Assert.DoesNotContain(lone.Unmapped, entry => entry.Reason is "outsideDeclarations" or "notIndexed");
    }

    [Fact]
    public async Task TwoPartsOfOneType_ChangedTogether_AreListedAtTheirOwnFiles_AndWalkedOnce()
    {
        var (service, blob, text, _) = Partial();
        var p1 = "namespace Demo;\n\npublic partial class P { private int n; public int X() => n; }\n";

        var impact = await Impact(service, AddedFile("src/P1.cs", Blob("src/P1.cs"), p1, bom: false) + AddedFile("src/P2.cs", blob, text, bom: false));

        Assert.True(impact.Complete);
        Assert.Equal(
            [(I("P"), "src/P1.cs"), (I("P.X"), "src/P1.cs"), (I("P"), "src/P2.cs"), (I("P.Y"), "src/P2.cs"), (I("P.Dispose"), "src/P2.cs")],
            impact.Changes.Select(change => (change.Identifier, change.File)));
        // One walk from the type: its dependent is counted once, on the first part listed.
        Assert.Equal(["CallsY", "UsesP"], impact.Items.Select(item => item.Name).Order());
        Assert.Equal([1, 0, 0, 1, 0], impact.Changes.Select(change => change.Dependents));
    }

    [Fact]
    public async Task MergedDeclarationsInOneFile_MapTheEarlierOnesHeaderToTheMergedNode_NotToTheModule()
    {
        // TypeScript merges two interfaces of one name; the worker keeps the last as the node and reports the first as
        // a part. Its header was read as the module's own line before: a change of Shared walked from the module.
        const string content = "interface Shared {\n  a: number;\n}\n\ninterface Shared {\n  b: string;\n}\n";
        var blob = WriteUtf8("src/g.ts", content, bom: false);
        var service = new GraphTestGraph(_root) { Freshness = new Fresh(DateTime.UtcNow.AddHours(1)) }
            .Node("Module", "Module", file: "src/g.ts", line: 0, endLine: 6)
            .Node("Shared", "Interface", file: "src/g.ts", line: 4, endLine: 6, parts: [("src/g.ts", 0, 2)])
            .Node("Shared.a", "Property", file: "src/g.ts", line: 1).Node("Shared.b", "Property", file: "src/g.ts", line: 5)
            .Node("UsesShared", file: "src/u.ts").Node("Importer", file: "src/i.ts")
            .Edge("Shared", "Shared.a", "HAS_PROPERTY").Edge("Shared", "Shared.b", "HAS_PROPERTY")
            .Edge("UsesShared", "Shared", "REFERENCES").Edge("Importer", "Module", "IMPORTS")
            .Build();

        var impact = await Impact(service, Diff("src/g.ts", blob, "@@ -1 +1 @@\n-interface Shared extends Base {\n+interface Shared {"));

        Assert.Equal([(I("Shared"), "container", "1")], impact.Changes.Select(change => (change.Identifier, change.Level, change.ChangedLines)));
        Assert.Equal(["UsesShared"], impact.Items.Select(item => item.Name));
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

    /// <summary>A C# worker by its manifest alone: it parses .cs, and names .csproj, .props and global.json as project files.</summary>
    private sealed class ManifestOnly : CodeContext.Core.Workers.ILanguageWorkerService
    {
        public IReadOnlyCollection<string> OwnedExtensions => [".cs"];

        public bool TryGetParserForExtension(string extension, out string parserId)
        {
            parserId = "csharp";
            return string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase);
        }

        public string? ParserNaming(string path)
            => Path.GetExtension(path) is ".cs" or ".csproj" or ".props" || Path.GetFileName(path) == "global.json" ? "csharp" : null;

        public bool TryGetParserForProjectInput(string path, bool isIncluded, out string parserId) => throw new NotSupportedException();
        public Task RefreshProjectsAsync(string parserId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task IndexWorkspaceAsync(string parserId, IReadOnlyList<string> files, CancellationToken ct = default,
            Action<CodeContext.Parser.Protocol.AnalysisProgress>? progressHandler = null) => throw new NotSupportedException();
        public Task ApplyChangesAsync(string parserId, IReadOnlyList<FileChange> changes, IReadOnlyList<string> approvedFiles,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CodeContext.Parser.Protocol.NativeSyntaxTreeResult> GetNativeSyntaxTreeAsync(string filePath, int? start = null, int? length = null,
            int maxDepth = 8, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private string Full(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// The sample with a worker and a selection policy, both shards reporting what they read: <paramref name="files"/>
    /// and <paramref name="absent"/> (root-relative) for the first, nothing more for the second.
    /// </summary>
    private ContextService WithReports(string[]? files = null, string[]? absent = null, bool secondReports = true)
        => ReportingSample(files, absent, secondReports).Service;

    private (GraphTestGraph Graph, ContextService Service) ReportingSample(string[]? files = null, string[]? absent = null, bool secondReports = true)
    {
        var graph = Sample();
        graph.Workers = new ManifestOnly();
        graph.Selector = new RepositoryFileSelector(Microsoft.Extensions.Options.Options.Create(new CodeContextOptions { RootPath = _root, IgnorePatterns = ["node_modules/**"] }));
        var service = graph.Build();
        graph.ReportInputs(
            new CodeContext.Parser.Protocol.WorkerInputs((files ?? []).Select(Full).ToList(), (absent ?? []).Select(Full).ToList()),
            secondReports ? new CodeContext.Parser.Protocol.WorkerInputs([], []) : null);
        return (graph, service);
    }

    private static string Modified(string path) => Diff(path, "1234567", "@@ -1 +1 @@\n-old\n+new");

    private static string Added(string path) => $"diff --git a/{path} b/{path}\nnew file mode 100644\nindex 0000000..1234567\n--- /dev/null\n+++ b/{path}\n@@ -0,0 +1 @@\n+new\n";

    private static string Deleted(string path) => $"diff --git a/{path} b/{path}\ndeleted file mode 100644\nindex 1234567..0000000\n--- a/{path}\n+++ /dev/null\n@@ -1 +0,0 @@\n-old\n";

    [Fact]
    public async Task AChangedFileNoWorkerReads_IsSetAside_AndDoesNotMakeTheAnswerIncomplete()
    {
        var service = WithReports();
        WriteFile("README.md", 3);
        WriteFile("docs/guide.md", 3);

        // Whether the working tree holds them as the diff has them does not matter: nothing indexed is read from them.
        var impact = await Impact(service,
            Modified("README.md") + Modified("docs/missing.md") + Added("docs/guide.md") + Deleted("docs/old.md")
            + "diff --git a/docs/logo.png b/docs/logo.png\nindex 1234567..89abcde 100644\nBinary files a/docs/logo.png and b/docs/logo.png differ\n"
            + Diff("src/Foo.cs", Blob("src/Foo.cs"), Replace(8)));

        Assert.Equal([I("Foo.Run")], impact.Changes.Select(change => change.Identifier));
        Assert.Empty(impact.Unmapped);
        Assert.True(impact.Complete);
        Assert.Equal(6, impact.DiffFiles);
        Assert.Equal(5, impact.SetAsideFileCount);
        Assert.Equal(["docs/guide.md", "docs/logo.png", "docs/missing.md", "docs/old.md", "README.md"], impact.SetAsideFiles);

        // The list is capped as every list is; the count is not.
        var capped = await Impact(service, Modified("README.md") + Modified("docs/missing.md") + Modified("docs/guide.md"), limit: 2);
        Assert.Equal((3, 2, true), (capped.SetAsideFileCount, capped.SetAsideFiles!.Count, capped.SetAsideFilesTruncated));
    }

    [Theory]
    // A worker parses its kind, or its manifest names it as a project file.
    [InlineData("src/Empty.cs", "modified", "parses files of its kind")]
    [InlineData("src/App.csproj", "modified", "project file")]
    [InlineData("Directory.Build.props", "added", "project file")]
    [InlineData("global.json", "deleted", "project file")]
    // What the host itself reads, or leaves out, and what packages follow.
    [InlineData(".gitignore", "modified", "ignore file")]
    [InlineData("web/node_modules/pkg/readme.md", "modified", "selection policy")]
    [InlineData("web/package-lock.json", "modified", "lock file")]
    // What a worker reported: read, looked for, looked for above it, or found by listing its directory.
    [InlineData("config/settings.json", "modified", "reported reading it")]
    [InlineData("config/wanted.json", "added", "looked for it")]
    [InlineData("vendor/lib/notes.md", "added", "looked for vendor")]
    [InlineData("listed/new.md", "added", "listing")]
    [InlineData("listed/gone.md", "deleted", "listing")]
    [InlineData("listed/removed/dir/gone.md", "deleted", "listing")]
    public async Task AFileThatMayFeedTheIndex_StaysNotIndexed_AndSaysWhy(string path, string status, string why)
    {
        var service = WithReports(files: ["config/settings.json", "listed"], absent: ["config/wanted.json", "vendor"]);
        Directory.CreateDirectory(Full("listed"));
        if (status != "deleted") WriteFile(path, 2);

        var impact = await Impact(service, status switch { "added" => Added(path), "deleted" => Deleted(path), _ => Modified(path) });

        var entry = Assert.Single(impact.Unmapped);
        Assert.True(entry.Reason is "notIndexed" or "contentMismatch" or "deletedFile", entry.Reason);
        if (entry.Reason == "notIndexed") Assert.Contains(why, entry.Detail);
        Assert.Equal(0, impact.SetAsideFileCount);
        Assert.Null(impact.SetAsideFiles);
        Assert.False(impact.Complete);
    }

    [Fact]
    public async Task AFileModifiedInAListedDirectory_IsSetAside_AsItsListingDidNotChange()
    {
        var service = WithReports(files: ["listed"]);
        WriteFile("listed/notes.md", 2);

        var impact = await Impact(service, Modified("listed/notes.md"));

        Assert.Equal(["listed/notes.md"], impact.SetAsideFiles);
        Assert.True(impact.Complete);
    }

    [Fact]
    public async Task ARenameFromOrToAFileAWorkerParses_ALink_AndASubmodule_AreNotSetAside()
    {
        var service = WithReports();
        WriteFile("docs/was-code.txt", 2);
        const string rename = "diff --git a/src/Gone.cs b/docs/was-code.txt\nsimilarity index 100%\nrename from src/Gone.cs\nrename to docs/was-code.txt\n";
        const string link = "diff --git a/docs/link.md b/docs/link.md\nnew file mode 120000\nindex 0000000..1234567\n--- /dev/null\n+++ b/docs/link.md\n@@ -0,0 +1 @@\n+../src\n";
        const string submodule = "diff --git a/vendored b/vendored\nindex 1234567..89abcde 160000\n--- a/vendored\n+++ b/vendored\n@@ -1 +1 @@\n-Subproject commit 1234567\n+Subproject commit 89abcde\n";

        var impact = await Impact(service, rename + link + submodule);

        Assert.Equal(0, impact.SetAsideFileCount);
        Assert.Equal(3, impact.UnmappedCount);
        Assert.False(impact.Complete);
    }

    [Fact]
    public async Task WhenAWorkerDoesNotReportWhatItReads_NothingIsSetAside()
    {
        // A worker need not report inputs: nothing then says a file is not among what it reads.
        var silent = WithReports(secondReports: false);
        WriteFile("README.md", 3);

        var impact = await Impact(silent, Modified("README.md"));

        var entry = Assert.Single(impact.Unmapped);
        Assert.Equal("notIndexed", entry.Reason);
        Assert.Contains("does not report what it reads", entry.Detail);
        Assert.Equal(0, impact.SetAsideFileCount);
        Assert.False(impact.Complete);

        // Nor without the workers and the selection policy to ask.
        var bare = Sample().Build();
        Assert.Equal("notIndexed", Assert.Single((await Impact(bare, Modified("README.md"))).Unmapped).Reason);
    }

    [Fact]
    public async Task AFileNoWorkerIsToldOf_IsNeverSaidToBeNotYetIndexed()
    {
        // It changed after the index last read the files, but no worker is told when it changes: retrying changes nothing.
        var graph = Sample(indexedAsOf: DateTime.UtcNow.AddHours(-1));
        graph.Workers = new ManifestOnly();
        graph.Selector = new RepositoryFileSelector(Microsoft.Extensions.Options.Options.Create(new CodeContextOptions { RootPath = _root }));
        var service = graph.Build();
        graph.ReportInputs(new CodeContext.Parser.Protocol.WorkerInputs([Full("config/settings.json")], []), new CodeContext.Parser.Protocol.WorkerInputs([], []));
        WriteFile("README.md", 3);
        WriteFile("config/settings.json", 3);

        var impact = await Impact(service, Modified("README.md") + Modified("config/settings.json"));

        Assert.Equal(["README.md"], impact.SetAsideFiles);
        Assert.Equal("notIndexed", Assert.Single(impact.Unmapped).Reason);
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
    public async Task AFileTheDiffNamesWithoutAChangedLine_IsReported_NotCountedAsChecked()
    {
        var service = Sample().Build();

        // A diff cut short after a file's header, or made with -w over a change in whitespace alone: the file differs
        // (its blob ids say so) and no changed line is shown.
        var cut = await Impact(service, Diff("src/Caller.cs", Blob("src/Caller.cs"), Replace(3))
            + $"diff --git a/src/Foo.cs b/src/Foo.cs\nindex 1234567..{Blob("src/Foo.cs")} 100644\n");
        Assert.Equal([I("Caller")], cut.Changes.Select(change => change.Identifier));
        var entry = Assert.Single(cut.Unmapped);
        Assert.Equal(("src/Foo.cs", "noChangedLines", 0), (entry.File, entry.Reason, entry.LineCount));
        Assert.False(cut.Complete);

        // An added file with content and no line shown is the same; an added empty file shows none and is whole.
        WriteText("src/Empty.txt", string.Empty);
        var added = await Impact(service, "diff --git a/src/New.cs b/src/New.cs\nnew file mode 100644\nindex 0000000..1234567\n"
            + "diff --git a/src/Empty.txt b/src/Empty.txt\nnew file mode 100644\nindex 0000000..e69de29\n");
        Assert.Equal(["src/Empty.txt:notIndexed", "src/New.cs:noChangedLines"], added.Unmapped.Select(unmapped => $"{unmapped.File}:{unmapped.Reason}"));
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
    public void AFileIsHashedAsItIsRead_AndCarriageReturnsAcrossReadsAreDropped()
    {
        // An 8 MB file with CRLF pairs straddling the 64 KB reads, and a lone CR that stays.
        var lf = new StringBuilder();
        while (lf.Length < 8 * 1024 * 1024) lf.Append(new string('x', 65_535 - lf.Length % 65_536)).Append('\n').Append("y\r z\n");
        var crlf = lf.ToString().Replace("\n", "\r\n");
        var path = Path.Combine(_root, "big.cs");
        File.WriteAllText(path, crlf);
        var lfBytes = Encoding.UTF8.GetBytes(lf.ToString());
        var tree = new DiffWorkingTree(_root, indexedAsOf: null);

        Assert.Equal(DiffWorkingTree.Verdict.Match, tree.Inspect(path, DiffWorkingTree.BlobId(lfBytes, sha256: true)[..12]).Verdict);
        var fresh = new DiffWorkingTree(_root, indexedAsOf: null);
        var asWritten = DiffWorkingTree.BlobId(Encoding.UTF8.GetBytes(crlf), sha256: false);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var verdict = fresh.Inspect(path, asWritten).Verdict;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(DiffWorkingTree.Verdict.Match, verdict);
        Assert.Equal(DiffWorkingTree.Verdict.Mismatch, fresh.Inspect(path, "deadbeef").Verdict);
        // Not the file's size: it is never held whole.
        Assert.True(allocated < 1024 * 1024, $"{allocated:N0} bytes allocated to hash an 8 MB file");
    }

    [Fact]
    public async Task ABlobIdOfASha256Repository_IsCheckedToo()
    {
        var service = Sample().Build();
        var content = File.ReadAllBytes(Path.Combine(_root, "src", "Foo.cs"));

        var full = await Impact(service, Diff("src/Foo.cs", DiffWorkingTree.BlobId(content, sha256: true), Replace(8)));
        var abbreviated = await Impact(service, Diff("src/Foo.cs", DiffWorkingTree.BlobId(content, sha256: true)[..12], Replace(8)));

        Assert.Equal((1, true), (full.ContentChecked, full.Complete));
        Assert.Equal((1, true), (abbreviated.ContentChecked, abbreviated.Complete));
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
    public void AFileRewrittenBetweenTheTimeCheckAndTheHash_IsNotTakenForTheOneTheIndexRead()
    {
        // The index read the old content; the diff's new side is written while the file is being checked.
        WriteText("src/Raced.cs", "old\n");
        var path = Path.Combine(_root, "src", "Raced.cs");
        var newBlob = DiffWorkingTree.BlobId(Encoding.UTF8.GetBytes("new\n"), sha256: false);
        var tree = new DiffWorkingTree(_root, indexedAsOf: DateTime.UtcNow.AddSeconds(1))
        {
            BeforeHashForTests = written =>
            {
                // Later than any time a file system could have given the first write, coarse clocks included.
                Thread.Sleep(50);
                File.WriteAllText(written, "new\n");
            },
        };

        Assert.Equal(DiffWorkingTree.Verdict.ChangedSinceIndexed, tree.Inspect(path, newBlob).Verdict);

        // Left alone, the same file with the same times is the diff's new side.
        Assert.Equal(DiffWorkingTree.Verdict.Match, new DiffWorkingTree(_root, DateTime.UtcNow.AddSeconds(1)).Inspect(path, newBlob).Verdict);
    }

    [Fact]
    public async Task AFileTheIndexFailedToRead_OrIsStillReading_IsNotMapped()
    {
        // Its blob id matches and its time is old, but the worker's last read of it failed: the index holds what it held before.
        var files = new CodeContext.Core.Repositories.InMemoryFileMetadataRepository();
        var graph = Sample();
        graph.Files = files;
        var service = graph.Build();
        var diff = Diff("src/Foo.cs", Blob("src/Foo.cs"), Replace(8));
        var path = Path.Combine(_root, "src", "Foo.cs");

        await files.UpsertAsync(new CodeContext.Core.Models.FileMetadata { FilePath = path, Status = CodeContext.Core.Models.FileProcessingStatus.Failed, ErrorMessage = "worker exited" });
        var failed = await Impact(service, diff);
        Assert.Empty(failed.Changes);
        var entry = Assert.Single(failed.Unmapped);
        Assert.Equal("notYetIndexed", entry.Reason);
        Assert.Contains("worker exited", entry.Detail);
        Assert.False(failed.Complete);

        await files.UpsertAsync(new CodeContext.Core.Models.FileMetadata { FilePath = path, Status = CodeContext.Core.Models.FileProcessingStatus.Processing });
        Assert.Equal("notYetIndexed", Assert.Single((await Impact(service, diff)).Unmapped).Reason);

        await files.UpsertAsync(new CodeContext.Core.Models.FileMetadata { FilePath = path, Status = CodeContext.Core.Models.FileProcessingStatus.Completed });
        Assert.True((await Impact(service, diff)).Complete);
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
        // Reached from both at the same distance: the change listed first (file and line order) has it, whatever the ids.
        Assert.Equal(I("Foo.Run"), seeds["Shared"]);
        // Every listed dependent is attributed to exactly one change.
        Assert.Equal(impact.TotalCount, impact.Changes.Sum(change => change.Dependents));
        Assert.Contains("RunTest", impact.Items.Select(item => item.Name));
    }

    [Fact]
    public async Task AChangeThatAnotherChangeUses_SaysSo_ThoughNoChangeIsListedAsADependent()
    {
        var service = Sample().Build();

        // Run changes, and so does Caller, which calls it: Caller is a change, not a dependent, so Run lists nothing.
        var impact = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"), Replace(8)) + Diff("src/Caller.cs", Blob("src/Caller.cs"), Replace(3)));

        var changes = impact.Changes.ToDictionary(change => change.Name!);
        Assert.Equal(0, changes["Run"].Dependents);
        Assert.Equal(1, changes["Run"].UsedByChangeCount);
        Assert.Equal([I("Caller")], changes["Run"].UsedByChanges);
        // Caller is used by Far, which did not change: an ordinary dependent, and no change uses Caller.
        Assert.Equal((1, 0), (changes["Caller"].Dependents, changes["Caller"].UsedByChangeCount));
        Assert.Null(changes["Caller"].UsedByChanges);
        Assert.DoesNotContain("Caller", impact.Items.Select(item => item.Name));

        // A relation filter that leaves the call out leaves the use out, as it leaves a dependent out.
        var filtered = await Impact(service,
            Diff("src/Foo.cs", Blob("src/Foo.cs"), Replace(8)) + Diff("src/Caller.cs", Blob("src/Caller.cs"), Replace(3)), relation: "REFERENCES");
        Assert.All(filtered.Changes, change => Assert.Null(change.UsedByChanges));
    }

    [Fact]
    public async Task AChangeUsedThroughADeclarationItImplements_OrInATestFile_SaysSoToo_AndTheListIsBounded()
    {
        // ThroughInterface calls IFoo.Run, which Foo.Run (by way of Base.Run) and Bar.Run implement; all three change, with a test's stub.
        var service = Dispatching().Build();
        string One(string file) => Diff(file, Blob(file), Replace(3));

        var impact = await Impact(service, One("src/Foo.cs") + One("src/Bar.cs") + One("src/A.cs") + One("src/D.cs") + One("tests/StubTests.cs") + One("tests/StubFoo.cs"));

        var changes = impact.Changes.ToDictionary(change => change.Identifier);
        // Foo.Run: Refers references it, ThroughInterface reaches it through IFoo.Run.
        Assert.Equal([I("ThroughInterface"), I("Refers")], changes[I("Foo.Run")].UsedByChanges);
        // Bar.Run implements the same declaration: the same caller, though the walk took that declaration's callers once.
        Assert.Equal([I("ThroughInterface")], changes[I("Bar.Run")].UsedByChanges);
        // A change in a test file that uses another is named as any other, tests listed or not.
        Assert.Contains(I("StubTests.Check"), changes[I("StubFoo.Run")].UsedByChanges!);

        // Seven changed callers of one changed member: the first five in file order, and the count of all.
        var graph = Sample();
        var diff = new StringBuilder(Diff("src/Foo.cs", Blob("src/Foo.cs"), Replace(8)));
        for (var caller = 0; caller < 7; caller++)
        {
            WriteFile($"src/C{caller}.cs", 3);
            graph.Node($"C{caller}", file: $"src/C{caller}.cs", line: 0, endLine: 2).Edge($"C{caller}", "Foo.Run");
            diff.Append(Diff($"src/C{caller}.cs", Blob($"src/C{caller}.cs"), Replace(2)));
        }
        var many = (await Impact(graph.Build(), diff.ToString())).Changes.Single(change => change.Name == "Run");
        Assert.Equal(7, many.UsedByChangeCount);
        Assert.Equal(Enumerable.Range(0, 5).Select(caller => I($"C{caller}")), many.UsedByChanges);
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
    public async Task ACancelledRequest_IsNotAnswered()
    {
        var service = Sample().Build();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetImpactAsync(
            new GraphImpactRequest { Diff = Diff("src/Foo.cs", Blob("src/Foo.cs"), Replace(8)) }, cancelled.Token));
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

    /// <summary>A file table that runs a step before the first file it is asked about, then answers as the store does.</summary>
    private sealed class SteppingFiles(Action beforeFirstRead) : CodeContext.Core.Repositories.IFileMetadataRepository
    {
        private readonly CodeContext.Core.Repositories.InMemoryFileMetadataRepository _inner = new();
        private int _reads;

        public Task<FileMetadata?> GetByFilePathAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 1) beforeFirstRead();
            return _inner.GetByFilePathAsync(filePath, cancellationToken);
        }

        public Task<IEnumerable<FileMetadata>> GetAllAsync(CancellationToken cancellationToken = default) => _inner.GetAllAsync(cancellationToken);
        public Task<IEnumerable<FileMetadata>> GetByStatusAsync(FileProcessingStatus status, CancellationToken cancellationToken = default)
            => _inner.GetByStatusAsync(status, cancellationToken);
        public Task UpsertAsync(FileMetadata metadata, CancellationToken cancellationToken = default) => _inner.UpsertAsync(metadata, cancellationToken);
        public Task UpsertBatchAsync(IEnumerable<FileMetadata> metadataList, CancellationToken cancellationToken = default)
            => _inner.UpsertBatchAsync(metadataList, cancellationToken);
        public Task DeleteAsync(string filePath, CancellationToken cancellationToken = default) => _inner.DeleteAsync(filePath, cancellationToken);
        public Task ClearAsync(CancellationToken cancellationToken = default) => _inner.ClearAsync(cancellationToken);
        public Task<int> GetCountByStatusAsync(FileProcessingStatus status, CancellationToken cancellationToken = default)
            => _inner.GetCountByStatusAsync(status, cancellationToken);
        public Task<bool> NeedsProcessingAsync(string filePath, DateTime lastModified, CancellationToken cancellationToken = default)
            => _inner.NeedsProcessingAsync(filePath, lastModified, cancellationToken);
    }

    [Fact]
    public async Task ACommitPublishedButNotYetCounted_IsNeverMixedIntoAnAnswer()
    {
        // As for affected (GraphGenerationTests): a commit publishes its map and stops before bumping the store's version,
        // and another reader caches the new map under the old version, so the answer's own version check cannot see it.
        // It lands after the file's declarations were read and before they are walked: both must be one generation.
        // In the next one Run keeps its identifier and span and changes its id, and Fresh is its only caller.
        WriteFile("src/Other.cs", 5);
        var next = new GraphTestGraph(_root)
            .Node("Foo", "Class", file: "src/Foo.cs", line: 2, endLine: 27)
            .Node("Foo.Run", file: "src/Foo.cs", line: 5, endLine: 11, id: "csharp:b:Foo.Run#2")
            .Node("Foo.Other", file: "src/Foo.cs", line: 15, endLine: 23)
            .Node("Fresh", file: "src/Other.cs", line: 1, endLine: 2)
            .Edge("Foo", "Foo.Run", "HAS_METHOD", targetId: "csharp:b:Foo.Run#2").Edge("Foo", "Foo.Other", "HAS_METHOD")
            .Edge("Fresh", "Foo.Run", targetId: "csharp:b:Foo.Run#2");
        var graph = Sample();
        var database = graph.Database;

        using var otherReaderMissed = new ManualResetEventSlim();
        using var otherReaderGo = new ManualResetEventSlim();
        using var published = new ManualResetEventSlim();
        using var writerGo = new ManualResetEventSlim();
        Thread? otherReaderThread = null;
        Thread? writerThread = null;
        graph.Files = new SteppingFiles(() =>
        {
            writerThread = new Thread(() =>
            {
                var (nodes, edges) = next.Facts;
                database.TryCommitGeneration(database.LastCommittedGeneration + 1, nodes, edges, scope: null);
            })
            { IsBackground = true };
            writerThread.Start();
            Assert.True(published.Wait(TimeSpan.FromSeconds(30)));
            otherReaderGo.Set();
            Assert.True(otherReaderThread!.Join(TimeSpan.FromSeconds(30)));
        });
        var service = graph.Build();
        database.AdjacencyMissForTests = () =>
        {
            if (Thread.CurrentThread != otherReaderThread) return;
            otherReaderMissed.Set();
            otherReaderGo.Wait(TimeSpan.FromSeconds(30));
        };
        database.AfterPublishForTests = () =>
        {
            published.Set();
            writerGo.Wait(TimeSpan.FromSeconds(30));
        };
        otherReaderThread = new Thread(() => database.GetAdjacency()) { IsBackground = true };
        otherReaderThread.Start();
        Assert.True(otherReaderMissed.Wait(TimeSpan.FromSeconds(30)));

        try
        {
            var impact = await Impact(service, Diff("src/Foo.cs", Blob("src/Foo.cs"), Replace(8)));
            Assert.Equal(I("Foo.Run"), Assert.Single(impact.Changes).Identifier);
            // Run's declaration read in one generation and walked in the other has no users.
            var users = string.Join(",", impact.Items.Select(item => item.Name));
            Assert.True(users is "Caller,Far" or "Fresh", $"An answer mixed generations: [{users}].");
        }
        finally
        {
            writerGo.Set();
            otherReaderGo.Set();
            writerThread?.Join(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public async Task AnAnswerInsideAPinnedRead_TakesWhatTheWorkersReportedFromThatGeneration_NotALaterOne()
    {
        // An outer reader holds generation 1, in which no worker reported reading README.md. Generation 2 replaces the
        // facts, and its worker reports reading README.md. The answer is generation 1's: its declarations, its walk, and
        // no report of generation 2 given as the reason for anything.
        var (graph, service) = ReportingSample();
        WriteFile("README.md", 3);
        var database = graph.Database;
        var diff = Modified("README.md") + Diff("src/Foo.cs", Blob("src/Foo.cs"), Replace(8));

        GraphImpactResponse impact;
        using (database.PinPublished())
        {
            // Generation 2: Caller is gone, and the first shard's worker now reports reading README.md.
            var (nodes, edges) = graph.Facts;
            Assert.True(database.TryCommitGeneration(database.LastCommittedGeneration + 1,
                nodes.Where(node => node.Name != "Caller").ToList(), edges.Where(edge => edge.SourceId != GraphTestGraph.Id("Caller")).ToList(),
                scope: null));
            graph.ReportInputs(new CodeContext.Parser.Protocol.WorkerInputs([Full("README.md")], []), new CodeContext.Parser.Protocol.WorkerInputs([], []));

            impact = await Impact(service, diff);
        }

        Assert.Equal(["Caller", "Far"], impact.Items.Select(item => item.Name));
        var readme = Assert.Single(impact.Unmapped);
        Assert.Equal(("README.md", "notIndexed"), (readme.File, readme.Reason));
        Assert.DoesNotContain("reported reading it", readme.Detail);
        Assert.Contains("not known", readme.Detail);

        // Unpinned, the answer is the newest generation's, its reports included.
        var now = await Impact(service, diff);
        Assert.Empty(now.Items);
        Assert.Contains("reported reading it", Assert.Single(now.Unmapped).Detail);
    }
}