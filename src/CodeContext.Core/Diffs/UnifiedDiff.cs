using System.Text;

namespace CodeContext.Core.Diffs
{
    /// <summary>What a diff says happened to one file.</summary>
    public enum DiffFileStatus { Modified, Added, Deleted, Renamed, Copied }

    /// <summary>Lines <see cref="Start"/> to <see cref="End"/>, 1-based and inclusive.</summary>
    public readonly record struct DiffLineRange(int Start, int End)
    {
        public int Count => End - Start + 1;
    }

    /// <summary>
    /// Removed lines that are not all blank: <see cref="Old"/> on the old side, and the new-side lines around the change
    /// block they belong to: <see cref="NewBefore"/> is the last new-side line before it (0 at the top of the file) and
    /// <see cref="NewAfter"/> the first after it, past any lines the block adds. <see cref="CommentsOnly"/> when every
    /// removed line that is not blank is a comment line (see <see cref="SourceLines.IsCommentLine"/>);
    /// <see cref="ContinuesBlockComment"/> when such a run starts like the inside or the end of a block comment
    /// (<c>* text</c>, <c>*/</c>), so that it was a comment only if one was open above it.
    /// </summary>
    public readonly record struct DiffRemoval(
        DiffLineRange Old, int NewBefore, int NewAfter, bool CommentsOnly = false, bool ContinuesBlockComment = false);

    /// <summary>
    /// One file of a unified diff, as data. Paths are as the diff names them, relative, <c>/</c>-separated, with git's
    /// quoting undone and its one-letter prefix (<c>a/</c>, <c>b/</c>, ...) removed; null for <c>/dev/null</c>. Only line
    /// numbers are kept, never the text: <see cref="Added"/> holds the new-side lines a hunk adds that are neither blank
    /// nor comment lines, <see cref="CommentAdded"/> the added comment lines, <see cref="Removals"/> the removed runs that
    /// are not all blank, and <see cref="IgnoredLines"/> counts the changed lines that hold only whitespace.
    /// </summary>
    public sealed class DiffFile
    {
        public string? OldPath { get; internal set; }
        public string? NewPath { get; internal set; }
        public DiffFileStatus Status { get; internal set; }

        /// <summary>The blob ids of the <c>index</c> line (hexadecimal, maybe abbreviated); null when absent or all zeros.</summary>
        public string? OldBlob { get; internal set; }
        public string? NewBlob { get; internal set; }

        /// <summary>The file mode the new side has, when the diff says (<c>160000</c> is a submodule).</summary>
        public string? NewMode { get; internal set; }
        public bool ModeChanged { get; internal set; }

        /// <summary>
        /// Whether the diff gives the file a mode, and only that of a regular file (<c>100644</c>, <c>100755</c>) on
        /// either side: not a symbolic link (<c>120000</c>) nor a submodule (<c>160000</c>). False when it gives none.
        /// </summary>
        public bool RegularFileMode => _modeSeen && !_otherModeSeen;
        private bool _modeSeen;
        private bool _otherModeSeen;

        internal void SeeMode(ReadOnlySpan<char> mode)
        {
            mode = mode.Trim();
            _modeSeen = true;
            if (mode is not ("100644" or "100755")) _otherModeSeen = true;
        }
        public bool Binary { get; internal set; }

        /// <summary>A combined diff of a merge (<c>diff --cc</c>), whose hunks are against several parents.</summary>
        public bool Combined { get; internal set; }

        /// <summary>Why a path the diff names is refused (absolute, leaving the root, not valid text), or null.</summary>
        public string? PathProblem { get; internal set; }

        public bool HasHunks { get; internal set; }
        public List<DiffLineRange> Added { get; } = new();
        public List<DiffLineRange> CommentAdded { get; } = new();

        /// <summary>
        /// Added comment lines that open or close a block comment whose other end is not among the lines changed with
        /// them: the unchanged lines next to them were commented out or back in (see <see cref="SourceLines.IsCommentLine"/>).
        /// </summary>
        public List<DiffLineRange> CommentBoundaries { get; } = new();

        /// <summary>
        /// In an added file, the added lines among <see cref="Added"/> whose effect stays in the file: a <c>using</c>
        /// directive that is not <c>global</c>, a namespace line, a lone brace, <c>#nullable</c> or <c>#pragma warning</c>
        /// (see <see cref="SourceLines.IsFileScopedDirective"/>).
        /// </summary>
        public List<DiffLineRange> FileScopedDirectives { get; } = new();

        /// <summary>
        /// The added lines among <see cref="Added"/> that are conditional-compilation directives
        /// (<see cref="SourceLines.IsConditionalDirective"/>): the unchanged lines under one may be compiled in or out.
        /// </summary>
        public List<DiffLineRange> ConditionalDirectives { get; } = new();
        public List<DiffRemoval> Removals { get; } = new();

        /// <summary>Where the last hunk read ended on each side: the next may not start before.</summary>
        internal int OldEnd { get; set; }
        internal int NewEnd { get; set; }
        public int IgnoredLines { get; internal set; }

        /// <summary>Lines the hunks add and remove, blank ones included.</summary>
        public int AddedLineCount { get; internal set; }
        public int RemovedLineCount { get; internal set; }

        /// <summary>The 1-based line of the diff where this file's header starts.</summary>
        public int DiffLine { get; internal set; }

        /// <summary>Only the file mode changed: no line, no content.</summary>
        public bool ModeOnly => ModeChanged && !HasHunks && !Binary && Status == DiffFileStatus.Modified;

        /// <summary>The path the file has after the change, or before it for a deletion.</summary>
        public string? Path => NewPath ?? OldPath;
    }

    /// <summary>
    /// Reads a unified diff (<c>git diff</c>, <c>git show</c>, <c>git format-patch</c>, <c>diff -u</c>) as hostile input:
    /// bounded in size and file count, every hunk checked against its header's line counts, so that a truncated or
    /// garbled diff is refused rather than read as a smaller change. Text outside file headers and hunks (a commit
    /// message, a mail signature) is skipped. Nothing here touches the disk or runs anything.
    /// </summary>
    public static class UnifiedDiff
    {
        public const int MaxLength = 16 * 1024 * 1024;
        public const int MaxFiles = 10_000;
        public const int MaxPathLength = 4_096;

        /// <summary>The largest line number a hunk may name.</summary>
        public const int MaxLine = 100_000_000;

        /// <summary>Parses <paramref name="text"/>; throws <see cref="ArgumentException"/> saying where and why it is refused.</summary>
        public static List<DiffFile> Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("diff is empty: there is no change to map. Pass the output of git diff (for example git diff main...HEAD).");
            if (text.Length > MaxLength)
                throw new ArgumentException($"diff is {text.Length:N0} characters; at most {MaxLength:N0} are read. Split it by path (git diff <base> -- <dir>).");
            return new Reader(text).Read();
        }

        private sealed class Reader(string text)
        {
            private int _position;
            private int _lineNumber;
            private readonly List<DiffFile> _files = new();
            private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
            private DiffFile? _file;

            /// <summary>Whether the current file came from a <c>diff --git</c> header, whose paths carry git's prefixes.</summary>
            private bool _gitHeader;

            /// <summary>Whether <c>rename</c>/<c>copy</c> lines named the paths, which then win over <c>---</c>/<c>+++</c>.</summary>
            private bool _pathsFromHeader;

            /// <summary>Inside a binary patch or a combined diff: lines are skipped until the next file.</summary>
            private bool _skipping;

            public List<DiffFile> Read()
            {
                // Lines are looked at where they lie: only a header line is copied out of the text.
                while (NextSpan(out var line))
                {
                    if (line.StartsWith("diff --git "))
                    {
                        StartFile(gitHeader: true);
                        ReadGitHeaderPaths(line["diff --git ".Length..].ToString());
                    }
                    else if (line.StartsWith("diff --cc ") || line.StartsWith("diff --combined "))
                    {
                        StartFile(gitHeader: true);
                        var path = line[(line[5..].IndexOf(' ') + 6)..].ToString();
                        _file!.NewPath = _file.OldPath = Path(path, stripPrefix: false);
                        _file.Combined = true;
                        _skipping = true;
                    }
                    else if (_skipping)
                    {
                        continue;
                    }
                    else if (line.StartsWith("--- ") && PeekLine() is { } next && next.StartsWith("+++ ", StringComparison.Ordinal))
                    {
                        // A plain diff -u starts a file here; a git diff names its paths again.
                        if (_file is null || _file.HasHunks || !_gitHeader) StartFile(gitHeader: false);
                        var minus = line[4..].ToString();
                        NextLine();
                        ReadMinusPlus(minus, next[4..]);
                    }
                    else if (line.StartsWith("@@ "))
                    {
                        if (_file is null) throw Malformed("a hunk with no file header before it");
                        ReadHunk(line);
                    }
                    else if (line.StartsWith("@@@"))
                    {
                        throw Malformed("a combined-diff hunk outside a combined diff");
                    }
                    else if (_file is not null && !_file.HasHunks)
                    {
                        ReadExtendedHeader(line.ToString());
                    }
                    // Anything else (a commit message, a mail signature, text between files) is not part of a change.
                }
                Finish();
                if (_files.Count == 0)
                    throw new ArgumentException("diff names no file: no 'diff --git' or '---'/'+++' header was found. Pass the output of git diff.");
                return _files;
            }

            private string? NextLine()
            {
                if (_position >= text.Length) return null;
                var end = text.IndexOf('\n', _position);
                var line = end < 0 ? text[_position..] : text[_position..end];
                _position = end < 0 ? text.Length : end + 1;
                _lineNumber++;
                return line.EndsWith('\r') ? line[..^1] : line;
            }

            /// <summary>The next line without a copy of it, for a hunk's lines; false at the end.</summary>
            private bool NextSpan(out ReadOnlySpan<char> line)
            {
                line = default;
                if (_position >= text.Length) return false;
                var rest = text.AsSpan(_position);
                var end = rest.IndexOf('\n');
                line = end < 0 ? rest : rest[..end];
                _position = end < 0 ? text.Length : _position + end + 1;
                _lineNumber++;
                if (line.EndsWith("\r")) line = line[..^1];
                return true;
            }

            private string? PeekLine()
            {
                if (_position >= text.Length) return null;
                var end = text.IndexOf('\n', _position);
                var line = end < 0 ? text[_position..] : text[_position..end];
                return line.EndsWith('\r') ? line[..^1] : line;
            }

            private ArgumentException Malformed(string what) => new($"diff line {_lineNumber}: {what}; the diff is refused rather than read in part.");

            private void StartFile(bool gitHeader)
            {
                Finish();
                if (_files.Count >= MaxFiles)
                    throw new ArgumentException($"diff names more than {MaxFiles:N0} files; split it by path (git diff <base> -- <dir>).");
                _file = new DiffFile { DiffLine = _lineNumber };
                _files.Add(_file);
                _gitHeader = gitHeader;
                _pathsFromHeader = false;
                _skipping = false;
            }

            private void Finish()
            {
                if (_file is not { } file) return;
                if (file.OldPath is null && file.NewPath is null)
                    throw new ArgumentException($"diff line {file.DiffLine}: a file header that names no path.");
                if (file.Status == DiffFileStatus.Modified)
                {
                    if (file.OldPath is null) file.Status = DiffFileStatus.Added;
                    else if (file.NewPath is null) file.Status = DiffFileStatus.Deleted;
                }
                // A header names both paths; the status says which side does not exist.
                DiffMapper.Normalize(file.FileScopedDirectives);
                if (file.Status == DiffFileStatus.Added) file.OldPath = null;
                if (file.Status == DiffFileStatus.Deleted) file.NewPath = null;
                if (file.PathProblem is null && file.Path is { } key && !_seen.Add(key))
                {
                    throw new ArgumentException($"diff line {file.DiffLine}: {key} appears a second time. One diff between two states is "
                        + "read, not a series of patches (git log -p prints one per commit): pass git diff <from>..<to>.");
                }
                _file = null;
            }

            private void ReadGitHeaderPaths(string rest)
            {
                // "a/P b/P", or quoted tokens. Renames name their paths again on their own lines.
                string? left = null, right = null;
                if (rest.StartsWith('"'))
                {
                    var (first, after) = Quoted(rest, 0);
                    left = first;
                    if (after < rest.Length && rest[after] == ' ')
                        right = rest[after + 1] == '"' ? Quoted(rest, after + 1).Value : rest[(after + 1)..];
                }
                else if ((rest.Length - 1) % 2 == 0 && rest.Length > 5 && rest[(rest.Length - 1) / 2] == ' '
                    && rest[2..((rest.Length - 1) / 2)] == rest[((rest.Length - 1) / 2 + 3)..])
                {
                    left = rest[..((rest.Length - 1) / 2)];
                    right = rest[((rest.Length - 1) / 2 + 1)..];
                }
                else
                {
                    var split = rest.IndexOf(" \"", StringComparison.Ordinal);
                    if (split > 0)
                    {
                        left = rest[..split];
                        right = Quoted(rest, split + 1).Value;
                    }
                    else
                    {
                        split = rest.IndexOf(" b/", StringComparison.Ordinal);
                        if (split > 0)
                        {
                            left = rest[..split];
                            right = rest[(split + 1)..];
                        }
                    }
                }
                if (left is null || right is null) throw Malformed("a 'diff --git' line whose two paths cannot be told apart");
                _file!.OldPath = Path(left, stripPrefix: true, quoted: false);
                _file.NewPath = Path(right, stripPrefix: true, quoted: false);
            }

            private void ReadMinusPlus(string minus, string plus)
            {
                var file = _file!;
                var oldPath = Path(minus, stripPrefix: _gitHeader || BothPrefixed(minus, plus));
                var newPath = Path(plus, stripPrefix: _gitHeader || BothPrefixed(minus, plus));
                if (_pathsFromHeader) return;
                if (IsDevNull(minus)) file.Status = DiffFileStatus.Added;
                if (IsDevNull(plus)) file.Status = DiffFileStatus.Deleted;
                file.OldPath = oldPath;
                file.NewPath = newPath;
            }

            private static bool BothPrefixed(string minus, string plus)
                => (minus.StartsWith("a/", StringComparison.Ordinal) || IsDevNull(minus))
                    && (plus.StartsWith("b/", StringComparison.Ordinal) || IsDevNull(plus));

            private static bool IsDevNull(string value) => value == "/dev/null" || value.StartsWith("/dev/null\t", StringComparison.Ordinal);

            private void ReadExtendedHeader(string line)
            {
                var file = _file!;
                if (line.StartsWith("index ", StringComparison.Ordinal))
                {
                    var value = line["index ".Length..];
                    var space = value.IndexOf(' ');
                    if (space >= 0) value = value[..space];
                    var dots = value.IndexOf("..", StringComparison.Ordinal);
                    if (dots < 0) throw Malformed("an index line without old..new");
                    file.OldBlob = Blob(value[..dots]);
                    file.NewBlob = Blob(value[(dots + 2)..]);
                    if (space >= 0)
                    {
                        file.NewMode ??= line["index ".Length..][(space + 1)..].Trim();
                        file.SeeMode(line.AsSpan("index ".Length + space + 1));
                    }
                }
                else if (line.StartsWith("new file mode ", StringComparison.Ordinal))
                {
                    file.Status = DiffFileStatus.Added;
                    file.NewMode = line["new file mode ".Length..].Trim();
                    file.SeeMode(file.NewMode);
                }
                else if (line.StartsWith("deleted file mode ", StringComparison.Ordinal))
                {
                    file.Status = DiffFileStatus.Deleted;
                    file.SeeMode(line.AsSpan("deleted file mode ".Length));
                }
                else if (line.StartsWith("old mode ", StringComparison.Ordinal))
                {
                    file.ModeChanged = true;
                    file.SeeMode(line.AsSpan("old mode ".Length));
                }
                else if (line.StartsWith("new mode ", StringComparison.Ordinal))
                {
                    file.ModeChanged = true;
                    file.NewMode = line["new mode ".Length..].Trim();
                    file.SeeMode(file.NewMode);
                }
                else if (line.StartsWith("rename from ", StringComparison.Ordinal) || line.StartsWith("copy from ", StringComparison.Ordinal))
                {
                    file.Status = line[0] == 'r' ? DiffFileStatus.Renamed : DiffFileStatus.Copied;
                    file.OldPath = Path(line[(line.IndexOf(" from ", StringComparison.Ordinal) + 6)..], stripPrefix: false);
                    _pathsFromHeader = true;
                }
                else if (line.StartsWith("rename to ", StringComparison.Ordinal) || line.StartsWith("copy to ", StringComparison.Ordinal))
                {
                    file.Status = line[0] == 'r' ? DiffFileStatus.Renamed : DiffFileStatus.Copied;
                    file.NewPath = Path(line[(line.IndexOf(" to ", StringComparison.Ordinal) + 4)..], stripPrefix: false);
                    _pathsFromHeader = true;
                }
                else if (line == "GIT binary patch" || (line.StartsWith("Binary files ", StringComparison.Ordinal) && line.EndsWith(" differ", StringComparison.Ordinal)))
                {
                    file.Binary = true;
                    // The patch's lines are base85 and never hold a space, so no header is mistaken among them.
                    _skipping = true;
                }
            }

            private string? Blob(string value)
            {
                if (value.Length is < 4 or > 64 || !value.All(char.IsAsciiHexDigit)) throw Malformed($"an index line with a blob id that is not hexadecimal ('{Shorten(value)}')");
                return value.All(digit => digit == '0') ? null : value.ToLowerInvariant();
            }

            private void ReadHunk(ReadOnlySpan<char> header)
            {
                var file = _file!;
                // @@ -oldStart[,oldCount] +newStart[,newCount] @@[ section]
                var end = header[3..].IndexOf(" @@");
                if (end < 0) throw Malformed($"a hunk header that does not close with '@@' ('{Shorten(header.ToString())}')");
                var ranges = header.Slice(3, end);
                var space = ranges.IndexOf(' ');
                if (space < 1 || space + 1 >= ranges.Length || ranges[(space + 1)..].Contains(' ') || ranges[0] != '-' || ranges[space + 1] != '+'
                    || !TryRange(ranges[1..space], out var oldStart, out var oldCount)
                    || !TryRange(ranges[(space + 2)..], out var newStart, out var newCount))
                {
                    throw Malformed($"a hunk header that is not '@@ -start,count +start,count @@' ('{Shorten(header.ToString())}')");
                }
                file.HasHunks = true;

                // A side with no lines names the line before the hunk.
                var nextOld = oldCount == 0 ? oldStart + 1 : oldStart;
                var nextNew = newCount == 0 ? newStart + 1 : newStart;
                int oldLeft = oldCount, newLeft = newCount;
                var hunkLine = _lineNumber;
                // One diff between two states names each line once, in order: anything else is not read as a smaller or other change.
                if (nextOld < file.OldEnd || nextNew < file.NewEnd)
                    throw Malformed($"a hunk that starts before the hunk above it ends ('{Shorten(header.ToString())}')");

                // The change block being read: removed lines (old side), and whether any is not blank.
                var blockNewBefore = -1;
                var removedStart = 0;
                var removedEnd = 0;
                var removedCode = false;
                var removedComment = false;
                var removedContinues = false;
                var removedBlank = 0;
                var addStart = 0;
                var commentStart = 0;
                // Where the block's comment lines leave a block comment, read from outside one: added and removed apart.
                var addedMarks = new CommentMarks();
                var removedMarks = new CommentMarks();
                List<int>? markedLines = null;

                while (oldLeft > 0 || newLeft > 0)
                {
                    if (!NextSpan(out var line)) throw new ArgumentException(
                        $"diff line {hunkLine}: the hunk ends early: {oldLeft} old and {newLeft} new line(s) of its header are missing (a truncated diff?); the diff is refused rather than read in part.");
                    var kind = line.Length == 0 ? ' ' : line[0];
                    var content = line.Length == 0 ? line : line[1..];
                    switch (kind)
                    {
                        case ' ':
                            if (oldLeft == 0 || newLeft == 0) throw Malformed("a context line beyond the hunk's counts");
                            CloseBlock();
                            oldLeft--;
                            newLeft--;
                            nextOld++;
                            nextNew++;
                            break;
                        case '-':
                            if (oldLeft == 0) throw Malformed("a removed line beyond the hunk's old count");
                            OpenBlock();
                            if (removedStart == 0) removedStart = nextOld;
                            removedEnd = nextOld;
                            if (SourceLines.IsBlank(content)) removedBlank++;
                            else if (SourceLines.IsCommentLine(content))
                            {
                                if (!removedMarks.Open && content.TrimStart()[0] == '*') removedContinues = true;
                                removedComment = true;
                                removedMarks.Follow(content);
                            }
                            else removedCode = true;
                            file.RemovedLineCount++;
                            oldLeft--;
                            nextOld++;
                            break;
                        case '+':
                            if (newLeft == 0) throw Malformed("an added line beyond the hunk's new count");
                            OpenBlock();
                            file.AddedLineCount++;
                            if (SourceLines.IsBlank(content))
                            {
                                file.IgnoredLines++;
                                CloseAdded();
                            }
                            else if (SourceLines.IsCommentLine(content))
                            {
                                CloseCode();
                                if (commentStart == 0) commentStart = nextNew;
                                if (addedMarks.Follow(content)) (markedLines ??= []).Add(nextNew);
                            }
                            else
                            {
                                CloseComments();
                                if (addStart == 0) addStart = nextNew;
                                if (content.TrimStart().StartsWith("#") && SourceLines.IsConditionalDirective(content))
                                    file.ConditionalDirectives.Add(new DiffLineRange(nextNew, nextNew));
                                if (file.Status == DiffFileStatus.Added && SourceLines.IsFileScopedDirective(content))
                                    file.FileScopedDirectives.Add(new DiffLineRange(nextNew, nextNew));
                            }
                            newLeft--;
                            nextNew++;
                            break;
                        case '\\':
                            // "\ No newline at end of file": about the line before, counted by neither side.
                            break;
                        default:
                            throw Malformed($"a line inside a hunk that is not context, '+' or '-' ('{Shorten(line.ToString())}')");
                    }
                }
                CloseBlock();
                file.OldEnd = nextOld;
                file.NewEnd = nextNew;
                // A marker may follow the last line.
                while (_position < text.Length && text[_position] == '\\') NextSpan(out _);

                void OpenBlock()
                {
                    if (blockNewBefore < 0) blockNewBefore = nextNew - 1;
                }

                void CloseAdded()
                {
                    CloseCode();
                    CloseComments();
                }

                void CloseCode()
                {
                    if (addStart == 0) return;
                    file.Added.Add(new DiffLineRange(addStart, nextNew - 1));
                    addStart = 0;
                }

                void CloseComments()
                {
                    if (commentStart == 0) return;
                    file.CommentAdded.Add(new DiffLineRange(commentStart, nextNew - 1));
                    commentStart = 0;
                }

                void CloseBlock()
                {
                    CloseAdded();
                    if (blockNewBefore < 0) return;
                    // Comment lines that leave a comment open, or close one, where the lines they replace did not: the
                    // unchanged lines beside them changed meaning. Removed, they are no comments-only removal.
                    if (addedMarks != removedMarks)
                    {
                        foreach (var marked in markedLines ?? []) file.CommentBoundaries.Add(new DiffLineRange(marked, marked));
                        if (removedComment) removedCode = true;
                    }
                    addedMarks = removedMarks = default;
                    markedLines = null;
                    if (removedStart > 0)
                    {
                        if (removedCode || removedComment)
                        {
                            file.Removals.Add(new DiffRemoval(
                                new DiffLineRange(removedStart, removedEnd), blockNewBefore, nextNew, CommentsOnly: !removedCode,
                                ContinuesBlockComment: !removedCode && removedContinues));
                        }
                        else file.IgnoredLines += removedBlank;
                    }
                    blockNewBefore = -1;
                    removedStart = removedEnd = removedBlank = 0;
                    removedCode = removedComment = removedContinues = false;
                }
            }

            private static bool TryRange(ReadOnlySpan<char> value, out int start, out int count)
            {
                count = 1;
                var comma = value.IndexOf(',');
                var startText = comma < 0 ? value : value[..comma];
                if (!int.TryParse(startText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out start)) return false;
                if (comma >= 0 && !int.TryParse(value[(comma + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out count)) return false;
                // Line 0 is only where a side with no lines starts (a new or deleted file); lines there do not exist.
                return start <= MaxLine && count <= MaxLine && (long)start + count <= MaxLine && (start > 0 || count == 0);
            }

            /// <summary>
            /// A path as a diff names it: git's C-style quoting undone, a trailing tab and timestamp dropped (diff -u),
            /// git's one-letter prefix removed when asked, and refused when it is absolute, leaves the root or is not
            /// plain text (then <see cref="DiffFile.PathProblem"/> says why). Null for <c>/dev/null</c>.
            /// </summary>
            private string? Path(string raw, bool stripPrefix, bool quoted = true)
            {
                string value;
                if (quoted && raw.StartsWith('"')) value = Quoted(raw, 0).Value;
                else if (!quoted) value = raw;
                else
                {
                    var tab = raw.IndexOf('\t');
                    value = tab >= 0 ? raw[..tab] : raw;
                }
                if (value == "/dev/null") return null;
                if (stripPrefix && value.Length > 2 && value[1] == '/' && char.IsAsciiLetterLower(value[0])) value = value[2..];
                if (Problem(value) is { } problem) _file!.PathProblem ??= problem;
                return value;
            }

            private static string? Problem(string path)
            {
                if (path.Length == 0) return "an empty path";
                if (path.Length > MaxPathLength) return $"a path longer than {MaxPathLength} characters";
                if (path.Any(char.IsControl)) return "a path holding a control character";
                if (path.StartsWith('/') || path.StartsWith('\\') || (path.Length > 1 && path[1] == ':') || System.IO.Path.IsPathRooted(path))
                    return "an absolute path";
                if (OperatingSystem.IsWindows() && path.Contains('\\')) return "a path holding '\\'";
                foreach (var segment in path.Split('/'))
                {
                    if (segment is "" or "." or "..") return "a path with an empty, '.' or '..' segment";
                }
                return null;
            }

            /// <summary>A C-quoted token starting at <paramref name="start"/>, and where it ends.</summary>
            private (string Value, int End) Quoted(string line, int start)
            {
                var bytes = new List<byte>();
                Span<byte> buffer = stackalloc byte[4];
                var i = start + 1;
                while (true)
                {
                    if (i >= line.Length) throw Malformed("a quoted path with no closing quote");
                    var c = line[i++];
                    if (c == '"') break;
                    if (c != '\\')
                    {
                        var written = Encoding.UTF8.GetBytes([c], buffer);
                        for (var b = 0; b < written; b++) bytes.Add(buffer[b]);
                        continue;
                    }
                    if (i >= line.Length) throw Malformed("a quoted path ending in '\\'");
                    var escape = line[i++];
                    switch (escape)
                    {
                        case 'a': bytes.Add(7); break;
                        case 'b': bytes.Add(8); break;
                        case 't': bytes.Add(9); break;
                        case 'n': bytes.Add(10); break;
                        case 'v': bytes.Add(11); break;
                        case 'f': bytes.Add(12); break;
                        case 'r': bytes.Add(13); break;
                        case '"': bytes.Add((byte)'"'); break;
                        case '\\': bytes.Add((byte)'\\'); break;
                        case >= '0' and <= '3' when i + 1 < line.Length && line[i] is >= '0' and <= '7' && line[i + 1] is >= '0' and <= '7':
                            bytes.Add((byte)(((escape - '0') << 6) | ((line[i] - '0') << 3) | (line[i + 1] - '0')));
                            i += 2;
                            break;
                        default:
                            throw Malformed($"a quoted path with an unknown escape '\\{escape}'");
                    }
                }
                string value;
                try
                {
                    value = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.ToArray());
                }
                catch (DecoderFallbackException)
                {
                    _file!.PathProblem ??= "a path that is not valid UTF-8";
                    value = Encoding.UTF8.GetString(bytes.ToArray());
                }
                return (value, i);
            }

            private static string Shorten(string value) => value.Length <= 60 ? value : value[..60] + "...";
        }
    }
}
