using System;
using System.Collections.Generic;
using Dm.Core.Diagnostics;
using Dm.Core.Services;
using Dm.Core.Symbols;
using Dm.Core.Syntax;
using Dm.Core.Text;

namespace Dm.Core.Binding;

/// <summary>
/// Walks a parsed file against the finished object tree and reports what does not resolve.
/// </summary>
/// <remarks>
/// <para>
/// <b>The governing constraint is that we never invent a diagnostic.</b> A project that builds
/// clean while we complain is a tool nobody trusts, so every rule here is the conservative half of
/// a compiler-verified behaviour: where the answer is not certain the check is skipped, and each
/// skip says why. Missing diagnostics are work outstanding; invented ones are work done wrong.
/// <c>dmc diagdiff</c> measures both columns against <c>dm.exe</c>.
/// </para>
/// <para>
/// <b>This does not use <see cref="TypeInference"/>, deliberately.</b> Inference exists so
/// completion can offer members for a half-written declaration, and it knowingly goes further than
/// the compiler. Diagnostics are the opposite job: dm.exe performs no local inference at all, so
/// <c>var/x = new /obj/item</c> gives <c>x</c> no type and every member of it is an error. Checking
/// an inferred type would report errors on code that compiles, and checking against inference's
/// answer would miss the ones dm.exe raises. Only a <b>written</b> type is ever checked here.
/// PLAN.md §8 has the matrix.
/// </para>
/// </remarks>
public sealed class Binder
{
    private readonly ObjectTree _tree;
    private readonly string? _file;
    private readonly List<Diagnostic> _diagnostics = new();

    // The reference sink. When attached, every name this walk RESOLVES is reported to it — the
    // same resolution, so the index and the diagnostics cannot disagree about what a name means.
    // Null on the diagnostics-only path, where every sink branch short-circuits to nothing.
    private readonly Action<Services.Reference>? _sink;

    // True while binding a type-level or global var's initializer, where `usr` does not exist
    // (probed — see BindDeclarations). Single-threaded walk, so a plain field.
    private bool _inTypeInitializer;

    // The sites resolution deliberately stops at — `:`-family accesses and `.` through an unwritten
    // type — for a caller that needs to know where proof ends rather than only where it succeeds.
    private readonly Action<Services.UncertainSite>? _uncertain;
    private readonly string? _referenceName;
    private string _inside = "/";

    /// <summary>Whether the proc being walked has nothing for <c>..()</c> to reach.</summary>
    private bool _parentless;

    /// <summary>
    /// Whether <c>#pragma ignore</c> silences this warning at this point in the file.
    /// </summary>
    /// <remarks>
    /// Only warnings carrying the compiler's own NAME can be silenced, which is the point of using
    /// those names: <c>#pragma ignore no_parent</c> is the project telling us, in the compiler's own
    /// vocabulary, not to say it. Our private <c>DM0xxx</c> ids have no pragma and are never
    /// suppressed here — they are the things dm.exe has no name for.
    /// </remarks>
    private bool Silenced(string id, TextSpan span)
        => _tree.SuppressedWarnings is { IsEmpty: false } levels
            && levels.IsIgnored(_file, span.Start, id);

    /// <summary>Every local declared in the proc being bound, in declaration order.</summary>
    private readonly List<LocalRecord> _procLocals = new();

    /// <summary>Labels declared in the proc being bound, with the span to report at.</summary>
    private readonly List<(string Name, TextSpan Span)> _procLabels = new();

    /// <summary>Label names some `break`, `continue` or `goto` NAMES. A bare `break` is not one.</summary>
    private readonly HashSet<string> _labelUses = new(StringComparer.Ordinal);

    /// <summary>Set while binding a `for` header, so its variable is recorded as exempt.</summary>
    private bool _declaringLoopVariable;

    private Binder(ObjectTree tree, string? file, Action<Services.Reference>? sink, string? referenceName,
        Action<Services.UncertainSite>? uncertain)
    {
        _tree = tree;
        _file = file;
        _sink = sink;
        _referenceName = referenceName;
        _uncertain = uncertain;
    }

    /// <summary>Binds one file's declarations and returns what did not resolve.</summary>
    /// <param name="tree">The finished object tree.</param>
    /// <param name="root">The file's parse.</param>
    /// <param name="file">
    /// The path the tree's declaration sites record for this file, when the caller knows it. Only
    /// the duplicate-definition check reads it — a same-file "previous definition" can then be
    /// anchored where dm.exe anchors it. Null degrades to reporting the duplicate half alone.
    /// </param>
    /// <param name="sink">
    /// Receives a <see cref="Services.Reference"/> for every name the walk resolves, when the
    /// caller is building the reference index. Emission never changes the diagnostics.
    /// </param>
    /// <param name="referenceName">
    /// A bare-name prefilter for the sink: only occurrences of this name are emitted, which lets a
    /// single-symbol query skip resolution work on every other name in the project.
    /// </param>
    /// <param name="uncertain">
    /// Receives the member accesses the walk deliberately does NOT resolve — `:`-family lookups
    /// and `.` through a receiver with no written type — when they carry the filtered name. This
    /// is what rename stands on: an edit needs proof, and these are exactly the sites where proof
    /// stops. Filtered by <paramref name="referenceName"/> the same way the sink is.
    /// </param>
    public static IReadOnlyList<Diagnostic> Bind(
        ObjectTree tree,
        FileSyntax root,
        string? file = null,
        Action<Services.Reference>? sink = null,
        string? referenceName = null,
        Action<Services.UncertainSite>? uncertain = null)
    {
        Binder binder = new(tree, file, sink, referenceName, uncertain);
        binder.BindDeclarations(root.Declarations, TypePath.Root);

        return binder._diagnostics;
    }

    private void BindDeclarations(IReadOnlyList<DeclarationSyntax> declarations, TypePath enclosing)
    {
        foreach (DeclarationSyntax declaration in declarations)
        {
            switch (declaration)
            {
                // A bare `var`/`proc` header declares nothing itself — but it can still carry a type
                // path in front of the keyword, and `mob/pc/verb` heads a block of verbs on
                // /mob/pc. Passing the enclosing path through unchanged puts every child on the
                // root, which is exactly what made `src` resolve to `/` on real code.
                case TypeDeclarationSyntax { IsGroupHeader: true } group:
                    BindDeclarations(group.Members, TypeTreeBuilder.GroupOwner(enclosing, group.Path));
                    break;

                case TypeDeclarationSyntax type:
                    BindDeclarations(type.Members, PathOf(type.Path, enclosing));
                    break;

                case VarDeclarationSyntax variable:
                    // A type-level initialiser runs with the enclosing type as `src` — and with
                    // NO `usr`: probed 2026-08-13, `usr` in a datum var initialiser, a global
                    // var initialiser and a bare override initialiser (`/world/name = usr`) are
                    // all dm.exe's "usr: undefined var". The flag is what lets BindIdentifier
                    // report it here while proc bodies keep the whitelist.
                    _inside = enclosing.IsRoot ? "/" : enclosing.Text;
                    CheckDuplicateVar(variable, enclosing);
                    _inTypeInitializer = true;
                    BindExpression(variable.Initializer, new Scope(enclosing), invoked: false);
                    _inTypeInitializer = false;
                    break;

                case ProcDeclarationSyntax proc:
                    BindProc(proc, enclosing);
                    break;
            }
        }
    }

    private void BindProc(ProcDeclarationSyntax proc, TypePath enclosing)
    {
        TypePath owner = TypeTreeBuilder.ProcOwner(enclosing, proc.Path);

        if (proc.IsNewDeclaration)
            CheckDuplicateProc(proc, owner);
        else
            EmitOverrideReference(proc, owner);

        _inside = owner.IsRoot ? $"/{proc.Name}()" : $"{owner.Text}/{proc.Name}()";
        _parentless = HasNoParentProc(proc, owner);

        Scope scope = new(owner);
        _procLocals.Clear();
        _procLabels.Clear();
        _labelUses.Clear();

        foreach (ParameterSyntax parameter in proc.Parameters)
        {
            scope.Declare(new LocalRecord(
                parameter.Name,
                parameter.DeclaredType is { } declared
                    ? TypePath.FromSegments(declared.Segments)
                    : TypeInference.FromInputType(parameter.InputType),
                // ParameterSyntax carries no span, and needs none: a parameter is exempt, so this
                // record is only ever consulted for shadowing and for the read flag.
                default,
                LocalKind.Parameter));

            BindExpression(parameter.DefaultValue, scope, invoked: false);
        }

        if (proc.Body is null)
            return;

        BindStatement(proc.Body, scope);
        ReportUnusedLocals();
        ReportUnusedLabels();
    }

    /// <summary>
    /// dm.exe's `unused_label`: a label nothing names. Compiler-verified one case per proc —
    /// `break used`, `continue looped` and `goto target` each count, while a BARE `break` inside a
    /// labelled block does not, and a label sitting before a loop nothing names warns like any
    /// other. Labels live on their own line; `looped: for(...)` is a syntax error.
    /// </summary>
    private void ReportUnusedLabels()
    {
        foreach ((string name, TextSpan span) in _procLabels)
        {
            if (_labelUses.Contains(name) || Silenced("unused_label", span))
                continue;

            _diagnostics.Add(Diagnostic.Warning("unused_label", span, $"{name}: unused label"));
        }

        _procLabels.Clear();
        _labelUses.Clear();
    }

    /// <summary>
    /// dm.exe's `unused_var`, on the narrow set it actually covers: PROC LOCALS ONLY. A local
    /// never read warns, and so does a write-only one — a plain `x = 1` writes without reading.
    /// Silent: any read at all, a compound `x += 1`, an unused parameter, a `for` loop variable,
    /// a catch variable, and every type-level var. Pinned one case per proc against 516.1686 and
    /// recorded as fixture `errors/unused_var`.
    /// </summary>
    private void ReportUnusedLocals()
    {
        foreach (LocalRecord local in _procLocals)
        {
            if (local.Read || local.Kind != LocalKind.Local)
                continue;

            if (Silenced("unused_var", local.NameSpan))
                continue;

            _diagnostics.Add(Diagnostic.Warning(
                "unused_var", local.NameSpan, $"{local.Name}: variable defined but not used"));
        }

        _procLocals.Clear();
    }

    /// <summary>
    /// The var half of <c>DM0403</c>: a name declared twice on one type, or redeclared over an
    /// ancestor's or a builtin's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A BARE OVERRIDE IS NOT A DECLARATION. <c>/obj/item/hp = 3</c> re-assigns an inherited var
    /// and is ordinary DM, so only sites that wrote <c>var/</c> count — <see
    /// cref="VarSymbol.IsDeclaration"/>. Getting that wrong would fire on most of a real game.
    /// </para>
    /// <para>
    /// THE PAIR'S LINES ARE INVERTED for the same-type case, which is the opposite of the proc
    /// half and was probed rather than assumed: dm.exe puts "duplicate definition" on the FIRST
    /// declaration and "previous definition" on the second. The ancestor case is the normal way
    /// round, with the child reported as the duplicate.
    /// </para>
    /// </remarks>
    private void CheckDuplicateVar(VarDeclarationSyntax variable, TypePath enclosing)
    {
        if (!variable.InVarContext)
            return;

        TypePath owner = TypeTreeBuilder.VarOwner(enclosing, variable.Path);

        if (_tree.Find(owner) is not { } type)
            return;

        IReadOnlyList<DeclarationSite> sites = type.VarDeclaringSites(variable.Name);

        if (sites.Count > 1)
        {
            int index = -1;

            for (int i = 0; i < sites.Count; i++)
            {
                if (sites[i].Span == variable.Span && sites[i].NameSpan == variable.NameSpan)
                {
                    index = i;
                    break;
                }
            }

            // Inverted against the proc half: the FIRST site is the one dm.exe calls the duplicate.
            if (index == 0)
            {
                _diagnostics.Add(Diagnostic.Error(
                    "DM0403", variable.NameSpan, $"{variable.Name}: duplicate definition"));
            }
            else if (index > 0)
            {
                _diagnostics.Add(Diagnostic.Error(
                    "DM0403", variable.NameSpan, $"{variable.Name}: previous definition"));
            }

            return;
        }

        // This site is the type's sole declaration. When a DESCENDANT re-declares the name, this
        // line is the pair's "previous definition" half — dm.exe reports it here whatever file
        // the descendant sits in, and ONCE, however many descendants duplicate it (both probed).
        if (sites.Count == 1
            && sites[0].Span == variable.Span
            && sites[0].NameSpan == variable.NameSpan
            && _tree.VarRedeclaredBelow(owner, variable.Name))
        {
            _diagnostics.Add(Diagnostic.Error(
                "DM0403", variable.NameSpan, $"{variable.Name}: previous definition"));
        }

        // Redeclaring what an ancestor declares, at any depth. An ancestor that merely overrides is
        // not a declarer, so the walk continues past it to whoever is.
        foreach (TypeSymbol ancestor in _tree.InheritanceChain(type))
        {
            if (ReferenceEquals(ancestor, type) || ancestor.FindVar(variable.Name) is not { } above)
                continue;

            if (above.IsBuiltin)
            {
                _diagnostics.Add(Diagnostic.Error(
                    "DM0403", variable.NameSpan,
                    $"{variable.Name}: duplicate definition (conflicts with built-in variable)"));
                return;
            }

            if (ancestor.VarDeclaringSites(variable.Name).Count > 0)
            {
                _diagnostics.Add(Diagnostic.Error(
                    "DM0403", variable.NameSpan, $"{variable.Name}: duplicate definition"));
                return;
            }
        }
    }

    /// <summary>
    /// A <c>proc/</c> name declared twice is dm.exe's duplicate-definition error — on one type, on
    /// an ancestor at any depth, or against a builtin (§8, probes dup1–dup9). Overrides carry no
    /// marker and never trip this; a var and a proc may legally share a name.
    /// </summary>
    /// <remarks>
    /// dm.exe reports a pair: <i>"duplicate definition"</i> on the later declaration and
    /// <i>"previous definition"</i> on the first. Binding is per file, so each site reports its
    /// own half — the descendant's file carries the duplicate line, and the ancestor's own bind
    /// reports the previous line through <see cref="ObjectTree.ProcRedeclaredBelow"/>, whichever
    /// file the descendant sits in. That closed the check's one documented miss without the
    /// per-bind descendant scan it was deferred over: the tree carries the answer as a
    /// once-per-build index. A builtin has no line at all, and dm.exe accordingly reports a
    /// single error there.
    /// </remarks>
    private void CheckDuplicateProc(ProcDeclarationSyntax proc, TypePath owner)
    {
        if (_tree.Find(owner) is not TypeSymbol type)
            return;

        if (type.FindProc(proc.Name) is not ProcSymbol symbol)
            return;

        if (symbol.IsBuiltin)
        {
            _diagnostics.Add(Diagnostic.Error(
                "DM0403", proc.NameSpan,
                $"{proc.Name}: duplicate definition (conflicts with built-in proc)"));
            return;
        }

        if (symbol.DeclaringSites.Count > 1)
        {
            // Which declaring site this syntax node is. A site we cannot find is a model
            // mismatch, and the conservative answer is silence.
            int index = -1;

            for (int i = 0; i < symbol.DeclaringSites.Count; i++)
            {
                if (symbol.DeclaringSites[i].Span == proc.Span
                    && symbol.DeclaringSites[i].NameSpan == proc.NameSpan)
                {
                    index = i;
                    break;
                }
            }

            if (index > 0)
            {
                _diagnostics.Add(Diagnostic.Error(
                    "DM0403", proc.NameSpan, $"{proc.Name}: duplicate definition"));
            }
            else if (index == 0)
            {
                _diagnostics.Add(Diagnostic.Error(
                    "DM0403", proc.NameSpan, $"{proc.Name}: previous definition"));
            }

            return;
        }

        // This site is the type's sole declaration. When a DESCENDANT re-declares the name, this
        // line is the pair's "previous definition" half — dm.exe reports it here whatever file
        // the descendant sits in, and ONCE, however many descendants duplicate it (both probed).
        // Before the ancestor walk, because a middle link of a three-deep chain is both halves.
        if (symbol.DeclaringSites.Count == 1
            && symbol.DeclaringSites[0].Span == proc.Span
            && symbol.DeclaringSites[0].NameSpan == proc.NameSpan
            && _tree.ProcRedeclaredBelow(owner, proc.Name))
        {
            _diagnostics.Add(Diagnostic.Error(
                "DM0403", proc.NameSpan, $"{proc.Name}: previous definition"));
        }

        // The sole declaration on its own type: a redeclaration of something an ancestor already
        // declares, at any depth, is still a duplicate. An ancestor that merely overrides is not
        // a declarer — the walk continues to whoever is.
        foreach (TypeSymbol ancestor in _tree.InheritanceChain(type))
        {
            if (ReferenceEquals(ancestor, type) || ancestor.FindProc(proc.Name) is not ProcSymbol above)
                continue;

            if (above.IsBuiltin)
            {
                _diagnostics.Add(Diagnostic.Error(
                    "DM0403", proc.NameSpan,
                    $"{proc.Name}: duplicate definition (conflicts with built-in proc)"));
                return;
            }

            if (above.DeclaringCount > 0)
            {
                _diagnostics.Add(Diagnostic.Error(
                    "DM0403", proc.NameSpan, $"{proc.Name}: duplicate definition"));
                return;
            }
        }
    }

    /// <summary>
    /// An expression-position path literal must name a type that exists: dm.exe reports
    /// "undefined type path" eagerly there, unlike a DECLARED type, which is silent until the var
    /// is used (§8). Restricted to what is certain:
    /// </summary>
    /// <remarks>
    /// Absolute anchors only — a leading-`.` path is an upward search with its own rules, and
    /// `.proc/X`, which PROC_REF expands to throughout /tg/station, names a proc. Paths carrying a
    /// `proc`/`verb` segment are proc references, judged by a different rule, so they are skipped
    /// too. Both are misses to close later rather than risks to take now.
    /// </remarks>
    /// <summary>
    /// Whether <c>..()</c> inside this proc has nothing to reach — dm.exe's <c>no_parent</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Compiler-verified on 516.1686, one case per proc in a single file, because three plausible
    /// readings of "has a parent" differ. It warns for a <b>new declaration</b> — a <c>proc/</c>
    /// marker — with no ancestor declaring the name: a global <c>/proc/f()</c>, and
    /// <c>/datum/a/proc/fresh()</c>. It stays silent for every override: of a project proc on a
    /// subtype, of a <b>builtin</b> such as <c>/mob/Login()</c>, and of the same type's own earlier
    /// declaration. And <c>/datum/b/proc/Login()</c> <b>does</b> warn even though <c>Login</c> is a
    /// builtin name, because <c>/datum/b</c> is unrelated to <c>/mob</c> — so the question is the
    /// enclosing type's ancestry, never the name in the abstract.
    /// </para>
    /// <para>
    /// The ancestor walk is kept rather than short-circuiting on <c>IsNewDeclaration</c> alone. In
    /// valid code an ancestor declaring the same name would already be <c>DM0403</c>, so the two
    /// agree — but a buffer mid-edit is not valid code, and warning about a missing parent that is
    /// sitting right there would read as our bug.
    /// </para>
    /// <para>
    /// Carries the compiler's warning name rather than a <c>DM0xxx</c> id, per §8a. Id 3013, on by
    /// default.
    /// </para>
    /// </remarks>
    private bool HasNoParentProc(ProcDeclarationSyntax proc, TypePath owner)
    {
        if (!proc.IsNewDeclaration)
            return false;

        if (owner.IsRoot)
            return true;

        if (_tree.Find(owner) is not TypeSymbol type)
            return false;

        foreach (TypeSymbol ancestor in _tree.InheritanceChain(type))
        {
            if (ancestor.Path == type.Path)
                continue;

            foreach (ProcSymbol candidate in ancestor.Procs)
            {
                if (candidate.Name == proc.Name)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// BYOND's own deprecation warning, <c>new_name</c>, for a builtin that has been renamed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Carries the compiler's warning NAME rather than a private <c>DM0xxx</c> id, per §8a: the
    /// names taken by <c>#pragma warn|ignore|error</c> are a shared vocabulary, and a project that
    /// silences <c>new_name</c> in source expects it silenced here too. Id 4005, on by default.
    /// </para>
    /// <para>
    /// <b>One entry, because one is what the compiler has.</b> Sixteen candidate renames were
    /// compiled against 516.1686 and only <c>lentext</c> warns — <c>text2list</c> and
    /// <c>list2text</c> are removed outright rather than deprecated, and everything else is
    /// current. The lab catalogue lists two more messages under this name, an output-operator
    /// <c>message()</c> and a <c>rand</c> STATEMENT, both different constructs and neither present
    /// in any corpus project, so they are left until something asks for them.
    /// </para>
    /// <para>
    /// A project declaring its own <c>lentext</c> shadows the builtin, and warning there would be
    /// reporting against a proc BYOND never named.
    /// </para>
    /// </remarks>
    private void ReportDeprecatedCall(InvocationExpressionSyntax invocation)
    {
        if (invocation.Target is not IdentifierExpressionSyntax { Name: "lentext" } callee)
            return;

        foreach (ProcSymbol declared in _tree.Root.Procs)
        {
            // The shadow is a declaration SITE, not a non-builtin symbol: on a seeded tree the
            // project's declaration MERGES into the builtin's ProcSymbol and IsBuiltin stays
            // true, so the previous `!IsBuiltin` test could only ever hold on an unseeded unit
            // harness — found 2026-08-13 the moment the harness seeded builtins like production.
            if (declared.Name == "lentext" && declared.Sites.Count > 0)
                return;
        }

        if (Silenced("new_name", callee.Span))
            return;

        _diagnostics.Add(Diagnostic.Warning(
            "new_name", callee.Span, "lentext: lentext is being phased out; replace with length"));
    }

    private void BindPath(PathExpressionSyntax path)
    {
        if (path.Path.Anchor != PathAnchor.Absolute || path.Path.Segments.Count == 0)
            return;

        foreach (string segment in path.Path.Segments)
        {
            if (segment is "proc" or "verb")
            {
                // `/type/proc/Name` is a proc REFERENCE — TYPE_PROC_REF's expanded shape — and
                // worth a hit in the index even though the diagnostics skip it. Only the
                // unambiguous form: the marker directly before the final segment.
                EmitProcReference(path);
                return;
            }
        }

        TypePath resolved = TypePath.FromSegments(path.Path.Segments);

        if (_tree.Find(resolved) is { } named)
        {
            if (SinkWants(path.Path.Segments[^1]))
                _sink!(new Reference(_file ?? string.Empty, path.Span, named.Path.Text, ReferenceKind.Read, _inside));

            return;
        }

        // `/obj/small/trap/get` names the verb `get` on /obj/small/trap with no `verb` marker
        // segment — mlaas writes `verbs += /obj/small/trap/get` and dm.exe accepts it. Resolved
        // through inheritance on purpose: leniency here costs a miss, strictness an invention.
        if (path.Path.Segments.Count >= 2)
        {
            List<string> ownerSegments = new(path.Path.Segments.Count - 1);
            for (int i = 0; i < path.Path.Segments.Count - 1; i++)
                ownerSegments.Add(path.Path.Segments[i]);

            if (_tree.Find(TypePath.FromSegments(ownerSegments)) is { } type
                && _tree.ResolveProc(type, path.Path.Segments[^1]) is not null)
            {
                return;
            }
        }

        _diagnostics.Add(Diagnostic.Error(
            "DM0402", path.Span, $"{resolved}: undefined type path"));
    }

    private void BindStatement(StatementSyntax? statement, Scope scope)
    {
        switch (statement)
        {
            case null:
                return;

            case BlockStatementSyntax block:
                foreach (StatementSyntax child in block.Statements)
                    BindStatement(child, scope);

                break;

            // The only position where an assignment's value goes nowhere, which is what makes a
            // bare `x = 1` a write and not a use. See the assignment case.
            case ExpressionStatementSyntax expression:
                BindExpression(expression.Expression, scope, invoked: false, discarded: true);
                break;

            // Declared as encountered, not gathered up front. A proc routinely reuses one name for
            // several loop variables of different types, so hoisting them into one flat scope let a
            // later `for(var/obj/disc_train/T ...)` decide how an earlier `T` was checked — which
            // invented errors on working code. A use above its declaration simply finds nothing and
            // is skipped, which is the safe direction.
            case LocalVarStatementSyntax local:
            {
                // A `for` header's own variable is exempt from unused_var: dm.exe leaves
                // `for(var/i in 1 to 3)` silent when the body never mentions `i`, and warning
                // there was worth 14 invented on mlaas alone.
                LocalKind kind =
                    _declaringLoopVariable ? LocalKind.LoopVariable
                    : IsVarBlockHeader(local) ? LocalKind.BlockHeader
                    : IsPersistent(local) ? LocalKind.Persistent
                    : LocalKind.Local;

                BindDimensions(local, scope);
                BindExpression(local.Initializer, scope, invoked: false);
                Declare(local, scope, kind);

                foreach (LocalVarStatementSyntax sibling in local.Siblings)
                {
                    BindDimensions(sibling, scope);
                    BindExpression(sibling.Initializer, scope, invoked: false);
                    Declare(sibling, scope, kind);
                }

                break;
            }

            case IfStatementSyntax branch:
                BindExpression(branch.Condition, scope, invoked: false);
                BindStatement(branch.Then, scope);
                BindStatement(branch.Otherwise, scope);
                break;

            case WhileStatementSyntax loop:
                BindExpression(loop.Condition, scope, invoked: false);
                BindStatement(loop.Body, scope);
                break;

            case DoWhileStatementSyntax loop:
                BindStatement(loop.Body, scope);
                BindExpression(loop.Condition, scope, invoked: false);
                break;

            // The loop variable belongs to the loop, so `for(var/obj/trainer/T ...)` cannot reach a
            // `T` of another type declared in a later loop of the same proc.
            case ForStatementSyntax loop:
            {
                Scope inner = scope.Nest();

                bool wasDeclaringLoopVariable = _declaringLoopVariable;
                _declaringLoopVariable = true;

                foreach (StatementSyntax initializer in loop.Initializers)
                    BindStatement(initializer, inner);

                _declaringLoopVariable = wasDeclaringLoopVariable;

                BindExpression(loop.Condition, inner, invoked: false);
                BindExpression(loop.Sequence, inner, invoked: false);

                // `for(var/i in 1 to end step by)` — the bound and the step are expressions like
                // any other and were bound by nothing until 2026-08-12, so a name used only there
                // was invisible to this walk AND to the reference index. tgstation writes
                // `for(var/i in 1 to value / gcf)`, which is what exposed it.
                BindExpression(loop.RangeEnd, inner, invoked: false);
                BindExpression(loop.Step, inner, invoked: false);

                foreach (StatementSyntax increment in loop.Increments)
                    BindStatement(increment, inner);

                BindStatement(loop.Body, inner);
                break;
            }

            case SwitchStatementSyntax choice:
                BindExpression(choice.Value, scope, invoked: false);

                foreach (SwitchCaseSyntax branch in choice.Cases)
                {
                    foreach (ExpressionSyntax value in branch.Values)
                        BindExpression(value, scope, invoked: false);

                    BindStatement(branch.Body, scope);
                }

                break;

            case ReturnStatementSyntax returned:
                BindExpression(returned.Value, scope, invoked: false);
                break;

            case SpawnStatementSyntax spawn:
                BindExpression(spawn.Delay, scope, invoked: false);
                BindStatement(spawn.Body, scope);
                break;

            case UnaryStatementSyntax unary:
                BindExpression(unary.Operand, scope, invoked: false);
                break;

            case TryStatementSyntax guarded:
            {
                BindStatement(guarded.Body, scope.Nest());

                // `catch(var/exception/e)` declares `e` for the handler alone.
                Scope handler = scope.Nest();

                if (guarded.Exception is { } caught)
                    Declare(caught, handler, LocalKind.Caught);

                BindStatement(guarded.CatchBody, handler);
                break;
            }

            // A label is not a name lookup, but it CARRIES A BODY: `set_adj_in_dir: { ... }` is
            // the label-plus-brace-block form that was worth 754 diagnostics when the parser
            // learned it, and every statement inside one was invisible here until 2026-08-12.
            // A `\`-continued macro body has no lines to indent, so tgstation writes whole
            // algorithms this way.
            case LabelStatementSyntax label:
                _procLabels.Add((label.Name, label.Span));
                BindStatement(label.Body, scope);
                break;

            // A label is USED only when something NAMES it. Compiler-verified: `break used`,
            // `continue looped` and `goto target` are all uses, while a BARE `break` inside a
            // labelled block leaves it unused — dm.exe warns there, which is the case an
            // implementation would most likely get wrong.
            case BreakStatementSyntax { Label: { } broken }:
                _labelUses.Add(broken);
                break;

            case GotoStatementSyntax { Label: { } jumped }:
                _labelUses.Add(jumped);
                break;

            // `set` names are a fixed vocabulary rather than members of anything — ten names,
            // probed in verbs and in procs alike (SyntaxFacts.SetNames), and a name outside it is
            // dm.exe's plain "undefined var" on the set line (probes b2_set_unknown,
            // b4_set_bogus_in, w3012_loop_checks). The VALUE stays unbound: `set src in view(7)`
            // is prompt configuration, not a read.
            case SetStatementSyntax setting:
                if (setting.Name.Length > 0
                    && System.Array.IndexOf(Syntax.SyntaxFacts.SetNames, setting.Name) < 0)
                {
                    _diagnostics.Add(Diagnostic.Error(
                        "DM0400", setting.Span, $"{setting.Name}: undefined var"));
                }

                break;

            // An unlabelled break names nothing.
            case GotoStatementSyntax:
            case BreakStatementSyntax:
                break;
        }
    }

    /// <remarks>
    /// <c>discarded</c> is true only for the expression of an expression-statement, where the value
    /// goes nowhere. It never propagates into subexpressions: in <c>x = (y = 1)</c> the outer value
    /// is discarded and the inner one is consumed.
    /// </remarks>
    private void BindExpression(
        ExpressionSyntax? expression, Scope scope, bool invoked, bool written = false, bool discarded = false)
    {
        switch (expression)
        {
            case null:
                return;

            case MemberAccessExpressionSyntax member:
                BindMemberAccess(member, scope, invoked, written);
                break;

            // Sink-only: a bare name resolved for the reference index. No diagnostic ever comes
            // from this case.
            case IdentifierExpressionSyntax identifier:
                BindIdentifier(identifier, scope, invoked, written);
                break;

            case PathExpressionSyntax path:
                BindPath(path);
                break;

            case InvocationExpressionSyntax invocation:
                // The callee is being called, which decides whether a miss is an undefined proc or
                // an undefined var — dm.exe reports them differently.
                BindExpression(invocation.Target, scope, invoked: true);
                ReportDeprecatedCall(invocation);

                foreach (ArgumentSyntax argument in invocation.Arguments)
                    BindArgument(argument, scope);

                break;

            case IndexExpressionSyntax index:
                BindExpression(index.Target, scope, invoked: false);
                BindExpression(index.Index, scope, invoked: false);
                break;

            case UnaryExpressionSyntax unary:
                BindExpression(unary.Operand, scope, invoked: false);
                break;

            case BinaryExpressionSyntax binary:
                BindExpression(binary.Left, scope, invoked: false);
                BindExpression(binary.Right, scope, invoked: false);
                break;

            case AssignmentExpressionSyntax assignment:
                // The target is a WRITE for the reference index; compound operators both read and
                // write, and report as writes — "where is hp assigned" is the question they answer.
                //
                // unused_var needs the other half of that fact, and it is compiler-verified: a
                // plain `x = 1` is a write alone and warns, while `x += 1` reads as well and is
                // silent. The index still calls both writes; only the local's read flag differs.
                // A compound `x += 1` reads as well as writing. So does a plain `x = 1` whose
                // VALUE IS CONSUMED — tgstation's `return screentip_change = TRUE` is the shape,
                // and dm.exe stays silent on it while warning on the same write as a bare
                // statement. Both come down to whether anything looks at the result.
                if ((assignment.OperatorToken != TokenKind.Assign || !discarded)
                    && assignment.Target is IdentifierExpressionSyntax compound
                    && scope.Find(compound.Name) is { } target)
                {
                    target.Read = true;
                }

                BindExpression(assignment.Target, scope, invoked: false, written: true);
                BindExpression(assignment.Value, scope, invoked: false);
                break;

            // `path {a = 1; b = x}` — the braces are mandatory here and the entries are ordinary
            // expressions, so a local read inside one is a read. Missing from this switch until
            // 2026-08-12, which made every such read invisible to the walk and to the index.
            // An entry's TARGET names a member of the constructed type, not anything in scope,
            // so the undefined-identifier check must not see it.
            case ModifiedTypeExpressionSyntax modified:
                BindExpression(modified.Type, scope, invoked: false);

                foreach (ExpressionSyntax entry in modified.Assignments)
                {
                    if (entry is AssignmentExpressionSyntax { Target: IdentifierExpressionSyntax setMember } set)
                    {
                        BindIdentifier(setMember, scope, invoked: false, written: true, checkUndefined: false);
                        BindExpression(set.Value, scope, invoked: false);
                    }
                    else
                    {
                        BindExpression(entry, scope, invoked: false);
                    }
                }

                break;

            case ConditionalExpressionSyntax conditional:
                BindExpression(conditional.Condition, scope, invoked: false);
                BindExpression(conditional.WhenTrue, scope, invoked: false);
                BindExpression(conditional.WhenFalse, scope, invoked: false);
                break;

            case NewExpressionSyntax created:
                // `new the_type(usr)` — a type held in a VAR, which the parser reads as one
                // invocation expression. The name is a VALUE READ and the parens are constructor
                // arguments; bound as a call it reported dm.exe-clean sites as undefined procs
                // and hid the read from unused_var (mlaas, madridspy and warklan all ship it).
                if (created.Type is InvocationExpressionSyntax constructed)
                {
                    BindExpression(constructed.Target, scope, invoked: false);

                    foreach (ArgumentSyntax argument in constructed.Arguments)
                        BindArgument(argument, scope);
                }
                else
                {
                    BindExpression(created.Type, scope, invoked: false);
                }

                foreach (ArgumentSyntax argument in created.Arguments)
                    BindArgument(argument, scope);

                break;

            case AsExpressionSyntax clause:
                BindExpression(clause.Expression, scope, invoked: false);
                break;

            case InterpolatedStringExpressionSyntax interpolated:
                foreach (InterpolatedStringPartSyntax part in interpolated.Parts)
                    BindExpression(part.Expression, scope, invoked: false);

                break;

            case ParentCallExpressionSyntax parent:
                if (_parentless && !Silenced("no_parent", parent.Span))
                {
                    _diagnostics.Add(Diagnostic.Warning(
                        "no_parent", parent.Span, "..: ..() has no parent proc to call"));
                }

                foreach (ArgumentSyntax argument in parent.Arguments)
                    BindArgument(argument, scope);

                break;
        }
    }

    /// <summary>
    /// An argument is up to three expressions, and only the value was bound until 2026-08-12.
    /// `list(key = 5)` carries a reader in <see cref="ArgumentSyntax.Name"/> — tgstation writes
    /// `list((toxin_to_get) = 5)` and `list(get_material(material_used) = ...)` — and
    /// `pick(20;"brown")` carries one in the weight. Both were invisible to this walk and to the
    /// reference index.
    /// </summary>
    /// <summary>
    /// A bracket declaration's sizes — `var/list/tier_list[max_tier]`. Bound BEFORE the name is
    /// declared, because the size is evaluated where the declaration sits and cannot refer to the
    /// variable it is sizing.
    /// </summary>
    private void BindDimensions(LocalVarStatementSyntax local, Scope scope)
    {
        foreach (ExpressionSyntax dimension in local.Dimensions)
            BindExpression(dimension, scope, invoked: false);
    }

    private void BindArgument(ArgumentSyntax argument, Scope scope)
    {
        // A bare-identifier assoc NAME is exempt from the undefined check: `list(k = "a")` stores
        // the STRING key "k" and never reads a variable (probed 2026-08-13, madridspy's gun
        // lists), a call's `f(name = 1)` names a parameter, and the parenthesized variable form
        // cannot be told apart because the AST keeps no parentheses.
        if (argument.Name is IdentifierExpressionSyntax assocName)
            BindIdentifier(assocName, scope, invoked: false, written: false, checkUndefined: false);
        else
            BindExpression(argument.Name, scope, invoked: false);

        BindExpression(argument.Weight, scope, invoked: false);
        BindExpression(argument.Value, scope, invoked: false);
    }

    /// <summary>
    /// The one check this pass makes: a member reached through a receiver whose type is written
    /// down, and which that type does not have.
    /// </summary>
    private void BindMemberAccess(MemberAccessExpressionSyntax member, Scope scope, bool invoked, bool written = false)
    {
        // A bare-identifier RECEIVER is not reported by the identifier check: dm.exe's message
        // there folds the whole dotted text into one symbol — `mob.name: undefined var`, pinned
        // by errors/bare_type_receiver — so an identifier-shaped report would be a different
        // diagnostic than the compiler's, invented and missed at once. Read-marking is deferred
        // too: dm.exe counts a member access as a USE of its receiver only when the access
        // COMPILES — unused_var fires beside every failing `.`, untyped receiver and missing
        // member alike (probed; errors/semantic pins the typed-receiver half) — while a
        // `:`-family access and a resolving `.` are ordinary reads.
        if (member.Target is IdentifierExpressionSyntax receiverName)
        {
            BindIdentifier(receiverName, scope, invoked: false, written: false,
                checkUndefined: false, markRead: false);
        }
        else
        {
            BindExpression(member.Target, scope, invoked: false);
        }

        void MarkReceiverRead()
        {
            if (member.Target is IdentifierExpressionSyntax name && scope.Find(name.Name) is { } local)
                local.Read = true;
        }

        // The `:` family. Untyped is NOT unchecked here, and the three questions differ - all
        // probed against 516.1687, one case per compilation unit:
        //
        //   `:`  on a WRITTEN type   the declared type, its ancestors AND ITS SUBTYPES.
        //                            `M:on_subtype` compiles where `M.on_subtype` does not;
        //                            `M:elsewhere`, on an unrelated type, is "undefined var".
        //   `?:` on anything         the WIDEST check: does the name exist as a member of
        //                            ANYTHING. `M?:elsewhere` compiles where `M:elsewhere`
        //                            errors, which is the pair that separates the two, and
        //                            `M?:nowhere_xyz` still errors, so it is not unchecked.
        //   either, UNTYPED receiver the same widest check - `x:hp` compiles, `x:icon_state`
        //                            compiles (builtins count), `x:nowhere_xyz` does not.
        //
        // Kind-sensitive throughout: `x:only_a_proc` read as a VAR is "undefined var" though the
        // proc exists, so the two name sets are asked separately.
        if (member.Kind is MemberAccessKind.Colon or MemberAccessKind.NullColon)
        {
            if (UncertainWants(member.Name))
                _uncertain!(new Services.UncertainSite(
                    _file ?? string.Empty, member.NameSpan, Services.UncertainReason.ColonAccess));

            BindColonAccess(member, scope, invoked, MarkReceiverRead);
            return;
        }

        // `::` and anything else in the family stays unchecked: a different operator with a
        // different question, and nothing has probed it.
        if (member.Kind is not (MemberAccessKind.Dot or MemberAccessKind.NullDot))
        {
            MarkReceiverRead();

            if (UncertainWants(member.Name))
                _uncertain!(new Services.UncertainSite(
                    _file ?? string.Empty, member.NameSpan, Services.UncertainReason.ColonAccess));

            return;
        }

        // Only a receiver whose type is written down is checked. Anything else is either genuinely
        // unknowable — a call result or an index, where dm.exe silently degrades `.` to `:` and
        // stops checking — or an untyped variable, where it rejects every member including the
        // right one. The second is a real diagnostic and a valuable one, but it needs certainty
        // that we saw the declaration, so it is not in this pass.
        if (ReceiverType(member.Target, scope) is not { } receiver || _tree.Find(receiver) is not { } type)
        {
            if (UncertainWants(member.Name))
                _uncertain!(new Services.UncertainSite(
                    _file ?? string.Empty, member.NameSpan, Services.UncertainReason.UntypedReceiver));

            // Two errors share dm.exe's dotted-symbol form here, and this branch is Dot-only —
            // the `:` family returned above. A receiver that resolves as NO var anywhere is
            // `mob.name: undefined var` (errors/bare_type_receiver). And `.` through an UNTYPED
            // var rejects every member, the right one included — probed across every spelling:
            // local, parameter, `as`-clause parameter, a member reached by bare name, and a
            // global, with the member existing elsewhere or nowhere, and the invoked form as the
            // proc twin (`x.f: undefined proc`). Certainty is what kept the untyped half out of
            // the first pass, and it is the guard: a BUILTIN var with no recorded type is OUR
            // gap — five are deliberately untyped because no probe discriminates them — so only
            // a declaration we can see reports.
            if (member.Target is IdentifierExpressionSyntax bare && !IsCompilerProvidedName(bare.Name))
            {
                bool untypedDeclared =
                    scope.Find(bare.Name) is { DeclaredType: null }
                    || (scope.Find(bare.Name) is null && UntypedProjectVar(bare.Name, scope));

                bool nowhere = scope.Find(bare.Name) is null
                    && !untypedDeclared
                    && !BareNameIsAVar(bare.Name, scope);

                // The nowhere-invoked form (`mob.f()`) is unprobed and stays a miss.
                if (untypedDeclared || (nowhere && !invoked))
                {
                    _diagnostics.Add(invoked
                        ? Diagnostic.Error(
                            "DM0401", member.NameSpan, $"{bare.Name}.{member.Name}: undefined proc")
                        : Diagnostic.Error(
                            "DM0400", member.NameSpan, $"{bare.Name}.{member.Name}: undefined var"));

                    return;
                }
            }

            // Unreported — a builtin-untyped receiver, a compiler-provided name, or a
            // non-identifier — is an access dm.exe compiles, so the receiver was used.
            MarkReceiverRead();
            return;
        }

        bool isProc = invoked && _tree.ResolveProc(type, member.Name) is not null;
        bool exists = isProc || _tree.ResolveVar(type, member.Name) is not null;

        if (exists)
        {
            MarkReceiverRead();

            if (SinkWants(member.Name) && DeclaringOwner(type, member.Name, isProc) is { } declaring)
            {
                ReferenceKind kind = invoked
                    ? ReferenceKind.Call
                    : written ? ReferenceKind.Write : ReferenceKind.Read;

                EmitReference(member.Name, member.NameSpan, kind, declaring, isProc);
            }

            return;
        }

        // Not finding it used to be insufficient on its own, because a miss can mean our tree is
        // short rather than that the author is wrong. Two holes caused exactly that and both are now
        // closed rather than worked around: `builtins.txt` had one var on `/image` and none of the
        // appearance vars, and a root-level user type implicitly derives from `/datum`, which the
        // tree did not model. Fixing the tree beat guarding against it — the guard also suppressed
        // real errors, since a typo that happens to name a member of some unrelated type is still a
        // typo.
        //
        // Re-measured with the guard removed: zero invented on mlaas and madridspy, both of which
        // compile clean. If a future project invents again, the cause is a third hole in what we
        // hold, and the fix is to close it here rather than to stop reporting.

        // A `new /path(...)` receiver is OURS ALONE. dm.exe accepts the member if it exists on any
        // type in the program, because it holds no type for the expression - and the runtime then
        // raises "undefined variable" the moment the line is reached. So the file compiles, which
        // makes this a DM03xx warning rather than a DM04xx error, and a deliberate divergence
        // rather than an invented diagnostic.
        if (member.Target is NewExpressionSyntax)
        {
            _diagnostics.Add(Diagnostic.Warning(
                "DM0302",
                member.NameSpan,
                $"{member.Name} is not on {type.Path}, so this compiles and then fails at runtime"
                + " — dm.exe does not check a member reached through `new`"));

            return;
        }

        _diagnostics.Add(invoked
            ? Diagnostic.Error(
                "DM0401",
                member.NameSpan,
                $"undefined proc on {type.Path}: {member.Name}")
            : Diagnostic.Error(
                "DM0400",
                member.NameSpan,
                $"undefined var on {type.Path}: {member.Name}"));
    }

    /// <summary>
    /// The <c>:</c> and <c>?:</c> check: wider than <c>.</c>, and still a check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reports in dm.exe's own form — the receiver as written, a plain <c>:</c> even when the
    /// source said <c>?:</c> (the compiler prints `M:nowhere_xyz` for `M?:nowhere_xyz`), and
    /// "undefined var" or "undefined proc" by whether the access is invoked.
    /// </para>
    /// <para>
    /// The certainty guard is the one the untyped-`.` check needed: only a receiver we can see
    /// the declaration of is reported. A builtin var with no recorded type is OUR table's gap —
    /// five are deliberately untyped because no probe discriminates them — and reporting through
    /// one would invent on working code.
    /// </para>
    /// </remarks>
    private void BindColonAccess(
        MemberAccessExpressionSyntax member, Scope scope, bool invoked, Action markReceiverRead)
    {
        bool wide = member.Kind == MemberAccessKind.NullColon;

        // Whether a FAILING access still counts as a use of its receiver splits by operator, and
        // only a probe could have said so. Same receiver, same name, one character apart:
        //
        //   M:nowhere_xyz    error AND unused_var on M   - the failing `:` is not a use
        //   M?:nowhere_xyz   error, NO unused_var        - the failing `?:` is
        //
        // which reads as `?:` evaluating the receiver for its null test before the member lookup
        // can fail. Whatever the mechanism, dm.exe's pairing is the contract, and getting it
        // backwards costs a missed diagnostic on every failing site rather than a wrong one.
        if (wide)
            markReceiverRead();

        if (member.Target is not IdentifierExpressionSyntax bare || IsCompilerProvidedName(bare.Name))
        {
            markReceiverRead();
            return;
        }
        TypeSymbol? receiver = null;

        if (!wide && ReceiverType(member.Target, scope) is { } path)
            receiver = _tree.Find(path);

        if (receiver is null)
        {
            // No written type to widen from, so the question is the widest one. Only ask it when
            // the receiver is a declaration we have seen: an untyped local, parameter or project
            // var. Anything else - a builtin whose type we never recorded, a name we cannot
            // place - stays silent, which is the untyped-`.` guard applied unchanged.
            bool ours = scope.Find(bare.Name) is not null || UntypedProjectVar(bare.Name, scope)
                || BareNameIsAVar(bare.Name, scope);

            if (!ours || _tree.AnyMemberNamed(member.Name, invoked))
            {
                markReceiverRead();
                return;
            }
        }
        else if (_tree.AnyDescendantHasMember(receiver, member.Name, invoked)
            || (invoked
                ? _tree.ResolveProc(receiver, member.Name) is not null
                : _tree.ResolveVar(receiver, member.Name) is not null))
        {
            markReceiverRead();
            return;
        }

        _diagnostics.Add(invoked
            ? Diagnostic.Error(
                "DM0401", member.NameSpan, $"{bare.Name}:{member.Name}: undefined proc")
            : Diagnostic.Error(
                "DM0400", member.NameSpan, $"{bare.Name}:{member.Name}: undefined var"));
    }

    // -- the reference sink ---------------------------------------------------

    /// <summary>Whether the sink is on and wants this name — the cheap gate before any walk.</summary>
    private bool SinkWants(string name)
        => _sink is not null
            && (_referenceName is null || string.Equals(name, _referenceName, StringComparison.Ordinal));

    /// <summary>The same gate for the uncertain sink, which is filtered by the same name.</summary>
    private bool UncertainWants(string name)
        => _uncertain is not null
            && (_referenceName is null || string.Equals(name, _referenceName, StringComparison.Ordinal));

    /// <summary>
    /// The FARTHEST type in the chain declaring the member — the canonical owner, so a call
    /// through a subtype receiver and an override of the same proc share one target.
    /// </summary>
    private TypeSymbol? DeclaringOwner(TypeSymbol start, string name, bool proc)
    {
        TypeSymbol? found = null;

        foreach (TypeSymbol step in _tree.InheritanceChain(start))
        {
            if (proc ? step.FindProc(name) is not null : step.FindVar(name) is not null)
                found = step;
        }

        return found;
    }

    private void EmitReference(string name, TextSpan span, ReferenceKind kind, TypeSymbol owner, bool isProc)
    {
        string prefix = owner.Path.IsRoot ? string.Empty : owner.Path.Text;
        string target = isProc ? $"{prefix}/{name}()" : $"{prefix}/{name}";

        _sink!(new Reference(_file ?? string.Empty, span, target, kind, _inside));
    }

    /// <summary>
    /// A hit for <c>/type/proc/Name</c> and <c>/type/verb/Name</c> literals, the shape
    /// <c>TYPE_PROC_REF</c> expands to. Only the unambiguous form is read: the marker segment
    /// directly before a final name segment.
    /// </summary>
    private void EmitProcReference(PathExpressionSyntax path)
    {
        IReadOnlyList<string> segments = path.Path.Segments;

        if (_sink is null || segments.Count < 2 || segments[^2] is not ("proc" or "verb"))
            return;

        string name = segments[^1];

        if (!SinkWants(name))
            return;

        List<string> ownerSegments = new(segments.Count - 2);

        for (int i = 0; i < segments.Count - 2; i++)
            ownerSegments.Add(segments[i]);

        TypeSymbol? owner = ownerSegments.Count == 0
            ? _tree.Root
            : _tree.Find(TypePath.FromSegments(ownerSegments));

        if (owner is null)
            return;

        if (DeclaringOwner(owner, name, proc: true) is { } declaring)
            EmitReference(name, path.Span, ReferenceKind.Read, declaring, isProc: true);
    }

    /// <summary>
    /// A proc declared WITHOUT the <c>proc/</c> marker overrides whatever an ancestor declared —
    /// the incoming half of a type hierarchy, reported as a reference to the origin.
    /// </summary>
    private void EmitOverrideReference(ProcDeclarationSyntax proc, TypePath owner)
    {
        if (!SinkWants(proc.Name) || _tree.Find(owner) is not TypeSymbol type)
            return;

        if (DeclaringOwner(type, proc.Name, proc: true) is { } origin && !ReferenceEquals(origin, type))
            EmitReference(proc.Name, proc.NameSpan, ReferenceKind.Override, origin, isProc: true);
    }

    /// <summary>
    /// Bare-name resolution, serving two consumers: the reference index (project-declared
    /// symbols) and the undefined-var check (anything dm.exe can see, builtins included).
    /// </summary>
    /// <remarks>
    /// The check is vars-only, which the mined probes pin: <c>&amp;f</c> and <c>initial(p)</c>
    /// both draw <i>"undefined var"</i> from dm.exe though <c>f</c> and <c>p</c> are procs in
    /// scope, so a proc name never satisfies value position. <c>checkUndefined</c> is false in
    /// the two positions where a bare name is not a value read at all: a member-access RECEIVER,
    /// where dm.exe folds the whole dotted text into its own message (<c>mob.name: undefined
    /// var</c> — a separate miss), and an argument's assoc NAME, which is STRING sugar
    /// (<c>list(k = "a")</c> stores the key <c>"k"</c>; probed 2026-08-13) and indistinguishable
    /// from the parenthesized variable form because the AST keeps no parentheses.
    /// </remarks>
    private void BindIdentifier(
        IdentifierExpressionSyntax identifier, Scope scope, bool invoked, bool written,
        bool checkUndefined = true, bool markRead = true)
    {
        // A local shadows a member whatever its type, so it settles the name before anything else
        // asks about it. Marking the read here — ahead of the sink gate rather than behind it — is
        // what makes unused_var possible at all: the sink is null on the diagnostics path, so
        // every bare-name read of a local was invisible to this walk, and a check hung off it saw
        // a proc's locals as never read. That is the shape attempt three could not account for.
        if (scope.Find(identifier.Name) is { } local)
        {
            // A CALL is not satisfied by the var — but the NAME can still resolve as a proc:
            // mlaas's `limittext(message, length)` calls the builtin `length()` with a parameter
            // of that name in scope, and dm.exe compiles it. Only a name no proc anywhere
            // satisfies reports. The local stays unread either way: probed, dm.exe warns
            // unused_var on a local whose only mention is a call.
            if (invoked)
            {
                if (checkUndefined && !BareNameIsAProc(identifier.Name, scope))
                {
                    _diagnostics.Add(Diagnostic.Error(
                        "DM0401", identifier.Span, $"{identifier.Name}: undefined proc"));
                }

                return;
            }

            if (!written && markRead)
                local.Read = true;

            // Locals and parameters are not index symbols.
            return;
        }

        // `usr` does not exist in a type-level or global var initializer — probed in all three
        // spellings (datum var, global var, bare override) as dm.exe's "usr: undefined var".
        if (identifier.Name == "usr" && _inTypeInitializer && checkUndefined && !invoked)
        {
            _diagnostics.Add(Diagnostic.Error("DM0400", identifier.Span, "usr: undefined var"));
            return;
        }

        // Proc-scope vars and the compiler's own vocabulary, which no tree holds. The
        // pseudo-macros resolve at dm.exe's parser layer and survive our preprocessor as plain
        // identifiers; `as`/`in`/`to` reach here only through error recovery on code the parser
        // already reported — dm.exe's message on `var/as = 40` is the parse error, never
        // "undefined var". `__FILE__`/`__LINE__` used to be absorbed here too and are now
        // expanded at the use instead — see IsCompilerProvidedName's note.
        if (IsCompilerProvidedName(identifier.Name))
            return;

        if (checkUndefined)
        {
            // The undefined halves of DM0400/DM0401: a bare name that nothing dm.exe can see
            // satisfies. Value position is VARS-ONLY and call position is PROCS-ONLY — probed
            // from both sides: `&f` and `initial(p)` error though the procs exist, and a called
            // global VAR is "undefined proc" though the var exists.
            if (!invoked && !BareNameIsAVar(identifier.Name, scope))
            {
                _diagnostics.Add(Diagnostic.Error(
                    "DM0400", identifier.Span, $"{identifier.Name}: undefined var"));
                return;
            }

            if (invoked && !BareNameIsAProc(identifier.Name, scope))
            {
                _diagnostics.Add(Diagnostic.Error(
                    "DM0401", identifier.Span, $"{identifier.Name}: undefined proc"));
                return;
            }
        }

        if (!SinkWants(identifier.Name))
            return;

        TypeSymbol? declaring = _tree.Find(scope.EnclosingType) is { } enclosing
            ? DeclaringOwner(enclosing, identifier.Name, invoked)
            : null;

        if (declaring is null)
        {
            bool atRoot = invoked
                ? _tree.Root.FindProc(identifier.Name) is not null
                : _tree.Root.FindVar(identifier.Name) is not null;

            if (!atRoot)
                return;

            declaring = _tree.Root;
        }

        ReferenceKind kind = invoked ? ReferenceKind.Call : written ? ReferenceKind.Write : ReferenceKind.Read;
        EmitReference(identifier.Name, identifier.Span, kind, declaring, isProc: invoked);
    }

    /// <summary>Proc-scope names, compiler vocabulary, and recovery-only keywords — never
    /// reported. <c>__FILE__</c>/<c>__LINE__</c> are deliberately NOT here since 2026-08-13: the
    /// expander now expands them at the use, so one reaching this walk as an identifier is a
    /// preprocessor regression that should surface, not be absorbed.</summary>
    private static bool IsCompilerProvidedName(string name)
        => name is "src" or "usr" or "world" or "global" or "args" or "caller" or "callee"
            or "__TYPE__" or "__PROC__" or "__IMPLIED_TYPE__"
            or "__MAIN__" or "DM_VERSION" or "DM_BUILD"
            or "as" or "in" or "to";

    /// <summary>Whether any var dm.exe can see satisfies this bare name: the enclosing chain's,
    /// builtins included, or a root global's.</summary>
    private bool BareNameIsAVar(string name, Scope scope)
        => (_tree.Find(scope.EnclosingType) is { } enclosing
                && _tree.ResolveVar(enclosing, name) is not null)
            || _tree.Root.FindVar(name) is not null;

    /// <summary>The call-position twin: procs and verbs only.</summary>
    private bool BareNameIsAProc(string name, Scope scope)
        => (_tree.Find(scope.EnclosingType) is { } enclosing
                && _tree.ResolveProc(enclosing, name) is not null)
            || _tree.Root.FindProc(name) is not null;

    /// <summary>
    /// A var whose declarations we can SEE carry no type ANYWHERE on the chain — the certainty
    /// the untyped-receiver check needs. A typed declaration at any depth wins over the untyped
    /// override shadowing it (tgstation's bots and `ai_controller`), and a builtin with no
    /// recorded type at any depth is our own table's gap, never reported.
    /// </summary>
    private bool UntypedProjectVar(string name, Scope scope)
    {
        if (_tree.Find(scope.EnclosingType) is { } enclosing)
        {
            bool anyVar = false;
            bool anyBuiltin = false;

            foreach (TypeSymbol candidate in _tree.InheritanceChain(enclosing))
            {
                if (candidate.FindVar(name) is not { } found)
                    continue;

                if (found.DeclaredType is not null)
                    return false;

                anyVar = true;
                anyBuiltin |= found.IsBuiltin;
            }

            if (anyVar)
                return !anyBuiltin;
        }

        return _tree.Root.FindVar(name) is { IsBuiltin: false, DeclaredType: null };
    }

    /// <summary>
    /// The receiver's <b>written</b> type, or null when nothing says what it is.
    /// </summary>
    private TypePath? ReceiverType(ExpressionSyntax? target, Scope scope) => target switch
    {
        // `src` is the type the proc is declared on.
        IdentifierExpressionSyntax { Name: "src" } => scope.EnclosingType,

        // A bare name, in dm.exe's own resolution order: the nearest local — typed or not, since
        // an untyped local still SETTLES the name and falling through would type the receiver
        // from a member it shadows — then `usr`, then the enclosing type's members by written
        // type, then a root global's. Members and globals were missing until 0.28: `clone.health`
        // through a typed member var `var/mob/pc/clone` sat unchecked, unindexed and uncertain to
        // rename, though its type is written down. A name resolving to none of these is NOT read
        // as a type; completion dropped that fallback at 0.26 and this walk never had it.
        IdentifierExpressionSyntax identifier => BareNameReceiverType(identifier.Name, scope),

        // A path written out as a value.
        PathExpressionSyntax { Path.Anchor: PathAnchor.Absolute } path
            => TypePath.FromSegments(path.Path.Segments),

        // `new /obj/item(...)` constructs exactly that type, so the receiver's type is written
        // down as plainly as a declared local's. dm.exe does NOT check it — the receiver is
        // `<expression>` to it, so any member existing anywhere compiles — and the RUNTIME then
        // raises "undefined variable /mob/test/var/elsewhere". Reported as DM0302, a warning,
        // because the file does compile; see BindMemberAccess.
        NewExpressionSyntax { Type: PathExpressionSyntax { Path.Anchor: PathAnchor.Absolute } created }
            => TypePath.FromSegments(created.Path.Segments),

        // A CHAIN — `t.weapon.ammo`, where the receiver is itself a member access. Resolved one
        // step at a time: the target's type, then the member's DECLARED type on it. Written types
        // only, exactly as everywhere else here, so a member with no declared type ends the chain
        // rather than guessing.
        //
        // Absent until 2026-08-12, which meant a chained receiver resolved to nothing and
        // BindMemberAccess returned before it could either check the member or record it — so
        // `t.weapon.ammo` was missing from find-references while completion, definition and hover
        // all answered at that exact position. They share `ResolveReceiver`; this is a third copy,
        // and the divergence is the drift this project has been bitten by before.
        MemberAccessExpressionSyntax { Kind: MemberAccessKind.Dot } chain
            when ReceiverType(chain.Target, scope) is { } owner
                && _tree.Find(owner) is { } ownerType
                && _tree.ResolveVarType(ownerType, chain.Name) is { } declared
            => declared,

        _ => null,
    };

    /// <summary>
    /// The written type a bare-name receiver carries, in <c>dm.exe</c>'s resolution order. Null
    /// both for "nothing declares it" and for an untyped declaration, which equally mean
    /// "do not check".
    /// </summary>
    private TypePath? BareNameReceiverType(string name, Scope scope)
    {
        // A local settles the name whatever its type.
        if (scope.Find(name) is { } local)
            return local.DeclaredType;

        // `usr` is always a /mob — compiler-verified in PLAN §8, including that `world.mob` is a
        // runtime default for connecting clients rather than a static retype.
        if (name == "usr")
            return UsrType;

        // The chain's first non-null type, not the first symbol's — an untyped override on a
        // subtype must not hide the typed declaration above it (see ObjectTree.ResolveVarType).
        if (_tree.Find(scope.EnclosingType) is { } enclosing
            && _tree.ResolveVarType(enclosing, name) is { } member)
        {
            return member;
        }

        // Root vars: project globals, and builtins like `world`, which is a root var typed /world.
        return _tree.Root.FindVar(name) is { DeclaredType: { } global } ? global : null;
    }

    private static readonly TypePath UsrType = TypePath.Parse("/mob");

    private static TypePath PathOf(PathSyntax path, TypePath enclosing)
        => path.Anchor == PathAnchor.Absolute
            ? TypePath.FromSegments(path.Segments)
            : enclosing.Append(path.Segments);

    /// <summary>
    /// A local that outlives the call. Corpus-verified rather than assumed: tgstation's
    /// `var/static/mob/jeremy = new()` is read nowhere and dm.exe compiles the project silent.
    /// </summary>
    private static bool IsPersistent(LocalVarStatementSyntax local)
    {
        foreach (string modifier in local.Modifiers)
        {
            if (modifier is "static" or "global" or "const")
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a local declaration is really a `var` block header naming a type. The discriminator
    /// is the whole path resolving to a declared type with NO initialiser written: `var/mob` heads
    /// a block, while `var/mob/M` declares `M` because `/mob/M` is no type, and `var/never = 1`
    /// declares a variable because a header carries no value. Both forms this recognises ship in
    /// mlaas, and warning on them is one of the two causes that backed this check out before.
    /// </summary>
    private bool IsVarBlockHeader(LocalVarStatementSyntax local)
    {
        if (local.Initializer is not null)
            return false;

        List<string> segments = new();

        if (local.DeclaredType is { } declared)
            segments.AddRange(declared.Segments);

        segments.Add(local.Name);

        return _tree.Find(TypePath.FromSegments(segments)) is not null;
    }

    private void Declare(LocalVarStatementSyntax local, Scope scope, LocalKind kind)
    {
        // Brackets TYPE a local: `var/L[0]` is a /list to dm.exe, sized or not, and a written
        // type still wins — the same rule the tree applies to type-level bracket vars, shared
        // rather than repeated (DeclaredType.Of).
        LocalRecord record = new(
            local.Name,
            DeclaredType.Of(local.DeclaredType, local.HasBrackets),
            local.NameSpan,
            kind);

        scope.Declare(record);

        // Held flat for the whole proc rather than reported as each scope dies, because a nested
        // scope's names are still the proc's locals and dm.exe reports them all together.
        _procLocals.Add(record);
    }

    /// <summary>Why a name is in scope. Only <see cref="LocalKind.Local"/> can be unused_var.</summary>
    private enum LocalKind
    {
        /// <summary>A proc parameter. dm.exe never warns about an unused one.</summary>
        Parameter,

        /// <summary>A `var/x` written in a proc body — the only kind this check reports.</summary>
        Local,

        /// <summary>A `for` loop variable, which dm.exe leaves silent even when the body ignores it.</summary>
        LoopVariable,

        /// <summary>`catch(var/exception/e)`, exempt for the same reason as a parameter.</summary>
        Caught,

        /// <summary>
        /// A `var` block header — `var/obj/small/clothing` heading indented names, or a bare
        /// `var/mob`. It names the TYPE its children carry rather than declaring a variable, and
        /// mlaas ships both forms. Kept in scope so nothing about name resolution moves; it is
        /// simply never reported.
        /// </summary>
        BlockHeader,

        /// <summary>
        /// A `var/static/`, `var/global/` or `var/const/` local — one the compiler does not treat
        /// as an ordinary slot. Corpus-verified: tgstation's `var/static/mob/jeremy = new()` and
        /// its `var/const/viewtext` are each read nowhere and dm.exe compiles the project silent.
        /// </summary>
        Persistent,
    }

    /// <summary>
    /// One name in scope. Carries what unused_var needs and the object identity that lets a read
    /// found deep in an expression reach the declaration that a nested scope may have shadowed.
    /// </summary>
    private sealed class LocalRecord
    {
        public LocalRecord(string name, TypePath? declaredType, TextSpan nameSpan, LocalKind kind)
        {
            Name = name;
            DeclaredType = declaredType;
            NameSpan = nameSpan;
            Kind = kind;
        }

        public string Name { get; }

        public TypePath? DeclaredType { get; }

        public TextSpan NameSpan { get; }

        public LocalKind Kind { get; }

        /// <summary>Set by any read of the name. A write alone does not set it — `x = 1` on a
        /// variable nothing ever reads is exactly what dm.exe warns about.</summary>
        public bool Read { get; set; }
    }

    /// <summary>Names visible at one point in a proc, with the type each was declared as.</summary>
    private sealed class Scope
    {
        private readonly Dictionary<string, LocalRecord> _names = new();
        private readonly Scope? _parent;

        public Scope(TypePath enclosingType) => EnclosingType = enclosingType;

        private Scope(Scope parent)
        {
            _parent = parent;
            EnclosingType = parent.EnclosingType;
        }

        /// <summary>The type this code is declared on, which is what `src` resolves to.</summary>
        public TypePath EnclosingType { get; }

        /// <summary>A scope for a loop or a catch handler, whose names do not outlive it.</summary>
        public Scope Nest() => new(this);

        public void Declare(LocalRecord record) => _names[record.Name] = record;

        /// <summary>
        /// The nearest declaration of a name, or null when it is not in scope. Nearest matters:
        /// an inner scope shadows an outer one, and a read has to mark the declaration it can
        /// actually see.
        /// </summary>
        public LocalRecord? Find(string name)
        {
            for (Scope? scope = this; scope is not null; scope = scope._parent)
            {
                if (scope._names.TryGetValue(name, out LocalRecord? record))
                    return record;
            }

            return null;
        }

        /// <summary>
        /// Whether the name is a local or parameter at all, typed or not — the distinction
        /// <see cref="Lookup"/> deliberately erases, and the one the reference index needs: a
        /// local shadows a member whatever its type.
        /// </summary>
        public bool Contains(string name) => Find(name) is not null;

        /// <summary>
        /// The declared type of a name, or null when it is untyped, shadowed by an untyped
        /// declaration, or not in scope at all. All three mean "do not check", which is why they
        /// share an answer.
        /// </summary>
        public TypePath? Lookup(string name) => Find(name)?.DeclaredType;
    }
}
