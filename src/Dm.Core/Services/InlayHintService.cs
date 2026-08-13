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

    /// <summary>A parameter's name at a call site, rendered before the argument.</summary>
    Parameter = 1,
}

/// <summary>One rendered annotation, anchored after a position in the file.</summary>
public sealed class InlayHint
{
    /// <summary>A hint at a position, its label already rendered.</summary>
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

    /// <summary>Type or parameter, so a client can style and toggle the kinds separately.</summary>
    public InlayHintKind Kind { get; }

    /// <summary>Debug rendering.</summary>
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
/// <c>CompletionService.FindLocalType</c>, so the two cannot disagree about what a local
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

                    foreach (ExpressionSyntax expression in ExpressionsIn(statement))
                        ParameterHints(tree, document, expression, startLine, endLine, encoding, hints);
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

    /// <summary>
    /// Every expression a statement holds, outermost first, including nested calls.
    /// </summary>
    /// <remarks>
    /// The type half only needs statements, so the walk above stops there. Parameter names live at
    /// call sites, and a call is an expression that nests — <c>f(g(1), 2)</c> wants hints on both.
    /// </remarks>
    private static IEnumerable<ExpressionSyntax> ExpressionsIn(StatementSyntax statement)
    {
        Stack<ExpressionSyntax?> pending = new();

        switch (statement)
        {
            case ExpressionStatementSyntax expression: pending.Push(expression.Expression); break;
            case LocalVarStatementSyntax local: pending.Push(local.Initializer); break;
            case ReturnStatementSyntax returned: pending.Push(returned.Value); break;
            case IfStatementSyntax branch: pending.Push(branch.Condition); break;
            case WhileStatementSyntax loop: pending.Push(loop.Condition); break;
            case DoWhileStatementSyntax loop: pending.Push(loop.Condition); break;
            case SwitchStatementSyntax choice: pending.Push(choice.Value); break;
            case UnaryStatementSyntax unary: pending.Push(unary.Operand); break;
            case SpawnStatementSyntax spawn: pending.Push(spawn.Delay); break;

            case ForStatementSyntax loop:
                pending.Push(loop.Condition);
                pending.Push(loop.Sequence);
                pending.Push(loop.RangeEnd);
                pending.Push(loop.Step);
                break;
        }

        while (pending.Count > 0)
        {
            ExpressionSyntax? current = pending.Pop();

            if (current is null)
                continue;

            yield return current;

            switch (current)
            {
                case InvocationExpressionSyntax invocation:
                    pending.Push(invocation.Target);

                    foreach (ArgumentSyntax argument in invocation.Arguments)
                    {
                        pending.Push(argument.Name);
                        pending.Push(argument.Value);
                    }

                    break;

                case NewExpressionSyntax created:
                    foreach (ArgumentSyntax argument in created.Arguments)
                        pending.Push(argument.Value);

                    break;

                case BinaryExpressionSyntax binary:
                    pending.Push(binary.Left);
                    pending.Push(binary.Right);
                    break;

                case AssignmentExpressionSyntax assignment:
                    pending.Push(assignment.Target);
                    pending.Push(assignment.Value);
                    break;

                case ConditionalExpressionSyntax conditional:
                    pending.Push(conditional.Condition);
                    pending.Push(conditional.WhenTrue);
                    pending.Push(conditional.WhenFalse);
                    break;

                case IndexExpressionSyntax index:
                    pending.Push(index.Target);
                    pending.Push(index.Index);
                    break;

                case MemberAccessExpressionSyntax member: pending.Push(member.Target); break;
                case UnaryExpressionSyntax unary: pending.Push(unary.Operand); break;
                case AsExpressionSyntax clause: pending.Push(clause.Expression); break;

                case InterpolatedStringExpressionSyntax interpolated:
                    foreach (InterpolatedStringPartSyntax part in interpolated.Parts)
                        pending.Push(part.Expression);

                    break;
            }
        }
    }

    /// <summary>
    /// Parameter names at a call site — <c>heal(<em>amount:</em> 5, <em>silent:</em> 1)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The callee is resolved by asking <see cref="SignatureHelpService"/> at each argument's own
    /// position, which answers with both the parameter list and WHICH parameter that position is.
    /// One resolution engine: a second copy would eventually disagree with the signature popup
    /// about what a call means, and the two sit side by side on screen.
    /// </para>
    /// <para>
    /// Suppressed where the hint would say nothing: an argument already written as
    /// <c>name = value</c>, and one whose text is already the parameter's name, which is the case
    /// that would otherwise put <c>amount:</c> in front of <c>amount</c>.
    /// </para>
    /// </remarks>
    private static void ParameterHints(
        ObjectTree tree,
        Document document,
        ExpressionSyntax expression,
        int startLine,
        int endLine,
        PositionEncoding encoding,
        List<InlayHint> hints)
    {
        if (expression is not InvocationExpressionSyntax invocation)
            return;

        foreach (ArgumentSyntax argument in invocation.Arguments)
        {
            // `f(name = value)` already says which parameter it means.
            if (argument.Name is not null || argument.Value.Span.IsEmpty)
                continue;

            LinePosition position = document.Text.GetLinePosition(argument.Value.Span.Start, encoding);

            if (position.Line < startLine || position.Line > endLine)
                continue;

            SignatureHelpResult? signature = SignatureHelpService.SignatureAt(
                tree, document, position.Line, position.Character, encoding);

            if (signature is null
                || signature.ActiveParameter < 0
                || signature.ActiveParameter >= signature.Parameters.Count)
            {
                continue;
            }

            string name = ParameterName(signature.Parameters[signature.ActiveParameter]);

            if (name.Length == 0 || name == document.Text.ToString(argument.Value.Span).Trim())
                continue;

            hints.Add(new InlayHint(position, $"{name}:", InlayHintKind.Parameter));
        }
    }

    /// <summary>
    /// The bare name out of a rendered parameter — <c>mob/target</c>, <c>amount as num</c> and
    /// <c>silent = 0</c> all yield the name alone.
    /// </summary>
    /// <remarks>
    /// Read back off the rendering rather than kept separately, because a signature is rendered
    /// once and every surface shows that same string; a second store would drift from it. The
    /// three shapes are exactly what <c>TypeTreeBuilder.Render</c> produces.
    /// </remarks>
    internal static string ParameterName(string rendered)
    {
        string text = rendered;

        int equals = text.IndexOf('=');
        if (equals >= 0)
            text = text[..equals];

        int asClause = text.IndexOf(" as ", StringComparison.Ordinal);
        if (asClause >= 0)
            text = text[..asClause];

        text = text.Trim();

        int slash = text.LastIndexOf('/');
        return slash >= 0 ? text[(slash + 1)..] : text;
    }
}
