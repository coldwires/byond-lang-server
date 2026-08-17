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

    /// <summary>
    /// F8: spaces around comparison and logical operators — <c>a == b</c>, <c>a &amp;&amp; b</c>,
    /// <c>a &lt; b</c>.
    /// </summary>
    /// <remarks>
    /// Measured at 69–98% of the reference codebase and <c>==</c> alone at 98%, so this is a
    /// convention read off the code rather than one established over it. It needs none of F3's
    /// unary guard: no operator in the set has a unary form.
    /// </remarks>
    public bool SpaceAroundComparison { get; set; } = true;

    /// <summary>F6: a run of three or more blank lines becomes one.</summary>
    /// <remarks>
    /// Measured at 90% single across the reference codebase, with doubles common enough
    /// (278 against 109 triple-or-more) that two blank lines are left as written. A run ENDING the
    /// file is also left alone: what is there is the file's trailing newlines, and that sits next
    /// to the never-touch rule about a final newline rather than under this one.
    /// </remarks>
    public bool CollapseBlankRuns { get; set; } = true;

    /// <summary>F11: a blank line before a proc or verb declaration that has none.</summary>
    /// <remarks>
    /// <para>
    /// <b>Insert-only, and the spec's basis for it did not survive re-measurement.</b> F11 was
    /// recorded as "measured, 84%"; counting all 2,421 proc and verb declarations across the five
    /// reference projects gives 36% with exactly one blank line and <b>54% with none</b>. So this
    /// rule establishes a convention rather than conforming to one — F2 and F3's position — and it
    /// is written to add spacing rather than to take an author's away.
    /// </para>
    /// <para>
    /// Two positions are exempt, and the second is not a style choice at all:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// A declaration sitting directly under the header that OPENS its block, which is 501 of the
    /// 1,308 sites with no blank line. Splitting <c>proc</c> from its first child reads as damage.
    /// </description></item>
    /// <item><description>
    /// A declaration under a comment. <b>A blank line ends a doc-comment run</b>, so inserting one
    /// between a <c>///</c> and the proc it documents detaches the documentation that hover and
    /// completion show — a change to what the tooling reports, not to layout.
    /// </description></item>
    /// </list>
    /// </remarks>
    public bool BlankLineBeforeProc { get; set; } = true;

    /// <summary>F9: one space between a line comment's slashes and its text.</summary>
    /// <remarks>
    /// Measured at 74% — the weakest rule in the spec, and purely cosmetic. <b>Insert-only</b>:
    /// whitespace the author already wrote is left exactly as written, so an aligned trailing
    /// comment and a comment's own internal indentation both survive. A run of nothing but
    /// slashes is a banner rather than a comment with text to separate, and is left alone.
    /// </remarks>
    public bool SpaceAfterCommentSlashes { get; set; } = true;

    /// <summary>Everything off, for a caller that wants to opt in one rule at a time.</summary>
    public static FormatOptions None => new()
    {
        SpaceAroundAssignment = false,
        SpaceAfterComma = false,
        SpaceAroundArithmetic = false,
        TightKeywordParen = false,
        TrimTrailingWhitespace = false,
        SpaceAroundComparison = false,
        SpaceAfterCommentSlashes = false,
        CollapseBlankRuns = false,
        BlankLineBeforeProc = false,
    };

    /// <summary>
    /// The spec's defaults with the file's own <c>.editorconfig</c> applied over them.
    /// </summary>
    /// <param name="filePath">The file about to be formatted. Need not exist.</param>
    /// <returns>Options to hand <see cref="FormattingService.Format"/>.</returns>
    /// <remarks>
    /// <para>
    /// <c>docs/dm-format.md</c>: where <c>.editorconfig</c> disagrees with the spec, it wins — a
    /// project should not have to argue with the formatter about its own house style. Where it
    /// says nothing, the defaults above stand.
    /// </para>
    /// <para>
    /// <b>One key reaches a v1 rule</b>, and that is a fact about v1 rather than about the
    /// parsing: <c>trim_trailing_whitespace</c> is F5. The other two keys the spec's example
    /// shows — <c>indent_style</c> and <c>insert_final_newline</c> — govern the two things v1
    /// deliberately never touches (F7 holds indentation, and about half the reference codebase's
    /// files have no final newline), so honouring them would mean claiming a rule that does not
    /// exist. They join the mapping the day their rules do.
    /// </para>
    /// </remarks>
    public static FormatOptions ForFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        FormatOptions options = new();

        if (EditorConfig.PropertiesFor(filePath).TryGetValue("trim_trailing_whitespace", out string? trim))
            options.TrimTrailingWhitespace = trim == "true";

        return options;
    }
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

        if (options.SpaceAfterCommentSlashes)
            SpaceCommentSlashes(lex, text, directiveLines, edits, cancellationToken);

        if (options.BlankLineBeforeProc)
            BlankLineBeforeProcs(document, text, edits, cancellationToken);

        // F6 before F5, because a blank line made of spaces is both a trailing-whitespace site and
        // part of a run this may delete whole — and two edits over the same characters is not a
        // set a client can apply.
        IReadOnlyList<TextSpan> deleted = options.CollapseBlankRuns
            ? CollapseBlankRuns(lex, text, edits, cancellationToken)
            : Array.Empty<TextSpan>();

        if (options.TrimTrailingWhitespace)
            TrimTrailing(text, deleted, edits, cancellationToken);

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

        // F8, and it is the cheap twin of F3: none of these operators has a unary form, so there
        // is nothing to tell apart and no guard to get wrong.
        if (options.SpaceAroundComparison
            && (IsComparison(left.Kind) || IsComparison(right.Kind)))
        {
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

    /// <summary>
    /// The operators F8 spaces: the comparisons, and <c>&amp;&amp;</c> / <c>||</c>.
    /// </summary>
    /// <remarks>
    /// Three families are deliberately absent, each for its own reason. <c>!</c> is unary. The
    /// bitwise <c>&amp;</c> <c>|</c> <c>^</c> are not comparisons and two of them wear other hats
    /// — <c>&amp;x</c> takes a reference (§4c level 4) and <c>|</c> separates the <c>as num|text</c>
    /// input filters. And <c>&lt;&lt;</c> / <c>&gt;&gt;</c> are DM's output and input operators as
    /// often as they are shifts. The spec's F8 names comparison and logical operators; these are
    /// them.
    /// </remarks>
    private static bool IsComparison(TokenKind kind) => kind
        is TokenKind.Equal or TokenKind.NotEqual
        or TokenKind.Less or TokenKind.Greater
        or TokenKind.LessEqual or TokenKind.GreaterEqual
        or TokenKind.Spaceship or TokenKind.EquivalentTo or TokenKind.NotEquivalentTo
        or TokenKind.AndAnd or TokenKind.OrOr;

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

    /// <summary>
    /// F9: a space between a line comment's leading slashes and its first character.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one rule that reaches INSIDE a token, since a comment is a single token and its text is
    /// the whole point. That is safe here and nowhere else: a comment's content reaches no
    /// compiler, while a string's is program data.
    /// </para>
    /// <para>
    /// The whole run of slashes is stepped over rather than the first two, or a <c>///</c> doc
    /// comment would become <c>// /</c> — the same doc comment hover and completion read.
    /// </para>
    /// </remarks>
    private static void SpaceCommentSlashes(
        LexResult lex,
        SourceText text,
        HashSet<int> directiveLines,
        List<FormatEdit> edits,
        CancellationToken cancellationToken)
    {
        foreach (Token token in lex.Tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!token.IsComment)
                continue;

            string comment = text.ToString(token.Span);

            // A block comment is a different rule the spec does not have.
            if (!comment.StartsWith("//", StringComparison.Ordinal))
                continue;

            if (directiveLines.Contains(text.GetLinePosition(token.Span.Start).Line))
                continue;

            int slashes = 0;
            while (slashes < comment.Length && comment[slashes] == '/')
                slashes++;

            // Nothing but slashes is a banner, and a bare `//` has no text to separate.
            if (slashes == comment.Length)
                continue;

            if (comment[slashes] is ' ' or '\t' or '\r' or '\n')
                continue;

            edits.Add(new FormatEdit(new TextSpan(token.Span.Start + slashes, 0), " "));
        }
    }

    /// <summary>
    /// F11: one blank line before each proc or verb declaration that has none.
    /// </summary>
    /// <remarks>
    /// The declarations come from the outline rather than from a token pattern, because DM's
    /// commonest proc declaration is an OVERRIDE — <c>/mob/Login()</c> — which carries no
    /// <c>proc</c> segment for a pattern to match. The outline needs the file's parse and no
    /// object tree, so this stays a per-file answer like the rest of the formatter.
    /// </remarks>
    private static void BlankLineBeforeProcs(
        Document document, SourceText text, List<FormatEdit> edits, CancellationToken cancellationToken)
    {
        foreach (DocumentSymbol symbol in
                 DocumentSymbolService.GetSymbols(document.Parse, cancellationToken: cancellationToken))
        {
            InsertBlankLineBefore(symbol, text, edits, cancellationToken);
        }
    }

    private static void InsertBlankLineBefore(
        DocumentSymbol symbol, SourceText text, List<FormatEdit> edits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (DocumentSymbol child in symbol.Children)
            InsertBlankLineBefore(child, text, edits, cancellationToken);

        if (symbol.Kind is not (SymbolKind.Proc or SymbolKind.Verb))
            return;

        int line = symbol.Start.Line;

        // Nothing to separate it from.
        if (line <= 0 || line >= text.LineCount)
            return;

        string previous = text.GetLineText(line - 1);

        // Already spaced.
        if (previous.Trim().Length == 0)
            return;

        // The header that opens this block: splitting `proc` from its first child reads as damage
        // rather than as spacing.
        if (IndentWidth(previous) < IndentWidth(text.GetLineText(line)))
            return;

        // A comment above a declaration is attached to it, and a blank line ENDS a doc-comment
        // run — so inserting one here would take the documentation off the symbol.
        if (previous.TrimStart().StartsWith("//", StringComparison.Ordinal)
            || previous.TrimEnd().EndsWith("*/", StringComparison.Ordinal))
        {
            return;
        }

        // The previous line's own terminator, so a CRLF file stays a CRLF file.
        TextSpan content = text.GetLineSpan(line - 1);
        TextSpan whole = text.GetLineSpanIncludingTerminator(line - 1);
        string terminator = text.ToString(new TextSpan(content.End, whole.End - content.End));

        if (terminator.Length == 0)
            return;

        edits.Add(new FormatEdit(new TextSpan(text.GetLineStart(line), 0), terminator));
    }

    /// <summary>
    /// Indentation as the compiler counts it: a tab and a space are each one column (§8).
    /// </summary>
    private static int IndentWidth(string line)
    {
        int width = 0;

        while (width < line.Length && line[width] is ' ' or '\t')
            width++;

        return width;
    }

    /// <summary>
    /// F6: every blank line but the first, in a run of three or more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns what it deleted, because F5 has to know: a blank line made of spaces is trailing
    /// whitespace AND part of a run, and an edit inside another edit is not applicable.
    /// </para>
    /// <para>
    /// The guard that matters is the multi-line token: a <c>{" ... "}</c> string carries its
    /// newlines as CONTENT, so a blank line inside one is program data rather than layout.
    /// Collapsing those would change what the program holds, which is the never-touch list's whole
    /// subject.
    /// </para>
    /// </remarks>
    private static List<TextSpan> CollapseBlankRuns(
        LexResult lex, SourceText text, List<FormatEdit> edits, CancellationToken cancellationToken)
    {
        List<TextSpan> deletions = new();
        HashSet<int> insideAToken = LinesInsideMultiLineTokens(lex, text);

        int line = 0;

        while (line < text.LineCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsBlank(text, line) || insideAToken.Contains(line))
            {
                line++;
                continue;
            }

            int last = line;

            while (last + 1 < text.LineCount
                && IsBlank(text, last + 1)
                && !insideAToken.Contains(last + 1))
            {
                last++;
            }

            // A run that reaches the end of the file is the file's trailing newlines. Left alone
            // on purpose — see the option's own note.
            if (last - line + 1 >= 3 && last < text.LineCount - 1)
            {
                int start = text.GetLineStart(line + 1);
                TextSpan span = new(start, text.GetLineStart(last + 1) - start);

                deletions.Add(span);
                edits.Add(new FormatEdit(span, string.Empty));
            }

            line = last + 1;
        }

        return deletions;
    }

    private static bool IsBlank(SourceText text, int line)
        => text.GetLineText(line).Trim().Length == 0;

    /// <summary>
    /// Every line that a single token continues onto — a <c>{" ... "}</c> string, a nesting block
    /// comment, anything carried across a <c>\</c> continuation.
    /// </summary>
    private static HashSet<int> LinesInsideMultiLineTokens(LexResult lex, SourceText text)
    {
        HashSet<int> lines = new();

        foreach (Token token in lex.Tokens)
        {
            if (token.Span.Length == 0)
                continue;

            int first = text.GetLineIndex(token.Span.Start);
            int last = text.GetLineIndex(token.Span.End - 1);

            for (int line = first + 1; line <= last; line++)
                lines.Add(line);
        }

        return lines;
    }

    /// <summary>Whitespace immediately before a line break, which F5 removes.</summary>
    private static void TrimTrailing(
        SourceText text,
        IReadOnlyList<TextSpan> deleted,
        List<FormatEdit> edits,
        CancellationToken cancellationToken)
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

            if (start < end && !IsInside(deleted, start))
                edits.Add(new FormatEdit(new TextSpan(start, end - start), string.Empty));

            // Step past a CRLF pair as one terminator rather than finding the LF again.
            if (whole[i] == '\r' && i + 1 < whole.Length && whole[i + 1] == '\n')
                i++;
        }
    }

    private static bool IsInside(IReadOnlyList<TextSpan> spans, int offset)
    {
        foreach (TextSpan span in spans)
        {
            if (offset >= span.Start && offset < span.End)
                return true;
        }

        return false;
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
