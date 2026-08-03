using System;
using System.Collections.Generic;
using Dm.Core.Diagnostics;
using Dm.Core.Text;

namespace Dm.Core.Syntax;

/// <summary>
/// Parses one DM expression, using the precedence table in PLAN.md §4c.
/// </summary>
/// <remarks>
/// <para>
/// The table is taken from the DM Reference's <c>/operator</c> index. It is the one thing here that
/// cannot be derived by testing, so it is treated as the spec rather than re-derived. Three parts of
/// it are easy to get wrong with C instincts:
/// </para>
/// <list type="bullet">
/// <item><description><c>in</c> is the <b>lowest</b> precedence operator in the language, below
/// assignment. <c>has = 2 in L</c> parses as <c>(has = 2) in L</c> — compiler-verified, PLAN.md §8.
/// </description></item>
/// <item><description>Unary <c>*</c> and <c>&amp;</c> are pointer operators binding at level 4,
/// while binary <c>*</c> and <c>&amp;</c> sit at levels 6 and 11.</description></item>
/// <item><description><c>~=</c> is an equivalence test at level 10, not a compound assignment,
/// despite ending in <c>=</c>.</description></item>
/// </list>
/// <para>
/// The parser never throws and always returns a node: an unparseable operand becomes an
/// <see cref="ErrorExpressionSyntax"/> plus a diagnostic, because an editor buffer is malformed on
/// every keystroke.
/// </para>
/// </remarks>
public sealed class ExpressionParser
{
    private readonly IReadOnlyList<Token> _tokens;
    private readonly TokenSource _source;
    private readonly List<Diagnostic> _diagnostics;

    private int _position;

    /// <summary>Nesting depth of (), [] and {}, inside which a newline does not end the expression.</summary>
    private int _groupDepth;

    /// <summary>Depth of ternary true-branches currently being parsed. See <see cref="IsTernaryColon"/>.</summary>
    private int _ternaryDepth;

    /// <summary>
    /// When set, a <c>:</c> ends the expression instead of starting a member access.
    /// </summary>
    /// <remarks>
    /// Needed for the <c>case 1:</c> label of a <c>#pragma syntax C switch</c>, where the colon
    /// belongs to the label. Without the pragma that same text is a member access, which is why
    /// <c>case 1:</c> fails with "expected var or proc name after : operator" in the default
    /// grammar — the compiler reads <c>case</c> as a name.
    /// </remarks>
    private bool _colonTerminates;

    private ExpressionParser(
        IReadOnlyList<Token> tokens,
        TokenSource source,
        List<Diagnostic> diagnostics,
        int position,
        bool colonTerminates)
    {
        _tokens = tokens;
        _source = source;
        _diagnostics = diagnostics;
        _position = position;
        _colonTerminates = colonTerminates;
    }

    /// <summary>Parses one expression starting at <paramref name="position"/>.</summary>
    /// <returns>The expression, and the position of the first token after it.</returns>
    public static (ExpressionSyntax Expression, int Position) Parse(
        IReadOnlyList<Token> tokens,
        TokenSource source,
        List<Diagnostic> diagnostics,
        int position,
        bool colonTerminates = false)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(diagnostics);

        ExpressionParser parser = new(tokens, source, diagnostics, position, colonTerminates);
        ExpressionSyntax expression = parser.ParseExpression();
        return (expression, parser._position);
    }

    // -- token access ------------------------------------------------------

    private TokenKind Current => _position < _tokens.Count ? _tokens[_position].Kind : TokenKind.EndOfFile;

    private TokenKind Peek(int offset = 1)
        => _position + offset < _tokens.Count ? _tokens[_position + offset].Kind : TokenKind.EndOfFile;

    private bool AtEnd => _position >= _tokens.Count || Current == TokenKind.EndOfFile;

    private TextSpan CurrentSpan
        => _position < _tokens.Count ? _tokens[_position].Span : new TextSpan(_source.Text.Length, 0);

    private string TextOf(int index) => _source.TextOf(index);

    private TextSpan SpanFrom(int startToken)
    {
        if (startToken >= _tokens.Count)
            return CurrentSpan;

        int endToken = Math.Min(Math.Max(startToken, _position - 1), _tokens.Count - 1);
        return TextSpan.FromBounds(_tokens[startToken].Span.Start, _tokens[endToken].Span.End);
    }

    /// <summary>
    /// Inside brackets a line break is layout, not a terminator, so argument lists may wrap.
    /// </summary>
    private void SkipLayoutInGroup()
    {
        if (_groupDepth == 0)
            return;

        while (Current is TokenKind.Newline or TokenKind.Indent or TokenKind.Dedent)
            _position++;
    }

    private void Report(TextSpan span, string message)
        => _diagnostics.Add(Diagnostic.Error("DM0201", span, message));

    /// <summary>
    /// True when the <c>:</c> at the cursor closes a conditional rather than starting a member
    /// access.
    /// </summary>
    /// <remarks>
    /// Compiled against dm.exe 516.1666, with a valid member <c>c</c> in scope. Inside a
    /// conditional the colon closes it in every case except one: written tight against a bare
    /// identifier, member access wins and the conditional is then left without a separator.
    /// <code>
    /// 1 ? b : c      conditional      1 ? b:c        error, "expected ':'"
    /// 1 ? b :c       conditional      1 ? b: c       error, "expected ':'"
    /// 1 ? "0":"1"    conditional      1 ? f():g()    conditional
    /// 1 ? L[1]:z     conditional      1 ? 1:2        conditional
    /// </code>
    /// So both halves matter: whitespace before the colon, and whether what precedes it is a name
    /// that could take a member. This is the only place in DM where spacing changes a parse.
    /// </remarks>
    private bool IsTernaryColon()
    {
        if (_ternaryDepth == 0 || Current != TokenKind.Colon)
            return false;

        if (_source.HasWhitespaceBefore(_position))
            return true;

        // Tight against a bare identifier is the one shape where member access wins.
        TokenKind previous = _position > 0 ? _tokens[_position - 1].Kind : TokenKind.EndOfFile;
        return !IsNameLike(previous);
    }

    // -- precedence --------------------------------------------------------

    /// <summary>
    /// Binding power of a binary operator; higher binds tighter. Levels mirror PLAN.md §4c, inverted
    /// so that the table's level 5 (tightest binary) is the largest number here.
    /// </summary>
    private static int BinaryPrecedence(TokenKind kind) => kind switch
    {
        TokenKind.StarStar => 14,

        TokenKind.Star or TokenKind.Slash or TokenKind.Percent or TokenKind.PercentPercent => 13,

        TokenKind.Plus or TokenKind.Minus => 12,

        TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual
            or TokenKind.Spaceship => 11,

        TokenKind.LeftShift or TokenKind.RightShift => 10,

        TokenKind.Equal or TokenKind.NotEqual or TokenKind.EquivalentTo or TokenKind.NotEquivalentTo => 9,

        TokenKind.Amp => 8,
        TokenKind.Caret => 7,
        TokenKind.Pipe => 6,
        TokenKind.AndAnd => 5,
        TokenKind.OrOr => 4,

        _ => 0,
    };

    private static bool IsAssignmentOperator(TokenKind kind) => kind
        is TokenKind.Assign
        or TokenKind.PlusAssign
        or TokenKind.MinusAssign
        or TokenKind.StarAssign
        or TokenKind.SlashAssign
        or TokenKind.PercentAssign
        or TokenKind.PercentPercentAssign
        or TokenKind.StarStarAssign
        or TokenKind.AndAssign
        or TokenKind.OrAssign
        or TokenKind.XorAssign
        or TokenKind.LeftShiftAssign
        or TokenKind.RightShiftAssign
        or TokenKind.AndAndAssign
        or TokenKind.OrOrAssign
        or TokenKind.ColonAssign;

    /// <summary>Keywords that are legal as a plain name, since DM has very few reserved words.</summary>
    private static bool IsNameLike(TokenKind kind) => kind
        is TokenKind.Identifier
        or TokenKind.KeywordSrc
        or TokenKind.KeywordUsr
        or TokenKind.KeywordWorld
        or TokenKind.KeywordGlobal
        or TokenKind.KeywordVar
        or TokenKind.KeywordSet
        or TokenKind.KeywordTo
        or TokenKind.KeywordStep
        or TokenKind.KeywordIn
        or TokenKind.KeywordAs;

    // -- grammar -----------------------------------------------------------

    private ExpressionSyntax ParseExpression() => ParseIn();

    /// <summary>
    /// <c>in</c> sits below assignment, so it binds last of everything. This is the level that makes
    /// <c>if(!A in L)</c> mean <c>if((!A) in L)</c>.
    /// </summary>
    private ExpressionSyntax ParseIn()
    {
        int start = _position;
        ExpressionSyntax left = ParseAssignment();

        while (Current == TokenKind.KeywordIn)
        {
            _position++;
            SkipLayoutInGroup();
            ExpressionSyntax right = ParseAssignment();
            left = new BinaryExpressionSyntax(left, TokenKind.KeywordIn, right, SpanFrom(start));
        }

        return left;
    }

    /// <summary>Assignment is the one right-associative level, so <c>a = b = c</c> is <c>a = (b = c)</c>.</summary>
    private ExpressionSyntax ParseAssignment()
    {
        int start = _position;
        ExpressionSyntax left = ParseConditional();

        if (!IsAssignmentOperator(Current))
            return left;

        TokenKind op = Current;
        _position++;
        SkipLayoutInGroup();

        ExpressionSyntax value = ParseAssignment();
        return new AssignmentExpressionSyntax(left, op, value, SpanFrom(start));
    }

    private ExpressionSyntax ParseConditional()
    {
        int start = _position;
        ExpressionSyntax condition = ParseBinary(1);

        if (Current != TokenKind.Question)
            return condition;

        _position++;
        SkipLayoutInGroup();

        _ternaryDepth++;
        ExpressionSyntax whenTrue = ParseAssignment();
        _ternaryDepth--;

        SkipLayoutInGroup();

        if (Current == TokenKind.Colon)
        {
            _position++;
            SkipLayoutInGroup();
        }
        else
        {
            Report(CurrentSpan, "expected ':' to complete the conditional");
        }

        ExpressionSyntax whenFalse = ParseAssignment();
        return new ConditionalExpressionSyntax(condition, whenTrue, whenFalse, SpanFrom(start));
    }

    private ExpressionSyntax ParseBinary(int minPrecedence)
    {
        int start = _position;
        ExpressionSyntax left = ParseUnary();

        while (true)
        {
            int precedence = BinaryPrecedence(Current);

            if (precedence == 0 || precedence < minPrecedence)
                break;

            TokenKind op = Current;
            _position++;
            SkipLayoutInGroup();

            // Every binary level is left-associative, so the right operand stops one level tighter.
            ExpressionSyntax right = ParseBinary(precedence + 1);
            left = new BinaryExpressionSyntax(left, op, right, SpanFrom(start));
        }

        return left;
    }

    private ExpressionSyntax ParseUnary()
    {
        int start = _position;

        UnaryOperatorKind? kind = Current switch
        {
            TokenKind.Not => UnaryOperatorKind.Not,
            TokenKind.Minus => UnaryOperatorKind.Negate,
            TokenKind.Tilde => UnaryOperatorKind.BitwiseNot,
            TokenKind.PlusPlus => UnaryOperatorKind.PreIncrement,
            TokenKind.MinusMinus => UnaryOperatorKind.PreDecrement,

            // Pointer operators, 515+. Unary they bind at level 4; binary the same glyphs are at 6
            // and 11, which is why they cannot share a precedence entry.
            TokenKind.Star => UnaryOperatorKind.Dereference,
            TokenKind.Amp => UnaryOperatorKind.AddressOf,

            _ => null,
        };

        if (kind is null)
            return ParsePostfix(ParsePrimary());

        _position++;
        SkipLayoutInGroup();

        ExpressionSyntax operand = ParseUnary();
        return new UnaryExpressionSyntax(kind.Value, operand, SpanFrom(start));
    }

    private ExpressionSyntax ParsePostfix(ExpressionSyntax expression)
    {
        int start = _position;

        while (true)
        {
            switch (Current)
            {
                case TokenKind.OpenParen:
                    expression = new InvocationExpressionSyntax(
                        expression,
                        ParseArgumentList(TokenKind.OpenParen, TokenKind.CloseParen),
                        SpanFrom(start));
                    break;

                case TokenKind.OpenBracket:
                case TokenKind.QuestionOpenBracket:
                    expression = ParseIndex(expression, start);
                    break;

                // A trailing `.` before the end of an interpolation hole collapses, the same way a
                // trailing path separator does. `world << "chasing [who.]"` compiles with 0 errors
                // on dm.exe 516.1666, and is in shipped game code.
                case TokenKind.Dot when Peek() == TokenKind.InterpolationEnd:
                    _position++;
                    break;

                case TokenKind.Dot:
                case TokenKind.QuestionDot:
                case TokenKind.QuestionColon:
                case TokenKind.ColonColon:
                    expression = ParseMemberAccess(expression, start);
                    break;

                // A tight `b:c` is member access even inside a conditional; only a colon with
                // whitespace before it closes the conditional.
                case TokenKind.Colon when !_colonTerminates && !IsTernaryColon():
                    expression = ParseMemberAccess(expression, start);
                    break;

                // `path{a = 1; b = 2}` — a modified-type initialiser. Braces are mandatory in this
                // position even though they are optional elsewhere in DM.
                case TokenKind.OpenBrace:
                    expression = ParseModifiedType(expression, start);
                    break;

                case TokenKind.PlusPlus:
                    _position++;
                    expression = new UnaryExpressionSyntax(
                        UnaryOperatorKind.PostIncrement, expression, SpanFrom(start));
                    break;

                case TokenKind.MinusMinus:
                    _position++;
                    expression = new UnaryExpressionSyntax(
                        UnaryOperatorKind.PostDecrement, expression, SpanFrom(start));
                    break;

                // `input(...) as text|null`. The clause belongs to the call it follows.
                case TokenKind.KeywordAs:
                    expression = ParseAsClause(expression, start);
                    break;

                default:
                    return expression;
            }
        }
    }

    private ExpressionSyntax ParseIndex(ExpressionSyntax target, int start)
    {
        bool nullConditional = Current == TokenKind.QuestionOpenBracket;
        _position++;
        _groupDepth++;
        SkipLayoutInGroup();

        ExpressionSyntax? index = Current == TokenKind.CloseBracket ? null : ParseExpression();

        SkipLayoutInGroup();
        _groupDepth--;

        if (Current == TokenKind.CloseBracket)
            _position++;
        else
            Report(CurrentSpan, "expected ']'");

        return new IndexExpressionSyntax(target, index, nullConditional, SpanFrom(start));
    }

    private ExpressionSyntax ParseMemberAccess(ExpressionSyntax? target, int start)
    {
        MemberAccessKind kind = Current switch
        {
            TokenKind.Dot => MemberAccessKind.Dot,
            TokenKind.Colon => MemberAccessKind.Colon,
            TokenKind.QuestionDot => MemberAccessKind.NullDot,
            TokenKind.QuestionColon => MemberAccessKind.NullColon,
            _ => MemberAccessKind.Scope,
        };

        _position++;

        if (!IsNameLike(Current))
        {
            Report(CurrentSpan, "expected a member name");
            return new MemberAccessExpressionSyntax(target, kind, string.Empty, CurrentSpan, false, SpanFrom(start));
        }

        string name = TextOf(_position);
        TextSpan nameSpan = CurrentSpan;
        _position++;

        // `A::B()` and `::A()` name a proc rather than calling it, so the empty parentheses belong to
        // the reference. Letting the postfix loop see them would build an invocation instead.
        bool isProcReference = false;
        if (kind == MemberAccessKind.Scope && Current == TokenKind.OpenParen && Peek() == TokenKind.CloseParen)
        {
            isProcReference = true;
            _position += 2;
        }

        return new MemberAccessExpressionSyntax(target, kind, name, nameSpan, isProcReference, SpanFrom(start));
    }

    private ExpressionSyntax ParseModifiedType(ExpressionSyntax type, int start)
    {
        _position++;
        _groupDepth++;

        List<ExpressionSyntax> assignments = new();

        while (!AtEnd && Current != TokenKind.CloseBrace)
        {
            SkipLayoutInGroup();

            if (Current is TokenKind.Semicolon or TokenKind.Comma)
            {
                _position++;
                continue;
            }

            if (Current == TokenKind.CloseBrace)
                break;

            int before = _position;
            assignments.Add(ParseExpression());

            if (_position == before)
                _position++;
        }

        _groupDepth--;

        if (Current == TokenKind.CloseBrace)
            _position++;
        else
            Report(CurrentSpan, "expected '}'");

        return new ModifiedTypeExpressionSyntax(type, assignments, SpanFrom(start));
    }

    /// <summary>Consumes an <c>as</c> clause: one or more input types separated by <c>|</c>.</summary>
    private ExpressionSyntax ParseAsClause(ExpressionSyntax expression, int start)
    {
        _position++;

        List<string> inputTypes = new();

        while (IsNameLike(Current) || Current == TokenKind.KeywordNull)
        {
            inputTypes.Add(TextOf(_position));
            _position++;

            if (Current != TokenKind.Pipe)
                break;

            _position++;
        }

        if (inputTypes.Count == 0)
            Report(CurrentSpan, "expected an input type after 'as'");

        return new AsExpressionSyntax(expression, inputTypes, SpanFrom(start));
    }

    private List<ArgumentSyntax> ParseArgumentList(TokenKind opener, TokenKind closer)
    {
        List<ArgumentSyntax> arguments = new();

        if (Current != opener)
            return arguments;

        _position++;
        _groupDepth++;
        SkipLayoutInGroup();

        while (!AtEnd && Current != closer)
        {
            if (Current == TokenKind.Comma)
            {
                _position++;
                SkipLayoutInGroup();
                continue;
            }

            int before = _position;
            int argumentStart = _position;
            ExpressionSyntax value = ParseExpression();

            // `list(a = 1, b = 2)` builds an associative list, so the left side is a key rather than
            // a parameter name. It arrives here as an assignment and is split back apart.
            ArgumentSyntax argument = value is AssignmentExpressionSyntax { OperatorToken: TokenKind.Assign } assignment
                ? new ArgumentSyntax(assignment.Target, assignment.Value, SpanFrom(argumentStart))
                : new ArgumentSyntax(null, value, SpanFrom(argumentStart));

            arguments.Add(argument);
            SkipLayoutInGroup();

            if (_position == before)
                _position++;
        }

        _groupDepth--;

        if (Current == closer)
            _position++;
        else
            Report(CurrentSpan, closer == TokenKind.CloseParen ? "expected ')'" : "expected ']'");

        return arguments;
    }

    private ExpressionSyntax ParsePrimary()
    {
        int start = _position;

        switch (Current)
        {
            case TokenKind.Number:
            {
                string text = TextOf(_position);
                TextSpan span = CurrentSpan;
                _position++;
                return new LiteralExpressionSyntax(LiteralKind.Number, text, span);
            }

            case TokenKind.Resource:
            {
                string text = TextOf(_position);
                TextSpan span = CurrentSpan;
                _position++;
                return new LiteralExpressionSyntax(LiteralKind.Resource, text, span);
            }

            case TokenKind.KeywordNull:
            {
                TextSpan span = CurrentSpan;
                _position++;
                return new LiteralExpressionSyntax(LiteralKind.Null, "null", span);
            }

            case TokenKind.StringStart:
                return ParseString();

            case TokenKind.OpenParen:
            {
                _position++;
                _groupDepth++;
                SkipLayoutInGroup();

                ExpressionSyntax inner = ParseExpression();

                SkipLayoutInGroup();
                _groupDepth--;

                if (Current == TokenKind.CloseParen)
                    _position++;
                else
                    Report(CurrentSpan, "expected ')'");

                return inner;
            }

            case TokenKind.KeywordNew:
                return ParseNew();

            // A leading separator makes this a path. Without one it is member access on a value, so
            // it never reaches here as a path — PLAN.md §4a.
            case TokenKind.Slash:
                return new PathExpressionSyntax(ParsePath(PathAnchor.Absolute));

            case TokenKind.Dot when IsNameLike(Peek()):
                return new PathExpressionSyntax(ParsePath(PathAnchor.UpwardSearch));

            // A bare `.` is the enclosing proc's implicit return value.
            case TokenKind.Dot:
            {
                TextSpan span = CurrentSpan;
                _position++;
                return new ReturnValueExpressionSyntax(span);
            }

            case TokenKind.DotDot:
            {
                _position++;
                List<ArgumentSyntax> arguments = ParseArgumentList(TokenKind.OpenParen, TokenKind.CloseParen);
                return new ParentCallExpressionSyntax(arguments, SpanFrom(start));
            }

            // The leading scope forms, `::A` and `::A()`.
            case TokenKind.ColonColon:
                return ParseMemberAccess(null, start);

            case TokenKind.Identifier:
            case TokenKind.KeywordSrc:
            case TokenKind.KeywordUsr:
            case TokenKind.KeywordWorld:
            case TokenKind.KeywordGlobal:

            // The contextual keywords are ordinary names in operand position, and `step` is also a
            // BYOND builtin proc, so `step(src, dir)` is a call rather than a loop clause. They only
            // reach here when an operand was expected, since `a in b` and `1 to 10 step 2` consume
            // them as operators before this point.
            case TokenKind.KeywordStep:
            case TokenKind.KeywordTo:
            case TokenKind.KeywordIn:
            case TokenKind.KeywordSet:
            case TokenKind.KeywordAs:
            {
                string name = TextOf(_position);
                TextSpan span = CurrentSpan;
                _position++;
                return new IdentifierExpressionSyntax(name, span);
            }

            default:
            {
                Report(CurrentSpan, "expected an expression");
                return new ErrorExpressionSyntax(CurrentSpan);
            }
        }
    }

    private ExpressionSyntax ParseNew()
    {
        int start = _position;
        _position++;

        ExpressionSyntax? type = null;

        // `new(loc)` gives no type; the target's declared type supplies it. `new /obj/thing(...)`
        // and `new/generator(...)` both name one, with or without a space.
        if (Current == TokenKind.Slash)
            type = new PathExpressionSyntax(ParsePath(PathAnchor.Absolute));
        else if (IsNameLike(Current))
            type = ParsePostfix(ParsePrimary());

        if (Current == TokenKind.OpenBrace)
            type = ParseModifiedType(type ?? new ErrorExpressionSyntax(CurrentSpan), start);

        List<ArgumentSyntax> arguments = ParseArgumentList(TokenKind.OpenParen, TokenKind.CloseParen);
        return new NewExpressionSyntax(type, arguments, SpanFrom(start));
    }

    /// <summary>
    /// Reads a path. Mid-path <c>/</c> and <c>.</c> are the same separator, so both are consumed
    /// without recording which was written — PLAN.md §4a.
    /// </summary>
    private PathSyntax ParsePath(PathAnchor anchor)
    {
        int start = _position;
        _position++;

        List<string> segments = new();
        List<TextSpan> segmentSpans = new();

        while (true)
        {
            if (!IsNameLike(Current))
                break;

            segments.Add(TextOf(_position));
            segmentSpans.Add(CurrentSpan);
            _position++;

            if (Current is not (TokenKind.Slash or TokenKind.Dot))
                break;

            // Doubled and trailing separators collapse, so `/obj/item/` means `/obj/item` — see
            // PLAN.md §4a. The trailing one must still be consumed: left behind, `istype(a, /mob/)`
            // reads it as division and then fails looking for a right operand.
            if (!IsNameLike(Peek()))
            {
                _position++;
                break;
            }

            _position++;
        }

        return new PathSyntax(anchor, segments, SpanFrom(start), segmentSpans);
    }

    private ExpressionSyntax ParseString()
    {
        int start = _position;
        _position++;

        List<InterpolatedStringPartSyntax> parts = new();
        bool hasHole = false;

        while (!AtEnd && Current != TokenKind.StringEnd)
        {
            if (Current == TokenKind.StringText)
            {
                parts.Add(new InterpolatedStringPartSyntax(TextOf(_position), null, CurrentSpan));
                _position++;
                continue;
            }

            if (Current == TokenKind.InterpolationStart)
            {
                hasHole = true;
                int holeStart = _position;
                _position++;

                // An empty hole is legal and positional: `text("<b>[][]</b>", a, b)` takes its
                // values from the arguments.
                if (Current == TokenKind.InterpolationEnd)
                {
                    _position++;
                    parts.Add(new InterpolatedStringPartSyntax(null, null, SpanFrom(holeStart)));
                    continue;
                }

                // A hole is ordinary expression context, which is what `"[src.name] hit"` needs.
                _groupDepth++;
                ExpressionSyntax inner = ParseExpression();
                _groupDepth--;

                if (Current == TokenKind.InterpolationEnd)
                    _position++;
                else
                    Report(CurrentSpan, "expected ']' to close the interpolation");

                parts.Add(new InterpolatedStringPartSyntax(null, inner, SpanFrom(holeStart)));
                continue;
            }

            // Anything else inside a string means the lexer already reported it; do not loop on it.
            _position++;
        }

        if (Current == TokenKind.StringEnd)
            _position++;

        TextSpan span = SpanFrom(start);

        return hasHole
            ? new InterpolatedStringExpressionSyntax(parts, span)
            : new LiteralExpressionSyntax(LiteralKind.String, _source.Text.ToString(span), span);
    }
}
