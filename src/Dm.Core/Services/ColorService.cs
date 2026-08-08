using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>How a colour was written, which decides what a presentation may offer back.</summary>
public enum ColorForm
{
    /// <summary>A string literal: <c>"#f00"</c>, <c>"#ff0000"</c>, <c>"#ff0000ff"</c>.</summary>
    Literal = 0,

    /// <summary>An <c>rgb()</c> call with plain numeric arguments.</summary>
    RgbCall = 1,
}

/// <summary>One colour found in a file, as the swatch a client draws beside it.</summary>
/// <remarks>
/// Components are 0-255 because that is what DM writes and reads: <c>rgb()</c> takes 0-255 and
/// returns <c>#RRGGBB</c>. The 0-1 floats LSP wants are a boundary conversion, not the model.
/// </remarks>
public sealed class ColorInformation
{
    public ColorInformation(TextSpan span, int red, int green, int blue, int alpha, ColorForm form)
    {
        Span = span;
        Red = red;
        Green = green;
        Blue = blue;
        Alpha = alpha;
        Form = form;
    }

    /// <summary>
    /// The whole colour as written, quotes included for a literal and <c>rgb(</c> to <c>)</c> for a
    /// call, so a presentation replaces the lot rather than leaving a stray quote behind.
    /// </summary>
    public TextSpan Span { get; }

    public int Red { get; }

    public int Green { get; }

    public int Blue { get; }

    /// <summary>0 transparent, 255 opaque. 255 when none was written.</summary>
    public int Alpha { get; }

    public ColorForm Form { get; }

    public override string ToString() =>
        $"{Span} rgb({Red},{Green},{Blue},{Alpha}) {Form}";
}

/// <summary>
/// The colours written in one file, and the text to write when a picker changes one.
/// </summary>
/// <remarks>
/// <para>
/// Off the token stream and per file, like <see cref="FoldingService"/> and
/// <see cref="DocumentLinkService"/>: a swatch is wanted for the file on screen whether or not the
/// project has been walked, and reading tokens means a <c>#ff0000</c> inside a comment is correctly
/// not a colour.
/// </para>
/// <para>
/// <b>Every rule here was read off the compiler rather than the reference</b>, and two of them are
/// not what the documentation implies (PLAN §8):
/// </para>
/// <list type="bullet">
/// <item><description>
/// A short form expands by <b>duplicating the digit</b>, so <c>#f08</c> is 255, 0, <b>136</b> —
/// <c>rgb2num("#f08")</c> returns <c>[255,0,136]</c>, not <c>[255,0,128]</c>. Shifting left by four
/// gives 128 and a visibly wrong swatch on every three-digit colour.
/// </description></item>
/// <item><description>
/// <c>rgb()</c> <b>truncates</b> a fractional component and clamps an out-of-range one:
/// <c>rgb(1.4,1.5,1.6)</c> is <c>#010101</c> and <c>rgb(300,-20,0)</c> is <c>#ff0000</c>. Rounding
/// 1.5 to 2 would disagree with the compiler on the one value most likely to be written.
/// </description></item>
/// </list>
/// <para>
/// <b>What is deliberately not a colour here</b>, each because answering would mean guessing:
/// an interpolated string (<c>"[c]#ff0000"</c> is not statically a colour), an <c>rgb()</c> whose
/// arguments are not plain numbers, and any call carrying a <c>space</c> or named argument — those
/// need the <c>COLORSPACE_*</c> constants, which are <c>#define</c>s in <c>stddef.dm</c> that
/// nothing seeds, plus HSV/HSL/HCY conversion. A named colour (<c>"red"</c>) is a real DM colour and
/// is also skipped: the table is ~140 names and none of it is verified yet. All four are silent
/// omissions on purpose — a wrong swatch reads as our bug, a missing one reads as unfinished.
/// </para>
/// </remarks>
public static class ColorService
{
    public static IReadOnlyList<ColorInformation> ColorsIn(
        Document document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        List<ColorInformation> colors = new();
        LexResult lex = document.Lex;
        IReadOnlyList<Token> tokens = lex.Tokens;

        for (int i = 0; i < tokens.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryReadLiteral(lex, tokens, i, out ColorInformation? literal))
            {
                colors.Add(literal!);
                i += 2;
                continue;
            }

            if (TryReadRgbCall(lex, tokens, i, out ColorInformation? call, out int consumed))
            {
                colors.Add(call!);
                i += consumed;
            }
        }

        return colors;
    }

    /// <summary>
    /// What a client may write in place of <paramref name="color"/>, nearest spelling first.
    /// </summary>
    /// <remarks>
    /// The form it was written in leads, so picking a colour does not silently rewrite an
    /// <c>rgb()</c> call into a literal or the reverse. An alpha of 255 is left off both spellings:
    /// DM treats a missing alpha as opaque, and <c>#ff0000ff</c> would be churn on every edit.
    /// </remarks>
    public static IReadOnlyList<string> PresentationsFor(ColorInformation color)
    {
        ArgumentNullException.ThrowIfNull(color);

        string hex = color.Alpha >= 255
            ? $"\"#{color.Red:x2}{color.Green:x2}{color.Blue:x2}\""
            : $"\"#{color.Red:x2}{color.Green:x2}{color.Blue:x2}{color.Alpha:x2}\"";

        string call = color.Alpha >= 255
            ? $"rgb({color.Red}, {color.Green}, {color.Blue})"
            : $"rgb({color.Red}, {color.Green}, {color.Blue}, {color.Alpha})";

        return color.Form == ColorForm.RgbCall
            ? new[] { call, hex }
            : new[] { hex, call };
    }

    /// <summary>
    /// A string literal holding nothing but a hex colour.
    /// </summary>
    /// <remarks>
    /// Exactly three tokens — start, one text run, end. An interpolated string carries more, so the
    /// shape check is what excludes <c>"[c]#ff0000"</c> rather than a separate test.
    /// </remarks>
    private static bool TryReadLiteral(
        LexResult lex, IReadOnlyList<Token> tokens, int index, out ColorInformation? color)
    {
        color = null;

        if (index + 2 >= tokens.Count)
            return false;

        if (tokens[index].Kind != TokenKind.StringStart
            || tokens[index + 1].Kind != TokenKind.StringText
            || tokens[index + 2].Kind != TokenKind.StringEnd)
        {
            return false;
        }

        if (!TryParseHex(lex.GetText(tokens[index + 1]), out int r, out int g, out int b, out int a))
            return false;

        color = new ColorInformation(
            TextSpan.FromBounds(tokens[index].Span.Start, tokens[index + 2].Span.End),
            r, g, b, a, ColorForm.Literal);

        return true;
    }

    /// <summary>
    /// <c>#rgb</c>, <c>#rgba</c>, <c>#rrggbb</c> or <c>#rrggbbaa</c>, case-insensitive.
    /// </summary>
    private static bool TryParseHex(string text, out int r, out int g, out int b, out int a)
    {
        r = g = b = 0;
        a = 255;

        if (text.Length is not (4 or 5 or 7 or 9) || text[0] != '#')
            return false;

        for (int i = 1; i < text.Length; i++)
        {
            if (!Uri.IsHexDigit(text[i]))
                return false;
        }

        if (text.Length is 4 or 5)
        {
            // Duplicated, not shifted: `#f08` is 255, 0, 136. Compiler-verified through rgb2num.
            r = Doubled(text[1]);
            g = Doubled(text[2]);
            b = Doubled(text[3]);

            if (text.Length == 5)
                a = Doubled(text[4]);

            return true;
        }

        r = Byte(text, 1);
        g = Byte(text, 3);
        b = Byte(text, 5);

        if (text.Length == 9)
            a = Byte(text, 7);

        return true;

        static int Doubled(char c)
        {
            int v = Convert.ToInt32(c.ToString(), 16);
            return (v * 16) + v;
        }

        static int Byte(string text, int at) =>
            Convert.ToInt32(text.Substring(at, 2), 16);
    }

    /// <summary>
    /// An <c>rgb(</c> call whose arguments are three or four plain number literals.
    /// </summary>
    /// <remarks>
    /// A named or <c>space</c> argument means another colour space, which this does not model, so
    /// anything that is not <c>number , number , number [, number]</c> is skipped whole. Reading it
    /// as RGB anyway would draw a red swatch beside a blue colour.
    /// </remarks>
    private static bool TryReadRgbCall(
        LexResult lex, IReadOnlyList<Token> tokens, int index,
        out ColorInformation? color, out int consumed)
    {
        color = null;
        consumed = 0;

        if (tokens[index].Kind != TokenKind.Identifier
            || index + 1 >= tokens.Count
            || tokens[index + 1].Kind != TokenKind.OpenParen
            || !string.Equals(lex.GetText(tokens[index]), "rgb", StringComparison.Ordinal))
        {
            return false;
        }

        Span<int> parts = stackalloc int[4];
        int count = 0;
        int at = index + 2;

        while (at < tokens.Count && count < 4)
        {
            // The lexer splits `-20` into Minus and Number, so a negative argument is two tokens.
            // It is worth accepting because the compiler clamps it — rgb(-1,-1,-1) is #000000 —
            // and skipping it would leave a written colour with no swatch.
            bool negative = tokens[at].Kind == TokenKind.Minus;

            if (negative && ++at >= tokens.Count)
                return false;

            if (tokens[at].Kind != TokenKind.Number)
                return false;

            if (!TryComponent(lex.GetText(tokens[at]), negative, out parts[count]))
                return false;

            count++;
            at++;

            if (at >= tokens.Count)
                return false;

            if (tokens[at].Kind == TokenKind.CloseParen)
                break;

            if (tokens[at].Kind != TokenKind.Comma)
                return false;

            at++;
        }

        if (at >= tokens.Count || tokens[at].Kind != TokenKind.CloseParen || count is not (3 or 4))
            return false;

        color = new ColorInformation(
            TextSpan.FromBounds(tokens[index].Span.Start, tokens[at].Span.End),
            parts[0], parts[1], parts[2], count == 4 ? parts[3] : 255, ColorForm.RgbCall);

        consumed = at - index;
        return true;
    }

    /// <summary>
    /// One numeric argument, clamped and truncated the way the compiler does it.
    /// </summary>
    private static bool TryComponent(string text, bool negative, out int value)
    {
        value = 0;

        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw))
            return false;

        if (negative)
            raw = -raw;

        // Truncation, not rounding: rgb(1.4,1.5,1.6) is #010101 on 516.1686.
        double truncated = Math.Truncate(raw);

        value = truncated switch
        {
            <= 0 => 0,
            >= 255 => 255,
            _ => (int)truncated,
        };

        return true;
    }
}
