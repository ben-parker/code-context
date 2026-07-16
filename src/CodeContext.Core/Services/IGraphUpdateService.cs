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
