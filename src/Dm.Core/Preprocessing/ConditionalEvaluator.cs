using System;
using System.Collections.Generic;
using System.Globalization;
using Dm.Core.Diagnostics;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Preprocessing;

/// <summary>
/// Evaluates the expression of an <c>#if</c> or <c>#elif</c>.
/// </summary>
/// <remarks>
/// <para>
/// DM's <c>#if</c> grammar is much narrower than C's, established by compiling each construct and
/// reading which branch the compiler took:
/// </para>
/// <list type="bullet">
/// <item><description>Accepted: number literals including floats and unary minus, defined macro
/// names, <c>defined(X)</c>, <c>!</c>, <c>+ - * /</c>, <c>&lt; &lt;= &gt; &gt;= == !=</c>,
/// <c>&amp;&amp; ||</c>, parentheses.</description></item>
/// <item><description>Rejected: <c>%</c>, <c>&lt;&lt;</c>, <c>&gt;&gt;</c>, <c>&amp;</c>,
/// <c>|</c>, and string literals.</description></item>
/// </list>
/// <para>
/// <b>An undefined identifier is an error, not zero.</b> This is the opposite of C, where undefined
/// names silently become 0, and it is why real DM guards with <c>#ifdef</c> rather than a bare
/// <c>#if NAME</c>. We report it and recover as 0 so analysis continues.
/// </para>
/// <para>
/// <c>defined</c> requires its parentheses — <c>defined FIVE</c> fails with "expected (".
/// </para>
/// </remarks>
internal sealed class ConditionalEvaluator
{
    private const int MaxExpansionDepth = 64;

    private readonly MacroTable _macros;
    private readonly List<Diagnostic> _diagnostics;
    private readonly List<(SourceText Text, Token Token)> _input = new();

    private int _position;

    private ConditionalEvaluator(MacroTable macros, List<Diagnostic> diagnostics)
    {
        _macros = macros;
        _diagnostics = diagnostics;
    }

    /// <summary>Evaluates a condition, returning true when the branch should be taken.</summary>
    public static bool Evaluate(
        LexResult lex,
        Directive directive,
        MacroTable macros,
        List<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(lex);
        ArgumentNullException.ThrowIfNull(macros);
        ArgumentNullException.ThrowIfNull(diagnostics);

        ConditionalEvaluator evaluator = new(macros, diagnostics);

        for (int i = directive.ArgumentStart; i < directive.ArgumentEnd; i++)
        {
            if (lex.Tokens[i].Kind != TokenKind.Comment)
                evaluator._input.Add((lex.Text, lex.Tokens[i]));
        }

        if (evaluator._input.Count == 0)
        {
            diagnostics.Add(Diagnostic.Error("DM0120", directive.Span, $"#{directive.Name} requires a condition"));
            return false;
        }

        double value = evaluator.ParseOr(depth: 0);

        if (!evaluator.AtEnd)
        {
            diagnostics.Add(Diagnostic.Error(
                "DM0121",
                evaluator.CurrentSpan,
                $"unexpected token in #{directive.Name} condition"));
        }

        return value != 0;
    }

    private bool AtEnd => _position >= _input.Count;

    private TokenKind CurrentKind => AtEnd ? TokenKind.EndOfFile : _input[_position].Token.Kind;

    private TextSpan CurrentSpan =>
        AtEnd
            ? (_input.Count > 0 ? _input[^1].Token.Span : new TextSpan(0, 0))
            : _input[_position].Token.Span;

    private string CurrentText =>
        AtEnd ? string.Empty : _input[_position].Text.ToString(_input[_position].Token.Span);

    // -- grammar, loosest binding first ------------------------------------

    private double ParseOr(int depth)
    {
        double left = ParseAnd(depth);

        while (CurrentKind == TokenKind.OrOr)
        {
            _position++;
            double right = ParseAnd(depth);
            left = (left != 0 || right != 0) ? 1 : 0;
        }

        return left;
    }

    private double ParseAnd(int depth)
    {
        double left = ParseEquality(depth);

        while (CurrentKind == TokenKind.AndAnd)
        {
            _position++;
            double right = ParseEquality(depth);
            left = (left != 0 && right != 0) ? 1 : 0;
        }

        return left;
    }

    private double ParseEquality(int depth)
    {
        double left = ParseRelational(depth);

        while (CurrentKind is TokenKind.Equal or TokenKind.NotEqual)
        {
            TokenKind op = CurrentKind;
            _position++;
            double right = ParseRelational(depth);
            left = op == TokenKind.Equal ? (left == right ? 1 : 0) : (left != right ? 1 : 0);
        }

        return left;
    }

    private double ParseRelational(int depth)
    {
        double left = ParseAdditive(depth);

        while (CurrentKind is TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual)
        {
            TokenKind op = CurrentKind;
            _position++;
            double right = ParseAdditive(depth);

            left = op switch
            {
                TokenKind.Less => left < right ? 1 : 0,
                TokenKind.LessEqual => left <= right ? 1 : 0,
                TokenKind.Greater => left > right ? 1 : 0,
                _ => left >= right ? 1 : 0,
            };
        }

        return left;
    }

    private double ParseAdditive(int depth)
    {
        double left = ParseMultiplicative(depth);

        while (CurrentKind is TokenKind.Plus or TokenKind.Minus)
        {
            TokenKind op = CurrentKind;
            _position++;
            double right = ParseMultiplicative(depth);
            left = op == TokenKind.Plus ? left + right : left - right;
        }

        return left;
    }

    private double ParseMultiplicative(int depth)
    {
        double left = ParseUnary(depth);

        while (CurrentKind is TokenKind.Star or TokenKind.Slash)
        {
            TokenKind op = CurrentKind;
            TextSpan span = CurrentSpan;
            _position++;
            double right = ParseUnary(depth);

            if (op == TokenKind.Star)
            {
                left *= right;
            }
            else if (right == 0)
            {
                _diagnostics.Add(Diagnostic.Error("DM0122", span, "division by zero in a preprocessor condition"));
                left = 0;
            }
            else
            {
                left /= right;
            }
        }

        return left;
    }

    private double ParseUnary(int depth)
    {
        switch (CurrentKind)
        {
            case TokenKind.Not:
                _position++;
                return ParseUnary(depth) == 0 ? 1 : 0;

            case TokenKind.Minus:
                _position++;
                return -ParseUnary(depth);

            case TokenKind.Plus:
                _position++;
                return ParseUnary(depth);

            default:
                return ParsePrimary(depth);
        }
    }

    private double ParsePrimary(int depth)
    {
        if (AtEnd)
        {
            _diagnostics.Add(Diagnostic.Error("DM0121", CurrentSpan, "unexpected end of preprocessor condition"));
            return 0;
        }

        if (CurrentKind == TokenKind.OpenParen)
        {
            _position++;
            double value = ParseOr(depth);

            if (CurrentKind == TokenKind.CloseParen)
                _position++;
            else
                _diagnostics.Add(Diagnostic.Error("DM0123", CurrentSpan, "expected ')'"));

            return value;
        }

        if (CurrentKind == TokenKind.Number)
        {
            string text = CurrentText;
            TextSpan span = CurrentSpan;
            _position++;

            return TryParseNumber(text, out double value)
                ? value
                : Fail(span, $"'{text}' is not a valid number in a preprocessor condition");
        }

        if (IsNameLike(CurrentKind))
        {
            string name = CurrentText;
            TextSpan span = CurrentSpan;
            _position++;

            if (name == "defined")
                return ParseDefined(span);

            if (_macros.TryGet(name, out MacroDefinition macro))
                return ExpandAndEvaluate(macro, span, depth);

            // Unlike C, DM rejects a bare undefined name here rather than treating it as 0.
            return Fail(span, $"'{name}' is not defined; #if rejects undefined names, use #ifdef");
        }

        {
            TextSpan span = CurrentSpan;
            string text = CurrentText;
            _position++;
            return Fail(span, $"'{text}' is not allowed in a preprocessor condition");
        }
    }

    /// <summary>
    /// <c>defined(NAME)</c>. The parentheses are required — <c>defined FIVE</c> is rejected by the
    /// compiler with "expected (".
    /// </summary>
    private double ParseDefined(TextSpan definedSpan)
    {
        if (CurrentKind != TokenKind.OpenParen)
            return Fail(definedSpan, "expected '(' after 'defined'");

        _position++;

        if (!IsNameLike(CurrentKind))
            return Fail(CurrentSpan, "expected a macro name inside defined()");

        string name = CurrentText;
        _position++;

        if (CurrentKind == TokenKind.CloseParen)
            _position++;
        else
            _diagnostics.Add(Diagnostic.Error("DM0123", CurrentSpan, "expected ')'"));

        return _macros.IsDefined(name) ? 1 : 0;
    }

    /// <summary>
    /// Substitutes a macro's body in place and continues evaluating.
    /// </summary>
    /// <remarks>
    /// Only object-like macros are handled. A function-like macro used without an argument list is
    /// not expanded at all — verified against dm.exe, where a bare function-like name reports
    /// "undefined var".
    /// </remarks>
    private double ExpandAndEvaluate(MacroDefinition macro, TextSpan useSpan, int depth)
    {
        if (depth >= MaxExpansionDepth)
            return Fail(useSpan, $"macro expansion of '{macro.Name}' is too deep; it is probably recursive");

        if (macro.IsFunctionLike)
            return Fail(useSpan, $"'{macro.Name}' is a function-like macro and needs an argument list");

        if (macro.Body.Count == 0)
            return Fail(useSpan, $"'{macro.Name}' expands to nothing, which is not a valid condition");

        List<(SourceText, Token)> replacement = new();
        foreach (Token token in macro.Body)
            replacement.Add((macro.Source, token));

        _input.InsertRange(_position, replacement);

        return ParseOr(depth + 1);
    }

    private double Fail(TextSpan span, string message)
    {
        _diagnostics.Add(Diagnostic.Error("DM0121", span, message));
        return 0;
    }

    private static bool TryParseNumber(string text, out double value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex))
            {
                value = hex;
                return true;
            }

            value = 0;
            return false;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsNameLike(TokenKind kind)
        => kind == TokenKind.Identifier || (kind >= TokenKind.KeywordVar && kind <= TokenKind.KeywordGlobal);
}
