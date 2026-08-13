using System;
using System.Collections.Generic;
using Dm.Core.Text;

namespace Dm.Core.Syntax;

/// <summary>
/// The tokens a parser reads, together with how to get each one's text.
/// </summary>
/// <remarks>
/// <para>
/// Exists because a token's <b>position</b> and its <b>text</b> stop being the same question once
/// the preprocessor is involved. Parsing a file straight off the lexer, every token indexes into
/// that one file and <c>Text.ToString(token.Span)</c> is the whole story. Parsing the preprocessed
/// stream, a token produced by a macro has its characters in the file that <i>defined</i> the macro
/// while the only position worth reporting is where the macro was <i>used</i>.
/// </para>
/// <para>
/// So this carries both: spans are always in <see cref="Text"/>, the file the author was looking
/// at, and <see cref="TextOf"/> returns what the token actually says. For an expanded run every
/// token of one invocation collapses onto the invocation's span, which is what makes
/// <see cref="Text.TextSpan.FromBounds"/> across a declaration meaningful again — and is also
/// exactly where a diagnostic or a go-to-definition should land.
/// </para>
/// <para>
/// Comments are dropped on the way in. They carry no structure, and every consumer filtered them
/// out anyway.
/// </para>
/// </remarks>
internal sealed class TokenSource
{
    private readonly string[]? _texts;
    private readonly bool[]? _spaceBefore;

    private TokenSource(SourceText text, IReadOnlyList<Token> tokens, string[]? texts, bool[]? spaceBefore)
    {
        Text = text;
        Tokens = tokens;
        _texts = texts;
        _spaceBefore = spaceBefore;
    }

    /// <summary>The file positions are measured in.</summary>
    public SourceText Text { get; }

    /// <summary>Tokens in order, comments removed. Spans are in <see cref="Text"/>.</summary>
    public IReadOnlyList<Token> Tokens { get; }

    /// <summary>True when the tokens came through the preprocessor rather than straight off a lex.</summary>
    public bool IsExpanded => _texts is not null;

    /// <summary>What the token at this index says.</summary>
    public string TextOf(int index)
        => _texts is null ? Text.ToString(Tokens[index].Span) : _texts[index];

    /// <summary>
    /// Whether whitespace immediately precedes this token <b>where it was written</b>.
    /// </summary>
    /// <remarks>
    /// DM has exactly one place where spacing changes a parse: a conditional's <c>:</c> is member
    /// access when it sits tight against a bare identifier, and the conditional's colon otherwise
    /// (PLAN.md §4c). The parser used to answer this by looking at the character before the token's
    /// span — which stops working the moment a span is repositioned onto a macro invocation, since
    /// the character before the invocation says nothing about the text inside it. Captured here
    /// against the token's real location instead, while that is still known.
    /// </remarks>
    public bool HasWhitespaceBefore(int index)
    {
        if (_spaceBefore is not null)
            return _spaceBefore[index];

        int start = Tokens[index].Span.Start;
        return start > 0 && char.IsWhiteSpace(Text[start - 1]);
    }

    /// <summary>A single file, straight off the lexer.</summary>
    public static TokenSource FromLex(LexResult lex)
    {
        ArgumentNullException.ThrowIfNull(lex);

        List<Token> tokens = new(lex.Tokens.Count);

        foreach (Token token in lex.Tokens)
        {
            if (token.Kind != TokenKind.Comment)
                tokens.Add(token);
        }

        return new TokenSource(lex.Text, tokens, null, null);
    }

    /// <summary>
    /// One file's worth of preprocessed tokens.
    /// </summary>
    /// <remarks>
    /// Each token keeps its own text but is repositioned onto the span an editor should point at,
    /// so a macro-heavy declaration still reports against the line the author wrote.
    /// </remarks>
    public static TokenSource FromExpanded(SourceText origin, IReadOnlyList<Preprocessing.ExpandedToken> run)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(run);

        List<Token> tokens = new(run.Count);
        List<string> texts = new(run.Count);
        List<bool> spaceBefore = new(run.Count);
        int furthest = 0;

        foreach (Preprocessing.ExpandedToken expanded in run)
        {
            if (expanded.Kind == TokenKind.Comment)
                continue;

            TextSpan span = expanded.ReportAt.Span;

            // Reported positions can go backwards inside one expansion. A body token reports at the
            // invocation, while a token substituted from an argument keeps the position the caller
            // wrote it at — so `#define X(a) a + a` yields argument, body, argument, and the body
            // token sits before the argument that preceded it. The parser builds node spans with
            // FromBounds across a run of tokens, which throws outright on a backwards pair.
            //
            // Collapsing a backwards token onto the furthest point reached keeps the sequence
            // non-decreasing without inventing a position: everything in one expansion belongs to
            // the invocation anyway, which is the only place worth pointing at.
            if (span.Start < furthest)
                span = new TextSpan(furthest, 0);
            else
                furthest = span.Start;

            tokens.Add(new Token(expanded.Kind, span));
            texts.Add(expanded.Text);

            // Against the token's own source, before repositioning loses it.
            int start = expanded.Span.Start;
            spaceBefore.Add(start > 0 && char.IsWhiteSpace(expanded.Source[start - 1]));
        }

        return new TokenSource(origin, tokens, texts.ToArray(), spaceBefore.ToArray());
    }
}
