using System;
using System.Collections.Generic;
using System.Threading;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>One whitespace edit the formatter produces.</summary>
public sealed class FormatEdit
{
    /// <summary>Bundles the parts; each argument lands in the same-named property.</summary>
    public FormatEdit(TextSpan span, string newText)
    {
        Span = span;
        NewText = newText;
    }

    /// <summary>The run of whitespace being replaced. Zero-length where a space is inserted.</summary>
    public TextSpan Span { get; }

    /// <summary>What to write there — always whitespace, and never a newline in v1.</summary>
    public string NewText { get; }

    /// <summary>Debug rendering: span and replacement, whitespace made visible.</summary>
    public override string ToString() => $"{Span} -> '{NewText}'";
}

/// <summary>Which rules to apply. Names match the identifiers in <c>docs/dm-format.md</c>.</summary>
public sealed class FormatOptions
{
    /// <summary>F1: spaces around <c>=</c>. Measured at 93–98% of the reference codebase.</summary>
    public bool SpaceAroundAssignment { get; set; } = true;

    /// <summary>
    /// F2: one space after a comma and none before it.
    /// </summary>
    /// <remarks>
    /// The author's call rather than a measured convention — the reference codebase is 64/36, so
    /// this rule ESTABLISHES a style rather than conforming to one, and its first run is the
    /// largest diff the formatter produces.
    /// </remarks>
    public bool SpaceAfterComma { get; set; } = true;

    /// <summary>
    /// F3: spaces around binary <c>+</c> <c>-</c> <c>*</c> <c>%</c> <c>%%</c> <c>**</c>.
    /// </summary>
    /// <remarks>
    /// The author's call rather than a measured convention — the reference codebase is roughly
    /// 50/50. <c>/</c> is deliberately absent: in DM it is overwhelmingly a PATH separator, and the
    /// survey that first suggested otherwise was counting <c>/mob/pc</c> as division 3,836 times.
    /// </remarks>
    public bool SpaceAroundArithmetic { get; set; } = true;

    /// <summary>
    /// F4 and F10: no space between a control keyword and its <c>(</c>.
    /// </summary>
    /// <remarks>
    /// Measured at 79% for <c>if</c>, 94% <c>while</c> and 87% <c>for</c>. <c>switch</c> is only
    /// 57%, so folding it in (F10) is consistency with the other three rather than a convention
    /// read off the code — and it is the ~30 sites where this rule is most visible.
    /// </remarks>
    public bool TightKeywordParen { get; set; } = true;

    /// <summary>F5: drop whitespace before a line break.</summary>
    public bool TrimTrailingWhitespace { get; set; } = true;

    /// <summary>Everything off, for a caller that wants to opt in one rule at a time.</summary>
    public static FormatOptions None => new()
    {
        SpaceAroundAssignment = false,
        SpaceAfterComma = false,
        SpaceAroundArithmetic = false,
        TightKeywordParen = false,
        TrimTrailingWhitespace = false,
    };
}

/// <summary>
/// Whitespace normalisation, driven by <c>docs/dm-format.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built on the token stream rather than on text.</b> DM has several kinds of whitespace that
/// change the program, and the spec's never-touch list is only enforceable if the formatter can
/// tell a string from an operator — which the lexer already does. A regex over source text cannot,
/// and would eventually reformat the inside of a <c>{" ... "}</c> block or a <c>##</c> paste.
/// </para>
/// <para>
/// Three guards fall out of that and are checked before any rule runs:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Only whitespace BETWEEN two tokens on the same line is ever touched.</b> Leading indentation
/// is whitespace at the start of a line and is left alone (F7) — it is semantic in DM, since a
/// <c>proc</c> block indented into a <c>var</c> block declares nothing at all.
/// </description></item>
/// <item><description>
/// <b>A line holding a preprocessor directive is skipped whole.</b> <c>a##b</c> glues and
/// <c>a ## b</c> does not, and this project has already paid 32 invented diagnostics for a lost
/// whitespace fact on that path.
/// </description></item>
/// <item><description>
/// <b>Whitespace around <c>:</c> is never touched.</b> <c>1 ? b : c</c> is a conditional and
/// <c>1 ? b:c</c> is a compile error — the tight colon reads as member access. It is the one place
/// in DM where spacing changes a parse (language notes §15).
/// </description></item>
/// </list>
/// <para>
/// String and comment interiors need no special case: each is a single token, and the formatter
/// only ever writes into the gaps between tokens.
/// </para>
/// </remarks>
public static class FormattingService
{
    /// <summary>
    /// The whitespace edits for a whole document, in source order and non-overlapping.
    /// </summary>
    /// <param name="document">The file to format.</param>
    /// <param name="options">Which rules to apply; defaults to all of them.</param>
    /// <param name="cancellationToken">Aborts at the next token.</param>
    /// <returns>The edits. Empty when the file already conforms, which is the ordinary answer.</returns>
    public static IReadOnlyList<FormatEdit> Format(
        Document document,
        FormatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        options ??= new FormatOptions();

        LexResult lex = document.Lex;
        SourceText text = lex.Text;
        IReadOnlyList<Token> tokens = lex.Tokens;

        List<FormatEdit> edits = new();
        HashSet<int> directiveLines = DirectiveLines(lex, text);

        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Token left = tokens[i];
            Token right = tokens[i + 1];

            if (right.IsEndOfFile)
                break;

            int gapStart = left.Span.End;
            int gapEnd = right.Span.Start;

            if (gapEnd < gapStart)
                continue;

            string gap = text.ToString(new TextSpan(gapStart, gapEnd - gapStart));

            // A gap carrying a line break is layout: indentation, blank lines, continuations.
            // None of that is v1's business (F7), and a `\` continuation makes the whitespace
            // after it string CONTENT rather than layout.
            if (gap.Contains('\n') || gap.Contains('\r') || gap.Contains('\\'))
                continue;

            int line = text.GetLinePosition(gapStart).Line;
            if (directiveLines.Contains(line))
                continue;

            // Comments keep whatever the author put in front of them; F9 is about the text
            // inside the comment and is not this loop's business.
            if (left.IsComment || right.IsComment)
                continue;

            if (Wanted(tokens, i, options) is not { } wanted || wanted == gap)
                continue;

            edits.Add(new FormatEdit(new TextSpan(gapStart, gap.Length), wanted));
        }

        if (options.TrimTrailingWhitespace)
            TrimTrailing(text, edits, cancellationToken);

        edits.Sort((a, b) => a.Span.Start.CompareTo(b.Span.Start));
        return edits;
    }

    /// <summary>
    /// The whitespace a rule wants between two tokens, or null to leave the gap as written.
    /// </summary>
    /// <remarks>
    /// Null rather than the existing text on purpose: "no rule applies here" and "a rule asked for
    /// exactly what is already there" are different states, and only the first should stay silent
    /// when a later rule is added beside it.
    /// </remarks>
    private static string? Wanted(IReadOnlyList<Token> tokens, int index, FormatOptions options)
    {
        Token left = tokens[index];
        Token right = tokens[index + 1];

        // F1. The compound forms (`+=`, `//=`, `:=`) are their own token kinds, so matching the
        // single Assign kind cannot catch them by accident.
        if (options.SpaceAroundAssignment
            && (left.Kind == TokenKind.Assign || right.Kind == TokenKind.Assign))
        {
            return " ";
        }

        // F2. Never a space BEFORE a comma, and one after it — except where the list ends there.
        // `f(a, )` is legal DM and reads as an oversight, so a trailing comma keeps its closer
        // tight. Real code writes it: an argument list broken across a rebuild leaves one behind.
        if (options.SpaceAfterComma)
        {
            if (right.Kind == TokenKind.Comma)
                return string.Empty;

            if (left.Kind == TokenKind.Comma)
                return ClosesAGroup(right.Kind) ? string.Empty : " ";
        }

        // F4 and F10. Only between the keyword and its own `(` — the space AFTER the closing
        // paren is a different question and the spec does not ask it.
        if (options.TightKeywordParen
            && right.Kind == TokenKind.OpenParen
            && IsControlKeyword(left.Kind))
        {
            return string.Empty;
        }

        // F3, and the whole difficulty is telling a BINARY operator from a unary one. `-1` must
        // not become `- 1`, and DM's pointer forms make it worse: `*p` dereferences and `&x` takes
        // a reference, both at precedence level 4 while their binary twins sit at 6 and 11.
        //
        // The test is the one CompletionService already asks of a leading `.` — does a value end
        // just before this operator — so it is shared rather than copied. Anything it cannot
        // confirm is left alone: not touching an expression is always safe, and spacing a unary
        // operator is not.
        if (options.SpaceAroundArithmetic)
        {
            if (IsArithmetic(right.Kind) && CompletionService.HasValueBefore(tokens, index + 1))
                return " ";

            if (IsArithmetic(left.Kind) && CompletionService.HasValueBefore(tokens, index))
                return " ";
        }

        return null;
    }

    /// <summary>
    /// The operators F3 spaces. <c>/</c> is absent on purpose — in DM it is overwhelmingly a path
    /// separator, and the first survey of this corpus counted <c>/mob/pc</c> as division 3,836
    /// times before that was caught.
    /// </summary>
    private static bool IsArithmetic(TokenKind kind) => kind
        is TokenKind.Plus or TokenKind.Minus or TokenKind.Star
        or TokenKind.StarStar or TokenKind.Percent or TokenKind.PercentPercent;

    private static bool ClosesAGroup(TokenKind kind) => kind
        is TokenKind.CloseParen or TokenKind.CloseBracket or TokenKind.CloseBrace;

    /// <summary>
    /// The four heads F4 tightens. <c>switch</c> is F10 and rides the same rule.
    /// </summary>
    /// <remarks>
    /// These are lexer keywords wherever they appear, and §8 records that most of them are also
    /// legal type-path segments — <c>/datum/if</c> compiles. That costs nothing here: the rule
    /// fires only on a keyword immediately followed by <c>(</c>, and it removes whitespace rather
    /// than adding any, so a path segment is untouched either way.
    /// </remarks>
    private static bool IsControlKeyword(TokenKind kind) => kind
        is TokenKind.KeywordIf or TokenKind.KeywordWhile
        or TokenKind.KeywordFor or TokenKind.KeywordSwitch;

    /// <summary>Whitespace immediately before a line break, which F5 removes.</summary>
    private static void TrimTrailing(
        SourceText text, List<FormatEdit> edits, CancellationToken cancellationToken)
    {
        string whole = text.ToString();

        for (int i = 0; i < whole.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (whole[i] is not ('\n' or '\r'))
                continue;

            int end = i;
            int start = end;

            while (start > 0 && whole[start - 1] is ' ' or '\t')
                start--;

            if (start < end)
                edits.Add(new FormatEdit(new TextSpan(start, end - start), string.Empty));

            // Step past a CRLF pair as one terminator rather than finding the LF again.
            if (whole[i] == '\r' && i + 1 < whole.Length && whole[i + 1] == '\n')
                i++;
        }
    }

    /// <summary>
    /// Lines carrying a preprocessor directive, which are skipped whole.
    /// </summary>
    /// <remarks>
    /// Found from the token stream rather than by looking for a leading <c>#</c>, so a <c>#</c>
    /// inside a string or a comment is correctly not a directive — the same reason
    /// <see cref="DocumentLinkService"/> reads includes off tokens.
    /// </remarks>
    private static HashSet<int> DirectiveLines(LexResult lex, SourceText text)
    {
        HashSet<int> lines = new();

        foreach (Token token in lex.Tokens)
        {
            if (token.Kind is TokenKind.Hash or TokenKind.DirectiveName)
                lines.Add(text.GetLinePosition(token.Span.Start).Line);
        }

        return lines;
    }
}
