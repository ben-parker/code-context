namespace CodeContext.Core.Services;

/// <summary>
/// Makes the repository include/exclude decision shared by scans, explicit refreshes,
/// and file-watcher events. Project-local .gitignore rules are honored; mandatory
/// runtime exclusions are never negatable.
/// </summary>
public interface IRepositoryFileSelector
{
    IReadOnlyList<string> EnumerateIncludedFiles(IEnumerable<string> supportedExtensions);
    bool IsIncluded(string path, bool isDirectory = false);
    bool IsIgnoreFile(string path);
    void Invalidate();
    int IgnoreSourceCount { get; }
    int IgnoredPathCount { get; }
    IReadOnlyList<string> MandatoryExclusions { get; }
}
