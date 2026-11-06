using CodeContext.CSharp.Worker;
using CodeContext.Parser.Protocol;

namespace CodeContext.Core.Tests.Workers;

/// <summary>Plan phase 6: save, restore into a new worker, and settle everything: the same buckets and hashes as the worker that saved.</summary>
public sealed class CSharpWorkerStateRoundTripTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("cc-csharp-roundtrip-").FullName;
    private string Root => Path.Combine(_base, "repo");
    private string StateDirectory => Path.Combine(_base, "state");

    public CSharpWorkerStateRoundTripTests()
    {
        Directory.CreateDirectory(StateDirectory);
        Write("Lib/Lib.csproj", CSharpWorkerBaselineTests.Project());
        Write("Lib/p1.cs", "namespace Lib; public partial class Shape { public int Area() => System.Math.Abs(1); }");
        Write("Lib/p2.cs", "namespace Lib; public partial class Shape { public void Grow() { Missing.Call(); } }");
        Write("loose.cs", "public class Loose { public void Go() => new Lib.Shape().Area(); }");
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public async Task RestoredWorkerSettlesToTheSameBucketsAndHashes()
    {
        var host = new InProcessCSharpHost(Root);
        await host.IndexAsync();
        var baseline = await host.SaveAsync(StateDirectory);
        var original = host.Analyzer.CaptureState("x")!;
        var originalBuckets = Buckets(host.Analyzer.Analyze(CancellationToken.None));

        host.Restart();
        await host.IndexAsync(baseline);
        Assert.Equal(0, VerifyLine.Parse(host.LastVerify).Walked);
        var restored = host.Analyzer.CaptureState("x")!;

        Assert.Equal(original.FactHashes.Select(hash => (hash.Path, Convert.ToHexString(hash.Hash))),
            restored.FactHashes.Select(hash => (hash.Path, Convert.ToHexString(hash.Hash))));
        Assert.Equal(original.Fingerprints, restored.Fingerprints);
        Assert.Equal(originalBuckets, Buckets(host.Analyzer.Analyze(CancellationToken.None)));
    }

    private static List<string> Buckets(CSharpWorkspaceAnalyzer.AnalysisResult analysis)
        => analysis.Buckets.Select(bucket => bucket.FilePath + "\n"
                + string.Join('\n', bucket.Nodes.Select(node => System.Text.Json.JsonSerializer.Serialize(node, ParserProtocolJsonContext.Default.ProtocolNode)).Order(StringComparer.Ordinal))
                + "\n" + string.Join('\n', bucket.Edges.Select(edge => edge.ToString()).Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToList();
}
