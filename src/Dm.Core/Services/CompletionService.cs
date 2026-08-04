using System;
using System.Collections.Generic;
using System.Threading;
using Dm.Core.Binding;
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
/// the proc is on including everything it inherits, then globals. Most of it is lookup rather than
/// inference: a declaration usually carries its type, so <c>var/mob/test/t</c> then <c>t.</c> needs
/// nothing worked out.
/// </para>
/// <para>
/// Where a declaration left the type out, <see cref="Binding.TypeInference"/> fills it in from a
/// <c>new</c>, an <c>as</c> clause or an assignment. That is <b>more than dm.exe does</b> — the
/// compiler has no local inference and rejects every <c>.</c> on an untyped var — so those items
/// are offered knowing the compiler would refuse them. See PLAN.md §6 for why.
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
        => CompleteAt(tree, document, line, character, null, encoding, cancellationToken);

    /// <summary>
    /// Builds the completion list, also offering the project's macros for a bare identifier.
    /// </summary>
    /// <remarks>
    /// Macros are the one thing in scope that the object tree cannot know about: they are gone by
    /// the time the parser sees anything, so they have to be carried in separately. They belong
    /// only in the bare-identifier list — a macro is not a member of anything, so nothing after
    /// <c>.</c> or <c>:</c> should offer one.
    /// </remarks>
    public static CompletionResult CompleteAt(
        ObjectTree tree,
        Document document,
        int line,
        int character,
        IReadOnlyCollection<string>? macros,
        PositionEncoding encoding = PositionEncoding.Utf16,
        CancellationToken cancellationToken = default)
        => CompleteAt(tree, document, line, character, macros, null, encoding, cancellationToken);

    /// <summary>
    /// Builds the completion list, also attaching each member's <c>///</c> comment.
    /// </summary>
    /// <remarks>
    /// <paramref name="fileText"/> reads a file the caller can reach. A member's documentation lives
    /// where it was declared, which is rarely the file being completed in, so without it the list
    /// still comes back — just undocumented. Supplied by the workspace, which already caches
    /// documents, so the cost is span arithmetic rather than repeated file reads.
    /// </remarks>
    public static CompletionResult CompleteAt(
        ObjectTree tree,
        Document document,
        int line,
        int character,
        IReadOnlyCollection<string>? macros,
        Func<string, SourceText?>? fileText,
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
            return Identifiers(tree, document, offset, macros, fileText);

        // A partly typed word is not context; the trigger is whatever sits before it.
        if (tokens[index].Kind == TokenKind.Identifier && tokens[index].Span.End >= offset)
            index--;

        TokenKind trigger = index >= 0 ? tokens[index].Kind : TokenKind.EndOfFile;

        switch (trigger)
        {
            case TokenKind.Dot:
            case TokenKind.QuestionDot:
                return Members(tree, document, tokens, index, offset, false, fileText, cancellationToken);

            case TokenKind.Colon:
            case TokenKind.QuestionColon:
                return Members(tree, document, tokens, index, offset, true, fileText, cancellationToken);

            case TokenKind.Slash:
                return TypePaths(tree, tokens, index);

            default:
                return Identifiers(tree, document, offset, macros, fileText);
        }
    }

    /// <summary>The last token starting at or before the cursor.</summary>
    internal static int IndexBefore(IReadOnlyList<Token> tokens, int offset)
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
        Func<string, SourceText?>? fileText,
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
            AddMembers(items, step, fileText);
        }

        // `:` also reaches members declared on subtypes, which is what makes it a wider check
        // rather than an absent one.
        if (widen)
        {
            foreach (TypeSymbol descendant in Descendants(receiver))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddMembers(items, descendant, fileText);
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

    private static void AddMembers(
        Dictionary<string, CompletionItem> items, TypeSymbol type, Func<string, SourceText?>? fileText)
    {
        foreach (VarSymbol variable in type.Vars)
        {
            items.TryAdd(variable.Name, new CompletionItem(
                variable.Name,
                CompletionKind.Variable,
                type.Path.Text,
                variable.IsBuiltin,
                DocumentationFor(variable.Site, variable.IsBuiltin, fileText)));
        }

        foreach (ProcSymbol proc in type.Procs)
        {
            items.TryAdd(proc.Name, new CompletionItem(
                proc.Name,
                proc.IsVerb ? CompletionKind.Verb : CompletionKind.Proc,
                $"{type.Path.Text}  ({string.Join(", ", proc.Parameters)})",
                proc.IsBuiltin,
                proc.Sites.Count > 0
                    ? DocumentationFor(proc.Sites[0], proc.IsBuiltin, fileText)
                    : string.Empty));
        }
    }

    /// <summary>
    /// The <c>///</c> comment above a declaration site, when the caller can reach the file.
    /// </summary>
    /// <remarks>
    /// Builtins are skipped outright: nothing declares them, so there is no file and no comment.
    /// </remarks>
    private static string DocumentationFor(
        DeclarationSite site, bool isBuiltin, Func<string, SourceText?>? fileText)
    {
        if (isBuiltin || fileText is null || string.IsNullOrEmpty(site.File))
            return string.Empty;

        SourceText? text = fileText(site.File);
        return text is null ? string.Empty : DocComments.AboveOffset(text, site.NameSpan.Start);
    }

    /// <summary>
    /// Works out what type sits to the left of the operator.
    /// </summary>
    /// <remarks>
    /// Handles the shapes that carry a type outright — <c>src</c>, a local or parameter with a
    /// declared type, and a name or path that is itself a type — and falls back to inference for a
    /// local that was declared without one. A call result or an index still resolves to nothing,
    /// which is where DM itself gives up and lets <c>.</c> behave like <c>:</c>.
    /// </remarks>
    internal static TypeSymbol? ResolveReceiver(
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

        // A written path: `/obj/item`, `obj/item`, or the relative `.item/sword`.
        if (start < index)
        {
            List<string> segments = new();

            for (int i = start; i <= index; i += 2)
                segments.Add(document.Text.ToString(tokens[i].Span));

            // A leading `.` is a search from the enclosing type upward, not a path from root
            // (PLAN.md §4a). Anything else is looked up as written.
            bool relative = start > 0
                && tokens[start - 1].Kind == TokenKind.Dot
                && (start - 2 < 0 || !IsName(tokens[start - 2].Kind));

            if (relative)
            {
                TypePath anchor = EnclosingType(tree, document, offset)?.Path ?? TypePath.Root;

                return RelativePath.Resolve(tree, anchor, segments) is { } found
                    ? tree.Find(found)
                    : null;
            }

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

    internal static bool IsName(TokenKind kind) => kind
        is TokenKind.Identifier or TokenKind.KeywordSrc or TokenKind.KeywordUsr
        or TokenKind.KeywordWorld or TokenKind.KeywordGlobal;

    // -- scope --------------------------------------------------------------

    private static CompletionResult Identifiers(
        ObjectTree tree,
        Document document,
        int offset,
        IReadOnlyCollection<string>? macros,
        Func<string, SourceText?>? fileText)
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
                AddMembers(items, step, fileText);
        }

        // Globals last. These are the root's procs and vars, which is where the builtins live.
        AddMembers(items, tree.Root, fileText);

        // Macros do not live on any type - the preprocessor has removed them long before the parser
        // runs - so they are carried in separately and go last, behind anything really in scope.
        if (macros is not null)
        {
            foreach (string macro in macros)
                items.TryAdd(macro, new CompletionItem(macro, CompletionKind.Macro, "macro", false));
        }

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

    /// <summary>
    /// The type of a local or parameter: the one it declared, or failing that an inferred one.
    /// </summary>
    /// <remarks>
    /// A written type always wins, because that is the only thing dm.exe itself checks. Inference
    /// only fills the gap where the declaration left the slot empty, and it goes further than the
    /// compiler does — see <see cref="TypeInference"/> for what that costs.
    /// </remarks>
    private static TypePath? FindLocalType(Document document, int offset, string name)
    {
        if (FindEnclosingProc(document, offset) is not { } proc)
            return null;

        foreach (LocalVarStatementSyntax local in Locals(proc, offset))
        {
            if (!string.Equals(local.Name, name, StringComparison.Ordinal))
                continue;

            if (local.DeclaredType is { } type)
                return TypePath.FromSegments(type.Segments);

            // An untyped local. The most recent assignment before the cursor describes what the
            // name holds *here*, so it beats the initialiser rather than the other way round.
            if (LastAssignedType(document, proc, offset, name) is { } assigned)
                return assigned;

            return Infer(document, proc, offset, local.Initializer, name);
        }

        foreach (ParameterSyntax parameter in proc.Parameters)
        {
            if (!string.Equals(parameter.Name, name, StringComparison.Ordinal))
                continue;

            if (parameter.DeclaredType is { } type)
                return TypePath.FromSegments(type.Segments);

            // `f(M as mob)` says what M is without declaring a path.
            return TypeInference.FromInputType(parameter.InputType);
        }

        return null;
    }

    /// <summary>
    /// Infers an expression's type, resolving any bare name it leans on against the same scope.
    /// </summary>
    /// <remarks>
    /// <paramref name="origin"/> is the name being resolved, and is refused as its own answer so
    /// that <c>var/x = x</c> cannot recurse forever.
    /// </remarks>
    private static TypePath? Infer(
        Document document, ProcDeclarationSyntax proc, int offset, ExpressionSyntax? expression, string origin)
        => TypeInference.Infer(expression, referenced
            => string.Equals(referenced, origin, StringComparison.Ordinal)
                ? null
                : FindLocalType(document, offset, referenced));

    /// <summary>
    /// The type last assigned to a name before the cursor, for <c>var/x</c> then <c>x = new /obj</c>.
    /// </summary>
    /// <remarks>
    /// Last one wins rather than first, so a name reassigned to a different type reports what it
    /// most recently held. Assignments after the cursor are ignored: they have not happened yet at
    /// the position being asked about.
    /// </remarks>
    private static TypePath? LastAssignedType(
        Document document, ProcDeclarationSyntax proc, int offset, string name)
    {
        if (proc.Body is null)
            return null;

        TypePath? found = null;

        foreach (StatementSyntax statement in Flatten(proc.Body))
        {
            if (statement.Span.Start >= offset)
                break;

            if (statement is not ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax assignment })
                continue;

            if (assignment.OperatorToken != TokenKind.Assign)
                continue;

            if (assignment.Target is not IdentifierExpressionSyntax target
                || !string.Equals(target.Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            if (Infer(document, proc, offset, assignment.Value, name) is { } inferred)
                found = inferred;
        }

        return found;
    }

    /// <summary>The type whose members <c>src</c> reaches at this position.</summary>
    internal static TypeSymbol? EnclosingType(ObjectTree tree, Document document, int offset)
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

    internal static ProcDeclarationSyntax? FindEnclosingProc(Document document, int offset)
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
