namespace CodeContext.Core.Instances
{
    public interface IInstanceRegistry
    {
        /// <summary>Returns all live instances, pruning entries whose process has died.</summary>
        IReadOnlyList<InstanceRecord> GetAll();

        /// <summary>Registers an instance, replacing any existing entry for the same root path.</summary>
        void Register(InstanceRecord record);

        /// <summary>
        /// Removes the entry for the given root path, if present. When
        /// <paramref name="instanceId"/> is supplied, the entry is removed only if it
        /// still describes that exact host incarnation. This prevents an older host's
        /// shutdown callback from unregistering a newer replacement.
        /// </summary>
        void Unregister(string rootPath, string? instanceId = null);

        /// <summary>
        /// Finds the instance whose root path is the given path or its closest registered ancestor
        /// (longest match wins). Returns null when no registered root contains the path.
        /// </summary>
        InstanceRecord? FindForPath(string path);
    }
}
