using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Snapshots;

namespace CodeContext.Core.Tests.Snapshots;

using ShardKey = InMemoryDatabase.ShardKey;

/// <summary>
/// The whole-fact comparison of the snapshot gates: every field of every node and edge, metadata pairs in key
/// order, one line per fact, sorted ordinally, so two stores compare equal exactly when they hold the same facts.
/// No field is left out.
/// </summary>
internal static class GraphFacts
{
    public static List<string> Of(IEnumerable<CodeNode> nodes, IEnumerable<CodeEdge> edges)
        => nodes.Select(node => string.Join('|',
                "N", F(node.Id), F(node.Identifier), F(node.Name), F(node.Type), F(node.FilePath),
                I(node.StartLine), I(node.EndLine), I(node.StartCol), I(node.EndCol),
                F(node.Namespace), F(node.Visibility), F(node.Signature), F(node.Language), F(node.ReturnType),
                F(node.Parameters), F(node.Modifiers), F(node.Metrics), M(node.Metadata)))
            .Concat(edges.Select(edge => string.Join('|',
                "E", F(edge.Id), F(edge.SourceId), F(edge.TargetId), F(edge.Type), M(edge.Metadata))))
            .Order(StringComparer.Ordinal)
            .ToList();

    public static List<string> Of(InMemoryDatabase database) => Of(database.EnumerateNodes(), database.EnumerateEdges());

    private static string F(string? value) => value is null
        ? "<null>"
        : "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("|", "\\p").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string M(IReadOnlyDictionary<string, string>? metadata) => metadata is null
        ? "<null>"
        : "{" + string.Join(',', metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => F(pair.Key) + ":" + F(pair.Value))) + "}";
}

/// <summary>
/// A temp directory for one test. Disposing deletes it and asserts it is gone, so a snapshot left
/// mapped or open (which Windows refuses to delete) fails the test that leaked it. Nothing here
/// touches the user profile: every key and snapshot lives inside this directory.
/// </summary>
internal sealed class SnapshotTempDirectory : IDisposable
{
    public SnapshotTempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ccsnap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>A snapshot file name for <paramref name="sequence"/>.</summary>
    public string Snapshot(ulong sequence = 1) => File(SnapshotFile.FormatName(sequence, 0x5d0c9a1e));

    public void Dispose()
    {
        Directory.Delete(Path, recursive: true);
        Assert.False(Directory.Exists(Path), $"temp directory {Path} was not deleted");
    }
}

/// <summary>Writing, loading and comparing snapshots in tests.</summary>
internal static class SnapshotTestSupport
{
    public const string HostVersion = "0.11.0-test+abc";
    public const string RootPath = "C:\\repo";

    public static readonly byte[] Key = Enumerable.Range(0, SnapshotKey.Length).Select(i => (byte)(i * 7 + 1)).ToArray();

    public static readonly byte[] OtherKey = Enumerable.Range(0, SnapshotKey.Length).Select(i => (byte)(255 - i)).ToArray();

    public static SnapshotHostMeta Host(bool caseInsensitive = true) => new(HostVersion, RootPath, "win-x64", caseInsensitive);

    public static SnapshotExpectations Expect(bool caseInsensitive = true) => new(HostVersion, RootPath, caseInsensitive);

    public static IReadOnlyList<KeyValuePair<ShardKey, IGraphShard>> ShardsOf(InMemoryDatabase database)
        => database.ShardKeys
            .Select(key => KeyValuePair.Create(key, (IGraphShard)database.GetShardState(key)!))
            .ToList();

    public static SnapshotWriteInput Input(
        IEnumerable<KeyValuePair<ShardKey, IGraphShard>> shards,
        ulong sequence = 1,
        ulong generation = 7,
        IReadOnlyDictionary<ShardKey, SnapshotShardInfo>? info = null,
        bool caseInsensitive = true)
        => new()
        {
            Shards = shards,
            ShardInfo = info,
            Host = Host(caseInsensitive),
            Sequence = sequence,
            StoreGeneration = generation,
            CreatedUtcTicks = new DateTime(2026, 11, 5, 10, 12, 3, DateTimeKind.Utc).Ticks,
            InstanceId = new Guid("5d0c9a1e-7b3f-4c0e-9a6d-2f8b1c4e7a90"),
        };

    public static byte[] WriteBytes(SnapshotWriteInput input, byte[]? key = null)
    {
        using var stream = new MemoryStream();
        SnapshotWriter.Write(stream, input, key ?? Key);
        return stream.ToArray();
    }

    public static SnapshotWriteResult WriteFile(string path, SnapshotWriteInput input, byte[]? key = null)
        => SnapshotWriter.WriteFile(path, input, key ?? Key);

    /// <summary>Loads a snapshot and asserts it loaded.</summary>
    public static SnapshotLoadResult LoadOrFail(string path, SnapshotExpectations? expected = null)
    {
        var result = SnapshotLoader.Load(path, Key, expected ?? Expect());
        Assert.True(result.Loaded, $"snapshot refused: {result.Rejection} {result.Reason}");
        return result;
    }

    /// <summary>A store holding exactly the given shards.</summary>
    public static InMemoryDatabase StoreOf(IEnumerable<KeyValuePair<ShardKey, IGraphShard>> shards)
    {
        var database = new InMemoryDatabase();
        database.PublishLoadedShards(shards);
        return database;
    }

    /// <summary>Recomputes the HMAC of a damaged file, so the damage reaches the structural checks.</summary>
    public static void Resign(byte[] bytes, byte[]? key = null)
    {
        using var hmac = System.Security.Cryptography.IncrementalHash.CreateHMAC(
            System.Security.Cryptography.HashAlgorithmName.SHA256, key ?? Key);
        hmac.AppendData(bytes.AsSpan(0, bytes.Length - SnapshotFormat.HmacSize));
        hmac.GetHashAndReset().CopyTo(bytes.AsSpan(bytes.Length - SnapshotFormat.HmacSize));
    }

    /// <summary>The name every in-memory open uses (sequence 1, as <see cref="SnapshotTempDirectory.Snapshot"/>).</summary>
    public static readonly string SnapshotName = SnapshotFile.FormatName(1, 0x5d0c9a1e);

    /// <summary>Opens snapshot bytes without a file: the same checks and reader as a file open.</summary>
    public static SnapshotOpenResult OpenInMemory(byte[] bytes, byte[]? key = null)
        => SnapshotFile.OpenInMemory(bytes, SnapshotName, key ?? Key, Expect());

    /// <summary>Opens snapshot bytes in memory and materializes them, as <see cref="SnapshotLoader.Load"/> does for a file.</summary>
    public static SnapshotLoadResult LoadInMemory(byte[] bytes)
    {
        var opened = OpenInMemory(bytes);
        if (opened.File is not { } file) return SnapshotLoadResult.Refused(opened.Rejection, opened.Reason);
        using (file) return SnapshotLoader.Materialize(file);
    }

    /// <summary>Where each part of a snapshot lies, read through the reader of a valid file.</summary>
    public sealed record ShardLayout(int Entry, int Blob, int BlobLength, Dictionary<SnapshotSectionKind, (int Offset, int Length)> Sections);

    public sealed record FileLayout(int Directory, IReadOnlyList<ShardLayout> Shards);

    public static FileLayout LayoutOf(byte[] bytes)
    {
        var opened = OpenInMemory(bytes);
        using var file = opened.File ?? throw new InvalidOperationException($"{opened.Rejection}: {opened.Reason}");
        var directory = (int)file.Header.ShardDirectoryOffset;
        var shards = new List<ShardLayout>();
        for (var i = 0; i < file.Shards.Count; i++)
        {
            var entry = directory + i * SnapshotFormat.DirectoryEntrySize;
            var reader = file.Shards[i];
            shards.Add(new ShardLayout(
                entry,
                (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(entry)),
                (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(entry + 8)),
                Enum.GetValues<SnapshotSectionKind>().ToDictionary(kind => kind, kind =>
                {
                    var (offset, length) = reader.SectionRange(kind);
                    return ((int)offset, (int)length);
                })));
        }
        return new FileLayout(directory, shards);
    }

    public static uint U32(byte[] bytes, long offset) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset));

    public static void W32(byte[] bytes, long offset, uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan((int)offset), value);

    public static void W64(byte[] bytes, long offset, ulong value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan((int)offset), value);

    /// <summary>A copy of <paramref name="bytes"/> changed by <paramref name="damage"/>, then re-signed.</summary>
    public static byte[] Damaged(byte[] bytes, Action<byte[]> damage)
    {
        var copy = (byte[])bytes.Clone();
        damage(copy);
        Resign(copy);
        return copy;
    }

    /// <summary>A heap shard holding exactly these facts, built directly (no commit, no store invariant applied).</summary>
    public static InMemoryDatabase.GraphState StateOf(IEnumerable<CodeNode> nodes, IEnumerable<CodeEdge> edges)
    {
        var state = new InMemoryDatabase.GraphState();
        foreach (var node in nodes)
        {
            state.Nodes[node.Id!] = node;
            if (!string.IsNullOrWhiteSpace(node.Identifier)) state.Identifiers[node.Identifier] = node.Id!;
        }
        foreach (var edge in edges) state.Edges[edge.Id!] = edge;
        return state;
    }

    /// <summary>Commits each shard's facts with a whole-shard scoped commit (the worker path).</summary>
    public static InMemoryDatabase Commit(params (ShardKey Key, IReadOnlyList<CodeNode> Nodes, IReadOnlyList<CodeEdge> Edges)[] shards)
    {
        var database = new InMemoryDatabase();
        var generation = 0L;
        foreach (var (key, nodes, edges) in shards)
            Assert.True(database.TryCommitGeneration(++generation, nodes, edges, new CommitScope(key.ParserId, key.WorkspaceId, null)));
        return database;
    }

    public static Dictionary<string, string> Owned(ShardKey key, params (string Key, string Value)[] pairs)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["parserId"] = key.ParserId,
            ["workspaceId"] = key.WorkspaceId,
        };
        foreach (var (k, v) in pairs) metadata[k] = v;
        return metadata;
    }
}
