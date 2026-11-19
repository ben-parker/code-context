using System.Diagnostics;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Snapshots
{
    /// <summary>The outcome of <see cref="SnapshotFreshness.Check"/>.</summary>
    /// <param name="Checked">Files and directories whose times were compared.</param>
    /// <param name="Moved">How many of them are newer than the snapshot (the walk stops at the first).</param>
    /// <param name="FirstMoved">The first one found, root-relative with '/' separators; null when none moved.</param>
    internal sealed record SnapshotFreshnessResult(int Checked, int Moved, string? FirstMoved, TimeSpan Elapsed)
    {
        public bool Unchanged => Moved == 0;
    }

    /// <summary>
    /// Whether anything a scan would read has changed since a snapshot was captured, judged without a worker:
    /// the snapshot holds no per-file sizes or hashes (those live in the worker's own state file), so the test
    /// is by time. Walked with the scan's own file selection (<see cref="RepositoryFileSelector"/>: mandatory
    /// exclusions, configured patterns, <c>.gitignore</c> rules), it compares against the capture time:
    /// <list type="bullet">
    /// <item>every included directory, the root too: its modification time moves when an entry is added,
    /// removed or renamed in it, which catches new, deleted and moved files, a moved file keeping its own times;</item>
    /// <item>every included file a worker claims by extension, or declares a project input by extension
    /// (<c>.csproj</c>), and every <c>.gitignore</c>;</item>
    /// <item>a project input a worker names exactly (<c>project.assets.json</c>) beside an included project
    /// input and in its <c>obj/</c> folder, where a restore writes it although <c>obj/</c> is never scanned.</item>
    /// </list>
    /// An entry has moved when the later of its modification and creation time is after the capture time less
    /// <see cref="Tolerance"/> (file systems that keep times to one or two seconds). On Windows and macOS a creation
    /// time is new for a file copied or unpacked with its old modification time; on Linux .NET reports no birth
    /// time, and such a file is seen through its folder. Not seen: content written in place over an existing file
    /// with an older modification time set afterwards (<c>cp -p</c>, <c>rsync -t</c> onto it), and an edit landing
    /// after a worker read a file but before the capture when the host then stops before its watcher reports it.
    /// The walk stops at the first moved entry.
    /// </summary>
    internal static class SnapshotFreshness
    {
        /// <summary>Times within this much before the capture count as after it.</summary>
        public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

        /// <summary>Where NuGet writes <c>project.assets.json</c> beside a project by default.</summary>
        private const string IntermediateDirectory = "obj";

        /// <param name="claimsExtension">Whether a worker indexes files with this extension.</param>
        /// <param name="isProjectInput">Whether a worker treats this file as a project input (path, included).</param>
        /// <param name="exactProjectInputNames">Project inputs that workers name exactly rather than by extension.</param>
        /// <param name="tolerance"><see cref="Tolerance"/> unless given (tests).</param>
        public static SnapshotFreshnessResult Check(
            RepositoryFileSelector selector,
            string rootPath,
            DateTime capturedUtc,
            Func<string, bool> claimsExtension,
            Func<string, bool, bool> isProjectInput,
            IReadOnlyCollection<string> exactProjectInputNames,
            TimeSpan? tolerance = null)
        {
            var started = Stopwatch.GetTimestamp();
            var root = Path.GetFullPath(rootPath);
            var limit = capturedUtc - (tolerance ?? Tolerance);
            var checkedCount = 0;
            string? moved = null;

            bool Moved(string path, DateTime lastWrite, DateTime creation)
            {
                checkedCount++;
                if (lastWrite <= limit && creation <= limit) return false;
                moved = Path.GetRelativePath(root, path).Replace('\\', '/');
                return true;
            }

            if (!TryTimes(root, out var rootWrite, out var rootCreation) || Moved(root, rootWrite, rootCreation))
                return new SnapshotFreshnessResult(checkedCount, 1, moved ?? ".", Stopwatch.GetElapsedTime(started));

            var projectDirectories = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            selector.WalkIncludedWithTimes(entry =>
            {
                var relevant = entry.IsDirectory
                    || claimsExtension(Path.GetExtension(entry.Path))
                    || selector.IsIgnoreFile(entry.Path);
                if (!relevant && isProjectInput(entry.Path, true))
                {
                    relevant = true;
                    if (Path.GetDirectoryName(entry.Path) is { } directory) projectDirectories.Add(directory);
                }
                return !relevant || !Moved(entry.Path, entry.LastWriteTimeUtc, entry.CreationTimeUtc);
            });
            if (moved is not null) return new SnapshotFreshnessResult(checkedCount, 1, moved, Stopwatch.GetElapsedTime(started));

            foreach (var directory in projectDirectories)
            {
                foreach (var name in exactProjectInputNames)
                {
                    foreach (var candidate in new[] { Path.Combine(directory, name), Path.Combine(directory, IntermediateDirectory, name) })
                    {
                        if (!File.Exists(candidate) || !TryTimes(candidate, out var write, out var creation)) continue;
                        if (Moved(candidate, write, creation))
                            return new SnapshotFreshnessResult(checkedCount, 1, moved, Stopwatch.GetElapsedTime(started));
                    }
                }
            }
            return new SnapshotFreshnessResult(checkedCount, 0, null, Stopwatch.GetElapsedTime(started));
        }

        /// <summary>
        /// The check as a host's routing defines it: extensions and project inputs from the workers, exact project
        /// input names from their manifests, the selection policy from <paramref name="options"/> (a selector of its
        /// own, so a scan's selector is never invalidated).
        /// </summary>
        internal static Func<DateTime, SnapshotFreshnessResult> For(
            CodeContextOptions options, LanguageWorkerService workers, IWorkerCatalog catalog, TimeSpan? tolerance = null)
        {
            var exactNames = catalog.Workers.SelectMany(worker => worker.Manifest.ProjectInputs ?? [])
                .Where(input => !input.StartsWith('.'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return capturedUtc => Check(
                new RepositoryFileSelector(Options.Create(options)), options.RootPath, capturedUtc,
                extension => workers.TryGetParserForExtension(extension, out _),
                (path, included) => workers.TryGetParserForProjectInput(path, included, out _),
                exactNames, tolerance);
        }

        private static bool TryTimes(string path, out DateTime lastWrite, out DateTime creation)
        {
            try
            {
                FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
                lastWrite = info.LastWriteTimeUtc;
                creation = info.CreationTimeUtc;
                return info.Exists;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                lastWrite = creation = default;
                return false;
            }
        }
    }
}
