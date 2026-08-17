using System;
using System.Collections.Generic;
using System.Threading;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>One text edit a code action applies, against the file the action was asked for.</summary>
public sealed class CodeActionEdit
{
    /// <summary>Bundles the parts; each argument lands in the same-named property.</summary>
    public CodeActionEdit(TextSpan span, string newText)
    {
        Span = span;
        NewText = newText;
    }

    /// <summary>
    /// The text being replaced. Zero-length for an insert, which is what every action here
    /// produces — an insert applies cleanly to a buffer somebody else is editing, where a
    /// replacement of surrounding text would fight them for it.
    /// </summary>
    public TextSpan Span { get; }

    /// <summary>What to write there.</summary>
    public string NewText { get; }

    /// <summary>Debug rendering: span and replacement.</summary>
    public override string ToString() => $"{Span} -> {NewText}";
}

/// <summary>A fix offered for a diagnostic at a position.</summary>
public sealed class CodeAction
{
    internal CodeAction(string title, string diagnosticId, IReadOnlyList<CodeActionEdit> edits)
    {
        Title = title;
        DiagnosticId = diagnosticId;
        Edits = edits;
    }

    /// <summary>What to show the user: <c>Declare x as /obj/item</c>.</summary>
    public string Title { get; }

    /// <summary>The diagnostic this answers — <c>DM0400</c> or <c>DM0401</c>.</summary>
    public string DiagnosticId { get; }

    /// <summary>The edits, applied together. Never empty: an action with nothing to do is not offered.</summary>
    public IReadOnlyList<CodeActionEdit> Edits { get; }

    /// <summary>Debug rendering: the title.</summary>
    public override string ToString() => Title;
}

/// <summary>
/// Fixes for the diagnostics the binder reports, as edits a client applies.
/// </summary>
/// <remarks>
/// <para>
/// One action today: <b>declare the type</b> on a member reached through an untyped local. That is
/// deliberately the first, because it is the only place this analyzer knowingly disagrees with
/// <c>dm.exe</c> — PLAN.md §6 records the decision to infer through <c>new</c> and assignment when
/// the compiler infers nothing at all, so completion offers members the build will refuse. Until
/// now the divergence was only ever FLAGGED (<c>inferred</c> on a completion item, a type inlay
/// hint, then <c>DM0400</c> when the author accepted one). This is the first surface that resolves
/// it: the fix for "the compiler will not check this" is to write the type down, and the type is
/// already known.
/// </para>
/// <para>
/// The edit is a <b>zero-length insert immediately before the name</b>, so <c>var/static/x</c>
/// becomes <c>var/static/obj/item/x</c> with the author's modifiers untouched where they were.
/// Probed on 516.1687 rather than assumed: a modifier keeps working anywhere in the segment run,
/// so <c>var/obj/item/static/x</c> applies <c>static</c> just as <c>var/static/obj/item/x</c> does
/// — verified by calling twice and watching the value persist, with a modifier-free control that
/// must NOT persist. Both spellings being legal is why the insert point is a choice, and the
/// conservative choice is to leave what the author wrote where they wrote it.
/// </para>
/// <para>
/// An action is offered only when applying it would actually FIX the access — the member has to
/// resolve on the inferred type. Offering it otherwise would trade one error for a different one
/// and call that a fix.
/// </para>
/// </remarks>
public static class CodeActionService
{
    /// <summary>
    /// Fixes available for the lines from <paramref name="startLine"/> through
    /// <paramref name="endLine"/>, inclusive and zero-based.
    /// </summary>
    /// <param name="tree">The project tree the receiver's type is confirmed against.</param>
    /// <param name="document">The file being edited.</param>
    /// <param name="startLine">First line to offer fixes on, zero-based.</param>
    /// <param name="endLine">Last line to offer fixes on, zero-based and inclusive.</param>
    /// <param name="encoding">Units the line lookup counts in.</param>
    /// <param name="cancellationToken">Aborts the walk at the next check.</param>
    /// <returns>The actions, in source order. Empty is the ordinary answer.</returns>
    public static IReadOnlyList<CodeAction> ActionsIn(
        ObjectTree tree,
        Document document,
        int startLine,
        int endLine,
        PositionEncoding encoding = PositionEncoding.Utf16,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(document);

        List<CodeAction> actions = new();

        foreach (DeclarationSyntax declaration in document.Parse.Root.Declarations)
            Walk(tree, document, declaration, startLine, endLine, encoding, actions, cancellationToken);

        return actions;
    }

    private static void Walk(
        ObjectTree tree,
        Document document,
        DeclarationSyntax declaration,
        int startLine,
        int endLine,
        PositionEncoding encoding,
        List<CodeAction> actions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (declaration)
        {
            case TypeDeclarationSyntax type:
                foreach (DeclarationSyntax member in type.Members)
                    Walk(tree, document, member, startLine, endLine, encoding, actions, cancellationToken);

                break;

            case ProcDeclarationSyntax { Body: { } body } proc:
                foreach (StatementSyntax statement in CompletionService.Flatten(body))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Offer(tree, document, proc, statement, startLine, endLine, encoding, actions);
                }

                break;
        }
    }

    private static void Offer(
        ObjectTree tree,
        Document document,
        ProcDeclarationSyntax proc,
        StatementSyntax statement,
        int startLine,
        int endLine,
        PositionEncoding encoding,
        List<CodeAction> actions)
    {
        // Which member accesses are CALLED, so the action names the diagnostic the binder
        // actually emitted: DM0401 for an invoked member, DM0400 otherwise. Read off the AST
        // rather than inferred from which lookup succeeded. The two agree on everything actually
        // offered today — the guard below drops the cases where they would not — but that is a
        // coincidence of the guard rather than a property of the label, and inferring the id from
        // the filter would tie the two together with nothing saying so.
        HashSet<(int, int)> invoked = new();
        List<MemberAccessExpressionSyntax> members = new();

        foreach (ExpressionSyntax expression in InlayHintService.ExpressionsIn(statement))
        {
            if (expression is InvocationExpressionSyntax { Target: MemberAccessExpressionSyntax callee })
                invoked.Add((callee.NameSpan.Start, callee.NameSpan.Length));

            if (expression is MemberAccessExpressionSyntax member)
                members.Add(member);
        }

        foreach (MemberAccessExpressionSyntax member in members)
        {
            if (member.NameSpan.IsEmpty || member.Target is not IdentifierExpressionSyntax bare)
                continue;

            LinePosition position = document.Text.GetLinePosition(member.NameSpan.Start, encoding);
            if (position.Line < startLine || position.Line > endLine)
                continue;

            if (DeclaringLocal(proc, bare.Name, member.NameSpan.Start) is not { } local)
                continue;

            // Only an UNTYPED declaration has anything to fix. `DeclaredType.Of` is the one copy
            // of "a written type, else brackets give /list" — the rule that had three copies until
            // 2026-08-15 and answered differently in one of them.
            if (DeclaredType.Of(local.DeclaredType, local.HasBrackets) is not null)
                continue;

            TypePath? inferredType = CompletionService.FindLocalType(
                document, member.NameSpan.Start, bare.Name, out bool inferred);

            // Inference only. A written type is not this diagnostic, and a path the tree cannot
            // confirm would write a typo into the author's declaration as though it were known.
            if (inferredType is not { } type || !inferred || tree.Find(type) is not { } symbol)
                continue;

            bool isCall = invoked.Contains((member.NameSpan.Start, member.NameSpan.Length));

            // The fix has to FIX it. If the member does not resolve on the inferred type either,
            // declaring that type swaps one error for another and calls it a repair.
            bool resolves = isCall
                ? tree.ResolveProc(symbol, member.Name) is not null
                : tree.ResolveVar(symbol, member.Name) is not null;

            if (!resolves)
                continue;

            // `/obj/item` inserted before the name as `obj/item/`, which is what turns
            // `var/x` into `var/obj/item/x` and `var/static/x` into `var/static/obj/item/x`.
            string segments = type.Text.TrimStart('/');
            if (segments.Length == 0)
                continue;

            actions.Add(new CodeAction(
                $"Declare {bare.Name} as {type.Text}",
                isCall ? "DM0401" : "DM0400",
                new[] { new CodeActionEdit(new TextSpan(local.NameSpan.Start, 0), segments + "/") }));
        }
    }

    /// <summary>
    /// The declaration of <paramref name="name"/> in force at <paramref name="offset"/>, or null.
    /// </summary>
    /// <remarks>
    /// Nearest declaration BEFORE the use, so a name declared twice in one proc — different types
    /// in two loops, which is real DM — is fixed against the one the use actually sees. Siblings of
    /// a comma list are their own declarations and carry their own names.
    /// </remarks>
    private static LocalVarStatementSyntax? DeclaringLocal(
        ProcDeclarationSyntax proc, string name, int offset)
    {
        if (proc.Body is null)
            return null;

        LocalVarStatementSyntax? found = null;

        foreach (StatementSyntax statement in CompletionService.Flatten(proc.Body))
        {
            if (statement is not LocalVarStatementSyntax local)
                continue;

            Consider(local);

            foreach (LocalVarStatementSyntax sibling in local.Siblings)
                Consider(sibling);
        }

        return found;

        void Consider(LocalVarStatementSyntax candidate)
        {
            if (candidate.NameSpan.IsEmpty
                || candidate.NameSpan.Start > offset
                || !string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                return;
            }

            if (found is null || candidate.NameSpan.Start > found.NameSpan.Start)
                found = candidate;
        }
    }
}
