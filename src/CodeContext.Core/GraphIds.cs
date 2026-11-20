namespace CodeContext.Core
{
    /// <summary>The parts of a worker-owned id, which starts with <c>parserId:workspaceId:</c>.</summary>
    internal static class GraphIds
    {
        /// <summary>The length of <paramref name="id"/>'s <c>parserId:workspaceId:</c> prefix, or 0 when it has none.</summary>
        public static int OwnerPrefixLength(string id)
        {
            var first = id.IndexOf(':');
            return first < 0 ? 0 : id.IndexOf(':', first + 1) + 1;
        }
    }
}
