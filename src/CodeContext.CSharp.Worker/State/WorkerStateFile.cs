using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CodeContext.CSharp.Worker.Projects;
using CodeContext.Parser.Protocol;
using EdgeFact = CodeContext.CSharp.Worker.CSharpWorkspaceAnalyzer.EdgeFact;

namespace CodeContext.CSharp.Worker.State;

/// <summary>What was known about a file's bytes when they were last read.</summary>
/// <param name="Racy">The file was written within <see cref="WorkerStateFile.RacyWindow"/> of the read, so a later
/// write could share its timestamp: its size and time alone never show it unchanged.</param>
internal readonly record struct FileStamp(long Size, long LastWriteTicks, bool Racy, byte[] ContentHash);

/// <summary>
/// Everything a new worker process needs to emit what this one would emit next (plan 3.7): the units'
/// fingerprints as last sent, every walked file's pre-settlement walk with the stamp of the bytes it
/// walked, and the hash of the facts last emitted under each path. Settlement is not stored: it is a
/// pure function of the walks and is rebuilt.
/// </summary>
internal sealed class WorkerState
{
    public required string WorkerIdentity { get; init; }
    public required string StateId { get; init; }
    public required string WorkspaceId { get; init; }
    public required string RootPath { get; init; }
    public required List<UnitFingerprint> Fingerprints { get; init; }
    public required List<WalkedFileState> Files { get; init; }
    public required List<FactHashState> FactHashes { get; init; }
}

/// <summary>One file's walk: its nodes and edges, the library symbols it used, and what it could not resolve.</summary>
internal sealed class WalkedFileState
{
    public required string Path { get; init; }
    public required FileStamp Stamp { get; init; }
    public required bool ContainsDirectives { get; init; }
    public required string UnitKey { get; init; }
    public required List<ProtocolNode> Nodes { get; init; }
    public required List<EdgeFact> Edges { get; init; }
    public required List<ProtocolNode> StubNodes { get; init; }
    public required List<EdgeFact> StubEdges { get; init; }
    public Dictionary<string, int>? UnresolvedNames { get; set; }
}

internal sealed record FactHashState(string Path, byte[] Hash);

/// <summary>A state file that is not one this worker could have written.</summary>
internal sealed class WorkerStateFormatException(string message) : Exception(message);

/// <summary>
/// The worker-private state file. The bytes come from a directory of the repository being indexed, so
/// the file is untrusted: <see cref="Open"/> refuses one over <see cref="MaxFileLength"/> before reading,
/// compares its SHA-256 with the one the host recorded before interpreting a byte, and <see cref="Read"/>
/// parses it by hand, checking every count, length and reference against the buffer, allocating nothing
/// from a count the remaining bytes could not hold, and never naming a type to create. A path in the file
/// is a key only: it is matched against the files the host approved, never opened.
///
/// Layout, little-endian (format version 1):
/// <code>
/// header      "CCWSTATE", u32 version, u32 reserved (0), u64 file length,
///             str workerIdentity, str stateId, str workspaceId, str rootPath   (str: u32 length, UTF-8)
/// strings     u32 count, then count × str                       referenced as u32: 0 null, else index + 1
/// units       u32 count, then count × (ref key, ref fingerprint)
/// files       u32 count, then count × (ref path, i64 size, i64 mtime ticks, u8 flags (1 racy, 2 directives),
///             32 content SHA-256, ref unit key, u32 facts offset, u32 facts length)
/// hashes      u32 count, then count × (ref path, 32 fact SHA-256)
/// facts       u32 length, then each file's facts in file order, contiguous:
///             nodes, edges, stub nodes, stub edges, unresolved names (u32 count or 0xFFFFFFFF for none,
///             then count × (ref name, i32 count))
/// node        ref id, identifier, name, kind, language, filePath; i32 start line, end line, start column,
///             end column; ref namespace, visibility, signature, returnType, parameters, modifiers;
///             u32 metadata count or 0xFFFFFFFF, then count × (ref key, ref value)
/// edge        ref source, ref target, ref kind, i32 line, i32 column   (both -1: no place in the source)
/// </code>
/// </summary>
internal static class WorkerStateFile
{
    public const int FormatVersion = 1;

    /// <summary>A state file longer than this is refused before a byte is read.</summary>
    public const long MaxFileLength = 1L << 30;

    public static readonly TimeSpan RacyWindow = TimeSpan.FromSeconds(2);

    private const uint Absent = uint.MaxValue;
    private const int HashSize = 32;
    private const byte RacyFlag = 1;
    private const byte DirectivesFlag = 2;

    private static ReadOnlySpan<byte> Magic => "CCWSTATE"u8;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The state file's bytes, when it is the one the host recorded: no longer than <paramref name="maxLength"/>
    /// and with the SHA-256 <paramref name="expectedSha256"/> (64 lowercase hex). Null with the reason otherwise.
    /// Nothing is interpreted before the hash matches.
    /// </summary>
    public static byte[]? Open(Disk disk, string path, string expectedSha256, out string? reason, long maxLength = MaxFileLength)
    {
        if (!TryParseSha256(expectedSha256, out var expected))
        {
            reason = "the recorded hash is not a SHA-256";
            return null;
        }

        byte[] bytes;
        try
        {
            using var stream = disk.OpenForOneScan(path);
            if (stream.Length > maxLength)
            {
                reason = "state file is too large";
                return null;
            }
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            reason = "state file is missing";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            reason = "state file could not be read";
            return null;
        }

        if (!MatchesHash(bytes, expected))
        {
            reason = "state file hash does not match";
            return null;
        }
        reason = null;
        return bytes;
    }

    /// <summary>Whether <paramref name="bytes"/> hash to <paramref name="expected"/>, compared in constant time.</summary>
    public static bool MatchesHash(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> expected)
        => CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), expected);

    private static bool TryParseSha256(string? text, out byte[] hash)
    {
        hash = [];
        if (text is not { Length: HashSize * 2 }) return false;
        foreach (var c in text)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        }
        hash = Convert.FromHexString(text);
        return true;
    }

    /// <summary>
    /// Saves <paramref name="state"/> at <paramref name="statePath"/>: written to <c>&lt;statePath&gt;.tmp</c>
    /// (created afresh, user-only on Unix; whatever was at that name is removed first, never written
    /// through), flushed to disk, then renamed over the path. Returns once the file is durable, with its
    /// length and the SHA-256 of exactly the bytes written. No directory is created. On any failure the
    /// temp file is gone and the exception propagates.
    /// </summary>
    public static (long Length, byte[] Sha256) Save(Disk disk, string statePath, WorkerState state, CancellationToken ct)
        => disk.ReplaceStateFile(statePath, stream => Write(stream, state, ct), ct);

    /// <summary>
    /// Writes <paramref name="state"/> to <paramref name="destination"/>; returns the length and SHA-256 of what was
    /// written. Two passes over what is already in memory: the first numbers the strings and sizes every part, the
    /// second streams the file through one small buffer, hashing as it goes, so a save allocates nothing in
    /// proportion to the state but the string table. Stops between files when <paramref name="ct"/> fires.
    /// </summary>
    public static (long Length, byte[] Sha256) Write(Stream destination, WorkerState state, CancellationToken ct = default)
    {
        var strings = new StringTable(state.Files.Sum(file => file.Nodes.Count + file.StubNodes.Count) * 4 + state.Files.Count * 2 + 16);
        var factLengths = new List<long>(state.Files.Count);
        long factsLength = 0;
        foreach (var file in state.Files)
        {
            ct.ThrowIfCancellationRequested();
            var length = InternFacts(strings, file);
            factLengths.Add(length);
            factsLength += length;
        }
        foreach (var fingerprint in state.Fingerprints)
        {
            strings.Ref(fingerprint.Unit);
            strings.Ref(fingerprint.Value);
        }
        foreach (var file in state.Files)
        {
            strings.Ref(file.Path);
            strings.Ref(file.UnitKey);
        }
        foreach (var hash in state.FactHashes) strings.Ref(hash.Path);

        var total = Magic.Length + 4 + 4 + 8
            + StrLength(state.WorkerIdentity) + StrLength(state.StateId) + StrLength(state.WorkspaceId) + StrLength(state.RootPath)
            + 4 + strings.ByteLength
            + 4 + 8L * state.Fingerprints.Count
            + 4 + 65L * state.Files.Count
            + 4 + (4L + HashSize) * state.FactHashes.Count
            + 4 + factsLength;
        if (total > MaxFileLength) throw new InvalidOperationException("The state is too large to save.");

        using var writer = new HashingWriter(destination);
        writer.Raw(Magic);
        writer.U32(FormatVersion);
        writer.U32(0);
        writer.U64((ulong)total);
        writer.Str(state.WorkerIdentity);
        writer.Str(state.StateId);
        writer.Str(state.WorkspaceId);
        writer.Str(state.RootPath);

        writer.U32((uint)strings.Count);
        foreach (var value in strings.Values) writer.Str(value);

        writer.U32((uint)state.Fingerprints.Count);
        foreach (var fingerprint in state.Fingerprints)
        {
            writer.U32(strings.Ref(fingerprint.Unit));
            writer.U32(strings.Ref(fingerprint.Value));
        }

        writer.U32((uint)state.Files.Count);
        long offset = 0;
        for (var index = 0; index < state.Files.Count; index++)
        {
            var file = state.Files[index];
            writer.U32(strings.Ref(file.Path));
            writer.I64(file.Stamp.Size);
            writer.I64(file.Stamp.LastWriteTicks);
            writer.U8((byte)((file.Stamp.Racy ? RacyFlag : 0) | (file.ContainsDirectives ? DirectivesFlag : 0)));
            writer.Raw(Checked(file.Stamp.ContentHash));
            writer.U32(strings.Ref(file.UnitKey));
            writer.U32((uint)offset);
            writer.U32((uint)factLengths[index]);
            offset += factLengths[index];
        }

        writer.U32((uint)state.FactHashes.Count);
        foreach (var hash in state.FactHashes)
        {
            writer.U32(strings.Ref(hash.Path));
            writer.Raw(Checked(hash.Hash));
        }

        writer.U32((uint)factsLength);
        foreach (var file in state.Files)
        {
            ct.ThrowIfCancellationRequested();
            WriteFacts(writer, strings, file);
        }

        var sha256 = writer.Finish();
        if (writer.Written != total) throw new InvalidOperationException($"The state wrote {writer.Written} bytes, {total} expected.");
        return (total, sha256);
    }

    private static ReadOnlySpan<byte> Checked(byte[] hash)
        => hash.Length == HashSize ? hash : throw new ArgumentException("A hash must be 32 bytes.", nameof(hash));

    private static long StrLength(string value) => 4L + StrictUtf8.GetByteCount(value);

    /// <summary>Numbers the strings of one file's facts and returns their length, in the layout <see cref="WriteFacts"/> writes.</summary>
    private static long InternFacts(StringTable strings, WalkedFileState file)
    {
        var length = InternNodes(strings, file.Nodes) + InternEdges(strings, file.Edges)
            + InternNodes(strings, file.StubNodes) + InternEdges(strings, file.StubEdges) + 4L;
        if (file.UnresolvedNames is { } names)
        {
            foreach (var name in names.Keys) strings.Ref(name);
            length += 8L * names.Count;
        }
        return length;
    }

    private static long InternNodes(StringTable strings, List<ProtocolNode> nodes)
    {
        var length = 4L;
        foreach (var node in nodes)
        {
            strings.Ref(node.Id);
            strings.Ref(node.Identifier);
            strings.Ref(node.Name);
            strings.Ref(node.Kind);
            strings.Ref(node.Language);
            strings.Ref(node.FilePath);
            strings.Ref(node.Namespace);
            strings.Ref(node.Visibility);
            strings.Ref(node.Signature);
            strings.Ref(node.ReturnType);
            strings.Ref(node.Parameters);
            strings.Ref(node.Modifiers);
            length += 68;
            if (node.Metadata is not { } metadata) continue;
            foreach (var (key, value) in metadata)
            {
                strings.Ref(key);
                strings.Ref(value);
            }
            length += 8L * metadata.Count;
        }
        return length;
    }

    private static long InternEdges(StringTable strings, List<EdgeFact> edges)
    {
        foreach (var edge in edges)
        {
            strings.Ref(edge.SourceId);
            strings.Ref(edge.TargetId);
            strings.Ref(edge.Kind);
        }
        return 4L + 20L * edges.Count;
    }

    private static void WriteFacts(HashingWriter facts, StringTable strings, WalkedFileState file)
    {
        WriteNodes(facts, strings, file.Nodes);
        WriteEdges(facts, strings, file.Edges);
        WriteNodes(facts, strings, file.StubNodes);
        WriteEdges(facts, strings, file.StubEdges);
        if (file.UnresolvedNames is not { } names)
        {
            facts.U32(Absent);
            return;
        }
        facts.U32((uint)names.Count);
        foreach (var (name, count) in names)
        {
            facts.U32(strings.Ref(name));
            facts.I32(count);
        }
    }

    private static void WriteNodes(HashingWriter facts, StringTable strings, List<ProtocolNode> nodes)
    {
        facts.U32((uint)nodes.Count);
        foreach (var node in nodes)
        {
            facts.U32(strings.Ref(node.Id));
            facts.U32(strings.Ref(node.Identifier));
            facts.U32(strings.Ref(node.Name));
            facts.U32(strings.Ref(node.Kind));
            facts.U32(strings.Ref(node.Language));
            facts.U32(strings.Ref(node.FilePath));
            facts.I32(node.StartLine);
            facts.I32(node.EndLine);
            facts.I32(node.StartColumn);
            facts.I32(node.EndColumn);
            facts.U32(strings.Ref(node.Namespace));
            facts.U32(strings.Ref(node.Visibility));
            facts.U32(strings.Ref(node.Signature));
            facts.U32(strings.Ref(node.ReturnType));
            facts.U32(strings.Ref(node.Parameters));
            facts.U32(strings.Ref(node.Modifiers));
            if (node.Metadata is not { } metadata)
            {
                facts.U32(Absent);
                continue;
            }
            facts.U32((uint)metadata.Count);
            foreach (var (key, value) in metadata)
            {
                facts.U32(strings.Ref(key));
                facts.U32(strings.Ref(value));
            }
        }
    }

    private static void WriteEdges(HashingWriter facts, StringTable strings, List<EdgeFact> edges)
    {
        facts.U32((uint)edges.Count);
        foreach (var edge in edges)
        {
            facts.U32(strings.Ref(edge.SourceId));
            facts.U32(strings.Ref(edge.TargetId));
            facts.U32(strings.Ref(edge.Kind));
            facts.I32(edge.Line);
            facts.I32(edge.Column);
        }
    }

    /// <summary>Where each part of a state file lies, for tests that damage one part at a time.</summary>
    internal sealed record Layout(int Strings, int Units, int Files, int Hashes, int Facts, int End);

    /// <summary>The layout of a valid state file.</summary>
    internal static Layout LayoutOf(ReadOnlySpan<byte> bytes)
    {
        Read(bytes, out var layout);
        return layout;
    }

    /// <summary>Parses a state file whose hash already matched. Throws <see cref="WorkerStateFormatException"/> when it is not well formed.</summary>
    public static WorkerState Read(ReadOnlySpan<byte> bytes) => Read(bytes, out _);

    private static WorkerState Read(ReadOnlySpan<byte> bytes, out Layout layout)
    {
        var reader = new Reader(bytes);
        if (!reader.Raw(Magic.Length).SequenceEqual(Magic)) throw new WorkerStateFormatException("not a worker state file");
        if (reader.U32() != FormatVersion) throw new WorkerStateFormatException("unknown state format version");
        if (reader.U32() != 0) throw new WorkerStateFormatException("reserved header field is set");
        if (reader.U64() != (ulong)bytes.Length) throw new WorkerStateFormatException("recorded length does not match");
        var workerIdentity = reader.Str();
        var stateId = reader.Str();
        var workspaceId = reader.Str();
        var rootPath = reader.Str();

        var stringsAt = reader.Position;
        var stringCount = reader.Count(minimumSize: 4);
        var strings = new string[stringCount];
        for (var index = 0; index < stringCount; index++) strings[index] = reader.Str();

        var unitsAt = reader.Position;
        var unitCount = reader.Count(minimumSize: 8);
        var fingerprints = new List<UnitFingerprint>(unitCount);
        var unitKeys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < unitCount; index++)
        {
            var unit = reader.Ref(strings);
            if (!unitKeys.Add(unit)) throw new WorkerStateFormatException("a unit is listed twice");
            fingerprints.Add(new UnitFingerprint(unit, reader.Ref(strings)));
        }

        var filesAt = reader.Position;
        var fileCount = reader.Count(minimumSize: 65);
        var entries = new List<(string Path, FileStamp Stamp, bool Directives, string Unit, uint Offset, uint Length)>(fileCount);
        for (var index = 0; index < fileCount; index++)
        {
            var path = reader.Ref(strings);
            var size = reader.I64();
            var ticks = reader.I64();
            var flags = reader.U8();
            if ((flags & ~(RacyFlag | DirectivesFlag)) != 0) throw new WorkerStateFormatException("unknown file flags");
            var content = reader.Raw(HashSize).ToArray();
            var unit = reader.Ref(strings);
            if (size < 0) throw new WorkerStateFormatException("a file size is negative");
            entries.Add((path, new FileStamp(size, ticks, (flags & RacyFlag) != 0, content), (flags & DirectivesFlag) != 0,
                unit, reader.U32(), reader.U32()));
        }

        var hashesAt = reader.Position;
        var hashCount = reader.Count(minimumSize: 4 + HashSize);
        var hashes = new List<FactHashState>(hashCount);
        for (var index = 0; index < hashCount; index++)
        {
            hashes.Add(new FactHashState(reader.Ref(strings), reader.Raw(HashSize).ToArray()));
        }

        var factsAt = reader.Position;
        var factsLength = reader.U32();
        if (factsLength != reader.Remaining) throw new WorkerStateFormatException("the facts do not end the file");

        var files = new List<WalkedFileState>(fileCount);
        long next = 0;
        var unresolvedTotal = 0L;
        foreach (var entry in entries)
        {
            if (entry.Offset != next || entry.Length > reader.Remaining) throw new WorkerStateFormatException("a file's facts are out of place");
            next = (long)entry.Offset + entry.Length;
            var block = new Reader(reader.Raw((int)entry.Length));
            var nodes = ReadNodes(ref block, strings);
            var edges = ReadEdges(ref block, strings);
            var stubNodes = ReadNodes(ref block, strings);
            var stubEdges = ReadEdges(ref block, strings);
            if (!stubNodes.TrueForAll(node => node is { StartLine: 0, EndLine: 0, StartColumn: 0, EndColumn: 0 })
                || !stubEdges.TrueForAll(edge => edge.Line < 0))
            {
                throw new WorkerStateFormatException("a library symbol has a source position");
            }
            var unresolved = ReadUnresolved(ref block, strings, ref unresolvedTotal);
            if (block.Remaining != 0) throw new WorkerStateFormatException("a file's facts are longer than they read");
            files.Add(new WalkedFileState
            {
                Path = entry.Path,
                Stamp = entry.Stamp,
                ContainsDirectives = entry.Directives,
                UnitKey = entry.Unit,
                Nodes = nodes,
                Edges = edges,
                StubNodes = stubNodes,
                StubEdges = stubEdges,
                UnresolvedNames = unresolved,
            });
        }
        if (reader.Remaining != 0) throw new WorkerStateFormatException("bytes follow the last file's facts");

        layout = new Layout(stringsAt, unitsAt, filesAt, hashesAt, factsAt, bytes.Length);
        return new WorkerState
        {
            WorkerIdentity = workerIdentity,
            StateId = stateId,
            WorkspaceId = workspaceId,
            RootPath = rootPath,
            Fingerprints = fingerprints,
            Files = files,
            FactHashes = hashes,
        };
    }

    private static List<ProtocolNode> ReadNodes(ref Reader reader, string[] strings)
    {
        var count = reader.Count(minimumSize: 68);
        var nodes = new List<ProtocolNode>(count);
        for (var index = 0; index < count; index++)
        {
            var id = reader.Ref(strings);
            var identifier = reader.Ref(strings);
            var name = reader.Ref(strings);
            var kind = reader.Ref(strings);
            var language = reader.Ref(strings);
            var filePath = reader.OptionalRef(strings);
            var startLine = reader.I32();
            var endLine = reader.I32();
            var startColumn = reader.I32();
            var endColumn = reader.I32();
            if (language != "csharp") throw new WorkerStateFormatException("a node is not C#");
            if (startLine < 0 || startColumn < 0 || endColumn < 0
                || endLine < startLine || (endLine == startLine && endColumn < startColumn))
            {
                throw new WorkerStateFormatException("a node's span is not a span");
            }
            var @namespace = reader.OptionalRef(strings);
            var visibility = reader.OptionalRef(strings);
            var signature = reader.OptionalRef(strings);
            var returnType = reader.OptionalRef(strings);
            var parameters = reader.OptionalRef(strings);
            var modifiers = reader.OptionalRef(strings);
            Dictionary<string, string>? metadata = null;
            if (reader.U32() is var pairs and not Absent)
            {
                var pairCount = reader.Count(pairs, minimumSize: 8);
                metadata = new Dictionary<string, string>(pairCount, StringComparer.Ordinal);
                for (var pair = 0; pair < pairCount; pair++)
                {
                    if (!metadata.TryAdd(reader.Ref(strings), reader.Ref(strings))) throw new WorkerStateFormatException("a metadata key is repeated");
                }
            }
            nodes.Add(new ProtocolNode(id, identifier, name, kind, language, filePath, startLine, endLine, startColumn, endColumn,
                @namespace, visibility, signature, returnType, parameters, modifiers, metadata));
        }
        return nodes;
    }

    private static List<EdgeFact> ReadEdges(ref Reader reader, string[] strings)
    {
        var count = reader.Count(minimumSize: 20);
        var edges = new List<EdgeFact>(count);
        for (var index = 0; index < count; index++)
        {
            var edge = new EdgeFact(reader.Ref(strings), reader.Ref(strings), reader.Ref(strings), reader.I32(), reader.I32());
            // An edge with no place in the source holds -1 for both.
            if (edge.Line < -1 || edge.Column < -1 || (edge.Line < 0) != (edge.Column < 0))
            {
                throw new WorkerStateFormatException("an edge's position is out of range");
            }
            edges.Add(edge);
        }
        return edges;
    }

    /// <summary>Counts are bounded so that no sum over a whole workspace can overflow.</summary>
    private static Dictionary<string, int>? ReadUnresolved(ref Reader reader, string[] strings, ref long total)
    {
        var marker = reader.U32();
        if (marker == Absent) return null;
        var count = reader.Count(marker, minimumSize: 8);
        var names = new Dictionary<string, int>(count, StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var name = reader.Ref(strings);
            var occurrences = reader.I32();
            total += occurrences;
            if (occurrences <= 0 || total > int.MaxValue / 2) throw new WorkerStateFormatException("an unresolved-name count is out of range");
            if (!names.TryAdd(name, occurrences)) throw new WorkerStateFormatException("an unresolved name is repeated");
        }
        return names;
    }

    /// <summary>A bounds-checked cursor. Every read checks the remaining bytes first.</summary>
    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _position;

        public readonly int Position => _position;
        public readonly int Remaining => _bytes.Length - _position;

        public ReadOnlySpan<byte> Raw(int length)
        {
            if (length < 0 || length > Remaining) throw new WorkerStateFormatException("the file ends early");
            var span = _bytes.Slice(_position, length);
            _position += length;
            return span;
        }

        public byte U8() => Raw(1)[0];
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Raw(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Raw(4));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Raw(8));
        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Raw(8));

        /// <summary>A count of items each at least <paramref name="minimumSize"/> bytes, which the rest of the buffer can hold.</summary>
        public int Count(int minimumSize) => Count(U32(), minimumSize);

        public readonly int Count(uint count, int minimumSize)
        {
            if (count > (uint)(Remaining / minimumSize)) throw new WorkerStateFormatException("a count exceeds the bytes left");
            return (int)count;
        }

        public string Str()
        {
            var length = U32();
            if (length > (uint)Remaining) throw new WorkerStateFormatException("a string runs past the end");
            try
            {
                return StrictUtf8.GetString(Raw((int)length));
            }
            catch (DecoderFallbackException)
            {
                throw new WorkerStateFormatException("a string is not UTF-8");
            }
        }

        public string Ref(string[] strings)
            => OptionalRef(strings) ?? throw new WorkerStateFormatException("a required string is missing");

        public string? OptionalRef(string[] strings)
        {
            var reference = U32();
            if (reference == 0) return null;
            if (reference > (uint)strings.Length) throw new WorkerStateFormatException("a string reference is out of range");
            return strings[reference - 1];
        }
    }

    /// <summary>
    /// Strings by first use; a reference is the index plus one, so 0 stands for null. The walks share one
    /// instance per id, so most lookups are answered by reference from a small open-addressed table of this
    /// code's own, without hashing the text or calling through a comparer; only an instance not seen before
    /// is looked up by value.
    /// </summary>
    private sealed class StringTable
    {
        private readonly Dictionary<string, int> _index;
        private string?[] _keys;
        private int[] _references;
        private int _filled;

        public StringTable(int capacity)
        {
            _index = new Dictionary<string, int>(capacity, StringComparer.Ordinal);
            var size = 16;
            while (size < capacity * 2) size <<= 1;
            _keys = new string?[size];
            _references = new int[size];
            Values = new List<string>(capacity);
        }

        public List<string> Values { get; }
        public int Count => Values.Count;

        /// <summary>The table's length in the file: each string's length prefix and bytes.</summary>
        public long ByteLength { get; private set; }

        public uint Ref(string? value)
        {
            if (value is null) return 0;
            var mask = _keys.Length - 1;
            var slot = RuntimeHelpers.GetHashCode(value) & mask;
            while (_keys[slot] is { } key)
            {
                if (ReferenceEquals(key, value)) return (uint)_references[slot];
                slot = (slot + 1) & mask;
            }

            if (!_index.TryGetValue(value, out var reference))
            {
                Values.Add(value);
                ByteLength += StrLength(value);
                reference = Values.Count;
                _index[value] = reference;
            }
            _keys[slot] = value;
            _references[slot] = reference;
            if (++_filled * 2 > _keys.Length) Grow();
            return (uint)reference;
        }

        private void Grow()
        {
            var (keys, references) = (_keys, _references);
            _keys = new string?[keys.Length * 2];
            _references = new int[keys.Length * 2];
            var mask = _keys.Length - 1;
            for (var index = 0; index < keys.Length; index++)
            {
                if (keys[index] is not { } key) continue;
                var slot = RuntimeHelpers.GetHashCode(key) & mask;
                while (_keys[slot] is not null) slot = (slot + 1) & mask;
                _keys[slot] = key;
                _references[slot] = references[index];
            }
        }
    }

    /// <summary>Writes through one 1 MiB buffer, hashing every byte, and counts them.</summary>
    private sealed class HashingWriter(Stream destination) : IDisposable
    {
        private readonly byte[] _buffer = new byte[1 << 20];
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private int _used;

        public long Written { get; private set; }

        private Span<byte> Take(int count)
        {
            if (_used + count > _buffer.Length) Flush();
            var span = _buffer.AsSpan(_used, count);
            _used += count;
            Written += count;
            return span;
        }

        private void Flush()
        {
            _hash.AppendData(_buffer, 0, _used);
            destination.Write(_buffer, 0, _used);
            _used = 0;
        }

        public void Raw(ReadOnlySpan<byte> bytes)
        {
            while (bytes.Length > 0)
            {
                if (_used == _buffer.Length) Flush();
                var count = Math.Min(_buffer.Length - _used, bytes.Length);
                bytes[..count].CopyTo(Take(count));
                bytes = bytes[count..];
            }
        }

        public void U8(byte value) => Take(1)[0] = value;
        public void U32(uint value) => BinaryPrimitives.WriteUInt32LittleEndian(Take(4), value);
        public void I32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), value);
        public void U64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Take(8), value);
        public void I64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), value);

        /// <summary>Strict, so a string that would not read back as itself (a lone surrogate) fails the save.</summary>
        public void Str(string value)
        {
            var count = StrictUtf8.GetByteCount(value);
            U32((uint)count);
            if (count > _buffer.Length)
            {
                Raw(StrictUtf8.GetBytes(value));
                return;
            }
            StrictUtf8.GetBytes(value, Take(count));
        }

        public byte[] Finish()
        {
            Flush();
            return _hash.GetHashAndReset();
        }

        public void Dispose() => _hash.Dispose();
    }
}
