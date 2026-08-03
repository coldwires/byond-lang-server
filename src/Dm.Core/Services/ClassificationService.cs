using System;
using System.Collections.Generic;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>
/// Maps tokens to colouring spans for syntax highlighting.
/// </summary>
/// <remarks>
/// <para>
/// Lexical only. It cannot tell a user type from a builtin, resolve identifiers introduced by
/// macros, or tell a proc name from a var name — those need the object tree and arrive at M6, using
/// the reserved members of <see cref="ClassificationKind"/>. What is here is what most editors
/// ship, and it looks correct.
/// </para>
/// <para>
/// Classification is always computed from a whole-file lex, then filtered. Lexing only the visible
/// range would be wrong: a <c>{" ... "}</c> string or a nested <c>/* */</c> comment can begin
/// thousands of lines earlier, and whether line 900 is code or string text depends on all of it.
/// The whole-file lex is cached on the document, so the cost is paid once per edit rather than once
/// per scroll.
/// </para>
/// </remarks>
public static class ClassificationService
{
    /// <summary>Classifies every token overlapping <paramref name="range"/>.</summary>
    public static IReadOnlyList<ClassifiedSpan> Classify(LexResult lex, TextSpan range)
    {
        ArgumentNullException.ThrowIfNull(lex);

        List<ClassifiedSpan> spans = new();

        foreach (Token token in lex.Tokens)
        {
            if (token.Span.IsEmpty)
                continue;

            if (token.Span.End <= range.Start)
                continue;

            if (token.Span.Start >= range.End)
                break;

            ClassificationKind kind = Classify(token.Kind);
            if (kind == ClassificationKind.None)
                continue;

            // Coalesce touching runs of the same kind. A string is three tokens (start, text, end)
            // and would otherwise be three spans for the client to colour identically.
            if (spans.Count > 0)
            {
                ClassifiedSpan previous = spans[^1];
                if (previous.Kind == kind && previous.Span.End == token.Span.Start)
                {
                    spans[^1] = new ClassifiedSpan(
                        TextSpan.FromBounds(previous.Span.Start, token.Span.End),
                        kind);
                    continue;
                }
            }

            spans.Add(new ClassifiedSpan(token.Span, kind));
        }

        return spans;
    }

    /// <summary>Classifies an inclusive range of lines. Out-of-range lines clamp.</summary>
    public static IReadOnlyList<ClassifiedSpan> ClassifyLines(LexResult lex, int startLine, int endLine)
    {
        ArgumentNullException.ThrowIfNull(lex);

        SourceText text = lex.Text;

        int first = Math.Clamp(startLine, 0, text.LineCount - 1);
        int last = Math.Clamp(endLine, first, text.LineCount - 1);

        TextSpan range = TextSpan.FromBounds(
            text.GetLineStart(first),
            text.GetLineSpanIncludingTerminator(last).End);

        return Classify(lex, range);
    }

    /// <summary>Classifies the whole file.</summary>
    public static IReadOnlyList<ClassifiedSpan> Classify(LexResult lex)
    {
        ArgumentNullException.ThrowIfNull(lex);
        return Classify(lex, new TextSpan(0, lex.Text.Length));
    }

    private static ClassificationKind Classify(TokenKind kind) => kind switch
    {
        TokenKind.Comment => ClassificationKind.Comment,
        TokenKind.Number => ClassificationKind.Number,
        TokenKind.Resource => ClassificationKind.Resource,
        TokenKind.Identifier => ClassificationKind.Identifier,
        TokenKind.Unknown => ClassificationKind.Error,

        TokenKind.StringStart or TokenKind.StringText or TokenKind.StringEnd
            => ClassificationKind.String,

        TokenKind.InterpolationStart or TokenKind.InterpolationEnd
            => ClassificationKind.InterpolationDelimiter,

        TokenKind.Hash or TokenKind.DirectiveName or TokenKind.DirectiveText
            => ClassificationKind.PreprocessorDirective,

        // Layout carries no colour. Newline and Dedent spans are empty or whitespace anyway.
        TokenKind.Newline or TokenKind.Indent or TokenKind.Dedent or TokenKind.EndOfFile
            => ClassificationKind.None,

        TokenKind.OpenParen or TokenKind.CloseParen
            or TokenKind.OpenBracket or TokenKind.CloseBracket
            or TokenKind.OpenBrace or TokenKind.CloseBrace
            or TokenKind.Comma or TokenKind.Semicolon
            => ClassificationKind.Punctuation,

        _ => IsKeyword(kind) ? ClassificationKind.Keyword : ClassificationKind.Operator,
    };

    private static bool IsKeyword(TokenKind kind)
        => kind >= TokenKind.KeywordVar && kind <= TokenKind.KeywordGlobal;
}
