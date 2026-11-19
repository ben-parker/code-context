using System.Threading.Tasks;

namespace CodeContext.Core.Services
{
    public interface IGraphUpdateService
    {
        Task ProcessFileChangeAsync(string filePath, FileChangeType changeType, CancellationToken cancellationToken);

        /// <summary>
        /// Processes a coalesced batch of file changes. Changed C# files are re-parsed
        /// together in one compilation instead of once per event.
        /// </summary>
        Task ProcessFileChangesAsync(IReadOnlyList<FileChange> changes, CancellationToken cancellationToken);

        /// <summary>
        /// The file changes that bring the index in line with the tree under each of <paramref name="directories"/>
        /// (existing or gone): a file the selection policy includes there and the index does not know is created, a
        /// known one that is gone or no longer included is deleted, a known one with another modification time is
        /// changed; ignore files and project inputs found there are listed as changed. Changes nothing itself: the
        /// result goes through <see cref="ProcessFileChangesAsync"/>.
        /// </summary>
        Task<IReadOnlyList<FileChange>> ExpandSubtreesAsync(IReadOnlyCollection<string> directories, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FileChange>>([]);

        Task PerformInitialScanAsync(string rootPath, IScanProgressReporter? progressReporter, CancellationToken cancellationToken);
        Task PerformResumableScanAsync(string rootPath, IScanProgressReporter? progressReporter, CancellationToken cancellationToken);
    }

    public enum FileChangeType
    {
        Created,
        Changed,
        Deleted,
        Renamed
    }

    public readonly record struct FileChange(string Path, FileChangeType Type);
}
