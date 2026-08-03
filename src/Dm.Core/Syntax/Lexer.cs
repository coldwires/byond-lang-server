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
/// <see cref="TokenKind.Dedent"/> are emitted for block structure. Indentation is compared by
/// prefix rather than by counting columns, so no tab width has to be assumed — a deeper line must
/// start with the enclosing line's exact whitespace. Blank and comment-only lines never change the
/// level.
/// </para>
/// <para>
/// Interpolated strings emit a flat run: <c>StringStart, StringText, InterpolationStart,
/// …expression…, InterpolationEnd, StringText, StringEnd</c>. Strings may nest inside an
/// interpolation hole, so the state is a stack.
/// </para>
/// </remarks>
public sealed class Lexer
{
    private sealed class StringState
    {
        public bool Multiline;
        public bool InInterpolation;
        public int BracketDepth;
    }

    private readonly SourceText _text;
    private readonly List<Token> _tokens = new();
    private readonly List<Diagnostic> _diagnostics = new();
    private readonly List<string> _indents = new() { string.Empty };
    private readonly Stack<StringState> _strings = new();

    private int _position;
    private bool _atLineStart = true;

    /// <summary>Depth of <c>(</c> and <c>[</c> nesting. Layout tokens are suppressed inside.</summary>
    private int _groupingDepth;

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

        // Blank lines and comment-only lines leave the level alone. Emitting Dedent for a blank
        // line would close blocks that the author is still inside.
        if (AtEnd || IsLineTerminator(Current) || IsCommentStart())
            return;

        string indent = _text.Content.Substring(lineStart, _position - lineStart);
        string current = _indents[^1];

        if (indent == current)
            return;

        if (indent.StartsWith(current, StringComparison.Ordinal))
        {
            _indents.Add(indent);
            _tokens.Add(new Token(TokenKind.Indent, TextSpan.FromBounds(lineStart, _position)));
            return;
        }

        while (_indents.Count > 1 && !indent.StartsWith(_indents[^1], StringComparison.Ordinal))
        {
            _indents.RemoveAt(_indents.Count - 1);
            _tokens.Add(new Token(TokenKind.Dedent, TextSpan.FromBounds(_position, _position)));
        }

        if (_indents[^1] == indent)
            return;

        // Popping did not land on a matching level, so the line sits between two enclosing levels.
        // Usually mixed tabs and spaces. Report it, then open a block at the new indent: after the
        // dedents above, this line does extend whatever level we landed on, so pushing keeps the
        // stack coherent. Overwriting the top entry instead would corrupt the enclosing level —
        // and at the root that would redefine what column 0 means for the rest of the file.
        _diagnostics.Add(Diagnostic.Error(
            "DM0003",
            TextSpan.FromBounds(lineStart, _position),
            "inconsistent indentation: this line does not match any enclosing block"));

        _indents.Add(indent);
        _tokens.Add(new Token(TokenKind.Indent, TextSpan.FromBounds(lineStart, _position)));
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

        if (_groupingDepth == 0)
            Add(TokenKind.Newline, start);

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
            LexStringStart();
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

        if (IsIdentifierStart(c))
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
            while (!AtEnd && !IsLineTerminator(Current))
                _position++;

            Add(TokenKind.Comment, start);
            return;
        }

        // Block comments nest in DM, so a depth counter is required rather than scanning for the
        // first "*/".
        _position += 2;
        int depth = 1;

        while (!AtEnd && depth > 0)
        {
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

    private void LexStringStart()
    {
        int start = _position;
        bool multiline = Current == '{';

        _position += multiline ? 2 : 1;

        Add(TokenKind.StringStart, start);
        _strings.Push(new StringState { Multiline = multiline });
    }

    private void LexResource()
    {
        int start = _position;
        _position++;

        while (!AtEnd && Current != '\'')
        {
            if (Current == '\\' && !AtEndAt(_position + 1))
            {
                _position += 2;
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

    private void LexIdentifier()
    {
        int start = _position;

        while (!AtEnd && IsIdentifierPart(Current))
            _position++;

        string text = _text.Content.Substring(start, _position - start);
        Add(Keywords.Lookup(text), start);
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

            if (c == '\\' && !AtEndAt(_position + 1))
            {
                _position += 2;
                continue;
            }

            if (c == '[')
            {
                FlushStringText(start);
                int bracket = _position;
                _position++;
                Add(TokenKind.InterpolationStart, bracket);

                state.InInterpolation = true;
                state.BracketDepth = 0;
                return;
            }

            if (state.Multiline)
            {
                if (c == '"' && Peek() == '}')
                {
                    FlushStringText(start);
                    int quote = _position;
                    _position += 2;
                    Add(TokenKind.StringEnd, quote);
                    _strings.Pop();
                    return;
                }
            }
            else
            {
                if (c == '"')
                {
                    FlushStringText(start);
                    int quote = _position;
                    _position++;
                    Add(TokenKind.StringEnd, quote);
                    _strings.Pop();
                    return;
                }

                // A single-quoted string cannot span lines. Stop at the terminator so the rest of
                // the file still lexes; the newline itself belongs to the enclosing code.
                if (IsLineTerminator(c))
                {
                    FlushStringText(start);
                    Report("DM0001", start, "unterminated string literal");
                    _strings.Pop();
                    return;
                }
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

    // -- operators ---------------------------------------------------------

    private void LexOperator(int start)
    {
        char c = Current;

        // Bracket depth has to be tracked before the interpolation check, since an interpolation
        // hole may itself contain indexing: "[list[i]]".
        StringState? enclosing = _strings.Count > 0 && _strings.Peek().InInterpolation ? _strings.Peek() : null;

        if (enclosing is not null)
        {
            if (c == '[')
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
