using System.Text;
using System.Text.RegularExpressions;

namespace CodeContext.Core.Services;

/// <summary>A path as ignore rules see it, prepared once for every rule that tests it.</summary>
internal readonly struct IgnorePath
{
    private IgnorePath(string text, string? plain)
    {
        Text = text;
        Plain = plain;
    }

    public string Text { get; }

    /// <summary>
    /// The text in the form the fast matcher compares, or null when only the regular
    /// expression can decide. Outside ASCII a case-insensitive expression equates letters
    /// ordinal folding does not, such as K and the Kelvin sign, and a newline changes what
    /// <c>$</c> and <c>.</c> mean.
    /// </summary>
    public string? Plain { get; }

    public static IgnorePath Create(string relativePath, bool ignoreCase)
    {
        var text = relativePath.Replace('\\', '/').TrimEnd('/');
        if (!Ascii.IsValid(text) || text.Contains('\n')) return new IgnorePath(text, null);
        return new IgnorePath(text, ignoreCase ? text.ToLowerInvariant() : text);
    }
}

/// <summary>
/// One line of a <c>.gitignore</c>. Every rule has a regular expression, which defines what
/// it means. A rule made only of plain characters also has a token list that reaches the
/// same decisions without one: expressions run interpreted under Native AOT, and evaluating
/// each on every path dominated the start of any repository with a typical ignore file.
/// Anything the tokens cannot match identically, such as a negated class, stays an expression.
/// </summary>
internal sealed class IgnorePattern
{
    private enum TokenKind : byte
    {
        Literal,

        /// <summary><c>?</c>: one character within a path segment.</summary>
        AnyCharacter,

        /// <summary><c>*</c>: any characters within a path segment.</summary>
        AnyInSegment,

        /// <summary><c>**</c> not followed by a slash: any characters, across segments.</summary>
        AnyAcrossSegments,

        /// <summary><c>**/</c>: nothing, or anything ending in a slash.</summary>
        AnyDirectories,

        CharacterClass,
    }

    private readonly record struct Token(TokenKind Kind, string Text = "", bool[]? Members = null);

    private readonly string _expression;
    private readonly RegexOptions _options;
    private readonly Token[]? _tokens;
    private readonly bool _lastSegmentOnly;
    private readonly string _requiredEnding;
    private Regex? _regex;

    private IgnorePattern(bool negated, bool directoryOnly, string expression, RegexOptions options, Token[]? tokens, bool lastSegmentOnly)
    {
        Negated = negated;
        DirectoryOnly = directoryOnly;
        _expression = expression;
        _options = options;
        _tokens = tokens;
        _lastSegmentOnly = lastSegmentOnly;
        _requiredEnding = tokens is { Length: > 0 } && tokens[^1].Kind == TokenKind.Literal ? tokens[^1].Text : string.Empty;

        // An expression the tokens stand in for cannot be malformed; any other is built
        // now, so a malformed rule fails when it is read, whatever paths are tested later.
        if (tokens is null) _regex = new Regex(expression, options);
    }

    public bool Negated { get; }

    public bool DirectoryOnly { get; }

    /// <summary>True when this rule can decide without its expression.</summary>
    internal bool IsPlain => _tokens is not null;

    public bool Matches(in IgnorePath path, bool isDirectory)
    {
        if (DirectoryOnly && !isDirectory) return false;
        if (_tokens is null || path.Plain is not { } plain)
        {
            return (_regex ??= new Regex(_expression, _options)).IsMatch(path.Text);
        }

        var text = plain.AsSpan();
        if (_lastSegmentOnly) text = text[(text.LastIndexOf('/') + 1)..];
        return text.EndsWith(_requiredEnding, StringComparison.Ordinal) && MatchFrom(0, text);
    }

    private bool MatchFrom(int index, ReadOnlySpan<char> text)
    {
        var tokens = _tokens!;
        for (; index < tokens.Length; index++)
        {
            var token = tokens[index];
            switch (token.Kind)
            {
                case TokenKind.Literal:
                    if (!text.StartsWith(token.Text, StringComparison.Ordinal)) return false;
                    text = text[token.Text.Length..];
                    break;

                case TokenKind.AnyCharacter:
                    if (text.IsEmpty || text[0] == '/') return false;
                    text = text[1..];
                    break;

                case TokenKind.CharacterClass:
                    if (text.IsEmpty || !token.Members![text[0]]) return false;
                    text = text[1..];
                    break;

                case TokenKind.AnyInSegment:
                    for (var length = 0; ; length++)
                    {
                        if (MatchFrom(index + 1, text[length..])) return true;
                        if (length == text.Length || text[length] == '/') return false;
                    }

                case TokenKind.AnyAcrossSegments:
                    for (var length = 0; length <= text.Length; length++)
                    {
                        if (MatchFrom(index + 1, text[length..])) return true;
                    }
                    return false;

                case TokenKind.AnyDirectories:
                    if (MatchFrom(index + 1, text)) return true;
                    for (var slash = text.IndexOf('/'); slash >= 0; slash = NextSlash(text, slash))
                    {
                        if (MatchFrom(index + 1, text[(slash + 1)..])) return true;
                    }
                    return false;
            }
        }
        return text.IsEmpty;
    }

    private static int NextSlash(ReadOnlySpan<char> text, int slash)
    {
        var next = text[(slash + 1)..].IndexOf('/');
        return next < 0 ? -1 : slash + 1 + next;
    }

    public static bool TryParse(string rawLine, bool ignoreCase, out IgnorePattern pattern)
    {
        pattern = null!;
        var line = TrimUnescapedTrailingSpaces(rawLine.TrimEnd('\r'));
        if (line.Length == 0 || line[0] == '#') return false;

        var negated = line[0] == '!';
        if (negated) line = line[1..];
        else if (line.StartsWith("\\!", StringComparison.Ordinal) || line.StartsWith("\\#", StringComparison.Ordinal))
            line = line[1..];
        if (line.Length == 0) return false;

        var directoryOnly = EndsWithUnescapedSlash(line);
        if (directoryOnly) line = line[..^1];
        var anchored = line.StartsWith("/", StringComparison.Ordinal);
        if (anchored) line = line[1..];
        var hasSlash = line.Contains('/');

        var tokens = new TokenList(ignoreCase);
        var prefix = anchored || hasSlash ? "^" : "(?:^|.*/)";
        var expression = prefix + Translate(line, tokens) + "$";
        var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        if (ignoreCase) options |= RegexOptions.IgnoreCase;

        // A rule with no slash applies at any depth. Unless it can cross a slash itself,
        // only the path's last segment can match it.
        var anyDepth = !anchored && !hasSlash;
        var lastSegmentOnly = anyDepth && !tokens.CrossesSegments;
        if (anyDepth && !lastSegmentOnly) tokens.StartWithAnyDirectories();
        pattern = new IgnorePattern(negated, directoryOnly, expression, options, tokens.ToArray(), lastSegmentOnly);
        return true;
    }

    private static string TrimUnescapedTrailingSpaces(string value)
    {
        var end = value.Length;
        while (end > 0 && value[end - 1] == ' ')
        {
            var slashes = 0;
            for (var index = end - 2; index >= 0 && value[index] == '\\'; index--) slashes++;
            if ((slashes & 1) == 1) break;
            end--;
        }
        return value[..end];
    }

    private static bool EndsWithUnescapedSlash(string value)
    {
        if (!value.EndsWith("/", StringComparison.Ordinal)) return false;
        var slashes = 0;
        for (var index = value.Length - 2; index >= 0 && value[index] == '\\'; index--) slashes++;
        return (slashes & 1) == 0;
    }

    /// <summary>Reads a glob once, producing its expression and, where possible, its tokens.</summary>
    private static string Translate(string glob, TokenList tokens)
    {
        var result = new StringBuilder();
        for (var index = 0; index < glob.Length; index++)
        {
            var character = glob[index];
            if (character == '\\' && index + 1 < glob.Length)
            {
                result.Append(Regex.Escape(glob[++index].ToString()));
                tokens.AddLiteral(glob[index]);
            }
            else if (character == '*')
            {
                var isDouble = index + 1 < glob.Length && glob[index + 1] == '*';
                if (!isDouble)
                {
                    result.Append("[^/]*");
                    tokens.Add(TokenKind.AnyInSegment);
                }
                else
                {
                    index++;
                    if (index + 1 < glob.Length && glob[index + 1] == '/')
                    {
                        index++;
                        result.Append("(?:.*/)?");
                        tokens.Add(TokenKind.AnyDirectories);
                    }
                    else
                    {
                        result.Append(".*");
                        tokens.Add(TokenKind.AnyAcrossSegments);
                    }
                }
            }
            else if (character == '?')
            {
                result.Append("[^/]");
                tokens.Add(TokenKind.AnyCharacter);
            }
            else if (character == '[')
            {
                var close = glob.IndexOf(']', index + 1);
                if (close < 0)
                {
                    result.Append("\\[");
                    tokens.AddLiteral('[');
                }
                else
                {
                    var content = glob[(index + 1)..close];
                    tokens.AddClass(content);
                    if (content.StartsWith('!')) content = "^" + content[1..];
                    result.Append('[').Append(content).Append(']');
                    index = close;
                }
            }
            else
            {
                result.Append(Regex.Escape(character.ToString()));
                tokens.AddLiteral(character);
            }
        }
        return result.ToString();
    }

    /// <summary>Collects tokens, and gives up on the first thing they could not match exactly.</summary>
    private sealed class TokenList(bool ignoreCase)
    {
        private readonly List<Token> _tokens = [];
        private readonly StringBuilder _literal = new();
        private bool _abandoned;

        public bool CrossesSegments { get; private set; }

        public void AddLiteral(char character)
        {
            if (!char.IsAscii(character) || character == '\n') _abandoned = true;
            else _literal.Append(Fold(character));
        }

        public void Add(TokenKind kind)
        {
            FlushLiteral();
            CrossesSegments |= kind == TokenKind.AnyAcrossSegments;
            _tokens.Add(new Token(kind));
        }

        /// <summary>
        /// Only a class of letters, digits and simple ranges: negation lets an expression
        /// match a slash, and the syntax of anything else is the expression engine's.
        /// </summary>
        public void AddClass(string content)
        {
            var members = new bool[128];
            var isPlain = content.Length > 0;
            for (var index = 0; isPlain && index < content.Length; index++)
            {
                var first = content[index];
                isPlain = char.IsAsciiLetterOrDigit(first);
                if (index + 2 < content.Length && content[index + 1] == '-')
                {
                    var last = content[index + 2];
                    isPlain &= char.IsAsciiLetterOrDigit(last) && first <= last;
                    for (var member = first; isPlain && member <= last; member++) members[Fold(member)] = true;
                    index += 2;
                }
                else if (isPlain)
                {
                    members[Fold(first)] = true;
                }
            }

            if (!isPlain)
            {
                _abandoned = true;
                return;
            }
            FlushLiteral();
            _tokens.Add(new Token(TokenKind.CharacterClass, Members: members));
        }

        public void StartWithAnyDirectories()
        {
            FlushLiteral();
            _tokens.Insert(0, new Token(TokenKind.AnyDirectories));
        }

        public Token[]? ToArray()
        {
            FlushLiteral();
            return _abandoned ? null : [.. _tokens];
        }

        private char Fold(char character) => ignoreCase ? char.ToLowerInvariant(character) : character;

        private void FlushLiteral()
        {
            if (_literal.Length == 0) return;
            _tokens.Add(new Token(TokenKind.Literal, _literal.ToString()));
            _literal.Clear();
        }
    }
}
