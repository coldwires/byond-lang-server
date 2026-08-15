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
        CancellationToken cancellationToken = default,
        int limit = 0)
        => CompleteAt(tree, document, line, character, macros, fileText, encoding, cancellationToken,
            documentOnly: null, limit);

    /// <summary>
    /// As above, collecting documentation for one item only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lazy-resolve half: a bare identifier on /tg/station offers <b>19,898</b> items and the
    /// user reads one. With <paramref name="documentOnly"/> set, exactly one item is documented —
    /// the rest come back with an empty string, which is what an unresolved item looks like anyway.
    /// </para>
    /// <para>
    /// <b>This is a payload saving, not a speed one</b>, and it was measured rather than assumed.
    /// Documentation is 12.7% of that 1.0 MB payload, so omitting it cuts the bytes — but the
    /// lookups themselves run over already-cached text, and full-versus-brief timing on
    /// /tg/station came back inside run-to-run noise (+210 ms, then −132 ms on ~11 s). The item
    /// COUNT is the rest of the payload and neither half of this addresses it; see PLAN §9.
    /// </para>
    /// </remarks>
    internal static CompletionResult CompleteAt(
        ObjectTree tree,
        Document document,
        int line,
        int character,
        IReadOnlyCollection<string>? macros,
        Func<string, SourceText?>? fileText,
        PositionEncoding encoding,
        CancellationToken cancellationToken,
        string? documentOnly,
        int limit = 0)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(document);

        cancellationToken.ThrowIfCancellationRequested();

        SourceText text = document.Text;
        int offset = text.GetOffset(new LinePosition(line, character), encoding);

        IReadOnlyList<Token> tokens = document.Lex.Tokens;
        int index = IndexBefore(tokens, offset);

        if (index < 0)
            return Identifiers(tree, document, offset, macros, fileText, documentOnly, limit);

        // A partly typed word is not context; the trigger is whatever sits before it.
        if (tokens[index].Kind == TokenKind.Identifier && tokens[index].Span.End >= offset)
            index--;

        // Inside an `icon_state = "…"`, the useful list is the states of the icon THIS TYPE uses,
        // which nothing else in the editor can tell the author. Checked before the trigger switch
        // because the caret is inside a string, where every other context is meaningless.
        if (IconStateCompletion(tree, document, tokens, index, offset) is { } states)
            return states;

        TokenKind trigger = index >= 0 ? tokens[index].Kind : TokenKind.EndOfFile;

        switch (trigger)
        {
            // A `.` with no value in front of it is DM's return-value variable, not member access.
            // Distinct context, empty list: the variable is untyped, and every client was having to
            // guess that the user did not want an identifier dump after typing `.`.
            case TokenKind.Dot when !HasValueBefore(tokens, index):
                return new CompletionResult(CompletionContext.ReturnValue, Array.Empty<CompletionItem>());

            // A `.` after a WRITTEN PATH continues the path; it is not member access. Mid-path the
            // two separators are the same token, so `/obj/item.weight` is the type
            // `/obj/item/weight` and dm.exe says "undefined type path" when no such child exists —
            // verified, with the child-type case compiling clean as the control. Offering members
            // here handed the user a completion that cannot build.
            case TokenKind.Dot when PathBefore(tree, document, tokens, index) is { } written:
                return ChildTypes(written);

            case TokenKind.Dot:
            case TokenKind.QuestionDot:
                return Members(tree, document, tokens, index, offset, false, fileText, cancellationToken, documentOnly, limit);

            case TokenKind.Colon:
            case TokenKind.QuestionColon:
                return Members(tree, document, tokens, index, offset, true, fileText, cancellationToken, documentOnly, limit);

            case TokenKind.Slash:
                return TypePaths(tree, tokens, index);

            // `as ` takes a CLOSED vocabulary of input-type filters, so the list is exact rather
            // than drawn from the tree. A `|` continues one — `as null|anything` — so the same
            // list is offered after the pipe.
            case TokenKind.KeywordAs:
            case TokenKind.Pipe when FollowsAnInputType(document, tokens, index):
                return InputTypes();

            default:
                return Identifiers(tree, document, offset, macros, fileText, documentOnly, limit);
        }
    }

    /// <summary>
    /// The input-type filters an <c>as</c> clause accepts, as a completion list.
    /// </summary>
    /// <remarks>
    /// Marked builtin because BYOND defines the vocabulary and no project can add to it, and given
    /// no rank of its own — the list is closed and short, so the order is the reference's rather
    /// than a scope distance that means nothing here.
    /// </remarks>
    private static CompletionResult InputTypes()
    {
        List<CompletionItem> items = new(SyntaxFacts.InputTypes.Length);

        foreach (string name in SyntaxFacts.InputTypes)
        {
            items.Add(new CompletionItem(
                name, CompletionKind.Keyword, "input filter", isBuiltin: true));
        }

        return new CompletionResult(CompletionContext.InputType, items);
    }

    /// <summary>
    /// Whether a <c>|</c> is continuing an <c>as</c> clause rather than being bitwise-or.
    /// </summary>
    /// <remarks>
    /// Decided by the name immediately before it being one of the filters, which is what
    /// <c>as null|anything</c> looks like. A bitwise <c>|</c> over a variable that happens to be
    /// named <c>text</c> would be misread, and that is accepted: offering eighteen keywords is a
    /// smaller error than missing the clause, and the alternative is tracking clause state through
    /// the lexer for a list nobody is harmed by seeing.
    /// </remarks>
    private static bool FollowsAnInputType(Document document, IReadOnlyList<Token> tokens, int pipeIndex)
    {
        for (int i = pipeIndex - 1; i >= 0; i--)
        {
            if (tokens[i].Kind is TokenKind.Comment)
                continue;

            if (tokens[i].Kind is not (TokenKind.Identifier or TokenKind.KeywordNull))
                return false;

            string name = document.Text.ToString(tokens[i].Span);

            return Array.IndexOf(SyntaxFacts.InputTypes, name) >= 0;
        }

        return false;
    }

    /// <summary>
    /// Whether the token before a <c>.</c> can end a value — a name, <c>)</c>, <c>]</c>, a literal.
    /// A dot after anything else opens no member access: it is the return-value variable, or the
    /// start of a leading-dot relative path.
    /// </summary>
    private static bool HasValueBefore(IReadOnlyList<Token> tokens, int operatorIndex)
    {
        for (int i = operatorIndex - 1; i >= 0; i--)
        {
            switch (tokens[i].Kind)
            {
                case TokenKind.Comment:
                    continue;

                case TokenKind.CloseParen:
                case TokenKind.CloseBracket:
                case TokenKind.StringEnd:
                case TokenKind.Number:
                    return true;

                default:
                    return IsName(tokens[i].Kind);
            }
        }

        return false;
    }

    /// <summary>
    /// The completion list with no documentation attached, for a client that resolves lazily.
    /// </summary>
    /// <remarks>
    /// Identical to <c>CompleteAt</c>
    /// except that no item carries a doc comment. Pair it with
    /// <see cref="ResolveDocumentation"/> when the user highlights one.
    /// </remarks>
    public static CompletionResult CompleteBriefAt(
        ObjectTree tree,
        Document document,
        int line,
        int character,
        IReadOnlyCollection<string>? macros = null,
        PositionEncoding encoding = PositionEncoding.Utf16,
        CancellationToken cancellationToken = default,
        int limit = 0)
        => CompleteAt(tree, document, line, character, macros, fileText: null, encoding,
            cancellationToken, documentOnly: null, limit);

    /// <summary>
    /// The documentation for one item of the list a position offers, or empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Stateless by design: the position and the item's name identify it, so nothing is retained
    /// between the list call and this one and a stale handle is impossible. DM has no overloads,
    /// so a name at a position is unambiguous.
    /// </para>
    /// <para>
    /// The list is rebuilt to find the item, which costs the scope walk again but exactly ONE doc
    /// lookup — the file read and comment scan that lazy resolve exists to defer.
    /// </para>
    /// </remarks>
    public static string ResolveDocumentation(
        ObjectTree tree,
        Document document,
        int line,
        int character,
        string name,
        IReadOnlyCollection<string>? macros = null,
        Func<string, SourceText?>? fileText = null,
        PositionEncoding encoding = PositionEncoding.Utf16,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);

        CompletionResult result = CompleteAt(
            tree, document, line, character, macros, fileText, encoding, cancellationToken,
            documentOnly: name);

        foreach (CompletionItem item in result.Items)
        {
            if (string.Equals(item.Name, name, StringComparison.Ordinal))
                return item.Documentation;
        }

        return string.Empty;
    }

    /// <summary>
    /// Scope-distance bands, nearest first. A builtin sinks to the bottom whatever band it sits in:
    /// BYOND's own members are the least likely thing a user is reaching for by name.
    /// </summary>
    private static class Rank
    {
        public const int Local = 0;
        public const int Parameter = 1;
        public const int Member = 2;
        public const int Global = 3;
        public const int Macro = 4;
        public const int Builtin = 5;
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
        CancellationToken cancellationToken,
        string? documentOnly = null,
        int limit = 0)
    {
        TypeSymbol? receiver = ResolveReceiver(
            tree, document, tokens, operatorIndex - 1, offset, out TypeSource typeSource);

        bool inferred = typeSource is not (TypeSource.Written or TypeSource.None);

        CompletionContext context = widen ? CompletionContext.SubtypeMember : CompletionContext.Member;

        if (receiver is null)
            return new CompletionResult(context, Array.Empty<CompletionItem>());

        Dictionary<string, CompletionItem> items = new(StringComparer.Ordinal);

        // The declared type and everything it inherits, in both modes. An inferred receiver marks
        // every item: the whole list rides on inference dm.exe does not do.
        foreach (TypeSymbol step in tree.InheritanceChain(receiver))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddMembers(items, step, fileText, inferred, documentOnly, Rank.Member, typeSource);
        }

        // `:` also reaches members declared on subtypes, which is what makes it a wider check
        // rather than an absent one.
        if (widen)
        {
            foreach (TypeSymbol descendant in Descendants(receiver))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AddMembers(items, descendant, fileText, inferred, documentOnly);
            }
        }

        return Capped(context, Sorted(items), limit);
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
        Dictionary<string, CompletionItem> items, TypeSymbol type, Func<string, SourceText?>? fileText,
        bool inferred = false, string? documentOnly = null, int rank = Rank.Member,
        TypeSource typeSource = TypeSource.None)
    {
        foreach (VarSymbol variable in type.Vars)
        {
            items.TryAdd(variable.Name, new CompletionItem(
                variable.Name,
                CompletionKind.Variable,
                type.Path.Text,
                variable.IsBuiltin,
                Wanted(documentOnly, variable.Name)
                    ? DocumentationFor(variable.Site, variable.IsBuiltin, fileText)
                    : string.Empty,
                inferred,
                variable.IsBuiltin ? Rank.Builtin : rank,
                variable.DeclaredType?.Text ?? string.Empty,
                variable.InitialValue,
                typeSource,
                variable.ConstantValue));
        }

        foreach (ProcSymbol proc in type.Procs)
        {
            items.TryAdd(proc.Name, new CompletionItem(
                proc.Name,
                proc.IsVerb ? CompletionKind.Verb : CompletionKind.Proc,
                $"{type.Path.Text}  ({string.Join(", ", proc.Parameters)})",
                proc.IsBuiltin,
                proc.Sites.Count > 0 && Wanted(documentOnly, proc.Name)
                    ? DocumentationFor(proc.Sites[0], proc.IsBuiltin, fileText)
                    : string.Empty,
                inferred,
                proc.IsBuiltin ? Rank.Builtin : rank));
        }
    }

    /// <summary>
    /// Whether this item's documentation is being collected: everything, or one named item.
    /// </summary>
    /// <remarks>
    /// A doc lookup walks back over the comment lines above a declaration, in text the workspace
    /// has already cached — cheap individually, which is why skipping 19,897 of them saves bytes
    /// rather than milliseconds. Measured; see <c>CompleteAt</c>'s remarks.
    /// </remarks>
    private static bool Wanted(string? documentOnly, string name)
        => documentOnly is null || string.Equals(documentOnly, name, StringComparison.Ordinal);

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
        => ResolveReceiver(tree, document, tokens, index, offset, out TypeSource _);

    /// <summary>
    /// As above, also reporting whether the receiver's type was <b>inferred</b> rather than
    /// written — the one place completion knowingly goes further than <c>dm.exe</c>, which checks
    /// only a written type. Everything offered through an inferred receiver carries the fact.
    /// </summary>
    internal static TypeSymbol? ResolveReceiver(
        ObjectTree tree, Document document, IReadOnlyList<Token> tokens, int index, int offset,
        out bool inferred)
    {
        TypeSymbol? found = ResolveReceiver(
            tree, document, tokens, index, offset, out TypeSource source);

        inferred = source is not (TypeSource.Written or TypeSource.None);
        return found;
    }

    /// <summary>
    /// As above, reporting WHICH route produced the receiver's type rather than only whether
    /// <c>dm.exe</c> would refuse it.
    /// </summary>
    internal static TypeSymbol? ResolveReceiver(
        ObjectTree tree, Document document, IReadOnlyList<Token> tokens, int index, int offset,
        out TypeSource source)
    {
        source = TypeSource.None;

        if (index < 0)
            return null;

        // `src` and `usr` are both WRITTEN in the sense that matters: dm.exe checks members
        // through them, so nothing here rides on inference.
        if (tokens[index].Kind == TokenKind.KeywordSrc)
        {
            source = TypeSource.Written;
            return EnclosingType(tree, document, offset);
        }

        // `usr` is always a /mob, and unlike `src` it does NOT take the enclosing type — verified
        // by compiling `usr.key` inside a proc on /obj, which resolves a /mob-only var, with
        // `usr.nonexistent_xyz` as the control that says the compiler is checking at all.
        if (tokens[index].Kind == TokenKind.KeywordUsr)
        {
            source = TypeSource.Written;
            return tree.Find(UsrType);
        }

        // `new /obj/item(args).` — the constructed object's type is WRITTEN two tokens back, so
        // this resolves exactly. dm.exe does not check it (the receiver is `<expression>` to it,
        // so any member existing anywhere compiles) and the runtime then RAISES: verified,
        // `new /mob/test(1).elsewhere` is "undefined variable /mob/test/var/elsewhere". Offering
        // /obj/item's members is therefore both more useful than nothing AND sound - it is the one
        // member list that will not crash.
        if (tokens[index].Kind == TokenKind.CloseParen
            && NewExpressionPath(tree, document, tokens, index) is { } constructed)
        {
            source = TypeSource.Written;
            return constructed;
        }

        if (!IsName(tokens[index].Kind))
            return null;

        // Walk back over a `name <sep> name` run, recording whether any separator was a `/`.
        int start = index;
        bool sawSlash = false;

        while (start > 0 && tokens[start - 1].Kind is TokenKind.Slash or TokenKind.Dot
               && start - 2 >= 0 && IsName(tokens[start - 2].Kind))
        {
            sawSlash |= tokens[start - 1].Kind == TokenKind.Slash;
            start -= 2;
        }

        bool leadingSeparator = start > 0
            && tokens[start - 1].Kind is TokenKind.Slash or TokenKind.Dot
            && (start - 2 < 0 || !IsName(tokens[start - 2].Kind));

        // §4a context 3, and this is the rule the run has to be tested against rather than
        // assumed: NO LEADING SEPARATOR MEANS IT IS NOT A PATH AT ALL. `m.friend` is the var
        // `friend` on whatever `m` holds, not the type `/m/friend` — so a dot-only run with
        // nothing in front of it is member access and is walked one member at a time.
        //
        // Folding it into a path instead is what made every two-level chain answer nothing:
        // `src.client.`, `usr.client.` and `m.friend.` alike, whether the vars were builtin or
        // written in the project. A single receiver worked, so it read as a builtins problem.
        if (start < index && !sawSlash && !leadingSeparator)
        {
            TypeSymbol? current = ResolveReceiver(tree, document, tokens, start, offset, out source);

            for (int i = start + 2; i <= index && current is not null; i += 2)
            {
                string member = document.Text.ToString(tokens[i].Span);

                current = tree.ResolveVar(current, member) is { DeclaredType: { } memberType }
                    ? tree.Find(memberType)
                    : null;
            }

            // An unresolved link breaks the chain rather than falling back to a path lookup: a
            // wrong list here is worse than none, and dm.exe rejects the member outright.
            return current;
        }

        // A written path: `/obj/item`, `obj/item`, or the relative `.item/sword`.
        if (start < index || leadingSeparator)
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

        // A local or parameter carries its declared type — or, failing that, an inferred one.
        if (FindLocalType(document, offset, name, out source) is { } localType)
            return tree.Find(localType);

        // No local answered; the source must not leak onto the written-type branches below.
        source = TypeSource.None;

        // A var on the enclosing type resolves normally: dm.exe checks members through it.
        if (EnclosingType(tree, document, offset) is { } enclosing
            && tree.ResolveVar(enclosing, name) is { DeclaredType: { } declared })
        {
            source = TypeSource.Written;
            return tree.Find(declared);
        }

        // A GLOBAL. Root vars are where dm.exe looks after locals and members — `world` lives here
        // too, as a root var typed /world. This lookup replaced the bare-type-name fallback
        // (`mob.` offering /mob's members): a bare type name is "undefined var" to dm.exe — §4a
        // context 3 needs a LEADING separator for a path — while a global NAMED after a type
        // (`var/mob/mob`, idiomatic DM) resolves through the var. The fallback answered that
        // shadow case right by coincidence and hid that globals never resolved here at all:
        // `machine.` on a root `var/obj/machine` was 0 items while dm.exe compiles the access.
        if (tree.Root.FindVar(name) is { DeclaredType: { } globalType })
        {
            source = TypeSource.Written;
            return tree.Find(globalType);
        }

        return null;
    }

    /// <summary>
    /// The type of <c>usr</c>. Always <c>/mob</c>, and deliberately not the enclosing type the way
    /// <c>src</c> is — see the note at its use site for the compiler evidence.
    /// </summary>
    private static TypePath UsrType => TypePath.Parse("/mob");

    internal static bool IsName(TokenKind kind) => kind
        is TokenKind.Identifier or TokenKind.KeywordSrc or TokenKind.KeywordUsr
        or TokenKind.KeywordWorld or TokenKind.KeywordGlobal;

    // -- scope --------------------------------------------------------------

    private static CompletionResult Identifiers(
        ObjectTree tree,
        Document document,
        int offset,
        IReadOnlyCollection<string>? macros,
        Func<string, SourceText?>? fileText,
        string? documentOnly = null,
        int limit = 0)
    {
        Dictionary<string, CompletionItem> items = new(StringComparer.Ordinal);

        // Nearest first: locals and parameters shadow members, which shadow globals.
        if (FindEnclosingProc(document, offset) is { } proc)
        {
            foreach (ParameterSyntax parameter in proc.Parameters)
            {
                // The `as` clause is NOT folded into the declared type: `f(n as num)` leaves n
                // untyped as far as dm.exe is concerned (§8), so reporting `num` as a type would
                // claim something the compiler does not hold.
                items.TryAdd(parameter.Name, new CompletionItem(
                    parameter.Name, CompletionKind.Parameter, parameter.DeclaredType?.Text ?? string.Empty, false,
                    rank: Rank.Parameter,
                    declaredType: parameter.DeclaredType?.Text ?? string.Empty,
                    initialValue: parameter.DefaultValue is { } given
                        ? document.Text.ToString(given.Span)
                        : string.Empty));
            }

            foreach (LocalVarStatementSyntax local in Locals(proc, offset))
            {
                items[local.Name] = new CompletionItem(
                    local.Name, CompletionKind.Local, local.DeclaredType?.Text ?? string.Empty, false,
                    rank: Rank.Local,
                    declaredType: local.DeclaredType?.Text ?? string.Empty,
                    initialValue: local.Initializer is { } value
                        ? document.Text.ToString(value.Span)
                        : string.Empty);
            }
        }

        if (EnclosingType(tree, document, offset) is { } enclosing)
        {
            foreach (TypeSymbol step in tree.InheritanceChain(enclosing))
                AddMembers(items, step, fileText, inferred: false, documentOnly, Rank.Member);
        }

        // Globals last. These are the root's procs and vars, which is where the builtins live.
        AddMembers(items, tree.Root, fileText, inferred: false, documentOnly, Rank.Global);

        // Macros do not live on any type - the preprocessor has removed them long before the parser
        // runs - so they are carried in separately and go last, behind anything really in scope.
        if (macros is not null)
        {
            foreach (string macro in macros)
                items.TryAdd(macro, new CompletionItem(macro, CompletionKind.Macro, "macro", false, rank: Rank.Macro));
        }

        return Capped(CompletionContext.Identifier, Sorted(items), limit);
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

    /// <remarks>
    /// Internal so <see cref="InlayHintService"/> walks bodies with the same coverage the scope
    /// chain uses — a second walker that missed a statement kind would silently hint nothing there.
    /// </remarks>
    internal static IEnumerable<StatementSyntax> Flatten(StatementSyntax statement)
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
    /// <remarks>
    /// Internal because <see cref="InlayHintService"/> shows the same inference this feeds
    /// completion with — a second copy of the local-type rules would drift.
    /// </remarks>
    internal static TypePath? FindLocalType(Document document, int offset, string name, out bool inferred)
    {
        TypePath? found = FindLocalType(document, offset, name, out TypeSource source);

        inferred = source is not (TypeSource.Written or TypeSource.None);
        return found;
    }

    /// <summary>
    /// As above, reporting WHICH route produced the type rather than only whether dm.exe would
    /// refuse it.
    /// </summary>
    /// <remarks>
    /// The two answers differ on the <c>as</c> clause: it is written down by the author and is
    /// still not a type the compiler checks members through, so a client that renders "inferred"
    /// there is telling someone their own words were a guess.
    /// </remarks>
    internal static TypePath? FindLocalType(
        Document document, int offset, string name, out TypeSource source)
    {
        source = TypeSource.None;

        if (FindEnclosingProc(document, offset) is not { } proc)
            return null;

        foreach (LocalVarStatementSyntax local in Locals(proc, offset))
        {
            if (!string.Equals(local.Name, name, StringComparison.Ordinal))
                continue;

            // A written type, or the /list that brackets give a declaration — both are types
            // dm.exe checks members through, so both report Written and neither is inference.
            // The bracket half was missing until 2026-08-15: `var/players[0]` then `players.`
            // answered nothing on every surface sharing this walk, while the binder and the tree
            // had known the rule since 2026-08-14. `DeclaredType.Of` is now the one copy.
            if (DeclaredType.Of(local.DeclaredType, local.HasBrackets) is { } type)
            {
                source = TypeSource.Written;
                return type;
            }

            // Everything past this line is inference the compiler does not do: dm.exe checks only
            // a written type, so an answer from here is offered knowing the build would refuse it.

            // An untyped local. The most recent assignment before the cursor describes what the
            // name holds *here*, so it beats the initialiser rather than the other way round.
            if (LastAssignedType(document, proc, offset, name) is { } assigned)
            {
                source = TypeSource.Assignment;
                return assigned;
            }

            TypePath? fromInitializer = Infer(document, proc, offset, local.Initializer, name);

            // Only claim a source when there is actually an answer - Infer returns null for a
            // local nothing types, and reporting Initializer there would name a route that
            // produced nothing.
            source = fromInitializer is null ? TypeSource.None : TypeSource.Initializer;
            return fromInitializer;
        }

        foreach (ParameterSyntax parameter in proc.Parameters)
        {
            if (!string.Equals(parameter.Name, name, StringComparison.Ordinal))
                continue;

            if (parameter.DeclaredType is { } type)
            {
                source = TypeSource.Written;
                return TypePath.FromSegments(type.Segments);
            }

            // `f(M as mob)` says what M is without declaring a path — an input filter, not a type
            // annotation, so this too is beyond what the compiler checks (language notes). It is
            // the one route the author WROTE and dm.exe still refuses, which is why the source is
            // reported separately from the flag.
            TypePath? fromFilter = TypeInference.FromInputType(parameter.InputType);

            source = fromFilter is null ? TypeSource.None : TypeSource.InputFilter;
            return fromFilter;
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
                : FindLocalType(document, offset, referenced, out TypeSource _));

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
    /// <summary>
    /// The states of the icon the enclosing type uses, when the caret sits inside an
    /// <c>icon_state = "…"</c> string. Null when that is not where we are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things have to line up, and each is checked rather than assumed: the caret is inside a
    /// STRING, that string is the right-hand side of an assignment to <c>icon_state</c>, and the
    /// enclosing type resolves an <c>icon</c> var to a resource literal. Any of them missing means
    /// this is not the context and the ordinary completion runs.
    /// </para>
    /// <para>
    /// The icon is read through <see cref="ObjectTree.IconStates"/>, which a shell supplies —
    /// <c>Dm.Core</c> cannot read a <c>.dmi</c>. With no reader the context is still REPORTED with
    /// an empty list, so a client can tell "no states" from "not an icon_state", exactly as the
    /// bare-`.` ReturnValue context does.
    /// </para>
    /// <para>
    /// The var is looked up through the inheritance chain, so a subtype that sets only
    /// <c>icon_state</c> still finds the <c>icon</c> its parent declared — which is how DM is
    /// actually written.
    /// </para>
    /// </remarks>
    private static CompletionResult? IconStateCompletion(
        ObjectTree tree, Document document, IReadOnlyList<Token> tokens, int index, int offset)
    {
        // A string is a RUN of tokens — StringStart, StringText, StringEnd — so the caret inside
        // one sits after the start or after some text. Walk back to the opening quote.
        if (index < 0 || tokens[index].Kind is not (TokenKind.StringStart or TokenKind.StringText))
            return null;

        int start = index;

        while (start >= 0 && tokens[start].Kind == TokenKind.StringText)
            start--;

        if (start < 0 || tokens[start].Kind != TokenKind.StringStart)
            return null;

        int assign = start - 1;

        while (assign >= 0 && tokens[assign].Kind is TokenKind.Newline or TokenKind.Comment)
            assign--;

        if (assign < 0 || tokens[assign].Kind != TokenKind.Assign)
            return null;

        int name = assign - 1;

        while (name >= 0 && tokens[name].Kind is TokenKind.Newline or TokenKind.Comment)
            name--;

        if (name < 0
            || tokens[name].Kind != TokenKind.Identifier
            || !string.Equals(document.Text.ToString(tokens[name].Span), "icon_state", StringComparison.Ordinal))
        {
            return null;
        }

        if (EnclosingType(tree, document, offset) is not { } enclosing)
            return new CompletionResult(CompletionContext.IconState, Array.Empty<CompletionItem>());

        string? resource = null;

        foreach (TypeSymbol step in tree.InheritanceChain(enclosing))
        {
            if (step.FindVar("icon") is { InitialValue.Length: > 0 } icon)
            {
                resource = ResourcePath(icon.InitialValue);
                break;
            }
        }

        if (resource is null || tree.IconStates is not { } read)
            return new CompletionResult(CompletionContext.IconState, Array.Empty<CompletionItem>());

        List<CompletionItem> items = new();

        foreach (string state in read(resource))
        {
            // The empty name is the DEFAULT state and is completely ordinary — 226 of 352 real
            // icons carry one. It completes to the empty string, which is what the author types.
            items.Add(new CompletionItem(
                state,
                CompletionKind.Value,
                state.Length == 0 ? $"(default state) {resource}" : resource,
                isBuiltin: false));
        }

        return new CompletionResult(CompletionContext.IconState, items);
    }

    /// <summary>
    /// The path out of a resource literal — <c>'icons/mob.dmi'</c> gives <c>icons/mob.dmi</c>.
    /// Null for anything that is not one, such as a var assigned from another var.
    /// </summary>
    private static string? ResourcePath(string initialValue)
    {
        string text = initialValue.Trim();

        return text.Length >= 2 && text[0] == '\'' && text[^1] == '\''
            ? text[1..^1]
            : null;
    }

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

    /// <summary>
    /// The type constructed by a <c>new /path(...)</c> ending at <paramref name="closeIndex"/>.
    /// </summary>
    /// <remarks>
    /// Matched by bracket depth rather than by scanning for the nearest <c>(</c>, so nested calls
    /// and parenthesised arguments do not throw it off. Returns null unless the whole shape is
    /// there: a balanced group, a path in front of it, and <c>new</c> in front of that.
    /// </remarks>
    private static TypeSymbol? NewExpressionPath(
        ObjectTree tree, Document document, IReadOnlyList<Token> tokens, int closeIndex)
    {
        int depth = 0;
        int open = -1;

        for (int i = closeIndex; i >= 0; i--)
        {
            if (tokens[i].Kind == TokenKind.CloseParen)
            {
                depth++;
            }
            else if (tokens[i].Kind == TokenKind.OpenParen)
            {
                depth--;

                if (depth == 0)
                {
                    open = i;
                    break;
                }
            }
        }

        if (open <= 0)
            return null;

        // The path sits between `new` and the `(`, written with either separator.
        int end = open - 1;

        if (end < 0 || !IsName(tokens[end].Kind))
            return null;

        int start = end;

        while (start > 0 && tokens[start - 1].Kind is TokenKind.Slash or TokenKind.Dot
               && start - 2 >= 0 && IsName(tokens[start - 2].Kind))
        {
            start -= 2;
        }

        // A leading separator is part of the path, and `new` must be what precedes it.
        int beforePath = start - 1;

        if (beforePath >= 0 && tokens[beforePath].Kind is TokenKind.Slash or TokenKind.Dot)
            beforePath--;

        if (beforePath < 0 || tokens[beforePath].Kind != TokenKind.KeywordNew)
            return null;

        List<string> segments = new();

        for (int i = start; i <= end; i += 2)
            segments.Add(document.Text.ToString(tokens[i].Span));

        return tree.Find(TypePath.FromSegments(segments));
    }

    /// <summary>
    /// The type a written path before a <c>.</c> names, or null when the run is not a path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A path is a run with a LEADING separator (<c>/obj/item.</c>, and the relative <c>.item/x.</c>)
    /// or one written with <c>/</c> somewhere in it. A bare dotted run is not a path at all —
    /// §4a context 3 — so <c>m.friend.</c> stays member access and only genuine paths land here.
    /// </para>
    /// <para>
    /// A BARE TYPE NAME is deliberately excluded even though <c>mob.hp</c> is equally
    /// uncompilable ("undefined var" — bare <c>mob</c> is neither a variable nor a path). That is
    /// PLAN §1's acceptance target and removing it is a product decision rather than a bug fix, so
    /// it keeps offering members and is marked inferred instead — see <see cref="Members"/>.
    /// </para>
    /// </remarks>
    private static TypeSymbol? PathBefore(
        ObjectTree tree, Document document, IReadOnlyList<Token> tokens, int dotIndex)
    {
        int index = dotIndex - 1;

        if (index < 0 || !IsName(tokens[index].Kind))
            return null;

        int start = index;
        bool sawSlash = false;

        while (start > 0 && tokens[start - 1].Kind is TokenKind.Slash or TokenKind.Dot
               && start - 2 >= 0 && IsName(tokens[start - 2].Kind))
        {
            sawSlash |= tokens[start - 1].Kind == TokenKind.Slash;
            start -= 2;
        }

        bool leadingSeparator = start > 0
            && tokens[start - 1].Kind is TokenKind.Slash or TokenKind.Dot
            && (start - 2 < 0 || !IsName(tokens[start - 2].Kind));

        if (!sawSlash && !leadingSeparator)
            return null;

        return ResolveReceiver(tree, document, tokens, index, tokens[index].Span.End, out TypeSource _);
    }

    /// <summary>The children of a type, as path segments rather than members.</summary>
    private static CompletionResult ChildTypes(TypeSymbol type)
    {
        List<CompletionItem> items = new();

        foreach (TypeSymbol child in type.Children)
            items.Add(new CompletionItem(child.Name, CompletionKind.Type, child.Path.Text, child.IsBuiltin));

        items.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return new CompletionResult(CompletionContext.TypePath, items);
    }

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

    /// <summary>
    /// Scope distance, then name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Alphabetical alone put <c>abs()</c> — a builtin nobody asked for — above a local the user
    /// declared two lines up. The ranking a query-driven picker uses (exact, then prefix, then
    /// substring, as <see cref="WorkspaceSymbolService"/> does) cannot help here: a bare identifier
    /// position has no query string to rank against. Scope distance is the information this
    /// position does have.
    /// </para>
    /// <para>
    /// Order is the whole contract — nothing crosses the ABI saying why. A client that preserves
    /// the order we return gets the ranking for free; the LSP shell writes it into
    /// <c>sortText</c>, which is how a server pins order in VS Code.
    /// </para>
    /// </remarks>
    private static List<CompletionItem> Sorted(Dictionary<string, CompletionItem> items)
    {
        List<CompletionItem> sorted = new(items.Values);

        sorted.Sort(static (a, b) => a.Rank != b.Rank
            ? a.Rank.CompareTo(b.Rank)
            : string.CompareOrdinal(a.Name, b.Name));

        return sorted;
    }

    /// <summary>Applies a caller's cap, reporting rather than implying that it cut the list.</summary>
    private static CompletionResult Capped(CompletionContext context, List<CompletionItem> items, int limit)
    {
        if (limit <= 0 || items.Count <= limit)
            return new CompletionResult(context, items);

        return new CompletionResult(context, items.GetRange(0, limit), truncated: true);
    }
}
