using System;
using System.Collections.Generic;
using System.Threading;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>What a hint annotates, so a client can style and toggle the kinds separately.</summary>
public enum InlayHintKind
{
    /// <summary>An inferred type on an untyped local, rendered after its name.</summary>
    Type = 0,
}

/// <summary>One rendered annotation, anchored after a position in the file.</summary>
public sealed class InlayHint
{
    public InlayHint(LinePosition position, string label, InlayHintKind kind)
    {
        Position = position;
        Label = label;
        Kind = kind;
    }

    /// <summary>Where the hint sits — immediately after the name it annotates.</summary>
    public LinePosition Position { get; }

    /// <summary>The rendered text, separator included: <c>: /obj/item</c>.</summary>
    public string Label { get; }

    public InlayHintKind Kind { get; }

    public override string ToString() => $"{Position} {Label}";
}

/// <summary>
/// Annotations a client renders inline: the inferred type of an untyped local.
/// </summary>
/// <remarks>
/// <para>
/// DM code is full of <c>var/x = new /obj/item</c>, and the type is exactly what a reader does not
/// have — the compiler never checks it (PLAN.md §8), so nothing forces the author to write it.
/// The hint shows the same inference completion rides on, through the same
/// <see cref="CompletionService.FindLocalType"/>, so the two cannot disagree about what a local
/// holds. Every hint is inferred by construction: a WRITTEN type needs no hint.
/// </para>
/// <para>
/// Parameter-name hints at call sites are the other half of the feature and are not built yet —
/// they need argument spans walked out of every invocation, which is its own piece of work.
/// </para>
/// </remarks>
public static class InlayHintService
{
    /// <summary>Hints for the lines from <paramref name="startLine"/> through <paramref name="endLine"/>, inclusive.</summary>
    public static IReadOnlyList<InlayHint> HintsFor(
        ObjectTree tree,
        Document document,
        int startLine,
        int endLine,
        PositionEncoding encoding = PositionEncoding.Utf16,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(document);

        List<InlayHint> hints = new();

        foreach (DeclarationSyntax declaration in document.Parse.Root.Declarations)
            Walk(tree, document, declaration, startLine, endLine, encoding, hints, cancellationToken);

        return hints;
    }

    private static void Walk(
        ObjectTree tree,
        Document document,
        DeclarationSyntax declaration,
        int startLine,
        int endLine,
        PositionEncoding encoding,
        List<InlayHint> hints,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (declaration)
        {
            case TypeDeclarationSyntax type:
                foreach (DeclarationSyntax member in type.Members)
                    Walk(tree, document, member, startLine, endLine, encoding, hints, cancellationToken);

                break;

            case ProcDeclarationSyntax { Body: { } body }:
                foreach (StatementSyntax statement in CompletionService.Flatten(body))
                {
                    if (statement is LocalVarStatementSyntax local)
                    {
                        Hint(tree, document, local, startLine, endLine, encoding, hints);

                        foreach (LocalVarStatementSyntax sibling in local.Siblings)
                            Hint(tree, document, sibling, startLine, endLine, encoding, hints);
                    }
                }

                break;
        }
    }

    private static void Hint(
        ObjectTree tree,
        Document document,
        LocalVarStatementSyntax local,
        int startLine,
        int endLine,
        PositionEncoding encoding,
        List<InlayHint> hints)
    {
        // A written type needs no hint, and a wrong guess would sit beside a right answer.
        if (local.DeclaredType is not null || local.NameSpan.IsEmpty)
            return;

        LinePosition position = document.Text.GetLinePosition(local.NameSpan.End, encoding);
        if (position.Line < startLine || position.Line > endLine)
            return;

        // The same inference completion uses, asked at the end of the declaration — the
        // initializer's answer, before any later reassignment changes what the name holds.
        TypePath? type = CompletionService.FindLocalType(
            document, local.Span.End, local.Name, out bool inferred);

        // Hint only what the tree can confirm exists: a path inference invented from a typo would
        // otherwise be rendered as if it were knowledge.
        if (type is null || !inferred || tree.Find(type.Value) is null)
            return;

        hints.Add(new InlayHint(position, $": {type.Value.Text}", InlayHintKind.Type));
    }
}
