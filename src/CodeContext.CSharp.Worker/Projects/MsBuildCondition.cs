namespace CodeContext.CSharp.Worker.Projects;

/// <summary>
/// Evaluates an MSBuild <c>Condition</c>. The answer is three-valued: null means the
/// expression could not be read, which a project's meaning must not silently depend on, so
/// callers decide what doubt means and record it. False beats doubt under <c>And</c> and
/// true beats it under <c>Or</c>, as they would for any value the doubtful side could take.
/// </summary>
public sealed class MsBuildCondition
{
    private enum Kind { End, Text, Word, Open, Close, Not, Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual }

    private readonly record struct Token(Kind Kind, string Text);

    private readonly List<Token> _tokens;
    private readonly MsBuildExpander _expander;
    private int _position;
    private bool _malformed;

    private MsBuildCondition(List<Token> tokens, MsBuildExpander expander)
    {
        _tokens = tokens;
        _expander = expander;
    }

    public static bool? Evaluate(string? condition, MsBuildExpander expander)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;
        if (Tokenize(condition) is not { } tokens) return null;

        var parser = new MsBuildCondition(tokens, expander);
        var result = parser.ParseOr();
        return parser._malformed || parser.Current.Kind != Kind.End ? null : result;
    }

    private Token Current => _position < _tokens.Count ? _tokens[_position] : new Token(Kind.End, string.Empty);

    private bool? ParseOr()
    {
        var left = ParseAnd();
        while (IsWord("or"))
        {
            _position++;
            var right = ParseAnd();
            left = left == true || right == true ? true : left is null || right is null ? null : false;
        }
        return left;
    }

    private bool? ParseAnd()
    {
        var left = ParseNot();
        while (IsWord("and"))
        {
            _position++;
            var right = ParseNot();
            left = left == false || right == false ? false : left is null || right is null ? null : true;
        }
        return left;
    }

    private bool? ParseNot()
    {
        if (Current.Kind != Kind.Not) return ParseComparison();
        _position++;
        return !ParseNot();
    }

    private bool IsWord(string word)
        => Current.Kind == Kind.Word && Current.Text.Equals(word, StringComparison.OrdinalIgnoreCase);

    private bool? ParseComparison()
    {
        if (Current.Kind == Kind.Open)
        {
            _position++;
            var inner = ParseOr();
            if (Current.Kind != Kind.Close) _malformed = true;
            _position++;
            return inner;
        }

        if (Current.Kind == Kind.Word && _position + 1 < _tokens.Count && _tokens[_position + 1].Kind == Kind.Open)
        {
            return ParseFunction();
        }

        var left = ParseOperand(out var leftCertain);
        var comparison = Current.Kind;
        if (comparison is not (Kind.Equal or Kind.NotEqual or Kind.Less or Kind.LessOrEqual or Kind.Greater or Kind.GreaterOrEqual))
        {
            if (!leftCertain) return null;
            return left.ToLowerInvariant() switch
            {
                "true" or "on" or "yes" => true,
                "false" or "off" or "no" => false,
                _ => null,
            };
        }
        _position++;
        var right = ParseOperand(out var rightCertain);
        if (!leftCertain || !rightCertain) return null;

        switch (comparison)
        {
            case Kind.Equal: return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            case Kind.NotEqual: return !string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
        if (!MsBuildExpander.TryVersion(left, out var leftVersion)
            || !MsBuildExpander.TryVersion(right, out var rightVersion))
        {
            return null;
        }
        var order = leftVersion.CompareTo(rightVersion);
        return comparison switch
        {
            Kind.Less => order < 0,
            Kind.LessOrEqual => order <= 0,
            Kind.Greater => order > 0,
            _ => order >= 0,
        };
    }

    private bool? ParseFunction()
    {
        var name = Current.Text;
        _position += 2;
        var argument = ParseOperand(out var certain);
        if (Current.Kind != Kind.Close) _malformed = true;
        _position++;
        if (!certain) return null;

        switch (name.ToLowerInvariant())
        {
            case "exists":
                if (argument.Trim().Length == 0) return false;
                var path = _expander.FullPath(argument.Trim());
                return !_expander.IsForeign(path) && (File.Exists(path) || Directory.Exists(path));
            case "hastrailingslash":
                return argument.EndsWith('/') || argument.EndsWith('\\');
            default:
                return null;
        }
    }

    private string ParseOperand(out bool certain)
    {
        var token = Current;
        if (token.Kind is not (Kind.Text or Kind.Word))
        {
            _malformed = true;
            certain = false;
            return string.Empty;
        }
        _position++;

        var before = _expander.Uncertain;
        _expander.Uncertain = false;
        var value = _expander.Expand(token.Text);
        certain = !_expander.Uncertain;
        _expander.Uncertain = before;
        return value;
    }

    private static List<Token>? Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            var character = text[i];
            if (char.IsWhiteSpace(character))
            {
                i++;
            }
            else if (character == '\'')
            {
                var end = EndOfQuoted(text, i);
                if (end < 0) return null;
                tokens.Add(new Token(Kind.Text, text[(i + 1)..end]));
                i = end + 1;
            }
            else if (character == '$' && i + 1 < text.Length && text[i + 1] == '(')
            {
                var end = MsBuildExpander.FindClosingParenthesis(text, i + 1);
                if (end < 0) return null;
                tokens.Add(new Token(Kind.Text, text[i..(end + 1)]));
                i = end + 1;
            }
            else if (character == '(')
            {
                tokens.Add(new Token(Kind.Open, "("));
                i++;
            }
            else if (character == ')')
            {
                tokens.Add(new Token(Kind.Close, ")"));
                i++;
            }
            else if (character is '=' or '!' or '<' or '>')
            {
                var doubled = i + 1 < text.Length && text[i + 1] == '=';
                var kind = (character, doubled) switch
                {
                    ('=', true) => Kind.Equal,
                    ('!', true) => Kind.NotEqual,
                    ('!', false) => Kind.Not,
                    ('<', true) => Kind.LessOrEqual,
                    ('<', false) => Kind.Less,
                    ('>', true) => Kind.GreaterOrEqual,
                    ('>', false) => Kind.Greater,
                    _ => Kind.End,
                };
                if (kind == Kind.End) return null;
                tokens.Add(new Token(kind, string.Empty));
                i += doubled ? 2 : 1;
            }
            else if (char.IsLetterOrDigit(character) || character is '_' or '-' or '.')
            {
                var start = i;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '-' or '.')) i++;
                tokens.Add(new Token(Kind.Word, text[start..i]));
            }
            else
            {
                return null;
            }
        }
        return tokens;
    }

    /// <summary>A quote inside <c>$(...)</c> belongs to the expression, not to this string.</summary>
    private static int EndOfQuoted(string text, int open)
    {
        for (var i = open + 1; i < text.Length; i++)
        {
            if (text[i] == '\'') return i;
            if (text[i] == '$' && i + 1 < text.Length && text[i + 1] == '(')
            {
                var end = MsBuildExpander.FindClosingParenthesis(text, i + 1);
                if (end < 0) return -1;
                i = end;
            }
        }
        return -1;
    }
}
