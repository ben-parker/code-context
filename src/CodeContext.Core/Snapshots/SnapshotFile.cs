using System.Buffers.Binary;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using ShardKey = CodeContext.Core.Repositories.InMemory.InMemoryDatabase.ShardKey;

namespace CodeContext.Core.Snapshots
{
    /// <summary>What a reader requires a snapshot to have been written for.</summary>
    internal sealed record SnapshotExpectations(string HostVersion, string RootPath, bool PathsCaseInsensitive);

    /// <summary>Why a snapshot file was refused, in the order the checks run.</summary>
    internal enum SnapshotRejection
    {
        None,
        BigEndianProcess,
        Name,
        Io,
        /// <summary>Unix: group or others may write the file, so another descriptor may change it after authentication.</summary>
        SharedWritable,
        /// <summary>Unix: the file's mode cannot be set by this user, so it is not this user's file.</summary>
        NotOwned,
        TooShort,
        /// <summary>The file is longer than <see cref="SnapshotFormat.MaxFileLength"/>; refused before hashing.</summary>
        TooLong,
        Magic,
        EndianTag,
        FormatVersion,
        HeaderSize,
        LengthMismatch,
        NameMismatch,
        HostMeta,
        HostVersion,
        RootPath,
        PathCase,
        Footer,
        Authentication,
        Structure,
        /// <summary>An exception no check anticipated (a reader bug); the file is refused and nothing stays open.</summary>
        Unexpected,
    }

    /// <summary>The fixed header fields of an opened snapshot.</summary>
    internal sealed record SnapshotHeader(
        uint Flags,
        long FileLength,
        ulong Sequence,
        ulong StoreGeneration,
        long CreatedUtcTicks,
        long HostMetaOffset,
        uint HostMetaLength,
        uint ShardCount,
        long ShardDirectoryOffset,
        Guid InstanceId)
    {
        public bool PathsCaseInsensitive => (Flags & SnapshotFormat.FlagPathsCaseInsensitive) != 0;
    }

    /// <summary>The outcome of <see cref="SnapshotFile.Open"/>: an opened file, or the check that refused it.</summary>
    internal sealed record SnapshotOpenResult(SnapshotFile? File, SnapshotRejection Rejection, string? Reason)
    {
        /// <summary>
        /// The header and host meta checks passed before the refusal, so the file was (or was being) read and
        /// hashed in full: an I/O failure from here on costs as much as an authentication failure.
        /// </summary>
        public bool PassedHostMeta { get; init; }

        public static SnapshotOpenResult Refused(SnapshotRejection rejection, string reason) => new(null, rejection, reason);
    }

    /// <summary>
    /// A bounds-checked read-only window over a mapped snapshot. Every read of mapped bytes goes
    /// through <see cref="Slice"/>, which throws <see cref="SnapshotCorruptException"/> for a range
    /// outside the region. The span it returns lives only inside the read call that asked for it:
    /// nothing stores a span or the pointer.
    ///
    /// Not safe against a read concurrent with <see cref="Dispose"/>: a read that has passed the
    /// disposed check can still touch the view after it is unmapped. Phase 2 has one user, the
    /// loader, which reads on one thread and disposes afterwards. Phase 8 adds a lease per read
    /// (the view's handle reference held for the read, the unmap after the last lease) before any
    /// reader shares a region across threads. <see cref="Dispose"/> itself is idempotent and safe
    /// against two concurrent calls.
    /// </summary>
    internal sealed unsafe class MappedRegion : IDisposable
    {
        private readonly MemoryMappedViewAccessor _view;
        private nint _address;

        public MappedRegion(MemoryMappedViewAccessor view, long length)
        {
            _view = view;
            byte* pointer = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            _address = (nint)(pointer + view.PointerOffset);
            Length = length;
        }

        public long Length { get; }

        /// <summary>The bytes <c>[offset, offset + length)</c>; throws <see cref="SnapshotCorruptException"/> outside the region.</summary>
        public ReadOnlySpan<byte> Slice(long offset, long length)
        {
            var address = Volatile.Read(ref _address);
            if (address == 0) throw new ObjectDisposedException(nameof(MappedRegion));
            if (offset < 0 || length < 0 || offset > Length || length > Length - offset || length > int.MaxValue)
                throw new SnapshotCorruptException($"Range [{offset}, +{length}) lies outside the {Length}-byte snapshot.");
            return new ReadOnlySpan<byte>((byte*)address + offset, (int)length);
        }

        public uint ReadU32(long offset) => BinaryPrimitives.ReadUInt32LittleEndian(Slice(offset, 4));

        public int ReadI32(long offset) => BinaryPrimitives.ReadInt32LittleEndian(Slice(offset, 4));

        public ulong ReadU64(long offset) => BinaryPrimitives.ReadUInt64LittleEndian(Slice(offset, 8));

        public void Dispose()
        {
            // Exactly one caller releases the pointer and the view, however many race here.
            if (Interlocked.Exchange(ref _address, 0) == 0) return;
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
        }
    }

    /// <summary>
    /// An opened, authenticated and structurally validated snapshot file, mapped read-only. Opening
    /// follows the validation order of plan section 3.2: name; length and header; host meta; a
    /// streaming HMAC over the same handle before anything is mapped; then the mapping and the
    /// directory and section tables. The file stays open deny-write until disposed (on Windows; on
    /// Unix sharing is advisory, so <see cref="Open"/> instead claims the file, see there).
    ///
    /// A read concurrent with <see cref="Dispose"/> is not safe today (see <see cref="MappedRegion"/>);
    /// phase 8 adds a lease per read. <see cref="Dispose"/> is idempotent.
    /// </summary>
    internal sealed class SnapshotFile : IDisposable
    {
        private const string NamePrefix = "graph.";
        private const string NameSuffix = ".ccsnap";
        private const int SequenceDigits = 10;
        private const int IdDigits = 8;
        private const int HashChunk = 1 << 20;

        /// <summary>The most characters of text from an unauthenticated file a refusal reason quotes.</summary>
        private const int MaxQuotedLength = 200;

        private readonly SafeFileHandle? _handle;
        private readonly MemoryMappedFile _mapping;
        private readonly MappedRegion _region;
        private int _disposed;

        private SnapshotFile(
            SafeFileHandle? handle, MemoryMappedFile mapping, MappedRegion region, SnapshotHeader header,
            IReadOnlyDictionary<string, string> hostMeta)
        {
            _handle = handle;
            _mapping = mapping;
            _region = region;
            Header = header;
            HostMeta = hostMeta;
            Shards = Array.Empty<SnapshotShardReader>();
        }

        public SnapshotHeader Header { get; }

        public IReadOnlyDictionary<string, string> HostMeta { get; }

        /// <summary>One reader per shard, in directory (shard key) order.</summary>
        public IReadOnlyList<SnapshotShardReader> Shards { get; private set; }

        public MappedRegion Region => _region;

        /// <summary>
        /// Parses a snapshot name, <c>graph.&lt;10-digit sequence&gt;.&lt;8 lowercase hex&gt;.ccsnap</c>. A temp
        /// file (<c>.tmp</c>) or any other name is not a snapshot.
        /// </summary>
        public static bool TryParseName(string fileName, out ulong sequence)
        {
            sequence = 0;
            if (fileName.Length != NamePrefix.Length + SequenceDigits + 1 + IdDigits + NameSuffix.Length
                || !fileName.StartsWith(NamePrefix, StringComparison.Ordinal)
                || !fileName.EndsWith(NameSuffix, StringComparison.Ordinal)
                || fileName[NamePrefix.Length + SequenceDigits] != '.')
                return false;
            var digits = fileName.AsSpan(NamePrefix.Length, SequenceDigits);
            foreach (var c in digits) if (c is < '0' or > '9') return false;
            foreach (var c in fileName.AsSpan(NamePrefix.Length + SequenceDigits + 1, IdDigits))
                if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
            return ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
        }

        /// <summary>The name of snapshot <paramref name="sequence"/> with the 8-hex-digit <paramref name="id"/>.</summary>
        public static string FormatName(ulong sequence, uint id)
            => string.Create(CultureInfo.InvariantCulture, $"{NamePrefix}{sequence:D10}.{id:x8}{NameSuffix}");

        /// <summary>
        /// Opens, authenticates and validates a snapshot. Never throws for a bad file: every failure is
        /// returned with the check that caught it, and nothing (handle, mapping, view) stays open after
        /// a refusal. Nothing is mapped before the HMAC verifies. Throws only for a bad argument (a key
        /// that is not <see cref="SnapshotKey.Length"/> bytes) and lets an <see cref="OutOfMemoryException"/>
        /// propagate after releasing everything.
        /// </summary>
        public static SnapshotOpenResult Open(string path, byte[] key, SnapshotExpectations expected)
            => Open(path, key, expected, SnapshotFormat.MaxFileLength);

        /// <summary><see cref="Open(string, byte[], SnapshotExpectations)"/> with the length cap given, for tests.</summary>
        internal static SnapshotOpenResult Open(string path, byte[] key, SnapshotExpectations expected, long maxFileLength)
        {
            if (CheckArguments(path, key, expected, out var nameSequence) is { } early) return early;

            SafeFileHandle handle;
            try
            {
                handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return SnapshotOpenResult.Refused(SnapshotRejection.Io, exception.Message);
            }

            var passedHostMeta = false;
            var result = OpenMapped(handle, () =>
            {
                if (!OperatingSystem.IsWindows() && ClaimUnixFile(handle) is { } claimRefusal) return (claimRefusal, null);
                var length = RandomAccess.GetLength(handle);
                var refusal = Precheck((buffer, offset) => ReadExactly(handle, buffer, offset), length, nameSequence, key, expected, maxFileLength,
                    out var header, out var hostMeta, () => passedHostMeta = true);
                if (refusal is not null) return (refusal, null);
                var mapping = MemoryMappedFile.CreateFromFile(handle, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
                return (null, (mapping, header!, hostMeta!));
            });
            return result.File is null && passedHostMeta ? result with { PassedHostMeta = true } : result;
        }

        /// <summary>
        /// Steps 2 to 5 over bytes in memory, mapped from an anonymous memory-mapped region instead of a
        /// file: the same checks and the same reader as <see cref="Open(string, byte[], SnapshotExpectations)"/>
        /// without a file per call. For tests that open thousands of damaged files.
        /// </summary>
        internal static SnapshotOpenResult OpenInMemory(byte[] bytes, string fileName, byte[] key, SnapshotExpectations expected)
        {
            if (CheckArguments(fileName, key, expected, out var nameSequence) is { } early) return early;
            return OpenMapped(null, () =>
            {
                var refusal = Precheck(ReadFrom(bytes), bytes.Length, nameSequence, key, expected, SnapshotFormat.MaxFileLength,
                    out var header, out var hostMeta);
                if (refusal is not null) return (refusal, null);
                var mapping = MemoryMappedFile.CreateNew(null, bytes.Length, MemoryMappedFileAccess.ReadWrite);
                try
                {
                    using var stream = mapping.CreateViewStream(0, bytes.Length, MemoryMappedFileAccess.Write);
                    stream.Write(bytes);
                }
                catch
                {
                    mapping.Dispose();
                    throw;
                }
                return (null, (mapping, header!, hostMeta!));
            });
        }

        private static SnapshotOpenResult? CheckArguments(string path, byte[] key, SnapshotExpectations expected, out ulong nameSequence)
        {
            ArgumentNullException.ThrowIfNull(path);
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(expected);
            if (key.Length != SnapshotKey.Length) throw new ArgumentException($"The snapshot key must be {SnapshotKey.Length} bytes.", nameof(key));
            nameSequence = 0;
            if (!BitConverter.IsLittleEndian)
                return SnapshotOpenResult.Refused(SnapshotRejection.BigEndianProcess, "snapshots are read only by little-endian processes");
            if (!TryParseName(Path.GetFileName(path), out nameSequence))
                return SnapshotOpenResult.Refused(SnapshotRejection.Name, "the file name is not a snapshot name");
            return null;
        }

        private delegate ((SnapshotRejection Rejection, string Reason)? Refusal, (MemoryMappedFile Mapping, SnapshotHeader Header, Dictionary<string, string> HostMeta)? Mapped) PrepareMapping();

        /// <summary>
        /// Runs <paramref name="prepare"/> (the checks before mapping, then the mapping), maps one view
        /// and validates the directory and every shard's tables. Every exception after the handle was
        /// opened ends in a refusal with everything released: a structural failure is
        /// <see cref="SnapshotRejection.Structure"/>; an I/O failure (including a view beyond a file that
        /// shrank after its length was read) is <see cref="SnapshotRejection.Io"/>; anything else is
        /// <see cref="SnapshotRejection.Unexpected"/>. An <see cref="OutOfMemoryException"/> is released
        /// and rethrown: the process cannot be trusted to continue normally after it.
        /// </summary>
        private static SnapshotOpenResult OpenMapped(SafeFileHandle? handle, PrepareMapping prepare)
        {
            MemoryMappedFile? mapping = null;
            MemoryMappedViewAccessor? view = null;
            MappedRegion? region = null;
            SnapshotFile? file = null;
            try
            {
                var (refusal, mapped) = prepare();
                if (refusal is { } r) return SnapshotOpenResult.Refused(r.Rejection, r.Reason);
                var (mappedFile, header, hostMeta) = mapped!.Value;
                mapping = mappedFile;
                try
                {
                    view = mapping.CreateViewAccessor(0, header.FileLength, MemoryMappedFileAccess.Read);
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    // The file shrank after its length was read: a view beyond its end.
                    return SnapshotOpenResult.Refused(SnapshotRejection.Io, exception.Message);
                }
                region = new MappedRegion(view, header.FileLength);
                view = null;   // the region owns it now
                file = new SnapshotFile(handle, mapping, region, header, hostMeta);
                file.Shards = file.ValidateDirectory();
                var result = new SnapshotOpenResult(file, SnapshotRejection.None, null);
                file = null;
                handle = null;
                mapping = null;
                region = null;
                return result;
            }
            catch (SnapshotCorruptException exception)
            {
                return SnapshotOpenResult.Refused(SnapshotRejection.Structure, exception.Message);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return SnapshotOpenResult.Refused(SnapshotRejection.Io, exception.Message);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return SnapshotOpenResult.Refused(SnapshotRejection.Unexpected, $"{exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                if (file is not null)
                {
                    file.Dispose();
                }
                else
                {
                    region?.Dispose();
                    view?.Dispose();
                    mapping?.Dispose();
                    handle?.Dispose();
                }
            }
        }

        /// <summary>
        /// Unix only, on the open handle and before anything is hashed. Unix file sharing is advisory, so
        /// the HMAC check and every later read are only as trustworthy as the inode: whoever can write the
        /// file can change it after authentication. Two steps close that. A mode that grants group or
        /// others write is refused, because another user may already hold a writable descriptor that no
        /// later mode change revokes. Then, unless the mode is already exactly 0600 (see
        /// <see cref="NeedsClaim"/>), the mode is set to user read/write: fchmod succeeds only for the
        /// file's owner (or root), so a permission failure means the file is not this user's, and it is
        /// refused; any other failure (a read-only file system) is an I/O refusal. After both, only this
        /// user (or root) can open the file for writing. The writer creates snapshots with mode 0600, so
        /// neither step changes a file it wrote, and a file it wrote opens on a read-only mount.
        /// </summary>
        [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
        private static (SnapshotRejection Rejection, string Reason)? ClaimUnixFile(SafeFileHandle handle)
        {
            var mode = File.GetUnixFileMode(handle);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                return (SnapshotRejection.SharedWritable, $"group or others may write the file (mode {Convert.ToString((int)mode, 8)})");
            if (!NeedsClaim(mode)) return null;
            try
            {
                File.SetUnixFileMode(handle, UserReadWrite);
            }
            catch (UnauthorizedAccessException)
            {
                return (SnapshotRejection.NotOwned, "the file is not owned by this user (its mode cannot be set)");
            }
            catch (IOException exception)
            {
                return (SnapshotRejection.Io, $"the file's mode cannot be set: {exception.Message}");
            }
            return null;
        }

        private const UnixFileMode UserReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        /// <summary>
        /// Whether a file of <paramref name="mode"/> (already free of group and other write) must be claimed
        /// with fchmod. Not when it is exactly 0600: only its owner (or root) could have opened it for
        /// reading, so it is this user's already. A readable file owned by someone else must grant group or
        /// others read, so it still reaches fchmod and fails with a permission error.
        /// </summary>
        internal static bool NeedsClaim(UnixFileMode mode) => mode != UserReadWrite;

        /// <summary>Reads <paramref name="buffer"/>.Length bytes at <paramref name="offset"/>, or throws an <see cref="IOException"/>.</summary>
        private delegate void ReadAt(Span<byte> buffer, long offset);

        private static ReadAt ReadFrom(byte[] bytes) => (buffer, offset) =>
        {
            if (offset < 0 || offset > bytes.Length || buffer.Length > bytes.Length - offset)
                throw new IOException("The snapshot ended early.");
            bytes.AsSpan((int)offset, buffer.Length).CopyTo(buffer);
        };

        /// <summary>
        /// Validation steps 2 to 4 over a file of <paramref name="length"/> bytes: length (at least the
        /// header and footer, at most <paramref name="maxFileLength"/>, both before anything is read) and
        /// header, host meta, then the streaming HMAC. Reads through <paramref name="read"/> only; maps nothing.
        /// </summary>
        private static (SnapshotRejection Rejection, string Reason)? Precheck(
            ReadAt read, long length, ulong nameSequence, byte[] key, SnapshotExpectations expected, long maxFileLength,
            out SnapshotHeader? header, out Dictionary<string, string>? hostMeta, Action? hostMetaPassed = null)
        {
            header = null;
            hostMeta = null;
            if (length < SnapshotFormat.MinimumFileLength) return (SnapshotRejection.TooShort, $"the file is {length} bytes");
            if (length > maxFileLength) return (SnapshotRejection.TooLong, $"the file is {length} bytes, above the {maxFileLength}-byte limit");

            var headerBytes = new byte[SnapshotFormat.HeaderSize];
            read(headerBytes, 0);
            var refusal = CheckHeader(headerBytes, length, nameSequence, out header);
            if (refusal is not null) return refusal;

            var hostMetaBytes = new byte[header!.HostMetaLength];
            read(hostMetaBytes, header.HostMetaOffset);
            if (!TryParseKeyValues(hostMetaBytes, out var meta)) return (SnapshotRejection.HostMeta, "the host meta block is malformed");
            if (!meta.TryGetValue(SnapshotFormat.HostVersionKey, out var hostVersion) || hostVersion != expected.HostVersion)
                return (SnapshotRejection.HostVersion, $"written by host {Quote(hostVersion)}, this host is {Quote(expected.HostVersion)}");
            var rootComparer = expected.PathsCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (!meta.TryGetValue(SnapshotFormat.RootPathKey, out var rootPath) || !rootComparer.Equals(rootPath, expected.RootPath))
                return (SnapshotRejection.RootPath, $"written for root {Quote(rootPath)}");
            if (header.PathsCaseInsensitive != expected.PathsCaseInsensitive)
                return (SnapshotRejection.PathCase, "written with another path case rule");
            hostMetaPassed?.Invoke();

            var footer = new byte[SnapshotFormat.FooterSize];
            read(footer, length - SnapshotFormat.FooterSize);
            if (!footer.AsSpan(0, 8).SequenceEqual(SnapshotFormat.FooterMagic)) return (SnapshotRejection.Footer, "the footer magic is wrong");
            if (!Authenticate(read, length, key, footer.AsSpan(8, SnapshotFormat.HmacSize)))
                return (SnapshotRejection.Authentication, "the HMAC does not verify with this user's key");
            hostMeta = meta;
            return null;
        }

        /// <summary>
        /// Steps 1 to 4 over the bytes of a file named <paramref name="fileName"/>, with no file and no
        /// mapping: the same checks <see cref="Open"/> runs before it maps. For tests that try every
        /// truncation and every byte change of a file.
        /// </summary>
        internal static (SnapshotRejection Rejection, string? Reason) Precheck(
            byte[] bytes, string fileName, byte[] key, SnapshotExpectations expected, long maxFileLength = SnapshotFormat.MaxFileLength)
        {
            if (!TryParseName(fileName, out var nameSequence)) return (SnapshotRejection.Name, "the file name is not a snapshot name");
            (SnapshotRejection Rejection, string Reason)? refusal;
            try
            {
                refusal = Precheck(ReadFrom(bytes), bytes.Length, nameSequence, key, expected, maxFileLength, out _, out _);
            }
            catch (IOException exception)
            {
                return (SnapshotRejection.Io, exception.Message);
            }
            return refusal is { } r ? (r.Rejection, r.Reason) : (SnapshotRejection.None, null);
        }

        private static (SnapshotRejection Rejection, string Reason)? CheckHeader(
            ReadOnlySpan<byte> bytes, long length, ulong nameSequence, out SnapshotHeader? header)
        {
            header = null;
            if (!bytes[..8].SequenceEqual(SnapshotFormat.HeaderMagic)) return (SnapshotRejection.Magic, "the header magic is wrong");
            if (SnapshotFormat.ReadU32(bytes, SnapshotFormat.HeaderEndianTag) != SnapshotFormat.EndianTag)
                return (SnapshotRejection.EndianTag, "the endian tag is wrong");
            var version = SnapshotFormat.ReadU32(bytes, SnapshotFormat.HeaderFormatVersion);
            if (version != SnapshotFormat.FormatVersion) return (SnapshotRejection.FormatVersion, $"format version {version}");
            if (SnapshotFormat.ReadU32(bytes, SnapshotFormat.HeaderHeaderSize) != SnapshotFormat.HeaderSize)
                return (SnapshotRejection.HeaderSize, "the header size is wrong");
            var fileLength = SnapshotFormat.ReadU64(bytes, SnapshotFormat.HeaderFileLength);
            if (fileLength != (ulong)length) return (SnapshotRejection.LengthMismatch, $"the header says {fileLength} bytes, the file has {length}");
            var sequence = SnapshotFormat.ReadU64(bytes, SnapshotFormat.HeaderSequence);
            if (sequence != nameSequence) return (SnapshotRejection.NameMismatch, $"the name says sequence {nameSequence}, the header {sequence}");

            var hostMetaOffset = SnapshotFormat.ReadU64(bytes, SnapshotFormat.HeaderHostMetaOffset);
            var hostMetaLength = SnapshotFormat.ReadU32(bytes, SnapshotFormat.HeaderHostMetaLength);
            var metaLimit = (ulong)(length - SnapshotFormat.FooterSize);
            if (hostMetaOffset != SnapshotFormat.HeaderSize
                || hostMetaLength > SnapshotFormat.MaxHostMetaLength || hostMetaLength > metaLimit - hostMetaOffset)
                return (SnapshotRejection.HostMeta, "the host meta block does not follow the header inside the file");

            header = new SnapshotHeader(
                SnapshotFormat.ReadU32(bytes, SnapshotFormat.HeaderFlags),
                length,
                sequence,
                SnapshotFormat.ReadU64(bytes, SnapshotFormat.HeaderStoreGeneration),
                BinaryPrimitives.ReadInt64LittleEndian(bytes[SnapshotFormat.HeaderCreatedUtcTicks..]),
                (long)hostMetaOffset,
                hostMetaLength,
                SnapshotFormat.ReadU32(bytes, SnapshotFormat.HeaderShardCount),
                // Checked against the file when the directory is validated; clamped so it stays a long.
                (long)Math.Min(SnapshotFormat.ReadU64(bytes, SnapshotFormat.HeaderShardDirectoryOffset), long.MaxValue),
                new Guid(bytes.Slice(SnapshotFormat.HeaderInstanceId, 16)));
            return null;
        }

        private static bool Authenticate(ReadAt read, long length, byte[] key, ReadOnlySpan<byte> expectedMac)
        {
            using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
            var buffer = new byte[HashChunk];
            var end = length - SnapshotFormat.HmacSize;
            for (long offset = 0; offset < end;)
            {
                var count = (int)Math.Min(buffer.Length, end - offset);
                read(buffer.AsSpan(0, count), offset);
                hmac.AppendData(buffer.AsSpan(0, count));
                offset += count;
            }
            Span<byte> actual = stackalloc byte[SnapshotFormat.HmacSize];
            hmac.GetHashAndReset(actual);
            return CryptographicOperations.FixedTimeEquals(actual, expectedMac);
        }

        /// <summary>
        /// Text from a file that is not yet authenticated, made safe to put in a refusal reason: quoted,
        /// at most <see cref="MaxQuotedLength"/> characters, control and format characters escaped.
        /// </summary>
        internal static string Quote(string? text)
        {
            if (text is null) return "(none)";
            var builder = new System.Text.StringBuilder(Math.Min(text.Length, MaxQuotedLength) + 8);
            builder.Append('\'');
            for (var i = 0; i < text.Length && i < MaxQuotedLength; i++)
            {
                var c = text[i];
                if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format
                        or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                    builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                else
                    builder.Append(c);
            }
            if (text.Length > MaxQuotedLength) builder.Append("...");
            return builder.Append('\'').ToString();
        }

        private static void ReadExactly(SafeFileHandle handle, Span<byte> buffer, long offset)
        {
            while (buffer.Length > 0)
            {
                var read = RandomAccess.Read(handle, buffer, offset);
                if (read <= 0) throw new IOException("The snapshot ended early.");
                buffer = buffer[read..];
                offset += read;
            }
        }

        /// <summary>Parses <c>u32 count, (u16 keyLength, key, u32 valueLength, value)*</c>; false on any damage or duplicate key.</summary>
        internal static bool TryParseKeyValues(ReadOnlySpan<byte> bytes, out Dictionary<string, string> values)
        {
            values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (bytes.Length < 4) return false;
            var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            var position = 4;
            for (uint i = 0; i < count; i++)
            {
                if (bytes.Length - position < 2) return false;
                int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[position..]);
                position += 2;
                if (bytes.Length - position < keyLength) return false;
                var keyBytes = bytes.Slice(position, keyLength);
                position += keyLength;
                if (bytes.Length - position < 4) return false;
                var valueLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]);
                position += 4;
                if ((uint)(bytes.Length - position) < valueLength) return false;
                var valueBytes = bytes.Slice(position, (int)valueLength);
                position += (int)valueLength;
                if (!System.Text.Unicode.Utf8.IsValid(keyBytes) || !System.Text.Unicode.Utf8.IsValid(valueBytes)) return false;
                if (!values.TryAdd(SnapshotFormat.StrictUtf8.GetString(keyBytes), SnapshotFormat.StrictUtf8.GetString(valueBytes))) return false;
            }
            return position == bytes.Length;
        }

        /// <summary>
        /// Validation step 5 for the directory, then each shard's tables. The layout is the writer's,
        /// exactly: the directory at the 8-byte boundary after host meta, each blob at the boundary
        /// after the previous one (so none overlaps the directory, another blob or the footer), the
        /// footer at the boundary after the last blob; shards in strictly ascending key order.
        /// </summary>
        private List<SnapshotShardReader> ValidateDirectory()
        {
            var header = Header;
            var footerOffset = header.FileLength - SnapshotFormat.FooterSize;
            var directoryOffset = header.ShardDirectoryOffset;
            var directoryLength = (long)header.ShardCount * SnapshotFormat.DirectoryEntrySize;
            if (directoryOffset != SnapshotFormat.Align8(header.HostMetaOffset + header.HostMetaLength)
                || directoryLength > footerOffset - directoryOffset)
                throw new SnapshotCorruptException("The shard directory does not follow host meta inside the file.");

            var readers = new List<SnapshotShardReader>();
            var previousEnd = directoryOffset + directoryLength;
            ShardKey? previousKey = null;
            for (var i = 0; i < header.ShardCount; i++)
            {
                // Entry: u64 blobOffset, u64 blobLength, 32 bytes blobSha256, u32 nodeCount, u32 edgeCount,
                // u32 flags, u32 reserved. blobSha256 is written for phase 8, which copies an unchanged blob
                // into the next file byte for byte and compares hashes instead of bytes; it is covered by
                // the file's HMAC like every other byte and is not verified at load.
                var entry = directoryOffset + (long)i * SnapshotFormat.DirectoryEntrySize;
                var blobOffset = _region.ReadU64(entry);
                var blobLength = _region.ReadU64(entry + 8);
                if (blobOffset != (ulong)SnapshotFormat.Align8(previousEnd) || blobLength > (ulong)(footerOffset - (long)blobOffset))
                    throw new SnapshotCorruptException($"Shard blob {i} does not start where the previous part ends, or runs past the footer.");
                var reader = new SnapshotShardReader(
                    _region, (long)blobOffset, (long)blobLength, _region.ReadU32(entry + 48), _region.ReadU32(entry + 52));
                if (previousKey is { } previous && ShardKey.Comparer.Compare(previous, reader.Key) >= 0)
                    throw new SnapshotCorruptException("Shards are not in strictly ascending key order.");
                previousKey = reader.Key;
                previousEnd = (long)(blobOffset + blobLength);
                readers.Add(reader);
            }
            if (SnapshotFormat.Align8(previousEnd) != footerOffset)
                throw new SnapshotCorruptException("The footer does not follow the last shard blob.");
            return readers;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _region.Dispose();
            _mapping.Dispose();
            _handle?.Dispose();
        }
    }
}
