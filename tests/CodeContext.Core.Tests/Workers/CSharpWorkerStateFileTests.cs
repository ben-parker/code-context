using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using CodeContext.CSharp.Worker;
using CodeContext.CSharp.Worker.Projects;
using CodeContext.CSharp.Worker.State;
using CodeContext.Parser.Protocol;
using EdgeFact = CodeContext.CSharp.Worker.CSharpWorkspaceAnalyzer.EdgeFact;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// The worker's state file: what a save writes reads back as it was, a restore from it settles to the
/// buckets and hashes of the worker that saved it, and bytes that are not exactly what that worker wrote
/// are refused. The file lives in the repository, so it is untrusted: every single-bit change is caught
/// by the hash before a byte is read, and damage that keeps the hash right (a writer bug, or a hash
/// recomputed by whoever damaged it) ends in a rejection or an ordinary restore, never an exception out
/// of the request or a hang.
/// </summary>
public sealed class CSharpWorkerStateFileTests : IDisposable
{
    private static readonly Disk AnyDisk = new([]);

    private readonly string _base = Directory.CreateTempSubdirectory("cc-csharp-state-").FullName;
    private string Root => Path.Combine(_base, "repo");
    private string StateDirectory => Path.Combine(Root, ".codecontext", "cache", "default");

    public CSharpWorkerStateFileTests()
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

    private async Task<(InProcessCSharpHost Host, IndexBaseline Baseline, byte[] Bytes)> SavedAsync()
    {
        var host = new InProcessCSharpHost(Root);
        await host.IndexAsync();
        var baseline = await host.SaveAsync(StateDirectory);
        return (host, baseline, File.ReadAllBytes(baseline.StatePath));
    }

    [Fact]
    public async Task Save_WritesWhatReadsBackAsItWas()
    {
        var (host, _, bytes) = await SavedAsync();
        var state = WorkerStateFile.Read(bytes);
        Assert.Equal(host.Analyzer.Identity, state.WorkerIdentity);
        Assert.Equal(Path.GetFullPath(Root), state.RootPath);
        Assert.Equal("default", state.WorkspaceId);
        Assert.Equal(host.LastDeltas[0].Fingerprints, state.Fingerprints);
        Assert.Equal(host.ApprovedFiles(), state.Files.Select(file => file.Path).Order(StringComparer.Ordinal));

        // Written again from what was read: the same bytes.
        using var again = new MemoryStream();
        var (length, sha) = WorkerStateFile.Write(again, state);
        Assert.Equal(bytes, again.ToArray());
        Assert.Equal(bytes.Length, length);
        Assert.Equal(SHA256.HashData(bytes), sha);
    }

    [Fact]
    public void Write_ThenRead_KeepsNullsAndEmptiesApart()
    {
        var hash = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var state = new WorkerState
        {
            WorkerIdentity = "w", StateId = "s", WorkspaceId = "default", RootPath = "C:\\r",
            Fingerprints = [new UnitFingerprint("u", "v")],
            Files =
            [
                new WalkedFileState
                {
                    Path = "C:\\r\\a.cs", Stamp = new FileStamp(10, 20, true, hash), ContainsDirectives = true, UnitKey = "u",
                    Nodes =
                    [
                        new ProtocolNode("csharp:default:u:A", "csharp:A", "A", "Class", "csharp", "C:\\r\\a.cs", 1, 2, 3, 4),
                        new ProtocolNode("csharp:default:u:B", "csharp:B", "B", "Class", "csharp", "C:\\r\\a.cs", 0, 0, 0, 0,
                            "N", "public", "sig", "ret", "p", "m", new Dictionary<string, string>()),
                    ],
                    Edges = [new EdgeFact("csharp:default:u:A", "t", "CALLS"), new EdgeFact("csharp:default:u:A", "t", "CALLS", 5, 6)],
                    StubNodes = [], StubEdges = [], UnresolvedNames = [],
                },
                new WalkedFileState
                {
                    Path = "C:\\r\\b.cs", Stamp = new FileStamp(0, 0, false, hash), ContainsDirectives = false, UnitKey = "u",
                    Nodes = [], Edges = [], StubNodes = [], StubEdges = [], UnresolvedNames = null,
                },
            ],
            FactHashes = [new FactHashState("external:System", hash)],
        };
        using var stream = new MemoryStream();
        WorkerStateFile.Write(stream, state);
        var read = WorkerStateFile.Read(stream.ToArray());

        Assert.Null(read.Files[0].Nodes[0].Namespace);
        Assert.Null(read.Files[0].Nodes[0].Metadata);
        Assert.Empty(read.Files[0].Nodes[1].Metadata!);
        Assert.Equal(state.Files[0].Nodes[1] with { Metadata = null }, read.Files[0].Nodes[1] with { Metadata = null });
        Assert.Equal(state.Files[0].Edges, read.Files[0].Edges);
        Assert.Equal(new FileStamp(10, 20, true, hash).Size, read.Files[0].Stamp.Size);
        Assert.True(read.Files[0].Stamp.Racy && read.Files[0].ContainsDirectives);
        Assert.NotNull(read.Files[0].UnresolvedNames);
        Assert.Null(read.Files[1].UnresolvedNames);
        Assert.Equal(hash, read.FactHashes[0].Hash);
    }

    [Fact]
    public void Write_RefusesAStringThatWouldNotReadBack()
    {
        var state = new WorkerState
        {
            WorkerIdentity = "w", StateId = "s", WorkspaceId = "\ud800", RootPath = "r",
            Fingerprints = [], Files = [], FactHashes = [],
        };
        Assert.ThrowsAny<System.Text.EncoderFallbackException>(() => WorkerStateFile.Write(Stream.Null, state));
    }

    /// <summary>Seeded cases per part of the file: 6 parts, so a few hundred in all.</summary>
    private const int Rounds = 50;

    /// <summary>
    /// Every bit of a small state file, flipped one at a time, fails the comparison with the recorded hash, which
    /// is all that stands before the parser; a sample goes through a whole request, which answers as without a baseline.
    /// </summary>
    [Fact]
    public async Task EverySingleBitFlip_IsRejectedByTheHash()
    {
        var (_, baseline, bytes) = await SavedAsync();
        var recorded = Convert.FromHexString(baseline.StateSha256);
        Assert.True(WorkerStateFile.MatchesHash(bytes, recorded));

        var damaged = (byte[])bytes.Clone();
        for (var bit = 0; bit < bytes.Length * 8; bit++)
        {
            damaged[bit / 8] ^= (byte)(1 << (bit % 8));
            Assert.False(WorkerStateFile.MatchesHash(damaged, recorded), $"Bit {bit} went unnoticed.");
            damaged[bit / 8] ^= (byte)(1 << (bit % 8));
        }

        foreach (var at in new[] { 0, 8, 16, bytes.Length / 2, bytes.Length - 1 })
        {
            damaged[at] ^= 1;
            File.WriteAllBytes(baseline.StatePath, damaged);
            damaged[at] ^= 1;
            Assert.Null(WorkerStateFile.Open(AnyDisk, baseline.StatePath, baseline.StateSha256, out var reason));
            Assert.Equal("state file hash does not match", reason);

            var host = new InProcessCSharpHost(Root);
            var result = await host.IndexAsync(baseline);
            Assert.Equal(BaselineOutcomes.Rejected, result.Baseline);
            Assert.Equal("state file hash does not match", result.BaselineReason);
            Assert.All(host.LastDeltas, delta => Assert.True(delta.ReplacesWorkspace));
        }
    }

    [Fact]
    public async Task AFileOverTheCap_IsRefusedBeforeItIsRead()
    {
        var (_, baseline, bytes) = await SavedAsync();
        Assert.NotNull(WorkerStateFile.Open(AnyDisk, baseline.StatePath, baseline.StateSha256, out _, maxLength: bytes.Length));

        Assert.Null(WorkerStateFile.Open(AnyDisk, baseline.StatePath, baseline.StateSha256, out var reason, maxLength: bytes.Length - 1));
        Assert.Equal("state file is too large", reason);
    }

    /// <summary>The production cap, with a file one byte over it: refused without the buffer a read would need.</summary>
    [Fact]
    public void AFileOverTheProductionCap_IsRefusedWithoutAllocatingForIt()
    {
        Assert.Equal(1L << 30, WorkerStateFile.MaxFileLength);
        var path = Path.Combine(StateDirectory, "huge.state");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.SetLength(WorkerStateFile.MaxFileLength + 1);

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(WorkerStateFile.Open(AnyDisk, path, new string('0', 64), out var reason));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal("state file is too large", reason);
        Assert.InRange(allocated, 0, 1 << 20);
    }

    /// <summary>A state too big for this process to read is a rejected baseline, not a failed request.</summary>
    [Fact]
    public async Task RunningOutOfMemoryWhileReading_RejectsTheBaseline()
    {
        var (_, baseline, _) = await SavedAsync();
        var host = new InProcessCSharpHost(Root, (workspaceId, root) => new CSharpWorkspaceAnalyzer(workspaceId, root)
        {
            RestoreFault = _ => throw new OutOfMemoryException(),
        });
        var result = await host.IndexAsync(baseline);
        Assert.Equal(BaselineOutcomes.Rejected, result.Baseline);
        Assert.Equal("state file needs more memory than is available", result.BaselineReason);
        Assert.All(host.LastDeltas, delta => Assert.True(delta.ReplacesWorkspace));
    }

    private static readonly ProtocolNode GoodNode = new("csharp:default:u:A", "csharp:A", "A", "Class", "csharp", "a.cs", 1, 2, 3, 4);
    private static readonly ProtocolNode GoodStub = new("csharp:default:external:S", "csharp:S", "S", "Class", "csharp", "external:S");
    private static readonly EdgeFact GoodEdge = new("csharp:default:u:A", "csharp:default:u:B", "CALLS", 5, 6);

    /// <summary>A value the worker never records, which the file alone shows wrong, and what the reader says of it.</summary>
    private static readonly Dictionary<string, (WalkedFileState File, string Message)> ImpossibleFacts = new()
    {
        ["negative start line"] = (Walk(nodes: [GoodNode with { StartLine = -1 }]), "a node's span is not a span"),
        ["negative start column"] = (Walk(nodes: [GoodNode with { StartColumn = -1 }]), "a node's span is not a span"),
        ["negative end column"] = (Walk(nodes: [GoodNode with { EndColumn = -1 }]), "a node's span is not a span"),
        ["end line before start"] = (Walk(nodes: [GoodNode with { EndLine = 0 }]), "a node's span is not a span"),
        ["end column before start, one line"] = (Walk(nodes: [GoodNode with { EndLine = 1, EndColumn = 2 }]), "a node's span is not a span"),
        ["another language"] = (Walk(nodes: [GoodNode with { Language = "csharq" }]), "a node is not C#"),
        ["edge line below -1"] = (Walk(edges: [GoodEdge with { Line = -2 }]), "an edge's position is out of range"),
        ["edge column below -1"] = (Walk(edges: [GoodEdge with { Column = -2 }]), "an edge's position is out of range"),
        ["edge line without column"] = (Walk(edges: [GoodEdge with { Column = -1 }]), "an edge's position is out of range"),
        ["edge column without line"] = (Walk(edges: [GoodEdge with { Line = -1 }]), "an edge's position is out of range"),
        ["stub with a line"] = (Walk(stubNodes: [GoodStub with { EndLine = 3 }]), "a library symbol has a source position"),
        ["stub with a column"] = (Walk(stubNodes: [GoodStub with { StartColumn = 3, EndColumn = 3 }]), "a library symbol has a source position"),
        ["stub edge with a position"] = (Walk(stubEdges: [GoodEdge]), "a library symbol has a source position"),
    };

    private static WalkedFileState Walk(
        List<ProtocolNode>? nodes = null, List<EdgeFact>? edges = null, List<ProtocolNode>? stubNodes = null, List<EdgeFact>? stubEdges = null) => new()
    {
        Path = "a.cs", Stamp = new FileStamp(1, 1, false, new byte[32]), ContainsDirectives = false, UnitKey = "u",
        Nodes = nodes ?? [], Edges = edges ?? [], StubNodes = stubNodes ?? [], StubEdges = stubEdges ?? [],
    };

    private static byte[] StateOf(WalkedFileState file)
    {
        using var stream = new MemoryStream();
        WorkerStateFile.Write(stream, new WorkerState
        {
            WorkerIdentity = "w", StateId = "s", WorkspaceId = "default", RootPath = "r", Fingerprints = [], FactHashes = [], Files = [file],
        });
        return stream.ToArray();
    }

    public static TheoryData<string> ImpossibleFactNames() => [.. ImpossibleFacts.Keys];

    [Theory]
    [MemberData(nameof(ImpossibleFactNames))]
    public void AValueTheWorkerNeverRecords_IsRefusedAsMalformed(string what)
    {
        var (file, message) = ImpossibleFacts[what];
        var refused = Assert.Throws<WorkerStateFormatException>(() => WorkerStateFile.Read(StateOf(file)));
        Assert.Equal(message, refused.Message);
    }

    /// <summary>What the worker does record at the edges of those checks reads back: zero-based positions, one-line spans, no position at all.</summary>
    [Fact]
    public void TheValuesTheWorkerDoesRecord_AreRead()
    {
        var read = WorkerStateFile.Read(StateOf(Walk(
            nodes: [GoodNode with { StartLine = 0, EndLine = 0, StartColumn = 0, EndColumn = 0 }, GoodNode with { StartColumn = 9, EndColumn = 0 }, GoodNode with { EndLine = 1, EndColumn = 3 }],
            edges: [GoodEdge, GoodEdge with { Line = 0, Column = 0 }, GoodEdge with { Line = -1, Column = -1 }],
            stubNodes: [GoodStub],
            stubEdges: [GoodEdge with { Line = -1, Column = -1 }])));
        Assert.Equal(3, read.Files[0].Nodes.Count);
        Assert.Equal(3, read.Files[0].Edges.Count);
    }

    /// <summary>Hash-valid bytes with an impossible value reach a request as a rejected baseline that says why.</summary>
    [Fact]
    public async Task AnImpossibleValue_RejectsTheBaseline_WithItsReason()
    {
        var (_, baseline, bytes) = await SavedAsync();
        var state = WorkerStateFile.Read(bytes);
        var file = state.Files.First(candidate => candidate.Nodes.Count > 0);
        file.Nodes[0] = file.Nodes[0] with { StartLine = -2 };
        using var stream = new MemoryStream();
        var (_, sha) = WorkerStateFile.Write(stream, state);
        File.WriteAllBytes(baseline.StatePath, stream.ToArray());

        var host = new InProcessCSharpHost(Root);
        var result = await host.IndexAsync(baseline with { StateSha256 = Convert.ToHexStringLower(sha) });
        Assert.Equal(BaselineOutcomes.Rejected, result.Baseline);
        Assert.Equal("state file is malformed: a node's span is not a span", result.BaselineReason);
    }

    /// <summary>An edge that leaves the workspace is one no walk records, whichever list it is in.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnEdgeTargetOutsideTheWorkspace_RejectsTheBaseline(bool stub)
    {
        var (_, baseline, bytes) = await SavedAsync();
        var state = WorkerStateFile.Read(bytes);
        var file = state.Files.First(candidate => (stub ? candidate.StubEdges : candidate.Edges).Count > 0);
        var edges = stub ? file.StubEdges : file.Edges;
        edges[0] = edges[0] with { TargetId = "nowhere" };
        using var stream = new MemoryStream();
        var (_, sha) = WorkerStateFile.Write(stream, state);
        File.WriteAllBytes(baseline.StatePath, stream.ToArray());

        var host = new InProcessCSharpHost(Root);
        var result = await host.IndexAsync(baseline with { StateSha256 = Convert.ToHexStringLower(sha) });
        Assert.Equal(BaselineOutcomes.Rejected, result.Baseline);
        Assert.Equal("state file holds a walk this worker would not record", result.BaselineReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("zz00000000000000000000000000000000000000000000000000000000000000")]
    public async Task ARecordedHashThatIsNotLowercaseSha256_IsRefused(string recorded)
    {
        var (_, baseline, _) = await SavedAsync();
        Assert.Null(WorkerStateFile.Open(AnyDisk, baseline.StatePath, recorded, out var reason));
        Assert.Equal("the recorded hash is not a SHA-256", reason);
    }

    /// <summary>The parts of the file damage is aimed at, one at a time.</summary>
    public static TheoryData<string> Tables() => ["header", "strings", "units", "files", "hashes", "facts"];

    /// <summary>
    /// Seeded damage over one part, with the hash recomputed so it reaches the parser: each case is refused
    /// as malformed, or rejected or accepted by the request, which then answers normally. Never an exception,
    /// never a hang. An accepted restore equals a fresh scan in its facts and its coverage, except for damage to
    /// a field whose value cannot be validated from the file alone: a redirected string reference, a line in
    /// range, a coverage count, text (of which an edge kind, a closed set the worker keeps no table of, is one).
    /// Those lie in the string table and the facts, and are reachable only with a hash from the authenticated
    /// snapshot, which is what stands against them; this test recomputes the hash to get there.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tables))]
    public async Task HashCorrectDamage_EndsInARejectionOrARestore(string table)
    {
        var (_, baseline, bytes) = await SavedAsync();
        var layout = WorkerStateFile.LayoutOf(bytes);
        var (start, end) = table switch
        {
            "header" => (0, layout.Strings),
            "strings" => (layout.Strings, layout.Units),
            "units" => (layout.Units, layout.Files),
            "files" => (layout.Files, layout.Hashes),
            "hashes" => (layout.Hashes, layout.Facts),
            _ => (layout.Facts, layout.End),
        };
        Assert.True(end > start);

        var scan = new InProcessCSharpHost(Root);
        await scan.IndexAsync();
        var fresh = scan.Facts();
        var freshCoverage = scan.Coverage();

        var random = new Random(20261106 + table.Length);
        var (refused, rejected, accepted, wrongFacts, wrongCoverage) = (0, 0, 0, 0, 0);
        for (var round = 0; round < Rounds; round++)
        {
            var damaged = Damage(bytes, start, end, random);
            try
            {
                WorkerStateFile.Read(damaged);
            }
            catch (WorkerStateFormatException)
            {
                refused++;
                continue;
            }

            // It parses: the request must still end in an answer.
            File.WriteAllBytes(baseline.StatePath, damaged);
            var offered = baseline with { StateSha256 = Convert.ToHexStringLower(SHA256.HashData(damaged)) };
            // A host that holds what the state was saved from, as one offering this baseline does.
            var host = new InProcessCSharpHost(Root);
            await host.IndexAsync();
            host.Restart();
            var clock = Stopwatch.StartNew();
            var result = await host.IndexAsync(offered).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"{table} round {round} took {clock.Elapsed}");
            if (result.Baseline == BaselineOutcomes.Accepted)
            {
                accepted++;
                Assert.All(host.LastDeltas, delta => Assert.False(delta.ReplacesWorkspace));
                var factsDiffer = !fresh.SequenceEqual(host.Facts());
                var coverageDiffers = freshCoverage != host.Coverage();
                if (!factsDiffer && !coverageDiffers) continue;
                // A field the file alone cannot validate can change and pass; only the hash the host recorded covers that.
                Assert.True(table is "strings" or "facts", $"{table} round {round}: an accepted restore differs from a fresh scan.");
                if (factsDiffer) wrongFacts++;
                if (coverageDiffers) wrongCoverage++;
            }
            else
            {
                Assert.Equal(BaselineOutcomes.Rejected, result.Baseline);
                rejected++;
                Assert.All(host.LastDeltas, delta => Assert.True(delta.ReplacesWorkspace));
            }
        }
        Assert.Equal(Rounds, refused + rejected + accepted);
        Console.WriteLine($"{table}: refused {refused}, rejected {rejected}, accepted {accepted}, of which differing from a fresh scan in facts {wrongFacts}, in coverage {wrongCoverage}");
    }

    /// <summary>One of: random bytes, an extreme 32-bit value, a cut, an insertion; then the length field fixed up or not.</summary>
    private static byte[] Damage(byte[] bytes, int start, int end, Random random)
    {
        var at = random.Next(start, end);
        byte[] damaged;
        switch (random.Next(4))
        {
            case 0:
                damaged = (byte[])bytes.Clone();
                for (var count = random.Next(1, 4); count > 0; count--) damaged[random.Next(start, end)] = (byte)random.Next(256);
                break;
            case 1:
                damaged = (byte[])bytes.Clone();
                at = Math.Min(at, bytes.Length - 4);
                uint[] extremes = [0, 1, 2, 0x7FFFFFFF, 0xFFFFFFFF, 0xFFFFFFFE, (uint)bytes.Length, (uint)random.Next()];
                BinaryPrimitives.WriteUInt32LittleEndian(damaged.AsSpan(at), extremes[random.Next(extremes.Length)]);
                break;
            case 2:
                damaged = bytes[..at];
                break;
            default:
                var inserted = new byte[random.Next(1, 9)];
                random.NextBytes(inserted);
                damaged = [.. bytes[..at], .. inserted, .. bytes[at..]];
                break;
        }
        // Half the time the recorded length is made to agree, so the damage reaches past the header check.
        if (damaged.Length >= 24 && random.Next(2) == 0) BinaryPrimitives.WriteUInt64LittleEndian(damaged.AsSpan(16), (ulong)damaged.Length);
        return damaged;
    }

    [Fact]
    public async Task Save_IntoAMissingDirectory_AnswersNotSaved_AndCreatesNothing()
    {
        var host = new InProcessCSharpHost(Root);
        await host.IndexAsync();
        var missing = Path.Combine(Root, ".codecontext", "cache", "missing", "worker.state");
        var result = await host.Session.SaveStateAsync(new SaveStateParams("default", missing, "id"), CancellationToken.None);
        Assert.False(result.Saved);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
    }

    [Fact]
    public async Task Save_ReplacesAnOldFileAndAnyLeftoverTemp_AndLeavesNoTemp()
    {
        var host = new InProcessCSharpHost(Root);
        await host.IndexAsync();
        var path = Path.Combine(StateDirectory, "worker.state");
        File.WriteAllText(path, "old");
        File.WriteAllText(path + ".tmp", "leftover");
        var result = await host.Session.SaveStateAsync(new SaveStateParams("default", path, "id"), CancellationToken.None);
        Assert.True(result.Saved);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(new FileInfo(path).Length, result.Bytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), result.Sha256);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public async Task Save_Cancelled_LeavesNoFile()
    {
        var host = new InProcessCSharpHost(Root);
        await host.IndexAsync();
        var path = Path.Combine(StateDirectory, "worker.state");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Session.SaveStateAsync(
            new SaveStateParams("default", path, "id"), new CancellationToken(canceled: true)));
        Assert.Empty(Directory.GetFiles(StateDirectory));
    }

    /// <summary>A cancel that arrives while the file is being written stops the writing, not only the rename.</summary>
    [Fact]
    public void Write_CancelledPartWay_StopsBeforeTheEnd()
    {
        var edges = Enumerable.Range(0, 5000).Select(index => new EdgeFact("csharp:default:u:A", "t", "CALLS", index, index)).ToList();
        var state = new WorkerState
        {
            WorkerIdentity = "w", StateId = "s", WorkspaceId = "default", RootPath = "r", Fingerprints = [], FactHashes = [],
            Files = Enumerable.Range(0, 40).Select(index => new WalkedFileState
            {
                Path = $"f{index}.cs",
                Stamp = new FileStamp(1, 1, false, new byte[32]),
                ContainsDirectives = false,
                UnitKey = "u",
                Nodes = [],
                Edges = edges,
                StubNodes = [],
                StubEdges = [],
            }).ToList(),
        };
        var (whole, _) = WorkerStateFile.Write(Stream.Null, state);

        using var cancel = new CancellationTokenSource();
        using var destination = new CancellingStream(cancel);
        Assert.ThrowsAny<OperationCanceledException>(() => WorkerStateFile.Write(destination, state, cancel.Token));
        Assert.InRange(destination.Length, 1, whole - 1);
    }

    /// <summary>Cancels at the first write it receives.</summary>
    private sealed class CancellingStream(CancellationTokenSource cancel) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            base.Write(buffer, offset, count);
            cancel.Cancel();
        }
    }

    [Fact]
    public async Task Save_BeforeAnythingWasEmitted_OrAfterAnUnfinishedEmission_AnswersNotSaved()
    {
        var session = new InProcessCSharpHost(Root);
        var path = Path.Combine(StateDirectory, "worker.state");
        Assert.False((await session.Session.SaveStateAsync(new SaveStateParams("default", path, "id"), CancellationToken.None)).Saved);

        await session.IndexAsync();
        // An analysis whose emission never completed: the walks are ahead of what the host holds.
        session.Analyzer.Analyze(CancellationToken.None);
        Assert.False((await session.Session.SaveStateAsync(new SaveStateParams("default", path, "id"), CancellationToken.None)).Saved);
        Assert.Empty(Directory.GetFiles(StateDirectory));
    }
}
