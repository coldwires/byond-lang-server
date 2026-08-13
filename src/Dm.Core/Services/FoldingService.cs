using System;
using System.Collections.Generic;
using System.Threading;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>What a foldable region is, so a client can honour "fold all comments" separately.</summary>
public enum FoldKind
{
    /// <summary>A declaration and its members.</summary>
    Region = 0,

    /// <summary>A block comment, or a run of touching line comments.</summary>
    Comment = 1,
}

/// <summary>One foldable line range, inclusive of both ends.</summary>
public sealed class FoldingRange
{
    /// <summary>One range; lines are zero-based.</summary>
    public FoldingRange(int startLine, int endLine, FoldKind kind)
    {
        StartLine = startLine;
        EndLine = endLine;
        Kind = kind;
    }

    /// <summary>Zero-based first line of the fold.</summary>
    public int StartLine { get; }

    /// <summary>Zero-based last line, inclusive — this line folds too.</summary>
    public int EndLine { get; }

    /// <summary>Region or comment, so a client can fold each separately.</summary>
    public FoldKind Kind { get; }

    /// <summary>Debug rendering.</summary>
    public override string ToString() => $"{StartLine}-{EndLine} {Kind}";
}

/// <summary>
/// Foldable regions for a file: declarations with members, and comment runs.
/// </summary>
/// <remarks>
/// <para>
/// Built from the AST rather than from indentation, because DM has two block syntaxes that nest
/// freely (PLAN §8) — folding by leading whitespace would miss everything written inside braces,
/// which is most macro-generated code.
/// </para>
/// <para>
/// A region is emitted only when it spans more than one line: a one-line declaration has nothing
/// to hide, and an editor that draws a fold arrow beside it looks broken.
/// </para>
/// </remarks>
public static class FoldingService
{
    /// <summary>Foldable ranges for the file: multi-line declarations, proc bodies and comment runs.</summary>
    public static IReadOnlyList<FoldingRange> RangesFor(
        Document document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        List<FoldingRange> ranges = new();

        foreach (DeclarationSyntax declaration in document.Parse.Root.Declarations)
            Walk(document, declaration, ranges, cancellationToken);

        AddCommentRuns(document, ranges, cancellationToken);

        return ranges;
    }

    private static void Walk(
        Document document,
        DeclarationSyntax declaration,
        List<FoldingRange> ranges,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Add(document, declaration.Span, FoldKind.Region, ranges);

        switch (declaration)
        {
            case TypeDeclarationSyntax type:
                foreach (DeclarationSyntax member in type.Members)
                    Walk(document, member, ranges, cancellationToken);

                break;

            case ProcDeclarationSyntax { Body: { } body }:
                // The body alone, so a proc's signature stays visible when its body is folded.
                Add(document, body.Span, FoldKind.Region, ranges);
                break;
        }
    }

    /// <summary>
    /// Block comments, and runs of line comments that touch.
    /// </summary>
    /// <remarks>
    /// A run is what a reader thinks of as one comment — the same grouping <c>DocComments</c> uses
    /// for a <c>///</c> block — so folding them individually would be noise.
    /// </remarks>
    private static void AddCommentRuns(
        Document document, List<FoldingRange> ranges, CancellationToken cancellationToken)
    {
        SourceText text = document.Text;

        int runStart = -1;
        int runEnd = -1;

        foreach (Token token in document.Lex.Tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (token.Kind != TokenKind.Comment)
                continue;

            int start = text.GetLineIndex(token.Span.Start);
            int end = text.GetLineIndex(Math.Max(token.Span.Start, token.Span.End - 1));

            if (runStart < 0)
            {
                runStart = start;
                runEnd = end;
                continue;
            }

            // Touching or overlapping: the same comment as far as a reader is concerned.
            if (start <= runEnd + 1)
            {
                runEnd = Math.Max(runEnd, end);
                continue;
            }

            Emit(runStart, runEnd, ranges);
            runStart = start;
            runEnd = end;
        }

        if (runStart >= 0)
            Emit(runStart, runEnd, ranges);

        static void Emit(int start, int end, List<FoldingRange> into)
        {
            if (end > start)
                into.Add(new FoldingRange(start, end, FoldKind.Comment));
        }
    }

    private static void Add(Document document, TextSpan span, FoldKind kind, List<FoldingRange> ranges)
    {
        if (span.IsEmpty || span.End > document.Text.Length)
            return;

        int start = document.Text.GetLineIndex(span.Start);
        int end = document.Text.GetLineIndex(Math.Max(span.Start, span.End - 1));

        // Nothing to hide on a single line, and a fold arrow beside one reads as a bug.
        if (end > start)
            ranges.Add(new FoldingRange(start, end, kind));
    }
}
