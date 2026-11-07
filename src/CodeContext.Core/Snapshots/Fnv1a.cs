namespace CodeContext.Core.Snapshots
{
    /// <summary>FNV-1a, 64-bit, over bytes: the key hash of every snapshot hash section.</summary>
    internal static class Fnv1a
    {
        public const ulong OffsetBasis = 14695981039346656037UL;
        public const ulong Prime = 1099511628211UL;

        public static ulong Hash(ReadOnlySpan<byte> bytes) => Append(OffsetBasis, bytes);

        /// <summary>Continues a hash over more bytes, so a key held in several pieces hashes as one.</summary>
        public static ulong Append(ulong hash, ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes)
            {
                hash ^= b;
                hash *= Prime;
            }
            return hash;
        }
    }
}
