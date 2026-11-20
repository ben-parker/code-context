using System.Diagnostics;
using CodeContext.Core.Paths;
using CodeContext.Core.Services;
using CodeContext.Core.Workers;
using CodeContext.Parser.Protocol;
using Microsoft.Extensions.Options;

namespace CodeContext.Core.Snapshots
{
    /// <summary>The outcome of <see cref="SnapshotFreshness.Check"/>.</summary>
    /// <param name="Checked">Files, directories and absent paths looked at.</param>
    /// <param name="FirstMoved">The first entry found changed, root-relative with '/' separators inside the root and absolute
    /// outside it; null when none changed. The walk stops at the first.</param>
    /// <param name="Why">What about <paramref name="FirstMoved"/> counts as a change.</param>
    /// <param name="InFuture">The entry's time is later than the clock allows (<see cref="SnapshotFreshness.Tolerance"/>
    /// ahead of now): no snapshot written now could be newer, so a host does not rewrite one for it.</param>
    /// <param name="NotChecked"><paramref name="FirstMoved"/> is a reported path the host may not look at
    /// (<see cref="ReportedPaths"/>): whether the snapshot is current is not known, and writing it again would not help.</param>
    internal sealed record SnapshotFreshnessResult(int Checked, string? FirstMoved, string? Why, bool InFuture, TimeSpan Elapsed, bool NotChecked = false)
    {
        public bool Unchanged => FirstMoved is null;

        /// <summary>One line for a person: what changed and how.</summary>
        public string Describe(string snapshotAt)
            => FirstMoved is null ? "nothing changed" : $"'{FirstMoved}' {Why} (the saved index is of {snapshotAt})";
    }

    /// <summary>
    /// Whether anything the snapshot's facts were made from has changed since it was captured, judged without a worker:
    /// the snapshot holds no per-file sizes or hashes (those live in the worker's own state), so the test is by time,
    /// read through <see cref="FileChangeTime"/> (the change time on Linux and Windows). It looks at exactly
    /// <list type="bullet">
    /// <item>what the scan selects, walked with the scan's own selection (<see cref="RepositoryFileSelector"/>: mandatory
    /// exclusions, configured patterns, <c>.gitignore</c> rules): every included directory (its time moves when an entry
    /// is added, removed or renamed in it), every included file a worker claims by extension, and the <c>.gitignore</c>
    /// of the root and of every included directory, whether or not its own rules ignore it;</item>
    /// <item>what each shard's worker reported it read besides those files (<see cref="WorkerInputs"/>, stored in the
    /// snapshot): each file or directory listed there must still exist and be no newer, each absent path must still be
    /// absent. A shard without a report is not checked here: the caller refuses it. A reported path is what a repository
    /// made its worker say, so each is put to <see cref="ReportedPaths.RefusedOnDisk"/> first: one on a share nobody
    /// chose, or behind a link the repository holds, is not looked at, and the answer is then "not known"
    /// (<see cref="SnapshotFreshnessResult.NotChecked"/>), which is never "unchanged". A snapshot written before reports
    /// were filtered as they arrive is covered by the same check here.</item>
    /// </list>
    /// An entry has changed when its time is after the capture time less <see cref="Tolerance"/> (file systems keeping
    /// times to a second or two). The root is the exception for one time: the one its writer recorded once its cache
    /// directory existed, since making <c>.codecontext/</c> moves the root's time just before the first capture.
    /// A time more than <see cref="Tolerance"/> ahead of now counts as changed, flagged <see cref="SnapshotFreshnessResult.InFuture"/>.
    /// A capture time that far ahead of now is never current: the clock was set back, and times say nothing.
    /// </summary>
    internal static class SnapshotFreshness
    {
        /// <summary>Times within this much before the capture count as after it; times this much ahead of now are not believed.</summary>
        public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

        /// <summary>The ignore file the selection policy reads in each directory (<see cref="RepositoryFileSelector"/>).</summary>
        private const string IgnoreFileName = ".gitignore";

        /// <param name="claimsExtension">Whether a worker indexes files with this extension.</param>
        /// <param name="inputs">Each loaded shard's reported inputs.</param>
        /// <param name="rootTimeTicks">The root's time its writer recorded (<see cref="SnapshotHeader.RootTimeTicks"/>); 0 for none.</param>
        /// <param name="nowUtc">The clock; <see cref="DateTime.UtcNow"/> unless given (tests).</param>
        /// <param name="tolerance"><see cref="Tolerance"/> unless given (tests).</param>
        /// <param name="refused">Which reported paths may not be looked at; <see cref="ReportedPaths.RefusedOnDisk"/> for the root unless given (tests).</param>
        public static SnapshotFreshnessResult Check(
            RepositoryFileSelector selector,
            string rootPath,
            DateTime capturedUtc,
            Func<string, bool> claimsExtension,
            IEnumerable<WorkerInputs> inputs,
            long rootTimeTicks = 0,
            DateTime? nowUtc = null,
            TimeSpan? tolerance = null,
            Func<string, bool>? refused = null)
        {
            var started = Stopwatch.GetTimestamp();
            var root = Path.GetFullPath(rootPath);
            var slack = tolerance ?? Tolerance;
            var limit = capturedUtc - slack;
            var ahead = (nowUtc ?? DateTime.UtcNow) + Tolerance;
            var checkedCount = 0;
            string? moved = null;
            string? why = null;
            var future = false;
            var notChecked = false;
            var mayNotLook = refused ?? ReportedPaths.RefusedOnDisk(root);

            string Shown(string path)
            {
                var relative = Path.GetRelativePath(root, path);
                return relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || Path.IsPathFullyQualified(relative)
                    ? path
                    : relative.Replace('\\', '/');
            }

            bool Changed(string path, DateTime time)
            {
                checkedCount++;
                if (time <= limit) return false;
                moved = Shown(path);
                future = time > ahead;
                why = future ? "has a time in the future" : "changed after the saved index";
                return true;
            }

            SnapshotFreshnessResult Result() => new(checkedCount, moved, why, future, Stopwatch.GetElapsedTime(started), notChecked);

            // Before anything else is done with a reported path: even writing it relative to the root can ask the disk
            // about it (Windows expands a short name), so a refused one is named as it was reported.
            bool Refused(string path)
            {
                if (!mayNotLook(path)) return false;
                checkedCount++;
                (moved, why, notChecked) = (path, "was reported by a worker and is not looked at (a share nobody chose, or behind a link in the repository)", true);
                return true;
            }

            // A capture later than the present: the clock was set back since, and whatever was edited meanwhile has a
            // time before the capture. Nothing by time can be said then. Not flagged InFuture: a host writes again,
            // with the time its clock has now.
            if (capturedUtc > ahead)
            {
                (moved, why) = (".", "is not checked: the saved index is dated after the present time (the clock was set back)");
                return Result();
            }

            checkedCount++;
            if (!FileChangeTime.TryRead(root, out var rootTime))
            {
                (moved, why) = (".", "cannot be read");
                return Result();
            }
            if (!(rootTimeTicks != 0 && rootTime.Ticks == rootTimeTicks) && Changed(root, rootTime)) return Result();

            // Each directory's ignore file is read whatever its own inclusion (its rules may ignore it), so it is looked
            // at directly, not as an entry of the walk. One that is not there is covered by its directory's time. One that
            // is a link is not read by the selector, so where it leads is not looked at: only its own time counts.
            bool IgnoreFileChanged(string directory)
            {
                var ignoreFile = Path.Combine(directory, IgnoreFileName);
                return FileChangeTime.TryRead(ignoreFile, out var time, followLinks: false) && Changed(ignoreFile, time);
            }

            if (IgnoreFileChanged(root)) return Result();
            var changeTimes = FileChangeTime.ReadsChangeTime;
            selector.WalkIncluded(withTimes: !changeTimes, entry =>
            {
                if (entry.IsDirectory)
                {
                    if (IgnoreFileChanged(entry.Path)) return false;
                }
                else if (!claimsExtension(Path.GetExtension(entry.Path)))
                {
                    return true;
                }
                DateTime time;
                if (!changeTimes) time = entry.LastWriteTimeUtc > entry.CreationTimeUtc ? entry.LastWriteTimeUtc : entry.CreationTimeUtc;
                else if (!FileChangeTime.TryRead(entry.Path, out time))
                {
                    // Gone since the listing: the listing's directory has moved as well, unless the race is very close.
                    checkedCount++;
                    (moved, why) = (Shown(entry.Path), "disappeared while it was checked");
                    return false;
                }
                return !Changed(entry.Path, time);
            });
            if (moved is not null) return Result();

            foreach (var reported in inputs)
            {
                foreach (var file in reported.Files)
                {
                    if (Refused(file)) return Result();
                    if (!FileChangeTime.TryRead(file, out var time))
                    {
                        checkedCount++;
                        (moved, why) = (Shown(file), "was read for the saved index and is gone");
                        return Result();
                    }
                    if (Changed(file, time)) return Result();
                }
                foreach (var absent in reported.Absent)
                {
                    if (Refused(absent)) return Result();
                    checkedCount++;
                    if (!FileChangeTime.TryRead(absent, out _)) continue;
                    (moved, why) = (Shown(absent), "was looked for and absent when the index was saved, and exists now");
                    return Result();
                }
            }
            return Result();
        }

        /// <summary>
        /// The check as a host's routing defines it: extensions from the workers, the selection policy from
        /// <paramref name="options"/> (a selector of its own, so a scan's selector is never invalidated), and besides
        /// what the shards' workers reported, where the workers themselves are installed.
        /// </summary>
        internal static SnapshotFreshnessResult For(
            CodeContextOptions options, LanguageWorkerService workers, DateTime capturedUtc, IEnumerable<WorkerInputs> inputs,
            long rootTimeTicks, TimeSpan? tolerance = null)
            => Check(
                new RepositoryFileSelector(Options.Create(options)), options.RootPath, capturedUtc,
                extension => workers.TryGetParserForExtension(extension, out _),
                inputs.Append(new WorkerInputs(workers.InstallationPaths(), [])), rootTimeTicks, tolerance: tolerance);
    }
}
