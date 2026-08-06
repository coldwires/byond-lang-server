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
    private readonly string? _referenceName;
    private string _inside = "/";

    private Binder(ObjectTree tree, string? file, Action<Services.Reference>? sink, string? referenceName)
    {
        _tree = tree;
        _file = file;
        _sink = sink;
        _referenceName = referenceName;
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
    public static IReadOnlyList<Diagnostic> Bind(
        ObjectTree tree,
        FileSyntax root,
        string? file = null,
        Action<Services.Reference>? sink = null,
        string? referenceName = null)
    {
        Binder binder = new(tree, file, sink, referenceName);
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
                    // A type-level initialiser runs with the enclosing type as `src`.
                    _inside = enclosing.IsRoot ? "/" : enclosing.Text;
                    BindExpression(variable.Initializer, new Scope(enclosing), invoked: false);
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

        Scope scope = new(owner);

        foreach (ParameterSyntax parameter in proc.Parameters)
        {
            scope.Declare(
                parameter.Name,
                parameter.DeclaredType is { } declared
                    ? TypePath.FromSegments(declared.Segments)
                    : TypeInference.FromInputType(parameter.InputType));

            BindExpression(parameter.DefaultValue, scope, invoked: false);
        }

        if (proc.Body is null)
            return;

        BindStatement(proc.Body, scope);
    }

    /// <summary>
    /// A <c>proc/</c> name declared twice is dm.exe's duplicate-definition error — on one type, on
    /// an ancestor at any depth, or against a builtin (§8, probes dup1–dup9). Overrides carry no
    /// marker and never trip this; a var and a proc may legally share a name.
    /// </summary>
    /// <remarks>
    /// dm.exe reports a pair: <i>"duplicate definition"</i> on the later declaration and
    /// <i>"previous definition"</i> on the first. Binding is per file, so each site reports its own
    /// half and a same-file pair matches the compiler line for line. The one deliberate miss: when
    /// the first declaration lives in another file, its <i>"previous definition"</i> line is not
    /// reported — finding it from here would mean scanning every descendant type on every bind.
    /// A builtin has no line at all, and dm.exe accordingly reports a single error there.
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

                // The pair's other half, when the ancestor's declaration sits in this same file.
                DeclarationSite first = above.DeclaringSites[0];

                if (_file is not null
                    && string.Equals(first.File, _file, StringComparison.OrdinalIgnoreCase))
                {
                    _diagnostics.Add(Diagnostic.Error(
                        "DM0403", first.NameSpan, $"{proc.Name}: previous definition"));
                }

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

            case ExpressionStatementSyntax expression:
                BindExpression(expression.Expression, scope, invoked: false);
                break;

            // Declared as encountered, not gathered up front. A proc routinely reuses one name for
            // several loop variables of different types, so hoisting them into one flat scope let a
            // later `for(var/obj/disc_train/T ...)` decide how an earlier `T` was checked — which
            // invented errors on working code. A use above its declaration simply finds nothing and
            // is skipped, which is the safe direction.
            case LocalVarStatementSyntax local:
                BindExpression(local.Initializer, scope, invoked: false);
                Declare(local, scope);

                foreach (LocalVarStatementSyntax sibling in local.Siblings)
                {
                    BindExpression(sibling.Initializer, scope, invoked: false);
                    Declare(sibling, scope);
                }

                break;

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

                foreach (StatementSyntax initializer in loop.Initializers)
                    BindStatement(initializer, inner);

                BindExpression(loop.Condition, inner, invoked: false);
                BindExpression(loop.Sequence, inner, invoked: false);

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
                    Declare(caught, handler);

                BindStatement(guarded.CatchBody, handler);
                break;
            }

            // `set` names are a fixed vocabulary rather than members of anything, and a label is
            // not a name lookup at all.
            case SetStatementSyntax:
            case LabelStatementSyntax:
            case GotoStatementSyntax:
            case BreakStatementSyntax:
                break;
        }
    }

    private void BindExpression(ExpressionSyntax? expression, Scope scope, bool invoked, bool written = false)
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

                foreach (ArgumentSyntax argument in invocation.Arguments)
                    BindExpression(argument.Value, scope, invoked: false);

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
                BindExpression(assignment.Target, scope, invoked: false, written: true);
                BindExpression(assignment.Value, scope, invoked: false);
                break;

            case ConditionalExpressionSyntax conditional:
                BindExpression(conditional.Condition, scope, invoked: false);
                BindExpression(conditional.WhenTrue, scope, invoked: false);
                BindExpression(conditional.WhenFalse, scope, invoked: false);
                break;

            case NewExpressionSyntax created:
                BindExpression(created.Type, scope, invoked: false);

                foreach (ArgumentSyntax argument in created.Arguments)
                    BindExpression(argument.Value, scope, invoked: false);

                break;

            case AsExpressionSyntax clause:
                BindExpression(clause.Expression, scope, invoked: false);
                break;

            case InterpolatedStringExpressionSyntax interpolated:
                foreach (InterpolatedStringPartSyntax part in interpolated.Parts)
                    BindExpression(part.Expression, scope, invoked: false);

                break;

            case ParentCallExpressionSyntax parent:
                foreach (ArgumentSyntax argument in parent.Arguments)
                    BindExpression(argument.Value, scope, invoked: false);

                break;
        }
    }

    /// <summary>
    /// The one check this pass makes: a member reached through a receiver whose type is written
    /// down, and which that type does not have.
    /// </summary>
    private void BindMemberAccess(MemberAccessExpressionSyntax member, Scope scope, bool invoked, bool written = false)
    {
        BindExpression(member.Target, scope, invoked: false);

        // `:` widens the check to every subtype of the declared type, and on an untyped receiver it
        // asks only whether the name exists as a member of anything in the program. Both are real
        // checks and both are worth making — but a wrong subtype walk invents errors on working
        // code, so it waits until the `.` case is proven at zero invented.
        if (member.Kind != MemberAccessKind.Dot)
            return;

        // Only a receiver whose type is written down is checked. Anything else is either genuinely
        // unknowable — a call result or an index, where dm.exe silently degrades `.` to `:` and
        // stops checking — or an untyped variable, where it rejects every member including the
        // right one. The second is a real diagnostic and a valuable one, but it needs certainty
        // that we saw the declaration, so it is not in this pass.
        if (ReceiverType(member.Target, scope) is not { } receiver)
            return;

        if (_tree.Find(receiver) is not { } type)
            return;

        bool isProc = invoked && _tree.ResolveProc(type, member.Name) is not null;
        bool exists = isProc || _tree.ResolveVar(type, member.Name) is not null;

        if (exists)
        {
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

    // -- the reference sink ---------------------------------------------------

    /// <summary>Whether the sink is on and wants this name — the cheap gate before any walk.</summary>
    private bool SinkWants(string name)
        => _sink is not null
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
    /// Sink-only bare-name resolution: the enclosing chain first, then the root's globals — the
    /// same order definition uses. Reports no diagnostic; the undefined-bare-identifier check is
    /// separate M11 work with its own zero-invented gate.
    /// </summary>
    private void BindIdentifier(IdentifierExpressionSyntax identifier, Scope scope, bool invoked, bool written)
    {
        if (!SinkWants(identifier.Name)
            || identifier.Name is "src" or "usr" or "world" or "global" or "args")
        {
            return;
        }

        // Locals and parameters shadow members and are not index symbols.
        if (scope.Contains(identifier.Name))
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

    /// <summary>
    /// The receiver's <b>written</b> type, or null when nothing says what it is.
    /// </summary>
    private TypePath? ReceiverType(ExpressionSyntax? target, Scope scope) => target switch
    {
        // `src` is the type the proc is declared on.
        IdentifierExpressionSyntax { Name: "src" } => scope.EnclosingType,

        // A local or parameter, but only when it carries a declared type. A bare name that is not
        // in scope is deliberately NOT resolved as a type here: completion falls back to reading
        // `mob` as `/mob`, which is right for offering members and wrong for reporting errors,
        // since an untyped local of that name would then be checked against a type it never had.
        IdentifierExpressionSyntax identifier => scope.Lookup(identifier.Name),

        // A path written out as a value.
        PathExpressionSyntax { Path.Anchor: PathAnchor.Absolute } path
            => TypePath.FromSegments(path.Path.Segments),

        _ => null,
    };

    private static TypePath PathOf(PathSyntax path, TypePath enclosing)
        => path.Anchor == PathAnchor.Absolute
            ? TypePath.FromSegments(path.Segments)
            : enclosing.Append(path.Segments);

    private static void Declare(LocalVarStatementSyntax local, Scope scope)
        => scope.Declare(
            local.Name,
            local.DeclaredType is { Segments.Count: > 0 } declared
                ? TypePath.FromSegments(declared.Segments)
                : null);

    /// <summary>Names visible at one point in a proc, with the type each was declared as.</summary>
    private sealed class Scope
    {
        private readonly Dictionary<string, TypePath?> _names = new();
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

        public void Declare(string name, TypePath? declaredType) => _names[name] = declaredType;

        /// <summary>
        /// Whether the name is a local or parameter at all, typed or not — the distinction
        /// <see cref="Lookup"/> deliberately erases, and the one the reference index needs: a
        /// local shadows a member whatever its type.
        /// </summary>
        public bool Contains(string name)
        {
            for (Scope? scope = this; scope is not null; scope = scope._parent)
            {
                if (scope._names.ContainsKey(name))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The declared type of a name, or null when it is untyped, shadowed by an untyped
        /// declaration, or not in scope at all. All three mean "do not check", which is why they
        /// share an answer.
        /// </summary>
        public TypePath? Lookup(string name)
        {
            for (Scope? scope = this; scope is not null; scope = scope._parent)
            {
                if (scope._names.TryGetValue(name, out TypePath? declared))
                    return declared;
            }

            return null;
        }
    }
}
