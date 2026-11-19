using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CodeContext.CSharp.Worker;
using CodeContext.CSharp.Worker.State;

namespace CodeContext.Core.Tests.Workers;

/// <summary>
/// The bytes <see cref="CSharpWorkspaceAnalyzer.FactHasher"/> hashes. A saved state holds bucket hashes and the
/// host holds fingerprints made with it, so however the hasher gathers its input, the stream it hashes may not
/// change: a state saved by an earlier build of the same worker identity must still restore.
/// </summary>
public sealed class CSharpWorkerFactHasherTests
{
    /// <summary>The framing written out plainly: a marker and a length before each string, a count before each collection.</summary>
    private sealed class Plain
    {
        private readonly MemoryStream _stream = new();

        public void Int(int value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            _stream.Write(bytes);
        }

        public void Bytes(byte[] value)
        {
            Int(value.Length);
            _stream.Write(value);
        }

        public void Str(string? value)
        {
            if (value is null)
            {
                _stream.WriteByte(0);
                return;
            }
            _stream.WriteByte(1);
            Bytes(Encoding.UTF8.GetBytes(value));
        }

        public void Metadata(Dictionary<string, string>? metadata)
        {
            if (metadata is null)
            {
                _stream.WriteByte(0);
                return;
            }
            _stream.WriteByte(1);
            Int(metadata.Count);
            foreach (var pair in metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                Str(pair.Key);
                Str(pair.Value);
            }
        }

        public byte[] HashAndReset()
        {
            var hash = SHA256.HashData(_stream.ToArray());
            _stream.SetLength(0);
            return hash;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void HashesExactlyTheFramedStream_WhateverTheSizesAndAcrossResets(int seed)
    {
        var random = new Random(seed);
        using var hasher = new CSharpWorkspaceAnalyzer.FactHasher();
        var plain = new Plain();
        // Sizes on every side of any buffer a hasher might keep, text of one to four bytes a character.
        int[] lengths = [0, 1, 3, 17, 255, 256, 1023, 4096, 8191, 8192, 8193, 16383, 16384, 16385, 70_000];
        string Text() => string.Create(lengths[random.Next(lengths.Length)], random, static (span, random) =>
        {
            for (var index = 0; index < span.Length; index++) span[index] = "aZ9/é€中"[random.Next(7)];
        });

        for (var round = 0; round < 6; round++)
        {
            for (var step = 0; step < 400; step++)
            {
                switch (random.Next(5))
                {
                    case 0:
                        var number = random.Next(int.MinValue, int.MaxValue);
                        hasher.Int(number);
                        plain.Int(number);
                        break;
                    case 1:
                        var text = random.Next(8) == 0 ? null : Text();
                        hasher.Str(text);
                        plain.Str(text);
                        break;
                    case 2:
                        var bytes = new byte[lengths[random.Next(lengths.Length)]];
                        random.NextBytes(bytes);
                        hasher.Bytes(bytes);
                        plain.Bytes(bytes);
                        break;
                    case 3:
                        var metadata = random.Next(4) == 0 ? null : Enumerable.Range(0, random.Next(4))
                            .ToDictionary(index => $"k{index}-{random.Next(100)}", _ => Text());
                        hasher.Metadata(metadata);
                        plain.Metadata(metadata);
                        break;
                    default:
                        hasher.Str("line");
                        plain.Str("line");
                        break;
                }
            }
            Assert.Equal(Convert.ToHexString(plain.HashAndReset()), Convert.ToHexString(hasher.GetHashAndReset()));
        }

        // Nothing fed hashes as nothing.
        Assert.Equal(Convert.ToHexString(SHA256.HashData([])), Convert.ToHexString(hasher.GetHashAndReset()));
    }

    [Fact]
    public void AFingerprintIsWhatItWasBeforeTheHasherBuffered()
    {
        // Taken from the build before the change: what a host and a saved state already hold.
        var fingerprints = UnitFingerprints.Compute("worker-identity", [
            new UnitFingerprints.UnitInput("Lib", "signature-of-lib", false,
                [new UnitFingerprints.FileInput("/repo/Lib/B.cs", [1, 2, 3], "Lib"), new UnitFingerprints.FileInput("/repo/Lib/A.cs", [], "Lib")], []),
            new UnitFingerprints.UnitInput("App", "signature-of-app", true,
                [new UnitFingerprints.FileInput("/repo/App/Program.cs", new byte[32], "App")], ["Lib"]),
        ]);

        Assert.Equal(
            ["App=5dd0c7c248bd06c6b84cf5bb99ec81ade7165cad2a2797f85a0a8200555a0a7b", "Lib=01afcf7afb8e4c3b4ddf563bb9b4c044bf731c5c6fcfcd67dbbd8b1abd54795a"],
            fingerprints.Select(fingerprint => $"{fingerprint.Unit}={fingerprint.Value}"));
    }

    [Fact]
    public void TheNodesAnEmissionAsksAbout_AreTheNodesOfTheBuckets()
    {
        // Whether an edge's target has a node is part of a bucket's hash. The analyzer answers from its ownership
        // tables; that must be the answer the buckets themselves give, shared ids and external stubs included.
        var root = Directory.CreateTempSubdirectory("cc-csharp-holds-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "A.cs"), "namespace W; public partial class P : Missing { public int A() => new System.Text.StringBuilder().Length; }");
            File.WriteAllText(Path.Combine(root, "B.cs"), "namespace W; public partial class P { public int B() => A() + System.Math.Abs(1); } public class Q : P { }");
            var analyzer = new CSharpWorkspaceAnalyzer("test", root);
            analyzer.ReplaceFiles([Path.Combine(root, "A.cs"), Path.Combine(root, "B.cs")], CancellationToken.None);

            void AssertAgrees(CSharpWorkspaceAnalyzer.AnalysisResult analysis)
            {
                Assert.NotNull(analysis.HasNode);
                Assert.NotEmpty(analysis.NodeIds);
                Assert.All(analysis.NodeIds, id => Assert.True(analysis.Holds(id), id));
                var targets = analysis.Buckets.SelectMany(bucket => bucket.Edges).Select(edge => edge.TargetId).Distinct().ToList();
                Assert.Contains(targets, target => !analysis.NodeIds.Contains(target));
                Assert.All(targets, target => Assert.Equal(analysis.NodeIds.Contains(target), analysis.Holds(target)));
            }

            AssertAgrees(analyzer.Analyze(CancellationToken.None));

            File.WriteAllText(Path.Combine(root, "B.cs"), "namespace W; public partial class P { public int B() => A(); }");
            analyzer.ApplyChanges([new CodeContext.Parser.Protocol.FileChangeDto(Path.Combine(root, "B.cs"), "changed")], CancellationToken.None);
            AssertAgrees(analyzer.Analyze(CancellationToken.None));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
