using System.Globalization;

namespace CodeContext.Core.Snapshots
{
    /// <summary>
    /// The names of worker state files (plan 3.1): <c>worker-&lt;parser&gt;.&lt;10-digit sequence&gt;.&lt;8 lowercase
    /// hex&gt;.state</c>, where the sequence is the snapshot the state was captured for and the hex is the first
    /// eight characters of its state id; a save in progress is that name plus <c>.tmp</c>. A parser id takes
    /// part only when it is 1 to 64 characters of <c>a-z</c>, <c>0-9</c>, <c>-</c> and <c>_</c>, so a name can
    /// never hold a separator, a dot sequence or a drive. The host computes every name from these parts and
    /// never takes one from anywhere else; a name read from a snapshot is used only after
    /// <see cref="TryParseName"/> accepts it, joined to the cache directory.
    /// </summary>
    internal static class WorkerStateFile
    {
        public const string Prefix = "worker-";
        private const string Suffix = ".state";
        private const string TempSuffix = ".tmp";
        private const int SequenceDigits = 10;
        private const int IdDigits = 8;
        private const int MaxParserIdLength = 64;

        /// <summary>Lowercase hex digits in a state id.</summary>
        public const int StateIdLength = 32;

        /// <summary>Lowercase hex digits in a SHA-256.</summary>
        public const int Sha256Length = 64;

        public static bool IsParserIdUsable(string? parserId)
        {
            if (string.IsNullOrEmpty(parserId) || parserId.Length > MaxParserIdLength) return false;
            foreach (var c in parserId)
                if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_')) return false;
            return true;
        }

        /// <summary>The state file name for <paramref name="parserId"/>, or null when the parser id cannot take part.</summary>
        public static string? FormatName(string parserId, ulong sequence, string stateId)
        {
            if (!IsParserIdUsable(parserId) || !IsStateId(stateId) || sequence > 9_999_999_999UL) return null;
            return string.Create(CultureInfo.InvariantCulture,
                $"{Prefix}{parserId}.{sequence:D10}.{stateId.AsSpan(0, IdDigits)}{Suffix}");
        }

        public static string TempNameFor(string stateFileName) => stateFileName + TempSuffix;

        /// <summary>Parses a state file name produced by <see cref="FormatName"/>; anything else is not one.</summary>
        public static bool TryParseName(string? fileName, out string parserId, out ulong sequence)
        {
            parserId = string.Empty;
            sequence = 0;
            if (fileName is null || !fileName.StartsWith(Prefix, StringComparison.Ordinal) || !fileName.EndsWith(Suffix, StringComparison.Ordinal))
                return false;
            var body = fileName.AsSpan(Prefix.Length, fileName.Length - Prefix.Length - Suffix.Length);
            // <parser>.<10 digits>.<8 hex>
            var tail = 1 + SequenceDigits + 1 + IdDigits;
            if (body.Length <= tail) return false;
            var parser = body[..^tail].ToString();
            var rest = body[^tail..];
            if (!IsParserIdUsable(parser) || rest[0] != '.' || rest[1 + SequenceDigits] != '.') return false;
            var digits = rest.Slice(1, SequenceDigits);
            foreach (var c in digits) if (c is < '0' or > '9') return false;
            foreach (var c in rest.Slice(2 + SequenceDigits, IdDigits)) if (!IsLowerHex(c)) return false;
            if (!ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out sequence)) return false;
            parserId = parser;
            return true;
        }

        public static bool TryParseTempName(string fileName, out ulong sequence)
        {
            sequence = 0;
            return fileName.EndsWith(TempSuffix, StringComparison.Ordinal)
                && TryParseName(fileName[..^TempSuffix.Length], out _, out sequence);
        }

        /// <summary>A state id as the host makes them: 32 lowercase hex digits.</summary>
        public static bool IsStateId(string? value) => IsLowerHex(value, StateIdLength);

        /// <summary>A SHA-256 as the protocol carries it: 64 lowercase hex digits.</summary>
        public static bool IsSha256(string? value) => IsLowerHex(value, Sha256Length);

        /// <summary>A new state id: 16 random bytes as lowercase hex.</summary>
        public static string NewStateId() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(StateIdLength / 2));

        private static bool IsLowerHex(string? value, int length)
        {
            if (value is null || value.Length != length) return false;
            foreach (var c in value) if (!IsLowerHex(c)) return false;
            return true;
        }

        private static bool IsLowerHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f');
    }
}
