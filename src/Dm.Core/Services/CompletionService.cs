using System;
using System.Collections.Generic;
using System.Threading;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Services;

/// <summary>
/// Answers what can be typed at a position.
/// </summary>
/// <remarks>
/// <para>
/// The scope chain is locals, then the enclosing proc's parameters, then the members of the type
/// the proc is on including everything it inherits, then globals. Nothing here is type inference:
/// a declaration already carries its type, so <c>var/mob/test/t</c> then <c>t.</c> is a lookup.
/// </para>
/// <para>
/// <c>.</c> and <c>:</c> produce different lists, and the difference is not "checked versus
/// unchecked". <c>.</c> checks the declared type; <c>:</c> widens the check to the declared type
/// <b>and its subtypes</b>, so reaching a member of an unrelated type is still an error. Offering
/// everything after <c>:</c> would be wrong — see PLAN.md §4a.
/// </para>
/// </remarks>
public static class CompletionService
{
    /// <summary>Builds the completion list for a position.</summary>
    public static CompletionResult CompleteAt(
        ObjectTree tree,
        Document document,
        int line,
        int character,
        PositionEncoding encoding = PositionEncoding.Utf16,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(document);

        cancellationToken.ThrowIfCancellationRequested();

        SourceText text = document.Text;
        int offset = text.GetOffset(new LinePosition(line, character), encoding);

        IReadOnlyList<Token> tokens = document.Lex.Tokens;
        int index = IndexBefore(tokens, offset);

        if (index < 0)
            return Empty(CompletionContext.Identifier, tree, document, offset);

        // A partly typed word is not context; the trigger is whatever sits before it.
        if (tokens[index].Kind == TokenKind.Identifier && tokens[index].Span.End >= offset)
            index--;

        TokenKind trigger = index >= 0 ? tokens[index].Kind : TokenKind.EndOfFile;

        switch (trigger)
        {
            case TokenKind.Dot:
            case TokenKind.QuestionDot:
                return Members(tree, document, tokens, index, offset, widen: false, cancellationToken);

            case TokenKind.Colon:
            case TokenKind.QuestionColon:
                return Members(tree, document, tokens, index, offset, widen: true, cancellationToken);

            case TokenKind.Slash:
                return TypePaths(tree, tokens, index);

            default:
                return Identifiers(tree, document, offset);
        }
    }

    private static CompletionResult Empty(
        CompletionContext context, ObjectTree tree, Document document, int offset)
        => Identifiers(tree, document, offset);

    /// <summary>The last token starting at or before the cursor.</summary>
    private static int IndexBefore(IReadOnlyList<Token> tokens, int offset)
    {
        int found = -1;

        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[i];

            if (token.Kind is TokenKind.Comment or TokenKind.Newline
                or TokenKind.Indent or TokenKind.Dedent or TokenKind.EndOfFile)
            {
                continue;
            }

            if (token.Span.Start >= offset)
                break;

            found = i;
        }

        return found;
    }

    // -- member access ------------------------------------------------------

    private static CompletionResult Members(
        ObjectTree tree,
        Document document,
        IReadOnlyList<Token> tokens,
        int operatorIndex,
        int offset,
        bool widen,
        CancellationToken cancellationToken)
    {
        TypeSymbol? receiver = ResolveReceiver(tree, document, tokens, operatorIndex - 1, offset);

        CompletionContext context = widen ? CompletionContext.SubtypeMember : CompletionContext.Member;

        if (receiver is null)
            return new CompletionResult(context, Array.Empty<CompletionItem>());

        Dictionary<string, CompletionItem> items = new(StringComparer.Ordinal);

        // The declared type and everything it inherits, in both modes.
        foreach (TypeSymbol step in tree.InheritanceChain(receiver))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddMembers(items, step);
        }

        // `:` also reaches members declared on subtypes, which is what makes it a wider check
        // rather than an absent one.
        if (widen)
        {
            foreach (TypeSymbol descendant in Descendants(receiver))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddMembers(items, descendant);
            }
        }

        return new CompletionResult(context, Sorted(items));
    }

    private static IEnumerable<TypeSymbol> Descendants(TypeSymbol type)
    {
        foreach (TypeSymbol child in type.Children)
        {
            yield return child;

            foreach (TypeSymbol deeper in Descendants(child))
                yield return deeper;
        }
    }

    private static void AddMembers(Dictionary<string, CompletionItem> items, TypeSymbol type)
    {
        foreach (VarSymbol variable in type.Vars)
        {
            items.TryAdd(variable.Name, new CompletionItem(
                variable.Name, CompletionKind.Variable, type.Path.Text, variable.IsBuiltin));
        }

        foreach (ProcSymbol proc in type.Procs)
        {
            items.TryAdd(proc.Name, new CompletionItem(
                proc.Name,
                proc.IsVerb ? CompletionKind.Verb : CompletionKind.Proc,
                $"{type.Path.Text}  ({string.Join(", ", proc.Parameters)})",
                proc.IsBuiltin));
        }
    }

    /// <summary>
    /// Works out what type sits to the left of the operator.
    /// </summary>
    /// <remarks>
    /// Handles the three shapes that carry a type without inference: <c>src</c>, a local or
    /// parameter with a declared type, and a name or path that is itself a type. Anything else —
    /// a call result, an index — has no declared type, which is exactly where DM itself gives up
    /// and lets <c>.</c> behave like <c>:</c>.
    /// </remarks>
    private static TypeSymbol? ResolveReceiver(
        ObjectTree tree, Document document, IReadOnlyList<Token> tokens, int index, int offset)
    {
        if (index < 0)
            return null;

        // A dotted path written out, `/obj/item.`, resolves as a path.
        int start = index;
        while (start > 0 && tokens[start - 1].Kind is TokenKind.Slash or TokenKind.Dot
               && start - 2 >= 0 && IsName(tokens[start - 2].Kind))
        {
            start -= 2;
        }

        if (tokens[index].Kind == TokenKind.KeywordSrc)
            return EnclosingType(tree, document, offset);

        if (!IsName(tokens[index].Kind))
            return null;

        // A written path: `/obj/item` or `obj/item`.
        if (start < index)
        {
            List<string> segments = new();

            for (int i = start; i <= index; i += 2)
                segments.Add(document.Text.ToString(tokens[i].Span));

            return tree.Find(TypePath.FromSegments(segments));
        }

        string name = document.Text.ToString(tokens[index].Span);

        // A local or parameter carries its declared type.
        if (FindLocalType(document, offset, name) is { } localType)
            return tree.Find(localType);

        // A var on the enclosing type, then a bare type name such as `mob`.
        if (EnclosingType(tree, document, offset) is { } enclosing
            && tree.ResolveVar(enclosing, name) is { DeclaredType: { } declared })
        {
            return tree.Find(declared);
        }

        return tree.Find(TypePath.Root.Append(name));
    }

    private static bool IsName(TokenKind kind) => kind
        is TokenKind.Identifier or TokenKind.KeywordSrc or TokenKind.KeywordUsr
        or TokenKind.KeywordWorld or TokenKind.KeywordGlobal;

    // -- scope --------------------------------------------------------------

    private static CompletionResult Identifiers(ObjectTree tree, Document document, int offset)
    {
        Dictionary<string, CompletionItem> items = new(StringComparer.Ordinal);

        // Nearest first: locals and parameters shadow members, which shadow globals.
        if (FindEnclosingProc(document, offset) is { } proc)
        {
            foreach (ParameterSyntax parameter in proc.Parameters)
            {
                items.TryAdd(parameter.Name, new CompletionItem(
                    parameter.Name, CompletionKind.Parameter, parameter.DeclaredType?.Text ?? string.Empty, false));
            }

            foreach (LocalVarStatementSyntax local in Locals(proc, offset))
            {
                items[local.Name] = new CompletionItem(
                    local.Name, CompletionKind.Local, local.DeclaredType?.Text ?? string.Empty, false);
            }
        }

        if (EnclosingType(tree, document, offset) is { } enclosing)
        {
            foreach (TypeSymbol step in tree.InheritanceChain(enclosing))
                AddMembers(items, step);
        }

        // Globals last. These are the root's procs and vars, which is where the builtins live.
        AddMembers(items, tree.Root);

        return new CompletionResult(CompletionContext.Identifier, Sorted(items));
    }

    /// <summary>Locals declared before the cursor. One declared later is not in scope yet.</summary>
    private static IEnumerable<LocalVarStatementSyntax> Locals(ProcDeclarationSyntax proc, int offset)
    {
        if (proc.Body is null)
            yield break;

        foreach (StatementSyntax statement in Flatten(proc.Body))
        {
            if (statement.Span.Start >= offset)
                yield break;

            if (statement is LocalVarStatementSyntax local)
            {
                yield return local;

                foreach (LocalVarStatementSyntax sibling in local.Siblings)
                    yield return sibling;
            }
        }
    }

    private static IEnumerable<StatementSyntax> Flatten(StatementSyntax statement)
    {
        yield return statement;

        switch (statement)
        {
            case BlockStatementSyntax block:
                foreach (StatementSyntax child in block.Statements)
                {
                    foreach (StatementSyntax deeper in Flatten(child))
                        yield return deeper;
                }

                break;

            case IfStatementSyntax branch:
                foreach (StatementSyntax deeper in FlattenOptional(branch.Then, branch.Otherwise))
                    yield return deeper;

                break;

            case ForStatementSyntax loop:
                foreach (StatementSyntax initializer in loop.Initializers)
                {
                    foreach (StatementSyntax deeper in Flatten(initializer))
                        yield return deeper;
                }

                foreach (StatementSyntax deeper in FlattenOptional(loop.Body, null))
                    yield return deeper;

                break;

            case WhileStatementSyntax loop:
                foreach (StatementSyntax deeper in FlattenOptional(loop.Body, null))
                    yield return deeper;

                break;

            case SpawnStatementSyntax spawn:
                foreach (StatementSyntax deeper in FlattenOptional(spawn.Body, null))
                    yield return deeper;

                break;
        }
    }

    private static IEnumerable<StatementSyntax> FlattenOptional(StatementSyntax? first, StatementSyntax? second)
    {
        if (first is not null)
        {
            foreach (StatementSyntax deeper in Flatten(first))
                yield return deeper;
        }

        if (second is not null)
        {
            foreach (StatementSyntax deeper in Flatten(second))
                yield return deeper;
        }
    }

    private static TypePath? FindLocalType(Document document, int offset, string name)
    {
        if (FindEnclosingProc(document, offset) is not { } proc)
            return null;

        foreach (LocalVarStatementSyntax local in Locals(proc, offset))
        {
            if (string.Equals(local.Name, name, StringComparison.Ordinal) && local.DeclaredType is { } type)
                return TypePath.FromSegments(type.Segments);
        }

        foreach (ParameterSyntax parameter in proc.Parameters)
        {
            if (string.Equals(parameter.Name, name, StringComparison.Ordinal) && parameter.DeclaredType is { } type)
                return TypePath.FromSegments(type.Segments);
        }

        return null;
    }

    /// <summary>The type whose members <c>src</c> reaches at this position.</summary>
    private static TypeSymbol? EnclosingType(ObjectTree tree, Document document, int offset)
    {
        TypePath path = TypePath.Root;
        bool found = false;

        Walk(document.Parse.Root.Declarations, TypePath.Root);

        return found ? tree.Find(path) : null;

        void Walk(IReadOnlyList<DeclarationSyntax> declarations, TypePath enclosing)
        {
            foreach (DeclarationSyntax declaration in declarations)
            {
                if (offset < declaration.Span.Start || offset > declaration.Span.End)
                    continue;

                switch (declaration)
                {
                    case TypeDeclarationSyntax { IsGroupHeader: true } group:
                        Walk(group.Members, enclosing);
                        break;

                    case TypeDeclarationSyntax type:
                    {
                        TypePath here = type.Path.Anchor == PathAnchor.Absolute
                            ? TypePath.FromSegments(type.Path.Segments)
                            : enclosing.Append(type.Path.Segments);

                        path = here;
                        found = true;
                        Walk(type.Members, here);
                        break;
                    }

                    case ProcDeclarationSyntax proc:
                    {
                        // The owner is everything before the name, minus any `proc`/`verb` segment.
                        List<string> owner = new();

                        foreach (string segment in proc.Path.Segments)
                        {
                            if (segment is "proc" or "verb")
                                continue;

                            owner.Add(segment);
                        }

                        if (owner.Count > 0)
                            owner.RemoveAt(owner.Count - 1);

                        path = proc.Path.Anchor == PathAnchor.Absolute
                            ? TypePath.FromSegments(owner)
                            : enclosing.Append(owner);

                        found = true;
                        break;
                    }
                }
            }
        }
    }

    private static ProcDeclarationSyntax? FindEnclosingProc(Document document, int offset)
    {
        ProcDeclarationSyntax? found = null;
        Walk(document.Parse.Root.Declarations);
        return found;

        void Walk(IReadOnlyList<DeclarationSyntax> declarations)
        {
            foreach (DeclarationSyntax declaration in declarations)
            {
                if (offset < declaration.Span.Start || offset > declaration.Span.End)
                    continue;

                switch (declaration)
                {
                    case ProcDeclarationSyntax proc:
                        found = proc;
                        break;

                    case TypeDeclarationSyntax type:
                        Walk(type.Members);
                        break;
                }
            }
        }
    }

    // -- paths --------------------------------------------------------------

    private static CompletionResult TypePaths(ObjectTree tree, IReadOnlyList<Token> tokens, int slashIndex)
    {
        // Only the root's children can be offered without knowing the written prefix, which the
        // member path above already handles when there is one.
        List<CompletionItem> items = new();

        foreach (TypeSymbol child in tree.Root.Children)
            items.Add(new CompletionItem(child.Name, CompletionKind.Type, child.Path.Text, child.IsBuiltin));

        items.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return new CompletionResult(CompletionContext.TypePath, items);
    }

    private static List<CompletionItem> Sorted(Dictionary<string, CompletionItem> items)
    {
        List<CompletionItem> sorted = new(items.Values);
        sorted.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return sorted;
    }
}
