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
    internal record struct CommentMarks(bool Open, int StrayCloses)
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
}
