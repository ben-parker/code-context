using System.Diagnostics;
using System.Text;
using CodeContext.Core.Diffs;
using CodeContext.Core.Services;
using Xunit.Abstractions;

namespace CodeContext.Core.Tests.Services;

/// <summary>
/// Opt-in timing of diff impact on a large change: a synthetic two-shard graph of 2,000 files (a class and 24 methods
/// each, 50,000 symbols, about 250,000 calls), real files on disk, and a diff of 600 of them with three hunks each.
/// Reports the median of 5 runs of the whole call (parse, working-tree checks, mapping, one walk, the answer) and of
/// the parse alone, and the bytes allocated by one call. Asserts nothing about time. Run with
/// <c>dotnet test -c Release --filter "FullyQualifiedName~GraphImpactBenchmarks" --logger "console;verbosity=detailed"</c>.
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class GraphImpactBenchmarks(ITestOutputHelper output) : IDisposable
{
    private const int Files = 2_000;
    private const int MethodsPerFile = 24;
    private const int LinesPerMethod = 8;
    private const int ChangedFiles = 600;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-impact-bench-" + Guid.NewGuid().ToString("N"));

    private sealed class Fresh : IIndexFreshness
    {
        public DateTime? FilesReadAsOfUtc => DateTime.UtcNow.AddHours(1);
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

    [Fact]
    public async Task ALargeDiff_IsAnsweredInOneWalk()
    {
        var random = new Random(20261120);
        var graph = new GraphTestGraph(_root) { Freshness = new Fresh() };
        var lines = 2 + MethodsPerFile * LinesPerMethod + 1;
        var blobs = new string[Files];
        var edges = 0;
        for (var f = 0; f < Files; f++)
        {
            var file = $"src/M{f % 40}/F{f}.cs";
            var path = Path.Combine(_root, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var content = string.Concat(Enumerable.Range(1, lines).Select(line => $"line {line}\n"));
            File.WriteAllText(path, content);
            blobs[f] = DiffWorkingTree.BlobId(Encoding.UTF8.GetBytes(content), sha256: false)[..9];

            graph.Node($"F{f}", "Class", file: file, line: 0, endLine: lines - 1);
            for (var m = 0; m < MethodsPerFile; m++)
            {
                var start = 1 + m * LinesPerMethod;
                graph.Node($"F{f}.M{m}", file: file, line: start, endLine: start + LinesPerMethod - 2).Edge($"F{f}", $"F{f}.M{m}", "HAS_METHOD");
            }
        }
        for (var f = 0; f < Files; f++)
        {
            for (var m = 0; m < MethodsPerFile; m++)
            {
                for (var c = 0; c < 5; c++)
                {
                    graph.Edge($"F{f}.M{m}", $"F{random.Next(Files)}.M{random.Next(MethodsPerFile)}");
                    edges++;
                }
            }
        }
        var service = graph.Build();

        var diff = new StringBuilder();
        foreach (var f in Enumerable.Range(0, Files).OrderBy(_ => random.Next()).Take(ChangedFiles).Order())
        {
            var file = $"src/M{f % 40}/F{f}.cs";
            diff.Append($"diff --git a/{file} b/{file}\nindex 123456789..{blobs[f]} 100644\n--- a/{file}\n+++ b/{file}\n");
            foreach (var m in new[] { 1, 9, 17 })
            {
                var line = 2 + m * LinesPerMethod + 2;
                diff.Append($"@@ -{line - 1},3 +{line - 1},3 @@\n line {line - 1}\n-old\n+line {line}\n line {line + 1}\n");
            }
        }
        var text = diff.ToString();
        var request = new GraphImpactRequest { Diff = text, MaxDepth = 5, Limit = 20 };
        output.WriteLine($"graph: {Files * (MethodsPerFile + 1)} symbols, {edges + Files * MethodsPerFile} edges; diff: {ChangedFiles} files, "
            + $"{ChangedFiles * 3} hunks, {text.Length:N0} characters");

        var warm = await service.GetImpactAsync(request);
        output.WriteLine($"answer: {warm.ChangeCount} changed, {warm.TotalCount} affected in {warm.FileCount} files, complete {warm.Complete}, "
            + $"content checked {warm.ContentChecked}");
        Assert.True(warm.Complete);
        Assert.Equal(ChangedFiles * 3, warm.ChangeCount);

        var parse = new List<double>();
        var whole = new List<double>();
        for (var run = 0; run < 5; run++)
        {
            var started = Stopwatch.GetTimestamp();
            UnifiedDiff.Parse(text);
            parse.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            started = Stopwatch.GetTimestamp();
            await service.GetImpactAsync(request);
            whole.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        await service.GetImpactAsync(request);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        GC.Collect();
        var retainedBefore = GC.GetTotalMemory(forceFullCollection: true);
        await service.GetImpactAsync(request);
        var retainedAfter = GC.GetTotalMemory(forceFullCollection: true);

        output.WriteLine($"parse: median {Median(parse):F1} ms [{string.Join(", ", parse.Select(ms => ms.ToString("F1")))}]");
        output.WriteLine($"impact: median {Median(whole):F1} ms [{string.Join(", ", whole.Select(ms => ms.ToString("F1")))}]");
        output.WriteLine($"allocated by one call: {allocated / (1024.0 * 1024):F1} MB; heap after a call minus before: "
            + $"{(retainedAfter - retainedBefore) / 1024.0:F0} KB");
    }

    /// <summary>
    /// The diff at its limits: <paramref name="files"/> files of <paramref name="members"/> eight-line methods each, every
    /// one with a doc comment above it, and a diff that changes a line and the doc comment of
    /// <paramref name="changedPerFile"/> of them in every file. Reports the same figures.
    /// </summary>
    [Theory]
    [InlineData(10_000, 4, 1)]      // the most files a diff may name
    [InlineData(46, 5_000, 2_000)]  // about 16 MB of diff, 184,000 hunks
    [InlineData(1, 5_000, 5_000)]   // one type of 5,000 members, every doc comment changed
    public async Task ADiffAtItsLimits_IsAnswered(int files, int members, int changedPerFile)
    {
        const int MemberLines = 8;
        var graph = new GraphTestGraph(_root) { Freshness = new Fresh() };
        var diff = new StringBuilder();
        var content = new StringBuilder();
        content.Append("namespace Bench;\nclass F\n{\n");
        for (var m = 0; m < members; m++)
        {
            content.Append("    /// <summary>Doc.</summary>\n    void M()\n    {\n");
            for (var line = 0; line < MemberLines - 4; line++) content.Append("        Call();\n");
            content.Append("    }\n");
        }
        content.Append("}\n");
        var text = content.ToString();
        var blob = DiffWorkingTree.BlobId(Encoding.UTF8.GetBytes(text), sha256: false)[..9];
        var step = members / changedPerFile;
        for (var f = 0; f < files; f++)
        {
            var file = $"src/M{f % 100}/F{f}.cs";
            var path = Path.Combine(_root, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            graph.Node($"F{f}", "Class", file: file, line: 1, endLine: 3 + members * MemberLines);
            for (var m = 0; m < members; m++)
            {
                // 1-based: the doc comment at 4 + 8m, the member from the line after it for seven lines.
                graph.Node($"F{f}.M{m}", file: file, line: 4 + m * MemberLines, endLine: 4 + m * MemberLines + MemberLines - 2);
            }
            if (f > 0) graph.Edge($"F{f}.M0", $"F{f - 1}.M0");
            diff.Append($"diff --git a/{file} b/{file}\nindex 123456789..{blob} 100644\n--- a/{file}\n+++ b/{file}\n");
            for (var m = 0; m < members; m += step)
            {
                var doc = 4 + m * MemberLines;
                diff.Append($"@@ -{doc} +{doc} @@\n-    /// <summary>Old.</summary>\n+    /// <summary>Doc.</summary>\n");
                diff.Append($"@@ -{doc + 3} +{doc + 3} @@\n-        Old();\n+        Call();\n");
            }
        }
        var service = graph.Build();
        var request = new GraphImpactRequest { Diff = diff.ToString(), MaxDepth = 5, Limit = 20 };
        output.WriteLine($"graph: {files * (members + 1):N0} symbols; diff: {files:N0} files, {request.Diff.Length:N0} characters");

        var started = Stopwatch.GetTimestamp();
        var parsed = UnifiedDiff.Parse(request.Diff);
        var parse = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Assert.Equal(files, parsed.Count);
        parsed = null;

        var warm = await service.GetImpactAsync(request);
        output.WriteLine($"answer: {warm.ChangeCount:N0} changed, {warm.TotalCount:N0} affected, {warm.IgnoredLines:N0} ignored, complete {warm.Complete}, "
            + $"unmapped {warm.UnmappedCount}");
        Assert.Equal(files * ((members + step - 1) / step), warm.ChangeCount);
        Assert.True(warm.Complete);

        var whole = new List<double>();
        for (var run = 0; run < 3; run++)
        {
            started = Stopwatch.GetTimestamp();
            await service.GetImpactAsync(request);
            whole.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var retainedBefore = GC.GetTotalMemory(forceFullCollection: true);
        var before = GC.GetTotalAllocatedBytes(precise: true);
        await service.GetImpactAsync(request);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        var retainedAfter = GC.GetTotalMemory(forceFullCollection: true);
        output.WriteLine($"parse: {parse:F0} ms; impact: median {Median(whole):F0} ms [{string.Join(", ", whole.Select(ms => ms.ToString("F0")))}]");
        output.WriteLine($"allocated by one call: {allocated / (1024.0 * 1024):F1} MB; heap after a call minus before: "
            + $"{(retainedAfter - retainedBefore) / 1024.0:F0} KB");
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
}
