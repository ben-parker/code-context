using CodeContext.Core.Diffs;
using CodeContext.Core.Repositories;
using CodeContext.Core.Repositories.InMemory;
using CodeContext.Core.Workers;

namespace CodeContext.Core.Services
{
    /// <summary>
    /// Which changed files of a diff cannot change an indexed fact, so that a changed README does not make an answer
    /// incomplete. A file is set aside only when everything that could tie it to the index says it is not tied:
    /// <list type="bullet">
    /// <item>no declaration of the index lies in it, at its new path or its old one;</item>
    /// <item>no worker's manifest names it: not by an extension a worker parses, not as a project marker or project
    /// input (<see cref="ILanguageWorkerService.ParserNaming"/>);</item>
    /// <item>it is not a dependency lock file (no worker opens one, but the packages the index was built over follow
    /// it), not an ignore file, and the selection policy does not leave its path out (build output and packages, which
    /// workers read, lie there);</item>
    /// <item>it is a regular file, by the diff's mode or on disk, reached through no link;</item>
    /// <item>every shard that holds facts carries its worker's report of what it read (protocol <c>inputs</c>), and no
    /// report names the file as read or as looked for, names a path it lies under as absent, or, when the diff adds,
    /// deletes, renames or copies it, names the directory it entered or left as one whose listing was used.</item>
    /// </list>
    /// A worker need not report inputs. When one that holds facts does not, nothing is set aside: nothing says what
    /// that worker reads. The rules about names are asked first and cost nothing; the reports are read once, for all
    /// the files that passed them. A file that is kept and that no manifest names is reported <c>notIndexed</c> without
    /// a look at the working tree: no worker is told when it changes, so its time and content say nothing of the index.
    /// </summary>
    internal sealed class ChangedFilesNoWorkerReads(
        ILanguageWorkerService workers, IRepositoryFileSelector selector, Func<string, bool> refusedOnDisk)
    {
        private static readonly StringComparer PathComparer = OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        /// <summary>Dependency lock files of the ecosystems the bundled workers read.</summary>
        private static readonly HashSet<string> LockFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            "package-lock.json", "npm-shrinkwrap.json", "yarn.lock", "pnpm-lock.yaml", "bun.lock", "bun.lockb", "deno.lock",
            "packages.lock.json", "paket.lock",
        };

        /// <summary>A file the name rules let through: its paths (new, old), and why it is kept after all, once known.</summary>
        public sealed class Candidate
        {
            public required DiffFile File { get; init; }
            public required string Display { get; init; }
            public required string[] Paths { get; init; }
            public string? Kept { get; set; }
        }

        private readonly List<Candidate> _candidates = new();

        public IReadOnlyList<Candidate> Candidates => _candidates;

        /// <summary>What the rules that need no report say of a file.</summary>
        public enum ByName
        {
            /// <summary>It holds declarations, or a worker's manifest names it: it is mapped and checked as any file.</summary>
            Indexed,

            /// <summary>It holds none, no worker is told when it changes, and it may feed the index all the same.</summary>
            Kept,

            /// <summary>Nothing ties it to the index yet: it waits for <see cref="Resolve"/>.</summary>
            Deferred,
        }

        /// <summary>
        /// Asks the rules that need no report; <paramref name="kept"/> says why the file may feed the index, when a rule does.
        /// </summary>
        /// <param name="fullPaths">Its new path and, when the diff names another, its old one.</param>
        public ByName Ask(DiffFile file, string display, string[] fullPaths, Func<string, bool> holdsDeclarations, out string? kept)
        {
            kept = null;
            foreach (var path in fullPaths)
            {
                if (holdsDeclarations(path)) return ByName.Indexed;
            }
            foreach (var path in fullPaths)
            {
                if (workers.TryGetParserForExtension(Path.GetExtension(path), out var parser))
                {
                    kept = $"the {parser} worker parses files of its kind and holds no declaration of this one (it declares nothing, or is not among the files the worker was given)";
                    return ByName.Indexed;
                }
                if (workers.ParserNaming(path) is { } naming)
                {
                    kept = $"the {naming} worker's manifest names it as a project file, which can change how every file binds";
                    return ByName.Indexed;
                }
            }
            foreach (var path in fullPaths)
            {
                kept = KeptByName(file, path);
                if (kept is not null) return ByName.Kept;
            }
            _candidates.Add(new Candidate { File = file, Display = display, Paths = fullPaths });
            return ByName.Deferred;
        }

        private string? KeptByName(DiffFile file, string path)
        {
            if (LockFiles.Contains(Path.GetFileName(path))) return "it is a dependency lock file, and the packages the index was built over follow it";
            if (selector.IsIgnoreFile(path)) return "it is an ignore file, which decides which files are indexed";
            if (!selector.IsIncluded(path))
                return "the selection policy leaves its path out (an excluded directory or an ignore rule), where build output and packages that feed the index lie";
            if (refusedOnDisk(path)) return "it is reached through a link, or by a path the host does not read";
            if (!file.RegularFileMode && !IsRegularFileOnDisk(path))
                return "nothing shows it to be a regular file (a link or a submodule can stand for files that are indexed)";
            return null;
        }

        private static bool IsRegularFileOnDisk(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return false;
            }
        }

        /// <summary>
        /// Reads what the workers reported, once, and marks every candidate a report ties to the index
        /// (<see cref="Candidate.Kept"/>); the rest cannot change an indexed fact.
        /// </summary>
        public void Resolve(
            IEnumerable<KeyValuePair<InMemoryDatabase.ShardKey, IGraphShard>> shards,
            IReadOnlyDictionary<InMemoryDatabase.ShardKey, ShardAnnotation> annotations)
        {
            if (_candidates.Count == 0) return;
            var reports = new List<(string Parser, CodeContext.Parser.Protocol.WorkerInputs Inputs)>();
            foreach (var (key, shard) in shards)
            {
                if (shard.NodeCount == 0 && shard.EdgeCount == 0) continue;
                if (annotations.TryGetValue(key, out var annotation) && annotation.Inputs is { } inputs)
                {
                    reports.Add((key.ParserId, inputs));
                    continue;
                }
                var worker = key.ParserId.Length > 0 ? $"the {key.ParserId} worker" : "a worker of this index";
                foreach (var candidate in _candidates)
                    candidate.Kept = $"{worker} does not report what it reads besides its own files, so nothing says this file is not among it";
                return;
            }

            // Each candidate by its paths, by every directory above them (an absent path above one means it was looked
            // for), and, when the diff added or removed its entry, by the directory that entry is listed in now.
            var byPath = new Dictionary<string, List<Candidate>>(PathComparer);
            var byAncestor = new Dictionary<string, List<Candidate>>(PathComparer);
            var byListing = new Dictionary<string, List<Candidate>>(PathComparer);
            foreach (var candidate in _candidates)
            {
                foreach (var path in candidate.Paths)
                {
                    Add(byPath, path, candidate);
                    var listing = candidate.File.Status == DiffFileStatus.Modified ? null : Path.GetDirectoryName(path);
                    for (var above = Path.GetDirectoryName(path); !string.IsNullOrEmpty(above); above = Path.GetDirectoryName(above))
                    {
                        Add(byAncestor, above, candidate);
                        // The nearest directory that is there: the one a listing shows the change in.
                        if (listing == above && !Directory.Exists(above)) listing = Path.GetDirectoryName(above);
                    }
                    if (!string.IsNullOrEmpty(listing)) Add(byListing, listing, candidate);
                }
            }

            foreach (var (parser, inputs) in reports)
            {
                foreach (var reported in inputs.Files)
                {
                    var path = AsWritten(reported);
                    Keep(byPath, path, $"the {parser} worker reported reading it");
                    Keep(byListing, path, $"the diff adds it to, or removes it from, a directory whose listing the {parser} worker used to find what it reads");
                }
                foreach (var reported in inputs.Absent)
                {
                    var path = AsWritten(reported);
                    Keep(byPath, path, $"the {parser} worker looked for it");
                    Keep(byAncestor, path, $"the {parser} worker looked for {Path.GetFileName(path)}, which it lies under");
                }
            }

            static void Add(Dictionary<string, List<Candidate>> into, string path, Candidate candidate)
            {
                if (!into.TryGetValue(path, out var list)) into[path] = list = new List<Candidate>(1);
                list.Add(candidate);
            }

            static void Keep(Dictionary<string, List<Candidate>> from, string path, string why)
            {
                if (!from.TryGetValue(path, out var list)) return;
                foreach (var candidate in list) candidate.Kept ??= why;
            }

            // A report's path as this platform writes one: its separators, and none at its end.
            static string AsWritten(string reported)
            {
                if (Path.DirectorySeparatorChar != '/') reported = reported.Replace('/', Path.DirectorySeparatorChar);
                return Path.EndsInDirectorySeparator(reported) ? Path.TrimEndingDirectorySeparator(reported) : reported;
            }
        }
    }
}
