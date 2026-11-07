using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using ShardKey = CodeContext.Core.Repositories.InMemory.InMemoryDatabase.ShardKey;

namespace CodeContext.Core.Snapshots
{
    /// <summary>
    /// The shard-level half of the baseline offer rule of plan 3.6 (the supervisor holds the process-level half:
    /// the capability is declared and this is the first mutating request of the worker process). A baseline is
    /// offered for a shard only when all hold:
    /// <list type="bullet">
    /// <item>its annotation records a worker state file still anchored to the shard as the next commit would see
    /// it: the shard came from the authenticated snapshot untouched, or is still the reference that file was
    /// captured with;</item>
    /// <item>its recorded parser version equals the running worker's;</item>
    /// <item>a state id and SHA-256 of the forms this code makes are recorded, and so are fingerprints;</item>
    /// <item>the state file name is one this code makes for that parser, and a regular file of that name exists
    /// in the cache directory now.</item>
    /// </list>
    /// The path handed to the worker is the cache directory joined with the checked name; nothing else read from a
    /// snapshot becomes a path. The host never reads or parses the state file.
    /// </summary>
    internal sealed class WorkerBaselines(InMemoryDatabase database, Func<CacheDirectory?> directory) : IWorkerBaselineSource
    {
        public IndexBaseline? TryGetBaseline(string parserId, string workspaceId, string parserVersion)
        {
            if (directory() is not { } cache) return null;
            var (shard, annotation) = database.CurrentShard(ShardKey.From(parserId, workspaceId));
            if (annotation?.StateFor(shard) is not { } state || annotation.Fingerprints is not { } fingerprints) return null;
            if (!string.Equals(annotation.ParserVersion, parserVersion, StringComparison.Ordinal)) return null;
            if (!WorkerStateFile.IsStateId(state.StateId) || !WorkerStateFile.IsSha256(state.Sha256)) return null;
            if (cache.ExistingWorkerStateFile(parserId, state.FileName) is not { } file) return null;
            return new IndexBaseline(file.Path, state.StateId, state.Sha256, fingerprints);
        }
    }
}
