namespace CodeContext.Core.Diffs
{
    /// <summary>
    /// What the host tells about a line of source without parsing it, for the C-family syntax every bundled worker
    /// reads: whether it is blank, only a comment, or a directive whose effect stays in its file. Every rule here is
    /// lexical, read from the line alone, and is used only to leave a changed line out of an answer where nothing the
    /// index holds says otherwise; the callers say where that is.
    /// </summary>
    public static partial class SourceLines
    {
        /// <summary>U+FEFF: at the very start of a file, the byte-order mark (EF BB BF in UTF-8); anywhere else, a character.</summary>
        public const char ByteOrderMark = '\uFEFF';

        /// <summary>
        /// The text of 1-based line <paramref name="number"/> of a file as every rule here reads it: line 1 without a
        /// leading byte-order mark, which belongs to the file's encoding (Visual Studio and EF Core write one), not to
        /// the line; any other line as it is, since U+FEFF there is a character of the text. Every caller reading a
        /// line of a file, from a diff or from disk, reads it through this.
        /// </summary>
        public static ReadOnlySpan<char> FileLine(ReadOnlySpan<char> line, int number)
            => number == 1 && !line.IsEmpty && line[0] == ByteOrderMark ? line[1..] : line;

        /// <summary>Whether a line holds only whitespace.</summary>
        public static bool IsBlank(ReadOnlySpan<char> line) => line.IsWhiteSpace();

        /// <summary>
        /// Whether a line holds only comments in the C-family syntax every bundled worker reads: it starts, after
        /// whitespace, with <c>//</c>, <c>/*</c>, <c>*/</c> or a block comment's <c>*</c> (followed by a space or nothing),
        /// and where a block comment ends on the line nothing but comments follows it (<c>/* note */ int x;</c> is code);
        /// or it is a <c>#region</c>/<c>#endregion</c> line. Lexical only: a line inside a string, or <c>* b</c>
        /// continuing an expression, reads as a comment too, so this is trusted only where no member holds the line.
        /// </summary>
        public static bool IsCommentLine(ReadOnlySpan<char> line)
        {
            var text = line.Trim();
            if (text.StartsWith("#region") || text.StartsWith("#endregion")) return true;
            while (true)
            {
                if (text.StartsWith("//")) return true;
                int from;
                if (text.StartsWith("/*")) from = 2;
                else if (text.StartsWith("*/")) from = 0;
                else if (text.Length > 0 && text[0] == '*' && (text.Length == 1 || text[1] == ' ' || text[1] == '\t')) from = 1;
                else return false;
                var close = text[from..].IndexOf("*/");
                if (close < 0) return true;
                text = text[(from + close + 2)..].TrimStart();
                if (text.IsEmpty) return true;
                // What follows the end of a block comment must open another comment.
                if (!text.StartsWith("//") && !text.StartsWith("/*")) return false;
            }
        }

        /// <summary>
        /// Whether a line is a preprocessor directive that decides which lines are compiled: <c>#if</c>, <c>#elif</c>,
        /// <c>#else</c>, <c>#endif</c>, <c>#define</c> or <c>#undef</c> (and C's <c>#ifdef</c>, <c>#ifndef</c>), with
        /// whitespace allowed after the <c>#</c>. Such a line changes what the unchanged lines under it mean.
        /// </summary>
        public static bool IsConditionalDirective(ReadOnlySpan<char> line)
        {
            var text = line.TrimStart();
            if (text.IsEmpty || text[0] != '#') return false;
            text = text[1..].TrimStart();
            var length = 0;
            while (length < text.Length && char.IsAsciiLetter(text[length])) length++;
            return text[..length] is "if" or "elif" or "else" or "endif" or "define" or "undef" or "ifdef" or "ifndef";
        }

        /// <summary>
        /// Whether a C# line only directs how its own file binds: <c>using N;</c>, <c>using static N;</c> or
        /// <c>using A = N;</c> (not <c>global using</c>, which binds the whole project), <c>namespace N</c> (with <c>;</c>
        /// or <c>{</c>), a lone <c>{</c> or <c>}</c>, <c>#nullable</c> or <c>#pragma warning</c>. Lexical.
        /// </summary>
        public static bool IsFileScopedDirective(ReadOnlySpan<char> line) => FileScopedDirective().IsMatch(line);

        [System.Text.RegularExpressions.GeneratedRegex(
            @"^\s*(using\s+(static\s+)?[A-Za-z_@][\w.@]*(\s*=\s*[\w.@<>,\s]+)?\s*;|namespace\s+[A-Za-z_@][\w.@]*\s*[;{]?|[{}]|#nullable\b.*|#pragma\s+warning\b.*)\s*(//.*)?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
        private static partial System.Text.RegularExpressions.Regex FileScopedDirective();
    }

    /// <summary>
    /// Where a run of comment lines leaves a block comment, read from outside one: whether one is open at its end,
    /// and how many <c>*/</c> it holds that close a comment it did not open. Lexical, as <see cref="IsCommentLine"/>.
    /// </summary>
    public record struct CommentMarks(bool Open, int StrayCloses)
    {
        /// <summary>Reads one comment line; true when it holds a block-comment marker.</summary>
        public bool Follow(ReadOnlySpan<char> text)
        {
            var marked = false;
            for (var i = 0; i + 1 < text.Length; i++)
            {
                var pair = (text[i], text[i + 1]);
                if (Open)
                {
                    if (pair is not ('*', '/')) continue;
                    Open = false;
                }
                else if (pair is ('/', '/')) break;
                else if (pair is ('/', '*')) Open = true;
                else if (pair is ('*', '/')) StrayCloses++;
                else continue;
                marked = true;
                i++;
            }
            return marked;
        }
    }

    /// <summary>
    /// A file's comment lines, read once from its text: which lines are comments, and for each line where the run of
    /// comment and blank lines it is in begins and ends. Here the whole file is at hand, so a line that starts like the
    /// inside or the end of a block comment (<c>* text</c>, <c>*/</c>) is a comment only while a comment line above it
    /// left one open: a Markdown bullet in a string is not. Still lexical: a string that holds <c>//</c> or <c>/*</c>
    /// at the start of a line reads as a comment.
    /// </summary>
    public sealed class CommentRuns
    {
        private const byte Comment = 1, Open = 2, Quiet = 4;

        /// <summary>For 1-based line i: whether it is a comment, whether a block comment is open at its end, whether it is a comment or blank.</summary>
        private readonly byte[] _kind;

        /// <summary>For 1-based line i: the first line of its run (i + 1 when line i is neither comment nor blank).</summary>
        private readonly int[] _runStart;

        /// <summary>For 1-based line i: the last line of its run (i - 1 when line i is neither).</summary>
        private readonly int[] _runEnd;

        private CommentMarks _marks;

        /// <summary>The longest line looked at; a longer one counts as code.</summary>
        private const int MaxLineBytes = 64 * 1024;

        public int Count { get; private set; }

        private CommentRuns(int lines)
        {
            _kind = new byte[lines + 2];
            _runStart = new int[lines + 2];
            _runEnd = new int[lines + 2];
        }

        /// <summary>The runs of UTF-8 <paramref name="text"/>, its lines ended by LF or CRLF.</summary>
        public static CommentRuns Read(ReadOnlySpan<byte> text)
        {
            var runs = new CommentRuns(text.Count((byte)'\n') + 1);
            var buffer = System.Buffers.ArrayPool<char>.Shared.Rent(256);
            try
            {
                while (!text.IsEmpty)
                {
                    var end = text.IndexOf((byte)'\n');
                    var line = end < 0 ? text : text[..end];
                    text = end < 0 ? default : text[(end + 1)..];
                    // A line too long to be a comment anyone wrote (minified code) is read as code, not decoded.
                    if (line.Length > MaxLineBytes)
                    {
                        runs.Add("x");
                        continue;
                    }
                    if (line.Length > buffer.Length)
                    {
                        System.Buffers.ArrayPool<char>.Shared.Return(buffer);
                        buffer = System.Buffers.ArrayPool<char>.Shared.Rent(line.Length);
                    }
                    runs.Add(buffer.AsSpan(0, System.Text.Encoding.UTF8.GetChars(line, buffer)));
                }
            }
            finally
            {
                System.Buffers.ArrayPool<char>.Shared.Return(buffer);
            }
            runs.Close();
            return runs;
        }

        /// <summary>The runs of <paramref name="lines"/>.</summary>
        public static CommentRuns Of(IReadOnlyCollection<string> lines)
        {
            var runs = new CommentRuns(lines.Count);
            foreach (var line in lines) runs.Add(line);
            runs.Close();
            return runs;
        }

        private void Add(ReadOnlySpan<char> line)
        {
            var text = SourceLines.FileLine(line, Count + 1).Trim();
            var comment = SourceLines.IsCommentLine(text);
            // The inside or the end of a block comment only under a comment line that opened one.
            if (comment && text[0] == '*' && !_marks.Open) comment = false;
            if (comment) _marks.Follow(text);
            var quiet = comment || text.IsEmpty;
            var index = ++Count;
            _kind[index] = (byte)((comment ? Comment : 0) | (_marks.Open ? Open : 0) | (quiet ? Quiet : 0));
            _runStart[index] = !quiet ? index + 1 : (_kind[index - 1] & Quiet) != 0 ? _runStart[index - 1] : index;
        }

        private void Close()
        {
            _runEnd[Count + 1] = Count;
            for (var index = Count; index >= 1; index--)
            {
                var quiet = (_kind[index] & Quiet) != 0;
                _runEnd[index] = !quiet ? index - 1 : index < Count && (_kind[index + 1] & Quiet) != 0 ? _runEnd[index + 1] : index;
            }
        }

        public bool IsComment(int line) => line >= 1 && line <= Count && (_kind[line] & Comment) != 0;

        /// <summary>Whether a block comment that a comment line opened is still open after 1-based <paramref name="line"/>.</summary>
        public bool IsOpenAfter(int line) => line >= 1 && line <= Count && (_kind[line] & Open) != 0;

        /// <summary>
        /// Whether a comment between 1-based lines <paramref name="before"/> and <paramref name="after"/> is, with the
        /// comment and blank lines around it, the file's leading comment or the comment just above a declaration (one
        /// that starts at a line of <paramref name="starts"/>).
        /// </summary>
        public bool IsDocCommentOrHeader(int before, int after, HashSet<int> starts)
        {
            before = Math.Min(before, Count);
            if (before <= 0 || _runStart[before] == 1) return true;
            if (after < 1 || after > Count) return false;
            return starts.Contains(_runEnd[after] + 1);
        }
    }
}
