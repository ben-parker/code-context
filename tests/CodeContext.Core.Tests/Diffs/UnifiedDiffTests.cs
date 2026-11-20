using CodeContext.Core.Diffs;

namespace CodeContext.Core.Tests.Diffs;

/// <summary>The diff reader on hand-written diffs: what it keeps, and what it refuses rather than reads in part.</summary>
public class UnifiedDiffTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    [Fact]
    public void AModifiedFile_KeepsTheAddedLinesAndTheRemovedRunsWithTheLinesAroundThem()
    {
        var files = UnifiedDiff.Parse(Lines(
            "diff --git a/src/Foo.cs b/src/Foo.cs",
            "index de98044..7be73ce 100644",
            "--- a/src/Foo.cs",
            "+++ b/src/Foo.cs",
            "@@ -10,6 +10,7 @@ class Foo",
            " context 10",
            "-old 11",
            "+new 11",
            "+new 12",
            " context 13 (old 12)",
            "-old 13",
            " context 14 (old 14)",
            "+",
            " context 16 (old 15)"));

        var file = Assert.Single(files);
        Assert.Equal(("src/Foo.cs", "src/Foo.cs"), (file.OldPath, file.NewPath));
        Assert.Equal(DiffFileStatus.Modified, file.Status);
        Assert.Equal(("de98044", "7be73ce"), (file.OldBlob, file.NewBlob));
        Assert.Equal([new DiffLineRange(11, 12)], file.Added);
        Assert.Equal(
            [new DiffRemoval(new DiffLineRange(11, 11), 10, 13), new DiffRemoval(new DiffLineRange(13, 13), 13, 14)],
            file.Removals);
        // The blank added line is not a change of anything on its own.
        Assert.Equal(1, file.IgnoredLines);
        Assert.Equal((3, 2), (file.AddedLineCount, file.RemovedLineCount));
    }

    [Theory]
    [InlineData("// a comment", true)]
    [InlineData("    /// <summary>Doc.</summary>", true)]
    [InlineData("/* block", true)]
    [InlineData(" * inside a block", true)]
    [InlineData(" *", true)]
    [InlineData(" */", true)]
    [InlineData("#region Helpers", true)]
    [InlineData("    #endregion", true)]
    [InlineData("var x = 1; // trailing", false)]
    [InlineData("*pointer = 1;", false)]
    [InlineData("#if DEBUG", false)]
    [InlineData("[Obsolete]", false)]
    // A comment that ends on the line is a comment line only when nothing but comments follows it.
    [InlineData("/* api */ public sealed class User", false)]
    [InlineData("    /* go */ helper();", false)]
    [InlineData(" */ run();", false)]
    [InlineData(" * last */ run();", false)]
    [InlineData("/* one */ /* two */", true)]
    [InlineData("/* one */ // two", true)]
    [InlineData("/* one */   ", true)]
    [InlineData(" * closes */", true)]
    [InlineData("/*/", true)]
    public void ACommentLine_IsToldApartByItsStart(string line, bool comment)
        => Assert.Equal(comment, SourceLines.IsCommentLine(line));

    [Fact]
    public void AMangledDiff_IsReadOrRefused_AndNothingElse()
    {
        // Real shapes of diff, then cut, spliced, doubled and overwritten at random: every outcome is a list of files or
        // an ArgumentException that says where, never another exception and never a file without a path.
        string[] samples =
        [
            Lines("diff --git a/src/Foo.cs b/src/Foo.cs", "index de98044..7be73ce 100644", "--- a/src/Foo.cs", "+++ b/src/Foo.cs",
                "@@ -10,6 +10,7 @@ class Foo", " context", "-old", "+new", "+// note", " context", "-/* gone", " context", "+", " context",
                "@@ -30,2 +31,3 @@", " a", "+#if DEBUG", " b", "\\ No newline at end of file"),
            Lines("diff --git a/old name.txt b/new name.txt", "similarity index 90%", "rename from old name.txt", "rename to new name.txt",
                "index 1111111..2222222 100644", "--- a/old name.txt", "+++ b/new name.txt", "@@ -1 +1 @@", "-a", "+b"),
            Lines("diff --git \"a/caf\\303\\251.cs\" \"b/caf\\303\\251.cs\"", "new file mode 100644", "index 0000000..3333333", "--- /dev/null",
                "+++ \"b/caf\\303\\251.cs\"", "@@ -0,0 +1,3 @@", "+using System;", "+namespace N;", "+class C { }"),
            Lines("diff --git a/img.png b/img.png", "index 4444444..5555555 100644", "GIT binary patch", "literal 3", "Kc$}6X", "",
                "diff --git a/gone.cs b/gone.cs", "deleted file mode 100644", "index 6666666..0000000", "--- a/gone.cs", "+++ /dev/null",
                "@@ -1,2 +0,0 @@", "-a", "-b", "diff --git a/run.sh b/run.sh", "old mode 100644", "new mode 100755"),
            "--- a/plain.c\t2026-01-01 00:00:00\r\n+++ b/plain.c\t2026-01-02 00:00:00\r\n@@ -1,2 +1,2 @@\r\n one\r\n-two\r\n+deux\r\n",
            Lines("diff --cc src/Merged.cs", "index 1111111,2222222..3333333", "--- a/src/Merged.cs", "+++ b/src/Merged.cs", "@@@ -1,2 -1,2 +1,2 @@@", "  a", "- b", " +c"),
        ];
        var random = new Random(20261120);
        var read = 0;
        var refused = 0;
        for (var round = 0; round < 30_000; round++)
        {
            var text = new System.Text.StringBuilder(samples[random.Next(samples.Length)]);
            for (var edits = 1 + random.Next(3); edits > 0 && text.Length > 0; edits--)
            {
                var at = random.Next(text.Length);
                switch (random.Next(6))
                {
                    case 0: text.Length = at; break;
                    case 1: text.Remove(at, Math.Min(text.Length - at, 1 + random.Next(12))); break;
                    case 2: text.Insert(at, samples[random.Next(samples.Length)].AsSpan(0, random.Next(40))); break;
                    case 3: text[at] = "@+- \\\n\r\"0,/\t\0\u00e9"[random.Next(14)]; break;
                    case 4: text.Insert(at, text.ToString(at, Math.Min(text.Length - at, random.Next(60)))); break;
                    default: text.Insert(at, random.Next(2) == 0 ? "\n@@ -1 +1 @@\n" : "\ndiff --git a/x b/x\n"); break;
                }
            }
            try
            {
                var files = UnifiedDiff.Parse(text.ToString());
                Assert.All(files, file => Assert.NotNull(file.Path));
                read++;
            }
            catch (ArgumentException exception)
            {
                Assert.False(string.IsNullOrWhiteSpace(exception.Message));
                refused++;
            }
        }
        // Both outcomes are common: the mutations are neither all harmless nor all fatal.
        Assert.True(read > 3_000 && refused > 3_000, $"{read} read, {refused} refused");
    }

    [Theory]
    [InlineData("#if DEBUG", true)]
    [InlineData("  #elif NET8_0_OR_GREATER", true)]
    [InlineData("#else", true)]
    [InlineData("#  endif // DEBUG", true)]
    [InlineData("#define TRACE", true)]
    [InlineData("#undef TRACE", true)]
    [InlineData("#ifdef __linux__", true)]
    [InlineData("#ifndef GUARD_H", true)]
    [InlineData("#region Helpers", false)]
    [InlineData("#pragma warning disable CS0168", false)]
    [InlineData("#nullable enable", false)]
    [InlineData("#include <stdio.h>", false)]
    [InlineData("#iffy", false)]
    [InlineData("#", false)]
    [InlineData("x #if", false)]
    public void AConditionalDirective_IsToldApartByItsName(string line, bool conditional)
        => Assert.Equal(conditional, SourceLines.IsConditionalDirective(line));

    [Fact]
    public void AHunkThatGoesBackOverLinesAnEarlierOneCovered_IsRefused()
    {
        var header = "diff --git a/a.cs b/a.cs\n--- a/a.cs\n+++ b/a.cs\n";
        // In order, and touching: read.
        Assert.Single(UnifiedDiff.Parse(header + "@@ -5 +5 @@\n-a\n+b\n@@ -6,0 +7 @@\n+c\n@@ -7 +8 @@\n-d\n+e\n"));

        var backwards = Assert.Throws<ArgumentException>(() => UnifiedDiff.Parse(header + "@@ -5 +5 @@\n-a\n+b\n@@ -2 +2 @@\n-c\n+d\n"));
        Assert.Contains("diff line 7", backwards.Message);
        Assert.Contains("before the hunk above it ends", backwards.Message);
        // The same line twice is two changes of one state, not one diff.
        Assert.Throws<ArgumentException>(() => UnifiedDiff.Parse(header + "@@ -5 +5 @@\n-a\n+b\n@@ -5 +5 @@\n-a\n+b\n"));
        Assert.Throws<ArgumentException>(() => UnifiedDiff.Parse(header + "@@ -5,0 +6,2 @@\n+a\n+b\n@@ -6 +7 @@\n-c\n+d\n"));
    }

    [Theory]
    [InlineData("using System;", true)]
    [InlineData("  using static System.Math; // why", true)]
    [InlineData("using Map = System.Collections.Generic.Dictionary<string, int>;", true)]
    [InlineData("namespace Demo.Core;", true)]
    [InlineData("namespace Demo {", true)]
    [InlineData("}", true)]
    [InlineData("#nullable enable", true)]
    [InlineData("#pragma warning disable CS1591", true)]
    [InlineData("global using System.IO;", false)]
    [InlineData("using (var stream = Open())", false)]
    [InlineData("using var stream = Open();", false)]
    [InlineData("[assembly: InternalsVisibleTo(\"Tests\")]", false)]
    [InlineData("#define TRACE", false)]
    public void AFileScopedDirective_IsToldApartFromWhatBindsBeyondTheFile(string line, bool scoped)
        => Assert.Equal(scoped, SourceLines.IsFileScopedDirective(line));

    [Fact]
    public void AddedCommentLines_AreKeptApart_AndARemovedRunOfCommentsIsMarked()
    {
        var file = Assert.Single(UnifiedDiff.Parse(Lines(
            "--- a/a.cs",
            "+++ b/a.cs",
            "@@ -1,5 +1,4 @@",
            "-/// old",
            "+/// new",
            "+int X;",
            " a",
            "-int Y;",
            "-// why",
            " b")));

        Assert.Equal([new DiffLineRange(1, 1)], file.CommentAdded);
        Assert.Equal([new DiffLineRange(2, 2)], file.Added);
        Assert.Equal([true, false], file.Removals.Select(removal => removal.CommentsOnly));
    }

    [Fact]
    public void ABlockCommentOpenedOrClosedAroundUnchangedLines_IsMarked_AndAWholeDocCommentIsNot()
    {
        var file = Assert.Single(UnifiedDiff.Parse(Lines(
            "--- a/a.cs",
            "+++ b/a.cs",
            "@@ -1,10 +1,13 @@",
            " class A {",
            "+/*",
            "     void Gone() {}",
            "+*/",
            "     void Mid() {}",
            "+    /**",
            "+     * A whole doc comment.",
            "+     */",
            "     void Kept() {}",
            "-    /** Old summary",
            "+    /** New summary",
            "      */",
            "-/*",
            "     void Back() {}",
            "-*/",
            " }")));

        // Lines 2 and 4 open and close a comment around line 3; the doc comment at 6-8 and the edited summary at 10 close
        // within what they change (or change nothing about where a comment ends).
        Assert.Equal([new DiffLineRange(2, 2), new DiffLineRange(4, 4)], file.CommentBoundaries);
        Assert.Equal([new DiffLineRange(2, 2), new DiffLineRange(4, 4), new DiffLineRange(6, 8), new DiffLineRange(10, 10)], file.CommentAdded);
        // Removing the markers around Back brings it back: those removals are not comments only.
        Assert.Equal([true, false, false], file.Removals.Select(removal => removal.CommentsOnly));
    }

    [Fact]
    public void ZeroContextHunks_PlaceAPureRemovalBetweenTheNewLinesAroundIt()
    {
        // git diff -U0: a side with no lines names the line before the hunk.
        var file = Assert.Single(UnifiedDiff.Parse(Lines(
            "diff --git a/a.ts b/a.ts",
            "--- a/a.ts",
            "+++ b/a.ts",
            "@@ -5,2 +4,0 @@",
            "-gone 5",
            "-gone 6",
            "@@ -20,0 +19,2 @@",
            "+added 20",
            "+added 21")));

        Assert.Equal([new DiffRemoval(new DiffLineRange(5, 6), 4, 5)], file.Removals);
        Assert.Equal([new DiffLineRange(19, 20)], file.Added);
    }

    [Fact]
    public void AddedDeletedRenamedCopiedAndModeOnlyFiles_AreToldApart()
    {
        var files = UnifiedDiff.Parse(Lines(
            "diff --git a/new.cs b/new.cs",
            "new file mode 100644",
            "index 0000000..3e75765",
            "--- /dev/null",
            "+++ b/new.cs",
            "@@ -0,0 +1,2 @@",
            "+class New",
            "+{}",
            "diff --git a/gone.cs b/gone.cs",
            "deleted file mode 100644",
            "index 3e75765..0000000",
            "--- a/gone.cs",
            "+++ /dev/null",
            "@@ -1 +0,0 @@",
            "-class Gone {}",
            "diff --git a/old/Name.cs b/new/Name.cs",
            "similarity index 100%",
            "rename from old/Name.cs",
            "rename to new/Name.cs",
            "diff --git a/src/A.cs b/src/B.cs",
            "similarity index 90%",
            "copy from src/A.cs",
            "copy to src/B.cs",
            "index 1111111..2222222 100644",
            "--- a/src/A.cs",
            "+++ b/src/B.cs",
            "@@ -1 +1 @@",
            "-class A {}",
            "+class B {}",
            "diff --git a/run.sh b/run.sh",
            "old mode 100644",
            "new mode 100755"));

        Assert.Equal(
            [
                (DiffFileStatus.Added, (string?)null, (string?)"new.cs"),
                (DiffFileStatus.Deleted, "gone.cs", null),
                (DiffFileStatus.Renamed, "old/Name.cs", "new/Name.cs"),
                (DiffFileStatus.Copied, "src/A.cs", "src/B.cs"),
                (DiffFileStatus.Modified, "run.sh", "run.sh"),
            ],
            files.Select(file => (file.Status, file.OldPath, file.NewPath)));
        Assert.Null(files[0].OldBlob);
        Assert.Equal("3e75765", files[0].NewBlob);
        Assert.Null(files[1].NewBlob);
        Assert.False(files[2].HasHunks);
        Assert.True(files[4].ModeOnly);
        Assert.Equal("100755", files[4].NewMode);
        Assert.False(files[3].ModeOnly);
    }

    [Fact]
    public void BinaryChanges_AreMarked_AndTheirPatchIsNotReadAsText()
    {
        var files = UnifiedDiff.Parse(Lines(
            "diff --git a/logo.png b/logo.png",
            "index 1111111..2222222 100644",
            "GIT binary patch",
            "literal 5",
            "McmZQzWMW|V000310RR91",
            "",
            "literal 3",
            "KcmZQzWB>pF",
            "",
            "diff --git a/icon.ico b/icon.ico",
            "index 3333333..4444444 100644",
            "Binary files a/icon.ico and b/icon.ico differ",
            "diff --git a/x.cs b/x.cs",
            "--- a/x.cs",
            "+++ b/x.cs",
            "@@ -1 +1 @@",
            "-a",
            "+b"));

        Assert.Equal([true, true, false], files.Select(file => file.Binary));
        Assert.Equal(["logo.png", "icon.ico", "x.cs"], files.Select(file => file.Path));
        Assert.Equal([new DiffLineRange(1, 1)], files[2].Added);
    }

    [Fact]
    public void QuotedPaths_AreUnescaped_AndNonAsciiPathsReadAsUtf8()
    {
        var files = UnifiedDiff.Parse(Lines(
            "diff --git \"a/sp ace \\303\\251.cs\" \"b/sp ace \\303\\251.cs\"",
            "--- \"a/sp ace \\303\\251.cs\"",
            "+++ \"b/sp ace \\303\\251.cs\"",
            "@@ -1 +1 @@",
            "-a",
            "+b",
            "diff --git a/café.ts b/café.ts",
            "--- a/café.ts",
            "+++ b/café.ts",
            "@@ -1 +1 @@",
            "-a",
            "+b",
            "diff --git a/with space.cs b/with space.cs",
            "--- a/with space.cs\t",
            "+++ b/with space.cs\t",
            "@@ -1 +1 @@",
            "-a",
            "+b"));

        Assert.Equal(["sp ace é.cs", "café.ts", "with space.cs"], files.Select(file => file.Path));
        Assert.All(files, file => Assert.Null(file.PathProblem));
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("src/../../outside.cs")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("src//double.cs")]
    public void APathThatLeavesTheRootOrIsAbsolute_IsMarkedNotFollowed(string path)
    {
        var file = Assert.Single(UnifiedDiff.Parse(Lines(
            $"--- {path}",
            $"+++ {path}",
            "@@ -1 +1 @@",
            "-a",
            "+b")));

        Assert.NotNull(file.PathProblem);
    }

    [Fact]
    public void APathWithAControlCharacterOrInvalidUtf8_IsMarked()
    {
        var files = UnifiedDiff.Parse(Lines(
            "--- \"a/new\\nline.cs\"",
            "+++ \"b/new\\nline.cs\"",
            "@@ -1 +1 @@",
            "-a",
            "+b",
            "--- \"a/bad\\377.cs\"",
            "+++ \"b/bad\\377.cs\"",
            "@@ -1 +1 @@",
            "-a",
            "+b"));

        Assert.Contains("control", files[0].PathProblem);
        Assert.Contains("UTF-8", files[1].PathProblem);
    }

    [Fact]
    public void ADiffWrittenWithWindowsLineEndings_ReadsTheSame()
    {
        var unix = Lines(
            "diff --git a/a.cs b/a.cs",
            "index 1111111..2222222 100644",
            "--- a/a.cs",
            "+++ b/a.cs",
            "@@ -1,3 +1,3 @@",
            " a",
            "-b",
            "+B",
            " c");

        var lf = Assert.Single(UnifiedDiff.Parse(unix));
        var crlf = Assert.Single(UnifiedDiff.Parse(unix.Replace("\n", "\r\n")));

        Assert.Equal(lf.Path, crlf.Path);
        Assert.Equal(lf.NewBlob, crlf.NewBlob);
        Assert.Equal(lf.Added, crlf.Added);
        Assert.Equal(lf.Removals, crlf.Removals);
    }

    [Fact]
    public void PlainDiffU_WithTimestamps_AndAFormatPatchMail_AreRead()
    {
        var plain = Assert.Single(UnifiedDiff.Parse(Lines(
            "--- src/a.cs\t2026-11-20 10:00:00.000000000 +0000",
            "+++ src/a.cs\t2026-11-20 10:01:00.000000000 +0000",
            "@@ -1 +1 @@",
            "-a",
            "+b")));
        Assert.Equal("src/a.cs", plain.Path);

        var mail = Assert.Single(UnifiedDiff.Parse(Lines(
            "From 45e04e4 Mon Sep 17 00:00:00 2001",
            "Subject: [PATCH] Change a",
            "",
            "--- a message line that looks like a header",
            "---",
            " a.cs | 2 +-",
            "",
            "diff --git a/a.cs b/a.cs",
            "index 1111111..2222222 100644",
            "--- a/a.cs",
            "+++ b/a.cs",
            "@@ -1,2 +1,2 @@",
            "-a",
            "--- a removed line that starts with dashes",
            "+b",
            "+c",
            "-- ",
            "2.43.0")));
        Assert.Equal("a.cs", mail.Path);
        Assert.Equal([new DiffLineRange(1, 2)], mail.Added);
        Assert.Equal(2, mail.RemovedLineCount);
    }

    [Fact]
    public void MnemonicPrefixes_AreRemovedLikeAAndB()
    {
        var file = Assert.Single(UnifiedDiff.Parse(Lines(
            "diff --git i/src/a.cs w/src/a.cs",
            "--- i/src/a.cs",
            "+++ w/src/a.cs",
            "@@ -1 +1 @@",
            "-a",
            "+b")));
        Assert.Equal("src/a.cs", file.Path);
    }

    [Fact]
    public void ANoNewlineMarker_IsCountedByNeitherSide()
    {
        var file = Assert.Single(UnifiedDiff.Parse(Lines(
            "--- a/a.cs",
            "+++ b/a.cs",
            "@@ -1 +1 @@",
            "-a",
            "\\ No newline at end of file",
            "+b",
            "\\ No newline at end of file")));
        Assert.Equal([new DiffLineRange(1, 1)], file.Added);
    }

    [Fact]
    public void ACombinedMergeDiff_IsMarked_AndItsHunksAreNotRead()
    {
        var file = Assert.Single(UnifiedDiff.Parse(Lines(
            "diff --cc src/a.cs",
            "index 1111111,2222222..3333333",
            "--- a/src/a.cs",
            "+++ b/src/a.cs",
            "@@@ -1,1 -1,1 +1,1 @@@",
            "- a",
            " -b",
            "++c")));
        Assert.True(file.Combined);
        Assert.Equal("src/a.cs", file.Path);
        Assert.Empty(file.Added);
    }

    [Theory]
    [InlineData("a hunk that ends before its counts", "--- a/a.cs\n+++ b/a.cs\n@@ -1,3 +1,3 @@\n a\n-b\n", "ends early")]
    [InlineData("a line that is not part of a hunk", "--- a/a.cs\n+++ b/a.cs\n@@ -1,2 +1,2 @@\n a\n?b\n+c\n", "not context")]
    [InlineData("a hunk with no file", "@@ -1 +1 @@\n-a\n+b\n", "no file header")]
    [InlineData("a header that does not close", "--- a/a.cs\n+++ b/a.cs\n@@ -1 +1\n-a\n+b\n", "does not close")]
    [InlineData("a header with letters", "--- a/a.cs\n+++ b/a.cs\n@@ -x,1 +1 @@\n-a\n+b\n", "is not '@@")]
    [InlineData("a header past the largest line", "--- a/a.cs\n+++ b/a.cs\n@@ -1 +2147483647,2 @@\n-a\n+b\n", "is not '@@")]
    [InlineData("an old side starting at line 0 with lines", "--- a/a.cs\n+++ b/a.cs\n@@ -0,1 +1,0 @@\n-a\n", "is not '@@")]
    [InlineData("a new side starting at line 0 with lines", "--- a/a.cs\n+++ b/a.cs\n@@ -1,0 +0 @@\n+a\n", "is not '@@")]
    [InlineData("more added lines than counted", "--- a/a.cs\n+++ b/a.cs\n@@ -1,1 +1,1 @@\n+a\n+b\n", "beyond the hunk's new count")]
    [InlineData("an unclosed quoted path", "--- \"a/a.cs\n+++ b/a.cs\n@@ -1 +1 @@\n-a\n+b\n", "no closing quote")]
    [InlineData("a blob id that is not hexadecimal", "diff --git a/a.cs b/a.cs\nindex xyz1234..2222222\n--- a/a.cs\n+++ b/a.cs\n@@ -1 +1 @@\n-a\n+b\n", "not hexadecimal")]
    [InlineData("the same file twice (git log -p)", "--- a/a.cs\n+++ b/a.cs\n@@ -1 +1 @@\n-a\n+b\n--- a/a.cs\n+++ b/a.cs\n@@ -1 +1 @@\n-b\n+c\n", "second time")]
    [InlineData("nothing", "   \n", "empty")]
    [InlineData("text that is not a diff", "commit 45e04e4\nAuthor: someone\n\n    message\n", "names no file")]
    public void AMalformedDiff_IsRefusedWithWhereAndWhy(string _, string text, string reason)
    {
        var refusal = Assert.Throws<ArgumentException>(() => UnifiedDiff.Parse(text));
        Assert.Contains(reason, refusal.Message);
    }

    [Fact]
    public void AbsurdSizesAndCounts_AreRefusedBeforeAnythingIsRead()
    {
        var huge = new string('+', UnifiedDiff.MaxLength + 1);
        Assert.Contains("at most", Assert.Throws<ArgumentException>(() => UnifiedDiff.Parse(huge)).Message);

        var many = string.Concat(Enumerable.Range(0, UnifiedDiff.MaxFiles + 1)
            .Select(i => $"diff --git a/f{i}.cs b/f{i}.cs\nold mode 100644\nnew mode 100755\n"));
        Assert.Contains("more than", Assert.Throws<ArgumentException>(() => UnifiedDiff.Parse(many)).Message);
    }
}
