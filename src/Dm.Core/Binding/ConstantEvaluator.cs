using System;
using System.Globalization;
using Dm.Core.Syntax;

namespace Dm.Core.Binding;

/// <summary>
/// Folds an initialiser that DM itself would fold, and renders it the way DM renders it.
/// </summary>
/// <remarks>
/// <para>
/// The point is a reader's question — <c>var/cooldown = 5 * 60</c> holds what, exactly — and the
/// answer has to be the compiler's rather than C#'s, because DM's arithmetic is not C#'s. All of
/// the following were run under 516.1687 and are pinned as runtime checks in
/// <c>ok/constants.dm</c>, so a BYOND release that changes any of them fails a build:
/// </para>
/// <list type="table">
///   <item><term><c>1 / 3</c></term><description>0.333333 — SIX significant digits, not seventeen</description></item>
///   <item><term><c>123456789</c></term><description>1.23457e+08 — six digits again, so a large integer does not round-trip</description></item>
///   <item><term><c>0.1 + 0.2</c></term><description>0.3 — BYOND numbers are 32-bit floats and the rendering hides the epsilon</description></item>
///   <item><term><c>7.5 % 2</c></term><description>1 — <c>%</c> truncates BOTH operands to integers first</description></item>
///   <item><term><c>7.5 %% 2</c></term><description>1.5 — the fractional modulo is a different operator</description></item>
///   <item><term><c>2 ** 3 ** 2</c></term><description>64 — left-associative, unlike most languages</description></item>
///   <item><term><c>-2 ** 2</c></term><description>4 — unary minus binds tighter than <c>**</c></description></item>
/// </list>
/// <para>
/// <b>A bare literal is never folded.</b> Folding <c>123456789</c> would replace the author's own
/// text with <c>1.23457e+08</c> — true to the compiler, and worse than saying nothing, since a
/// reader already has the literal in front of them. Folding earns its place only where it tells
/// them something the source does not.
/// </para>
/// <para>
/// A macro is already gone by the time the parser runs, which is the common case for a name in
/// an initialiser. A DM <c>const</c> var is the other case, and it is resolved through the
/// <see cref="Resolver"/> a caller supplies — the tree walk and the cycle guard live in
/// <see cref="Symbols.ObjectTree.ConstantValueOf"/>, because they need the finished tree and this
/// runs per file. Probed 2026-08-16 with <c>-warn init_proc</c>, which fires on a <c>/turf</c>
/// var whose initialiser is not a compile-time constant: every const-derived initialiser is
/// silent — a const on the same type, on an ancestor, at root, a const of a const, a string
/// const, and the <c>/path::NAME</c> static form — so dm.exe folds all of them. A non-const var
/// named in a type-level initialiser is <i>"expected a constant expression"</i>, which is why the
/// resolver is asked only for consts.
/// </para>
/// </remarks>
internal static class ConstantEvaluator
{
    /// <summary>
    /// Answers a name in an initialiser: a bare identifier when <paramref name="scope"/> is null,
    /// or <c>NAME</c> in <c>scope::NAME</c> when it is a path. Null when the name is not a
    /// constant the caller can see.
    /// </summary>
    internal delegate Constant? Resolver(string name, PathSyntax? scope);

    /// <summary>A folded value, or nothing when the expression is not a compile-time constant.</summary>
    internal readonly struct Constant
    {
        private Constant(float number, string? text)
        {
            Number = number;
            Text = text;
        }

        /// <summary>The numeric value, when <see cref="Text"/> is null.</summary>
        internal float Number { get; }

        /// <summary>The string value, or null when this is a number.</summary>
        internal string? Text { get; }

        internal static Constant FromNumber(float value) => new(value, null);

        internal static Constant FromString(string value) => new(0, value);

        /// <summary>DM's own rendering: six significant digits, scientific beyond them.</summary>
        internal string Render()
            => Text ?? Number.ToString("G6", CultureInfo.InvariantCulture)
                .Replace("E+0", "e+0", StringComparison.Ordinal)
                .Replace("E-0", "e-0", StringComparison.Ordinal)
                .Replace("E+", "e+", StringComparison.Ordinal)
                .Replace("E-", "e-", StringComparison.Ordinal);
    }

    /// <summary>
    /// The value an initialiser folds to, or null when it is not constant or is already a literal.
    /// </summary>
    internal static string? Fold(ExpressionSyntax? expression) => Fold(expression, null);

    /// <summary>
    /// The value an initialiser folds to with names answered by <paramref name="resolve"/>, or
    /// null when it is not constant or is already a literal.
    /// </summary>
    internal static string? Fold(ExpressionSyntax? expression, Resolver? resolve)
    {
        // A literal is its own best rendering - see the class remarks.
        if (expression is null or LiteralExpressionSyntax)
            return null;

        return Evaluate(expression, resolve, 0) is { } value ? value.Render() : null;
    }

    /// <summary>
    /// Whether the expression names anything at all - the case the per-file fold cannot answer and
    /// the tree's lazy pass exists for. Cheap, so the tree asks it before building a resolver.
    /// </summary>
    internal static bool NamesAnything(ExpressionSyntax? expression)
    {
        return expression switch
        {
            null => false,
            IdentifierExpressionSyntax => true,
            MemberAccessExpressionSyntax { Kind: MemberAccessKind.Scope } => true,
            UnaryExpressionSyntax unary => NamesAnything(unary.Operand),
            BinaryExpressionSyntax binary => NamesAnything(binary.Left) || NamesAnything(binary.Right),
            ConditionalExpressionSyntax conditional => NamesAnything(conditional.Condition)
                || NamesAnything(conditional.WhenTrue) || NamesAnything(conditional.WhenFalse),
            _ => false,
        };
    }

    private static Constant? Evaluate(ExpressionSyntax expression, Resolver? resolve, int depth)
    {
        // Guards a pathological nesting rather than any known construct: an editor buffer is
        // malformed on every keystroke and this runs on it.
        if (depth > 32)
            return null;

        switch (expression)
        {
            case LiteralExpressionSyntax literal:
                return FromLiteral(literal);

            // A bare name: a const var the caller can see, or nothing. Macros never reach here -
            // the preprocessor replaced them before the parser looked.
            case IdentifierExpressionSyntax identifier:
                return resolve?.Invoke(identifier.Name, null);

            // `/turf/probe::TYPE_MAX` - the static form, folded by dm.exe like a bare const.
            case MemberAccessExpressionSyntax
            {
                Kind: MemberAccessKind.Scope, IsProcReference: false,
                Target: PathExpressionSyntax { Path.Anchor: PathAnchor.Absolute } target,
            } scoped:
                return resolve?.Invoke(scoped.Name, target.Path);

            case UnaryExpressionSyntax unary when Evaluate(unary.Operand, resolve, depth + 1) is { } operand:
                return ApplyUnary(unary.Kind, operand);

            case BinaryExpressionSyntax binary
                when Evaluate(binary.Left, resolve, depth + 1) is { } left
                    && Evaluate(binary.Right, resolve, depth + 1) is { } right:
                return ApplyBinary(binary.OperatorToken, left, right);

            case ConditionalExpressionSyntax conditional
                when Evaluate(conditional.Condition, resolve, depth + 1) is { Text: null } test:
                return Evaluate(
                    test.Number != 0 ? conditional.WhenTrue : conditional.WhenFalse, resolve, depth + 1);

            default:
                return null;
        }
    }

    /// <summary>
    /// The unrendered value, for a caller that will feed it back into another fold — a const of a
    /// const has to carry the 32-bit float, not its six-digit rendering, or <c>1/3</c> times 3
    /// stops being 1.
    /// </summary>
    internal static Constant? Value(ExpressionSyntax? expression, Resolver? resolve)
        => expression is null ? null : Evaluate(expression, resolve, 0);

    private static Constant? FromLiteral(LiteralExpressionSyntax literal)
    {
        switch (literal.Kind)
        {
            case LiteralKind.Number:
                // `1#INF` and `1#IND` lex as numbers (§8) and are not foldable arithmetic.
                return float.TryParse(
                    literal.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                    ? Constant.FromNumber(value)
                    : ParseSpecial(literal.Text);

            case LiteralKind.String:
                // The text carries its delimiters; an interpolated string is a different node, so
                // anything here is a plain literal.
                return literal.Text.Length >= 2
                    ? Constant.FromString(literal.Text[1..^1])
                    : null;

            default:
                return null;
        }
    }

    private static Constant? ParseSpecial(string text)
        => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex)
            ? Constant.FromNumber(hex)
            : null;

    private static Constant? ApplyUnary(UnaryOperatorKind kind, Constant operand)
    {
        if (operand.Text is not null)
            return null;

        return kind switch
        {
            UnaryOperatorKind.Negate => Constant.FromNumber(-operand.Number),
            UnaryOperatorKind.Not => Constant.FromNumber(operand.Number == 0 ? 1 : 0),
            UnaryOperatorKind.BitwiseNot => Constant.FromNumber(~(int)operand.Number),
            _ => null,
        };
    }

    private static Constant? ApplyBinary(TokenKind op, Constant left, Constant right)
    {
        // `"a" + "b"` is `ab`, and it is the only string arithmetic there is.
        if (left.Text is not null || right.Text is not null)
        {
            return op == TokenKind.Plus && left.Text is not null && right.Text is not null
                ? Constant.FromString(left.Text + right.Text)
                : null;
        }

        float a = left.Number;
        float b = right.Number;

        return op switch
        {
            TokenKind.Plus => Constant.FromNumber(a + b),
            TokenKind.Minus => Constant.FromNumber(a - b),
            TokenKind.Star => Constant.FromNumber(a * b),
            TokenKind.Slash when b != 0 => Constant.FromNumber(a / b),

            // `%` truncates BOTH operands first - 7.5 % 2 is 1, not 1.5. `%%` is the fractional
            // one. Getting these the wrong way round is a wrong number with no error anywhere.
            TokenKind.Percent when (int)b != 0 => Constant.FromNumber((int)a % (int)b),
            TokenKind.PercentPercent when b != 0 => Constant.FromNumber(a % b),

            // Left-associative in the parser, so `2 ** 3 ** 2` arrives here as (2**3)**2 = 64.
            TokenKind.StarStar => Constant.FromNumber(MathF.Pow(a, b)),

            TokenKind.LeftShift => Constant.FromNumber((int)a << (int)b),
            TokenKind.RightShift => Constant.FromNumber((int)a >> (int)b),
            TokenKind.Amp => Constant.FromNumber((int)a & (int)b),
            TokenKind.Pipe => Constant.FromNumber((int)a | (int)b),
            TokenKind.Caret => Constant.FromNumber((int)a ^ (int)b),

            TokenKind.Less => Bool(a < b),
            TokenKind.LessEqual => Bool(a <= b),
            TokenKind.Greater => Bool(a > b),
            TokenKind.GreaterEqual => Bool(a >= b),
            TokenKind.Equal => Bool(a == b),
            TokenKind.NotEqual => Bool(a != b),
            TokenKind.AndAnd => Bool(a != 0 && b != 0),
            TokenKind.OrOr => Bool(a != 0 || b != 0),

            _ => null,
        };

        static Constant Bool(bool value) => Constant.FromNumber(value ? 1 : 0);
    }
}
