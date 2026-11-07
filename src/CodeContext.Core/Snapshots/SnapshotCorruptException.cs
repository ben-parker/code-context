namespace CodeContext.Core.Snapshots
{
    /// <summary>
    /// A snapshot value failed a structural check: an offset, length, count, string reference or index
    /// outside its section, or a table that contradicts another. The only exception the snapshot
    /// reader throws for bad bytes; met after authentication it means a writer bug, and the shard is
    /// never answered from.
    /// </summary>
    internal sealed class SnapshotCorruptException : Exception
    {
        public SnapshotCorruptException(string message)
            : base(message)
        {
        }
    }
}
