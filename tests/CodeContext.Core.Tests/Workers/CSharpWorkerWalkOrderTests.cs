using System.Text.Json;
using CodeContext.Core.Services;
using CodeContext.CSharp.Worker;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// Files are walked in whatever order keeps the threads busy. None of that may show: progress
/// counts up as it always did, and the facts and their order depend only on what was asked for.
/// </summary>
public sealed class CSharpWorkerWalkOrderTests : IDisposable
{
    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
        </Project>
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("cc-csharp-walk-order-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Sizes that differ widely, so the order of the walk differs from the list's.</summary>
    private List<string> SizedFiles(int count)
    {
        Write("App/App.csproj", Project);
        var files = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var members = string.Concat(Enumerable.Range(0, index % 7 * 40).Select(member => $"public int M{member}() => {member};\n"));
            files.Add(Write($"App/F{index:D4}.cs", $"public class Type{index} {{ {members} }}"));
        }
        return files;
    }

    private static async Task<List<(int Processed, int Total, string? File)>> Progress(
        CSharpWorkspaceAnalyzer analyzer, IReadOnlyList<string> requested)
    {
        var reports = new List<(int, int, string?)>();
        await analyzer.AnalyzeAsync(
            (processed, total, file) =>
            {
                reports.Add((processed, total, file));
                return ValueTask.CompletedTask;
            },
            CancellationToken.None,
            requested);
        return reports;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(257)]
    public async Task ProgressCountsUpInItsUsualSteps(int count)
    {
        var files = SizedFiles(count);
        var analyzer = new CSharpWorkspaceAnalyzer("test", _root);
        analyzer.ReplaceFiles(files, CancellationToken.None);

        var reports = await Progress(analyzer, files);

        var interval = Math.Max(1, (count + 99) / 100);
        var expected = Enumerable.Range(1, count).Where(processed => processed == count || processed % interval == 0).ToList();
        Assert.Equal(expected, reports.Select(report => report.Processed));
        Assert.All(reports, report => Assert.Equal(count, report.Total));
        Assert.All(reports, report => Assert.Equal(files[report.Processed - 1], report.File));
    }

    [Fact]
    public async Task AFileWithNothingToWalkStillCounts()
    {
        var files = SizedFiles(5);
        var gone = Path.Combine(_root, "App", "Gone.cs");
        var analyzer = new CSharpWorkspaceAnalyzer("test", _root);
        analyzer.ReplaceFiles(files, CancellationToken.None);

        var reports = await Progress(analyzer, [gone, .. files]);

        Assert.Equal([1, 2, 3, 4, 5, 6], reports.Select(report => report.Processed));
        Assert.All(reports, report => Assert.Equal(6, report.Total));
    }

    [Fact]
    public async Task ProgressWithNothingToDoStillReports()
    {
        var files = SizedFiles(3);
        var analyzer = new CSharpWorkspaceAnalyzer("test", _root);
        analyzer.ReplaceFiles(files, CancellationToken.None);
        analyzer.Analyze(CancellationToken.None);

        var reports = await Progress(analyzer, []);

        Assert.Equal([(0, 0, (string?)null)], reports);
    }

    [Fact]
    public async Task AScanSeenThroughTheHostNeverCountsBackwards()
    {
        var files = SizedFiles(240);
        var reporter = new RecordingReporter();
        await using var pipeline = new CSharpWorkerPipeline(_root);

        await pipeline.GraphUpdateService.PerformInitialScanAsync(_root, reporter, CancellationToken.None);

        // The host keeps only the newest updates of a burst, so how many arrive varies.
        var counts = reporter.Counts();
        Assert.True(counts.Count > 1, $"Only {counts.Count} reports.");
        Assert.Equal(counts.Order(), counts);
        Assert.Equal(files.Count, counts[^1]);
        Assert.Equal(files.Count, reporter.CompletedCount);
    }

    private sealed class RecordingReporter : IScanProgressReporter
    {
        private readonly List<int> _counts = [];

        public int CompletedCount { get; private set; }

        public List<int> Counts()
        {
            lock (_counts) return [.. _counts];
        }

        public void ReportProgress(int processed, int total, string currentFile)
        {
            lock (_counts) _counts.Add(processed);
        }

        public void ReportComplete(int total, TimeSpan duration) => CompletedCount = total;

        public void ReportError(string filePath, string error) { }
    }

    [Fact]
    public async Task ACancelledWalkStopsAndLeavesNoHalfResult()
    {
        var files = SizedFiles(120);
        var analyzer = new CSharpWorkspaceAnalyzer("test", _root);
        analyzer.ReplaceFiles(files, CancellationToken.None);
        using var cancel = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => analyzer.AnalyzeAsync(
            (_, _, _) =>
            {
                cancel.Cancel();
                return ValueTask.CompletedTask;
            },
            cancel.Token,
            files));

        Assert.Equal(files.Count, analyzer.Analyze(CancellationToken.None).Nodes.Count(node => node.Kind == "Class"));
    }

    [Fact]
    public async Task AReporterThatFailsStopsTheWalkAndSurfaces()
    {
        var files = SizedFiles(120);
        var analyzer = new CSharpWorkspaceAnalyzer("test", _root);
        analyzer.ReplaceFiles(files, CancellationToken.None);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => analyzer.AnalyzeAsync(
            (_, _, _) => throw new InvalidOperationException("pipe closed"),
            CancellationToken.None,
            files));

        Assert.Equal("pipe closed", thrown.Message);
    }

    /// <summary>A workspace where order could plausibly matter: shared ids, rivals, stubs.</summary>
    private List<string> Contested()
    {
        Write("A/A.csproj", Project);
        Write("B/B.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="../A/A.csproj" /><Compile Include="../A/Linked.cs" /></ItemGroup>
            </Project>
            """);
        return
        [
            Write("A/Part1.cs", "namespace N; public partial class Split : System.IDisposable { public void Dispose() { } public int One() => Two(); }"),
            Write("A/Part2.cs", "namespace N; public partial class Split : System.IDisposable { public int Two() => new System.Random().Next(); }"),
            Write("A/Linked.cs", "namespace N; public class Linked { public string Text() => new System.Text.StringBuilder().ToString(); }"),
            Write("A/Same.cs", "namespace N; public class Same { public int Value() => Missing(); }"),
            Write("B/Same.cs", "namespace N; public class Same { public int Value() => new Split().One(); public int Other() => AlsoMissing(); }"),
            Write("B/User.cs", "namespace M; public class User { public string Go(N.Linked linked) => linked.Text() + new System.Random().Next(); }"),
            Write("Loose.cs", "public class Loose { public string Go() => string.Empty.Trim(); }"),
        ];
    }

    private static List<string> SortedFacts(CSharpWorkspaceAnalyzer.AnalysisResult result)
    {
        var facts = new List<string>();
        facts.AddRange(result.Nodes.Select(node => JsonSerializer.Serialize(node, ParserProtocolJsonContext.Default.ProtocolNode)));
        facts.AddRange(result.Edges.Select(edge => JsonSerializer.Serialize(edge, ParserProtocolJsonContext.Default.ProtocolEdge)));
        facts.AddRange(result.Coverage.Select(coverage =>
            $"{coverage.Name} {coverage.State} {coverage.Files} {coverage.UnresolvedNames} {string.Join(",", coverage.CommonUnresolvedNames ?? [])}"));
        facts.Sort(StringComparer.Ordinal);
        return facts;
    }

    private async Task<CSharpWorkspaceAnalyzer.AnalysisResult> Scan(IReadOnlyList<string> files)
    {
        var analyzer = new CSharpWorkspaceAnalyzer("test", _root);
        analyzer.ReplaceFiles(files, CancellationToken.None);
        return await analyzer.AnalyzeAsync((_, _, _) => ValueTask.CompletedTask, CancellationToken.None, files);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task FilesInAnyOrderGiveTheSameGraph(int seed)
    {
        var files = Contested();
        var expected = SortedFacts(await Scan(files));
        var random = new Random(seed);

        var actual = SortedFacts(await Scan(files.OrderBy(_ => random.Next()).ToList()));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task FactsComeOutInTheOrderFilesWereAskedFor()
    {
        var files = SizedFiles(60);

        var first = await Scan(files);
        var second = await Scan(files);

        Assert.Equal(first.Nodes.Select(node => node.Id), second.Nodes.Select(node => node.Id));
        Assert.Equal(first.Edges.Select(edge => edge.Id), second.Edges.Select(edge => edge.Id));
        var owners = first.Nodes.Where(node => node.Kind == "Class").Select(node => node.Name).ToList();
        Assert.Equal(Enumerable.Range(0, files.Count).Select(index => "Type" + index), owners);
    }
}
