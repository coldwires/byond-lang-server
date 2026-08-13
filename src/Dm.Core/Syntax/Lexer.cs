using System;
using System.Collections.Generic;
using Dm.Core.Diagnostics;
using Dm.Core.Text;

namespace Dm.Core.Syntax;

/// <summary>
/// Turns DM source into a flat token stream.
/// </summary>
/// <remarks>
/// <para>
/// Never throws on malformed input. Anything unrecognised becomes a <see cref="TokenKind.Unknown"/>
/// token plus a diagnostic, so an editor buffer mid-keystroke still lexes end to end. The
/// stddef.dm corpus test asserts zero Unknown tokens, which is what catches an operator missing
/// from the table.
/// </para>
/// <para>
/// Layout: <see cref="TokenKind.Newline"/>, <see cref="TokenKind.Indent"/> and
/// <see cref="TokenKind.Dedent"/> are emitted for block structure. Depth is measured by
/// <see cref="MeasureDepth"/>, which follows what dm.exe actually accepts rather than any tidier
/// rule. Blank lines, comment-only lines and preprocessor directives never change the level.
/// </para>
/// <para>
/// Interpolated strings emit a flat run: <c>StringStart, StringText, InterpolationStart,
/// …expression…, InterpolationEnd, StringText, StringEnd</c>. Strings may nest inside an
/// interpolation hole, so the state is a stack.
/// </para>
/// </remarks>
internal sealed class Lexer
{
    private sealed class StringState
    {
        /// <summary>
        /// The exact text that closes this string: <c>"</c>, <c>"}</c>, or — for a raw string —
        /// whatever delimiter the author chose.
        /// </summary>
        public string Terminator = "\"";

        /// <summary>
        /// A raw string. Neither backslash escapes nor <c>[...]</c> interpolation are active, which
        /// is the whole point of the form — it exists so regexes and Windows paths can be written
        /// literally.
        /// </summary>
        public bool Raw;

        /// <summary>False for single-line forms, where a line break means the string is unterminated.</summary>
        public bool AllowsLineBreaks;

        public bool InInterpolation;
        public int BracketDepth;
    }

    private readonly SourceText _text;
    private readonly List<Token> _tokens = new();
    private readonly List<Diagnostic> _diagnostics = new();
    private readonly List<int> _indents = new() { 0 };
    private readonly Stack<StringState> _strings = new();

    private int _position;
    private bool _atLineStart = true;

    /// <summary>Depth of <c>(</c> and <c>[</c> nesting. Layout tokens are suppressed inside.</summary>
    private int _groupingDepth;

    /// <summary>Whether the current line carried a directive; its newline survives grouping.</summary>
    private bool _lineHadDirective;

    private Lexer(SourceText text) => _text = text;

    public static LexResult Lex(SourceText text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Lexer lexer = new(text);
        lexer.Run();
        return new LexResult(text, lexer._tokens, lexer._diagnostics);
    }

    private char Current => _position < _text.Length ? _text[_position] : '\0';

    private char Peek(int offset = 1)
    {
        int index = _position + offset;
        return index < _text.Length ? _text[index] : '\0';
    }

    private bool AtEnd => _position >= _text.Length;

    private void Add(TokenKind kind, int start) => _tokens.Add(new Token(kind, TextSpan.FromBounds(start, _position)));

    private void Report(string id, int start, string message)
        => _diagnostics.Add(Diagnostic.Error(id, TextSpan.FromBounds(start, Math.Max(_position, start + 1)), message));

    private void Run()
    {
        while (!AtEnd)
        {
            if (_strings.Count > 0 && !_strings.Peek().InInterpolation)
            {
                LexStringBody();
                continue;
            }

            if (_atLineStart && _groupingDepth == 0 && _strings.Count == 0)
            {
                HandleLineStart();
                if (AtEnd)
                    break;
            }

            LexCode();
        }

        // Close any block still open at end of file.
        while (_indents.Count > 1)
        {
            _indents.RemoveAt(_indents.Count - 1);
            Add(TokenKind.Dedent, _position);
        }

        Add(TokenKind.EndOfFile, _position);
    }

    // -- layout ------------------------------------------------------------

    private void HandleLineStart()
    {
        _atLineStart = false;

        int lineStart = _position;
        while (!AtEnd && (Current == ' ' || Current == '\t'))
            _position++;

        // Blank lines, comment-only lines and preprocessor directives leave the level alone.
        //
        // Blank lines: emitting Dedent would close blocks the author is still inside.
        //
        // Directives: `#ifdef` and `#endif` are conventionally written at column 0 regardless of
        // the surrounding code's indentation, and they neither open nor close a block. Treating
        // one as a normal line dedents to the root, and the next real line then re-enters at its
        // own depth without restoring the levels in between — so every later dedent misses. Real
        // DM does this constantly, wrapping indented code in column-0 conditionals.
        //
        // A `#` at the start of a line is always a directive. Stringification only appears inside
        // a macro body, never in this position.
        if (AtEnd || IsLineTerminator(Current) || IsCommentStart() || Current == '#')
            return;

        int depth = MeasureDepth(_text.Content.AsSpan(lineStart, _position - lineStart));

        if (depth == _indents[^1])
            return;

        if (depth > _indents[^1])
        {
            _indents.Add(depth);
            _tokens.Add(new Token(TokenKind.Indent, TextSpan.FromBounds(lineStart, _position)));
            return;
        }

        while (_indents.Count > 1 && depth < _indents[^1])
        {
            _indents.RemoveAt(_indents.Count - 1);
            _tokens.Add(new Token(TokenKind.Dedent, TextSpan.FromBounds(_position, _position)));
        }

        // Landing between two levels means the file has an indentation DM would itself reject.
        // We adopt the level rather than report: under-reporting is far safer than flagging valid
        // code, and DM's own rule is not yet fully modelled (see below).
        if (depth != _indents[^1])
            _indents[^1] = depth;
    }

    /// <summary>
    /// Indentation depth of a line's leading whitespace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tabs decide the depth; spaces only count when there are no tabs at all. This is not a guess
    /// — it is the only simple model consistent with what dm.exe 516.1666 actually accepts. Given a
    /// sibling declared at one tab:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>" \t"</c>, <c>"\t "</c> — accepted as the same level. Tab count 1 either way.</description></item>
    /// <item><description><c>" "</c> — accepted as the same level. No tabs, so one space is depth 1.</description></item>
    /// <item><description><c>"    "</c> — <b>rejected</b> by dm.exe as "inconsistent indentation". Depth 4 here, so we treat it as nesting.</description></item>
    /// </list>
    /// <para>
    /// Prefix comparison, which is what this used to do, fails the first two: neither <c>"\t"</c>
    /// nor <c>" \t"</c> is a prefix of the other, so it reported an error on code DM compiles.
    /// </para>
    /// <para>
    /// The last case is the one place we knowingly diverge: DM errors, we silently nest. Missing an
    /// error is far cheaper than flagging valid code in an editor, and DM's exact rule is still
    /// unmodelled — see PLAN.md open questions.
    /// </para>
    /// </remarks>
    private static int MeasureDepth(ReadOnlySpan<char> indent)
    {
        int tabs = 0;
        int spaces = 0;

        foreach (char c in indent)
        {
            if (c == '\t')
                tabs++;
            else if (c == ' ')
                spaces++;
        }

        return tabs > 0 ? tabs : spaces;
    }

    private bool IsCommentStart() => Current == '/' && (Peek() == '/' || Peek() == '*');

    private static bool IsLineTerminator(char c) => c == '\n' || c == '\r';

    private void ConsumeLineTerminator()
    {
        int start = _position;

        if (Current == '\r')
        {
            _position++;
            if (Current == '\n')
                _position++;
        }
        else
        {
            _position++;
        }

        // A line that carried a directive keeps its newline even inside a group: the directive
        // scanner needs the terminator, or an `#include` spliced into an argument list swallows
        // the token after it. See LexHash.
        if (_groupingDepth == 0 || _lineHadDirective)
            Add(TokenKind.Newline, start);

        _lineHadDirective = false;
        _atLineStart = _groupingDepth == 0;
    }

    // -- code --------------------------------------------------------------

    private void LexCode()
    {
        while (!AtEnd && (Current == ' ' || Current == '\t'))
            _position++;

        if (AtEnd)
            return;

        char c = Current;
        int start = _position;

        if (IsLineTerminator(c))
        {
            ConsumeLineTerminator();
            return;
        }

        if (c == '\\' && IsLineTerminatorAfterBackslash())
        {
            // Line continuation: swallow the backslash and the terminator, emit nothing, and do
            // not re-measure indentation on the joined line.
            _position++;
            if (Current == '\r')
                _position++;
            if (Current == '\n')
                _position++;
            return;
        }

        if (IsCommentStart())
        {
            LexComment();
            return;
        }

        if (c == '"' || (c == '{' && Peek() == '"'))
        {
            LexStringStart(raw: false);
            return;
        }

        // `@` has no other meaning in DM, and the delimiter that follows it can be any single
        // character, so anything after `@` other than whitespace opens a raw string.
        if (c == '@' && !AtEndAt(_position + 1) && !IsLineTerminator(Peek()) && Peek() != ' ' && Peek() != '\t')
        {
            LexStringStart(raw: true);
            return;
        }

        if (c == '\'')
        {
            LexResource();
            return;
        }

        if (char.IsDigit(c) || (c == '.' && char.IsDigit(Peek())))
        {
            LexNumber();
            return;
        }

        // A backslash not before a line break begins a name. See LexIdentifier.
        if (IsIdentifierStart(c) || (c == '\\' && IsNameEscapeAt(_position)))
        {
            LexIdentifier();
            return;
        }

        if (c == '#')
        {
            LexHash();
            return;
        }

        LexOperator(start);
    }

    private bool IsLineTerminatorAfterBackslash()
    {
        char next = Peek();
        return next == '\n' || next == '\r';
    }

    private void LexComment()
    {
        int start = _position;

        if (Peek() == '/')
        {
            // A backslash at the end of a line comment continues it onto the next line, as in C.
            // Verified against dm.exe: a `//` comment ending in `\` followed by a line of garbage
            // compiles clean, so the garbage was still comment. Real code relies on this to wrap
            // long explanatory comments.
            while (!AtEnd)
            {
                if (Current == '\\' && !AtEndAt(_position + 1) && IsLineTerminator(Peek()))
                {
                    _position++;

                    if (Current == '\r')
                        _position++;
                    if (Current == '\n')
                        _position++;

                    continue;
                }

                if (IsLineTerminator(Current))
                    break;

                _position++;
            }

            Add(TokenKind.Comment, start);
            return;
        }

        // Block comments nest in DM, so a depth counter is required rather than scanning for the
        // first "*/". Verified against dm.exe 516.1666: `/*` `/*` `*/` reports "end of file
        // reached inside of comment", which only happens if the inner delimiter nested.
        _position += 2;
        int depth = 1;

        while (!AtEnd && depth > 0)
        {
            // A line comment inside a block comment hides both delimiters to end of line. Also
            // verified against dm.exe: `/*` then `// */` then code then `*/` compiles clean, so
            // the `*/` on the `//` line was ignored. Without this, a line like
            // `//*see the article` nests the block comment and swallows the rest of the file.
            //
            // Quotes get no such treatment — `"*/"` inside a block comment does close it.
            if (Current == '/' && Peek() == '/')
            {
                while (!AtEnd && !IsLineTerminator(Current))
                    _position++;

                continue;
            }

            if (Current == '/' && Peek() == '*')
            {
                depth++;
                _position += 2;
            }
            else if (Current == '*' && Peek() == '/')
            {
                depth--;
                _position += 2;
            }
            else
            {
                _position++;
            }
        }

        if (depth > 0)
            Report("DM0002", start, "unterminated block comment");

        Add(TokenKind.Comment, start);
    }

    /// <summary>
    /// Opens a string and records what will close it.
    /// </summary>
    /// <remarks>
    /// Ordinary strings are <c>"…"</c> or <c>{"…"}</c>. Raw strings are the awkward ones: the
    /// reference documents three forms, all verified to compile.
    /// <list type="bullet">
    /// <item><description><c>@X…X</c> — <b>any single character</b> is the delimiter. <c>@#…#</c>,
    /// <c>@!…!</c>, and critically <c>@/(\d+)/</c>, which is what raw strings exist for. No line
    /// breaks allowed.</description></item>
    /// <item><description><c>@{"…"}</c> — multiline.</description></item>
    /// <item><description><c>@(XYZ)…XYZ</c> — an arbitrary multi-character terminator, multiline.</description></item>
    /// </list>
    /// Treating <c>@</c> as only ever introducing <c>@"</c> mis-lexes <c>@/(\d+)/</c> as a division,
    /// which is a silent wrong answer rather than an error.
    /// </remarks>
    private void LexStringStart(bool raw)
    {
        int start = _position;

        if (!raw)
        {
            bool multiline = Current == '{';
            _position += multiline ? 2 : 1;

            Add(TokenKind.StringStart, start);
            _strings.Push(new StringState
            {
                Terminator = multiline ? "\"}" : "\"",
                AllowsLineBreaks = multiline,
            });
            return;
        }

        _position++;

        if (Current == '{' && Peek() == '"')
        {
            _position += 2;
            Add(TokenKind.StringStart, start);
            _strings.Push(new StringState { Terminator = "\"}", Raw = true, AllowsLineBreaks = true });
            return;
        }

        if (Current == '(')
        {
            int reset = _position;
            _position++;

            int textStart = _position;
            while (!AtEnd && Current != ')' && !IsLineTerminator(Current))
                _position++;

            if (Current == ')' && _position > textStart)
            {
                string terminator = _text.Content.Substring(textStart, _position - textStart);
                _position++;

                Add(TokenKind.StringStart, start);
                _strings.Push(new StringState { Terminator = terminator, Raw = true, AllowsLineBreaks = true });
                return;
            }

            // Malformed. Fall through and treat the '(' itself as the delimiter.
            _position = reset;
        }

        if (AtEnd)
        {
            Add(TokenKind.Unknown, start);
            Report("DM0006", start, "'@' at end of file");
            return;
        }

        string delimiter = Current.ToString();
        _position++;

        Add(TokenKind.StringStart, start);
        _strings.Push(new StringState { Terminator = delimiter, Raw = true, AllowsLineBreaks = false });
    }

    private void LexResource()
    {
        int start = _position;
        _position++;

        while (!AtEnd && Current != '\'')
        {
            if (Current == '\\' && !AtEndAt(_position + 1))
            {
                ConsumeStringEscape();
                continue;
            }

            if (IsLineTerminator(Current))
                break;

            _position++;
        }

        if (Current == '\'')
            _position++;
        else
            Report("DM0004", start, "unterminated resource literal");

        Add(TokenKind.Resource, start);
    }

    private bool AtEndAt(int index) => index >= _text.Length;

    private bool MatchesAt(int index, string value)
    {
        if (value.Length == 0 || index + value.Length > _text.Length)
            return false;

        for (int i = 0; i < value.Length; i++)
        {
            if (_text[index + i] != value[i])
                return false;
        }

        return true;
    }

    /// <summary>True when the text just after the cursor matches, case-sensitively.</summary>
    private bool MatchesAhead(string text)
    {
        if (_position + text.Length >= _text.Length)
            return false;

        for (int i = 0; i < text.Length; i++)
        {
            if (_text[_position + 1 + i] != text[i])
                return false;
        }

        return true;
    }

    private void LexNumber()
    {
        int start = _position;

        if (Current == '0' && (Peek() == 'x' || Peek() == 'X'))
        {
            _position += 2;
            while (!AtEnd && Uri.IsHexDigit(Current))
                _position++;

            Add(TokenKind.Number, start);
            return;
        }

        while (!AtEnd && char.IsDigit(Current))
            _position++;

        // Only take the dot if a digit follows, so `1.Foo()` stays a number then a member access.
        if (Current == '.' && char.IsDigit(Peek()))
        {
            _position++;
            while (!AtEnd && char.IsDigit(Current))
                _position++;
        }

        // A trailing dot with nothing name-like after it is part of the number, not an operator.
        // /tg/station writes `COOLDOWN_START(src, x, 0. SECONDS)` and `SECONDS` is `*10`, so the
        // stream is `0. *10`. Split, the dot reads as member access and asks for a member name.
        // The name check is what keeps `1.Foo()` a member access, which is the case above.
        else if (Current == '.' && Peek() != '.' && !char.IsLetter(Peek()) && Peek() != '_')
        {
            _position++;
        }

        // The infinity and indeterminate literals, `1#INF` and `1#IND`. Left split, the `#` reads as
        // a directive and the rest as a name. Found in ter13's HudLib.
        if (Current == '#' && MatchesAhead("INF"))
        {
            _position += 4;
            Add(TokenKind.Number, start);
            return;
        }

        if (Current == '#' && MatchesAhead("IND"))
        {
            _position += 4;
            Add(TokenKind.Number, start);
            return;
        }

        if (Current is 'e' or 'E')
        {
            int save = _position;
            _position++;

            if (Current is '+' or '-')
                _position++;

            if (char.IsDigit(Current))
            {
                while (!AtEnd && char.IsDigit(Current))
                    _position++;
            }
            else
            {
                _position = save;
            }
        }

        Add(TokenKind.Number, start);
    }

    /// <summary>
    /// Lexes an identifier, including embedded backslash escapes.
    /// </summary>
    /// <remarks>
    /// DM names may contain <c>\</c> escapes, which control how a verb or var is presented to
    /// players — <c>\the</c>, <c>\proper</c>, <c>\~</c> and so on. Verified against dm.exe:
    /// <c>\~Admin_Chat(T as text)</c>, <c>D\~E</c> mid-name, and <c>var/\~G</c> all compile. A bare
    /// <c>\~</c> in expression position does not, but rejecting that is the parser's job — the
    /// lexer has no way to know which position it is in.
    ///
    /// The escaped character can be anything, including a digit, so the rule is simply "backslash
    /// plus one character", not a lookup of known macros.
    /// </remarks>
    private void LexIdentifier()
    {
        int start = _position;

        while (!AtEnd)
        {
            if (IsNameEscapeAt(_position))
            {
                _position += 2;
                continue;
            }

            if (!IsIdentifierPart(Current))
                break;

            _position++;
        }

        string text = _text.Content.Substring(start, _position - start);
        Add(Keywords.Lookup(text), start);
    }

    /// <summary>
    /// True if a name escape starts at <paramref name="index"/>: a backslash followed by a
    /// character that is neither whitespace nor a line terminator.
    /// </summary>
    /// <remarks>
    /// Excluding line terminators is what keeps this from swallowing a line continuation, which is
    /// checked earlier and means something entirely different.
    /// </remarks>
    private bool IsNameEscapeAt(int index)
    {
        if (index >= _text.Length || _text[index] != '\\')
            return false;

        if (index + 1 >= _text.Length)
            return false;

        char next = _text[index + 1];
        return next != ' ' && next != '\t' && !IsLineTerminator(next);
    }

    private void LexHash()
    {
        int start = _position;
        _position++;
        Add(TokenKind.Hash, start);

        // At the start of a line this introduces a directive. Elsewhere `#` is stringification
        // inside a macro body, and the operand is an ordinary identifier.
        bool atDirectivePosition = IsAtStartOfLogicalLine(start);

        while (!AtEnd && (Current == ' ' || Current == '\t'))
            _position++;

        if (!atDirectivePosition || !IsIdentifierStart(Current))
            return;

        int nameStart = _position;
        while (!AtEnd && IsIdentifierPart(Current))
            _position++;

        Add(TokenKind.DirectiveName, nameStart);

        // A directive line ends at its physical line end even inside an open bracket, where
        // newlines are otherwise suppressed so argument lists can wrap. dm.exe allows an
        // `#include` inside an open paren — tgstation splices a version file into a call — and
        // without the terminator the directive scanner reads the NEXT line's tokens as payload,
        // swallowing the very `)` that closes the group.
        _lineHadDirective = true;

        // `#warn` and `#error` take free text, not tokens. The compiler prints the rest of the
        // line verbatim, so apostrophes and unbalanced quotes are legal there — real library code
        // contains `#warn ... HudLib won't work`, whose apostrophe would otherwise open a resource
        // literal and run to end of line.
        string name = _text.Content.Substring(nameStart, _position - nameStart);
        if (name is "warn" or "error")
            LexDirectiveText();
    }

    private void LexDirectiveText()
    {
        while (!AtEnd && (Current == ' ' || Current == '\t'))
            _position++;

        int start = _position;
        while (!AtEnd && !IsLineTerminator(Current))
            _position++;

        if (_position > start)
            Add(TokenKind.DirectiveText, start);
    }

    private bool IsAtStartOfLogicalLine(int hashOffset)
    {
        for (int i = hashOffset - 1; i >= 0; i--)
        {
            char c = _text[i];

            if (c == ' ' || c == '\t')
                continue;

            return c == '\n' || c == '\r';
        }

        return true;
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    // -- strings -----------------------------------------------------------

    private void LexStringBody()
    {
        StringState state = _strings.Peek();
        int start = _position;

        while (!AtEnd)
        {
            char c = Current;

            if (c == '\\' && !state.Raw && !AtEndAt(_position + 1))
            {
                ConsumeStringEscape();
                continue;
            }

            if (c == '[' && !state.Raw)
            {
                FlushStringText(start);
                int bracket = _position;
                _position++;
                Add(TokenKind.InterpolationStart, bracket);

                state.InInterpolation = true;
                state.BracketDepth = 0;
                return;
            }

            if (MatchesAt(_position, state.Terminator))
            {
                FlushStringText(start);
                int terminator = _position;
                _position += state.Terminator.Length;
                Add(TokenKind.StringEnd, terminator);
                _strings.Pop();
                return;
            }

            // A single-line form cannot span lines. Stop at the break so the rest of the file still
            // lexes; the newline itself belongs to the enclosing code.
            if (!state.AllowsLineBreaks && IsLineTerminator(c))
            {
                FlushStringText(start);
                Report("DM0001", start, "unterminated string literal");
                _strings.Pop();
                return;
            }

            _position++;
        }

        FlushStringText(start);
        Report("DM0001", start, "unterminated string literal");
        _strings.Pop();
    }

    private void FlushStringText(int start)
    {
        if (_position > start)
            Add(TokenKind.StringText, start);
    }

    /// <summary>
    /// Consumes a backslash and whatever it escapes.
    /// </summary>
    /// <remarks>
    /// A backslash immediately before a line break is a continuation: the string carries on to the
    /// next line and the break is not part of its value. The terminator has to be consumed whole —
    /// skipping a fixed two characters eats the CR of a CRLF pair and leaves the LF behind, which
    /// then reads as the end of an unterminated single-line string. That failure is invisible on
    /// LF files and appears only on Windows-authored source, where this form is common for long
    /// description text.
    /// </remarks>
    private void ConsumeStringEscape()
    {
        _position++;

        if (Current == '\r')
        {
            _position++;
            if (Current == '\n')
                _position++;

            return;
        }

        _position++;
    }

    // -- operators ---------------------------------------------------------

    private void LexOperator(int start)
    {
        char c = Current;

        // Bracket depth has to be tracked before the interpolation check, since an interpolation
        // hole may itself contain indexing: "[list[i]]".
        StringState? enclosing = _strings.Count > 0 && _strings.Peek().InInterpolation ? _strings.Peek() : null;

        if (enclosing is not null)
        {
            // `?[` is a single token, so the `[` never arrives on its own — but it still opens a
            // bracket that the matching `]` closes. Missed, the hole ends at that `]` instead:
            // `"[( L?["[k]"] ? 0 : 1 )]"` closed after `L?["[k]"`, and everything after it was
            // read as an expression that then failed. 174 diagnostics on /tg/station, all from
            // OFFSET_RENDER_TARGET, which nests exactly this shape.
            if (c == '[' || (c == '?' && Peek() == '['))
            {
                enclosing.BracketDepth++;
            }
            else if (c == ']')
            {
                if (enclosing.BracketDepth == 0)
                {
                    _position++;
                    Add(TokenKind.InterpolationEnd, start);
                    enclosing.InInterpolation = false;
                    return;
                }

                enclosing.BracketDepth--;
            }
        }

        TokenKind kind = MatchOperator();

        if (kind == TokenKind.Unknown)
        {
            _position++;
            Report("DM0005", start, $"unexpected character '{c}'");
        }

        Add(kind, start);
    }

    /// <summary>Longest-match operator scan. Advances past whatever it matched.</summary>
    private TokenKind MatchOperator()
    {
        char c = Current;
        char c1 = Peek();
        char c2 = Peek(2);

        switch (c)
        {
            case '(': _position++; _groupingDepth++; return TokenKind.OpenParen;
            case ')': _position++; if (_groupingDepth > 0) _groupingDepth--; return TokenKind.CloseParen;
            case '[': _position++; _groupingDepth++; return TokenKind.OpenBracket;
            case ']': _position++; if (_groupingDepth > 0) _groupingDepth--; return TokenKind.CloseBracket;
            case '{': _position++; return TokenKind.OpenBrace;
            case '}': _position++; return TokenKind.CloseBrace;
            case ',': _position++; return TokenKind.Comma;
            case ';': _position++; return TokenKind.Semicolon;

            case ':':
                if (c1 == ':') { _position += 2; return TokenKind.ColonColon; }
                if (c1 == '=') { _position += 2; return TokenKind.ColonAssign; }
                _position++; return TokenKind.Colon;

            case '?':
                if (c1 == '.') { _position += 2; return TokenKind.QuestionDot; }
                if (c1 == ':') { _position += 2; return TokenKind.QuestionColon; }
                if (c1 == '[') { _position += 2; _groupingDepth++; return TokenKind.QuestionOpenBracket; }
                _position++; return TokenKind.Question;

            case '.':
                if (c1 == '.') { _position += 2; return TokenKind.DotDot; }
                _position++; return TokenKind.Dot;

            case '+':
                if (c1 == '+') { _position += 2; return TokenKind.PlusPlus; }
                if (c1 == '=') { _position += 2; return TokenKind.PlusAssign; }
                _position++; return TokenKind.Plus;

            case '-':
                if (c1 == '-') { _position += 2; return TokenKind.MinusMinus; }
                if (c1 == '=') { _position += 2; return TokenKind.MinusAssign; }
                _position++; return TokenKind.Minus;

            case '*':
                if (c1 == '*' && c2 == '=') { _position += 3; return TokenKind.StarStarAssign; }
                if (c1 == '*') { _position += 2; return TokenKind.StarStar; }
                if (c1 == '=') { _position += 2; return TokenKind.StarAssign; }
                _position++; return TokenKind.Star;

            case '/':
                if (c1 == '=') { _position += 2; return TokenKind.SlashAssign; }
                _position++; return TokenKind.Slash;

            case '%':
                if (c1 == '%' && c2 == '=') { _position += 3; return TokenKind.PercentPercentAssign; }
                if (c1 == '%') { _position += 2; return TokenKind.PercentPercent; }
                if (c1 == '=') { _position += 2; return TokenKind.PercentAssign; }
                _position++; return TokenKind.Percent;

            case '=':
                if (c1 == '=') { _position += 2; return TokenKind.Equal; }
                _position++; return TokenKind.Assign;

            case '!':
                if (c1 == '=') { _position += 2; return TokenKind.NotEqual; }
                _position++; return TokenKind.Not;

            case '~':
                if (c1 == '=') { _position += 2; return TokenKind.EquivalentTo; }
                if (c1 == '!') { _position += 2; return TokenKind.NotEquivalentTo; }
                _position++; return TokenKind.Tilde;

            case '<':
                if (c1 == '<' && c2 == '=') { _position += 3; return TokenKind.LeftShiftAssign; }
                if (c1 == '=' && c2 == '>') { _position += 3; return TokenKind.Spaceship; }
                if (c1 == '<') { _position += 2; return TokenKind.LeftShift; }
                if (c1 == '=') { _position += 2; return TokenKind.LessEqual; }
                if (c1 == '>') { _position += 2; return TokenKind.NotEqual; }   // DM spells != as <> too
                _position++; return TokenKind.Less;

            case '>':
                if (c1 == '>' && c2 == '=') { _position += 3; return TokenKind.RightShiftAssign; }
                if (c1 == '>') { _position += 2; return TokenKind.RightShift; }
                if (c1 == '=') { _position += 2; return TokenKind.GreaterEqual; }
                _position++; return TokenKind.Greater;

            case '&':
                if (c1 == '&' && c2 == '=') { _position += 3; return TokenKind.AndAndAssign; }
                if (c1 == '&') { _position += 2; return TokenKind.AndAnd; }
                if (c1 == '=') { _position += 2; return TokenKind.AndAssign; }
                _position++; return TokenKind.Amp;

            case '|':
                if (c1 == '|' && c2 == '=') { _position += 3; return TokenKind.OrOrAssign; }
                if (c1 == '|') { _position += 2; return TokenKind.OrOr; }
                if (c1 == '=') { _position += 2; return TokenKind.OrAssign; }
                _position++; return TokenKind.Pipe;

            case '^':
                if (c1 == '=') { _position += 2; return TokenKind.XorAssign; }
                _position++; return TokenKind.Caret;

            default:
                return TokenKind.Unknown;
        }
    }
}
