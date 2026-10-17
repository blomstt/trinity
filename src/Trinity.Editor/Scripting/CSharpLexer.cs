namespace Trinity.Editor.Scripting;

enum TokenKind : byte
{
    Plain,
    Keyword,
    Type,
    Method,
    String,
    Comment,
    Number,
    Preprocessor,
}

/// <summary>What a line starts inside of, carried from the line before.</summary>
enum LexState : byte
{
    Normal,
    BlockComment,
    VerbatimString,
    RawString,
}

readonly record struct Token(int Start, int Length, TokenKind Kind);

/// <summary>
/// Splits C# into coloured tokens a line at a time, for the script editor. Not a parser: identifiers
/// starting with a capital are shown as types, identifiers before '(' as methods, which reads well enough.
/// </summary>
static class CSharpLexer
{
    static readonly HashSet<string> Keywords =
    [
        "abstract", "and", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char",
        "checked", "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "dynamic",
        "else", "enum", "event", "explicit", "extern", "false", "file", "finally", "fixed", "float", "for",
        "foreach", "get", "global", "goto", "if", "implicit", "in", "init", "int", "interface", "internal", "is",
        "lock", "long", "nameof", "namespace", "new", "nint", "not", "nuint", "null", "object", "operator", "or",
        "out", "override", "params", "partial", "private", "protected", "public", "readonly", "record", "ref",
        "required", "return", "sbyte", "scoped", "sealed", "set", "short", "sizeof", "stackalloc", "static",
        "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked",
        "unsafe", "ushort", "using", "value", "var", "virtual", "void", "volatile", "when", "where", "while",
        "with", "yield",
    ];

    public static bool IsKeyword(string word) => Keywords.Contains(word);

    /// <summary>Tokenises <paramref name="line"/> (if <paramref name="tokens"/> is given) and returns the state the next line starts in.</summary>
    public static LexState Line(string line, LexState state, List<Token>? tokens)
    {
        var i = 0;
        var n = line.Length;

        // Continue a comment or string from the line before
        if (state != LexState.Normal)
        {
            var (end, closed) = state switch
            {
                LexState.BlockComment => FindEnd(line, 0, "*/"),
                LexState.RawString => FindEnd(line, 0, "\"\"\""),
                _ => VerbatimEnd(line, 0),
            };
            tokens?.Add(new Token(0, end, state == LexState.BlockComment ? TokenKind.Comment : TokenKind.String));
            if (!closed)
                return state;
            i = end;
            state = LexState.Normal;
        }

        // A preprocessor line (#if, #region, ...) is one token
        var first = 0;
        while (first < n && line[first] == ' ')
            first++;
        if (i == 0 && first < n && line[first] == '#')
        {
            tokens?.Add(new Token(first, n - first, TokenKind.Preprocessor));
            return LexState.Normal;
        }

        while (i < n)
        {
            var c = line[i];
            var start = i;
            if (c == '/' && i + 1 < n && line[i + 1] == '/')
            {
                tokens?.Add(new Token(i, n - i, TokenKind.Comment));
                return LexState.Normal;
            }
            if (c == '/' && i + 1 < n && line[i + 1] == '*')
            {
                var (end, closed) = FindEnd(line, i + 2, "*/");
                tokens?.Add(new Token(i, end - i, TokenKind.Comment));
                if (!closed)
                    return LexState.BlockComment;
                i = end;
                continue;
            }
            if (c == '"' || ((c == '@' || c == '$') && i + 1 < n && (line[i + 1] == '"' || line[i + 1] is '@' or '$')))
            {
                // Skip $ and @ prefixes, then see which kind of string this is
                var q = i;
                var verbatim = false;
                while (q < n && line[q] is '$' or '@')
                {
                    verbatim |= line[q] == '@';
                    q++;
                }
                if (q >= n || line[q] != '"')
                {
                    i++;
                    tokens?.Add(new Token(start, 1, TokenKind.Plain));
                    continue;
                }
                if (line.AsSpan(q).StartsWith("\"\"\""))
                {
                    var (end, closed) = FindEnd(line, q + 3, "\"\"\"");
                    tokens?.Add(new Token(start, end - start, TokenKind.String));
                    if (!closed)
                        return LexState.RawString;
                    i = end;
                    continue;
                }
                if (verbatim)
                {
                    var (end, closed) = VerbatimEnd(line, q + 1);
                    tokens?.Add(new Token(start, end - start, TokenKind.String));
                    if (!closed)
                        return LexState.VerbatimString;
                    i = end;
                    continue;
                }
                i = QuotedEnd(line, q + 1, '"');
                tokens?.Add(new Token(start, i - start, TokenKind.String));
                continue;
            }
            if (c == '\'')
            {
                i = QuotedEnd(line, i + 1, '\'');
                tokens?.Add(new Token(start, i - start, TokenKind.String));
                continue;
            }
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(line[i + 1])))
            {
                while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] is '.' or '_'))
                    i++;
                tokens?.Add(new Token(start, i - start, TokenKind.Number));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                while (i < n && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                    i++;
                if (tokens == null)
                    continue;
                var word = line[start..i];
                var next = i;
                while (next < n && line[next] == ' ')
                    next++;
                var kind = Keywords.Contains(word) ? TokenKind.Keyword
                    : next < n && line[next] == '(' ? TokenKind.Method
                    : char.IsUpper(c) ? TokenKind.Type
                    : TokenKind.Plain;
                tokens.Add(new Token(start, i - start, kind));
                continue;
            }
            i++;
            tokens?.Add(new Token(start, 1, TokenKind.Plain));
        }
        return LexState.Normal;
    }

    static (int End, bool Closed) FindEnd(string line, int from, string close)
    {
        var at = line.IndexOf(close, from, StringComparison.Ordinal);
        return at < 0 ? (line.Length, false) : (at + close.Length, true);
    }

    // Verbatim strings end at a quote that isn't doubled
    static (int End, bool Closed) VerbatimEnd(string line, int from)
    {
        for (var i = from; i < line.Length; i++)
            if (line[i] == '"')
            {
                if (i + 1 < line.Length && line[i + 1] == '"')
                    i++;
                else
                    return (i + 1, true);
            }
        return (line.Length, false);
    }

    static int QuotedEnd(string line, int from, char quote)
    {
        for (var i = from; i < line.Length; i++)
        {
            if (line[i] == '\\')
                i++;
            else if (line[i] == quote)
                return i + 1;
        }
        return line.Length;
    }

    /// <summary>The text with comments and string contents blanked out, same length, for simple scanning.</summary>
    public static string[] Code(IReadOnlyList<string> lines)
    {
        var result = new string[lines.Count];
        var state = LexState.Normal;
        var tokens = new List<Token>();
        for (var l = 0; l < lines.Count; l++)
        {
            tokens.Clear();
            state = Line(lines[l], state, tokens);
            var chars = lines[l].ToCharArray();
            foreach (var t in tokens)
                if (t.Kind is TokenKind.Comment or TokenKind.Preprocessor)
                    Array.Fill(chars, ' ', t.Start, t.Length);
                else if (t.Kind == TokenKind.String && t.Length > 2)
                    Array.Fill(chars, ' ', t.Start + 1, t.Length - 2);
            result[l] = new string(chars);
        }
        return result;
    }
}
